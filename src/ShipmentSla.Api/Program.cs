// Shipment SLA API – ASP.NET Core Minimal API.
// On AWS it runs inside Lambda behind API Gateway (HTTP API); locally `dotnet run` uses Kestrel.
//   POST /carriers/{carrier}/webhook        carrier pushes a status → normalize → SQS FIFO
//   GET  /shipments/{carrier}/{tracking}    current state + full timeline (DynamoDB)
//   GET  /shipments?status=&carrier=&region=&late=&overdue=&hadFailedAttempt=&days=&limit=   list (Athena)
//   GET  /sla/summary?days=30&groupBy=carrier|region   late-delivery rate from the event lake (Athena)
//   GET  /sla/trend?days=30&carrier=                   per delivery day: volume, late %, avg delivery days
//   GET  /health
// Auth: webhook = shared secret; /health = public; all other GETs = Cognito login
//       (CSKH: /shipments*, Quản lý: everything) – see AccessRules.
// (API nhận webhook, chuẩn hóa rồi đẩy vào hàng đợi; xử lý nặng nằm ở Processor.)
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Athena;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using Amazon.SQS.Model;
using ShipmentSla.Api;
using ShipmentSla.Core;
using ShipmentSla.Core.Analytics;
using ShipmentSla.Core.Carriers;
using ShipmentSla.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Lambda hosting: no-op when not running in Lambda, so local runs keep working.
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);

// Configuration comes from environment variables (set by template.yaml on AWS).
string Env(string name) => Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException($"Missing environment variable {name}");
var queueUrl = Env("QUEUE_URL");
var tableName = Env("TABLE_NAME");
var webhookSecret = Env("WEBHOOK_SECRET");

builder.Services.AddSingleton<IAmazonAthena>(_ => new AmazonAthenaClient());
// Cache of Athena results (see LakeQueryService). Size = number of distinct queries kept; the shipment list
// has many filter combinations, so the cap keeps memory bounded on a 512 MB Lambda.
builder.Services.AddMemoryCache(o => o.SizeLimit = 200);
builder.Services.AddSingleton(sp => new LakeQueryService(
    sp.GetRequiredService<IAmazonAthena>(), Env("ATHENA_DATABASE"), Env("ATHENA_WORKGROUP"),
    sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
    sp.GetRequiredService<ILogger<LakeQueryService>>()));
builder.Services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient());
builder.Services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
builder.Services.AddSingleton(sp => new ShipmentStore(sp.GetRequiredService<IAmazonDynamoDB>(), tableName));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", carriers = CarrierRegistry.Codes }));

app.MapPost("/carriers/{carrier}/webhook", async (
    string carrier, HttpRequest request, IAmazonSQS sqs, ILogger<Program> log, CancellationToken ct) =>
{
    // 1) Authenticate the carrier with a shared secret header (constant-time compare).
    var given = request.Headers["X-Webhook-Secret"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(webhookSecret)))
        return Results.Unauthorized();

    // 2) Pick the adapter for this carrier.
    if (!CarrierRegistry.TryGet(carrier, out var adapter))
        return Results.NotFound(new { error = $"unknown carrier '{carrier}'", supported = CarrierRegistry.Codes });

    // 3) Parse + normalize. Bad payload → 400 so the carrier knows not to retry.
    NormalizedEvent evt;
    try
    {
        using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
        evt = adapter.Parse(doc.RootElement, DateTimeOffset.UtcNow);
    }
    catch (Exception ex) when (ex is JsonException or CarrierPayloadException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    // 4) Enqueue. FIFO group = shipment → events of ONE shipment are processed in order,
    //    different shipments still run in parallel. Dedup id = eventId (5-minute SQS window;
    //    DynamoDB dedups forever).
    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl = queueUrl,
        MessageBody = JsonSerializer.Serialize(evt, ShipmentJson.Options),
        MessageGroupId = evt.ShipmentId,
        MessageDeduplicationId = evt.EventId
    }, ct);

    log.LogInformation("Accepted {EventId} {ShipmentId} {Status}", evt.EventId, evt.ShipmentId, evt.Status);

    // 202 = accepted for asynchronous processing.
    return Results.Accepted(value: new { evt.EventId, evt.ShipmentId, status = evt.Status.ToString() });
});

app.MapGet("/shipments/{carrier}/{trackingCode}", async (
    string carrier, string trackingCode, ShipmentStore store, CancellationToken ct) =>
{
    var timeline = await store.GetTimelineAsync(ShipmentKeys.Build(carrier.ToLowerInvariant(), trackingCode), ct);
    return timeline is null ? Results.NotFound() : Results.Ok(timeline);
}).AddEndpointFilter(Require(AccessArea.Tracking));

