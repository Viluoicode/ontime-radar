// The shipment state machine: decides whether an event may change the current state.
// (Bộ luật chuyển trạng thái – chống sự kiện cũ, đi lùi, sau trạng thái kết thúc.)
namespace ShipmentSla.Core;

public sealed record Decision(EventOutcome Outcome, string Reason);

public static class StatusRules
{
    // Progress rank: a newer event must not move the shipment to a lower rank.
    private static int Rank(ShipmentStatus s) => s switch
    {
        ShipmentStatus.Created => 0,
        ShipmentStatus.PickedUp => 1,
        ShipmentStatus.InTransit => 2,
        ShipmentStatus.OutForDelivery => 3,
        ShipmentStatus.FailedAttempt => 3,   // same rank: delivery attempts can loop
        ShipmentStatus.Delivered => 4,
        ShipmentStatus.Returned => 4,
        ShipmentStatus.Cancelled => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(s))
    };

    public static bool IsTerminal(ShipmentStatus s) =>
        s is ShipmentStatus.Delivered or ShipmentStatus.Returned or ShipmentStatus.Cancelled;

    /// <summary>
    /// Decide what to do with <paramref name="e"/> given the current state (null = first event).
    /// Duplicates are detected by storage (eventId), not here.
    /// </summary>
    public static Decision Decide(ShipmentState? current, NormalizedEvent e)
    {
        // First event for this shipment: accept anything (carriers may skip "Created").
        if (current is null)
            return new(EventOutcome.Applied, "first event");

        // 1) Late-arriving older event → keep in history, do not touch the state.
        if (e.OccurredAt < current.LastEventAt)
            return new(EventOutcome.Stale, $"occurred before latest applied event ({current.LastEventAt:O})");

        // 2) Nothing may change a finished shipment.
        if (IsTerminal(current.Status))
            return new(EventOutcome.Rejected, $"shipment already {current.Status}");

        // 3) Cancelling is only possible before pickup.
        if (e.Status == ShipmentStatus.Cancelled && current.Status != ShipmentStatus.Created)
            return new(EventOutcome.Rejected, "cannot cancel after pickup");

        // 4) No going backwards (InTransit → InTransit is allowed: hub scans).
        if (Rank(e.Status) < Rank(current.Status))
            return new(EventOutcome.Rejected, $"backward transition {current.Status} → {e.Status}");

        return new(EventOutcome.Applied, "ok");
    }

    /// <summary>Build the new state after an <see cref="EventOutcome.Applied"/> decision.</summary>
    public static ShipmentState Apply(ShipmentState? current, NormalizedEvent e, DateTimeOffset now)
    {
        // A newer event may carry a (re-)estimated delivery date; otherwise keep the old one.
        var estimate = e.EstimatedDelivery ?? current?.EstimatedDelivery;
        var deliveredAt = e.Status == ShipmentStatus.Delivered ? e.OccurredAt : current?.DeliveredAt;

        return new ShipmentState(
            Carrier: e.Carrier,
            TrackingCode: e.TrackingCode,
            Status: e.Status,
            LastEventAt: e.OccurredAt,
            UpdatedAt: now,
            Version: (current?.Version ?? 0) + 1,
            EstimatedDelivery: estimate,
            DeliveredAt: deliveredAt,
            IsLate: deliveredAt is null ? null : SlaRules.IsLate(deliveredAt.Value, estimate),
            Region: e.Region ?? current?.Region,
            OrderId: e.OrderId ?? current?.OrderId);
    }
}

public static class SlaRules
{
    /// <summary>
    /// Late = delivered on a calendar day (UTC) after the promised day.
    /// Returns null when no promise is known (cannot judge).
    /// </summary>
    public static bool? IsLate(DateTimeOffset deliveredAt, DateOnly? estimatedDelivery) =>
        estimatedDelivery is null
            ? null
            : DateOnly.FromDateTime(deliveredAt.UtcDateTime) > estimatedDelivery.Value;
}
