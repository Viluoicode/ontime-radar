// Tests for the carrier adapters and the simulator (simulate → parse must round-trip).
// (Kiểm thử chuẩn hóa đa hãng: 3 định dạng khác nhau → cùng 1 kết quả.)
using System.Text.Json;
using ShipmentSla.Core;
using ShipmentSla.Core.Carriers;
using ShipmentSla.Core.Simulation;
using Xunit;

namespace ShipmentSla.Core.Tests;

public class CarrierAdapterTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);

    private static NormalizedEvent Parse(string carrier, string json)
    {
        Assert.True(CarrierRegistry.TryGet(carrier, out var adapter));
        using var doc = JsonDocument.Parse(json);
        return adapter.Parse(doc.RootElement, Received);
    }

    [Fact]
    public void Ghn_payload_is_normalized()
    {
        var e = Parse("ghn", """
            {"order_code":"GHN1","status":"delivering","updated_date":"2017-10-05T08:00:00Z",
             "expected_delivery_time":"2017-10-06","to_province":"SP","client_order_code":"o1"}
            """);
        Assert.Equal(ShipmentStatus.OutForDelivery, e.Status);
        Assert.Equal("ghn#GHN1", e.ShipmentId);
        Assert.Equal(new DateOnly(2017, 10, 6), e.EstimatedDelivery);
        Assert.Equal("SP", e.Region);
    }

    [Fact]
    public void Ghtk_vietnam_time_is_converted_to_utc()
    {
        var e = Parse("ghtk", """{"label_id":"S1.A1.1","status_id":5,"action_time":"2017-10-05T15:00:00+07:00"}""");
        Assert.Equal(ShipmentStatus.Delivered, e.Status);
        Assert.Equal(new DateTimeOffset(2017, 10, 5, 8, 0, 0, TimeSpan.Zero), e.OccurredAt);
        Assert.Equal(TimeSpan.Zero, e.OccurredAt.Offset);
    }

    [Fact]
    public void Jt_epoch_milliseconds_are_parsed()
    {
        var e = Parse("jt", """{"billCode":"JT1","scanType":"ARRIVAL","scanTime":1507190400000}""");
        Assert.Equal(ShipmentStatus.InTransit, e.Status);
        Assert.Equal(new DateTimeOffset(2017, 10, 5, 8, 0, 0, TimeSpan.Zero), e.OccurredAt);
    }

    [Theory]
    [InlineData("ghn", """{"order_code":"GHN1","status":"teleported","updated_date":"2017-10-05T08:00:00Z"}""")]
    [InlineData("ghn", """{"order_code":"GHN1","status":"picked","updated_date":"2017-10-05 08:00:00"}""")] // no timezone
    [InlineData("ghtk", """{"label_id":"S1","status_id":999,"action_time":"2017-10-05T15:00:00+07:00"}""")]
    [InlineData("jt", """{"scanType":"SIGNED","scanTime":1507190400000}""")]                                // no billCode
    public void Invalid_payloads_are_rejected(string carrier, string json) =>
        Assert.Throws<CarrierPayloadException>(() => Parse(carrier, json));

    // ---------- tracking code: ^[A-Za-z0-9._-]{1,64}$ in all 3 adapters ----------

    private static string PayloadWithTracking(string carrier, string tracking)
    {
        var code = JsonSerializer.Serialize(tracking);   // proper JSON escaping (quotes, \n, unicode)
        return carrier switch
        {
            "ghn" => $$"""{"order_code":{{code}},"status":"picked","updated_date":"2017-10-05T08:00:00Z"}""",
            "ghtk" => $$"""{"label_id":{{code}},"status_id":3,"action_time":"2017-10-05T15:00:00+07:00"}""",
            "jt" => $$"""{"billCode":{{code}},"scanType":"PICKUP","scanTime":1507190400000}""",
            _ => throw new ArgumentOutOfRangeException(nameof(carrier))
        };
    }

    public static TheoryData<string, string> ValidTrackingCodes()
    {
        var data = new TheoryData<string, string>();
        foreach (var carrier in new[] { "ghn", "ghtk", "jt" })
            foreach (var code in new[] { "GHN1A2B3C4D", "S1.A1.123456", "JT1234567890", "DEMO1120_x-y.z", "a", new string('9', 64) })
                data.Add(carrier, code);
        return data;
    }

    public static TheoryData<string, string> InvalidTrackingCodes()
    {
        var data = new TheoryData<string, string>();
        foreach (var carrier in new[] { "ghn", "ghtk", "jt" })
            foreach (var code in new[]
                     {
                         new string('9', 65),   // too long
                         "GHN 123",             // space
                         "ghn#GHN1",            // '#' is our key separator
                         "../etc/passwd",       // '/' (path-like)
                         "GHN1\n",              // trailing newline ($ vs \z)
                         "ĐƠN1",                // non-ASCII letters
                         "GHN1;DROP",           // ';'
                         "<script>",            // HTML
                     })
                data.Add(carrier, code);
        return data;
    }

    [Theory]
    [MemberData(nameof(ValidTrackingCodes))]
    public void Valid_tracking_codes_are_accepted(string carrier, string code) =>
        Assert.Equal(code, Parse(carrier, PayloadWithTracking(carrier, code)).TrackingCode);

    [Theory]
    [MemberData(nameof(InvalidTrackingCodes))]
    public void Invalid_tracking_codes_are_rejected(string carrier, string code)
    {
        var ex = Assert.Throws<CarrierPayloadException>(() => Parse(carrier, PayloadWithTracking(carrier, code)));
        Assert.Contains("A-Z a-z 0-9 . _ -", ex.Message);
    }

    [Fact]
    public void Tracking_code_rule_matches_spec()
    {
        Assert.True(TrackingCodes.IsValid("A.b_C-9"));
        Assert.True(TrackingCodes.IsValid(new string('x', 64)));
        Assert.False(TrackingCodes.IsValid(""));
        Assert.False(TrackingCodes.IsValid(null));
        Assert.False(TrackingCodes.IsValid(new string('x', 65)));
        Assert.False(TrackingCodes.IsValid("ABC\n"));
    }

    [Fact]
    public void Same_webhook_twice_gives_same_event_id()
    {
        const string json = """{"billCode":"JT1","scanType":"SIGNED","scanTime":1507190400000}""";
        Assert.Equal(Parse("jt", json).EventId, Parse("jt", json).EventId);
    }

    [Theory]
    [InlineData("ghn")]
    [InlineData("ghtk")]
    [InlineData("jt")]
    public void Simulator_round_trips_every_status(string carrier)
    {
        var order = new ReplayOrder("order-1", carrier, "TRK1", "SP", "RJ", new DateOnly(2017, 10, 20), []);
        var at = new DateTimeOffset(2017, 10, 5, 8, 30, 0, TimeSpan.Zero);

        foreach (var status in Enum.GetValues<ShipmentStatus>())
        {
            var e = Parse(carrier, CarrierSimulator.BuildPayload(order, new ReplayStep(status, at)));

            Assert.Equal(status, e.Status);
            Assert.Equal(at, e.OccurredAt);
            Assert.Equal("TRK1", e.TrackingCode);
            Assert.Equal(new DateOnly(2017, 10, 20), e.EstimatedDelivery);
            Assert.Equal("SP", e.Region);
            Assert.Equal("order-1", e.OrderId);
        }
    }

    [Fact]
    public void Dataset_line_is_parsed()
    {
        var order = CarrierSimulator.ParseLine("""
            {"orderId":"o1","carrier":"ghtk","trackingCode":"S1.A1.9","customerState":"SP","sellerState":"MG",
             "estimatedDelivery":"2017-10-18","events":[{"status":"Created","at":"2017-10-02T13:56:33Z"},
             {"status":"PickedUp","at":"2017-10-04T22:28:00Z"}]}
            """);
        Assert.Equal(2, order.Events.Count);
        Assert.Equal(ShipmentStatus.PickedUp, order.Events[1].Status);
        Assert.Equal(new DateOnly(2017, 10, 18), order.EstimatedDelivery);
    }
}