// ---------------- Event-lake (Athena) endpoints for the dashboard ----------------
// All inputs are validated in LakeQueries before any SQL is built (no SQL injection).

app.MapGet("/shipments", async (
    LakeQueryService lake, ILogger<Program> log, CancellationToken ct,
    int days = 30, int limit = 50, string? status = null, string? carrier = null, string? region = null,
    bool late = false, bool overdue = false, bool hadFailedAttempt = false) =>
{
    ShipmentStatus? parsedStatus = null;
    if (status is not null)
    {
        if (!LakeQueries.TryParseStatus(status, out var s))
            return Results.BadRequest(new { error = $"status must be one of: {string.Join(", ", Enum.GetNames<ShipmentStatus>())}" });
        parsedStatus = s;
    }
    var query = new ShipmentQuery(days, limit, parsedStatus, carrier, region, late, overdue, hadFailedAttempt);
    if (LakeQueries.Validate(query) is { } error) return Results.BadRequest(new { error });

    return await Lake(log, "shipment list", async () => new { query, rows = await lake.ShipmentsAsync(query, ct) });
}).AddEndpointFilter(Require(AccessArea.Tracking));

app.MapGet("/sla/summary", async (
    LakeQueryService lake, ILogger<Program> log, CancellationToken ct, int days = 30, string groupBy = "carrier") =>
{
    if (LakeQueries.ValidateDays(days) is { } error) return Results.BadRequest(new { error });
    if (!LakeQueries.TryGetGroupColumn(groupBy, out var column))
        return Results.BadRequest(new { error = "groupBy must be 'carrier' or 'region'" });

    return await Lake(log, "SLA summary", async () => new { days, groupBy = column, rows = await lake.SummaryAsync(column, days, ct) });
}).AddEndpointFilter(Require(AccessArea.SlaReports));

app.MapGet("/sla/trend", async (
    LakeQueryService lake, ILogger<Program> log, CancellationToken ct, int days = 30, string? carrier = null) =>
{
    if ((LakeQueries.ValidateDays(days) ?? LakeQueries.ValidateCarrier(carrier)) is { } error)
        return Results.BadRequest(new { error });

    return await Lake(log, "SLA trend", async () => new { days, carrier, rows = await lake.TrendAsync(days, carrier, ct) });
}).AddEndpointFilter(Require(AccessArea.SlaReports));

app.Run();

// ---------------- Role check (second layer after API Gateway's JWT authorizer) ----------------
// API Gateway already rejects missing/invalid/expired tokens with 401. Here we read the verified
// claims it passes along and allow the call only for the right Cognito group (else 403).
static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(AccessArea area) =>
    async (ctx, next) =>
    {
        var claims = VerifiedClaims(ctx.HttpContext);
        if (claims is null) return Results.Unauthorized();   // e.g. route deployed without the authorizer

        var groups = AccessRules.ParseGroups(claims.TryGetValue(AccessRules.GroupsClaim, out var g) ? g : null);
        return AccessRules.CanAccess(area, groups)
            ? await next(ctx)
            : Results.Json(new { error = $"your account has no access to {area} (groups: [{string.Join(", ", groups)}])" },
                statusCode: StatusCodes.Status403Forbidden);
    };

// Claims verified by the API Gateway JWT authorizer, taken from the original Lambda event.
static IDictionary<string, string>? VerifiedClaims(HttpContext http)
{
    if (http.Items["LambdaRequestObject"] is Amazon.Lambda.APIGatewayEvents.APIGatewayHttpApiV2ProxyRequest req
        && req.RequestContext?.Authorizer?.Jwt?.Claims is { Count: > 0 } jwtClaims)
        return jwtClaims;

    // Fallback: the Lambda ASP.NET Core bridge may also expose them as the request's User.
    return http.User.Identity?.IsAuthenticated == true
        ? http.User.Claims.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.First().Value)
        : null;
}

// Shared error handling for Athena-backed endpoints: timeout → 504, anything else → 500 (details only in logs).
static async Task<IResult> Lake(ILogger log, string what, Func<Task<object>> run)
{
    try
    {
        return Results.Ok(await run());
    }
    catch (TimeoutException)
    {
        return Results.StatusCode(504);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "{What} failed", what);
        return Results.StatusCode(500);
    }
}
