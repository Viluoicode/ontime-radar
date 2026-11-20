// ShipmentStore: decide + persist in ONE DynamoDB transaction; a repeated eventId must become Duplicate,
// a concurrent writer (version conflict) must NOT be mistaken for a Duplicate.
// (Kiểm thử transaction, chống trùng, xung đột version – DynamoDB giả trong bộ nhớ.)
using Amazon.DynamoDBv2.Model;
using ShipmentSla.Core;
using ShipmentSla.Infrastructure;
using Xunit;
using static ShipmentSla.Service.Tests.TestData;

namespace ShipmentSla.Service.Tests;

public class ShipmentStoreTests
{
    private readonly FakeDynamoTable _table = new();
    private readonly ShipmentStore _store;
    private static readonly DateTimeOffset Now = T0.AddDays(10);

    public ShipmentStoreTests() => _store = new ShipmentStore(_table.Client, "test-table");

    private static Put PutAt(TransactWriteItemsRequest r, int i) => r.TransactItems[i].Put;

    [Fact] // S1
    public async Task First_event_writes_history_and_state_in_one_transaction()
    {
        var result = await _store.ProcessAsync(Event("A1", ShipmentStatus.PickedUp, T0), Now);

        Assert.Equal(EventOutcome.Applied, result.Decision.Outcome);
        var tx = Assert.Single(_table.Transactions);
        Assert.Equal(2, tx.TransactItems.Count);
        Assert.StartsWith("EVENT#", PutAt(tx, 0).Item["SK"].S);
        Assert.Equal("attribute_not_exists(PK)", PutAt(tx, 0).ConditionExpression);   // history: never overwrite
        Assert.Equal("STATE", PutAt(tx, 1).Item["SK"].S);
        Assert.Equal("attribute_not_exists(PK)", PutAt(tx, 1).ConditionExpression);   // first state: must not exist yet
        Assert.Equal(1, result.State!.Version);
        Assert.Equal(2, _table.Count);
    }

    [Fact] // S2
    public async Task Next_event_is_guarded_by_the_current_version()
    {
        await _store.ProcessAsync(Event("A1", ShipmentStatus.PickedUp, T0), Now);
        var result = await _store.ProcessAsync(Event("A1", ShipmentStatus.InTransit, T0.AddHours(5)), Now);

        var statePut = PutAt(_table.Transactions[1], 1);
        Assert.Equal("#v = :v", statePut.ConditionExpression);
        Assert.Equal("version", statePut.ExpressionAttributeNames["#v"]);
        Assert.Equal("1", statePut.ExpressionAttributeValues[":v"].N);                 // the version we read
        Assert.Equal(2, result.State!.Version);
        Assert.Equal(ShipmentStatus.InTransit, (await _store.GetStateAsync("ghn#A1"))!.Status);
    }

    [Fact] // S3
    public async Task Same_event_twice_is_a_Duplicate_and_writes_nothing()
    {
        var e = Event("A1", ShipmentStatus.PickedUp, T0);
        await _store.ProcessAsync(e, Now);
        var itemsBefore = _table.Count;

        var second = await _store.ProcessAsync(e, Now.AddMinutes(1));

        Assert.Equal(EventOutcome.Duplicate, second.Decision.Outcome);
        Assert.Equal(1, second.State!.Version);                 // state untouched (all-or-nothing)
        Assert.False(second.NewlyLate);
        Assert.False(second.NewlyDelivered);
        Assert.Equal(itemsBefore, _table.Count);
        Assert.Equal(2, _table.Transactions.Count);              // it was attempted, then cancelled
    }

    [Fact] // S3b – a redelivered late delivery must not be counted twice in the SLA metrics
    public async Task Duplicate_late_delivery_does_not_count_again()
    {
        await _store.ProcessAsync(Event("A1", ShipmentStatus.PickedUp, T0, promise: Promise), Now);
        var delivered = Event("A1", ShipmentStatus.Delivered, T0.AddDays(7), promise: Promise); // after the promise

        var first = await _store.ProcessAsync(delivered, Now);
        var again = await _store.ProcessAsync(delivered, Now);

        Assert.True(first.NewlyDelivered);
        Assert.True(first.NewlyLate);
        Assert.Equal(EventOutcome.Duplicate, again.Decision.Outcome);
        Assert.False(again.NewlyDelivered);
        Assert.False(again.NewlyLate);
    }

