// Chaos runner: sends a chaos scenario to the DEPLOYED webhook API the way misbehaving carriers would
// (duplicates, shuffled, late, malformed), waits until the queue is empty, reconciles DynamoDB with the ground
// truth and writes a report. Start it with tools/chaos.ps1 (it looks up the stack and passes the secret).
// ALL data is simulated: tracking codes DEMOCX… are left out of the SLA reports (LakeQueries).
// AWS access uses your local AWS profile (read DynamoDB / SQS); webhooks use the shared webhook secret.
// (Gửi kịch bản chaos lên hệ thống thật, chờ xử lý xong, đối chiếu với đáp án và ghi báo cáo.)
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using Amazon.SQS.Model;
using ShipmentSla.Core;
using ShipmentSla.Core.Simulation;
using ShipmentSla.Infrastructure;

Console.OutputEncoding = Encoding.UTF8;
var inv = CultureInfo.InvariantCulture;

// ---------------- 1) Configuration + scenario ----------------
var now = DateTimeOffset.UtcNow;
Args a;
ChaosOptions options;
int targetWebhooks, concurrency, replayerMinute;
double rate;
try
{
    a = Args.Parse(args);
    options = new ChaosOptions
    {
        DupFactor = a.Int("dup-factor", 5),
        LatePercent = a.Double("late-pct", 10),
        MalformedPercent = a.Double("malformed-pct", 1),
        Seed = a.Int("seed", Random.Shared.Next(1, 1_000_000)),
        RunId = a.Str("run-id", now.ToString("MMddHHmm", inv)),
        NoLateDeliveries = !a.Flag("allow-late-deliveries")    // Q8: no late delivery → the late-rate alarm cannot fire
    };
    targetWebhooks = a.Int("webhooks", 2500);                   // Q9
    rate = a.Double("rate", 8);                                 // requests/s; the API allows 10 (burst 20) per route
    concurrency = a.Int("concurrency", 4);
    replayerMinute = a.Int("replayer-minute", 10);
}
catch (ArgumentException ex) { return Usage(ex.Message); }

if (options.Validate() is { } invalid) return Usage(invalid);
if (targetWebhooks < 1) return Usage("webhooks must be ≥ 1");
if (rate is <= 0 or > 10) return Usage("rate must be > 0 and ≤ 10 (API Gateway throttles at 10 req/s)");
if (concurrency is < 1 or > 10) return Usage("concurrency must be 1-10 (the account runs at most 10 Lambdas at once)");
if (replayerMinute is < 0 or > 14) return Usage("replayer-minute must be 0-14");
options = options with { Shipments = ChaosScenario.ShipmentsFor(targetWebhooks, options, now) };
var scenario = ChaosScenario.Generate(options, now);
var estimate = TimeSpan.FromSeconds(scenario.Webhooks.Count / rate * 1.15);   // +15 % for 429 retries

var valid = scenario.Webhooks.Count(w => w.Kind == WebhookKind.Valid);
var heldBackEvents = scenario.Webhooks.Where(w => w.HeldBack).Select(w => (w.ShipmentId, w.Step)).Distinct().Count();
var kinds = scenario.Shipments.GroupBy(s => s.Kind).OrderBy(g => g.Key)
    .Select(g => $"{g.Key} {g.Count()}").ToList();

Console.WriteLine($"Chaos run {options.RunId} · seed {options.Seed} · DỮ LIỆU GIẢ LẬP (mã vận đơn {ChaosScenario.TrackingPrefix}{options.RunId}…)");
Console.WriteLine($"  {scenario.Shipments.Count} shipment ({string.Join(", ", kinds)}) · {scenario.UniqueEvents} event khác nhau");
Console.WriteLine($"  {scenario.Webhooks.Count} webhook = {valid} hợp lệ (~{(double)valid / scenario.UniqueEvents:0.0} bản/event) + {scenario.MalformedWebhooks} hỏng · {heldBackEvents} event gửi trễ");
Console.WriteLine($"  Tốc độ {rate:0.#} req/s, {concurrency} luồng → ước tính {estimate:mm\\:ss}");

if (a.Flag("dry-run"))
{
    Console.WriteLine("--dry-run: không gửi gì.");
    return 0;
}

string api, table, queueUrl, dlqUrl;
try
{
    api = a.Required("api").TrimEnd('/');
    table = a.Required("table");
    queueUrl = a.Required("queue-url");
    dlqUrl = a.Required("dlq-url");
}
catch (ArgumentException ex) { return Usage(ex.Message); }
var secret = Environment.GetEnvironmentVariable("CHAOS_WEBHOOK_SECRET");
if (string.IsNullOrEmpty(secret)) return Usage("environment variable CHAOS_WEBHOOK_SECRET is not set (use tools/chaos.ps1)");
var region = RegionEndpoint.GetBySystemName(a.Str("region", "ap-southeast-1"));
var outDir = a.Str("out", Path.Combine("docs", "evidence"));

