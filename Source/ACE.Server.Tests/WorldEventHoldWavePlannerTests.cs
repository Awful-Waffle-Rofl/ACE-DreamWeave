using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events Hold H5: the wave planner (HoldWavePlan per wave) and the tag builder (one WorldEventHoldTag per spawn
    /// entry). Pure: tuning comes from the settings resolver with an empty property lookup, so no PropertyManager read.
    /// </summary>
    [TestClass]
    public class WorldEventHoldWavePlannerTests
    {
        private const uint RunId = 4242;

        private static readonly HoldKnightSlot[] Knights =
        {
            new HoldKnightSlot(0x80000001, "Sir North", 0f, 6f),
            new HoldKnightSlot(0x80000002, "Sir East", 6f, 0f),
            new HoldKnightSlot(0x80000003, "Sir South", 0f, -6f),
        };

        private static SpecialWaveDef Special(HoldWaveType type) => new SpecialWaveDef { Type = type.ToString().ToLowerInvariant(), TypeKind = type, Weight = 1 };

        private static GoalDef Goal(bool waveMix, int assaultEvery = 0)
        {
            var protect = new ProtectDef
            {
                Wcid = 1007650,
                Count = new ScaledCount { Base = 3, PerParticipant = 0, Cap = 3 },
                Health = new ScaledCount { Base = 10000, PerParticipant = 0, Cap = 10000 },
                Radius = 6,
                Waves = new ScaledCount { Base = 5, PerParticipant = 0, Cap = 5 },
                SpecialWaves = new List<SpecialWaveDef>
                {
                    Special(HoldWaveType.Breach), Special(HoldWaveType.Runners), Special(HoldWaveType.Ranged),
                    Special(HoldWaveType.Hunters), Special(HoldWaveType.Finale)
                },
                WaveMix = waveMix ? new WaveMixDef() : null,
                Corrupted = new CorruptedDef { AssaultEveryWaves = assaultEvery, AssaultFromWave = 1, AssaultCount = 2 },
            };

            return new GoalDef { Id = "hold_test", Type = "Hold", TypeKind = GoalType.Hold, HoldSeconds = 600, Protect = protect };
        }

        private static WorldEventHoldTuning Tuning(GoalDef goal, Func<string, string> lookup = null) =>
            WorldEventHoldTuning.From(WorldEventHoldSettingsResolver.Resolve(goal, lookup ?? (_ => null)), goal, 3);

        private static WavePick Pick(int trash, int champions = 0)
        {
            var t = Enumerable.Range(0, trash).Select(i => 6000u + (uint)(i % 4)).ToList();
            var c = Enumerable.Range(0, champions).Select(i => 7000u + (uint)i).ToList();

            return new WavePick(t, c);
        }

        private static readonly IReadOnlyList<uint> RangedPool = new uint[] { 6100, 6101 };

        private static HoldWaveRequest Request(int wave, WavePick pick, HoldWaveType? forced = null, ChampionPick? champion = null) => new HoldWaveRequest
        {
            WaveNumber = wave,
            TargetWaves = 5,
            Pick = pick,
            Knights = Knights,
            Forced = forced,
            RangedPool = RangedPool,
            IsCaster = w => w == 6101,
            Champion = champion ?? ChampionPick.None,
            ChampionName = "Champion Shallows Gorger",
        };

        private static void AssertEveryTagCarriesTheRun(WorldEventHoldTag[] tags, int expected, IWorldEventHoldRunView run, string what)
        {
            Assert.AreEqual(expected, tags.Length, what);

            for (var i = 0; i < tags.Length; i++)
            {
                Assert.IsNotNull(tags[i], $"{what}: entry {i} has no tag");
                Assert.AreEqual(RunId, tags[i].RunId, $"{what}: entry {i}");
                Assert.AreSame(run, tags[i].Run, $"{what}: entry {i}");
            }
        }

        // ---- the contract: every entry is tagged with the run ------------------------------------------------------

        [TestMethod]
        public void EveryWaveEntry_CarriesANonNullTag_WithTheRunId_ForEveryWaveType()
        {
            var run = new WorldEventHoldRun(RunId);

            foreach (var forced in new HoldWaveType?[] { null, HoldWaveType.Breach, HoldWaveType.Runners, HoldWaveType.Ranged, HoldWaveType.Hunters })
            {
                var goal = Goal(waveMix: false);
                var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(5));
                var pick = Pick(10, 1);

                var plan = planner.BuildPlan(Request(2, pick, forced));
                var count = plan.KeepCount ?? pick.Total;

                AssertEveryTagCarriesTheRun(WorldEventHoldWavePlanner.BuildTags(plan, count, RunId, run, planner.Tuning), count, run, $"forced {forced?.ToString() ?? "none"} ({plan.Type})");
            }

            var mixedGoal = Goal(waveMix: true, assaultEvery: 1);
            var mixed = new WorldEventHoldWavePlanner(mixedGoal, Tuning(mixedGoal), new Random(9));
            var mixedPlan = mixed.BuildPlan(Request(2, Pick(12, 1)));

            Assert.AreEqual(HoldWaveType.Mixed, mixedPlan.Type);
            AssertEveryTagCarriesTheRun(WorldEventHoldWavePlanner.BuildTags(mixedPlan, 13, RunId, run, mixed.Tuning), 13, run, "mixed assault");

            var finaleGoal = Goal(waveMix: true);
            var finale = new WorldEventHoldWavePlanner(finaleGoal, Tuning(finaleGoal), new Random(9));
            var bossPlan = finale.BuildPlan(Request(5, Pick(8), champion: new ChampionPick(7777, false)));
            var escort = finale.BuildEscortPlan(bossPlan, Pick(8));

            AssertEveryTagCarriesTheRun(WorldEventHoldWavePlanner.BuildTags(bossPlan, 1, RunId, run, finale.Tuning), 1, run, "finale boss");
            AssertEveryTagCarriesTheRun(WorldEventHoldWavePlanner.BuildTags(escort, 8, RunId, run, finale.Tuning), 8, run, "finale escort");
        }

        // ---- wave types -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void LastWave_IsTheFinale_ItsBossIsKindFinaleBoss_AndItsEscortHasNoCopyOfTheBoss()
        {
            var goal = Goal(waveMix: true);
            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(1));
            var pick = new WavePick(new List<uint> { 7777, 6000, 7777, 6001 }, new List<uint>());

            var plan = planner.BuildPlan(Request(5, pick, champion: new ChampionPick(7777, true)));

            Assert.AreEqual(HoldWaveType.Finale, plan.Type, "the last counted wave of a goal with a finale is the finale, mix or not");
            Assert.IsTrue(plan.FinaleBossOnly);
            Assert.AreEqual(0, plan.ChampionEntryIndex);
            Assert.AreEqual("Warlord Shallows Gorger", plan.ChampionName, "the promotion's 'Champion ' comes off and the finale title goes on");

            var boss = WorldEventHoldWavePlanner.BuildTag(plan, 1, 0, RunId, null, planner.Tuning, null);
            Assert.AreEqual(HoldUnitKind.FinaleBoss, boss.Kind);
            Assert.AreEqual(HoldDamageRole.None, boss.Role);

            var escort = planner.BuildEscortPlan(plan, pick);
            Assert.IsFalse(escort.ReplacedTrash.Contains(7777u), "the escort carries no copy of the boss's own monster");
            Assert.IsTrue(WorldEventHoldWavePlanner.BuildTags(escort, 4, RunId, null, planner.Tuning).All(t => t.Kind == HoldUnitKind.Monster));
        }

        [TestMethod]
        public void Breach_SendsEveryMonsterAtOneStandingKnight_AndNamesItsSide()
        {
            var goal = Goal(waveMix: false);
            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(2));

            var plan = planner.BuildPlan(Request(2, Pick(6), HoldWaveType.Breach));

            Assert.AreEqual(HoldWaveType.Breach, plan.Type);
            Assert.IsTrue(Knights.Any(k => k.Guid == plan.BreachTargetGuid));
            Assert.AreEqual(WorldEventHoldRules.CompassSide(Knights.First(k => k.Guid == plan.BreachTargetGuid).Dx, Knights.First(k => k.Guid == plan.BreachTargetGuid).Dy), plan.BreachSide);

            foreach (var tag in WorldEventHoldWavePlanner.BuildTags(plan, 6, RunId, null, planner.Tuning))
            {
                Assert.IsTrue(tag.Seeker);
                Assert.AreEqual(plan.BreachTargetGuid, tag.ForcedTargetGuid);
                Assert.AreEqual(HoldRole.Breach, tag.WaveRole);
                Assert.AreEqual(HoldDamageRole.Melee, tag.Role);
            }
        }

        [TestMethod]
        public void Runners_KeepTheirShare_AndCarryTheirSpeeds()
        {
            var goal = Goal(waveMix: false);
            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(2));

            var plan = planner.BuildPlan(Request(2, Pick(10), HoldWaveType.Runners));

            Assert.AreEqual(6, plan.KeepCount, "countMult 0.6 of 10");

            foreach (var tag in WorldEventHoldWavePlanner.BuildTags(plan, 6, RunId, null, planner.Tuning))
            {
                Assert.AreEqual(HoldDamageRole.Runner, tag.Role);
                Assert.IsTrue(tag.Seeker, "runners always go for the knights");
                Assert.AreEqual(plan.RunRateMult, tag.RunRateMult);
                Assert.AreEqual(plan.AttackSpeedMult, tag.AttackSpeedMult);
                Assert.IsTrue(tag.RunRateMult > 1f);
            }
        }

        /// <summary>
        /// Review finding F3: the runner and ranged settings were ignored because the planner read the special wave's def
        /// values. The tuning (setting over def) is what the plan uses now; the def supplies only countMult and hunters.
        /// </summary>
        [TestMethod]
        public void Runners_AndRanged_TakeTheSettings_OverTheWaveDef()
        {
            var goal = Goal(waveMix: false);
            var runnersDef = goal.Protect.SpecialWaves.First(s => s.TypeKind == HoldWaveType.Runners);
            var rangedDef = goal.Protect.SpecialWaves.First(s => s.TypeKind == HoldWaveType.Ranged);

            Assert.AreEqual(1.5, runnersDef.SpeedMult, "control: the def's own speed differs from the setting");
            Assert.AreEqual(18.0, rangedDef.HoldDistance, "control: the def's own distance differs from the setting");

            var set = new Dictionary<string, string>
            {
                [HoldKeys.PropertyPrefix + HoldKeys.RunnerSpeedMult] = "2",
                [HoldKeys.PropertyPrefix + HoldKeys.RunnerAttackSpeedMult] = "2.5",
                [HoldKeys.PropertyPrefix + HoldKeys.RunnerHealthMult] = "0.4",
                [HoldKeys.PropertyPrefix + HoldKeys.RangedKeepDistance] = "12",
            };

            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal, k => set.TryGetValue(k, out var v) ? v : null), new Random(2));

            var runners = planner.BuildPlan(Request(2, Pick(10), HoldWaveType.Runners));
            var tag = WorldEventHoldWavePlanner.BuildTags(runners, 6, RunId, null, planner.Tuning)[0];

            Assert.AreEqual(2f, tag.RunRateMult, "runner_speed_mult 2 over the def's 1.5");
            Assert.AreEqual(2.5f, tag.AttackSpeedMult, "runner_attack_speed_mult");
            Assert.AreEqual(0.4, runners.HealthMult, 1e-9, "runner_health_mult");
            Assert.AreEqual(6, runners.KeepCount, "countMult still comes from the def");

            var ranged = planner.BuildPlan(Request(2, Pick(4), HoldWaveType.Ranged));
            Assert.AreEqual(12f, ranged.HoldDistance, "ranged_keep_distance over the def's 18");

            // Unset: the def's values come through (the tuning's fallback is the def).
            var plain = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(2));
            Assert.AreEqual(1.5f, plain.BuildPlan(Request(2, Pick(10), HoldWaveType.Runners)).RunRateMult);
            Assert.AreEqual(18f, plain.BuildPlan(Request(2, Pick(4), HoldWaveType.Ranged)).HoldDistance);
        }

        [TestMethod]
        public void Hunters_AreTakenFromTheNonSeekers_AndHuntPlayers()
        {
            var goal = Goal(waveMix: false);
            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(4));

            var plan = planner.BuildPlan(Request(2, Pick(10), HoldWaveType.Hunters));
            var tags = WorldEventHoldWavePlanner.BuildTags(plan, 10, RunId, null, planner.Tuning);

            Assert.AreEqual(2, tags.Count(t => t.Hunter), "the default hunters count");
            Assert.IsTrue(tags.Where(t => t.Hunter).All(t => !t.Seeker && t.WaveRole == HoldRole.Hunter));
        }

        [TestMethod]
        public void Ranged_SlotWithoutARealRangedAttack_BecomesASeeker_AndACasterIsTunedAsOne()
        {
            var goal = Goal(waveMix: false);
            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(4));

            var plan = planner.BuildPlan(Request(2, Pick(4), HoldWaveType.Ranged));

            Assert.IsTrue(plan.ReplacedTrash.All(w => RangedPool.Contains(w)), "every slot drawn from the ranged pool");
            Assert.IsTrue(plan.HoldDistance >= 5);

            var archer = WorldEventHoldWavePlanner.BuildTag(plan, 4, 0, RunId, null, planner.Tuning, new WorldEventHoldWavePlanner.EntryAttack(true, false));
            var caster = WorldEventHoldWavePlanner.BuildTag(plan, 4, 1, RunId, null, planner.Tuning, new WorldEventHoldWavePlanner.EntryAttack(true, true));
            var dud = WorldEventHoldWavePlanner.BuildTag(plan, 4, 2, RunId, null, planner.Tuning, new WorldEventHoldWavePlanner.EntryAttack(false, false));

            Assert.AreEqual(HoldDamageRole.Ranged, archer.Role);
            Assert.AreEqual(plan.HoldDistance, archer.HoldDistance);
            Assert.AreEqual(HoldDamageRole.Caster, caster.Role);
            Assert.AreEqual(HoldRole.Seeker, dud.WaveRole);
            Assert.AreEqual(HoldDamageRole.Melee, dud.Role);
            Assert.AreEqual(0f, dud.HoldDistance, "a monster that cannot shoot is never held back");
        }

        [TestMethod]
        public void Mixed_AssaultWave_SendsEachCorruptedAtAKnight_TintedAndUnwarded()
        {
            var goal = Goal(waveMix: true, assaultEvery: 1);
            var tuning = Tuning(goal, k => k == HoldKeys.PropertyPrefix + HoldKeys.CorruptedTint ? "0x04001381" : null);
            var planner = new WorldEventHoldWavePlanner(goal, tuning, new Random(11));

            var plan = planner.BuildPlan(Request(2, Pick(12)));

            Assert.AreEqual(HoldWaveType.Mixed, plan.Type);
            Assert.IsTrue(plan.Assault);
            Assert.AreEqual(2, plan.Roles.Count(r => r == HoldRole.Corrupted), "corrupted_assault_count");
            Assert.AreEqual(2, plan.AssaultCalls.Count);

            var tags = WorldEventHoldWavePlanner.BuildTags(plan, 12, RunId, null, tuning);

            foreach (var i in Enumerable.Range(0, 12).Where(i => plan.Roles[i] == HoldRole.Corrupted))
            {
                Assert.IsTrue(tags[i].Corrupted);
                Assert.AreEqual(plan.CorruptedTargets[i], tags[i].ForcedTargetGuid);
                Assert.AreEqual(0x04001381u, tags[i].TintPalette);
                Assert.AreEqual(HoldDamageRole.Corrupted, tags[i].Role);
            }

            Assert.IsTrue(tags.Where(t => !t.Corrupted).All(t => t.TintPalette == 0));
        }

        [TestMethod]
        public void Mixed_WithNoRangedPool_HasNoRangedRole_AndWithNoKnights_NoBreachOrCorrupted()
        {
            var goal = Goal(waveMix: true);
            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(3));

            var request = new HoldWaveRequest { WaveNumber = 2, TargetWaves = 5, Pick = Pick(12), Knights = Array.Empty<HoldKnightSlot>() };
            var plan = planner.BuildPlan(request);

            Assert.IsFalse(plan.Roles.Contains(HoldRole.Ranged));
            Assert.IsFalse(plan.Roles.Contains(HoldRole.Breach));
            Assert.IsFalse(plan.Roles.Contains(HoldRole.Corrupted));
        }

        [TestMethod]
        public void Mixed_SeekersAvoidTheBreachTarget_WhichTheBreachSquadIsForcedOnto()
        {
            var goal = Goal(waveMix: true);
            var tuning = Tuning(goal, k => k == HoldKeys.PropertyPrefix + HoldKeys.MinBreach ? "2" : null);
            var planner = new WorldEventHoldWavePlanner(goal, tuning, new Random(6));

            var plan = planner.BuildPlan(Request(2, Pick(12)));
            var tags = WorldEventHoldWavePlanner.BuildTags(plan, 12, RunId, null, tuning);

            Assert.IsTrue(plan.Roles.Count(r => r == HoldRole.Breach) >= 2);
            Assert.IsTrue(tags.Where(t => t.WaveRole == HoldRole.Breach).All(t => t.ForcedTargetGuid == plan.BreachTargetGuid));
            Assert.IsTrue(tags.Where(t => t.WaveRole == HoldRole.Seeker).All(t => t.AvoidTargetGuid == plan.BreachTargetGuid));
        }

        [TestMethod]
        public void ElitesAndChampions_NeverTakeTheRangedRole()
        {
            var goal = Goal(waveMix: true);
            var tuning = Tuning(goal, k => k == HoldKeys.PropertyPrefix + HoldKeys.ShareRanged ? "1" : null);

            for (var seed = 0; seed < 20; seed++)
            {
                var planner = new WorldEventHoldWavePlanner(goal, tuning, new Random(seed));
                var pick = new WavePick(Enumerable.Repeat(6000u, 6).ToList(), new List<uint> { 7000 }, default, default, 2, false, null);

                var plan = planner.BuildPlan(Request(2, pick));

                Assert.AreNotEqual(HoldRole.Ranged, plan.Roles[2], "the elite slot keeps its monster");
                Assert.AreNotEqual(HoldRole.Ranged, plan.Roles[6], "the champion keeps its monster");
            }
        }

        [TestMethod]
        public void RoleLevelOffset_RedrawsThatRolesSlots_FromTheShiftedBand()
        {
            var goal = Goal(waveMix: false);
            var tuning = Tuning(goal, k => k == HoldKeys.PropertyPrefix + HoldKeys.Role("melee", HoldKeys.RoleLevelOffset) ? "20" : null);
            var planner = new WorldEventHoldWavePlanner(goal, tuning, new Random(1));

            var members = new List<FamilyMember>
            {
                new FamilyMember { Wcid = 6000, Level = 50, Role = 0 },
                new FamilyMember { Wcid = 6900, Level = 70, Role = 0 },
            };

            var pick = new WavePick(Enumerable.Repeat(6000u, 4).ToList(), new List<uint>(), default, new LevelBand(45, 55));
            var request = new HoldWaveRequest { WaveNumber = 1, TargetWaves = 5, Pick = pick, Knights = Knights, Members = members };

            var plan = planner.BuildPlan(request);

            Assert.IsNotNull(plan.LevelOffsetSlots);
            Assert.AreEqual(4, plan.LevelOffsetSlots.Count);
            Assert.IsTrue(plan.ReplacedTrash.All(w => w == 6900), "every melee slot redrawn from 65-75");
            Assert.IsTrue(plan.LevelOffsetSlots.Values.All(v => v == 20));
        }

        [TestMethod]
        public void SpecialWaves_NeverRepeatBackToBack_AndCountTowardTheMinimum()
        {
            var goal = Goal(waveMix: false);
            goal.Protect.SpecialWaveChance = 1.0;
            goal.Protect.SpecialWaveFromWave = 1;

            var planner = new WorldEventHoldWavePlanner(goal, Tuning(goal), new Random(8));
            var previous = HoldWaveType.Normal;

            for (var wave = 1; wave <= 4; wave++)
            {
                var plan = planner.BuildPlan(Request(wave, Pick(6)));

                Assert.AreNotEqual(HoldWaveType.Normal, plan.Type);
                Assert.AreNotEqual(previous, plan.Type, $"wave {wave}");
                previous = plan.Type;
            }

            Assert.AreEqual(4, planner.SpecialsSoFar);
        }
    }
}
