// Runs the SLA aggregation on the S3 event lake through Athena.
// (Truy vấn tổng hợp SLA trên event lake bằng Athena.)
using System.Globalization;
using Amazon.Athena;
using Amazon.Athena.Model;

namespace ShipmentSla.Api;

public sealed record SlaRow(string Group, long Delivered, long Late, double LatePercent);

public sealed class SlaQueryService(IAmazonAthena athena, string database, string workGroup)
{
    // Whitelist: user input never reaches the SQL text except as a validated integer.
    private static readonly Dictionary<string, string> GroupColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["carrier"] = "carrier",
        ["region"] = "region"
    };

    public static bool TryGetColumn(string groupBy, out string column) => GroupColumns.TryGetValue(groupBy, out column!);

    public async Task<List<SlaRow>> SummaryAsync(string column, int days, CancellationToken ct)
    {
        var from = DateTime.UtcNow.Date.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sql = $"""
            SELECT {column} AS grp,
                   count(*) AS delivered,
                   count_if(islate) AS late,
                   round(100.0 * count_if(islate) / count(*), 2) AS late_pct
            FROM events
            WHERE dt >= '{from}' AND status = 'Delivered' AND outcome = 'Applied'
              AND NOT starts_with(trackingcode, 'DEMO')   -- tools/demo.ps1 shipments
              AND NOT starts_with(trackingcode, 'GHNTEST') -- README smoke-test shipment
            GROUP BY {column}
            ORDER BY late_pct DESC
            """;

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
            await Task.Delay(500, ct);
        }

        var result = await athena.GetQueryResultsAsync(new GetQueryResultsRequest { QueryExecutionId = id }, ct);
        return result.ResultSet.Rows.Skip(1)   // first row = column headers
            .Select(r => r.Data.Select(d => d.VarCharValue).ToArray())
            .Select(c => new SlaRow(
                string.IsNullOrEmpty(c[0]) ? "(unknown)" : c[0],
                long.Parse(c[1], CultureInfo.InvariantCulture),
                long.Parse(c[2], CultureInfo.InvariantCulture),
                double.Parse(c[3], CultureInfo.InvariantCulture)))
            .ToList();
    }
}