// ---------------- 2) Do not share the 10 req/s with the Replayer ----------------
if (!a.Flag("no-wait"))
{
    var wait = ChaosSchedule.WaitBeforeStart(DateTimeOffset.UtcNow, replayerMinute, estimate);
    if (wait is null) return Usage($"a run of {estimate:mm\\:ss} does not fit between two Replayer runs: lower --webhooks or raise --rate");
    if (wait > TimeSpan.Zero)
    {
        Console.WriteLine($"  Chờ tới {DateTimeOffset.UtcNow + wait:HH:mm:ss} UTC (tránh lượt Replayer)…");
        await Task.Delay(wait.Value);
    }
}

using var sqs = new AmazonSQSClient(region);
using var ddb = new AmazonDynamoDBClient(region);
var dlqBefore = await QueueDepth(sqs, dlqUrl);

// ---------------- 3) Send (rate-limited, retries like the Replayer) ----------------
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var codes = new ConcurrentDictionary<int, int>();          // final status per webhook (0 = network error)
var unexpected = new ConcurrentQueue<string>();
var retries = 0;
var dispatched = 0;
var sendStarted = DateTimeOffset.UtcNow;
var clock = Stopwatch.StartNew();

// Held-back events go out only after everything else has been answered.
await SendPhase(scenario.Webhooks.Where(w => !w.HeldBack).ToList());
await SendPhase(scenario.Webhooks.Where(w => w.HeldBack).ToList());
var sendTime = clock.Elapsed;
Console.WriteLine($"  Đã gửi {dispatched} webhook trong {sendTime:mm\\:ss} ({dispatched / sendTime.TotalSeconds:0.0} req/s), gửi lại {retries} lần.");

// ---------------- 4) Wait until the Processor has drained the queue ----------------
Console.WriteLine("  Chờ hàng đợi trống…");
var drained = await WaitForEmptyQueue(sqs, queueUrl, TimeSpan.FromMinutes(10));
var dlqAfter = await QueueDepth(sqs, dlqUrl);

// ---------------- 5) Reconcile with the ground truth ----------------
var store = new ShipmentStore(ddb, table);
var timelines = new Dictionary<string, ShipmentTimeline?>();
foreach (var s in scenario.Shipments) timelines[s.ShipmentId] = await store.GetTimelineAsync(s.ShipmentId);
var mismatches = ChaosReconciler.Compare(scenario, Observations());
if (mismatches.Count > 0)
{
    // DynamoDB Query is eventually consistent: read the differing shipments once more before judging.
    await Task.Delay(TimeSpan.FromSeconds(10));
    foreach (var id in mismatches.Select(m => m.ShipmentId).Distinct()) timelines[id] = await store.GetTimelineAsync(id);
    mismatches = ChaosReconciler.Compare(scenario, Observations());
}

var history = timelines.Values.Where(t => t is not null).SelectMany(t => t!.Events).ToList();
var latencies = history.Where(e => e.ProcessedAt is not null)
    .Select(e => (e.ProcessedAt!.Value - e.ReceivedAt).TotalMilliseconds).ToList();
var outcomes = history.GroupBy(e => e.Outcome).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}");
int Code(int c) => codes.GetValueOrDefault(c);
var otherCodes = codes.Where(kv => kv.Key is not (202 or 400)).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}×{kv.Value}").ToList();

var passed = drained && mismatches.Count == 0 && unexpected.IsEmpty && dlqAfter <= dlqBefore;

