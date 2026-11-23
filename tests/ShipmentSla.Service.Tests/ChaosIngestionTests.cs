// Chaos ingestion: the generated webhooks (duplicated, shuffled, held back, malformed) go through the real
// adapters and the real ShipmentStore on the in-memory DynamoDB. The final state of every shipment must equal
// the ground truth – whatever the arrival order – and every event must be stored exactly once.
// One test runs the whole path in memory: real API (HTTP) → SQS messages → real Processor Lambda.
// (Phép thử "hãng gửi lộn xộn" trong bộ nhớ: đối chiếu trạng thái cuối với đáp án – dữ liệu giả lập.)
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS.Model;
using NSubstitute;
using ShipmentSla.Core;
using ShipmentSla.Core.Carriers;
using ShipmentSla.Core.Simulation;
using ShipmentSla.Infrastructure;
using ShipmentSla.Processor;
using Xunit;

namespace ShipmentSla.Service.Tests;

public class ChaosIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    /// <summary>What the webhook endpoint does with a body: the carrier's adapter, or a refusal (400).</summary>
    private static NormalizedEvent? Ingest(ChaosWebhook w)
    {
        Assert.True(CarrierRegistry.TryGet(w.Carrier, out var adapter));
        try
        {
            using var doc = JsonDocument.Parse(w.Body);
            return adapter.Parse(doc.RootElement, Now);
        }
        catch (Exception ex) when (ex is JsonException or CarrierPayloadException)   // same filter as Program.cs
        {
            return null;
        }
    }

    /// <summary>Webhooks in arrival order → adapter → (SQS JSON round trip) → ShipmentStore. Returns the refusals.</summary>
    private static async Task<(ShipmentStore Store, int Refused)> RunInMemory(ChaosScenario scenario)
    {
        var store = new ShipmentStore(new FakeDynamoTable().Client, "test-table");
        var refused = 0;
        foreach (var w in scenario.Webhooks)
        {
            if (Ingest(w) is not { } evt)
            {
                refused++;
                continue;
            }
            var message = JsonSerializer.Serialize(evt, ShipmentJson.Options);
            await store.ProcessAsync(JsonSerializer.Deserialize<NormalizedEvent>(message, ShipmentJson.Options)!, Now);
        }
        return (store, refused);
    }

    /// <summary>Every difference between the store and the ground truth (empty list = reconciled).</summary>
    private static async Task<List<string>> Mismatches(ShipmentStore store, ChaosScenario scenario)
    {
        var problems = new List<string>();
        foreach (var s in scenario.Shipments)
        {
            var timeline = await store.GetTimelineAsync(s.ShipmentId);
            if (timeline is null)
            {
                problems.Add($"{s.ShipmentId}: not in the store");
                continue;
            }
            var actual = new ChaosExpectation(timeline.State.Status, timeline.State.DeliveredAt, timeline.State.IsLate);
            if (actual != s.Expected)
                problems.Add($"{s.ShipmentId} ({s.Kind}): expected {s.Expected}, got {actual}");
            if (timeline.Events.Count != s.Order.Events.Count)
                problems.Add($"{s.ShipmentId}: {timeline.Events.Count} history items, expected {s.Order.Events.Count}");
        }
        return problems;
    }

    [Fact] // C1
    public async Task Default_chaos_scenario_ends_exactly_in_the_ground_truth()
    {
        // 200 shipments, ~5 copies per event, shuffled, 10 % held back, 1 % malformed.
        var scenario = ChaosScenario.Generate(new ChaosOptions(), Now);

        var (store, refused) = await RunInMemory(scenario);

        Assert.Equal(scenario.MalformedWebhooks, refused);
        Assert.Empty(await Mismatches(store, scenario));
    }

    [Fact] // C2 – property: the result does not depend on the arrival order
    public async Task Any_arrival_order_of_the_same_events_gives_the_same_final_state()
    {
        var failures = new List<string>();
        for (var arrivalSeed = 1; arrivalSeed <= 100; arrivalSeed++)
        {
            var scenario = ChaosScenario.Generate(new ChaosOptions { Shipments = 10, Seed = 42, ArrivalSeed = arrivalSeed }, Now);
            var (store, _) = await RunInMemory(scenario);
            failures.AddRange((await Mismatches(store, scenario)).Select(m => $"arrival seed {arrivalSeed}: {m}"));
        }
        Assert.Empty(failures);
    }

    [Fact] // C3 – whole path: HTTP status codes, SQS message ids, Processor batches, event lake
    public async Task Chaos_through_the_real_api_and_processor_is_reconciled()
    {
        var scenario = ChaosScenario.Generate(new ChaosOptions { Shipments = 40, Seed = 3, MalformedPercent = 5 }, Now);

        // 1) Real API in memory; SQS captures what would be enqueued.
        using var api = new ApiFactory();
        var enqueued = new List<SendMessageRequest>();
        api.Sqs.SendMessageAsync(Arg.Do<SendMessageRequest>(enqueued.Add), Arg.Any<CancellationToken>())
            .Returns(new SendMessageResponse());
        using var http = api.CreateClient();

        var wrongStatus = new List<string>();
        foreach (var w in scenario.Webhooks)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/carriers/{w.Carrier}/webhook")
            {
                Content = new StringContent(w.Body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Webhook-Secret", ApiFactory.Secret);
            using var response = await http.SendAsync(request);

            var expected = w.Kind == WebhookKind.Valid ? HttpStatusCode.Accepted : HttpStatusCode.BadRequest;
            if (response.StatusCode != expected)
                wrongStatus.Add($"{w.Carrier} {w.Defect ?? "valid"} → {(int)response.StatusCode}: {w.Body}");
        }
        Assert.Empty(wrongStatus);
        Assert.Equal(scenario.Webhooks.Count - scenario.MalformedWebhooks, enqueued.Count);
        // One dedup id per distinct event (SQS FIFO drops repeats within 5 minutes; DynamoDB catches the rest).
        Assert.Equal(scenario.UniqueEvents, enqueued.Select(m => m.MessageDeduplicationId).Distinct().Count());
        Assert.All(enqueued, m => Assert.StartsWith(ChaosScenario.TrackingPrefix, m.MessageGroupId.Split('#')[1]));

        // 2) Real Processor Lambda, batches of 10 in queue order (no SQS dedup here: every copy reaches DynamoDB).
        var table = new FakeDynamoTable();
        var store = new ShipmentStore(table.Client, "test-table");
        var s3 = Substitute.For<IAmazonS3>();
        var lakeWrites = new List<PutObjectRequest>();
        s3.PutObjectAsync(Arg.Do<PutObjectRequest>(lakeWrites.Add), Arg.Any<CancellationToken>()).Returns(new PutObjectResponse());
        var cloudWatch = Substitute.For<IAmazonCloudWatch>();
        cloudWatch.PutMetricDataAsync(Arg.Any<PutMetricDataRequest>(), Arg.Any<CancellationToken>()).Returns(new PutMetricDataResponse());
        var processor = new Function(store, s3, cloudWatch, "lake-bucket");

        foreach (var batch in enqueued.Select((m, i) => new SQSEvent.SQSMessage { MessageId = $"m{i}", Body = m.MessageBody }).Chunk(10))
        {
            var result = await processor.Handler(new SQSEvent { Records = [.. batch] }, new TestLambdaContext());
            Assert.Empty(result.BatchItemFailures);
        }

        // 3) Reconcile: state = ground truth, history once per event, lake once per event (duplicates are not re-written).
        Assert.Empty(await Mismatches(store, scenario));
        var lakeLines = lakeWrites.Sum(w => w.ContentBody.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(scenario.UniqueEvents, lakeLines);
    }
}
