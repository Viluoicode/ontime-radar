// The real API app (routes + filters + handlers) in memory, with fake AWS clients.
// Claims are injected the way the Lambda bridge does it in production (HttpContext.Items), from a test header.
// (Chạy cả ứng dụng API trong bộ nhớ: route thật, phân quyền thật, AWS giả.)
using System.Net;
using System.Text;
using Amazon.Athena;
using Amazon.Athena.Model;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ShipmentSla.Api;
using ShipmentSla.Core;
using Xunit;

namespace ShipmentSla.Service.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Secret = "test-webhook-secret-123";
    public const string GroupsHeader = "X-Test-Groups";   // test-only: becomes the verified "cognito:groups" claim

    public FakeDynamoTable Table { get; } = new();
    public IAmazonSQS Sqs { get; } = Substitute.For<IAmazonSQS>();
    public IAmazonAthena Athena { get; } = Substitute.For<IAmazonAthena>();

    static ApiFactory()
    {
        // Program.cs reads its configuration from environment variables at startup (like on Lambda).
        foreach (var (k, v) in new Dictionary<string, string>
                 {
                     ["QUEUE_URL"] = "https://sqs.local/queue.fifo", ["TABLE_NAME"] = "test-table",
                     ["WEBHOOK_SECRET"] = Secret, ["ATHENA_DATABASE"] = "db", ["ATHENA_WORKGROUP"] = "wg",
                     ["AWS_REGION"] = "ap-southeast-1"
                 })
            Environment.SetEnvironmentVariable(k, v);
    }

    public ApiFactory()
    {
        Sqs.SendMessageAsync(Arg.Any<SendMessageRequest>(), Arg.Any<CancellationToken>()).Returns(new SendMessageResponse());
        Athena.StartQueryExecutionAsync(Arg.Any<StartQueryExecutionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonAthenaException("no Athena in tests"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IAmazonDynamoDB>(Table.Client);
            services.AddSingleton(Sqs);
            services.AddSingleton(Athena);
            services.AddSingleton<IStartupFilter, FakeAuthorizerStartupFilter>();
        });

    /// <summary>Plays API Gateway's JWT authorizer: header present → verified claims on the request.</summary>
    private sealed class FakeAuthorizerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                if (ctx.Request.Headers.TryGetValue(GroupsHeader, out var groups))
                    ctx.Items[AccessFilter.LambdaRequestItem] = AccessFilterTests.GatewayEvent(
                        new() { ["sub"] = "user-1", [AccessRules.GroupsClaim] = groups.ToString() });
                await nextMiddleware();
            });
            next(app);
        };
    }
}

public class ApiEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<HttpStatusCode> Get(string path, string? groups)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (groups is not null) request.Headers.Add(ApiFactory.GroupsHeader, groups);
        using var response = await factory.CreateClient().SendAsync(request);
        return response.StatusCode;
    }

    // ---------- A7: no read route may be left without the role filter ----------

    [Fact]
    public void Every_GET_route_except_health_is_protected_with_the_right_area()
    {
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
            .ToDictionary(e => e.RoutePattern.RawText!, e => e.Metadata.GetMetadata<RequiresAccess>()?.Area);

        Assert.True(routes.Count >= 5, "expected the dashboard routes to be discovered");
        foreach (var (route, area) in routes.Where(r => r.Key != "/health"))
        {
            var expected = route.StartsWith("/sla", StringComparison.Ordinal) ? AccessArea.SlaReports : AccessArea.Tracking;
            Assert.True(area == expected, $"GET {route} must RequireAccess({expected}) but has {area?.ToString() ?? "NO filter"}");
        }
        Assert.Null(routes["/health"]);   // public on purpose (uptime checks)
    }

    // ---------- A1–A4 through the whole pipeline ----------

    [Theory]
    [InlineData("/shipments/ghn/GHN1", null, HttpStatusCode.Unauthorized)]
    [InlineData("/shipments/ghn/GHN1", "[cskh]", HttpStatusCode.NotFound)]          // passed the filter → handler: unknown shipment
    [InlineData("/shipments?days=0", "[cskh]", HttpStatusCode.BadRequest)]         // passed → handler validation
    [InlineData("/sla/summary?days=0", null, HttpStatusCode.Unauthorized)]
    [InlineData("/sla/summary?days=0", "[cskh]", HttpStatusCode.Forbidden)]
    [InlineData("/sla/trend?days=0", "[cskh]", HttpStatusCode.Forbidden)]
    [InlineData("/sla/summary?days=0", "[manager]", HttpStatusCode.BadRequest)]    // passed → handler validation
    [InlineData("/sla/trend?days=0", "[cskh manager]", HttpStatusCode.BadRequest)]
    [InlineData("/health", null, HttpStatusCode.OK)]
    public async Task Role_matrix(string path, string? groups, HttpStatusCode expected) =>
        Assert.Equal(expected, await Get(path, groups));

    [Fact]
    public async Task Forbidden_request_never_reaches_Athena()
    {
        factory.Athena.ClearReceivedCalls();
        Assert.Equal(HttpStatusCode.Forbidden, await Get("/sla/summary?days=30", "[cskh]"));
        await factory.Athena.DidNotReceiveWithAnyArgs().StartQueryExecutionAsync(default!, default);
    }

    // ---------- A8: webhook authentication (shared secret, not Cognito) ----------

    private async Task<HttpStatusCode> PostWebhook(string? secret, string trackingCode = "GHN1")
    {
        var body = $$"""{"order_code":"{{trackingCode}}","status":"picked","updated_date":"2026-11-01T08:00:00Z"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/carriers/ghn/webhook")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (secret is not null) request.Headers.Add("X-Webhook-Secret", secret);
        using var response = await factory.CreateClient().SendAsync(request);
        return response.StatusCode;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-secret")]
    [InlineData(ApiFactory.Secret + "x")]   // longer than the real one
    public async Task Webhook_without_the_right_secret_is_401_and_nothing_is_queued(string? secret)
    {
        factory.Sqs.ClearReceivedCalls();
        Assert.Equal(HttpStatusCode.Unauthorized, await PostWebhook(secret));
        await factory.Sqs.DidNotReceiveWithAnyArgs().SendMessageAsync(default(SendMessageRequest)!, default);
    }

    [Fact]
    public async Task Webhook_with_the_secret_is_queued_per_shipment_with_eventId_dedup()
    {
        factory.Sqs.ClearReceivedCalls();
        Assert.Equal(HttpStatusCode.Accepted, await PostWebhook(ApiFactory.Secret, "GHN-OK.1"));
        await factory.Sqs.Received(1).SendMessageAsync(
            Arg.Is<SendMessageRequest>(m => m.MessageGroupId == "ghn#GHN-OK.1"
                                            && m.MessageDeduplicationId.Length == 32
                                            && m.QueueUrl == "https://sqs.local/queue.fifo"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Webhook_with_an_invalid_tracking_code_is_400_and_nothing_is_queued()
    {
        factory.Sqs.ClearReceivedCalls();
        Assert.Equal(HttpStatusCode.BadRequest, await PostWebhook(ApiFactory.Secret, "ghn#EVIL"));
        await factory.Sqs.DidNotReceiveWithAnyArgs().SendMessageAsync(default(SendMessageRequest)!, default);
    }
}
