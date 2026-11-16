// Processor Lambda – triggered by the SQS FIFO queue (batch of up to 10 messages).
// For each event: decide (state machine) → DynamoDB transaction → collect for the S3 event lake.
// After the batch: write one JSONL file to S3 and publish the "late delivery" metric.
// (Xử lý sự kiện theo thứ tự từng vận đơn, ghi trạng thái + lịch sử, cảnh báo đơn trễ.)
using System.Text.Json;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using ShipmentSla.Core;
using ShipmentSla.Infrastructure;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ShipmentSla.Processor;

public sealed class Function
{
    private const string MetricNamespace = "ShipmentSla";
    private const string ServiceName = "shipment-sla";

    // Created once per Lambda container and reused across invocations (cold-start optimisation).
    private readonly ShipmentStore _store;
    private readonly IAmazonS3 _s3;
    private readonly IAmazonCloudWatch _cloudWatch;
    private readonly string _lakeBucket;

    public Function() : this(
        new ShipmentStore(new AmazonDynamoDBClient(), Env("TABLE_NAME")),
        new AmazonS3Client(), new AmazonCloudWatchClient(), Env("LAKE_BUCKET")) { }

    // Constructor for tests / dependency injection.
    public Function(ShipmentStore store, IAmazonS3 s3, IAmazonCloudWatch cloudWatch, string lakeBucket)
    {
        _store = store; _s3 = s3; _cloudWatch = cloudWatch; _lakeBucket = lakeBucket;
    }

    public async Task<SQSBatchResponse> Handler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();
        var lakeLines = new List<string>();
        var lateByCarrier = new Dictionary<string, int>();
        var failedOnce = false;

        foreach (var message in sqsEvent.Records)
        {
            // FIFO rule: after one failure, report every later message as failed too,
            // otherwise a later event could be applied before the earlier one is retried.
            if (failedOnce)
            {
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId });
                continue;
            }

            try
            {
                var evt = JsonSerializer.Deserialize<NormalizedEvent>(message.Body, ShipmentJson.Options)
                          ?? throw new InvalidOperationException("empty message body");
                var now = DateTimeOffset.UtcNow;
                var result = await _store.ProcessAsync(evt, now);

                context.Logger.LogInformation(
                    $"{evt.ShipmentId} {evt.Status} → {result.Decision.Outcome} ({result.Decision.Reason})");

                if (result.Decision.Outcome != EventOutcome.Duplicate)
                    lakeLines.Add(LakeLine(evt, result, now));

                if (result.NewlyLate)
                    lateByCarrier[evt.Carrier] = lateByCarrier.GetValueOrDefault(evt.Carrier) + 1;
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Message {message.MessageId} failed: {ex}");
                failedOnce = true;
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId });
            }
        }

        // Best effort: DynamoDB is the source of truth; analytics/metrics must not block processing.
        await TryWriteLakeAsync(lakeLines, context);
        await TryPublishLateMetricAsync(lateByCarrier, context);

        // Only failed messages go back to the queue (after 5 tries → DLQ).
        return new SQSBatchResponse { BatchItemFailures = failures };
    }

    /// <summary>One flat JSON line per event – easy to query with Athena later.</summary>
    private static string LakeLine(NormalizedEvent e, ProcessResult r, DateTimeOffset processedAt) =>
        JsonSerializer.Serialize(new
        {
            e.EventId,
            e.ShipmentId,
            e.Carrier,
            e.TrackingCode,
            e.OrderId,
            status = e.Status.ToString(),
            e.RawStatus,
            e.OccurredAt,
            e.ReceivedAt,
            processedAt,
            e.Region,
            e.Location,
            e.EstimatedDelivery,
            outcome = r.Decision.Outcome.ToString(),
            r.Decision.Reason,
            currentStatus = r.State?.Status.ToString(),
            isLate = r.State?.IsLate
        }, ShipmentJson.Options);

    private async Task TryWriteLakeAsync(List<string> lines, ILambdaContext context)
    {
        if (lines.Count == 0) return;
        // Partition by processing date: s3://bucket/events/dt=2026-11-16/<guid>.jsonl
        var key = $"events/dt={DateTime.UtcNow:yyyy-MM-dd}/{Guid.NewGuid():N}.jsonl";
        try
        {
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _lakeBucket,
                Key = key,
                ContentBody = string.Join('\n', lines) + "\n",
                ContentType = "application/x-ndjson"
            });
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Event lake write failed ({lines.Count} lines, key {key}): {ex.Message}");
        }
    }

    private async Task TryPublishLateMetricAsync(Dictionary<string, int> lateByCarrier, ILambdaContext context)
    {
        if (lateByCarrier.Count == 0) return;

        var data = new List<MetricDatum>();
        foreach (var (carrier, count) in lateByCarrier)
        {
            // Two dimension sets: per service (used by the alarm) and per carrier (for comparison).
            data.Add(Datum(count, new Dimension { Name = "Service", Value = ServiceName }));
            data.Add(Datum(count,
                new Dimension { Name = "Service", Value = ServiceName },
                new Dimension { Name = "Carrier", Value = carrier }));
        }

        try
        {
            await _cloudWatch.PutMetricDataAsync(new PutMetricDataRequest { Namespace = MetricNamespace, MetricData = data });
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"PutMetricData failed: {ex.Message}");
        }
    }

    private static MetricDatum Datum(int value, params Dimension[] dims) => new()
    {
        MetricName = "LateDeliveries",
        Unit = StandardUnit.Count,
        Value = value,
        TimestampUtc = DateTime.UtcNow,
        Dimensions = dims.ToList()
    };

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing environment variable {name}");
}
