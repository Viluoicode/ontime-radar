// Runs the event-lake queries (SQL built in ShipmentSla.Core.Analytics.LakeQueries) on Athena
// and maps the rows to JSON-friendly records for the API / dashboard.
// (Chạy truy vấn Athena trên event lake và chuyển kết quả thành dữ liệu cho API.)
using System.Globalization;
using Amazon.Athena;
using Amazon.Athena.Model;
using Microsoft.Extensions.Caching.Memory;
using ShipmentSla.Core.Analytics;

namespace ShipmentSla.Api;

public sealed record SlaRow(string Group, long Delivered, long Late, double LatePercent);

public sealed record TrendRow(string Day, long Delivered, long Late, double LatePercent, double? AvgDeliveryDays);

public sealed record ShipmentRow(
    string Carrier, string TrackingCode, string? OrderId, string? Region, string? Status,
    bool? IsLate, string? EstimatedDelivery, string? LastEventAt, string? UpdatedAt, long FailedAttempts);

// Two cache layers, because the lake only changes every 15 minutes (Replayer schedule):
//  1. In-memory (per warm Lambda instance), 5 min: repeat dashboard loads return in ~0 ms, no Athena call.
//  2. Athena query result reuse, 15 min: on a cold instance the identical SQL is answered from the previous
//     result without scanning S3 again (cheaper, usually faster).
// The SQL contains the date window, so a new day automatically means a new cache key.
public sealed class LakeQueryService(
    IAmazonAthena athena, string database, string workGroup, IMemoryCache cache, ILogger<LakeQueryService> log,
    TimeSpan? queryTimeout = null)
{
    public static readonly TimeSpan MemoryTtl = TimeSpan.FromMinutes(5);
    public const int AthenaReuseMinutes = 15;

    /// <summary>API Gateway HTTP API cuts requests at 30 s, so we give up a bit earlier.</summary>
    public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(25);
    private readonly TimeSpan _queryTimeout = queryTimeout ?? DefaultQueryTimeout;

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

    /// <summary>Cached rows for this SQL, or run it on Athena (and cache the rows).</summary>
    private async Task<List<string?[]>> RunAsync(string sql, CancellationToken ct)
    {
        if (cache.TryGetValue(sql, out List<string?[]>? hit) && hit is not null)
        {
            log.LogInformation("Lake query: memory cache hit ({Rows} rows)", hit.Count);
            return hit;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (rows, reused) = await RunOnAthenaAsync(sql, ct);
        log.LogInformation("Lake query: Athena {Source} in {Ms} ms ({Rows} rows)",
            reused ? "reused result" : "scan", sw.ElapsedMilliseconds, rows.Count);

        // Only successful results are cached (failures/timeouts throw before this line).
        cache.Set(sql, rows, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = MemoryTtl, Size = 1 });
        return rows;
    }

    /// <summary>Start the query, wait for it, return all data rows (header row removed).</summary>
    private async Task<(List<string?[]> Rows, bool Reused)> RunOnAthenaAsync(string sql, CancellationToken ct)
    {
        var start = await athena.StartQueryExecutionAsync(new StartQueryExecutionRequest
        {
            QueryString = sql,
            WorkGroup = workGroup,
            QueryExecutionContext = new QueryExecutionContext { Database = database },
            ResultReuseConfiguration = new ResultReuseConfiguration
            {
                ResultReuseByAgeConfiguration = new ResultReuseByAgeConfiguration
                {
                    Enabled = true,
                    MaxAgeInMinutes = AthenaReuseMinutes
                }
            }
        }, ct);
        var id = start.QueryExecutionId;

        // Poll until done, or give up after the timeout (API Gateway would cut the request anyway).
        var deadline = DateTime.UtcNow + _queryTimeout;
        bool reused;
        try
        {
            while (true)
            {
                var q = await athena.GetQueryExecutionAsync(new GetQueryExecutionRequest { QueryExecutionId = id }, ct);
                var state = q.QueryExecution.Status.State;
                if (state == QueryExecutionState.SUCCEEDED)
                {
                    reused = q.QueryExecution.Statistics?.ResultReuseInformation?.ReusedPreviousResult == true;
                    break;
                }
                if (state == QueryExecutionState.FAILED || state == QueryExecutionState.CANCELLED)
                    throw new InvalidOperationException($"Athena query {state}: {q.QueryExecution.Status.StateChangeReason}");
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Athena query timed out");
                await Task.Delay(300, ct);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Nobody will read the result: stop the query so it does not keep running (and billing) in Athena.
            await TryStopAsync(id);
            throw;
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
        return (rows.Skip(1).ToList(), reused);
    }

    /// <summary>Best effort: a failed stop is only logged – the caller still gets the original timeout / cancellation.</summary>
    private async Task TryStopAsync(string id)
    {
        try
        {
            // Not the request's token: it may be the very thing that was cancelled.
            await athena.StopQueryExecutionAsync(new StopQueryExecutionRequest { QueryExecutionId = id }, CancellationToken.None);
            log.LogWarning("Lake query {Id} stopped after timeout / cancellation", id);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not stop lake query {Id}", id);
        }
    }

    private static string? Text(string? s) => string.IsNullOrEmpty(s) ? null : s;
    private static long Long(string? s) => string.IsNullOrEmpty(s) ? 0 : long.Parse(s, Inv);
    private static double? Double(string? s) => string.IsNullOrEmpty(s) ? null : double.Parse(s, Inv);
    private static bool? Bool(string? s) => string.IsNullOrEmpty(s) ? null : bool.Parse(s);
}
