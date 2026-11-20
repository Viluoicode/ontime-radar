// Processor Lambda: SQS FIFO batch → ShipmentStore (fake DynamoDB) → S3 event lake + CloudWatch metrics.
// Key rule: after the first failure in a batch, that message AND every later one is reported failed and
// NOT processed – otherwise a later event of the same shipment could overtake the one being retried.
// (Kiểm thử lỗi giữa batch FIFO, event lake, metric.)
using System.Text.Json;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.DynamoDBv2;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ShipmentSla.Core;
using ShipmentSla.Infrastructure;
using ShipmentSla.Processor;
using Xunit;
using static ShipmentSla.Service.Tests.TestData;

namespace ShipmentSla.Service.Tests;

public class ProcessorTests
{
    private readonly FakeDynamoTable _table = new();
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly IAmazonCloudWatch _cw = Substitute.For<IAmazonCloudWatch>();
    private readonly List<PutObjectRequest> _lakeWrites = [];
    private readonly List<PutMetricDataRequest> _metrics = [];
    private readonly Function _function;

    public ProcessorTests()
    {
        _s3.PutObjectAsync(Arg.Do<PutObjectRequest>(_lakeWrites.Add), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse());
        _cw.PutMetricDataAsync(Arg.Do<PutMetricDataRequest>(_metrics.Add), Arg.Any<CancellationToken>())
            .Returns(new PutMetricDataResponse());
        _function = new Function(new ShipmentStore(_table.Client, "test-table"), _s3, _cw, "lake-bucket");
    }

    private static SQSEvent.SQSMessage Msg(string id, NormalizedEvent e) =>
        new() { MessageId = id, Body = JsonSerializer.Serialize(e, ShipmentJson.Options) };

    private Task<Amazon.Lambda.SQSEvents.SQSBatchResponse> Run(params SQSEvent.SQSMessage[] messages) =>
        _function.Handler(new SQSEvent { Records = [.. messages] }, new TestLambdaContext());

    private List<string> AttemptedEventIds() =>
        _table.Transactions.Select(t => t.TransactItems[0].Put.Item["eventId"].S).ToList();

    private List<JsonElement> LakeLines() =>
        _lakeWrites.SelectMany(w => w.ContentBody.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                   .Select(l => JsonDocument.Parse(l).RootElement).ToList();

    private void FailTransactionFor(string eventId) =>
        _table.FailTransaction = r => r.TransactItems[0].Put.Item["eventId"].S == eventId
            ? new AmazonDynamoDBException("DynamoDB unavailable")
            : null;

    [Fact] // P1
    public async Task Failure_mid_batch_fails_it_and_every_later_message_without_processing_them()
    {
        var a1 = Event("A", ShipmentStatus.PickedUp, T0);
        var a2 = Event("A", ShipmentStatus.InTransit, T0.AddHours(1));
        var b1 = Event("B", ShipmentStatus.PickedUp, T0);
        var a3 = Event("A", ShipmentStatus.OutForDelivery, T0.AddHours(2));
        FailTransactionFor(a2.EventId);

        var response = await Run(Msg("m1", a1), Msg("m2", a2), Msg("m3", b1), Msg("m4", a3));

        Assert.Equal(["m2", "m3", "m4"], response.BatchItemFailures.Select(f => f.ItemIdentifier));
        Assert.Equal([a1.EventId, a2.EventId], AttemptedEventIds());       // B1 and A3 never touched the store
        Assert.Equal(ShipmentStatus.PickedUp, (await new ShipmentStore(_table.Client, "t").GetStateAsync("ghn#A"))!.Status);
        Assert.Equal([a1.EventId], LakeLines().Select(l => l.GetProperty("eventId").GetString()));
    }

    [Fact] // P2
    public async Task Poison_message_first_fails_the_whole_batch()
    {
        var response = await Run(
            new SQSEvent.SQSMessage { MessageId = "bad", Body = "{not json" },
            Msg("m2", Event("A", ShipmentStatus.PickedUp, T0)));

        Assert.Equal(["bad", "m2"], response.BatchItemFailures.Select(f => f.ItemIdentifier));
        Assert.Empty(_table.Transactions);
        Assert.Empty(_lakeWrites);
    }

    [Fact] // P3
    public async Task Clean_batch_has_no_failures_and_one_lake_file_partitioned_by_date()
    {
        var response = await Run(
            Msg("m1", Event("A", ShipmentStatus.PickedUp, T0)),
            Msg("m2", Event("B", ShipmentStatus.PickedUp, T0)),
            Msg("m3", Event("A", ShipmentStatus.InTransit, T0.AddHours(1))));

        Assert.Empty(response.BatchItemFailures);
        var write = Assert.Single(_lakeWrites);
        Assert.Equal("lake-bucket", write.BucketName);
        Assert.Matches(@"^events/dt=\d{4}-\d{2}-\d{2}/[0-9a-f]{32}\.jsonl$", write.Key);
        Assert.Equal(3, LakeLines().Count);
        Assert.All(LakeLines(), l => Assert.Equal("Applied", l.GetProperty("outcome").GetString()));
    }

    [Fact] // P4
    public async Task Duplicate_is_success_but_not_written_to_the_lake_again()
    {
        var e = Event("A", ShipmentStatus.PickedUp, T0);

        var response = await Run(Msg("m1", e), Msg("m2", e));   // carrier retry: same payload, new SQS message

        Assert.Empty(response.BatchItemFailures);
        Assert.Single(LakeLines());
    }

    [Fact] // P5
    public async Task Lake_and_metrics_contain_only_messages_processed_before_the_failure()
    {
        var late = Event("A", ShipmentStatus.Delivered, T0.AddDays(7), promise: Promise);
        var ok = Event("B", ShipmentStatus.PickedUp, T0);
        var bad = Event("C", ShipmentStatus.PickedUp, T0);
        FailTransactionFor(bad.EventId);

        await Run(Msg("m1", late), Msg("m2", ok), Msg("m3", bad));

        Assert.Equal([late.EventId, ok.EventId], LakeLines().Select(l => l.GetProperty("eventId").GetString()));
        var sent = Assert.Single(_metrics).MetricData;
        Assert.Equal(1, sent.Single(d => d.MetricName == "LateDeliveries" && d.Dimensions.Count == 1).Value);
    }

    [Fact] // P6
    public async Task Lake_or_metric_outage_does_not_fail_the_batch()
    {
        _s3.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AmazonS3Exception("S3 down"));
        _cw.PutMetricDataAsync(Arg.Any<PutMetricDataRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new AmazonCloudWatchException("CW down"));

        var response = await Run(Msg("m1", Event("A", ShipmentStatus.Delivered, T0.AddDays(7), promise: Promise)));

        Assert.Empty(response.BatchItemFailures);                          // DynamoDB is the source of truth
        Assert.NotNull(await new ShipmentStore(_table.Client, "t").GetStateAsync("ghn#A"));
    }

