// Tests for the reconciliation used by both the in-memory chaos tests and the run against AWS,
// and for the start window that keeps a chaos run away from the Replayer.
// (Kiểm thử phần đối chiếu với đáp án và cửa sổ thời gian tránh lượt Replayer.)
using ShipmentSla.Core;
using ShipmentSla.Core.Simulation;
using Xunit;

namespace ShipmentSla.Core.Tests;

public class ChaosReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 11, 0, 0, TimeSpan.Zero);
    private static readonly ChaosScenario Scenario =
        ChaosScenario.Generate(new ChaosOptions { Shipments = 30, MalformedPercent = 0 }, Now);

    /// <summary>What a perfect store would hold: exactly the ground truth.</summary>
    private static Dictionary<string, ChaosObservation> Perfect() => Scenario.Shipments.ToDictionary(
        s => s.ShipmentId,
        s => new ChaosObservation(
            new ShipmentState(s.Order.Carrier, s.Order.TrackingCode, s.Expected.Status, Now, Now, Version: 1,
                DeliveredAt: s.Expected.DeliveredAt, IsLate: s.Expected.IsLate),
            s.Order.Events.Count));

    [Fact]
    public void Ground_truth_itself_has_no_mismatch() =>
        Assert.Empty(ChaosReconciler.Compare(Scenario, Perfect()));

    [Fact]
    public void Every_kind_of_difference_is_reported()
    {
        var observed = Perfect();
        var delivered = Scenario.Shipments.First(s => s.Expected.DeliveredAt is not null);
        var other = Scenario.Shipments.First(s => s.ShipmentId != delivered.ShipmentId);
        var third = Scenario.Shipments.Last();

        var o = observed[delivered.ShipmentId];
        observed[delivered.ShipmentId] = o with { State = o.State! with { IsLate = !delivered.Expected.IsLate } };  // wrong SLA
        observed[other.ShipmentId] = observed[other.ShipmentId] with { HistoryItems = other.Order.Events.Count + 1 }; // stored twice
        observed.Remove(third.ShipmentId);                                                                       // lost

        var problems = ChaosReconciler.Compare(Scenario, observed);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.ShipmentId == delivered.ShipmentId && p.Problem.StartsWith("expected"));
        Assert.Contains(problems, p => p.ShipmentId == other.ShipmentId && p.Problem.Contains("history items"));
        Assert.Contains(problems, p => p.ShipmentId == third.ShipmentId && p.Problem == "not in the store");
    }

    [Fact]
    public void Wrong_final_status_is_a_mismatch()
    {
        var observed = Perfect();
        var s = Scenario.Shipments[0];
        observed[s.ShipmentId] = observed[s.ShipmentId] with
        {
            State = observed[s.ShipmentId].State! with { Status = ShipmentStatus.InTransit }
        };
        Assert.Single(ChaosReconciler.Compare(Scenario, observed));
    }

    [Theory]
    [InlineData(50, 5)]
    [InlineData(95, 10)]
    [InlineData(100, 10)]
    [InlineData(0, 1)]
    public void Percentile_uses_nearest_rank(double p, double expected) =>
        Assert.Equal(expected, ChaosReconciler.Percentile([10, 1, 9, 2, 8, 3, 7, 4, 6, 5], p));

    [Fact]
    public void Percentile_of_nothing_is_null() => Assert.Null(ChaosReconciler.Percentile([], 95));

    // Replayer at :10/:25/:40/:55; a 6-minute run must start ≥ 2.5 min after it and end ≥ 1 min before the next.
    [Theory]
    [InlineData("11:13:00", 0)]       // 3 min after :10 → go
    [InlineData("11:18:00", 0)]       // latest start: ends 11:24, 1 min before :25
    [InlineData("11:11:00", 90)]      // Replayer still running → wait until 11:12:30
    [InlineData("11:20:00", 450)]     // would overlap :25 → wait until 11:27:30
    [InlineData("11:05:00", 450)]     // would overlap :10 → wait until 11:12:30
    [InlineData("00:01:00", 0)]       // across midnight: 6 min after 23:55 → go
    [InlineData("00:09:00", 210)]     // would overlap 00:10 → wait until 00:12:30
    public void Start_window_avoids_the_replayer(string utcTime, int expectedWaitSeconds)
    {
        var now = new DateTimeOffset(new DateTime(2026, 11, 23).Add(TimeSpan.Parse(utcTime)), TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromSeconds(expectedWaitSeconds), ChaosSchedule.WaitBeforeStart(now, 10, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void Late_duplicates_are_the_earliest_accepted_and_wait_past_the_sqs_dedup_window()
    {
        var t = Now;
        var accepted = new Dictionary<string, DateTimeOffset>
        {
            ["c"] = t.AddSeconds(20), ["a"] = t, ["d"] = t.AddSeconds(30), ["b"] = t.AddSeconds(10)
        };

        var (bodies, notBefore) = ChaosSchedule.PickLateDuplicates(accepted, 3);

        Assert.Equal(["a", "b", "c"], bodies);
        // The LAST picked one (accepted at +20 s) must be > 5 min old: +20 s + 5 min + 30 s margin.
        Assert.Equal(t.AddSeconds(20) + TimeSpan.FromMinutes(5.5), notBefore);
    }

    [Fact]
    public void Pacing_keeps_a_steady_rate_when_on_time()
    {
        // 8 req/s: request 16 is due at 2 s; being 0.5 s early or late (≤ 1 s) keeps the schedule.
        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.Zero), ChaosSchedule.NextSend(16, 8, TimeSpan.Zero, TimeSpan.FromSeconds(1.5)));
        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.Zero), ChaosSchedule.NextSend(16, 8, TimeSpan.Zero, TimeSpan.FromSeconds(2.5)));
    }

    [Fact]
    public void Pacing_after_a_stall_moves_the_schedule_instead_of_catching_up()
    {
        // Stalled 5 minutes at request 100 (due 12.5 s): send it now and keep 8 req/s from here on.
        var elapsed = TimeSpan.FromSeconds(312.5);
        var (due, shift) = ChaosSchedule.NextSend(100, 8, TimeSpan.Zero, elapsed);
        Assert.Equal(elapsed, due);
        Assert.Equal(TimeSpan.FromSeconds(300), shift);

        // The next request is 1/8 s later – not immediately (that was the burst that hit the 429s).
        Assert.Equal((elapsed + TimeSpan.FromSeconds(0.125), shift), ChaosSchedule.NextSend(101, 8, shift, elapsed));
    }

    [Fact]
    public void No_late_duplicates_means_nothing_to_wait_for()
    {
        var (bodies, _) = ChaosSchedule.PickLateDuplicates(new Dictionary<string, DateTimeOffset> { ["a"] = Now }, 0);
        Assert.Empty(bodies);
    }

    [Fact] // the default run with late duplicates (~6 min send + ~1 min) still fits between Replayer runs
    public void A_seven_minute_run_fits_after_the_replayer() =>
        Assert.Equal(TimeSpan.Zero,
            ChaosSchedule.WaitBeforeStart(new DateTimeOffset(2026, 11, 24, 2, 13, 0, TimeSpan.Zero), 10, TimeSpan.FromMinutes(7)));

    [Fact]
    public void A_run_longer_than_the_gap_between_replayer_runs_cannot_start() =>
        Assert.Null(ChaosSchedule.WaitBeforeStart(Now, 10, TimeSpan.FromMinutes(12)));
}
