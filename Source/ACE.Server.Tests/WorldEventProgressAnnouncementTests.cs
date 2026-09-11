using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Objectives;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the periodic progress broadcast and the boss health milestone latch (2026-08-29, owner
    /// directive: "Event should continue updating global messages as it continues"). The pure decision
    /// functions (<see cref="WorldEvent.ProgressDue"/>, <see cref="WorldEvent.BossHealthMilestoneCrossed"/>,
    /// <see cref="WorldEvent.DueBossHealthMilestones"/>) are tested directly (TECH-DESIGN D6); the wiring
    /// into <see cref="WorldEvent.Tick"/> is tested by driving a real event with a fake clock and capturing
    /// <see cref="WorldEventAnnouncer.BroadcastSink"/>, following <see cref="WorldEventStateMachineTests"/>'s
    /// pattern.
    /// </summary>
    [TestClass]
    public class WorldEventProgressAnnouncementTests
    {
        // ---- fakes and builders (mirrors WorldEventStateMachineTests.BuildEvent) -----------------------

        private sealed class FakeObjective : IWorldEventObjective
        {
            public bool IsComplete { get; set; }

            public string ProgressText { get; set; } = "0 of 1 slain.";

            public void OnCreatureDied(Creature creature, DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)
            {
            }

            public void Tick(double now)
            {
            }

            public WorldEventMvp Mvp() => WorldEventMvp.None;
        }

        private static readonly double T0 = 1_700_000_000d;

        private static SourceThemeDef BuildSource()
        {
            return new SourceThemeDef
            {
                Id = "ambush",
                DisplayName = "Ambush",
                Geometry = "edges",
                GeometryKind = SourceGeometry.Edges,
                GeometryRadius = 45f,
                GeometryPoints = 3,
                WaveIntervalSeconds = 45,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 },
                HoldAdjacentLandblocks = false,
                RewardRadius = 60f,
                CompatibleGoals = new List<string> { "kill_count" },
                StartFlavour = "Something is coming through the weave near {anchor}.",
                WaveFlavour = "Another rank tears its way in!"
            };
        }

        private static GoalDef BuildGoal()
        {
            return new GoalDef
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
        }

        private static RewardDef BuildReward()
        {
            return new RewardDef
            {
                Id = "standard",
                DisplayName = "Hammer Crate",
                SuccessCrateWcid = 1002600,
                ConsolationCrateWcid = 1002601,
                CacheWcid = 1002602,
                ParticipantsPerCache = 8,
                ClaimWindowSeconds = 120,
                GateByCharacter = true,
                GateByAccount = true,
                GateByIp = true
            };
        }

        private static FamilyDef BuildFamily()
        {
            return new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                HueKey = "ember",
                BiomeTags = new List<string> { "volcanic" },
                Members = new List<FamilyMember>
                {
                    new FamilyMember { Wcid = 1002604, Name = "Emberwrought Thrall", Level = 20, Role = 0 }
                }
            };
        }

        private static AnchorDef BuildAnchor()
        {
            return new AnchorDef
            {
                Id = "here",
                DisplayName = "the test anchor",
                CellId = 0x016C019E,
                BiomeTags = new List<string>()
            };
        }

        private static BossDef BuildNamedBoss()
        {
            return new BossDef
            {
                Id = "vhaleth",
                DisplayName = "Vhaleth the Undying",
                Kind = BossKind.Named,
                NamedWcid = 1002700
            };
        }

        private static WorldEventComposition BuildComposition(BossDef boss = null)
        {
            return new WorldEventComposition(BuildSource(), new[] { BuildFamily() }, boss ?? BossDef.None, BuildGoal(),
                BuildReward(), BuildAnchor(), new Position(0x016C019E, 10f, 10f, 0f, 0f, 0f, 0f, 1f, 0),
                WorldEventAxisStore.Empty);
        }

        private static WorldEvent BuildEvent(FakeObjective objective, double now,
            int maxDurationSeconds = WorldEvent.DefaultMaxDurationSeconds,
            int abandonAfterSeconds = 100000, int wipeGraceSeconds = 100000,
            int minDurationSeconds = WorldEvent.DefaultMinDurationSeconds,
            BossDef boss = null, Func<uint, double, bool> bossSpawner = null,
            Func<(bool Ok, uint Current, uint Max)> bossHealthSampler = null)
        {
            var request = new WorldEventRequest
            {
                SourceId = "ambush",
                FamilyId = "emberwrought",
                GoalId = "kill_count",
                AnnounceLeadSeconds = 0,
                MaxDurationSeconds = maxDurationSeconds,
                MinDurationSeconds = minDurationSeconds,

                // Left far out of range of every test's tick window: these tests are about the progress
                // cadence, not about the abandon/wipe timers, and a default 90s abandon window with nobody
                // ever credited would otherwise finish the run out from under a 120s+ interval test.
                AbandonAfterSeconds = abandonAfterSeconds,
                WipeGraceSeconds = wipeGraceSeconds,
                Invoker = "test"
            };

            return new WorldEvent(1, BuildComposition(boss), request, objective,
                () => new AudienceEstimate(3, 100, 150), () => now,
                bossSpawner: bossSpawner, bossHealthSampler: bossHealthSampler);
        }

        // ---- ProgressDue (pure) --------------------------------------------------------------------------

        [TestMethod]
        public void ProgressDue_NeverFiresAtElapsedZero()
        {
            Assert.IsFalse(WorldEvent.ProgressDue(now: T0, lastProgressAt: T0, intervalSeconds: 120));
        }

        [TestMethod]
        public void ProgressDue_FiresOnceTheIntervalHasElapsed()
        {
            Assert.IsFalse(WorldEvent.ProgressDue(now: T0 + 119, lastProgressAt: T0, intervalSeconds: 120));
            Assert.IsTrue(WorldEvent.ProgressDue(now: T0 + 120, lastProgressAt: T0, intervalSeconds: 120));
        }

        [TestMethod]
        public void ProgressDue_ZeroInterval_NeverFires()
        {
            Assert.IsFalse(WorldEvent.ProgressDue(now: T0 + 100000, lastProgressAt: T0, intervalSeconds: 0));
        }

        [TestMethod]
        public void ProgressDue_NegativeInterval_NeverFires()
        {
            Assert.IsFalse(WorldEvent.ProgressDue(now: T0 + 100000, lastProgressAt: T0, intervalSeconds: -5));
        }

        // ---- ProgressDisplayWave (pure) - 2026-08-29 review fix: the off-by-one -------------------------

        /// <summary>
        /// -1 (nothing has spawned yet - OnBecameActive's own initial value) must omit the clause, not
        /// display "Wave 0.".
        /// </summary>
        [TestMethod]
        public void ProgressDisplayWave_NoWaveSpawnedYet_ReturnsZero()
        {
            Assert.AreEqual(0, WorldEvent.ProgressDisplayWave(lastWaveIndexSpawned: -1));
        }

        /// <summary>
        /// The bug this fixes: OnBecameActive's synchronous SpawnWave(initial: true) IS the run's genuine
        /// first wave (the code's own docstring calls it "wave 0") - so once it has actually landed
        /// (lastWaveIndexSpawned == 0), the DISPLAY value must be 1, not 0.
        /// </summary>
        [TestMethod]
        public void ProgressDisplayWave_AfterTheGenuineFirstWaveLands_ReturnsOne()
        {
            Assert.AreEqual(1, WorldEvent.ProgressDisplayWave(lastWaveIndexSpawned: 0));
        }

        /// <summary>The second wave's landing advances lastWaveIndexSpawned to 1; display must read 2, never "Wave 1" again.</summary>
        [TestMethod]
        public void ProgressDisplayWave_AfterTheSecondWaveLands_ReturnsTwo()
        {
            Assert.AreEqual(2, WorldEvent.ProgressDisplayWave(lastWaveIndexSpawned: 1));
        }

        // ---- BossHealthMilestoneCrossed / DueBossHealthMilestones (pure) ---------------------------------

        [TestMethod]
        public void BossHealthMilestoneCrossed_AtExactlyTheThreshold_IsTrue()
        {
            Assert.IsTrue(WorldEvent.BossHealthMilestoneCrossed(current: 75, max: 100, pct: 75));
        }

        [TestMethod]
        public void BossHealthMilestoneCrossed_AboveTheThreshold_IsFalse()
        {
            Assert.IsFalse(WorldEvent.BossHealthMilestoneCrossed(current: 76, max: 100, pct: 75));
        }

        [TestMethod]
        public void BossHealthMilestoneCrossed_ZeroMax_IsFalse()
        {
            Assert.IsFalse(WorldEvent.BossHealthMilestoneCrossed(current: 0, max: 0, pct: 75));
        }

        [TestMethod]
        public void DueBossHealthMilestones_75Percent_FiresExactlyOnce()
        {
            var fired = new HashSet<int>();

            var due = WorldEvent.DueBossHealthMilestones(current: 75, max: 100, fired);

            CollectionAssert.AreEqual(new[] { 75 }, due);
            Assert.IsTrue(fired.Contains(75));

            // Same sample again (e.g. the next tick, nothing changed) must not re-fire it.
            var dueAgain = WorldEvent.DueBossHealthMilestones(current: 75, max: 100, fired);

            Assert.AreEqual(0, dueAgain.Count);
        }

        /// <summary>
        /// The invariant named explicitly in the spec: a mid-fight max-health RAISE (RatchetBossHealth) that
        /// pushes the ratio back above a tier already announced must never re-fire that tier.
        /// </summary>
        [TestMethod]
        public void DueBossHealthMilestones_RaisingMaxAfterward_DoesNotRefireAnAlreadyFiredTier()
        {
            var fired = new HashSet<int>();

            // Boss drops to 75/100 = 75% - fires.
            var due = WorldEvent.DueBossHealthMilestones(current: 75, max: 100, fired);
            CollectionAssert.AreEqual(new[] { 75 }, due);

            // A ratchet raises max to 400 with current following it up to 350 (75/100 == 300/400, but the
            // raise landed a bit above that): 350/400 = 87.5%, comfortably back above the 75% tier.
            var dueAfterRatchet = WorldEvent.DueBossHealthMilestones(current: 350, max: 400, fired);

            Assert.AreEqual(0, dueAfterRatchet.Count, "the 75% tier already fired once and must not fire again");
        }

        [TestMethod]
        public void DueBossHealthMilestones_FastDrop_FiresEveryTierItPassedInOneSample()
        {
            var fired = new HashSet<int>();

            // One hit takes the boss from full health straight to 20% - all three tiers are due at once.
            var due = WorldEvent.DueBossHealthMilestones(current: 20, max: 100, fired);

            CollectionAssert.AreEqual(new[] { 75, 50, 25 }, due);
            Assert.AreEqual(3, fired.Count);
        }

        // ---- WorldEvent.Tick wiring -----------------------------------------------------------------------

        private static List<string> CaptureBroadcasts(Action drive)
        {
            var captured = new List<string>();
            var original = WorldEventAnnouncer.BroadcastSink;

            try
            {
                WorldEventAnnouncer.BroadcastSink = captured.Add;
                drive();
            }
            finally
            {
                WorldEventAnnouncer.BroadcastSink = original;
            }

            return captured;
        }

        private static void WithProgressInterval(long intervalSeconds, Action body)
        {
            var original = WorldEvent.ProgressIntervalSecondsSource;

            try
            {
                WorldEvent.ProgressIntervalSecondsSource = () => intervalSeconds;
                body();
            }
            finally
            {
                WorldEvent.ProgressIntervalSecondsSource = original;
            }
        }

        [TestMethod]
        public void Tick_ProgressFiresAtIntervalMultiples_NeverAtElapsedZero()
        {
            WithProgressInterval(120, () =>
            {
                var objective = new FakeObjective();
                var evt = BuildEvent(objective, T0);

                var lines = CaptureBroadcasts(() =>
                {
                    evt.Stage(T0);
                    evt.Tick(T0); // -> Active, ActiveAt = T0. Elapsed 0 - must not fire here.

                    for (var offset = 1; offset < 120; offset++)
                        evt.Tick(T0 + offset);

                    evt.Tick(T0 + 120); // exactly one interval past Active - first progress line.
                });

                var progressLines = lines.Where(l => l.Contains(":") && l.Contains("slain")).ToList();

                Assert.AreEqual(1, progressLines.Count,
                    $"expected exactly one progress line at +120s, got: {string.Join(" | ", lines)}");
            });
        }

        [TestMethod]
        public void Tick_ProgressDoesNotFire_WhenIntervalPropertyIsZero()
        {
            WithProgressInterval(0, () =>
            {
                var objective = new FakeObjective();
                var evt = BuildEvent(objective, T0);

                var lines = CaptureBroadcasts(() =>
                {
                    evt.Stage(T0);
                    evt.Tick(T0);

                    for (var offset = 1; offset <= 500; offset++)
                        evt.Tick(T0 + offset);
                });

                var progressLines = lines.Where(l => l.Contains("slain")).ToList();

                Assert.AreEqual(0, progressLines.Count, $"interval 0 must disable progress entirely: {string.Join(" | ", lines)}");
            });
        }

        /// <summary>
        /// Once the run leaves Active (here: the objective completes and the minimum duration has already
        /// elapsed, so the very next tick resolves Success), no further progress line may land - even though
        /// TickActive's own Finish() call for the terminal timer verdicts falls through the switch rather
        /// than returning (see the WorldEvent.cs remarks beside the State check).
        /// </summary>
        [TestMethod]
        public void Tick_ProgressDoesNotFire_AfterTheRunLeavesActive()
        {
            WithProgressInterval(1, () =>
            {
                var objective = new FakeObjective();
                var evt = BuildEvent(objective, T0);

                List<string> lines = null;

                evt.Stage(T0);
                evt.Tick(T0);

                objective.IsComplete = true;

                // Run past the (default) minimum duration so the very next tick resolves Success.
                lines = CaptureBroadcasts(() =>
                {
                    evt.Tick(T0 + WorldEvent.DefaultMinDurationSeconds);
                });

                Assert.AreEqual(WorldEventState.Rewarding, evt.State, "sanity: the run really did leave Active on this tick");

                var afterFinish = CaptureBroadcasts(() =>
                {
                    // The state machine refuses Active -> anything but Resolved once Resolved has been left,
                    // so driving Tick again must never produce another progress line - it has nothing to
                    // dispatch to (State is no longer Active).
                    evt.Tick(T0 + WorldEvent.DefaultMinDurationSeconds + 5);
                });

                Assert.AreEqual(0, afterFinish.Count(l => l.Contains("slain")),
                    $"no progress line may land once the run has left Active: {string.Join(" | ", afterFinish)}");
            });
        }

        /// <summary>
        /// 2026-08-29 review fix: the off-by-one bug end to end. SpawnWave never runs without a held
        /// landblock (OnBecameActive's own guard) and no WorldEvent unit test in this suite provides one
        /// (there is no IWorldEventLandblockBridge implementation anywhere under Source/ACE.Server.Tests), so
        /// the two fields a real SpawnWave success would have left behind are driven directly here instead -
        /// lastWaveIndexSpawned is internal for exactly this, alongside the already-public WaveIndex. This
        /// still exercises the REAL TickProgress/ProgressDisplayWave wiring through a real Tick() call; only
        /// the wave placement itself is simulated.
        /// </summary>
        [TestMethod]
        public void Tick_ProgressWaveClause_MatchesTheActualWaveNumber()
        {
            WithProgressInterval(10, () =>
            {
                var objective = new FakeObjective();
                var evt = BuildEvent(objective, T0);

                evt.Stage(T0);
                evt.Tick(T0);

                // The genuine first wave ("wave 0" per SpawnWave's own docstring) has landed.
                evt.WaveIndex = 0;
                evt.lastWaveIndexSpawned = 0;

                var firstWaveLines = CaptureBroadcasts(() => evt.Tick(T0 + 10));
                var firstProgress = firstWaveLines.FirstOrDefault(l => l.Contains("slain"));

                Assert.IsNotNull(firstProgress, $"expected a progress line at +10s: {string.Join(" | ", firstWaveLines)}");
                StringAssert.Contains(firstProgress, "Wave 1.");
                Assert.IsFalse(firstProgress.Contains("Wave 0."), firstProgress);

                // The second wave lands: SpawnWave increments WaveIndex BEFORE the pick, so it is 1 here.
                evt.WaveIndex = 1;
                evt.lastWaveIndexSpawned = 1;

                var secondWaveLines = CaptureBroadcasts(() => evt.Tick(T0 + 20));
                var secondProgress = secondWaveLines.FirstOrDefault(l => l.Contains("slain"));

                Assert.IsNotNull(secondProgress, $"expected a progress line at +20s: {string.Join(" | ", secondWaveLines)}");
                StringAssert.Contains(secondProgress, "Wave 2.");
                Assert.IsFalse(secondProgress.Contains("Wave 1."), secondProgress);
            });
        }

        // ---- boss arrival / health milestones through Tick -----------------------------------------------

        [TestMethod]
        public void Tick_NamedBossPlacement_BroadcastsExactlyOneArrivalLine()
        {
            var objective = new FakeObjective();
            var boss = BuildNamedBoss();

            var evt = BuildEvent(objective, T0, minDurationSeconds: 5, boss: boss,
                bossSpawner: (wcid, healthMult) => true);

            evt.Stage(T0);
            evt.Tick(T0);

            var lines = CaptureBroadcasts(() =>
            {
                for (var offset = 1; offset <= 6; offset++)
                    evt.Tick(T0 + offset);
            });

            var arrivalLines = lines.Where(l => l.Contains("has arrived at")).ToList();

            Assert.AreEqual(1, arrivalLines.Count, $"expected exactly one arrival line: {string.Join(" | ", lines)}");
            StringAssert.Contains(arrivalLines[0], "Vhaleth the Undying has arrived at");
        }

        /// <summary>
        /// The arrival line is Named-only (WP-progress spec item 2): a FamilyChampion pick has no authored
        /// voice, and BossKind.None means the goal has no boss at all. Both must reach championSpawned
        /// through the exact same TickChampion/IssueChampionPlacement path a Named boss does, with no
        /// arrival line ever landing.
        /// </summary>
        [TestMethod]
        public void Tick_ChampionPlacement_NeverAnnouncesArrival_ForFamilyChampionOrNone()
        {
            foreach (var boss in new[] { BossDef.FamilyChampion, BossDef.None })
            {
                var objective = new FakeObjective();

                var evt = BuildEvent(objective, T0, minDurationSeconds: 5, boss: boss,
                    bossSpawner: (wcid, healthMult) => true);

                evt.Stage(T0);
                evt.Tick(T0);

                var lines = CaptureBroadcasts(() =>
                {
                    for (var offset = 1; offset <= 6; offset++)
                        evt.Tick(T0 + offset);
                });

                Assert.AreEqual(0, lines.Count(l => l.Contains("has arrived at")),
                    $"boss kind {boss.Kind}: {string.Join(" | ", lines)}");
            }
        }

        [TestMethod]
        public void Tick_NamedBossHealthMilestones_EachFiresExactlyOnceAsHealthDrops()
        {
            var objective = new FakeObjective();
            var boss = BuildNamedBoss();

            var current = 100u;
            const uint max = 100u;

            var evt = BuildEvent(objective, T0, boss: boss, bossHealthSampler: () => (true, current, max));

            evt.Stage(T0);
            evt.Tick(T0);

            var lines75 = CaptureBroadcasts(() =>
            {
                current = 75;
                evt.Tick(T0 + 1);
            });

            Assert.AreEqual(1, lines75.Count(l => l.Contains("is at 75% health!")), string.Join(" | ", lines75));

            // Same 75% again next tick - must not re-fire.
            var linesUnchanged = CaptureBroadcasts(() => evt.Tick(T0 + 2));

            Assert.AreEqual(0, linesUnchanged.Count(l => l.Contains("% health!")), string.Join(" | ", linesUnchanged));

            var lines50 = CaptureBroadcasts(() =>
            {
                current = 50;
                evt.Tick(T0 + 3);
            });

            Assert.AreEqual(1, lines50.Count(l => l.Contains("is at 50% health!")), string.Join(" | ", lines50));

            var lines25 = CaptureBroadcasts(() =>
            {
                current = 25;
                evt.Tick(T0 + 4);
            });

            Assert.AreEqual(1, lines25.Count(l => l.Contains("is at 25% health!")), string.Join(" | ", lines25));
        }

        /// <summary>
        /// The invariant named explicitly in the spec, driven through a real Tick() rather than only the
        /// pure DueBossHealthMilestones helper: once 75% has fired, a mid-fight max-health RATCHET
        /// (RatchetBossHealth) that pushes the ratio back above 75% must never re-fire that tier - but the
        /// LOWER tiers (50%, 25%) must still be free to fire once the fight brings the ratio back down
        /// through them, against the NEW (raised) max.
        /// </summary>
        [TestMethod]
        public void Tick_NamedBossHealthMilestones_MaxRaiseAfter75Fired_DoesNotRefire75_ButLowerTiersStillFire()
        {
            var objective = new FakeObjective();
            var boss = BuildNamedBoss();

            var current = 100u;
            var max = 100u;

            var evt = BuildEvent(objective, T0, boss: boss, bossHealthSampler: () => (true, current, max));

            evt.Stage(T0);
            evt.Tick(T0);

            var lines75 = CaptureBroadcasts(() =>
            {
                current = 75;
                evt.Tick(T0 + 1);
            });

            Assert.AreEqual(1, lines75.Count(l => l.Contains("is at 75% health!")), string.Join(" | ", lines75));

            // A ratchet raises max to 400, and current jumps to 350 with it (75/100 == 300/400, the raise
            // landed a bit above that floor): 350/400 = 87.5%, comfortably back above the 75% tier.
            var linesAfterRatchet = CaptureBroadcasts(() =>
            {
                current = 350;
                max = 400;
                evt.Tick(T0 + 2);
            });

            Assert.AreEqual(0, linesAfterRatchet.Count(l => l.Contains("% health!")),
                $"75% already fired once and must not re-fire after the ratchet: {string.Join(" | ", linesAfterRatchet)}");

            // The fight continues against the NEW max: 200/400 = 50% - the 50% tier must still be free to fire.
            var lines50 = CaptureBroadcasts(() =>
            {
                current = 200;
                evt.Tick(T0 + 3);
            });

            Assert.AreEqual(1, lines50.Count(l => l.Contains("is at 50% health!")),
                $"50% must still fire against the raised max: {string.Join(" | ", lines50)}");
        }

        [TestMethod]
        public void Tick_NamedBossHealthMilestones_NeverFire_WhenTheGateIsDisabled()
        {
            var original = WorldEvent.BossHealthMilestonesEnabledSource;

            try
            {
                WorldEvent.BossHealthMilestonesEnabledSource = () => false;

                var objective = new FakeObjective();
                var boss = BuildNamedBoss();

                // 10% would cross every tier at once if the gate were open.
                var evt = BuildEvent(objective, T0, boss: boss, bossHealthSampler: () => (true, 10u, 100u));

                evt.Stage(T0);
                evt.Tick(T0);

                var lines = CaptureBroadcasts(() => evt.Tick(T0 + 1));

                Assert.AreEqual(0, lines.Count(l => l.Contains("% health!")), string.Join(" | ", lines));
            }
            finally
            {
                WorldEvent.BossHealthMilestonesEnabledSource = original;
            }
        }
    }
}
