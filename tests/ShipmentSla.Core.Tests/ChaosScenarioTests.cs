// Tests for the chaos generator itself: reproducible, every valid webhook parses back to its event,
// every broken one is refused as bad input, and the ground truth agrees with a legal lifecycle.
// (Kiểm thử bộ sinh kịch bản "hãng gửi lộn xộn" – dữ liệu giả lập.)
using System.Text.Json;
using ShipmentSla.Core;
using ShipmentSla.Core.Carriers;
using ShipmentSla.Core.Simulation;
using Xunit;

namespace ShipmentSla.Core.Tests;

public class ChaosScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 9, 30, 45, TimeSpan.Zero);

    private static ChaosScenario Generate(ChaosOptions? options = null) =>
        ChaosScenario.Generate(options ?? new ChaosOptions(), Now);

    private static NormalizedEvent Parse(ChaosWebhook w)
    {
        Assert.True(CarrierRegistry.TryGet(w.Carrier, out var adapter));
        using var doc = JsonDocument.Parse(w.Body);
        return adapter.Parse(doc.RootElement, Now);
    }

    [Fact]
    public void Same_options_give_the_same_scenario()
    {
        var a = Generate(new ChaosOptions { Seed = 7 });
        var b = Generate(new ChaosOptions { Seed = 7 });
        Assert.Equal(a.Webhooks, b.Webhooks);
        Assert.NotEqual(a.Webhooks.Select(w => w.Body), Generate(new ChaosOptions { Seed = 8 }).Webhooks.Select(w => w.Body));
    }

    [Fact]
    public void Arrival_seed_changes_only_the_order()
    {
        var a = Generate(new ChaosOptions { Seed = 7, ArrivalSeed = 1 });
        var b = Generate(new ChaosOptions { Seed = 7, ArrivalSeed = 2 });

        Assert.Equal(a.Shipments.Select(s => s.Order.Events), b.Shipments.Select(s => s.Order.Events));
        Assert.Equal(a.Webhooks.Select(w => w.Body).Order(), b.Webhooks.Select(w => w.Body).Order());
        Assert.NotEqual(a.Webhooks.Select(w => w.Body), b.Webhooks.Select(w => w.Body));
    }

    [Fact]
    public void Every_valid_webhook_parses_back_to_its_event()
    {
        var scenario = Generate();
        var byId = scenario.Shipments.ToDictionary(s => s.ShipmentId);

        foreach (var w in scenario.Webhooks.Where(w => w.Kind == WebhookKind.Valid))
        {
            var shipment = byId[w.ShipmentId];
            var step = shipment.Order.Events[w.Step];
            var e = Parse(w);

            Assert.Equal(w.ShipmentId, e.ShipmentId);
            Assert.Equal(step.Status, e.Status);
            Assert.Equal(step.At, e.OccurredAt);
            Assert.Equal(shipment.Order.EstimatedDelivery, e.EstimatedDelivery);
            Assert.True(e.OccurredAt < Now);
        }
    }

    [Fact]
    public void Tracking_codes_are_valid_unique_and_left_out_of_sla_reports()
    {
        var scenario = Generate(new ChaosOptions { RunId = "R1123" });
        Assert.All(scenario.Shipments, s =>
        {
            Assert.True(TrackingCodes.IsValid(s.Order.TrackingCode));
            Assert.StartsWith("DEMO", s.Order.TrackingCode);   // LakeQueries excludes DEMO* from /sla/*
        });
        Assert.Equal(scenario.Shipments.Count, scenario.Shipments.Select(s => s.ShipmentId).Distinct().Count());
    }

    [Fact]
    public void Every_malformed_webhook_is_refused_as_bad_input()
    {
        // Many broken webhooks so that every defect appears for every carrier.
        var scenario = Generate(new ChaosOptions { Shipments = 100, MalformedPercent = 30 });
        var malformed = scenario.Webhooks.Where(w => w.Kind == WebhookKind.Malformed).ToList();
        Assert.Equal(21, malformed.Select(w => (w.Carrier, w.Defect)).Distinct().Count());   // 3 carriers × 7 defects

        // The API turns exactly these two exception types into 400; anything else would be a 500.
        Assert.All(malformed, w =>
        {
            var ex = Record.Exception(() => Parse(w));
            Assert.True(ex is JsonException or CarrierPayloadException,
                $"{w.Carrier} '{w.Defect}' threw {ex?.GetType().Name ?? "nothing"}: {w.Body}");
        });
    }

    [Fact]
    public void Ground_truth_is_the_end_of_a_legal_lifecycle()
    {
        var scenario = Generate();
        foreach (var s in scenario.Shipments)
        {
            // Applied in the order they happened, every event of a generated lifecycle must be accepted.
            ShipmentState? state = null;
            foreach (var step in s.Order.Events)
            {
                var e = new NormalizedEvent("id", s.Order.Carrier, s.Order.TrackingCode, step.Status, "raw",
                    step.At, step.At, EstimatedDelivery: s.Order.EstimatedDelivery);
                Assert.Equal(EventOutcome.Applied, StatusRules.Decide(state, e).Outcome);
                state = StatusRules.Apply(state, e, step.At);
            }

            Assert.Equal(s.Expected.Status, state!.Status);
            Assert.Equal(s.Expected.DeliveredAt, state.DeliveredAt);
            Assert.Equal(s.Expected.IsLate, state.IsLate);
            Assert.True(s.Order.Events.Zip(s.Order.Events.Skip(1)).All(p => p.First.At < p.Second.At), "strictly increasing");
        }
    }

    [Fact]
    public void Default_scenario_covers_every_lifecycle_kind()
    {
        var kinds = Generate().Shipments.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.All(Enum.GetValues<LifecycleKind>(), k => Assert.True(kinds.GetValueOrDefault(k) > 0, $"no {k}"));
        Assert.All(Generate().Shipments.Where(s => s.Kind == LifecycleKind.Late), s => Assert.True(s.Expected.IsLate));
        Assert.All(Generate().Shipments.Where(s => s.Kind == LifecycleKind.OnTime), s => Assert.False(s.Expected.IsLate));
    }

    [Fact]
    public void Counts_follow_the_options()
    {
        var scenario = Generate();   // 200 shipments, 5 copies on average, 10 % late, 1 % malformed
        var valid = scenario.Webhooks.Where(w => w.Kind == WebhookKind.Valid).ToList();
        var copiesPerEvent = (double)valid.Count / scenario.UniqueEvents;
        var lateEvents = valid.Where(w => w.HeldBack).Select(w => (w.ShipmentId, w.Step)).Distinct().Count();

        Assert.Equal(200, scenario.Shipments.Count);
        Assert.InRange(copiesPerEvent, 4.5, 5.5);
        Assert.InRange(100.0 * lateEvents / scenario.UniqueEvents, 6, 14);
        Assert.Equal(Math.Round(valid.Count / 100.0, MidpointRounding.AwayFromZero), scenario.MalformedWebhooks);
        Assert.Equal(scenario.UniqueEvents, valid.Select(w => (w.ShipmentId, w.Step)).Distinct().Count());
    }

    [Fact]
    public void Without_chaos_every_event_is_sent_once_in_order()
    {
        var scenario = Generate(new ChaosOptions { DupFactor = 1, Shuffle = false, LatePercent = 0, MalformedPercent = 0 });

        Assert.Equal(scenario.UniqueEvents, scenario.Webhooks.Count);
        Assert.All(scenario.Webhooks.GroupBy(w => w.ShipmentId),
            g => Assert.Equal(Enumerable.Range(0, g.Count()), g.Select(w => w.Step)));
    }

    [Fact]
    public void Held_back_events_arrive_after_all_the_others()
    {
        var webhooks = Generate(new ChaosOptions { LatePercent = 30 }).Webhooks;
        var firstLate = webhooks.ToList().FindIndex(w => w.HeldBack);

        Assert.True(firstLate > 0);
        Assert.All(webhooks.Skip(firstLate), w => Assert.True(w.HeldBack));
    }

    [Fact]
    public void Estimate_first_only_leaves_the_promise_out_of_later_events()
    {
        var scenario = Generate(new ChaosOptions { Shipments = 20, EstimateFirstOnly = true, MalformedPercent = 0 });
        Assert.All(scenario.Webhooks, w => Assert.Equal(w.Step == 0, Parse(w).EstimatedDelivery is not null));
    }

    [Fact]
    public void No_late_deliveries_turns_every_late_shipment_into_an_on_time_one()
    {
        var normal = Generate();
        var noLate = Generate(new ChaosOptions { NoLateDeliveries = true });

        Assert.Contains(normal.Shipments, s => s.Kind == LifecycleKind.Late);
        Assert.DoesNotContain(noLate.Shipments, s => s.Kind == LifecycleKind.Late);
        Assert.All(noLate.Shipments.Where(s => s.Expected.DeliveredAt is not null), s => Assert.False(s.Expected.IsLate));
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(500)]
    public void Shipments_for_a_webhook_target_lands_close_to_it(int target)
    {
        var options = new ChaosOptions { Seed = 1123, NoLateDeliveries = true };
        var shipments = ChaosScenario.ShipmentsFor(target, options, Now);
        var sent = ChaosScenario.Generate(options with { Shipments = shipments }, Now).Webhooks.Count;
        Assert.InRange(sent, target * 0.95, target * 1.05);
    }

    [Theory]
    [InlineData(0, 5, 10, 1, "x")]
    [InlineData(10, 0, 10, 1, "x")]
    [InlineData(10, 5, 101, 1, "x")]
    [InlineData(10, 5, double.NaN, 1, "x")]
    [InlineData(10, 5, 10, -1, "x")]
    [InlineData(10, 5, 10, 1, "bad id")]
    [InlineData(10, 5, 10, 1, "")]
    public void Invalid_options_are_refused(int shipments, int dup, double late, double malformed, string runId)
    {
        var options = new ChaosOptions
        {
            Shipments = shipments, DupFactor = dup, LatePercent = late, MalformedPercent = malformed, RunId = runId
        };
        Assert.NotNull(options.Validate());
        Assert.Throws<ArgumentException>(() => ChaosScenario.Generate(options, Now));
    }
}
