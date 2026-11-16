// "Fake carriers": turn one replayed Olist order step into the webhook JSON that carrier would send.
// It is the exact inverse of the adapters, which lets tests check simulate → parse round-trips.
// (Giả lập webhook của hãng từ dữ liệu Olist thật.)
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShipmentSla.Core.Carriers;

namespace ShipmentSla.Core.Simulation;

/// <summary>One line of the prepared dataset (tools/prepare_olist.py → chunks/*.jsonl).</summary>
public sealed record ReplayOrder(
    string OrderId,
    string Carrier,
    string TrackingCode,
    string? CustomerState,
    string? SellerState,
    DateOnly? EstimatedDelivery,
    IReadOnlyList<ReplayStep> Events);

public sealed record ReplayStep(ShipmentStatus Status, DateTimeOffset At);

public static class CarrierSimulator
{
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    /// <summary>Build the raw webhook body (JSON string) for one step of one order.</summary>
    public static string BuildPayload(ReplayOrder order, ReplayStep step)
    {
        var estimate = order.EstimatedDelivery?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        JsonObject body = order.Carrier.ToLowerInvariant() switch
        {
            "ghn" => new JsonObject
            {
                ["order_code"] = order.TrackingCode,
                ["status"] = RawCode(GhnAdapter.StatusMap, step.Status),
                ["updated_date"] = step.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["warehouse"] = order.SellerState,
                ["expected_delivery_time"] = estimate,
                ["to_province"] = order.CustomerState,
                ["client_order_code"] = order.OrderId
            },
            "ghtk" => new JsonObject
            {
                ["label_id"] = order.TrackingCode,
                ["status_id"] = RawCode(GhtkAdapter.StatusMap, step.Status),
                // GHTK-style sends local Vietnam time with offset – the adapter must convert back to UTC.
                ["action_time"] = step.At.ToOffset(VietnamOffset).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["deliver_date"] = estimate,
                ["province"] = order.CustomerState,
                ["partner_id"] = order.OrderId
            },
            "jt" => new JsonObject
            {
                ["billCode"] = order.TrackingCode,
                ["scanType"] = RawCode(JtAdapter.StatusMap, step.Status),
                ["scanTime"] = step.At.ToUnixTimeMilliseconds(),
                ["scanNetwork"] = order.SellerState,
                ["expectedDate"] = estimate,
                ["receiverProvince"] = order.CustomerState,
                ["orderNo"] = order.OrderId
            },
            _ => throw new ArgumentException($"unknown carrier '{order.Carrier}'")
        };
        return body.ToJsonString();
    }

    /// <summary>First raw code that maps to the wanted status (inverse lookup of the adapter map).</summary>
    private static TKey RawCode<TKey>(IReadOnlyDictionary<TKey, ShipmentStatus> map, ShipmentStatus status) =>
        map.First(kv => kv.Value == status).Key;

    /// <summary>Parse one JSONL line of the prepared dataset.</summary>
    public static ReplayOrder ParseLine(string line) =>
        JsonSerializer.Deserialize<ReplayOrder>(line, ShipmentJson.Options)
        ?? throw new FormatException("empty dataset line");
}
