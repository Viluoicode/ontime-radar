// Chaos scenario: valid shipment lifecycles, the webhooks a misbehaving carrier would send for them
// (duplicated, shuffled, held back, malformed) and the ground truth the system must end up with.
// Pure + seeded: the same options always give the same scenario → reproducible runs and property tests.
// Used by the tests (in memory) and later by the chaos runner against AWS. ALL of this data is simulated.
// (Kịch bản "hãng gửi lộn xộn" + đáp án đúng để đối chiếu – dữ liệu giả lập 100 %.)
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ShipmentSla.Core.Simulation;

public sealed record ChaosOptions
{
    public int Shipments { get; init; } = 200;
    public int DupFactor { get; init; } = 5;            // each event is sent this many times on average (1 = never repeated)
    public bool Shuffle { get; init; } = true;          // shuffle the arrival order inside every shipment
    public double LatePercent { get; init; } = 10;      // % of events held back and sent after everything else
    public double MalformedPercent { get; init; } = 1;  // extra broken webhooks, as % of the valid ones
    public bool EstimateFirstOnly { get; init; }        // only the first event carries the promised date (gap R2)
    public bool NoLateDeliveries { get; init; }         // every delivery on time → the late-delivery alarm cannot fire (runs on prod)
    public int Seed { get; init; } = 1;                 // content: shipments, timestamps, copies, defects
    public int? ArrivalSeed { get; init; }              // arrival order only (null = derived from Seed)
    public string RunId { get; init; } = "0";           // part of every tracking code → runs never collide

    private static readonly Regex RunIdPattern = new(@"\A[A-Za-z0-9]{1,20}\z", RegexOptions.CultureInvariant);

    /// <summary>Error message, or null when the options are usable.</summary>
    public string? Validate() =>
        Shipments is < 1 or > ChaosScenario.MaxShipments ? $"shipments must be 1-{ChaosScenario.MaxShipments}"
        : DupFactor is < 1 or > 50 ? "dup-factor must be 1-50"
        : !(LatePercent is >= 0 and <= 100) ? "late-pct must be 0-100"
        : !(MalformedPercent is >= 0 and <= 100) ? "malformed-pct must be 0-100"
        : !RunIdPattern.IsMatch(RunId) ? "run id must be 1-20 letters or digits"
        : null;
}

public enum LifecycleKind { OnTime, Late, Returned, Cancelled, InProgress }

/// <summary>What the system must show for one shipment once every webhook has been processed.</summary>
public sealed record ChaosExpectation(ShipmentStatus Status, DateTimeOffset? DeliveredAt, bool? IsLate);

public sealed record ChaosShipment(ReplayOrder Order, LifecycleKind Kind, ChaosExpectation Expected)
{
    public string ShipmentId => ShipmentKeys.Build(Order.Carrier, Order.TrackingCode);
}

public enum WebhookKind { Valid, Malformed }

/// <summary>One HTTP request the carrier sends. <see cref="Step"/> = index into the shipment's events.</summary>
public sealed record ChaosWebhook(
    string Carrier, string ShipmentId, int Step, WebhookKind Kind, string Body,
    bool HeldBack = false, string? Defect = null);

