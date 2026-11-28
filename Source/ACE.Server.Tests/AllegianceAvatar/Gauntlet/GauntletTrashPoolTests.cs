using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Avatars.Gauntlet;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// Owner ruling 2026-11-26: the Gauntlet's total trash health scales with headcount the way Threads do, so each player carries
    /// the same load at any group size. The pool is count(H) x per-trash multiplier; it must be E x the solo pool, and per player
    /// it must not move. The pure rules, the planner's rewrite of the plan's multiplier (the value ShapeCreature reads) and the
    /// shaped creature itself (GauntletWavePlannerTests.Level500Wave_ShapedHealth_EqualsThreadFixture) are pinned separately.
    /// Reads no PropertyManager: every tuning and group is hand-built.
    /// </summary>
    [TestClass]
    public class GauntletTrashPoolTests
    {
        private static readonly int[] Headcounts = { 1, 2, 5, 10, 20, 50, 61 };

        private static GroupScaling Group(int h) => GroupScaling.Compute(h, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare,
            GroupScaling.DefaultGroupMaxMonsters, GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap,
            GroupScaling.DefaultRewardBonusPerMember, GroupScaling.DefaultRewardBonusCap, 1.0);

        /// <summary>The plan BuildLivePlan hands back for a group: the Thread's H = E / C_thread already folded into the multiplier (DungeonPopulationBuilder.cs:2576-2577).</summary>
        private static DungeonSpawnPlan ThreadPlan(GroupScaling g, double baseMult, out double threadH)
        {
            var c = g.IsGroup ? g.CountTarget : 1.0;
            threadH = g.IsGroup ? GroupScaling.HealthMultFor(g.Effort, c) : 1.0;
            return new DungeonSpawnPlan { HealthCurveTarget = 50000, HealthMultiplier = baseMult * threadH, BossHealthMultiplier = 3.0 * g.Effort, GroupHealthMult = threadH };
        }

        [TestMethod]
        public void Pure_PoolPerPlayer_IsConstant_AtEveryHeadcount_PlainAndBossWave()
        {
            var rules = new GauntletRules(GauntletTuning.Defaults);

            foreach (var boss in new[] { false, true })
            {
                var solo = rules.TrashCount(1, boss);
                Assert.IsTrue(solo > 0, "positive control: a solo wave has trash");

                foreach (var h in Headcounts)
                {
                    var e = GroupScaling.EffortFor(h, GroupScaling.DefaultEffortPerMember);
                    var pool = rules.TrashCount(h, boss) * rules.TrashHealthMultiplier(h, boss, e);

                    Assert.AreEqual(solo, pool / e, 1e-9, $"H{h} boss={boss}: pool is E x the solo pool");
                    Assert.AreEqual(solo, pool / h, 1e-9, $"H{h} boss={boss}: pool per player is the solo pool (E = H at 1.0 per member)");
                }
            }
        }

        [TestMethod]
        public void Pure_BeyondMaxAlive_TheMultiplierRisesToCompensate()
        {
            var rules = new GauntletRules(GauntletTuning.Sanitized(GauntletTuning.Defaults with { MaxAlive = 30 }));

            Assert.AreEqual(30, rules.TrashCount(50, false), "control: the count is capped");
            Assert.AreEqual(30, rules.TrashCount(61, false));

            var below = rules.TrashHealthMultiplier(10, false, 10.0);
            var capped = rules.TrashHealthMultiplier(50, false, 50.0);

            Assert.AreEqual(10.0 * 10 / 28, below, 1e-9, "H10: count 28, under the cap");
            Assert.AreEqual(50.0 * 10 / 30, capped, 1e-9, "H50: count held at 30, so the multiplier carries the rest");
            Assert.AreEqual(10 * 50.0, rules.TrashCount(50, false) * capped, 1e-9, "the pool is still E x solo");
        }

        [TestMethod]
        public void Pure_SoloAndDegenerateInputs()
        {
            var rules = new GauntletRules(GauntletTuning.Defaults);

            Assert.AreEqual(1.0, rules.TrashHealthMultiplier(1, false, 1.0), 1e-12, "solo is exactly 1");
            Assert.AreEqual(1.0, rules.TrashHealthMultiplier(1, true, double.NaN), 1e-12, "a non-finite effort reads as 1");
            Assert.AreEqual(1.0, rules.TrashHealthMultiplier(1, true, 0.2), 1e-12, "a sub-1 effort reads as 1");

            var none = new GauntletRules(GauntletTuning.Sanitized(GauntletTuning.Defaults with { BossWaveTrashFraction = 0.0 }));
            Assert.AreEqual(0, none.TrashCount(20, true), "control: a boss wave with no trash");
            Assert.AreEqual(20.0, none.TrashHealthMultiplier(20, true, 20.0), 1e-12, "no pool to hold: reads as E, never divides by zero");
        }

        [TestMethod]
        public void Planner_RewritesTheMultiplierTheSpawnerReads_PoolPerPlayerConstant()
        {
            foreach (var boss in new[] { false, true })
            {
                var wave = boss ? 5 : 2;
                var perPlayer = new List<double>();
                var rules0 = new GauntletRules(GauntletTuning.Defaults);

                foreach (var h in Headcounts)
                {
                    var g = Group(h);
                    var plan = ThreadPlan(g, 1.5, out _);
                    var planner = new GauntletWavePlanner(GauntletTuning.Defaults, (DungeonGemSpec spec, DungeonEntryDef site, GroupScaling grp,
                        Func<DungeonPopulationLimits, DungeonPopulationLimits> adjust, out ThreadDungeonSpawner.LivePlanBuildStats stats) => { stats = default; return plan; }, x => Group(x));
                    var result = planner.Plan(GauntletFixtureSites.Site(GauntletFixtureSites.SixPortalRow()), wave, h, 90210, "pool");

                    Assert.AreEqual(boss, result.IsBoss);
                    Assert.IsTrue(result.TrashTarget > 0, "positive control: trash to price");

                    var basePerCreature = plan.HealthCurveTarget * 1.5;
                    var pool = result.TrashTarget * plan.HealthCurveTarget * plan.HealthMultiplier;
                    var soloPool = new GauntletRules(GauntletTuning.Defaults).TrashCount(1, boss) * basePerCreature;

                    Assert.AreEqual(soloPool * g.Effort, pool, soloPool * 1e-9, $"H{h} boss={boss}: the pool the spawner will build is E x solo");
                    Assert.AreEqual(plan.HealthCurveTarget * plan.HealthMultiplier, result.UnspawnedHealthEstimate[DungeonRole.Trash], 1e-6, "the score's unspawned estimate reads the same multiplier");
                    Assert.AreEqual(rules0.TrashHealthMultiplier(h, boss, g.Effort), plan.GroupHealthMult, 1e-12, "GroupHealthMult follows the rewrite (the boss floor reads E / H)");
                    Assert.AreEqual(3.0 * g.Effort, plan.BossHealthMultiplier, 1e-9, "the boss multiplier is untouched");
                    perPlayer.Add(pool / h);
                }

                Assert.IsTrue(perPlayer.Max() - perPlayer.Min() < perPlayer.Max() * 1e-9, $"boss={boss}: pool per player is the same at every headcount");
            }
        }

        [TestMethod]
        public void Planner_TrashRewardFactor_FollowsTheNewHealth_BossFactorUnchanged()
        {
            var rules = new GauntletRules(GauntletTuning.Defaults);

            foreach (var h in Headcounts.Where(x => x > 1))
            {
                var g = Group(h);
                var plan = ThreadPlan(g, 1.5, out var threadH);
                plan.GroupTrashRewardFactor = g.TrashRewardFactor(threadH);
                plan.GroupBossRewardFactor = g.BossRewardFactor();
                var builderTrash = plan.GroupTrashRewardFactor;
                var boss = plan.GroupBossRewardFactor;

                // The consumer: the spawner prices XpOverride and luminance through these two, with the scale and the modifier products neutral.
                plan.GroupRosterSize = h;
                plan.XpScale = 1.0;
                plan.XpMultiplier = 1.0;
                plan.LumScale = 1.0;
                plan.LumMultiplier = 1.0;

                GauntletWavePlanner.ApplyHeadcountHealth(plan, g, rules, h, false);

                var gauntletH = rules.TrashHealthMultiplier(h, false, g.Effort);
                Assert.AreEqual((long)Math.Round(1e6 * g.TrashRewardFactor(gauntletH)), DungeonPopulationBuilder.XpFor(plan, 1_000_000, DungeonRole.Trash),
                    $"H{h}: the XP a trash kill awards carries the Gauntlet H");
                // lumBase = round(1e6 / 4000 / 50) x 50 = 250 at a trash rate of 1 and a neutral scale
                Assert.AreEqual((int)Math.Round(250 * g.TrashRewardFactor(gauntletH), MidpointRounding.AwayFromZero), DungeonPopulationBuilder.LuminanceFor(plan, 1_000_000, 200, DungeonRole.Trash),
                    $"H{h}: and so does the luminance");
                Assert.AreEqual(gauntletH * g.RewardBonus / g.ShareTotal, plan.GroupTrashRewardFactor, 1e-12, $"H{h}: H x B / T with the Gauntlet H");
                Assert.AreEqual(g.TrashRewardFactor(gauntletH), plan.GroupTrashRewardFactor, 1e-12, $"H{h}: the group's own formula");
                Assert.AreEqual(boss, plan.GroupBossRewardFactor, 0.0, $"H{h}: the boss factor (E x B / T) does not read H");

                if (h == 5 || h == 50)
                    Assert.AreNotEqual(builderTrash, plan.GroupTrashRewardFactor, 1e-6, $"H{h}: differs from the builder's Thread value");
            }
        }

        [TestMethod]
        public void Planner_SoloPlan_IsUntouched()
        {
            var g = Group(1);
            var plan = ThreadPlan(g, 1.5, out _);
            GauntletWavePlanner.ApplyHeadcountHealth(plan, g, new GauntletRules(GauntletTuning.Defaults), 1, false);

            Assert.AreEqual(1.5, plan.HealthMultiplier, 1e-12);
            Assert.AreEqual(1.0, plan.GroupHealthMult, 1e-12);
            Assert.AreEqual(1.0, plan.GroupTrashRewardFactor, 0.0, "a solo plan keeps the neutral reward factor");
        }

        /// <summary>
        /// The control: master's multiplier was the Thread's E / C_actual. Live plans cap C_actual (3.6 on the shipped outdoor site for
        /// H of 10 and up, per the #2376 review's live BuildLivePlan run at wave 13), so master's pool against a solo of 10 was
        /// 16 / 30 / 77.8 / 266.7 / 1500 / 2033 at H 2 / 5 / 10 / 20 / 50 / 61. The new rule must differ from it, and
        /// equal E x solo, so the pins above can fail.
        /// </summary>
        [TestMethod]
        public void Control_TheOldThreadMultiplier_DoesNotHoldThePool()
        {
            var rules = new GauntletRules(GauntletTuning.Defaults);
            var live = new Dictionary<int, (double CActual, double MasterPool)> { [20] = (3.6, 266.7), [50] = (3.6, 1500.0) };

            foreach (var (h, (cActual, masterPool)) in live)
            {
                var e = GroupScaling.EffortFor(h, GroupScaling.DefaultEffortPerMember);
                var oldPool = rules.TrashCount(h, false) * GroupScaling.HealthMultFor(e, cActual);
                var newPool = rules.TrashCount(h, false) * rules.TrashHealthMultiplier(h, false, e);

                Assert.AreEqual(masterPool, oldPool, 0.1, $"H{h}: reproduces master's live pool");
                Assert.AreEqual(10.0 * e, newPool, 1e-9, $"H{h}: the new pool");
                Assert.AreNotEqual(oldPool, newPool, 1.0, $"H{h}: the old multiplier does not hold the pool");
            }
        }
    }
}
