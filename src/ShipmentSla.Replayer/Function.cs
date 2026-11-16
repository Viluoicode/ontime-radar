// Replayer Lambda – triggered by EventBridge Scheduler (e.g. every 15 minutes).
// Each run: read the next N real Olist orders from S3 → for every step, build the carrier-style
// webhook (CarrierSimulator) → POST it to our own API exactly like a real carrier would.
// Progress is stored in DynamoDB (REPLAY/CURSOR) so every run continues where the last stopped.
// (Phát lại đơn hàng thật như sự kiện realtime để hệ thống có traffic thật mỗi ngày.)
using System.Net.Http.Headers;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using ShipmentSla.Core.Simulation;
using ShipmentSla.Infrastructure;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace ShipmentSla.Replayer;

public sealed record ReplayResult(int CursorBefore, int CursorAfter, int Orders, int Sent, int Failed, bool DatasetFinished);

public sealed class Function
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ShipmentStore _store;
    private readonly IAmazonS3 _s3;
    private readonly string _bucket = Env("DATASET_BUCKET");
    private readonly string _prefix = Environment.GetEnvironmentVariable("DATASET_PREFIX") ?? "dataset/chunks/";
    private readonly int _chunkSize = int.Parse(Environment.GetEnvironmentVariable("CHUNK_SIZE") ?? "500");
    private readonly int _ordersPerRun = int.Parse(Environment.GetEnvironmentVariable("ORDERS_PER_RUN") ?? "20");
    private readonly string _apiBaseUrl = Env("API_BASE_URL").TrimEnd('/');
    private readonly string _secret = Env("WEBHOOK_SECRET");

    public Function()
    {
        _store = new ShipmentStore(new AmazonDynamoDBClient(), Env("TABLE_NAME"));
        _s3 = new AmazonS3Client();
    }

    // The scheduler sends an (empty) JSON object; we do not need its content.
    public async Task<ReplayResult> Handler(Dictionary<string, object>? input, ILambdaContext context)
    {
        var cursor = await _store.GetReplayCursorAsync();
        var chunkIndex = cursor / _chunkSize;
        var key = $"{_prefix}{chunkIndex:D5}.jsonl";

        // 1) Load the chunk that contains the cursor (≈500 orders, a few hundred KB).
        List<string> lines;
        try
        {
            using var obj = await _s3.GetObjectAsync(_bucket, key);
            using var reader = new StreamReader(obj.ResponseStream, Encoding.UTF8);
            lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchKey")
        {
            context.Logger.LogWarning($"No chunk {key}: dataset finished at cursor {cursor}.");
            return new ReplayResult(cursor, cursor, 0, 0, 0, DatasetFinished: true);
        }

        // 2) Take the next orders inside this chunk (a run never crosses a chunk boundary).
        var orders = lines.Skip(cursor % _chunkSize).Take(_ordersPerRun).Select(CarrierSimulator.ParseLine).ToList();

        // 3) POST every step in chronological order, like the carrier's system would.
        int sent = 0, failed = 0;
        foreach (var order in orders)
        {
            foreach (var step in order.Events.OrderBy(s => s.At))
            {
                var ok = await PostAsync(order.Carrier, CarrierSimulator.BuildPayload(order, step), context);
                if (ok) sent++; else failed++;
            }
        }

        // 4) Only move forward if the API mostly worked; otherwise retry the same orders next run
        //    (safe: duplicates are detected by eventId).
        var total = sent + failed;
        var advance = failed <= total / 2; // also true for an empty batch → skip to next chunk
        // Move to the next chunk when this one is used up.
        var next = !advance ? cursor
                 : orders.Count < _ordersPerRun ? (chunkIndex + 1) * _chunkSize
                 : cursor + orders.Count;
        if (next != cursor) await _store.SetReplayCursorAsync(next);

        var result = new ReplayResult(cursor, next, orders.Count, sent, failed, DatasetFinished: false);
        context.Logger.LogInformation($"Replay {result}");
        return result;
    }

    private async Task<bool> PostAsync(string carrier, string json, ILambdaContext context)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBaseUrl}/carriers/{carrier}/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
        };
        request.Headers.Add("X-Webhook-Secret", _secret);
        try
        {
            using var response = await Http.SendAsync(request);
            if (response.IsSuccessStatusCode) return true;
            context.Logger.LogWarning($"{carrier} webhook → {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return false;
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"{carrier} webhook failed: {ex.Message}");
            return false;
        }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing environment variable {name}");
}
