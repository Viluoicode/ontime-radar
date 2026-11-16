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

/// <summary>Small helpers so each adapter stays short and gives clear 400 messages.</summary>
internal static class Json
{
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
        var tracking = Json.RequiredString(p, "order_code");
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
        var tracking = Json.RequiredString(p, "label_id");
        if (!p.TryGetProperty("status_id", out var sid) || !sid.TryGetInt32(out var code))
            throw new CarrierPayloadException("missing integer field 'status_id'");
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
        var tracking = Json.RequiredString(p, "billCode");
        var raw = Json.RequiredString(p, "scanType");
        if (!StatusMap.TryGetValue(raw, out var status))
            throw new CarrierPayloadException($"unknown J&T scanType '{raw}'");
        if (!p.TryGetProperty("scanTime", out var st) || !st.TryGetInt64(out var epochMs))
            throw new CarrierPayloadException("missing integer field 'scanTime' (epoch ms)");
        var at = DateTimeOffset.FromUnixTimeMilliseconds(epochMs);

        return new NormalizedEvent(EventIds.Create(Code, tracking, raw, at), Code, tracking, status, raw, at, receivedAt,
            Location: Json.OptionalString(p, "scanNetwork"),
            EstimatedDelivery: Json.OptionalDate(p, "expectedDate"),
            Region: Json.OptionalString(p, "receiverProvince"),
            OrderId: Json.OptionalString(p, "orderNo"));
    }
}
