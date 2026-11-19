// Tests for what each processed event contributes to the late-delivery RATE alarm.
using ShipmentSla.Core;

namespace ShipmentSla.Core.Tests;

public class SlaMetricTests
{
    private static readonly DateTimeOffset T = new(2017, 5, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Promise = new(2017, 5, 9);

    private static ShipmentState State(ShipmentStatus status, bool? isLate = null, DateOnly? promise = null) =>
        new("ghn", "GHN1", status, T, T, 1, promise ?? Promise, status == ShipmentStatus.Delivered ? T : null, isLate);

    [Fact]
    public void On_time_delivery_counts_as_delivered_not_late()
    {
        var d = SlaRules.MetricDelta(State(ShipmentStatus.OutForDelivery), State(ShipmentStatus.Delivered, isLate: false));
        Assert.Equal((true, false), d);
    }

    [Fact]
    public void Late_delivery_counts_as_delivered_and_late()
    {
        var d = SlaRules.MetricDelta(State(ShipmentStatus.OutForDelivery), State(ShipmentStatus.Delivered, isLate: true));
        Assert.Equal((true, true), d);
    }

    [Fact]
    public void First_event_being_a_delivery_still_counts()
    {
        Assert.Equal((true, true), SlaRules.MetricDelta(null, State(ShipmentStatus.Delivered, isLate: true)));
    }

    [Fact]
    public void Delivery_without_promise_is_not_counted()   // cannot be on time or late → must not dilute the rate
    {
        var after = State(ShipmentStatus.Delivered, isLate: null) with { EstimatedDelivery = null };
        Assert.Equal((false, false), SlaRules.MetricDelta(State(ShipmentStatus.InTransit), after));
    }

    [Fact]
    public void Unchanged_delivered_state_is_not_counted_twice()   // duplicate / rejected event after delivery
    {
        var delivered = State(ShipmentStatus.Delivered, isLate: true);
        Assert.Equal((false, false), SlaRules.MetricDelta(delivered, delivered));
    }

    [Fact]
    public void Events_before_delivery_count_nothing()
    {
        Assert.Equal((false, false), SlaRules.MetricDelta(State(ShipmentStatus.PickedUp), State(ShipmentStatus.InTransit)));
    }
}
