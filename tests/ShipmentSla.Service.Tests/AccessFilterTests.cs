// The role filter on its own (no web host): 401 without verified claims, 403 for the wrong group.
// (Kiểm thử bộ lọc phân quyền: 401 / 403 / cho qua.)
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using ShipmentSla.Api;
using ShipmentSla.Core;
using Xunit;

namespace ShipmentSla.Service.Tests;

public class AccessFilterTests
{
    private const string Passed = "handler ran";

    /// <summary>The API Gateway event exactly as the Lambda bridge stores it (claims already verified).</summary>
    internal static APIGatewayHttpApiV2ProxyRequest GatewayEvent(Dictionary<string, string> claims) => new()
    {
        RequestContext = new APIGatewayHttpApiV2ProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription
            {
                Jwt = new APIGatewayHttpApiV2ProxyRequest.AuthorizerDescription.JwtDescription { Claims = claims }
            }
        }
    };

    private static async Task<object?> Call(AccessArea area, Dictionary<string, string>? claims)
    {
        var http = new DefaultHttpContext();
        if (claims is not null) http.Items[AccessFilter.LambdaRequestItem] = GatewayEvent(claims);
        return await AccessFilter.Require(area)(
            new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>(Passed));
    }

    private static Dictionary<string, string> Groups(string groupsClaim) =>
        new() { ["sub"] = "user-1", [AccessRules.GroupsClaim] = groupsClaim };

    private static int Status(object? result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    [Theory] // A1 – e.g. a route deployed without the JWT authorizer
    [InlineData(AccessArea.Tracking)]
    [InlineData(AccessArea.SlaReports)]
    public async Task No_verified_claims_is_401(AccessArea area) =>
        Assert.Equal(401, Status(await Call(area, claims: null)));

    [Fact] // A1b – an empty claim set is treated like no token at all
    public async Task Empty_claims_is_401() =>
        Assert.Equal(401, Status(await Call(AccessArea.Tracking, new Dictionary<string, string>())));

    [Fact] // A2
    public async Task Cskh_may_track_shipments() =>
        Assert.Equal(Passed, await Call(AccessArea.Tracking, Groups("[cskh]")));

    [Fact] // A3
    public async Task Cskh_may_not_read_SLA_reports_and_is_told_why()
    {
        var result = await Call(AccessArea.SlaReports, Groups("[cskh]"));

        Assert.Equal(403, Status(result));
        var body = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Contains("SlaReports", body);
        Assert.Contains("cskh", body);
    }

    [Theory] // A4
    [InlineData(AccessArea.Tracking)]
    [InlineData(AccessArea.SlaReports)]
    public async Task Manager_may_do_everything(AccessArea area) =>
        Assert.Equal(Passed, await Call(area, Groups("[manager]")));

    [Theory] // A5 – the formats the groups claim can arrive in
    [InlineData("[cskh manager]")]          // API Gateway HTTP API flattens the JSON array like this
    [InlineData("[\"cskh\",\"manager\"]")]  // raw JSON array
    [InlineData("cskh,manager")]
    [InlineData("MANAGER")]                 // case-insensitive
    public async Task Manager_is_recognised_in_every_claim_format(string claim) =>
        Assert.Equal(Passed, await Call(AccessArea.SlaReports, Groups(claim)));

    [Theory] // A6 – signed in, but in no (known) group
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("[admins]")]
    public async Task Signed_in_without_a_known_group_is_403(string claim) =>
        Assert.Equal(403, Status(await Call(AccessArea.Tracking, Groups(claim))));

    [Fact] // A6b – a token without the groups claim at all
    public async Task Missing_groups_claim_is_403() =>
        Assert.Equal(403, Status(await Call(AccessArea.Tracking, new Dictionary<string, string> { ["sub"] = "user-1" })));
}
