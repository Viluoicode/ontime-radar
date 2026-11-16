// Tests for the shipment state machine and SLA rule.
// (Kiểm thử luật chuyển trạng thái – phần nghiệp vụ quan trọng nhất.)
using ShipmentSla.Core;
using Xunit;

namespace ShipmentSla.Core.Tests;

public class StatusRulesTests
{
    private static readonly DateTimeOffset T0 = new(2017, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private static NormalizedEvent Event(ShipmentStatus status, DateTimeOffset at, DateOnly? estimate = null) =>
        new($"id-{status}-{at:O}", "ghn", "GHN123", status, status.ToString(), at, at, EstimatedDelivery: estimate);

    private static ShipmentState State(ShipmentStatus status, DateTimeOffset lastEventAt) =>
        new("ghn", "GHN123", status, lastEventAt, lastEventAt, Version: 3);

    [Fact]
    public void First_event_is_always_applied()
    {
        var d = StatusRules.Decide(null, Event(ShipmentStatus.InTransit, T0));
        Assert.Equal(EventOutcome.Applied, d.Outcome);
    }

    [Fact]
    public void Forward_transition_is_applied()
    {
        var d = StatusRules.Decide(State(ShipmentStatus.PickedUp, T0), Event(ShipmentStatus.InTransit, T0.AddHours(5)));
        Assert.Equal(EventOutcome.Applied, d.Outcome);
    }

    [Fact]
    public void Repeated_in_transit_hub_scans_are_applied()
    {
        var d = StatusRules.Decide(State(ShipmentStatus.InTransit, T0), Event(ShipmentStatus.InTransit, T0.AddHours(8)));
        Assert.Equal(EventOutcome.Applied, d.Outcome);
    }

    [Fact]
    public void Older_event_arriving_late_is_stale()
    {
        var d = StatusRules.Decide(State(ShipmentStatus.OutForDelivery, T0), Event(ShipmentStatus.InTransit, T0.AddHours(-3)));
        Assert.Equal(EventOutcome.Stale, d.Outcome);
    }

    [Fact]
    public void Nothing_changes_a_delivered_shipment()
    {
        var d = StatusRules.Decide(State(ShipmentStatus.Delivered, T0), Event(ShipmentStatus.Returned, T0.AddDays(1)));
        Assert.Equal(EventOutcome.Rejected, d.Outcome);
    }

    [Fact]
    public void Backward_transition_is_rejected()
    {
        var d = StatusRules.Decide(State(ShipmentStatus.OutForDelivery, T0), Event(ShipmentStatus.InTransit, T0.AddHours(1)));
        Assert.Equal(EventOutcome.Rejected, d.Outcome);
    }

    [Fact]
    public void Cancel_after_pickup_is_rejected()
    {
        var d = StatusRules.Decide(State(ShipmentStatus.PickedUp, T0), Event(ShipmentStatus.Cancelled, T0.AddHours(1)));
        Assert.Equal(EventOutcome.Rejected, d.Outcome);
    }

    [Theory]
    [InlineData(ShipmentStatus.OutForDelivery, ShipmentStatus.FailedAttempt)]
    [InlineData(ShipmentStatus.FailedAttempt, ShipmentStatus.OutForDelivery)] // re-delivery attempt
    [InlineData(ShipmentStatus.FailedAttempt, ShipmentStatus.Returned)]
    public void Delivery_attempt_loop_is_allowed(ShipmentStatus from, ShipmentStatus to)
    {
        var d = StatusRules.Decide(State(from, T0), Event(to, T0.AddHours(2)));
        Assert.Equal(EventOutcome.Applied, d.Outcome);
    }

    [Fact]
    public void Apply_marks_late_delivery_and_increments_version()
    {
        var current = new ShipmentState("ghn", "GHN123", ShipmentStatus.OutForDelivery, T0, T0, Version: 4,
            EstimatedDelivery: new DateOnly(2017, 10, 1));

        var next = StatusRules.Apply(current, Event(ShipmentStatus.Delivered, T0.AddHours(1)), T0.AddHours(2));

        Assert.Equal(ShipmentStatus.Delivered, next.Status);
        Assert.Equal(5, next.Version);
        Assert.Equal(T0.AddHours(1), next.DeliveredAt);
        Assert.True(next.IsLate);                              // delivered 2 Oct, promised 1 Oct
        Assert.Equal(new DateOnly(2017, 10, 1), next.EstimatedDelivery); // kept from earlier state
    }

    [Theory]
    [InlineData("2017-10-02T23:59:00Z", "2017-10-02", false)] // same day = on time
    [InlineData("2017-10-03T00:01:00Z", "2017-10-02", true)]  // next day = late
    public void Late_is_judged_by_calendar_day(string delivered, string promised, bool expectedLate)
    {
        var late = SlaRules.IsLate(DateTimeOffset.Parse(delivered), DateOnly.Parse(promised));
        Assert.Equal(expectedLate, late);
    }

    [Fact]
    public void Late_is_unknown_without_a_promise() =>
        Assert.Null(SlaRules.IsLate(T0, null));
}
