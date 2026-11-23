// One adapter per carrier: turns that carrier's webhook JSON into a NormalizedEvent.
// NOTE: payload formats are SIMULATED (inspired by VN carriers), not official API specs.
// (Mỗi hãng 1 adapter – thêm hãng mới chỉ cần thêm 1 class.)
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShipmentSla.Core.Carriers;

public interface ICarrierAdapter
{
    string Code { get; }   // used in the URL: POST /carriers/{code}/webhook
    NormalizedEvent Parse(JsonElement payload, DateTimeOffset receivedAt);
}

public static class CarrierRegistry
{
    private static readonly Dictionary<string, ICarrierAdapter> Adapters =
        new ICarrierAdapter[] { new GhnAdapter(), new GhtkAdapter(), new JtAdapter() }
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Codes => Adapters.Keys;

    public static bool TryGet(string code, out ICarrierAdapter adapter) =>
        Adapters.TryGetValue(code, out adapter!);
}

/// <summary>Deterministic event id: the same webhook delivered twice gets the same id.</summary>
public static class EventIds
{
    public static string Create(string carrier, string tracking, string rawStatus, DateTimeOffset occurredAt)
    {
        var input = $"{carrier}|{tracking}|{rawStatus}|{occurredAt.UtcDateTime:O}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant(); // 32 hex chars is plenty
    }
}

/// <summary>Rules for the carrier's tracking code (it becomes part of keys, URLs and S3/Athena data).</summary>
public static class TrackingCodes
{
    public const int MaxLength = 64;

    // \z (not $): in .NET "$" also matches before a trailing "\n", so "ABC\n" would pass with ^...$.
    private static readonly Regex Allowed = new(@"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant);

    public static bool IsValid(string? code) => code is not null && Allowed.IsMatch(code);
}

/// <summary>Small helpers so each adapter stays short and gives clear 400 messages.</summary>
/// <remarks>
/// JsonElement.TryGetProperty / TryGetInt32 THROW InvalidOperationException on the wrong ValueKind
/// (array body, number sent as a string) instead of returning false. The API turns only
/// CarrierPayloadException / JsonException into 400, so every kind check happens here first.
/// </remarks>
internal static class Json
{
    /// <summary>Call first in every adapter: the body must be a JSON object (not an array, string, null...).</summary>
    public static void RequireObject(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new CarrierPayloadException($"payload must be a JSON object, got {e.ValueKind}");
    }