    [Fact] // P7
    public async Task Metrics_count_new_deliveries_and_late_ones_per_service_and_carrier()
    {
        await Run(
            Msg("m1", Event("A", ShipmentStatus.Delivered, T0.AddDays(7), carrier: "ghn", promise: Promise)), // late
            Msg("m2", Event("B", ShipmentStatus.Delivered, T0.AddDays(1), carrier: "jt", promise: Promise)),  // on time
            Msg("m3", Event("C", ShipmentStatus.Delivered, T0.AddDays(1), carrier: "jt", promise: null)));    // no promise

        var data = Assert.Single(_metrics).MetricData;
        double Value(string metric, string? carrier) => data.Single(d => d.MetricName == metric
            && d.Dimensions.Any(x => x is { Name: "Service", Value: "shipment-sla" })
            && (carrier is null ? d.Dimensions.Count == 1 : d.Dimensions.Any(x => x.Name == "Carrier" && x.Value == carrier))).Value;

        Assert.Equal(2, Value("Deliveries", null));       // C has no promise → cannot be on time or late → not counted
        Assert.Equal(1, Value("Deliveries", "ghn"));
        Assert.Equal(1, Value("Deliveries", "jt"));
        Assert.Equal(1, Value("LateDeliveries", null));
        Assert.Equal(1, Value("LateDeliveries", "ghn"));
        Assert.DoesNotContain(data, d => d.MetricName == "LateDeliveries" && d.Dimensions.Any(x => x.Value == "jt"));
    }

    [Fact] // P8
    public async Task Batch_of_only_duplicates_calls_neither_S3_nor_CloudWatch()
    {
        var e = Event("A", ShipmentStatus.Delivered, T0.AddDays(7), promise: Promise);
        await Run(Msg("m1", e));
        _lakeWrites.Clear(); _metrics.Clear();

        var response = await Run(Msg("m2", e), Msg("m3", e));

        Assert.Empty(response.BatchItemFailures);
        Assert.Empty(_lakeWrites);
        Assert.Empty(_metrics);
    }

    [Fact] // Known limitation (docs/product.md): DynamoDB and S3 are not written atomically.
    public async Task Known_gap_event_committed_before_a_crash_never_reaches_the_lake()
    {
        var e = Event("A", ShipmentStatus.PickedUp, T0);
        // 1st delivery: the transaction commits, then the Lambda dies before the S3 write (simulated by
        // calling the store directly). SQS redelivers the message → it is now a Duplicate → skipped.
        await new ShipmentStore(_table.Client, "test-table").ProcessAsync(e, T0);

        var response = await Run(Msg("m1-redelivered", e));

        Assert.Empty(response.BatchItemFailures);
        Assert.Empty(LakeLines());   // in DynamoDB, missing from S3 / Athena. If this starts failing, the gap was fixed.
    }
}