// ---------------- 6) Report ----------------
string Ms(double? v) => v is null ? "–" : v.Value.ToString("N0", inv);
var md = new StringBuilder();
md.AppendLine($"# Chaos run {options.RunId} – {sendStarted:yyyy-MM-dd HH:mm} UTC");
md.AppendLine();
md.AppendLine($"> Dữ liệu **giả lập 100 %** (mã vận đơn `{ChaosScenario.TrackingPrefix}{options.RunId}…`, không tính vào báo cáo SLA).");
md.AppendLine($"> Không có đơn giao trễ (chọn để không bật alarm tỉ lệ trễ). Seed `{options.Seed}` → chạy lại được y hệt.");
md.AppendLine();
md.AppendLine($"**Kết luận: {(passed ? "ĐẠT" : "KHÔNG ĐẠT")}**");
md.AppendLine();
md.AppendLine("## Kịch bản");
md.AppendLine();
md.AppendLine("| Mục | Giá trị |");
md.AppendLine("|---|---|");
md.AppendLine($"| Shipment | {scenario.Shipments.Count} ({string.Join(", ", kinds)}) |");
md.AppendLine($"| Event khác nhau | {scenario.UniqueEvents} |");
md.AppendLine($"| Webhook gửi | {scenario.Webhooks.Count} = {valid} hợp lệ (trùng ~{(double)valid / scenario.UniqueEvents:0.0} bản/event) + {scenario.MalformedWebhooks} hỏng |");
md.AppendLine($"| Thứ tự | xáo trong từng shipment; {heldBackEvents} event giữ lại gửi sau cùng |");
md.AppendLine($"| Tốc độ | {rate:0.#} req/s, {concurrency} luồng; gửi xong trong {sendTime:mm\\:ss} |");
md.AppendLine();
md.AppendLine("## Kết quả");
md.AppendLine();
md.AppendLine("| Chỉ số | Thực tế | Kỳ vọng |");
md.AppendLine("|---|---|---|");
md.AppendLine($"| HTTP 202 (nhận) | {Code(202)} | {valid} |");
md.AppendLine($"| HTTP 400 (payload hỏng, chặn ở API) | {Code(400)} | {scenario.MalformedWebhooks} |");
md.AppendLine($"| Mã khác sau khi gửi lại | {(otherCodes.Count == 0 ? "0" : string.Join(", ", otherCodes))} | 0 |");
md.AppendLine($"| Lần gửi lại (429 / 5xx) | {retries} | – |");
md.AppendLine($"| Hàng đợi trống sau khi gửi | {(drained ? "có" : "KHÔNG (hết 10 phút)")} | có |");
md.AppendLine($"| History item trong DynamoDB | {history.Count} | {scenario.UniqueEvents} (1 / event) |");
md.AppendLine($"| Sai lệch so với đáp án (status, deliveredAt, isLate, số history) | {mismatches.Count} | 0 |");
md.AppendLine($"| DLQ trước → sau | {dlqBefore} → {dlqAfter} | không tăng |");
md.AppendLine($"| Quyết định của state machine | {string.Join(", ", outcomes)} | – |");
md.AppendLine($"| Độ trễ receivedAt → processedAt (ms) p50 / p95 / max | {Ms(ChaosReconciler.Percentile(latencies, 50))} / {Ms(ChaosReconciler.Percentile(latencies, 95))} / {Ms(latencies.Count == 0 ? null : latencies.Max())} | – |");
md.AppendLine();
md.AppendLine("Ghi chú: độ trễ tính cho bản đầu tiên của mỗi event, gồm cả thời gian chờ trong hàng đợi (Processor chạy tối đa 2 luồng).");
md.AppendLine("Bản trùng trong 5 phút bị SQS FIFO loại; bản trùng sau đó bị DynamoDB loại (`Duplicate`, không ghi history).");
if (!unexpected.IsEmpty || mismatches.Count > 0)
{
    md.AppendLine();
    md.AppendLine("## Chi tiết lỗi (tối đa 50 dòng)");
    md.AppendLine();
    foreach (var line in unexpected.Take(25)) md.AppendLine($"- HTTP: {line}");
    foreach (var m in mismatches.Take(25)) md.AppendLine($"- {m}");
}

Console.WriteLine();
Console.WriteLine(md.ToString());
Directory.CreateDirectory(outDir);
var stamp = $"{sendStarted:yyyy-MM-dd_HHmm}";
var reportPath = Path.Combine(outDir, $"chaos-{stamp}.md");
var truthPath = Path.Combine(outDir, $"chaos-{stamp}-ground-truth.json");
await File.WriteAllTextAsync(reportPath, md.ToString(), new UTF8Encoding(false));
await File.WriteAllTextAsync(truthPath, JsonSerializer.Serialize(scenario.Shipments.Select(s => new
{
    s.ShipmentId, kind = s.Kind.ToString(), s.Expected.Status, s.Expected.DeliveredAt, s.Expected.IsLate,
    events = s.Order.Events.Count
}), new JsonSerializerOptions(ShipmentJson.Options) { WriteIndented = true }));
Console.WriteLine($"Đã lưu: {reportPath}");
Console.WriteLine($"Đã lưu: {truthPath}");
return passed ? 0 : 1;

// ================================================================================================

Dictionary<string, ChaosObservation> Observations() =>
    timelines.ToDictionary(kv => kv.Key, kv => new ChaosObservation(kv.Value?.State, kv.Value?.Events.Count ?? 0));

