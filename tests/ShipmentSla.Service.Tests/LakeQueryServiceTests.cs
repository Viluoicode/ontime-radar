// LakeQueryService: an Athena query that nobody waits for any more (timeout / cancelled request) must be stopped,
// otherwise it keeps running and billing in Athena (review item S3, fixed 24/11/2026).
// (Truy vấn Athena hết thời gian chờ / bị huỷ phải được dừng – Athena giả bằng NSubstitute.)
using Amazon.Athena;
using Amazon.Athena.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ShipmentSla.Api;
using Xunit;

namespace ShipmentSla.Service.Tests;

public class LakeQueryServiceTests
{
    private const string QueryId = "q-123";
    private readonly IAmazonAthena _athena = Substitute.For<IAmazonAthena>();

    public LakeQueryServiceTests()
    {
        _athena.StartQueryExecutionAsync(Arg.Any<StartQueryExecutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StartQueryExecutionResponse { QueryExecutionId = QueryId });
        _athena.StopQueryExecutionAsync(Arg.Any<StopQueryExecutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new StopQueryExecutionResponse());
    }

    private LakeQueryService Service(TimeSpan timeout) =>
        new(_athena, "db", "wg", new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 }),
            NullLogger<LakeQueryService>.Instance, timeout);

    private void QueryState(QueryExecutionState state) =>
        _athena.GetQueryExecutionAsync(Arg.Any<GetQueryExecutionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetQueryExecutionResponse
            {
                QueryExecution = new QueryExecution { Status = new QueryExecutionStatus { State = state } }
            });

    private Task StopWasCalled() =>
        _athena.Received(1).StopQueryExecutionAsync(
            Arg.Is<StopQueryExecutionRequest>(r => r.QueryExecutionId == QueryId), CancellationToken.None);

    [Fact]
    public async Task Timed_out_query_is_stopped_and_the_timeout_still_reaches_the_caller()
    {
        QueryState(QueryExecutionState.RUNNING);

        await Assert.ThrowsAsync<TimeoutException>(() => Service(TimeSpan.Zero).SummaryAsync("carrier", 30, default));

        await StopWasCalled();
    }

    [Fact]
    public async Task Cancelled_request_stops_the_query_with_a_token_that_is_not_cancelled()
    {
        QueryState(QueryExecutionState.RUNNING);
        using var cts = new CancellationTokenSource();
        cts.Cancel();   // e.g. the browser went away / API Gateway cut the request

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(TimeSpan.FromMinutes(1)).SummaryAsync("carrier", 30, cts.Token));

        await StopWasCalled();   // CancellationToken.None: the request's own token is already cancelled
    }

    [Fact]
    public async Task Failing_stop_is_only_logged_the_caller_still_gets_the_timeout()
    {
        QueryState(QueryExecutionState.RUNNING);
        _athena.StopQueryExecutionAsync(Arg.Any<StopQueryExecutionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonAthenaException("no permission"));

        await Assert.ThrowsAsync<TimeoutException>(() => Service(TimeSpan.Zero).SummaryAsync("carrier", 30, default));
    }

    [Fact]
    public async Task Finished_query_is_not_stopped()
    {
        QueryState(QueryExecutionState.SUCCEEDED);
        _athena.GetQueryResultsAsync(Arg.Any<GetQueryResultsRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetQueryResultsResponse
            {
                ResultSet = new ResultSet
                {
                    Rows =
                    [
                        new Row { Data = [new() { VarCharValue = "group" }, new() { VarCharValue = "delivered" }, new() { VarCharValue = "late" }, new() { VarCharValue = "pct" }] },
                        new Row { Data = [new() { VarCharValue = "ghn" }, new() { VarCharValue = "10" }, new() { VarCharValue = "1" }, new() { VarCharValue = "10.0" }] }
                    ]
                }
            });

        var rows = await Service(TimeSpan.Zero).SummaryAsync("carrier", 30, default);

        Assert.Equal(new SlaRow("ghn", 10, 1, 10.0), Assert.Single(rows));
        await _athena.DidNotReceiveWithAnyArgs().StopQueryExecutionAsync(default!, default);
    }

    [Fact]
    public async Task Failed_query_is_not_stopped_it_is_already_over()
    {
        QueryState(QueryExecutionState.FAILED);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(TimeSpan.Zero).SummaryAsync("carrier", 30, default));

        await _athena.DidNotReceiveWithAnyArgs().StopQueryExecutionAsync(default!, default);
    }
}
