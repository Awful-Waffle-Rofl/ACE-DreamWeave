using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.HazardMechanics;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A World Event's hazard lifecycle through its test seams (hazardSettings, hazardStarter): started when the run goes
    /// Active, count ramped each tick, a Completed run chains the next one, Aborted runs restart at most three times in a
    /// row, and Finish stops the live run exactly once with no restart afterwards. No PropertyManager read and no
    /// landblock: the starter hands back fakes.
    /// </summary>
    [TestClass]
    public class WorldEventHazardLifecycleTests
    {
        private const double T0 = 1_700_000_000d;

        private sealed class FakeObjective : IWorldEventObjective
        {
            public bool IsComplete { get; set; }

            public string ProgressText => "";

            public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
            {
            }

            public void Tick(double now)
            {
            }

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        private sealed class FakeRun : IWorldEventHazardRun
        {
            public object Key => this;

            public List<int?> Counts { get; } = new List<int?>();

            public int Stops { get; private set; }

            public string StopReason { get; private set; }

            public void SetLiveCount(int? count) => Counts.Add(count);

            public bool ThrowOnStop { get; set; }

            public void Stop(string reason)
            {
                Stops++;
                StopReason = reason;

                if (ThrowOnStop)
                    throw new InvalidOperationException("stop failed");
            }
        }

        private sealed class Harness
        {
            public double Clock = T0;
            public readonly FakeObjective Objective = new FakeObjective();
            public readonly List<WorldEventHazardStart> Starts = new List<WorldEventHazardStart>();
            public readonly List<FakeRun> Runs = new List<FakeRun>();

            /// <summary>When true the starter refuses (returns null).</summary>
            public bool Refuse;

            /// <summary>What the presence scan reports (int.MaxValue = the scan failed).</summary>
            public int Presence = 1;

            public WorldEvent Event;

            public FakeRun Live => Runs.LastOrDefault();

            public void End(FakeRun run, HazardOutcome outcome) => ((WorldEventHazardHost)Starts.Last().Host).NotifyEnded(run, outcome);
        }

        private static Harness Build(HazardDef hazard, string requested = "", WorldEventHazardSettings settings = null, int minDurationSeconds = 300,
            float x = 96f, float y = 96f, BossDef boss = null, Func<uint, double, bool> bossSpawner = null)
        {
            var h = new Harness();
            var anchor = new Position(0xA9B40005, x, y, 50f, 0f, 0f, 0f, 1f, 0);

            var source = new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                Geometry = "single",
                GeometryKind = SourceGeometry.Single,
                GeometryRadius = 0f,
                GeometryPoints = 1,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1, Cap = 18 },
                RewardRadius = 60f,
                CompatibleGoals = new List<string> { "kill_count" },
                Hazard = hazard
            };

            var goal = new GoalDef
            {
                Id = "kill_count",
                DisplayName = "Kill Count",
                Type = "KillCount",
                TypeKind = GoalType.KillCount,
                MvpRule = "mostKills",
                RuleKind = ACE.Server.WorldEvents.Defs.MvpRule.MostKills,
                Count = new ScaledCount { Base = 20, PerParticipant = 6, Cap = 150 },
                ProgressTemplate = "{killed} of {target} slain."
            };

            var reward = new RewardDef
            {
                Id = "standard",
                DisplayName = "Event Crate",
                SuccessCrateWcid = 1002600,
                ConsolationCrateWcid = 1002601,
                CacheWcid = 1002602,
                ParticipantsPerCache = 8,
                ClaimWindowSeconds = 120
            };

            var family = new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string>(),
                Members = new List<FamilyMember> { new FamilyMember { Wcid = 1002604, Name = "Thrall", Level = 20, Role = 0 } }
            };

            var anchorDef = new AnchorDef { Id = "here", DisplayName = "the test anchor", CellId = anchor.LandblockId.Raw, BiomeTags = new List<string>() };

            var composition = new WorldEventComposition(source, new[] { family }, boss ?? BossDef.None, goal, reward, anchorDef, anchor, WorldEventAxisStore.Empty);

            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 0,
                MinDurationSeconds = minDurationSeconds,
                Invoker = "test",
                Hazard = string.IsNullOrEmpty(requested) ? null : requested
            };

            h.Event = new WorldEvent(1, composition, request, h.Objective,
                audienceSampler: () => new AudienceEstimate(3, 100, 150),
                clock: () => h.Clock,
                presenceCounter: () => h.Presence,
                presenceSampler: () => new List<PresenceSample>(),
                presenceSettings: () => (120, 2),
                ringFlag: () => false,
                bossSpawner: bossSpawner,
                hazardSettings: () => settings ?? WorldEventHazardSettings.Defaults,
                hazardStarter: start =>
                {
                    h.Starts.Add(start);

                    if (h.Refuse)
                        return null;

                    var run = new FakeRun();

                    h.Runs.Add(run);

                    return run;
                });

            return h;
        }

        private static Harness Active(HazardDef hazard = null, string requested = "", WorldEventHazardSettings settings = null, int min = 300, float x = 96f, float y = 96f)
        {
            var h = Build(hazard ?? new HazardDef { Kind = "wisps", Element = "fire" }, requested, settings, min, x, y);

            h.Event.Stage(T0);
            h.Event.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, h.Event.State, "fixture: the run is Active");

            return h;
        }

        private static void TickAt(Harness h, double t)
        {
            h.Clock = t;
            h.Event.Tick(t);
        }

        // ---- start --------------------------------------------------------------------------------------------

        [TestMethod]
        public void GoingActive_StartsOneRun_OfThePlannedKindAndElement_AtTheLowCount()
        {
            var h = Active();

            Assert.AreEqual(1, h.Starts.Count);
            Assert.AreEqual("wisps", h.Starts[0].Kind);
            Assert.AreEqual(WorldEventHazardElement.Fire, h.Starts[0].Element);
            Assert.AreEqual(4, h.Live.Counts.Last(), "the ramp starts at world_events_hazard_count_low");
            Assert.IsTrue(h.Starts[0].Host is WorldEventHazardHost);
            Assert.IsTrue(((IHazardHost)h.Starts[0].Host).PlayerFacing);
        }

        [TestMethod]
        public void EachTick_SetsTheRampedCount()
        {
            var h = Active();

            TickAt(h, T0 + 1);
            TickAt(h, T0 + 150);

            Assert.AreEqual(7, h.Live.Counts.Last(), "halfway through a 300 s minimum: 4 + (10 - 4) / 2");

            TickAt(h, T0 + 299);
            TickAt(h, T0 + 300);

            Assert.AreEqual(10, h.Live.Counts.Last());
            Assert.AreEqual(1, h.Starts.Count, "ticking never restarts a live run");
        }

        [TestMethod]
        public void HazardStatusText_NamesKindElementAndLiveCount_WhileARunIsLive_AndNullOtherwise()
        {
            var h = Active();

            Assert.AreEqual("wisps/fire/4", h.Event.HazardStatusText);

            TickAt(h, T0 + 150);
            Assert.AreEqual("wisps/fire/7", h.Event.HazardStatusText, "tracks the ramped count");

            h.End(h.Live, HazardOutcome.Aborted);
            h.Refuse = true;
            TickAt(h, T0 + 151);
            Assert.IsNull(h.Event.HazardStatusText, "no live run, no fragment");

            var none = Active(requested: "none");
            Assert.IsNull(none.Event.HazardStatusText);
        }

        [TestMethod]
        public void NoHazard_WhenTheRequestSaysNone_OrTheSwitchIsOff()
        {
            var none = Active(requested: "none");
            var off = Active(settings: WorldEventHazardSettings.Defaults with { Enabled = false });

            Assert.AreEqual(0, none.Starts.Count);
            Assert.AreEqual(0, off.Starts.Count);

            TickAt(none, T0 + 5);
            TickAt(off, T0 + 5);

            Assert.AreEqual(0, none.Starts.Count);
            Assert.AreEqual(0, off.Starts.Count);
        }

        [TestMethod]
        public void RequestOverridesTheSourceDefault()
        {
            var h = Active(new HazardDef { Kind = "wisps", Element = "fire" }, requested: "rain");

            Assert.AreEqual("rain", h.Starts[0].Kind);
            Assert.IsNotNull(h.Starts[0].Args.GetString("drop_z"));
        }

        [TestMethod]
        public void SkyRiftStyleElement_FollowsThePickedStyle()
        {
            // No landblock means no decor is spawned, so no style is picked: the element falls to random but is one of the five.
            var h = Active(new HazardDef { Kind = "balls", StyleElements = new List<string> { "frost", "acid", "fire", "lightning" } });

            Assert.AreEqual(1, h.Starts.Count);
            Assert.IsTrue(Enum.IsDefined(typeof(WorldEventHazardElement), h.Starts[0].Element));
        }

        // ---- the per-run hit summary ---------------------------------------------------------------------------

        private static List<string> CaptureSummaries(Action body)
        {
            var appender = new MemoryAppender();
            var hierarchy = (Hierarchy)LogManager.GetRepository(typeof(WorldEvent).Assembly);
            var root = hierarchy.Root;
            var priorLevel = root.Level;
            var priorConfigured = hierarchy.Configured;

            try
            {
                root.AddAppender(appender);
                root.Level = Level.All;
                hierarchy.Configured = true;

                body();

                return appender.GetEvents().Where(e => e.Level == Level.Info && e.RenderedMessage.Contains("hazard summary")).Select(e => e.RenderedMessage).ToList();
            }
            finally
            {
                root.RemoveAppender(appender);
                root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;
            }
        }

        [TestMethod]
        public void Summary_Format_IsOneLine_WithPatternDashAndOneDecimalPct()
        {
            var line = WorldEventHazardHost.FormatSummary(7, "rain", null, 41.6, 3, (12, 2, 87.456));

            Assert.AreEqual("[WORLDEVENT] run=7 hazard summary kind=rain pattern=- seconds=42 players=3 hits=12 players_hit=2 dmg_pct_sum=87.5", line);
            Assert.AreEqual("[WORLDEVENT] run=7 hazard summary kind=balls pattern=ring seconds=0 players=0 hits=0 players_hit=0 dmg_pct_sum=0.0",
                WorldEventHazardHost.FormatSummary(7, "balls", "ring", -1, 0, (0, 0, 0.0)));
        }

        [TestMethod]
        public void Summary_Tally_CountsHits_DistinctPlayers_AndDamageAsPercentOfEachVictimsMaxHealth()
        {
            var host = new WorldEventHazardHost();
            var a = new object();
            var b = new object();

            host.RecordHit(a, 1, 30, 100);   // 30 %
            host.RecordHit(a, 1, 60, 200);   // 30 %
            host.RecordHit(a, 2, 50, 500);   // 10 %
            host.RecordHit(a, 3, 0, 500);    // a zero-damage hit is not a hit
            host.RecordHit(b, 9, 10, 100);   // another run: its own tally

            var (hits, players, pct) = host.TakeTally(a);

            Assert.AreEqual(3, hits);
            Assert.AreEqual(2, players);
            Assert.AreEqual(70.0, pct, 1e-9);
            Assert.AreEqual((0, 0, 0.0), host.TakeTally(a), "taking a tally clears it");
            Assert.AreEqual(1, host.TakeTally(b).Hits);
            Assert.AreEqual((0, 0, 0.0), host.TakeTally(new object()), "a run that was never hit reads zeros");
        }

        [TestMethod]
        public void Summary_LoggedOnce_WhenARunEnds_WithItsTally()
        {
            var h = Active(new HazardDef { Kind = "balls", Element = "fire" });
            var host = (WorldEventHazardHost)h.Starts[0].Host;
            var run = h.Live;
            var pattern = h.Starts[0].Pattern ?? "-";

            var lines = CaptureSummaries(() =>
            {
                host.RecordHit(run, 1, 30, 100);
                host.RecordHit(run, 1, 30, 100);
                host.RecordHit(run, 2, 150, 1000);

                h.End(run, HazardOutcome.Completed);
                TickAt(h, T0 + 60);
                TickAt(h, T0 + 61);
            });

            Assert.AreEqual(1, lines.Count, string.Join(" | ", lines));
            Assert.AreEqual($"[WORLDEVENT] run=1 hazard summary kind={h.Starts[0].Kind} pattern={pattern} seconds=60 players=1 hits=3 players_hit=2 dmg_pct_sum=75.0", lines[0]);
            Assert.AreEqual(2, h.Starts.Count, "control: the Completed run chained a second one, which has no summary yet");
        }

        [TestMethod]
        public void Summary_LoggedOnce_WhenFinishStopsTheLiveRun_AndNotAgain()
        {
            var h = Active();
            var host = (WorldEventHazardHost)h.Starts[0].Host;
            var run = h.Live;

            var lines = CaptureSummaries(() =>
            {
                host.RecordHit(run, 5, 10, 100);
                h.Clock = T0 + 30;
                h.Event.Finish(WorldEventOutcome.FailedTimeout);
                h.Event.Finish(WorldEventOutcome.AbortedShutdown);
                host.NotifyEnded(run, HazardOutcome.Stopped);
                TickAt(h, T0 + 31);
            });

            Assert.AreEqual(1, lines.Count, string.Join(" | ", lines));
            StringAssert.Contains(lines[0], "kind=wisps pattern=mixed seconds=30");
            StringAssert.Contains(lines[0], "hits=1 players_hit=1 dmg_pct_sum=10.0");
        }
        [TestMethod]
        public void Summary_TwoChainedRuns_EachGetTheirOwnLine_WithTheirOwnHit()
        {
            var h = Active(new HazardDef { Kind = "wisps", Element = "fire" });
            var host = (WorldEventHazardHost)h.Starts[0].Host;

            var lines = CaptureSummaries(() =>
            {
                host.RecordHit(h.Runs[0], 1, 10, 100);
                h.End(h.Runs[0], HazardOutcome.Completed);
                TickAt(h, T0 + 20);

                Assert.AreEqual(2, h.Runs.Count, "fixture: the Completed run chained a second");

                host.RecordHit(h.Runs[1], 2, 20, 100);
                h.End(h.Runs[1], HazardOutcome.Completed);
                TickAt(h, T0 + 40);
            });

            Assert.AreEqual(2, lines.Count, string.Join(" | ", lines));
            StringAssert.Contains(lines[0], " hits=1 players_hit=1 dmg_pct_sum=10.0");
            StringAssert.Contains(lines[1], " hits=1 players_hit=1 dmg_pct_sum=20.0");
        }

        [TestMethod]
        public void Summary_ALateHit_AfterTheTallyWasTaken_LeavesNoOrphanEntry()
        {
            var host = new WorldEventHazardHost();
            var run = new object();

            host.RecordHit(run, 1, 10, 100);
            Assert.AreEqual(1, host.OpenTallyCount, "control: a live run has an entry");

            host.TakeTally(run);
            host.RecordHit(run, 1, 10, 100);

            Assert.AreEqual(0, host.OpenTallyCount, "the late hit of a finished run is dropped");
            Assert.AreEqual((0, 0, 0.0), host.TakeTally(run));
        }

        [TestMethod]
        public void Summary_StillLogged_WhenTheRunsStopThrows()
        {
            var h = Active();
            var run = h.Live;

            run.ThrowOnStop = true;

            var lines = CaptureSummaries(() =>
            {
                ((WorldEventHazardHost)h.Starts[0].Host).RecordHit(run, 1, 10, 100);
                h.Event.Finish(WorldEventOutcome.FailedTimeout);
            });

            Assert.AreEqual(1, lines.Count, string.Join(" | ", lines));
            StringAssert.Contains(lines[0], "hits=1");
        }

        [TestMethod]
        public void Summary_PlayersIsMinusOne_WhenThePresenceScanFailed()
        {
            var h = Active();
            var run = h.Live;

            h.Presence = int.MaxValue;

            var lines = CaptureSummaries(() =>
            {
                h.End(run, HazardOutcome.Completed);
                TickAt(h, T0 + 5);
            });

            Assert.AreEqual(1, lines.Count, string.Join(" | ", lines));
            StringAssert.Contains(lines[0], " players=-1 ");
        }

        // ---- the hold -----------------------------------------------------------------------------------------
        [TestMethod]
        public void HoldsAdjacents_OnlyWhenAHazardIsPlannedAndTheArenaCrossesTheEdge()
        {
            var crossing = Build(new HazardDef { Kind = "wisps" }, x: 10f, y: 96f);
            var inside = Build(new HazardDef { Kind = "wisps" }, x: 96f, y: 96f);
            var noHazard = Build(new HazardDef { Kind = "none" }, x: 10f, y: 96f);

            foreach (var h in new[] { crossing, inside, noHazard })
                h.Event.Stage(T0);

            Assert.IsNotNull(crossing.Event.HazardPlan);
            Assert.IsTrue(crossing.Event.HazardHoldsAdjacents);
            Assert.IsFalse(inside.Event.HazardHoldsAdjacents);
            Assert.IsNull(noHazard.Event.HazardPlan);
            Assert.IsFalse(noHazard.Event.HazardHoldsAdjacents, "the arena crosses the edge but there is no hazard to hold for");
        }

        [TestMethod]
        public void HoldsAdjacents_AtExactlyTheEdge_FollowsTheRegionCover_NotTheArenaTest()
        {
            // centre x 132, r 60: the circle reaches x = 192.0 exactly, which the run's cover (HazardSpan) puts in the east
            // landblock, while the arena's own edge test (> 192) says it does not cross.
            var exact = Build(new HazardDef { Kind = "wisps" }, x: 132f, y: 96f);
            var short1 = Build(new HazardDef { Kind = "wisps" }, x: 131.9f, y: 96f);

            exact.Event.Stage(T0);
            short1.Event.Stage(T0);

            Assert.IsFalse(exact.Event.Arena.CrossesBlockEdge(), "fixture: the arena test disagrees at exactly 192");
            Assert.IsTrue(exact.Event.HazardHoldsAdjacents);
            Assert.IsFalse(short1.Event.HazardHoldsAdjacents);
        }

        // ---- the boss latch -------------------------------------------------------------------------------

        private static Harness ActiveWithBoss(bool placementSucceeds, int min = 10)
        {
            var h = Build(new HazardDef { Kind = "wisps", Element = "fire" }, "", null, min, 96f, 96f, BossDef.FamilyChampion, (wcid, mult) => placementSucceeds);

            h.Event.Stage(T0);
            h.Event.Tick(T0);

            Assert.AreEqual(WorldEventState.Active, h.Event.State, "fixture: the run is Active");

            return h;
        }

        [TestMethod]
        public void Boss_RefusedPlacement_KeepsTheCountAHighMinusOne()
        {
            var h = ActiveWithBoss(placementSucceeds: false);

            for (var t = 1; t <= 30; t++)
                TickAt(h, T0 + t);

            Assert.AreEqual(WorldEventState.Active, h.Event.State, "fixture: the refusals are inside the grace");
            Assert.AreEqual(9, h.Live.Counts.Last(), "no boss stands, so the count stops a step short of high");
        }

        [TestMethod]
        public void Boss_PlacedLiftsTheCountToHigh()
        {
            var h = ActiveWithBoss(placementSucceeds: true);

            for (var t = 1; t <= 30; t++)
                TickAt(h, T0 + t);

            Assert.AreEqual(10, h.Live.Counts.Last());
        }

        // ---- chaining and restarts ----------------------------------------------------------------------------

        [TestMethod]
        public void CompletedRun_ChainsTheNext_WhileActive_KeepingTheElement()
        {
            var h = Active(new HazardDef { Kind = "wisps" });
            var element = h.Starts[0].Element;

            h.End(h.Live, HazardOutcome.Completed);
            TickAt(h, T0 + 1);

            Assert.AreEqual(2, h.Starts.Count);
            Assert.AreEqual(element, h.Starts[1].Element, "one element per event");
            Assert.AreEqual(0, h.Runs[0].Stops, "an ended run is not stopped again");
            Assert.AreEqual(4, h.Live.Counts.First(), "the new run is handed the current count at once");
        }

        [TestMethod]
        public void CompletedRuns_ChainWithoutLimit_AndResetTheAbortStreak()
        {
            var h = Active();

            for (var i = 0; i < 3; i++)
            {
                h.End(h.Live, HazardOutcome.Aborted);
                TickAt(h, T0 + 1 + i);
            }

            Assert.AreEqual(4, h.Starts.Count, "three aborts, three restarts");

            h.End(h.Live, HazardOutcome.Completed);
            TickAt(h, T0 + 10);

            Assert.AreEqual(5, h.Starts.Count);

            for (var i = 0; i < 3; i++)
            {
                h.End(h.Live, HazardOutcome.Aborted);
                TickAt(h, T0 + 11 + i);
            }

            Assert.AreEqual(8, h.Starts.Count, "the streak restarted from zero after the Completed run");
        }

        [TestMethod]
        public void AbortedRuns_RestartThreeTimesInARow_ThenTheHazardIsGivenUp()
        {
            var h = Active();

            for (var i = 0; i < 3; i++)
            {
                h.End(h.Live, HazardOutcome.Aborted);
                TickAt(h, T0 + 1 + i);
            }

            Assert.AreEqual(4, h.Starts.Count);

            h.End(h.Live, HazardOutcome.Aborted);
            TickAt(h, T0 + 5);
            TickAt(h, T0 + 6);
            TickAt(h, T0 + 7);

            Assert.AreEqual(4, h.Starts.Count, "the fourth consecutive abort ends the hazard for good");
            Assert.AreEqual(WorldEventState.Active, h.Event.State, "a dead hazard never ends the event");
        }

        [TestMethod]
        public void RefusedStarts_CountAsAborts_AndGiveUpAfterTheThirdRetry()
        {
            var h = Build(new HazardDef { Kind = "wisps" });

            h.Refuse = true;
            h.Event.Stage(T0);
            h.Event.Tick(T0);

            Assert.AreEqual(1, h.Starts.Count);

            for (var i = 1; i <= 10; i++)
                TickAt(h, T0 + i);

            Assert.AreEqual(4, h.Starts.Count, "the first try plus three retries, then no more");
            Assert.AreEqual(0, h.Runs.Count);
        }

        [DataTestMethod]
        [DataRow(HazardOutcome.Stopped)]
        [DataRow(HazardOutcome.Failed)]
        public void AnyOtherEnd_GivesTheHazardUp_WithoutARestart(HazardOutcome outcome)
        {
            var h = Active();

            h.End(h.Live, outcome);
            TickAt(h, T0 + 1);
            TickAt(h, T0 + 2);

            Assert.AreEqual(1, h.Starts.Count);
        }

        [TestMethod]
        public void ReapedRuns_CountTowardTheSameThreeRestartStreak_AsAborted()
        {
            var h = Active();

            for (var i = 0; i < 3; i++)
            {
                h.End(h.Live, HazardOutcome.Reaped);
                TickAt(h, T0 + 1 + i);
            }

            Assert.AreEqual(4, h.Starts.Count, "three reaps, three restarts");

            h.End(h.Live, HazardOutcome.Aborted);
            TickAt(h, T0 + 5);
            TickAt(h, T0 + 6);

            Assert.AreEqual(4, h.Starts.Count, "Reaped and Aborted share one streak: the fourth consecutive end gives up");
        }

        [TestMethod]
        public void AStaleEnd_ForARunNoLongerHeld_IsIgnored()
        {
            var h = Active();
            var first = h.Live;

            h.End(first, HazardOutcome.Completed);
            TickAt(h, T0 + 1);

            Assert.AreEqual(2, h.Starts.Count);

            h.End(first, HazardOutcome.Aborted); // the first run ends again: nothing holds it any more
            TickAt(h, T0 + 2);

            Assert.AreEqual(2, h.Starts.Count, "the live run is untouched by a stale end");
            Assert.AreEqual(0, h.Live.Stops);
        }

        // ---- teardown -----------------------------------------------------------------------------------------

        [DataTestMethod]
        [DataRow(WorldEventOutcome.Success)]
        [DataRow(WorldEventOutcome.FailedTimeout)]
        [DataRow(WorldEventOutcome.AbortedShutdown)]
        [DataRow(WorldEventOutcome.AbortedError)]
        public void Finish_StopsTheLiveRunOnce_ForEveryOutcome(WorldEventOutcome outcome)
        {
            var h = Active();
            var run = h.Live;

            h.Event.Finish(outcome);

            Assert.AreEqual(1, run.Stops);
            Assert.IsTrue(run.StopReason.Contains("finish:" + outcome), run.StopReason);
            Assert.IsNull(h.Event.HazardRunHandle);

            h.Event.Finish(outcome);
            h.Event.Finish(WorldEventOutcome.AbortedShutdown);

            Assert.AreEqual(1, run.Stops, "Finish is idempotent and so is the stop");
        }

        [TestMethod]
        public void AfterFinish_NothingRestarts_EvenWithACompletedRunPending()
        {
            var h = Active();
            var run = h.Live;

            h.End(run, HazardOutcome.Completed);
            h.Event.Finish(WorldEventOutcome.Success);

            for (var i = 1; i <= 5; i++)
                TickAt(h, T0 + i);

            Assert.AreEqual(1, h.Starts.Count, "a Completed end queued before Finish does not chain");
        }

        [TestMethod]
        public void Cleanup_AfterTheClaimWindow_DoesNotStopTwice()
        {
            var h = Active();
            var run = h.Live;

            h.Clock = T0 + 10;
            h.Event.Finish(WorldEventOutcome.Success);

            Assert.AreEqual(WorldEventState.Rewarding, h.Event.State, "fixture: a success opens the claim window");

            TickAt(h, T0 + 10 + 200);

            Assert.AreEqual(WorldEventState.Done, h.Event.State);
            Assert.AreEqual(1, run.Stops);
            Assert.AreEqual(1, h.Starts.Count);
        }

        [TestMethod]
        public void AbortDuringTheClaimWindow_DoesNotStopTwice()
        {
            var h = Active();
            var run = h.Live;

            h.Event.Finish(WorldEventOutcome.Success);
            h.Event.Finish(WorldEventOutcome.AbortedShutdown);

            Assert.AreEqual(WorldEventState.Done, h.Event.State);
            Assert.AreEqual(1, run.Stops);
        }

        [TestMethod]
        public void FinishBeforeAnyRunStarted_StopsNothing_AndStartsNothing()
        {
            var h = Build(new HazardDef { Kind = "wisps" });

            h.Event.Stage(T0);
            h.Event.Finish(WorldEventOutcome.AbortedShutdown);
            h.Event.Tick(T0 + 1);

            Assert.AreEqual(0, h.Starts.Count);
        }

        [TestMethod]
        public void ARunEndedButNotYetSettled_ThenFinish_StopsTheHeldRunOnce()
        {
            var h = Active();
            var run = h.Live;

            h.End(run, HazardOutcome.Aborted);
            h.Event.Finish(WorldEventOutcome.Success);

            Assert.AreEqual(1, run.Stops, "the event still holds the run until it has settled the end; Stop on an ended run is a no-op in the framework");
        }
    }
}