    public static int RequiredInt32(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : throw new CarrierPayloadException($"missing integer field '{name}'");

    public static long RequiredInt64(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : throw new CarrierPayloadException($"missing integer field '{name}'");

    /// <summary>Required tracking code: 1–64 chars of A-Z a-z 0-9 . _ - (rejects '#', '/', spaces, unicode...).</summary>
    public static string RequiredTrackingCode(JsonElement e, string name)
    {
        var s = RequiredString(e, name);
        return TrackingCodes.IsValid(s)
            ? s
            : throw new CarrierPayloadException(
                $"field '{name}' must be 1-{TrackingCodes.MaxLength} characters of A-Z a-z 0-9 . _ -");
    }

    public static string RequiredString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : throw new CarrierPayloadException($"missing or empty string field '{name}'");

    public static string? OptionalString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static DateOnly? OptionalDate(JsonElement e, string name)
    {
        var s = OptionalString(e, name);
        if (string.IsNullOrEmpty(s)) return null;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new CarrierPayloadException($"field '{name}' must be yyyy-MM-dd");
    }

    /// <summary>ISO-8601 with offset (Z or +07:00). Always returned as UTC.</summary>
    public static DateTimeOffset RequiredTimestamp(JsonElement e, string name)
    {
        var s = RequiredString(e, name);
        // Must end with "Z" or "+hh:mm"/"-hh:mm": a timestamp without timezone is ambiguous.
        return Regex.IsMatch(s, @"(Z|[+-]\d{2}:\d{2})$")
               && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToUniversalTime()
            : throw new CarrierPayloadException($"field '{name}' must be ISO-8601 with timezone");
    }
}

/// <summary>GHN-style: text statuses, ISO UTC timestamps.</summary>
public sealed class GhnAdapter : ICarrierAdapter
{
    public string Code => "ghn";

    public static readonly IReadOnlyDictionary<string, ShipmentStatus> StatusMap = new Dictionary<string, ShipmentStatus>
    {
        ["ready_to_pick"] = ShipmentStatus.Created,
        ["picked"] = ShipmentStatus.PickedUp,
        ["storing"] = ShipmentStatus.InTransit,
        ["transporting"] = ShipmentStatus.InTransit,
        ["delivering"] = ShipmentStatus.OutForDelivery,
        ["delivery_fail"] = ShipmentStatus.FailedAttempt,
        ["delivered"] = ShipmentStatus.Delivered,
        ["return"] = ShipmentStatus.Returned,
        ["cancel"] = ShipmentStatus.Cancelled
    };

    public NormalizedEvent Parse(JsonElement p, DateTimeOffset receivedAt)
    {
        Json.RequireObject(p);
        var tracking = Json.RequiredTrackingCode(p, "order_code");
        var raw = Json.RequiredString(p, "status");
        if (!StatusMap.TryGetValue(raw, out var status))
            throw new CarrierPayloadException($"unknown GHN status '{raw}'");
        var at = Json.RequiredTimestamp(p, "updated_date");

        return new NormalizedEvent(EventIds.Create(Code, tracking, raw, at), Code, tracking, status, raw, at, receivedAt,
            Location: Json.OptionalString(p, "warehouse"),
            EstimatedDelivery: Json.OptionalDate(p, "expected_delivery_time"),
            Region: Json.OptionalString(p, "to_province"),
            OrderId: Json.OptionalString(p, "client_order_code"));
    }
}

/// <summary>GHTK-style: numeric status ids, timestamps in Vietnam time (+07:00).</summary>
public sealed class GhtkAdapter : ICarrierAdapter
{
    public string Code => "ghtk";

    public static readonly IReadOnlyDictionary<int, ShipmentStatus> StatusMap = new Dictionary<int, ShipmentStatus>
    {
        [1] = ShipmentStatus.Created,
        [3] = ShipmentStatus.PickedUp,
        [4] = ShipmentStatus.InTransit,
        [45] = ShipmentStatus.OutForDelivery,
        [49] = ShipmentStatus.FailedAttempt,
        [5] = ShipmentStatus.Delivered,
        [21] = ShipmentStatus.Returned,
        [-1] = ShipmentStatus.Cancelled
    };

    public NormalizedEvent Parse(JsonElement p, DateTimeOffset receivedAt)
    {
        Json.RequireObject(p);
        var tracking = Json.RequiredTrackingCode(p, "label_id");
        var code = Json.RequiredInt32(p, "status_id");
        if (!StatusMap.TryGetValue(code, out var status))
            throw new CarrierPayloadException($"unknown GHTK status_id {code}");
        var at = Json.RequiredTimestamp(p, "action_time"); // "+07:00" → converted to UTC here
        var raw = code.ToString(CultureInfo.InvariantCulture);

        return new NormalizedEvent(EventIds.Create(Code, tracking, raw, at), Code, tracking, status, raw, at, receivedAt,
            Location: null,
            EstimatedDelivery: Json.OptionalDate(p, "deliver_date"),
            Region: Json.OptionalString(p, "province"),
            OrderId: Json.OptionalString(p, "partner_id"));
    }
}

/// <summary>J&amp;T-style: scan types, Unix epoch milliseconds.</summary>
public sealed class JtAdapter : ICarrierAdapter
{
    public string Code => "jt";

    public static readonly IReadOnlyDictionary<string, ShipmentStatus> StatusMap = new Dictionary<string, ShipmentStatus>
    {
        ["ORDER_CREATED"] = ShipmentStatus.Created,
        ["PICKUP"] = ShipmentStatus.PickedUp,
        ["DEPARTURE"] = ShipmentStatus.InTransit,
        ["ARRIVAL"] = ShipmentStatus.InTransit,
        ["OUT_FOR_DELIVERY"] = ShipmentStatus.OutForDelivery,
        ["PROBLEM"] = ShipmentStatus.FailedAttempt,
        ["SIGNED"] = ShipmentStatus.Delivered,
        ["RETURN"] = ShipmentStatus.Returned,
        ["CANCEL"] = ShipmentStatus.Cancelled
    };

    public NormalizedEvent Parse(JsonElement p, DateTimeOffset receivedAt)
    {
        Json.RequireObject(p);
        var tracking = Json.RequiredTrackingCode(p, "billCode");
        var raw = Json.RequiredString(p, "scanType");
        if (!StatusMap.TryGetValue(raw, out var status))
            throw new CarrierPayloadException($"unknown J&T scanType '{raw}'");
        var epochMs = Json.RequiredInt64(p, "scanTime");   // epoch milliseconds
        var at = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);

        return new NormalizedEvent(EventIds.Create(Code, tracking, raw, at), Code, tracking, status, raw, at, receivedAt,
            Location: Json.OptionalString(p, "scanNetwork"),
            EstimatedDelivery: Json.OptionalDate(p, "expectedDate"),
            Region: Json.OptionalString(p, "receiverProvince"),
            OrderId: Json.OptionalString(p, "orderNo"));
    }
}
