// Core domain models – no AWS dependency, so this project is fully unit-testable.
// (Mô hình nghiệp vụ dùng chung cho API, Processor, Replayer.)
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShipmentSla.Core;

/// <summary>The 8 carrier-independent statuses every carrier is normalized into.</summary>
public enum ShipmentStatus
{
    Created,
    PickedUp,
    InTransit,
    OutForDelivery,
    FailedAttempt,
    Delivered,
    Returned,
    Cancelled
}

/// <summary>What the processor decided to do with an incoming event.</summary>
public enum EventOutcome
{
    Applied,   // state was updated
    Stale,     // older than the latest applied event → kept in history only
    Rejected,  // invalid transition (e.g. going backwards, after a terminal status)
    Duplicate  // same eventId seen before → ignored (idempotency)
}

/// <summary>
/// One carrier event after normalization. This is the message that travels through SQS.
/// (Sự kiện đã chuẩn hóa – định dạng chung cho mọi hãng.)
/// </summary>
public sealed record NormalizedEvent(
    string EventId,                 // deterministic hash → same webhook twice = same id
    string Carrier,                 // "ghn" | "ghtk" | "jt"
    string TrackingCode,            // carrier's own tracking number
    ShipmentStatus Status,
    string RawStatus,               // original carrier code, kept for debugging
    DateTimeOffset OccurredAt,      // when it happened (always UTC)
    DateTimeOffset ReceivedAt,      // when our API received it (UTC)
    string? Location = null,
    DateOnly? EstimatedDelivery = null,
    string? Region = null,          // destination province/state
    string? OrderId = null)
{
    /// <summary>Shipment key = carrier + tracking code (tracking codes are only unique per carrier).</summary>
    public string ShipmentId => ShipmentKeys.Build(Carrier, TrackingCode);
}

/// <summary>Current state of one shipment (one row per shipment in DynamoDB).</summary>
public sealed record ShipmentState(
    string Carrier,
    string TrackingCode,
    ShipmentStatus Status,
    DateTimeOffset LastEventAt,     // OccurredAt of the latest *applied* event
    DateTimeOffset UpdatedAt,
    int Version,                    // optimistic-concurrency counter
    DateOnly? EstimatedDelivery = null,
    DateTimeOffset? DeliveredAt = null,
    bool? IsLate = null,            // null until delivered (or no estimate known)
    string? Region = null,
    string? OrderId = null)
{
    public string ShipmentId => ShipmentKeys.Build(Carrier, TrackingCode);
}

public static class ShipmentKeys
{
    public static string Build(string carrier, string trackingCode) => $"{carrier}#{trackingCode}";
}

/// <summary>Shared JSON settings: camelCase + enums as strings ("InTransit" instead of 2).</summary>
public static class ShipmentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Thrown when a carrier payload cannot be understood → API answers 400.</summary>
public sealed class CarrierPayloadException(string message) : Exception(message);