    [Fact] // S4 – the most important one: a lost race must be retried, not silently dropped
    public async Task Version_conflict_is_not_mistaken_for_a_Duplicate()
    {
        await _store.ProcessAsync(Event("A1", ShipmentStatus.PickedUp, T0), Now);

        // Another writer bumps the state to version 2 after we read version 1, before our transaction runs.
        _table.FailTransaction = _ =>
        {
            var state = _table.Item("SHIPMENT#ghn#A1", "STATE")!;
            state["version"] = new AttributeValue { N = "2" };
            _table.Seed(state);
            _table.FailTransaction = null;
            return null;                                         // let the transaction run against the new state
        };

        var ex = await Assert.ThrowsAsync<TransactionCanceledException>(() =>
            _store.ProcessAsync(Event("A1", ShipmentStatus.InTransit, T0.AddHours(5)), Now));

        Assert.Equal(["None", "ConditionalCheckFailed"], ex.CancellationReasons.Select(r => r.Code));
        Assert.Single(_table.ItemsWithPk("SHIPMENT#ghn#A1"), i => i["SK"].S.StartsWith("EVENT#")); // new history NOT written
    }

    [Theory] // S5
    [InlineData(-1, EventOutcome.Stale)]     // older than the latest applied event
    [InlineData(+1, EventOutcome.Rejected)]  // backwards (InTransit after OutForDelivery)
    public async Task Stale_or_rejected_event_only_adds_history(int hours, EventOutcome expected)
    {
        await _store.ProcessAsync(Event("A1", ShipmentStatus.OutForDelivery, T0), Now);

        var result = await _store.ProcessAsync(Event("A1", ShipmentStatus.InTransit, T0.AddHours(hours)), Now);

        Assert.Equal(expected, result.Decision.Outcome);
        Assert.Single(_table.Transactions[1].TransactItems);     // history only
        var state = await _store.GetStateAsync("ghn#A1");
        Assert.Equal(ShipmentStatus.OutForDelivery, state!.Status);
        Assert.Equal(1, state.Version);
        var timeline = await _store.GetTimelineAsync("ghn#A1");
        Assert.Contains(timeline!.Events, ev => ev.Outcome == expected);
    }

    [Fact] // S6
    public async Task Cancelled_transaction_without_reasons_is_not_a_Duplicate()
    {
        _table.FailTransaction = _ => new TransactionCanceledException("throttled") { CancellationReasons = [] };

        await Assert.ThrowsAsync<TransactionCanceledException>(() =>
            _store.ProcessAsync(Event("A1", ShipmentStatus.PickedUp, T0), Now));
        Assert.Equal(0, _table.Count);
    }

    [Fact] // S7
    public async Task State_round_trips_every_field_in_utc()
    {
        var vnTime = new DateTimeOffset(2026, 11, 4, 22, 30, 0, TimeSpan.FromHours(7));    // = 15:30 UTC
        await _store.ProcessAsync(Event("A1", ShipmentStatus.PickedUp, T0, promise: Promise, region: "RJ"), Now);
        await _store.ProcessAsync(Event("A1", ShipmentStatus.Delivered, vnTime, promise: null, region: null), Now);

        var s = await _store.GetStateAsync("ghn#A1");

        Assert.NotNull(s);
        Assert.Equal(ShipmentStatus.Delivered, s.Status);
        Assert.Equal(vnTime, s.DeliveredAt);                     // same instant…
        Assert.Equal(TimeSpan.Zero, s.DeliveredAt!.Value.Offset);  // …stored and read back as UTC
        Assert.Equal(Promise, s.EstimatedDelivery);              // kept from the earlier event
        Assert.Equal("RJ", s.Region);                            // kept from the earlier event
        Assert.False(s.IsLate);                                  // delivered 4 Nov, promised 5 Nov
        Assert.Equal("order-A1", s.OrderId);
        Assert.Equal(2, s.Version);
        Assert.Equal(Now, s.UpdatedAt);
    }

    [Fact] // S8
    public async Task Timeline_reads_every_page()
    {
        _table.QueryPageSize = 2;
        var statuses = new[] { ShipmentStatus.Created, ShipmentStatus.PickedUp, ShipmentStatus.InTransit,
                               ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered };
        for (var i = 0; i < statuses.Length; i++)
            await _store.ProcessAsync(Event("A1", statuses[i], T0.AddHours(i)), Now);

        var timeline = await _store.GetTimelineAsync("ghn#A1");

        Assert.Equal(statuses, timeline!.Events.Select(e => e.Status));   // all 5, in time order
        Assert.Equal(ShipmentStatus.Delivered, timeline.State.Status);
    }

    [Fact] // S9
    public async Task Unknown_shipment_has_no_timeline() =>
        Assert.Null(await _store.GetTimelineAsync("ghn#NOPE"));
}
