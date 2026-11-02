using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldEvents.Objectives;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H6 review fixes: the objective's own wave cadence driven through a fake host (IHoldCadenceHost, so
    /// no landblock and no spawner), the finale freeze against an audience that rises mid-send, and the knights' success
    /// line. Settings come through the run's resolver seam; broadcasts through WorldEventAnnouncer.BroadcastSink (restored
    /// in a finally). No PropertyManager.
    /// </summary>
    [TestClass]
    public class WorldEventHoldCadenceTests
    {
        private const double T0 = 1_000_000.0;

        private sealed class FakeHost : IHoldCadenceHost
        {
            public bool KnightsLanded { get; set; }
            public int LiveCount { get; set; }
            public double SourceWaveIntervalSeconds { get; set; } = 45;

            /// <summary>The answers SendWave gives in turn; true once they run out.</summary>
            public readonly Queue<bool> Answers = new Queue<bool>();

            /// <summary>Called inside SendWave, as the run's audience re-sample is (before the pick is sized).</summary>
            public Action DuringSend;

            public readonly List<(double Now, bool Initial, string Trigger, int Extra, bool SkipThemeLine)> Sends =
                new List<(double, bool, string, int, bool)>();

            public readonly List<(int Wave, int Target)> After = new List<(int, int)>();

            public bool SendWave(double now, bool initial, string trigger, WorldEventObjectiveWave wave)
            {
                DuringSend?.Invoke();

                // The run's wave step sizes with the objective's theme and then asks for the extra quantity.
                var sized = wave.SizingTheme(new SourceThemeDef());
                Sends.Add((now, initial, trigger, wave.ExtraQuantity(sized), wave.SkipThemeWaveLine));

                return Answers.Count == 0 || Answers.Dequeue();
            }

            public void AfterWave(int waveNumber, int target, HoldWavePlan plan) => After.Add((waveNumber, target));
        }

        private static Func<GoalDef, WorldEventHoldSettings> Resolver(IDictionary<string, string> props)
        {
            var full = (props ?? new Dictionary<string, string>()).ToDictionary(kv => K.PropertyPrefix + kv.Key, kv => kv.Value);

            return g => WorldEventHoldSettingsResolver.Resolve(g, p => full.TryGetValue(p, out var v) ? v : null);
        }

        private static GoalDef Goal(ScaledCount waves, List<string> success = null) => new GoalDef
        {
            Id = "hold_cadence",
            DisplayName = "Hold Cadence",
            Type = "Hold",
            TypeKind = GoalType.Hold,
            HoldSeconds = 3600,
            MvpRule = MvpRule.MostKills.ToString(),
            RuleKind = MvpRule.MostKills,
            Protect = new ProtectDef
            {
                Wcid = 2000001,
                Count = new ScaledCount { Base = 1, PerParticipant = 0, Cap = 3 },
                Health = new ScaledCount { Base = 20000, PerParticipant = 0, Cap = 20000 },
                Radius = 6.0,
                MinSurvivors = 1,
                Noun = "envoy",
                NounPlural = "envoys",
                Waves = waves,
                WaveSize = new ScaledCount { Base = 4, PerParticipant = 0, Cap = 4 },
                Lines = new ProtectLinesDef { Success = success ?? new List<string>() }
            }
        };

        private static readonly Dictionary<string, string> Cadence = new Dictionary<string, string>
        {
            [K.FirstWaveDelaySeconds] = "10",
            [K.BreatherSeconds] = "5",
            [K.WaveIntervalSeconds] = "0",
            [K.FinalWaveBonus] = "0.25",
        };

        private static (HoldObjective Objective, FakeHost Host) Build(GoalDef goal, Func<HoldWaveCounts> waves = null)
        {
            var run = new WorldEventHoldRun(5, Resolver(Cadence));
            var objective = new HoldObjective(goal, () => Array.Empty<uint>(), () => Array.Empty<uint>(), () => T0, null, null, waves, null, run);

            objective.OnStaged(T0);

            var host = new FakeHost();
            objective.AttachCadenceHostForTest(host);

            return (objective, host);
        }

        [TestMethod]
        public void FirstWave_WaitsForTheKnights_AndTheDelay_ThenGoesInitial()
        {
            var (o, host) = Build(Goal(new ScaledCount { Base = 2, PerParticipant = 0, Cap = 2 }));

            o.TickCadenceForTest(T0 + 5);
            Assert.AreEqual(0, host.Sends.Count, "no knights yet, inside the delay");

            host.KnightsLanded = true;
            o.TickCadenceForTest(T0 + 9);
            Assert.AreEqual(0, host.Sends.Count, "knights stand but first_wave_delay_seconds (10) has not passed");

            o.TickCadenceForTest(T0 + 10);
            Assert.AreEqual(1, host.Sends.Count);
            Assert.IsTrue(host.Sends[0].Initial);
            Assert.AreEqual("initial", host.Sends[0].Trigger);
            Assert.IsTrue(host.Sends[0].SkipThemeLine, "the Hold wave lines replace the theme's generic one");
            Assert.AreEqual(1, o.WavesStarted);
            Assert.IsFalse(o.LastWaveWasFinal);
            CollectionAssert.AreEqual(new[] { (1, 2) }, host.After);
        }

        [TestMethod]
        public void FirstWave_WithoutKnights_GoesAfterTheDelayPlusTheGrace()
        {
            var (o, host) = Build(Goal(new ScaledCount { Base = 2, PerParticipant = 0, Cap = 2 }));

            o.TickCadenceForTest(T0 + 10 + HoldObjective.PlacementGraceSeconds - 0.5);
            Assert.AreEqual(0, host.Sends.Count);

            o.TickCadenceForTest(T0 + 10 + HoldObjective.PlacementGraceSeconds);
            Assert.AreEqual(1, host.Sends.Count, "a batch that never reports back cannot hold the run without waves");
        }

        [TestMethod]
        public void WaveIntervalZero_FallsBackToTheSourceInterval_AndTheLastWaveIsFinal()
        {
            var (o, host) = Build(Goal(new ScaledCount { Base = 2, PerParticipant = 0, Cap = 2 }));
            host.KnightsLanded = true;
            host.SourceWaveIntervalSeconds = 45;

            o.TickCadenceForTest(T0 + 10);
            Assert.AreEqual(0, host.Sends[0].Extra, "not the final wave: no bonus");

            host.LiveCount = 5;
            o.TickCadenceForTest(T0 + 10 + 44);
            Assert.AreEqual(1, host.Sends.Count, "the field stands and the source's 45 s have not passed");

            o.TickCadenceForTest(T0 + 10 + 45);
            Assert.AreEqual(2, host.Sends.Count, "wave_interval_seconds 0 used the source's interval");
            Assert.AreEqual("interval", host.Sends[1].Trigger);
            Assert.IsFalse(host.Sends[1].Initial);
            Assert.AreEqual(1, host.Sends[1].Extra, "the final wave: ceil(4 * 0.25)");
            Assert.IsTrue(o.LastWaveWasFinal);

            o.TickCadenceForTest(T0 + 1000);
            Assert.AreEqual(2, host.Sends.Count, "no wave after the last counted one");
        }

        [TestMethod]
        public void EmptyPick_RetriesAfterTheBreather_NotEverySecond()
        {
            var (o, host) = Build(Goal(new ScaledCount { Base = 2, PerParticipant = 0, Cap = 2 }));
            host.KnightsLanded = true;
            host.Answers.Enqueue(false);

            o.TickCadenceForTest(T0 + 10);
            Assert.AreEqual(1, host.Sends.Count);
            Assert.AreEqual(0, o.WavesStarted, "nothing placed: the wave did not start");
            Assert.AreEqual(0, host.After.Count, "no lines for a wave that never went out");

            o.TickCadenceForTest(T0 + 11);
            o.TickCadenceForTest(T0 + 14);
            Assert.AreEqual(1, host.Sends.Count, "inside the breather (5 s): no retry");

            o.TickCadenceForTest(T0 + 15);
            Assert.AreEqual(2, host.Sends.Count, "retried once the breather passed");
            Assert.IsTrue(host.Sends[1].Initial, "still the first wave");
            Assert.AreEqual(1, o.WavesStarted);
        }

        [TestMethod]
        public void RisingAudience_DuringTheFinaleSend_NeverAddsASecondFinale()
        {
            var (o, host) = Build(Goal(new ScaledCount { Base = 2, PerParticipant = 1, Cap = 10 }));
            host.KnightsLanded = true;

            Assert.AreEqual(2, o.TargetWaves, "sized for the staged audience");

            o.TickCadenceForTest(T0 + 10);

            // The audience surges inside the very wave step that sends the finale.
            host.DuringSend = () => o.OnAudienceResampled(40);
            host.LiveCount = 0;
            o.TickCadenceForTest(T0 + 10 + 5);

            Assert.AreEqual(2, host.Sends.Count);
            Assert.AreEqual(2, o.TargetWaves, "the finale was the next wave: the target is frozen");
            Assert.IsTrue(o.LastWaveWasFinal);
            Assert.AreEqual(1, host.Sends[1].Extra, "sized as the finale");

            o.TickCadenceForTest(T0 + 1000);
            Assert.AreEqual(2, host.Sends.Count, "no second finale");
        }

        [TestMethod]
        public void OnAudienceResampled_FinaleNext_DoesNotRaise_EarlierStillRaises()
        {
            var goal = Goal(new ScaledCount { Base = 3, PerParticipant = 1, Cap = 10 });

            var started = 2;
            var (o, _) = Build(goal, () => new HoldWaveCounts(started, started, started, 0));

            Assert.AreEqual(3, o.TargetWaves);

            o.OnAudienceResampled(20);
            Assert.AreEqual(3, o.TargetWaves, "Started = target - 1: the finale is next, the target is frozen");

            var (early, _) = Build(goal, () => new HoldWaveCounts(0, 0, 0, 0));
            early.OnAudienceResampled(20);
            Assert.AreEqual(10, early.TargetWaves, "control: with waves still to come the target ratchets up");
        }

        // ---- the knights' success line ------------------------------------------------------------------------------

        private static WorldEvent EventFor(HoldObjective objective, GoalDef goal)
        {
            var source = new SourceThemeDef
            {
                Id = "ambush", DisplayName = "Ambush", Geometry = "edges", GeometryKind = SourceGeometry.Edges, GeometryRadius = 45f,
                GeometryPoints = 3, WaveIntervalSeconds = 45, MaxAlive = 24, WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 },
                RewardRadius = 60f, CompatibleGoals = new List<string> { goal.Id }, StartFlavour = "x", WaveFlavour = "y"
            };

            var family = new FamilyDef
            {
                Id = "emberwrought", DisplayName = "the Emberwrought", HueKey = "ember", BiomeTags = new List<string> { "volcanic" },
                Members = new List<FamilyMember> { new FamilyMember { Wcid = 1002604, Name = "Emberwrought Thrall", Level = 20, Role = 0 } }
            };

            var reward = new RewardDef
            {
                Id = "standard", DisplayName = "Hammer Crate", SuccessCrateWcid = 1002600, ConsolationCrateWcid = 1002601, CacheWcid = 1002602,
                ParticipantsPerCache = 8, ClaimWindowSeconds = 120
            };

            var composition = new WorldEventComposition(source, new[] { family }, BossDef.None, goal, reward,
                new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = 0x016C019E, BiomeTags = new List<string>() },
                new ACE.Entity.Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0), WorldEventAxisStore.Empty);

            var request = new WorldEventRequest
            {
                SourceId = "ambush", FamilyId = "emberwrought", GoalId = goal.Id, AnnounceLeadSeconds = 0,
                MaxDurationSeconds = WorldEvent.DefaultMaxDurationSeconds, MinDurationSeconds = 0,
                AbandonAfterSeconds = WorldEvent.DefaultAbandonAfterSeconds, WipeGraceSeconds = WorldEvent.DefaultWipeGraceSeconds, Invoker = "test"
            };

            return new WorldEvent(9, composition, request, objective, () => new AudienceEstimate(3, 100, 150), () => T0);
        }

        /// <summary>
        /// The success line's world copy is an in-run line: through WorldEvent.AnnounceInRun, so broadcast = local keeps it to
        /// the players near the fight. The run is never staged here (OnStaged would place the knights); the run's own Stage
        /// gives it the goal's lines, and the knight is published by hand.
        /// </summary>
        [TestMethod]
        public void SuccessLine_GoesThroughTheRun_LocalNeverBroadcasts()
        {
            var saved = WorldEventAnnouncer.BroadcastSink;
            var sent = new List<string>();

            try
            {
                WorldEventAnnouncer.BroadcastSink = sent.Add;

                foreach (var local in new[] { false, true })
                {
                    sent.Clear();

                    var goal = Goal(new ScaledCount { Base = 2, PerParticipant = 0, Cap = 2 }, new List<string> { "We made it, friends!" });
                    var props = new Dictionary<string, string>(Cadence) { [K.Broadcast] = local ? "local" : "world" };
                    var run = new WorldEventHoldRun(9, Resolver(props));
                    var objective = new HoldObjective(goal, () => Array.Empty<uint>(), () => Array.Empty<uint>(), () => T0, null, null, null, null, run);
                    var evt = EventFor(objective, goal);

                    objective.AttachEvent(evt);
                    run.Stage(goal, 1, T0);
                    run.PublishKnights(new[] { HoldFixtures.Tagged(run, HoldUnitKind.Knight, name: "Envoy Adarl") });

                    Assert.AreEqual(local, evt.ObjectiveAnnouncesLocally, "sanity: the broadcast row reached the hook");

                    objective.OnFinish(WorldEventOutcome.Success, T0 + 100);

                    if (local)
                        Assert.AreEqual(0, sent.Count, "broadcast = local: the success copy never goes world-wide: " + string.Join(" | ", sent));
                    else
                        Assert.IsTrue(sent.Count == 1 && sent[0].Contains("We made it, friends!"), "broadcast = world: one world copy: " + string.Join(" | ", sent));
                }
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = saved;
            }
        }

        [TestMethod]
        public void SuccessLine_OneOnAPayingSuccess_NoneOnAFailure()
        {
            var saved = WorldEventAnnouncer.BroadcastSink;
            var sent = new List<string>();

            try
            {
                WorldEventAnnouncer.BroadcastSink = sent.Add;

                var goal = Goal(new ScaledCount { Base = 2, PerParticipant = 0, Cap = 2 }, new List<string> { "We made it, friends!" });

                var (won, _) = Build(goal);
                won.Run.PublishKnights(new[] { HoldFixtures.Tagged(won.Run, HoldUnitKind.Knight, name: "Envoy Adarl") });
                won.OnFinish(WorldEventOutcome.Success, T0 + 100);

                Assert.AreEqual(1, sent.Count, string.Join(" | ", sent));
                StringAssert.Contains(sent[0], "Envoy Adarl");
                StringAssert.Contains(sent[0], "We made it, friends!");

                sent.Clear();

                var (lost, _) = Build(goal);
                lost.Run.PublishKnights(new[] { HoldFixtures.Tagged(lost.Run, HoldUnitKind.Knight, name: "Envoy Adarl") });
                lost.OnFinish(WorldEventOutcome.FailedObjectiveLost, T0 + 100);

                Assert.AreEqual(0, sent.Count, "a failed hold says no success line");

                var (empty, _) = Build(goal);
                empty.OnFinish(WorldEventOutcome.Success, T0 + 100);
                Assert.AreEqual(0, sent.Count, "no knight standing: nobody to speak");
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = saved;
            }
        }
    }
}
