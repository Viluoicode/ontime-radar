// DynamoDB single-table access for shipments.
//   PK = SHIPMENT#{carrier}#{tracking}   SK = STATE                              → current state (1 item)
//   PK = SHIPMENT#{carrier}#{tracking}   SK = EVENT#{occurredAtUtc}#{eventId}   → history (sorted by time)
//   PK = REPLAY                          SK = CURSOR                             → replayer progress
// (Một bảng duy nhất: trạng thái + lịch sử nằm chung partition → 1 Query lấy được cả timeline.)
using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ShipmentSla.Core;

namespace ShipmentSla.Infrastructure;

public sealed record TimelineEntry(
    string EventId, ShipmentStatus Status, string RawStatus, DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt, EventOutcome Outcome, string Reason, string? Location);

public sealed record ShipmentTimeline(ShipmentState State, IReadOnlyList<TimelineEntry> Events);

/// <summary>
/// Result of processing one event. NewlyDelivered / NewlyLate drive the CloudWatch metrics
/// "Deliveries" and "LateDeliveries" (the alarm watches late / delivered).
/// </summary>
public sealed record ProcessResult(Decision Decision, ShipmentState? State, bool NewlyLate, bool NewlyDelivered = false);

public sealed class ShipmentStore(IAmazonDynamoDB ddb, string tableName)
{
    private const string StateSk = "STATE";
    private static string Pk(string shipmentId) => $"SHIPMENT#{shipmentId}";
    private static string EventSk(NormalizedEvent e) => $"EVENT#{e.OccurredAt.UtcDateTime:O}#{e.EventId}";

