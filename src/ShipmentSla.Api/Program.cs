// Shipment SLA API – ASP.NET Core Minimal API.
// On AWS it runs inside Lambda behind API Gateway (HTTP API); locally `dotnet run` uses Kestrel.
//   POST /carriers/{carrier}/webhook        carrier pushes a status → normalize → SQS FIFO
//   GET  /shipments/{carrier}/{tracking}    current state + full timeline (DynamoDB)
//   GET  /sla/summary?days=30&groupBy=carrier|region   late-delivery rate from the event lake (Athena)
//   GET  /health
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
builder.Services.AddSingleton(sp => new SlaQueryService(
    sp.GetRequiredService<IAmazonAthena>(), Env("ATHENA_DATABASE"), Env("ATHENA_WORKGROUP")));
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
});

app.MapGet("/sla/summary", async (
    SlaQueryService sla, ILogger<Program> log, CancellationToken ct, int days = 30, string groupBy = "carrier") =>
{
    if (days is < 1 or > 365) return Results.BadRequest(new { error = "days must be 1..365" });
    if (!SlaQueryService.TryGetColumn(groupBy, out var column))
        return Results.BadRequest(new { error = "groupBy must be 'carrier' or 'region'" });
    try
    {
        var rows = await sla.SummaryAsync(column, days, ct);
        return Results.Ok(new { days, groupBy = column, rows });
    }
    catch (TimeoutException)
    {
        return Results.StatusCode(504);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "SLA summary failed");
        return Results.StatusCode(500);
    }
});

app.Run();
