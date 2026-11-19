// Runs the event-lake queries (SQL built in ShipmentSla.Core.Analytics.LakeQueries) on Athena
// and maps the rows to JSON-friendly records for the API / dashboard.
// (Chạy truy vấn Athena trên event lake và chuyển kết quả thành dữ liệu cho API.)
using System.Globalization;
using Amazon.Athena;
using Amazon.Athena.Model;
using ShipmentSla.Core.Analytics;

namespace ShipmentSla.Api;

public sealed record SlaRow(string Group, long Delivered, long Late, double LatePercent);

public sealed record TrendRow(string Day, long Delivered, long Late, double LatePercent, double? AvgDeliveryDays);

public sealed record ShipmentRow(
    string Carrier, string TrackingCode, string? OrderId, string? Region, string? Status,
    bool? IsLate, string? EstimatedDelivery, string? LastEventAt, string? UpdatedAt, long FailedAttempts);

public sealed class LakeQueryService(IAmazonAthena athena, string database, string workGroup)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    public async Task<List<SlaRow>> SummaryAsync(string column, int days, CancellationToken ct) =>
        (await RunAsync(LakeQueries.SlaSummary(column, days, Today), ct))
        .Select(c => new SlaRow(Text(c[0]) ?? "(unknown)", Long(c[1]), Long(c[2]), Double(c[3]) ?? 0))
        .ToList();

    public async Task<List<TrendRow>> TrendAsync(int days, string? carrier, CancellationToken ct) =>
        (await RunAsync(LakeQueries.SlaTrend(days, carrier, Today), ct))
        .Select(c => new TrendRow(c[0]!, Long(c[1]), Long(c[2]), Double(c[3]) ?? 0, Double(c[4])))
        .ToList();

    public async Task<List<ShipmentRow>> ShipmentsAsync(ShipmentQuery q, CancellationToken ct) =>
        (await RunAsync(LakeQueries.Shipments(q, Today), ct))
        .Select(c => new ShipmentRow(c[0]!, c[1]!, Text(c[2]), Text(c[3]), Text(c[4]),
            Bool(c[5]), Text(c[6]), Text(c[7]), Text(c[8]), Long(c[9])))
        .ToList();

    /// <summary>Start the query, wait for it, return all data rows (header row removed).</summary>
    private async Task<List<string?[]>> RunAsync(string sql, CancellationToken ct)
    {
        var start = await athena.StartQueryExecutionAsync(new StartQueryExecutionRequest
        {
            QueryString = sql,
            WorkGroup = workGroup,
            QueryExecutionContext = new QueryExecutionContext { Database = database }
        }, ct);
        var id = start.QueryExecutionId;

        // Poll (API Gateway HTTP API limit is 30 s, so we give up after ~25 s).
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (true)
        {
            var q = await athena.GetQueryExecutionAsync(new GetQueryExecutionRequest { QueryExecutionId = id }, ct);
            var state = q.QueryExecution.Status.State;
            if (state == QueryExecutionState.SUCCEEDED) break;
            if (state == QueryExecutionState.FAILED || state == QueryExecutionState.CANCELLED)
                throw new InvalidOperationException($"Athena query {state}: {q.QueryExecution.Status.StateChangeReason}");
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Athena query timed out");
            await Task.Delay(300, ct);
        }

        // Results come in pages of up to 1000 rows; the first row of the first page is the header.
        var rows = new List<string?[]>();
        string? next = null;
        do
        {
            var page = await athena.GetQueryResultsAsync(new GetQueryResultsRequest { QueryExecutionId = id, NextToken = next }, ct);
            rows.AddRange(page.ResultSet.Rows.Select(r => r.Data.Select(d => d.VarCharValue).ToArray()));
            next = page.NextToken;
        } while (next is not null);
        return rows.Skip(1).ToList();
    }

    private static string? Text(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static long Long(string? s) => string.IsNullOrEmpty(s) ? 0 : long.Parse(s, Inv);
    private static double? Double(string? s) => string.IsNullOrEmpty(s) ? null : double.Parse(s, Inv);
    private static bool? Bool(string? s) => string.IsNullOrEmpty(s) ? null : bool.Parse(s);
}
