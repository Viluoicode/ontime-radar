// Athena SQL for the S3 event lake (table `events`, one JSON line per processed event).
// Pure string building + input validation, so it is unit-testable without AWS.
// SECURITY: user input never goes into SQL as free text. Every value is either a validated
// integer, an enum name, a whitelisted carrier code, or matches a strict regex.
// (Sinh câu SQL cho Athena + kiểm tra tham số – chống SQL injection.)
using System.Globalization;
using System.Text.RegularExpressions;
using ShipmentSla.Core.Carriers;

namespace ShipmentSla.Core.Analytics;

/// <summary>Filters for GET /shipments (list view of the dashboard).</summary>
public sealed record ShipmentQuery(
    int Days = 30,
    int Limit = 50,
    ShipmentStatus? Status = null,
    string? Carrier = null,
    string? Region = null,
    bool Late = false,       // delivered after the promised date
    bool Overdue = false);   // still open, but the latest event is already past the promised date

public static partial class LakeQueries
{
    public const int MaxDays = 365;
    public const int MaxLimit = 200;

    // Demo (tools/demo.ps1) and README smoke-test shipments must not distort the numbers.
    private const string ExcludeTestData =
        "NOT starts_with(trackingcode, 'DEMO') AND NOT starts_with(trackingcode, 'GHNTEST')";

    private static readonly Dictionary<string, string> GroupColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["carrier"] = "carrier",
        ["region"] = "region"
    };

    [GeneratedRegex("^[A-Za-z]{2,10}$")]
    private static partial Regex RegionPattern();

    // ---------------- validation ----------------

    public static bool TryGetGroupColumn(string groupBy, out string column) =>
        GroupColumns.TryGetValue(groupBy, out column!);

    /// <summary>Only exact enum names ("Delivered", case-insensitive); numbers like "5" are refused.</summary>
    public static bool TryParseStatus(string value, out ShipmentStatus status)
    {
        var name = Enum.GetNames<ShipmentStatus>().FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase));
        status = name is null ? default : Enum.Parse<ShipmentStatus>(name);
        return name is not null;
    }

    public static string? ValidateDays(int days) =>
        days is < 1 or > MaxDays ? $"days must be 1..{MaxDays}" : null;

    public static string? ValidateCarrier(string? carrier) =>
        carrier is null || CarrierRegistry.Codes.Contains(carrier.ToLowerInvariant())
            ? null
            : $"carrier must be one of: {string.Join(", ", CarrierRegistry.Codes)}";

    /// <summary>Returns an error message, or null when the query is valid.</summary>
    public static string? Validate(ShipmentQuery q) =>
        ValidateDays(q.Days)
        ?? (q.Limit is < 1 or > MaxLimit ? $"limit must be 1..{MaxLimit}" : null)
        ?? ValidateCarrier(q.Carrier)
        ?? (q.Region is not null && !RegionPattern().IsMatch(q.Region) ? "region must be 2-10 letters" : null)
        ?? (q.Late && q.Overdue ? "late and overdue cannot be combined (late = delivered, overdue = still open)" : null);

    // ---------------- queries ----------------

    /// <summary>Late-delivery rate grouped by carrier or region. Columns: grp, delivered, late, late_pct.</summary>
    public static string SlaSummary(string column, int days, DateOnly today) => $"""
        SELECT {column} AS grp,
               count(*) AS delivered,
               count_if(islate) AS late,
               round(100.0 * count_if(islate) / count(*), 2) AS late_pct
        FROM events
        WHERE dt >= '{From(days, today)}' AND status = 'Delivered' AND outcome = 'Applied'
          AND {ExcludeTestData}
        GROUP BY {column}
        ORDER BY late_pct DESC
        """;

    /// <summary>
    /// One row per delivery day: delivered, late, late %, average days from first event to delivery.
    /// Columns: day, delivered, late, late_pct, avg_days.
    /// </summary>
    public static string SlaTrend(int days, string? carrier, DateOnly today) => $"""
        WITH ev AS (
            SELECT carrier, status, outcome, islate,
                   from_iso8601_timestamp(occurredat) AS at,
                   min(from_iso8601_timestamp(occurredat)) OVER (PARTITION BY shipmentid) AS first_at
            FROM events
            WHERE dt >= '{From(days, today)}' AND {ExcludeTestData}
        )
        SELECT CAST(date(at) AS varchar) AS day,
               count(*) AS delivered,
               count_if(islate) AS late,
               round(100.0 * count_if(islate) / count(*), 2) AS late_pct,
               round(avg(date_diff('hour', first_at, at)) / 24.0, 1) AS avg_days
        FROM ev
        WHERE status = 'Delivered' AND outcome = 'Applied'{CarrierFilter(carrier)}
        GROUP BY date(at)
        ORDER BY date(at)
        """;

    /// <summary>
    /// Current state of each shipment = its most recently processed lake row.
    /// Columns: carrier, trackingcode, orderid, region, status, islate, promised, last_event_at, updated_at.
    /// </summary>
    public static string Shipments(ShipmentQuery q, DateOnly today)
    {
        var where = new List<string> { "rn = 1" };
        if (q.Status is { } s) where.Add($"currentstatus = '{s}'");
        if (q.Carrier is { } c) where.Add($"carrier = '{c.ToLowerInvariant()}'");
        if (q.Region is { } r) where.Add($"region = '{r.ToUpperInvariant()}'");
        if (q.Late) where.Add("islate = true");
        if (q.Overdue)
            where.Add("currentstatus NOT IN ('Delivered', 'Returned', 'Cancelled') " +
                      "AND promised IS NOT NULL AND date(last_event_at) > CAST(promised AS date)");

        return $"""
            WITH ranked AS (
                SELECT carrier, trackingcode, orderid, region, currentstatus, islate, processedat,
                       max(estimateddelivery) OVER (PARTITION BY shipmentid) AS promised,
                       max(CASE WHEN outcome = 'Applied' THEN from_iso8601_timestamp(occurredat) END)
                           OVER (PARTITION BY shipmentid) AS last_event_at,
                       row_number() OVER (PARTITION BY shipmentid ORDER BY processedat DESC) AS rn
                FROM events
                WHERE dt >= '{From(q.Days, today)}' AND {ExcludeTestData}
            )
            SELECT carrier, trackingcode, orderid, region, currentstatus, islate, promised,
                   to_iso8601(last_event_at) AS last_event_at, processedat
            FROM ranked
            WHERE {string.Join(" AND ", where)}
            ORDER BY processedat DESC
            LIMIT {q.Limit}
            """;
    }

    private static string CarrierFilter(string? carrier) =>
        carrier is null ? "" : $" AND carrier = '{carrier.ToLowerInvariant()}'";

    // Partition pruning: only scan dt folders inside the window (cheaper + faster).
    private static string From(int days, DateOnly today) =>
        today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
