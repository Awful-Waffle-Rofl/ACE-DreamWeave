using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Character Details baseline (ThreadBaseline): ONE band-standard derivation shared with the plan
    /// (DungeonPopulationBuilder.ThreadsStandardAt == plan.BandStandard), no level clamp, and the three runtime ceilings
    /// a spawned non-boss gets. Runs against the SHIPPED roster (ShippedRosterFixture), whose pivot is the real one.
    /// </summary>
    [TestClass]
    public class ThreadBaselineTests
    {
        internal static DungeonPopulationLimits Limits(bool statCurve = DungeonPopulationLimits.DefaultStatCurve, bool reachUp = DungeonPopulationLimits.DefaultReachUp)
            => new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, reachUp: reachUp, statCurve: statCurve);

        /// <summary>A synthetic profile over the shipped roster's levels (ThreadsRunCeilingTests' shape plus magic D and crit resist).</summary>
        internal static DungeonStatProfile Profile(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return DungeonStatProfile.Empty;

            var l = (uint)level;
            var attrs = new Dictionary<PropertyAttribute, uint>
            {
                [PropertyAttribute.Strength] = l, [PropertyAttribute.Endurance] = l, [PropertyAttribute.Quickness] = l,
                [PropertyAttribute.Coordination] = l, [PropertyAttribute.Focus] = l, [PropertyAttribute.Self] = l,
            };

            return new DungeonStatProfile(level, 0,
                new Dictionary<Skill, uint>
                {
                    [Skill.MeleeDefense] = 2 * l, [Skill.MissileDefense] = 2 * l, [Skill.MagicDefense] = 2 * l, [Skill.HeavyWeapons] = 2 * l,
                },
                l, 2 * l, 0, 0.0, attrs, critResistRating: wcid % 7 == 0 ? 10u : 0u);
        }

        [TestInitialize]
        public void Reset()
        {
            TestGameTables.EnsureInitialized();
            ThreadPlanCache.Invalidate();
        }

        private static DungeonSpawnPlan Plan(int level, DungeonPopulationLimits limits)
            => DungeonPopulationBuilder.Build(new DungeonGemSpec("filos_doom", level, 6, "any", 1, null, 0, 0), ShippedRosterFixture.Dungeon(30),
                ThreadDungeonStoreTests.LoadShipped(), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, limits,
                new Random(1), Profile);

        private static string Fingerprint(DungeonBandStandard s)
        {
            string Dict<TK>(IReadOnlyDictionary<TK, uint> d) => string.Join(",", d.OrderBy(k => k.Key.ToString()).Select(k => $"{k.Key}={k.Value}"));

            return $"n={s.SampleCount} low={s.SampleLowRatio:R} dmg={s.MaxBodyDamage} armor={s.MaxBaseArmor} tier={s.SpellTier} hr={s.HealthRate:R} "
                 + $"crr={s.CritResistRatingMedian} skills[{Dict(s.SkillMedians)}] eff[{Dict(s.EffectiveSkillMedians)}] attr[{Dict(s.AttributeMedians)}]";
        }

        [TestMethod]
        public void ThreadsStandardAt_EqualsThePlansBandStandard_AtEveryLevel()
        {
            Assert.IsNotNull(ShippedRosterFixture.Tables, "Content/events/axes/species not found from the test output directory");

            foreach (var limits in new[] { Limits(), Limits(statCurve: false), Limits(reachUp: false) })
            {
                foreach (var level in new[] { 250, 310, 400, 800, 2000 })
                {
                    var plan = Plan(level, limits);
                    var direct = DungeonPopulationBuilder.ThreadsStandardAt(level, limits, ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, Profile);

                    Assert.AreEqual(Fingerprint(plan.BandStandard), Fingerprint(direct), $"level {level} statCurve {limits.StatCurve} reachUp {limits.ReachUp}");
                }
            }

            // positive control: the standard is real, and the curve really moves it above the pivot
            var at400 = DungeonPopulationBuilder.ThreadsStandardAt(400, Limits(), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, Profile);
            var at2000 = DungeonPopulationBuilder.ThreadsStandardAt(2000, Limits(), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, Profile);
            Assert.IsFalse(at400.IsEmpty);
            Assert.IsTrue(at2000.EffectiveSkillMedianFor(Skill.MeleeDefense) > at400.EffectiveSkillMedianFor(Skill.MeleeDefense), "no level clamp: 2000 sits above 400");
        }

        [TestMethod]
        public void At2000_EveryFieldIsFinite_AndTheMissileCeilingBinds()
        {
            var limits = Limits();
            var standard = DungeonPopulationBuilder.ThreadsStandardAt(2000, limits, ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, Profile);
            var baseline = ThreadBaselineCreature.FromStandard(2000, standard, limits);

            Assert.IsNotNull(baseline);
            Assert.IsTrue(standard.EffectiveSkillMedianFor(Skill.MissileDefense) > 720, "positive control: the uncapped median is above the cap");
            Assert.AreEqual(720u, baseline.MissileDefense, "dynamic_dungeons_missile_defense_skill_cap binds");
            Assert.IsTrue(baseline.MeleeDefense > 0 && baseline.MeleeDefense < uint.MaxValue);
            // Magic defense has no EFFECTIVE median here: TestGameTables carries no Magic Defense formula row, so the
            // sample skips it (DungeonBandStandard.EffectiveSkillMediansOf). Its InitLevel median is on the retail curve.
            Assert.IsTrue(standard.MedianFor(Skill.MagicDefense) > 0 && standard.MedianFor(Skill.MagicDefense) < uint.MaxValue);
            Assert.IsTrue(double.IsFinite(standard.HealthRate) && double.IsFinite(standard.SampleLowRatio));
        }

        [TestMethod]
        public void FromStandard_AppliesTheThreeCeilings_AndPicksTheBestSchoolPerCategory()
        {
            var standard = DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 100, 100,
                effectiveSkillMedians: new Dictionary<Skill, uint>
                {
                    [Skill.MeleeDefense] = 400, [Skill.MissileDefense] = 900, [Skill.MagicDefense] = 450,
                    [Skill.HeavyWeapons] = 380, [Skill.UnarmedCombat] = 410,
                    [Skill.MissileWeapons] = 300, [Skill.Bow] = 320,
                    [Skill.WarMagic] = 700, [Skill.LifeMagic] = 600,
                },
                critResistRatingMedian: 12);

            var baseline = ThreadBaselineCreature.FromStandard(300, standard, Limits());

            Assert.AreEqual(400u, baseline.MeleeDefense, "the median + 100 defense ceiling does not bind on the median itself");
            Assert.AreEqual(720u, baseline.MissileDefense, "missile defense ceiling (720)");
            Assert.AreEqual(450u, baseline.MagicDefense);
            Assert.AreEqual(410u, baseline.MeleeAttack, "the best melee school");
            Assert.AreEqual(320u, baseline.MissileAttack, "the best missile school");
            Assert.AreEqual(490u, baseline.MagicAttack, "magic attack ceiling (490)");
            Assert.AreEqual(12, baseline.CritResistRating);

            // a cap of 0 is off on both dials
            var off = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, magicAttackSkillCap: 0, missileDefenseSkillCap: 0);
            var uncapped = ThreadBaselineCreature.FromStandard(300, standard, off);
            Assert.AreEqual(900u, uncapped.MissileDefense);
            Assert.AreEqual(700u, uncapped.MagicAttack);

            Assert.IsNull(ThreadBaselineCreature.FromStandard(300, DungeonBandStandard.Empty, Limits()), "an empty standard is no baseline");
        }

        [TestMethod]
        public void Memo_ServesTheSameBaseline_AndThePlanCacheInvalidateClearsIt()
        {
            var limits = Limits();
            var first = ThreadBaseline.For(320, limits, ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, Profile);
            var second = ThreadBaseline.For(320, limits, ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, Profile);

            Assert.IsNotNull(first);
            Assert.AreSame(first, second);
            Assert.IsTrue(ThreadBaseline.Count >= 1);

            ThreadPlanCache.Invalidate();
            Assert.AreEqual(0, ThreadBaseline.Count);

            // a changed dial is a different key, not a stale hit
            var noCap = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, missileDefenseSkillCap: 0);
            Assert.AreNotEqual(ThreadBaseline.DialsKey(limits), ThreadBaseline.DialsKey(noCap));
        }
    }
}