public sealed record ChaosScenario(
    ChaosOptions Options, IReadOnlyList<ChaosShipment> Shipments, IReadOnlyList<ChaosWebhook> Webhooks)
{
    /// <summary>Tracking codes start with "DEMO" → the SLA reports (LakeQueries) already leave them out.</summary>
    public const string TrackingPrefix = "DEMOCX";
    public const int MaxShipments = 99_999;              // 5-digit index keeps tracking codes short
    public static readonly IReadOnlyList<string> Carriers = ["ghn", "ghtk", "jt"];

    private static readonly string[] Regions = ["SP", "RJ", "MG", "RS", "PR", "SC", "BA"];

    // Field names per carrier, used to break payloads on purpose (see Corrupt).
    private static readonly Dictionary<string, (string Tracking, string Status, string Time)> Fields = new()
    {
        ["ghn"] = ("order_code", "status", "updated_date"),
        ["ghtk"] = ("label_id", "status_id", "action_time"),
        ["jt"] = ("billCode", "scanType", "scanTime")
    };

    /// <summary>Distinct events (shipment + step). The store must end up with exactly this many history items.</summary>
    public int UniqueEvents => Shipments.Sum(s => s.Order.Events.Count);

    public int MalformedWebhooks => Webhooks.Count(w => w.Kind == WebhookKind.Malformed);

    /// <summary>
    /// Number of shipments that gives about <paramref name="targetWebhooks"/> requests with these options
    /// (measured on a 200-shipment sample with the same seed: the first N shipments are identical).
    /// </summary>
    public static int ShipmentsFor(int targetWebhooks, ChaosOptions options, DateTimeOffset now)
    {
        const int sample = 200;
        var perShipment = (double)Generate(options with { Shipments = sample }, now).Webhooks.Count / sample;
        return Math.Clamp((int)Math.Round(targetWebhooks / perShipment), 1, MaxShipments);
    }

    /// <param name="now">Every event happens before this moment (lifecycles end 3–14 days ago).</param>
    public static ChaosScenario Generate(ChaosOptions options, DateTimeOffset now)
    {
        if (options.Validate() is { } error) throw new ArgumentException(error, nameof(options));

        var content = new Random(options.Seed);
        var origin = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero)
                     - TimeSpan.FromDays(14);

        var shipments = new List<ChaosShipment>();
        var perShipment = new List<List<ChaosWebhook>>();   // in-order sends per shipment (before shuffling)
        var heldBack = new List<ChaosWebhook>();
        for (var i = 0; i < options.Shipments; i++)
        {
            var shipment = NewShipment(i, options.RunId, options.NoLateDeliveries, content, origin);
            shipments.Add(shipment);

            var sends = new List<ChaosWebhook>();
            for (var step = 0; step < shipment.Order.Events.Count; step++)
            {
                var order = options.EstimateFirstOnly && step > 0 ? shipment.Order with { EstimatedDelivery = null } : shipment.Order;
                var body = CarrierSimulator.BuildPayload(order, shipment.Order.Events[step]);
                // uniform 0 .. 2(N-1) extra copies → N sends on average
                var copies = 1 + content.Next(0, 2 * (options.DupFactor - 1) + 1);
                var late = content.NextDouble() * 100 < options.LatePercent;
                for (var c = 0; c < copies; c++)
                    (late ? heldBack : sends).Add(new ChaosWebhook(
                        shipment.Order.Carrier, shipment.ShipmentId, step, WebhookKind.Valid, body, HeldBack: late));
            }
            perShipment.Add(sends);
        }

        // Malformed webhooks are EXTRA requests (broken copies of real events): the valid event still arrives,
        // so the ground truth does not depend on them.
        var valid = perShipment.Sum(s => s.Count) + heldBack.Count;
        var malformed = (int)Math.Round(valid * options.MalformedPercent / 100, MidpointRounding.AwayFromZero);
        for (var m = 0; m < malformed; m++)
        {
            var index = content.Next(shipments.Count);
            var shipment = shipments[index];
            var step = content.Next(shipment.Order.Events.Count);
            var (defect, body) = Corrupt(CarrierSimulator.BuildPayload(shipment.Order, shipment.Order.Events[step]),
                shipment.Order.Carrier, content);
            perShipment[index].Add(new ChaosWebhook(
                shipment.Order.Carrier, shipment.ShipmentId, step, WebhookKind.Malformed, body, Defect: defect));
        }

        // Arrival order uses its own random source: same content, different order (property tests).
        var arrival = new Random(options.ArrivalSeed ?? unchecked(options.Seed * 31 + 7));
        if (options.Shuffle)
        {
            foreach (var sends in perShipment) ShuffleInPlace(sends, arrival);
            ShuffleInPlace(heldBack, arrival);
        }
        var webhooks = Interleave(perShipment, arrival);
        webhooks.AddRange(heldBack);                          // late events come after everything else

        return new ChaosScenario(options, shipments, webhooks);
    }

    private static ChaosShipment NewShipment(int index, string runId, bool noLate, Random rng, DateTimeOffset origin)
    {
        var carrier = Carriers[rng.Next(Carriers.Count)];
        var roll = rng.Next(100);
        var kind = roll < 55 ? LifecycleKind.OnTime
                 : roll < 75 ? (noLate ? LifecycleKind.OnTime : LifecycleKind.Late)
                 : roll < 83 ? LifecycleKind.Returned
                 : roll < 90 ? LifecycleKind.Cancelled
                 : LifecycleKind.InProgress;

        // Whole minutes and strictly increasing: every carrier format can carry the exact time,
        // and no two events of one shipment share a timestamp (see the known gap on ties in StatusRulesTests).
        var t = origin + TimeSpan.FromMinutes(rng.Next(0, 3 * 24 * 60));
        var steps = new List<ReplayStep> { new(ShipmentStatus.Created, t) };
        void Next(ShipmentStatus status, int minHours, int maxHours)
        {
            t += TimeSpan.FromMinutes(rng.Next(minHours * 60, maxHours * 60 + 1));
            steps.Add(new ReplayStep(status, t));
        }
        void Transit(int scans)
        {
            for (var s = 0; s < scans; s++) Next(ShipmentStatus.InTransit, 4, 24);   // hub scans
        }

        switch (kind)
        {
            case LifecycleKind.Cancelled:
                Next(ShipmentStatus.Cancelled, 1, 4);
                break;
            case LifecycleKind.InProgress:
                Next(ShipmentStatus.PickedUp, 2, 30);
                Transit(rng.Next(1, 3));
                if (rng.Next(2) == 0) Next(ShipmentStatus.OutForDelivery, 4, 20);
                break;
            case LifecycleKind.Returned:
                Next(ShipmentStatus.PickedUp, 2, 30);
                Transit(rng.Next(1, 3));
                Next(ShipmentStatus.OutForDelivery, 4, 20);
                Next(ShipmentStatus.FailedAttempt, 2, 8);
                if (rng.Next(2) == 0)
                {
                    Next(ShipmentStatus.OutForDelivery, 10, 20);
                    Next(ShipmentStatus.FailedAttempt, 2, 8);
                }
                Next(ShipmentStatus.Returned, 24, 72);
                break;
            default: // OnTime, Late
                Next(ShipmentStatus.PickedUp, 2, 30);
                Transit(rng.Next(1, 4));
                if (kind == LifecycleKind.Late && rng.Next(2) == 0)
                {
                    Next(ShipmentStatus.OutForDelivery, 6, 20);
                    Next(ShipmentStatus.FailedAttempt, 2, 8);
                }
                Next(ShipmentStatus.OutForDelivery, 4, 20);
                Next(ShipmentStatus.Delivered, 1, 8);
                break;
        }

        // The promise is chosen FROM the intended outcome (not computed with SlaRules), so the ground truth
        // is independent of the code under test. Late = delivered on a later UTC calendar day than promised.
        DateTimeOffset? deliveredAt = kind is LifecycleKind.OnTime or LifecycleKind.Late ? t : null;
        var deliveredDay = DateOnly.FromDateTime(t.UtcDateTime);
        var estimate = kind switch
        {
            LifecycleKind.OnTime => deliveredDay.AddDays(rng.Next(0, 4)),
            LifecycleKind.Late => deliveredDay.AddDays(-rng.Next(1, 4)),
            _ => DateOnly.FromDateTime(steps[0].At.UtcDateTime).AddDays(rng.Next(3, 8))
        };
        bool? isLate = deliveredAt is null ? null : kind == LifecycleKind.Late;

        var order = new ReplayOrder(
            OrderId: $"chaos-{runId}-{index}",
            Carrier: carrier,
            TrackingCode: $"{TrackingPrefix}{runId}{index:D5}",
            CustomerState: Regions[rng.Next(Regions.Length)],
            SellerState: Regions[rng.Next(Regions.Length)],
            EstimatedDelivery: estimate,
            Events: steps);
        return new ChaosShipment(order, kind, new ChaosExpectation(steps[^1].Status, deliveredAt, isLate));
    }

    /// <summary>Break a valid payload in one of the ways real integrations break. Every one must be a 400.</summary>
    private static (string Defect, string Body) Corrupt(string validBody, string carrier, Random rng)
    {
        var (tracking, status, time) = Fields[carrier];
        var json = JsonNode.Parse(validBody)!.AsObject();
        switch (rng.Next(7))
        {
            case 0:
                json.Remove(tracking);
                return ("missing tracking code", json.ToJsonString());
            case 1:
                json[tracking] = "BAD CODE#1";
                return ("invalid tracking code", json.ToJsonString());
            case 2:
                if (carrier == "ghtk") json[status] = 999; else json[status] = "TELEPORTED";
                return ("unknown status", json.ToJsonString());
            case 3:
                json[time] = "2026-11-20T10:00:00";               // no timezone (and a string for J&T's epoch)
                return ("timestamp without timezone", json.ToJsonString());
            case 4:
                if (carrier == "ghtk") json[status] = "45"; else json[status] = 45;   // right value, wrong JSON type
                return ("status with the wrong JSON type", json.ToJsonString());
            case 5:
                return ("not a JSON object", $"[{validBody}]");
            default:
                return ("truncated JSON", validBody[..(validBody.Length / 2)]);
        }
    }

    private static void ShuffleInPlace<T>(List<T> list, Random rng)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Random merge of the per-shipment lists that keeps each shipment's own order.</summary>
    private static List<ChaosWebhook> Interleave(List<List<ChaosWebhook>> perShipment, Random rng)
    {
        var queues = perShipment.Where(s => s.Count > 0).Select(s => new Queue<ChaosWebhook>(s)).ToList();
        var result = new List<ChaosWebhook>(perShipment.Sum(s => s.Count));
        while (queues.Count > 0)
        {
            var i = rng.Next(queues.Count);
            result.Add(queues[i].Dequeue());
            if (queues[i].Count == 0)
            {
                queues[i] = queues[^1];
                queues.RemoveAt(queues.Count - 1);
            }
        }
        return result;
    }
}
