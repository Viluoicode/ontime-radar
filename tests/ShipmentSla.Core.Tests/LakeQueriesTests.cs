// Tests for the Athena SQL builders: input validation (no SQL injection) and filters.
// (Kiểm thử sinh SQL cho dashboard: chặn tham số độc hại, lọc đúng điều kiện.)
using ShipmentSla.Core;
using ShipmentSla.Core.Analytics;

namespace ShipmentSla.Core.Tests;

public class LakeQueriesTests
{
    private static readonly DateOnly Today = new(2026, 11, 16);

    // ---------------- validation ----------------

    [Fact]
    public void Default_shipment_query_is_valid() =>
        Assert.Null(LakeQueries.Validate(new ShipmentQuery()));

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void Days_out_of_range_is_refused(int days) =>
        Assert.NotNull(LakeQueries.Validate(new ShipmentQuery(Days: days)));

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Limit_out_of_range_is_refused(int limit) =>
        Assert.NotNull(LakeQueries.Validate(new ShipmentQuery(Limit: limit)));

    [Theory]
    [InlineData("SP' OR '1'='1")]
    [InlineData("SP;DROP")]
    [InlineData("S")]
    [InlineData("SP--")]
    public void Region_injection_is_refused(string region) =>
        Assert.NotNull(LakeQueries.Validate(new ShipmentQuery(Region: region)));

    [Theory]
    [InlineData("ghn")]
    [InlineData("GHTK")]
    [InlineData("jt")]
    public void Known_carriers_are_accepted(string carrier) =>
        Assert.Null(LakeQueries.ValidateCarrier(carrier));

    [Theory]
    [InlineData("vtp")]
    [InlineData("ghn' OR 1=1 --")]
    public void Unknown_carrier_is_refused(string carrier) =>
        Assert.NotNull(LakeQueries.ValidateCarrier(carrier));

    [Fact]
    public void Late_and_overdue_together_is_refused() =>
        Assert.NotNull(LakeQueries.Validate(new ShipmentQuery(Late: true, Overdue: true)));

    [Theory]
    [InlineData("delivered", ShipmentStatus.Delivered)]
    [InlineData("FailedAttempt", ShipmentStatus.FailedAttempt)]
    public void Status_names_parse_case_insensitively(string value, ShipmentStatus expected)
    {
        Assert.True(LakeQueries.TryParseStatus(value, out var s));
        Assert.Equal(expected, s);
    }

    [Theory]
    [InlineData("5")]          // Enum.TryParse would accept numbers – we must not
    [InlineData("Lost")]
    [InlineData("Delivered'")]
    public void Unknown_status_is_refused(string value) =>
        Assert.False(LakeQueries.TryParseStatus(value, out _));

    [Fact]
    public void Group_column_is_whitelisted()
    {
        Assert.True(LakeQueries.TryGetGroupColumn("Region", out var col));
        Assert.Equal("region", col);
        Assert.False(LakeQueries.TryGetGroupColumn("status", out _));
    }

    // ---------------- generated SQL ----------------

    [Fact]
    public void Queries_prune_partitions_and_exclude_test_data()
    {
        foreach (var sql in new[]
                 {
                     LakeQueries.SlaSummary("carrier", 30, Today),
                     LakeQueries.SlaTrend(30, null, Today),
                     LakeQueries.Shipments(new ShipmentQuery(), Today)
                 })
        {
            Assert.Contains("dt >= '2026-10-17'", sql);
            Assert.Contains("NOT starts_with(trackingcode, 'DEMO')", sql);
        }
    }

    [Fact]
    public void Shipment_filters_are_applied()
    {
        var sql = LakeQueries.Shipments(
            new ShipmentQuery(Days: 7, Limit: 20, Status: ShipmentStatus.FailedAttempt, Carrier: "GHN", Region: "sp"), Today);

        Assert.Contains("currentstatus = 'FailedAttempt'", sql);
        Assert.Contains("carrier = 'ghn'", sql);
        Assert.Contains("region = 'SP'", sql);
        Assert.Contains("LIMIT 20", sql);
        Assert.DoesNotContain("islate = true", sql);
    }

    [Fact]
    public void Late_filter_only_selects_late_deliveries()
    {
        var sql = LakeQueries.Shipments(new ShipmentQuery(Late: true), Today);
        Assert.Contains("islate = true", sql);
    }

    [Fact]
    public void Overdue_filter_only_selects_open_shipments_past_promise()
    {
        var sql = LakeQueries.Shipments(new ShipmentQuery(Overdue: true), Today);
        Assert.Contains("NOT IN ('Delivered', 'Returned', 'Cancelled')", sql);
        Assert.Contains("CAST(promised AS date)", sql);
    }

    [Fact]
    public void Trend_filters_by_carrier_only_when_given()
    {
        Assert.Contains("carrier = 'jt'", LakeQueries.SlaTrend(30, "JT", Today));
        Assert.DoesNotContain("carrier = '", LakeQueries.SlaTrend(30, null, Today));
    }
}
