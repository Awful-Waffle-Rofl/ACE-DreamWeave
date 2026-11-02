using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pinnacles;
using ACE.Server.SpeedCourses;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.Tests
{
    [TestClass]
    public class SpeedCourseRulesTests
    {
        // ---- run level and gem spec ----

        [TestMethod]
        public void RunLevel_FixedLevel_IsFlatWhateverThePlayerLevel()
        {
            foreach (var player in new[] { 1, 60, 150, 275 })
                Assert.AreEqual(150, SpeedCourseRules.RunLevel(player, 60, 40, 150), $"player level {player}");

            Assert.AreEqual(150, SpeedCourseRules.RunLevel(275, 150, 150, 150));
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, SpeedCourseRules.RunLevel(10, 60, 0, 9999), "a fixed level above the ceiling is capped");
        }

        [TestMethod]
        public void RunLevel_FixedLevelZero_KeepsTheScalingBehaviour()
        {
            Assert.AreEqual(60, SpeedCourseRules.RunLevel(1, 60, 0, 0));
            Assert.AreEqual(200, SpeedCourseRules.RunLevel(200, 60, 0, 0));
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, SpeedCourseRules.RunLevel(9999, 60, 0, 0));
        }

        [TestMethod]
        public void RunLevel_ClampsToTheLargestFloorAndTheRunCeiling()
        {
            Assert.AreEqual(60, SpeedCourseRules.RunLevel(10, 60, 40, 0));
            Assert.AreEqual(70, SpeedCourseRules.RunLevel(10, 60, 70, 0));
            Assert.AreEqual(1, SpeedCourseRules.RunLevel(0, 0, 0, 0));
            Assert.AreEqual(150, SpeedCourseRules.RunLevel(150, 60, 0, 0));
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, SpeedCourseRules.RunLevel(9999, 60, 0, 0));
            Assert.AreEqual(DungeonGemSpec.MaxRunLevel, SpeedCourseRules.RunLevel(10, 9999, 0, 0), "a floor above the ceiling cannot push past it");
        }

        [TestMethod]
        public void BuildGemSpec_CarriesTheCourseFamilySeedAndNoModifiers()
        {
            var c = SpeedCourseFixtures.Course();
            var spec = SpeedCourseRules.BuildGemSpec(c, 120, 0x50000001u);

            Assert.AreEqual("speed_black_book", spec.DungeonId);
            Assert.AreEqual(120, spec.Level);
            Assert.AreEqual(2, spec.Tier);
            Assert.AreEqual("undead", spec.Family);
            Assert.AreEqual(3003, spec.Seed);
            Assert.AreEqual(0, spec.Modifiers.Count);
            Assert.AreEqual(0x50000001u, spec.OwnerGuid);
        }

        // ---- the Seal ----

        [TestMethod]
        public void SealOpens_AllThirtyTwoSubsets_OpenExactlyWhenP1AndP3AndTwoRites()
        {
            var c = SpeedCourseFixtures.Course();
            var names = new[] { "p1", "p3", "north_rite", "east_rite", "south_rite" };
            var opened = 0;

            for (var mask = 0; mask < 32; mask++)
            {
                var set = names.Where((_, i) => (mask & (1 << i)) != 0).ToList();
                var expected = set.Contains("p1") && set.Contains("p3") && set.Count(n => n.EndsWith("_rite")) >= 2;

                Assert.AreEqual(expected, SpeedCourseRules.SealOpens(set, c), "subset " + string.Join(",", set));

                if (expected)
                    opened++;
            }

            Assert.AreEqual(4, opened, "p1+p3 with C(3,2)+C(3,3) rite choices");
        }

        [TestMethod]
        public void SealOpens_RepeatsCountOnce_AndUnknownTokensWeighNothing()
        {
            var c = SpeedCourseFixtures.Course();

            Assert.IsFalse(SpeedCourseRules.SealOpens(new[] { "p1", "p1", "p1" }, c));
            Assert.IsFalse(SpeedCourseRules.SealOpens(new[] { "p1", "p3", "north_rite", "mystery", "mystery2" }, c));
            Assert.IsFalse(SpeedCourseRules.SealOpens(null, c));
            Assert.IsFalse(SpeedCourseRules.SealOpens(new string[0], c));
        }

        // ---- puzzle types ----

        [TestMethod]
        public void PickPuzzleTypes_NeverRepeatsAType_AcrossManySeeds()
        {
            for (var seed = 0; seed < 200; seed++)
            {
                var picked = SpeedCourseRules.PickPuzzleTypes(new Random(seed), 3, SpeedCourseRules.PuzzleTypeNames);

                Assert.AreEqual(3, picked.Count);
                Assert.AreEqual(3, picked.Distinct().Count(), "seed " + seed);
            }
        }

        [TestMethod]
        public void PickPuzzleTypes_IsDeterministicPerSeed_AndCapsAtDistinctTypes()
        {
            var a = SpeedCourseRules.PickPuzzleTypes(new Random(7), 3, SpeedCourseRules.PuzzleTypeNames);
            var b = SpeedCourseRules.PickPuzzleTypes(new Random(7), 3, SpeedCourseRules.PuzzleTypeNames);
            CollectionAssert.AreEqual(a, b);

            var capped = SpeedCourseRules.PickPuzzleTypes(new Random(1), 5, new[] { "Sigil", "Sigil", "Odd" });
            Assert.AreEqual(2, capped.Count);
            Assert.AreEqual(0, SpeedCourseRules.PickPuzzleTypes(new Random(1), 3, null).Count);
        }

        [TestMethod]
        public void PuzzleTypeNames_MatchTheEnum()
        {
            CollectionAssert.AreEqual(Enum.GetNames(typeof(ACE.Server.PuzzleGates.PuzzleGateType)), SpeedCourseRules.PuzzleTypeNames.ToList());
        }

        // ---- seeds and pct ----

        [TestMethod]
        public void HazardSeed_IsDeterministic_DistinctPerSlotAndAttempt_AndNonZero()
        {
            Assert.AreEqual(SpeedCourseRules.HazardSeed(3003, 2), SpeedCourseRules.HazardSeed(3003, 2));

            var seeds = new HashSet<int>();

            for (var slot = 0; slot < 8; slot++)
                for (var attempt = 0; attempt < 4; attempt++)
                    seeds.Add(SpeedCourseRules.HazardSeed(3003, slot, attempt));

            Assert.AreEqual(32, seeds.Count);
            Assert.IsTrue(seeds.All(s => s > 0));
            Assert.AreNotEqual(SpeedCourseRules.HazardSeed(3003, 1), SpeedCourseRules.HazardSeed(3004, 1));
        }

        [TestMethod]
        public void ScaledPct_CallsHazardLevelScale()
        {
            var h = SpeedCourseFixtures.Course().Hazards[0];

            Assert.AreEqual(HazardLevelScale.Scaled(120, h.PctAuthored, h.PctMin, h.PctMax), SpeedCourseRules.ScaledPct(h, 120), 1e-12);
        }

        // ---- trigger state machine ----

        private static SpeedHazardTriggerDef Trig(string type, string slot = null, double seconds = 0, double fraction = 0)
            => new SpeedHazardTriggerDef { Type = type, Slot = slot, Seconds = seconds, Fraction = fraction };

        [TestMethod]
        public void ClockRebase_StartsOnceRebased_AndNeverStops()
        {
            var t = Trig(SpeedHazardTriggers.ClockRebase);

            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot()));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { ClockRebased = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { ClockRebased = true, Running = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { ClockRebased = true, Running = true, AnyRunnerInRegion = false }));
        }

        [TestMethod]
        public void WhileInRegion_StartsStopsAndRestartsAfterCompleted()
        {
            var t = Trig(SpeedHazardTriggers.WhileInRegion);

            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot()));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { AnyRunnerInRegion = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { AnyRunnerInRegion = true, Running = true }));
            Assert.AreEqual(SpeedHazardDecision.Stop, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { AnyRunnerInRegion = false, Running = true }));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { AnyRunnerInRegion = true, LastEnd = SpeedHazardEnd.Completed, LastEndTime = 10, Now = 10 }), "completed while still inside restarts");
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { AnyRunnerInRegion = false, LastEnd = SpeedHazardEnd.Completed }));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { AnyRunnerInRegion = true, LastEnd = SpeedHazardEnd.Stopped }), "re-entering after a stop starts again");
        }

        [TestMethod]
        public void OnNamedDead_NeedsTheNamedDeadAndARunnerInside()
        {
            var t = Trig(SpeedHazardTriggers.OnNamedDeadAndInRegion, SpeedNamedSlots.K2);

            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 5, new SpeedHazardSnapshot { AnyRunnerInRegion = true, K1Dead = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 5, new SpeedHazardSnapshot { K2Dead = true }));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 5, new SpeedHazardSnapshot { AnyRunnerInRegion = true, K2Dead = true }));
            Assert.AreEqual(SpeedHazardDecision.Stop, SpeedCourseRules.DecideHazard(t, 5, new SpeedHazardSnapshot { K2Dead = true, Running = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 5,
                new SpeedHazardSnapshot { AnyRunnerInRegion = true, K2Dead = true, LastEnd = SpeedHazardEnd.Completed }), "a completed memory is final");

            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(Trig(SpeedHazardTriggers.OnNamedDeadAndInRegion, SpeedNamedSlots.Boss), 0,
                new SpeedHazardSnapshot { AnyRunnerInRegion = true, BossDead = true }));
        }

        [TestMethod]
        public void MemoryRetry_WaitsRetrySecondsAfterAFailure_ThenStarts()
        {
            var t = Trig(SpeedHazardTriggers.OnNamedDeadAndInRegion, SpeedNamedSlots.K2);

            SpeedHazardSnapshot At(double now) => new SpeedHazardSnapshot
            {
                AnyRunnerInRegion = true, K2Dead = true, LastEnd = SpeedHazardEnd.Failed, LastEndTime = 100, LastStart = 80, Now = now,
            };

            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 5, At(100)));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 5, At(104.9)));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 5, At(105)));
        }

        [TestMethod]
        public void FailedRetry_IsFlooredAtOneSecond_WhenRetrySecondsIsZero()
        {
            var t = Trig(SpeedHazardTriggers.OnNamedDeadAndInRegion, SpeedNamedSlots.K2);

            SpeedHazardSnapshot At(double now) => new SpeedHazardSnapshot
            {
                AnyRunnerInRegion = true, K2Dead = true, LastEnd = SpeedHazardEnd.Failed, LastEndTime = 100, LastStart = 80, Now = now,
            };

            Assert.AreEqual(1.0, SpeedCourseRules.MinFailedRetrySeconds);
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, At(100)), "retrySeconds 0 does not restart on the same tick");
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, At(100.5)));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0, At(101)));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0.25, At(100.9)), "a sub-second retry is floored too");

            // The floor applies to Failed only: a Stopped run restarts as soon as its condition returns.
            var stopped = new SpeedHazardSnapshot { AnyRunnerInRegion = true, K2Dead = true, LastEnd = SpeedHazardEnd.Stopped, LastEndTime = 100, Now = 100 };
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0, stopped));
        }

        [TestMethod]
        public void BossAliveInterval_FirstStartAtOnce_ThenEveryIntervalFromTheLastStart_UntilBossDies()
        {
            var t = Trig(SpeedHazardTriggers.BossAliveInterval, seconds: 20);

            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot()), "no boss yet");
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { BossSpawned = true, Now = 1 }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { BossSpawned = true, Now = 25, LastStart = 10, LastEnd = SpeedHazardEnd.Completed, LastEndTime = 15 }));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { BossSpawned = true, Now = 30, LastStart = 10, LastEnd = SpeedHazardEnd.Completed, LastEndTime = 15 }));
            Assert.AreEqual(SpeedHazardDecision.Stop, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { BossSpawned = true, BossDead = true, Running = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { BossSpawned = true, BossDead = true, Now = 99 }));
        }

        [TestMethod]
        public void BossHealthBelow_StartsAtTheFraction_AndIsNotStoppedByHealing()
        {
            var t = Trig(SpeedHazardTriggers.BossHealthBelow, fraction: 1.0 / 3);

            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { BossSpawned = true, BossHealthFraction = 0.5 }));
            Assert.AreEqual(SpeedHazardDecision.Start, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { BossSpawned = true, BossHealthFraction = 1.0 / 3 }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { BossSpawned = true, BossHealthFraction = 0.9, Running = true }));
            Assert.AreEqual(SpeedHazardDecision.Stop, SpeedCourseRules.DecideHazard(t, 0, new SpeedHazardSnapshot { BossSpawned = true, BossDead = true, Running = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(t, 0,
                new SpeedHazardSnapshot { BossSpawned = true, BossHealthFraction = 0.1, LastEnd = SpeedHazardEnd.Completed }));
        }

        [TestMethod]
        public void UnknownOrNullTrigger_Holds()
        {
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(null, 0, new SpeedHazardSnapshot { ClockRebased = true }));
            Assert.AreEqual(SpeedHazardDecision.Hold, SpeedCourseRules.DecideHazard(Trig("bogus"), 0, new SpeedHazardSnapshot { ClockRebased = true, AnyRunnerInRegion = true }));
        }

        // ---- named placement ----

        private static DungeonSpawnPointDef Pt(float x) => new DungeonSpawnPointDef { Cell = 1, X = x };

        private static DungeonSpawnPlanEntry E(uint wcid, DungeonRole role, float x, int uplift = 0, int stamp = 0)
            => new DungeonSpawnPlanEntry(wcid, role, Pt(x), uplift, stamp);

        [TestMethod]
        public void PlaceNamed_TakesTheFirstTwoElites_AndTheBoss()
        {
            var plan = new List<DungeonSpawnPlanEntry>
            {
                E(1, DungeonRole.Trash, 1), E(2, DungeonRole.Elite, 2, uplift: 50), E(3, DungeonRole.Trash, 3),
                E(4, DungeonRole.Elite, 4, stamp: 60), E(5, DungeonRole.Elite, 5), E(6, DungeonRole.Boss, 6),
            };

            var r = SpeedCourseRules.PlaceNamed(plan, Pt(100), Pt(200), Pt(300), null);

            Assert.AreEqual(6, r.Entries.Count);
            Assert.AreEqual(2u, r.K1.Wcid);
            Assert.AreEqual(100f, r.K1.Point.X);
            Assert.AreEqual(50, r.K1.UpliftLevel, "uplift survives the move");
            Assert.AreEqual(4u, r.K2.Wcid);
            Assert.AreEqual(200f, r.K2.Point.X);
            Assert.AreEqual(60, r.K2.StampLevel);
            Assert.AreEqual(6u, r.Boss.Wcid);
            Assert.AreEqual(300f, r.Boss.Point.X);
            Assert.AreEqual(DungeonRole.Boss, r.Boss.Role, "the boss keeps its role");
            Assert.AreEqual(5f, r.Entries[4].Point.X, "the third elite stays put");
            Assert.AreEqual(2f, plan[1].Point.X, "the input plan is not modified");
            CollectionAssert.AreEqual(plan.Select(e => e.Wcid).ToList(), r.Entries.Select(e => e.Wcid).ToList(), "plan order is kept");
        }

        [TestMethod]
        public void PlaceNamed_FewerThanTwoElites_FillsFromTheHighestLevelTrash()
        {
            var levels = new Dictionary<uint, int> { { 1, 10 }, { 3, 90 }, { 4, 90 }, { 5, 30 } };
            var plan = new List<DungeonSpawnPlanEntry>
            {
                E(1, DungeonRole.Trash, 1), E(2, DungeonRole.Elite, 2), E(3, DungeonRole.Trash, 3),
                E(4, DungeonRole.Trash, 4), E(5, DungeonRole.Trash, 5),
            };

            var r = SpeedCourseRules.PlaceNamed(plan, Pt(100), Pt(200), Pt(300), e => levels.TryGetValue(e.Wcid, out var l) ? l : 0);

            Assert.AreEqual(2u, r.K1.Wcid, "the elite is K1");
            Assert.AreEqual(3u, r.K2.Wcid, "tie on level 90 goes to the earlier plan entry");
            Assert.AreEqual(DungeonRole.Elite, r.K2.Role, "the trash fill-in is raised to elite");
            Assert.IsNull(r.Boss, "no boss entry, nothing to place");
        }

        [TestMethod]
        public void PlaceNamed_NoElites_TwoHighestTrash_AndTinyPlans()
        {
            var plan = new List<DungeonSpawnPlanEntry> { E(1, DungeonRole.Trash, 1), E(2, DungeonRole.Trash, 2), E(3, DungeonRole.Trash, 3) };
            var r = SpeedCourseRules.PlaceNamed(plan, Pt(100), Pt(200), Pt(300), e => (int)e.Wcid);

            Assert.AreEqual(3u, r.K1.Wcid);
            Assert.AreEqual(2u, r.K2.Wcid);
            Assert.AreEqual(DungeonRole.Elite, r.K1.Role, "a promoted trash entry is shaped as an elite");
            Assert.AreEqual(DungeonRole.Elite, r.K2.Role);
            Assert.AreEqual(DungeonRole.Trash, plan[2].Role, "the input entry is not modified");
            Assert.AreEqual(DungeonRole.Trash, r.Entries[0].Role, "an unmoved trash entry keeps its role");

            var one = SpeedCourseRules.PlaceNamed(new List<DungeonSpawnPlanEntry> { E(1, DungeonRole.Trash, 1) }, Pt(100), Pt(200), Pt(300), null);
            Assert.AreEqual(1u, one.K1.Wcid);
            Assert.IsNull(one.K2);

            Assert.AreEqual(0, SpeedCourseRules.PlaceNamed(null, Pt(1), Pt(2), Pt(3), null).Entries.Count);
        }

        // ---- the short-plan WARN ----

        [TestMethod]
        public void ShortPlanWarning_OnlyWhenNonBossEntriesFallShortOfCuratedPoints()
        {
            var plan = new DungeonSpawnPlan();
            plan.Entries.Add(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, Pt(1)));
            plan.Entries.Add(new DungeonSpawnPlanEntry(2, DungeonRole.Elite, Pt(2)));
            plan.Entries.Add(new DungeonSpawnPlanEntry(3, DungeonRole.Boss, Pt(3)));
            plan.Notes.Add("family: no trash in band");

            Assert.IsNull(SpeedCourseRules.ShortPlanWarning(plan, 2), "two non-boss entries fill two points (the boss does not count)");
            Assert.IsNull(SpeedCourseRules.ShortPlanWarning(null, 5), "a null plan is reported by the run itself");

            var warning = SpeedCourseRules.ShortPlanWarning(plan, 3);
            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "2 non-boss creature(s) for 3 curated point(s)");
            StringAssert.Contains(warning, "family: no trash in band", "the plan notes say why");
        }
    }
}