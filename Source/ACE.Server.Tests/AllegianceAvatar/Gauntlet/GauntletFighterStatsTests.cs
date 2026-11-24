using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Avatars.Gauntlet;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    /// <summary>
    /// GauntletFighterStats, the fighter's power from the wave plan and the Avatar level A (pure; spec "The Avatar fights",
    /// Power). Expected values are written out from the spec's formulas, and the skill and attribute targets are checked
    /// against the normalizer the spec names, so a change to either side shows here. No PropertyManager key is read: the
    /// tuning is GauntletTuning.Defaults or a record built here.
    /// </summary>
    [TestClass]
    public class GauntletFighterStatsTests
    {
        private static readonly GauntletTuning T = GauntletTuning.Defaults;

        [TestMethod]
        public void Defaults_AreTheSpecPlaceholders()
        {
            Assert.AreEqual(10.0, T.FighterHealthBase);
            Assert.AreEqual(0.04, T.FighterHealthPerLevel);
            Assert.AreEqual(1.0, T.FighterOffenseBase);
            Assert.AreEqual(0.0004, T.FighterOffensePerLevel);
            Assert.AreEqual(1.0, T.FighterDamageBase);
            Assert.AreEqual(0.004, T.FighterDamagePerLevel);
            Assert.AreEqual(20.0, T.FighterMinDeathSeconds);
            Assert.AreEqual(5.0, T.FighterBurstSeconds);
        }

        [TestMethod]
        public void Health_ScalesWithAvatarLevel()
        {
            // plan.HealthCurveTarget x (10 + 0.04 x A)
            Assert.AreEqual(10_000u, GauntletFighterStats.MaxHealth(1000, 0, T), "A = 0: x10");
            Assert.AreEqual(10_400u, GauntletFighterStats.MaxHealth(1000, 10, T), "A = 10: x10.4");
            Assert.AreEqual(13_000u, GauntletFighterStats.MaxHealth(1000, 75, T), "A = 75: x13");
            Assert.AreEqual(GauntletFighterStats.MaxHealth(1000, 0, T), GauntletFighterStats.MaxHealth(1000, -5, T), "a negative level reads as 0");

            Assert.AreEqual(0u, GauntletFighterStats.MaxHealth(0, 50, T), "no curve target: leave the weenie's health");
            Assert.AreEqual(0u, GauntletFighterStats.MaxHealth(double.NaN, 50, T));

            Assert.AreEqual((uint)T.MaxCreatureHealth, GauntletFighterStats.MaxHealth(1e12, 50, T), "held to max_creature_health");
            Assert.AreEqual(5_000u, GauntletFighterStats.MaxHealth(1e12, 50, T with { MaxCreatureHealth = 5000 }));

            var stamp = GauntletFighterStats.Compute(300, 1000, null, 0, 10, T);
            Assert.AreEqual(10_400u, stamp.MaxHealth, "the stamp carries it");
            Assert.AreEqual(300, stamp.Level, "the level shown is the wave's L(n)");
            Assert.AreEqual(10, stamp.AvatarLevel);
        }

        [TestMethod]
        public void Ratios_FromNormalizerTargets()
        {
            var standard = GauntletFighterFixtures.Standard();
            const int a = 250; // keeps the old ratios: offense 1 + 0.0004 x 250 = 1.1, damage 1 + 0.004 x 250 = 2
            var offense = 1.0 + 0.0004 * a;

            Assert.AreEqual(offense, GauntletFighterStats.OffenseRatio(a, T), 1e-12);
            Assert.AreEqual(1.0 + 0.004 * a, GauntletFighterStats.DamageRatio(a, T), 1e-12);

            var stamp = GauntletFighterStats.Compute(400, 1000, standard, 60, a, T);

            Assert.AreEqual(DungeonCombatNormalizer.AttackSkillTarget(standard, Skill.LightWeapons, offense), stamp.Skills[Skill.LightWeapons]);
            Assert.AreEqual(DungeonCombatNormalizer.DefenseSkillTarget(standard, Skill.MeleeDefense, offense), stamp.Skills[Skill.MeleeDefense]);
            Assert.AreEqual(DungeonCombatNormalizer.DefenseSkillTarget(standard, Skill.MissileDefense, offense), stamp.Skills[Skill.MissileDefense]);
            Assert.AreEqual(440u, stamp.Skills[Skill.LightWeapons], "400 x 1.1");
            Assert.AreEqual(330u, stamp.Skills[Skill.MeleeDefense], "300 x 1.1");

            // an attack school the band has no median for falls back to the highest attack median (the normalizer's rule)
            Assert.AreEqual(440u, stamp.Skills[Skill.WarMagic], "magic skills follow the same target");
            Assert.IsFalse(stamp.Skills.ContainsKey(Skill.MagicDefense), "no median, no target: left authored");

            Assert.AreEqual(DungeonCombatNormalizer.AttributeTarget(standard, PropertyAttribute.Strength, offense, offense), stamp.Attributes[PropertyAttribute.Strength]);
            Assert.AreEqual(220u, stamp.Attributes[PropertyAttribute.Strength], "200 x 1.1");
            Assert.AreEqual(198u, stamp.Attributes[PropertyAttribute.Endurance], "the defense attributes use the same ratio: 180 x 1.1");
            Assert.IsFalse(stamp.Attributes.ContainsKey(PropertyAttribute.Self));

            Assert.AreEqual(120u, stamp.DamageTarget, "plan.PerHitMedian 60 x (1 + 0.004 x 250)");
            Assert.AreEqual(180u, GauntletFighterStats.Compute(400, 1000, standard, 0, a, T).DamageTarget, "no per-hit median: the band's MaxBodyDamage 90 x 2");
            Assert.AreEqual(0u, GauntletFighterStats.Compute(400, 1000, null, 0, a, T).DamageTarget, "neither: left authored");

            var empty = GauntletFighterStats.Compute(400, 1000, DungeonBandStandard.Empty, 60, a, T);
            Assert.AreEqual(0, empty.Skills.Count, "an empty standard sets no skill");
            Assert.AreEqual(0, empty.Attributes.Count);

            Assert.AreEqual(T.FighterMinDeathSeconds, stamp.MinDeathSeconds);
            Assert.AreEqual(T.FighterBurstSeconds, stamp.BurstSeconds);
        }

        [TestMethod]
        public void Compute_FromThePlan_ReadsItsFields()
        {
            var plan = FighterRunHarness.Plan(3, GauntletFighterFixtures.Standard(), 2000, 50);
            var stamp = GauntletFighterStats.Compute(plan, 0, T);

            Assert.AreEqual(plan.Level, stamp.Level);
            Assert.AreEqual(20_000u, stamp.MaxHealth, "HealthCurveTarget 2000 x 10");
            Assert.AreEqual(50u, stamp.DamageTarget, "PerHitMedian 50 x 1");
            Assert.AreEqual(400u, stamp.Skills[Skill.LightWeapons], "the plan's BandStandard at ratio 1");

            Assert.IsNull(GauntletFighterStats.Compute((GauntletWavePlan)null, 10, T), "a refused wave: no stamp, the fighter keeps its stats");
        }

        [TestMethod]
        public void Restamp_KeepsHealthFraction()
        {
            Assert.AreEqual(5_000u, GauntletFighterStats.KeptHealth(500, 1000, 10_000), "half stays half");
            Assert.AreEqual(10_000u, GauntletFighterStats.KeptHealth(1000, 1000, 10_000), "full stays full");
            Assert.AreEqual(250u, GauntletFighterStats.KeptHealth(2500, 10_000, 1000), "a smaller maximum keeps the fraction too");
            Assert.AreEqual(1u, GauntletFighterStats.KeptHealth(1, 1_000_000, 100), "a living fighter is never stamped to 0");
            Assert.AreEqual(0u, GauntletFighterStats.KeptHealth(0, 1000, 10_000), "a dead one stays at 0");
            Assert.AreEqual(10_000u, GauntletFighterStats.KeptHealth(0, 0, 10_000), "no old maximum: full");
            Assert.AreEqual(10_000u, GauntletFighterStats.KeptHealth(5000, 1000, 10_000), "never above the new maximum");
        }
    }
}
