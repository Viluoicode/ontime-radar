// Compares what the system stored with the chaos ground truth, plus small helpers for the chaos runner.
// Pure (no AWS): the in-memory tests and the runner against AWS use the SAME comparison.
// (Đối chiếu kết quả với đáp án – dùng chung cho test trong bộ nhớ và lượt chạy trên AWS.)
namespace ShipmentSla.Core.Simulation;

/// <summary>What the store holds for one shipment: current state (null = never seen) + number of history items.</summary>
public sealed record ChaosObservation(ShipmentState? State, int HistoryItems);

public sealed record ChaosMismatch(string ShipmentId, LifecycleKind Kind, string Problem)
{
    public override string ToString() => $"{ShipmentId} ({Kind}): {Problem}";
}

public static class ChaosReconciler
{
    /// <summary>Every difference from the ground truth; an empty list means the run is reconciled.</summary>
    public static IReadOnlyList<ChaosMismatch> Compare(
        ChaosScenario scenario, IReadOnlyDictionary<string, ChaosObservation> observed)
    {
        var problems = new List<ChaosMismatch>();
        foreach (var s in scenario.Shipments)
        {
            if (!observed.TryGetValue(s.ShipmentId, out var o) || o.State is null)
            {
                problems.Add(new(s.ShipmentId, s.Kind, "not in the store"));
                continue;
            }
            var actual = new ChaosExpectation(o.State.Status, o.State.DeliveredAt, o.State.IsLate);
            if (actual != s.Expected)
                problems.Add(new(s.ShipmentId, s.Kind, $"expected {s.Expected}, got {actual}"));
            if (o.HistoryItems != s.Order.Events.Count)
                problems.Add(new(s.ShipmentId, s.Kind,
                    $"{o.HistoryItems} history items, expected {s.Order.Events.Count} (one per distinct event)"));
        }
        return problems;
    }

    /// <summary>Nearest-rank percentile (p in 0–100). Null for an empty list.</summary>
    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return null;
        var rank = (int)Math.Ceiling(p / 100 * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }
}

/// <summary>When a chaos run may start without sharing the webhook route's 10 req/s with the Replayer.</summary>
public static class ChaosSchedule
{
    public static readonly TimeSpan ReplayerPeriod = TimeSpan.FromMinutes(15);
    /// <summary>A Replayer run takes about 1–2 minutes (≈100 webhooks with retries); start after it.</summary>
    public static readonly TimeSpan AfterReplayerStart = TimeSpan.FromMinutes(2.5);
    /// <summary>Stop sending this long before the next Replayer run.</summary>
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(1);

    /// <summary>SQS FIFO drops a repeated MessageDeduplicationId only within 5 minutes of the first send.</summary>
    public static readonly TimeSpan SqsDedupWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DedupMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Late duplicates: the <paramref name="count"/> bodies whose LAST accepted copy is oldest, and the moment from which
    /// all of them are outside the SQS dedup window – resent then, they reach DynamoDB and must come back as Duplicate.
    /// The last copy (not the first) matters: a copy sent after a window expired is delivered and opens a NEW
    /// 5-minute window (measured 24/11: an 18-minute send let 61 copies through, and 9 of 50 resends timed from the
    /// first copy were dropped by SQS).
    /// </summary>
    public static (IReadOnlyList<string> Bodies, DateTimeOffset NotBefore) PickLateDuplicates(
        IReadOnlyDictionary<string, DateTimeOffset> lastAccepted, int count)
    {
        var picked = lastAccepted.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, count)).ToList();
        var notBefore = picked.Count == 0 ? DateTimeOffset.MinValue : picked[^1].Value + SqsDedupWindow + DedupMargin;
        return (picked.Select(kv => kv.Key).ToList(), notBefore);
    }

    /// <summary>Behind schedule by more than this → drop the backlog instead of catching up.</summary>
    public static readonly TimeSpan MaxLag = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Steady pacing: request <paramref name="index"/> is due at index / rate (+ <paramref name="shift"/>).
    /// If the sender fell behind (machine asleep, slow network), the schedule is moved forward instead of sending the
    /// backlog at full speed – catching up overran the 10 req/s throttle on 24/11 (≈ 200 × 429, 6 copies lost).
    /// </summary>
    public static (TimeSpan Due, TimeSpan Shift) NextSend(int index, double rate, TimeSpan shift, TimeSpan elapsed)
    {
        var due = TimeSpan.FromSeconds(index / rate) + shift;
        return elapsed - due > MaxLag ? (elapsed, shift + (elapsed - due)) : (due, shift);
    }

    /// <summary>
    /// How long to wait before starting a run of <paramref name="duration"/>. The Replayer starts at
    /// minute <paramref name="replayerMinute"/> of every 15-minute cycle (e.g. 10 → :10, :25, :40, :55 UTC).
    /// Null = the run cannot fit between two Replayer runs at all.
    /// </summary>
    public static TimeSpan? WaitBeforeStart(DateTimeOffset nowUtc, int replayerMinute, TimeSpan duration)
    {
        if (duration > ReplayerPeriod - AfterReplayerStart - SafetyMargin) return null;

        var sinceReplayer = TimeSpan.FromTicks(
            ((nowUtc.UtcDateTime.TimeOfDay - TimeSpan.FromMinutes(replayerMinute)).Ticks % ReplayerPeriod.Ticks
             + ReplayerPeriod.Ticks) % ReplayerPeriod.Ticks);
        var latestStart = ReplayerPeriod - SafetyMargin - duration;

        if (sinceReplayer >= AfterReplayerStart && sinceReplayer <= latestStart) return TimeSpan.Zero;
        return sinceReplayer < AfterReplayerStart
            ? AfterReplayerStart - sinceReplayer
            : ReplayerPeriod - sinceReplayer + AfterReplayerStart;
    }
}