async Task SendPhase(List<ChaosWebhook> webhooks)
{
    using var gate = new SemaphoreSlim(concurrency);
    var tasks = new List<Task>();
    foreach (var w in webhooks)
    {
        var due = TimeSpan.FromSeconds(dispatched / rate);   // steady pace across both phases
        if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed);
        await gate.WaitAsync();
        dispatched++;
        if (dispatched % 250 == 0) Console.WriteLine($"    … {dispatched}/{scenario.Webhooks.Count}");
        tasks.Add(Task.Run(async () =>
        {
            try { await Send(w); }
            finally { gate.Release(); }
        }));
    }
    await Task.WhenAll(tasks);
}

async Task Send(ChaosWebhook w)
{
    const int maxAttempts = 5;
    var expected = w.Kind == WebhookKind.Valid ? 202 : 400;
    for (var attempt = 1; ; attempt++)
    {
        int code;
        TimeSpan? retryAfter = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{api}/carriers/{w.Carrier}/webhook")
            {
                Content = new StringContent(w.Body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Webhook-Secret", secret);
            using var response = await http.SendAsync(request);
            code = (int)response.StatusCode;
            retryAfter = response.Headers.RetryAfter?.Delta;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            code = 0;
        }

        // Temporary (throttled, Lambda busy, network) → wait and send the SAME body again (same eventId).
        if (code is 0 or 429 or 502 or 503 or 504 && attempt < maxAttempts)
        {
            Interlocked.Increment(ref retries);
            await Task.Delay(retryAfter is { } ra && ra > TimeSpan.Zero
                ? ra
                : TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100)));
            continue;
        }

        codes.AddOrUpdate(code, 1, (_, n) => n + 1);
        if (code != expected)
            unexpected.Enqueue($"{w.Carrier} {w.Defect ?? "hợp lệ"} → {code} (cần {expected}) {w.ShipmentId}");
        return;
    }
}

static async Task<int> QueueDepth(IAmazonSQS sqs, string url)
{
    var names = new List<string> { "ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible", "ApproximateNumberOfMessagesDelayed" };
    var res = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = url, AttributeNames = names });
    return names.Sum(n => res.Attributes.TryGetValue(n, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : 0);
}

// SQS counts are approximate → require two empty readings in a row.
static async Task<bool> WaitForEmptyQueue(IAmazonSQS sqs, string url, TimeSpan timeout)
{
    var deadline = DateTimeOffset.UtcNow + timeout;
    var emptyReadings = 0;
    while (DateTimeOffset.UtcNow < deadline)
    {
        emptyReadings = await QueueDepth(sqs, url) == 0 ? emptyReadings + 1 : 0;
        if (emptyReadings >= 2) return true;
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
    return false;
}

static int Usage(string error)
{
    Console.Error.WriteLine($"Lỗi: {error}");
    Console.Error.WriteLine("""
        Dùng: tools/chaos.ps1 [-Webhooks 2500] [-Seed n] [-DryRun]
          (gọi trực tiếp: dotnet run --project tools/ShipmentSla.Chaos -- --api URL --table T --queue-url U --dlq-url U
           [--webhooks 2500] [--seed n] [--rate 8] [--concurrency 4] [--dup-factor 5] [--late-pct 10]
           [--malformed-pct 1] [--run-id x] [--replayer-minute 10] [--no-wait] [--dry-run] [--out docs/evidence]
           [--allow-late-deliveries]; secret qua biến môi trường CHAOS_WEBHOOK_SECRET)
        """);
    return 2;
}

/// <summary>Minimal "--name value" / "--flag" parser (no extra package for a demo tool).</summary>
internal sealed class Args
{
    private static readonly HashSet<string> Flags = ["dry-run", "no-wait", "allow-late-deliveries"];
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) throw new ArgumentException($"unexpected argument '{args[i]}'");
            var name = args[i][2..];
            if (Flags.Contains(name)) a._flags.Add(name);
            else if (i + 1 < args.Length) a._values[name] = args[++i];
            else throw new ArgumentException($"--{name} needs a value");
        }
        return a;
    }

    public bool Flag(string name) => _flags.Contains(name);
    public string Str(string name, string fallback) => _values.GetValueOrDefault(name, fallback);
    public string Required(string name) =>
        _values.TryGetValue(name, out var v) ? v : throw new ArgumentException($"--{name} is required");
    public int Int(string name, int fallback) =>
        !_values.TryGetValue(name, out var v) ? fallback
        : int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
        : throw new ArgumentException($"--{name} must be an integer");
    public double Double(string name, double fallback) =>
        !_values.TryGetValue(name, out var v) ? fallback
        : double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
        : throw new ArgumentException($"--{name} must be a number");
}