    public async Task<ShipmentState?> GetStateAsync(string shipmentId, CancellationToken ct = default)
    {
        var res = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Key(Pk(shipmentId), StateSk),
            ConsistentRead = true // read-your-own-writes inside the processor
        }, ct);
        return res.Item is { Count: > 0 } item ? ToState(item) : null;
    }

    /// <summary>State + full event history in one Query (same partition key).</summary>
    public async Task<ShipmentTimeline?> GetTimelineAsync(string shipmentId, CancellationToken ct = default)
    {
        ShipmentState? state = null;
        var events = new List<TimelineEntry>();
        Dictionary<string, AttributeValue>? startKey = null;

        do // paginate: a long-lived shipment could exceed 1 MB of history
        {
            var res = await ddb.QueryAsync(new QueryRequest
            {
                TableName = tableName,
                KeyConditionExpression = "PK = :pk",
                ExpressionAttributeValues = new() { [":pk"] = S(Pk(shipmentId)) },
                ExclusiveStartKey = startKey
            }, ct);

            foreach (var item in res.Items)
            {
                if (item["SK"].S == StateSk) state = ToState(item);
                else events.Add(ToTimelineEntry(item));
            }
            startKey = res.LastEvaluatedKey is { Count: > 0 } k ? k : null;
        } while (startKey is not null);

        return state is null ? null : new ShipmentTimeline(state, events);
    }

    /// <summary>
    /// Decide + persist atomically: history item and (if applied) new state in ONE transaction.
    ///  - history Put has "attribute_not_exists(PK)" → a repeated eventId cancels the transaction → Duplicate
    ///  - state Put checks the version → two writers can never overwrite each other
    /// </summary>
    public async Task<ProcessResult> ProcessAsync(NormalizedEvent e, DateTimeOffset now, CancellationToken ct = default)
    {
        var current = await GetStateAsync(e.ShipmentId, ct);
        var decision = StatusRules.Decide(current, e);

        var writes = new List<TransactWriteItem>
        {
            new()
            {
                Put = new Put
                {
                    TableName = tableName,
                    Item = ToEventItem(e, decision, now),
                    ConditionExpression = "attribute_not_exists(PK)"
                }
            }
        };

        ShipmentState? newState = null;
        if (decision.Outcome == EventOutcome.Applied)
        {
            newState = StatusRules.Apply(current, e, now);
            var statePut = new Put { TableName = tableName, Item = ToStateItem(newState) };
            if (current is null)
            {
                statePut.ConditionExpression = "attribute_not_exists(PK)";
            }
            else
            {
                statePut.ConditionExpression = "#v = :v";
                statePut.ExpressionAttributeNames = new() { ["#v"] = "version" };
                statePut.ExpressionAttributeValues = new() { [":v"] = N(current.Version) };
            }
            writes.Add(new TransactWriteItem { Put = statePut });
        }

        try
        {
            await ddb.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = writes }, ct);
        }
        catch (TransactionCanceledException ex)
            when (ex.CancellationReasons is { Count: > 0 } reasons && reasons[0].Code == "ConditionalCheckFailed")
        {
            // History item already exists → we processed this exact event before.
            return new ProcessResult(new Decision(EventOutcome.Duplicate, "eventId already processed"), current, false);
        }
        // Any other failure (e.g. version conflict) bubbles up → SQS redelivers the message → retried.

        var (newlyDelivered, newlyLate) = SlaRules.MetricDelta(current, newState ?? current);
        return new ProcessResult(decision, newState ?? current, newlyLate, newlyDelivered);
    }

    // ---------- replayer cursor ----------

    public async Task<int> GetReplayCursorAsync(CancellationToken ct = default)
    {
        var res = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = tableName, Key = Key("REPLAY", "CURSOR"), ConsistentRead = true
        }, ct);
        return res.Item is { Count: > 0 } item ? int.Parse(item["offset"].N, CultureInfo.InvariantCulture) : 0;
    }

    public Task SetReplayCursorAsync(int offset, CancellationToken ct = default) =>
        ddb.PutItemAsync(new PutItemRequest
        {
            TableName = tableName,
            Item = new() { ["PK"] = S("REPLAY"), ["SK"] = S("CURSOR"), ["offset"] = N(offset) }
        }, ct);

    // ---------- mapping helpers (DynamoDB item <-> domain records) ----------

    private static AttributeValue S(string v) => new() { S = v };
    private static AttributeValue N(int v) => new() { N = v.ToString(CultureInfo.InvariantCulture) };
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseIso(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new() { ["PK"] = S(pk), ["SK"] = S(sk) };

    private static string? Opt(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var v) ? v.S : null;

    private static Dictionary<string, AttributeValue> ToStateItem(ShipmentState s)
    {
        var item = Key(Pk(s.ShipmentId), StateSk);
        item["carrier"] = S(s.Carrier);
        item["trackingCode"] = S(s.TrackingCode);
        item["status"] = S(s.Status.ToString());
        item["lastEventAt"] = S(Iso(s.LastEventAt));
        item["updatedAt"] = S(Iso(s.UpdatedAt));
        item["version"] = N(s.Version);
        // Only write optional attributes when they have a value (DynamoDB has no "empty").
        if (s.EstimatedDelivery is { } est) item["estimatedDelivery"] = S(est.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (s.DeliveredAt is { } del) item["deliveredAt"] = S(Iso(del));
        if (s.IsLate is { } late) item["isLate"] = new AttributeValue { BOOL = late };
        if (s.Region is { } region) item["region"] = S(region);
        if (s.OrderId is { } order) item["orderId"] = S(order);
        return item;
    }

    private static ShipmentState ToState(Dictionary<string, AttributeValue> i) => new(
        Carrier: i["carrier"].S,
        TrackingCode: i["trackingCode"].S,
        Status: Enum.Parse<ShipmentStatus>(i["status"].S),
        LastEventAt: ParseIso(i["lastEventAt"].S),
        UpdatedAt: ParseIso(i["updatedAt"].S),
        Version: int.Parse(i["version"].N, CultureInfo.InvariantCulture),
        EstimatedDelivery: Opt(i, "estimatedDelivery") is { } est ? DateOnly.ParseExact(est, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        DeliveredAt: Opt(i, "deliveredAt") is { } del ? ParseIso(del) : null,
        IsLate: i.TryGetValue("isLate", out var late) ? late.BOOL : null,
        Region: Opt(i, "region"),
        OrderId: Opt(i, "orderId"));

    private static Dictionary<string, AttributeValue> ToEventItem(NormalizedEvent e, Decision d, DateTimeOffset now)
    {
        var item = Key(Pk(e.ShipmentId), EventSk(e));
        item["eventId"] = S(e.EventId);
        item["status"] = S(e.Status.ToString());
        item["rawStatus"] = S(e.RawStatus);
        item["occurredAt"] = S(Iso(e.OccurredAt));
        item["receivedAt"] = S(Iso(e.ReceivedAt));
        item["processedAt"] = S(Iso(now));
        item["outcome"] = S(d.Outcome.ToString());
        item["reason"] = S(d.Reason);
        if (e.Location is { } loc) item["location"] = S(loc);
        return item;
    }

    private static TimelineEntry ToTimelineEntry(Dictionary<string, AttributeValue> i) => new(
        EventId: i["eventId"].S,
        Status: Enum.Parse<ShipmentStatus>(i["status"].S),
        RawStatus: i["rawStatus"].S,
        OccurredAt: ParseIso(i["occurredAt"].S),
        ReceivedAt: ParseIso(i["receivedAt"].S),
        Outcome: Enum.Parse<EventOutcome>(i["outcome"].S),
        Reason: i["reason"].S,
        Location: Opt(i, "location"));
}
