// Small builders shared by the test classes.
using ShipmentSla.Core;

namespace ShipmentSla.Service.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
    public static readonly DateOnly Promise = new(2026, 11, 5);

    /// <summary>Event with a deterministic id derived from its content (like the real adapters).</summary>
    public static NormalizedEvent Event(string tracking, ShipmentStatus status, DateTimeOffset at,
        string carrier = "ghn", DateOnly? promise = null, string? region = "SP", string? id = null) =>
        new(id ?? $"{carrier}-{tracking}-{status}-{at:yyyyMMddHHmmss}", carrier, tracking, status, status.ToString(),
            at, at.AddMinutes(1), Location: "HUB-1", EstimatedDelivery: promise, Region: region, OrderId: $"order-{tracking}");
}
