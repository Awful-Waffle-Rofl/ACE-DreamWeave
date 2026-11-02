using System;
using System.Linq;

using ACE.Server.Pinnacles;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    [TestClass]
    public class PinnacleScalingTests
    {
        private static string Src() => PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleScaling.cs");

        [TestMethod]
        public void BothMemoCalls_SitInsideTheGate_AndTheCreatureWriteDoesNot()
        {
            var src = Src();
            var band = PooledLootSourceText.MethodBody(src, "private static (DungeonBandStandard Standard, uint Median) BandFor(int knotLevel)");

            var gate = band.IndexOf("lock (MemoGate)", StringComparison.Ordinal);
            var standard = band.IndexOf("WorldEventBandMemo.StandardFor(", StringComparison.Ordinal);
            var median = band.IndexOf("WorldEventBandMemo.MedianHealthFor(", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0 && gate < standard && gate < median, "both memo calls follow the lock");

            // the only memo calls in the whole file are the two in BandFor, and there is one gate
            Assert.AreEqual(1, Count(src, "WorldEventBandMemo.StandardFor("));
            Assert.AreEqual(1, Count(src, "WorldEventBandMemo.MedianHealthFor("));
            Assert.AreEqual(1, Count(src, "lock (MemoGate)"));

            var apply = PooledLootSourceText.MethodBody(src, "public static bool Apply(Creature creature, int knotLevel, PinnacleTier tier)");
            Assert.IsFalse(apply.Contains("lock ("), "BandUplift.Apply runs outside the lock");
            StringAssert.Contains(apply, "BandFor(knotLevel)");
        }

        [TestMethod]
        public void LootGateLevel_IsRecordedBeforeTheUpliftRaisesTheLevel()
        {
            var apply = PooledLootSourceText.MethodBody(Src(), "public static bool Apply(Creature creature, int knotLevel, PinnacleTier tier)");

            var gate = apply.IndexOf("creature.LootGateLevel = gate.Value", StringComparison.Ordinal);
            var uplift = apply.IndexOf("BandUplift.Apply(creature, knotLevel, standard)", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0 && gate < uplift);
        }

        [TestMethod]
        public void LootGateFor_IsTheAuthoredLevelOnlyWhenTheUpliftRaisesIt()
        {
            Assert.AreEqual(100, PinnacleScaling.LootGateFor(100, 200), "raised: remember the authored level");
            Assert.IsNull(PinnacleScaling.LootGateFor(250, 200), "already above the Knot level: untouched");
            Assert.IsNull(PinnacleScaling.LootGateFor(200, 200), "equal: untouched");
        }

        // ---- C6: wielded weapons follow the body-part damage ratio ----

        private static System.Collections.Generic.Dictionary<ACE.Entity.Enum.CombatBodyPart, ACE.Entity.Models.PropertiesBodyPart> Parts(params int[] dvals)
        {
            var parts = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.CombatBodyPart, ACE.Entity.Models.PropertiesBodyPart>();

            for (var i = 0; i < dvals.Length; i++)
                parts[(ACE.Entity.Enum.CombatBodyPart)i] = new ACE.Entity.Models.PropertiesBodyPart { DVal = dvals[i] };

            return parts;
        }

        [TestMethod]
        public void BodyDamageRatio_IsTheStandardOverTheOwnMaximumPart_UpwardOnly()
        {
            Assert.AreEqual(2.0, PinnacleScaling.BodyDamageRatio(Parts(10, 40, 25), 80), 1e-9, "max part 40 against 80");
            Assert.AreEqual(1.0, PinnacleScaling.BodyDamageRatio(Parts(10, 90), 80), 1e-9, "already above: never lowered");
            Assert.AreEqual(1.0, PinnacleScaling.BodyDamageRatio(Parts(), 80), 1e-9, "no parts");
            Assert.AreEqual(1.0, PinnacleScaling.BodyDamageRatio(null, 80), 1e-9);
            Assert.AreEqual(1.0, PinnacleScaling.BodyDamageRatio(Parts(40), 0), 1e-9, "no standard");
        }

        [TestMethod]
        public void BodyDamageRatio_MatchesTheRatioTheBodyPartUpliftApplies()
        {
            // the weapon ratio is the one the parts get: the uplift scales the max part to exactly the standard
            var parts = Parts(12, 30, 18);
            var ratio = PinnacleScaling.BodyDamageRatio(parts, 75);

            Assert.AreEqual(75, PinnacleScaling.ScaledWeaponDamage(30, ratio), "the max part lands on the standard");
            Assert.AreEqual(30, PinnacleScaling.ScaledWeaponDamage(12, ratio));
            Assert.AreEqual(45, PinnacleScaling.ScaledWeaponDamage(18, ratio));
        }

        [TestMethod]
        public void ScaledWeaponDamage_RoundsAndNeverLowers()
        {
            Assert.AreEqual(50, PinnacleScaling.ScaledWeaponDamage(20, 2.5));
            Assert.AreEqual(20, PinnacleScaling.ScaledWeaponDamage(20, 1.0));
            Assert.AreEqual(20, PinnacleScaling.ScaledWeaponDamage(20, 0.5));
            Assert.AreEqual(0, PinnacleScaling.ScaledWeaponDamage(0, 3.0), "a weapon with no Damage stays at zero");
            Assert.AreEqual(int.MaxValue, PinnacleScaling.ScaledWeaponDamage(int.MaxValue / 2, 3.0));
        }

        [TestMethod]
        public void Apply_ReadsTheRatioBeforeTheUplift_AndScalesWeaponsWithIt_SkippingLaunchers()
        {
            var src = Src();
            var apply = PooledLootSourceText.MethodBody(src, "public static bool Apply(Creature creature, int knotLevel, PinnacleTier tier)");

            var ratio = apply.IndexOf("BodyDamageRatio(creature.Biota?.PropertiesBodyPart, standard.MaxBodyDamage)", StringComparison.Ordinal);
            var uplift = apply.IndexOf("BandUplift.Apply(creature, knotLevel, standard)", StringComparison.Ordinal);
            var weapons = apply.IndexOf("ScaleWieldedWeapons(creature, damageRatio)", StringComparison.Ordinal);

            Assert.IsTrue(ratio >= 0 && ratio < uplift && uplift < weapons, "ratio, uplift, then weapons");

            var scale = PooledLootSourceText.MethodBody(src, "private static int ScaleWieldedWeapons(");
            StringAssert.Contains(scale, "if (item is MissileLauncher)");
            StringAssert.Contains(scale, "item.SetProperty(PropertyInt.Damage, next)");
        }

        // ---- E2: tiers ----

        private const double Low = DungeonHealthCurve.DefaultAnchorLow;
        private const double High = DungeonHealthCurve.DefaultAnchorHigh;
        private const long Top = DungeonHealthCurve.DefaultTopLevel;

        private static uint Health(PinnacleTier tier, int level) => PinnacleScaling.TierHealth(tier, level, Low, High, Top);

        [TestMethod]
        public void TierMultipliers_AreTheOwnerRulings()
        {
            Assert.AreEqual(1.5, PinnacleScaling.EliteHealthCurveMultiplier);
            Assert.AreEqual(3.0, PinnacleScaling.BossHealthCurveMultiplier);
            Assert.AreEqual(1.5, PinnacleScaling.HeadHealthOverBossMultiplier);

            Assert.AreEqual(1.5, PinnacleScaling.TierHealthFactor(PinnacleTier.Elite), 1e-12);
            Assert.AreEqual(3.0, PinnacleScaling.TierHealthFactor(PinnacleTier.Boss), 1e-12);
            Assert.AreEqual(4.5, PinnacleScaling.TierHealthFactor(PinnacleTier.Head), 1e-12);
        }

        [TestMethod]
        public void TierHealth_AtL200_IsTheMultipleOfTheThreadsCurve()
        {
            var t = DungeonHealthCurve.Target(200, Low, High, Top);
            Assert.AreEqual(1100.5, t, 0.01, "T(200) on the shipped anchors");

            Assert.AreEqual((uint)Math.Round(1.5 * t), Health(PinnacleTier.Elite, 200));
            Assert.AreEqual((uint)Math.Round(3.0 * t), Health(PinnacleTier.Boss, 200));
            Assert.AreEqual((uint)Math.Round(4.5 * t), Health(PinnacleTier.Head, 200));

            Assert.AreEqual(1651u, Health(PinnacleTier.Elite, 200));
            Assert.AreEqual(3302u, Health(PinnacleTier.Boss, 200));
            Assert.AreEqual(4952u, Health(PinnacleTier.Head, 200));
        }

        [TestMethod]
        public void TierHealth_AtL375_IsTheMultipleOfTheHighAnchor()
        {
            Assert.AreEqual(13100.0, DungeonHealthCurve.Target(375, Low, High, Top));

            Assert.AreEqual(19650u, Health(PinnacleTier.Elite, 375));
            Assert.AreEqual(39300u, Health(PinnacleTier.Boss, 375));
            Assert.AreEqual(58950u, Health(PinnacleTier.Head, 375));
        }

        [TestMethod]
        public void TierHealth_FollowsTheLiveAnchors()
        {
            // the anchors are parameters (read live by CurveDials), not baked in
            Assert.AreEqual(3000u, PinnacleScaling.TierHealth(PinnacleTier.Elite, 185, 2000, High, Top), "T(185) is the low anchor");
            Assert.AreEqual(3u * 20000u, PinnacleScaling.TierHealth(PinnacleTier.Boss, 375, Low, 20000, Top), "T(375) is the high anchor");
        }

        [TestMethod]
        public void HealthCurveDials_ReadTheThreadsKeys_WithTheThreadsDefaults()
        {
            // source-level, so no PropertyManager read happens in the test
            var spawner = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs");
            StringAssert.Contains(spawner, "\"" + PinnacleTunables.HealthCurveAnchorLowKey + "\"");
            StringAssert.Contains(spawner, "\"" + PinnacleTunables.HealthCurveAnchorHighKey + "\"");
            StringAssert.Contains(spawner, "\"" + PinnacleTunables.HealthCurveTopLevelKey + "\"");

            var dials = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/Pinnacles/PinnacleTunables.cs"), "public static (double Low, double High, long Top) HealthCurveDials()");
            StringAssert.Contains(dials, "GetDouble(HealthCurveAnchorLowKey, ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorLow)");
            StringAssert.Contains(dials, "GetDouble(HealthCurveAnchorHighKey, ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorHigh)");
            StringAssert.Contains(dials, "GetLong(HealthCurveTopLevelKey, ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveTopLevel)");
            StringAssert.Contains(dials, "catch (Exception)");

            StringAssert.Contains(Src(), "=> ApplyTier(creature, knotLevel, tier, standard, PinnacleTunables.HealthCurveDials());");
        }

        [TestMethod]
        public void TierHealth_KnotLevelZeroOrBelow_LeavesHealthAuthored()
        {
            foreach (var tier in new[] { PinnacleTier.Elite, PinnacleTier.Boss, PinnacleTier.Head })
            {
                Assert.AreEqual(0u, Health(tier, 0), "0 = leave it");
                Assert.AreEqual(0u, Health(tier, -5));
            }
        }

        [TestMethod]
        public void Apply_KnotLevelZeroOrBelow_ReturnsBeforeTouchingTheCreature()
        {
            // L <= 0: no scaling at all, the creature spawns as authored (and a null creature is refused first)
            Assert.IsFalse(PinnacleScaling.Apply(null, 300, PinnacleTier.Boss));

            var apply = PooledLootSourceText.MethodBody(Src(), "public static bool Apply(Creature creature, int knotLevel, PinnacleTier tier)");
            const string guardText = "if (creature == null || knotLevel <= 0)";
            var guard = apply.IndexOf(guardText, StringComparison.Ordinal);
            var refuse = apply.IndexOf("return false;", guard, StringComparison.Ordinal);
            var first = apply.IndexOf("creature.", guard + guardText.Length, StringComparison.Ordinal);

            Assert.IsTrue(guard >= 0 && refuse > guard && first > refuse, "the guard returns before any creature read or write");
        }

        [TestMethod]
        public void TierLevel_EliteIsTheKnotLevel_BossAndHeadTheThreadsBossStamp()
        {
            Assert.AreEqual(200, PinnacleScaling.TierLevel(PinnacleTier.Elite, 200));
            Assert.AreEqual(375, PinnacleScaling.TierLevel(PinnacleTier.Elite, 375));

            Assert.AreEqual(220, PinnacleScaling.TierLevel(PinnacleTier.Boss, 200), "ceil(200 x 1.10)");
            Assert.AreEqual(220, PinnacleScaling.TierLevel(PinnacleTier.Head, 200));
            Assert.AreEqual(413, PinnacleScaling.TierLevel(PinnacleTier.Boss, 375), "ceil(375 x 1.10) = ceil(412.5)");
            Assert.AreEqual(110, PinnacleScaling.TierLevel(PinnacleTier.Boss, 100), "100 x 1.10 floats a hair above 110; the guard keeps it 110");
            Assert.AreEqual(2, PinnacleScaling.TierLevel(PinnacleTier.Boss, 1), "never below L + 1");

            Assert.AreEqual(1.10, DungeonPopulationLimits.DefaultBossLevelMargin, "the Threads boss margin this stamp reuses");
        }

        [TestMethod]
        public void BossRatings_AreTheThreadsBossFloors()
        {
            Assert.AreEqual(30, PinnacleScaling.BossDamageRating);
            Assert.AreEqual(15, PinnacleScaling.BossDamageResistRating);

            // a Threads retune shows up here rather than silently diverging
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossDamageRatingFloor, PinnacleScaling.BossDamageRating);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossDamageResistFloor, PinnacleScaling.BossDamageResistRating);
        }

        [TestMethod]
        public void BossPlan_CarriesTheStandardAndTheThreadsDefaultRatios()
        {
            var standard = DungeonBandStandard.ForTest(null, 80, 100);
            Assert.IsFalse(standard.IsEmpty);
            var plan = PinnacleScaling.BossPlan(standard);

            Assert.AreSame(standard, plan.BandStandard);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossOffenseBandRatio, plan.BossOffenseBandRatio);
            Assert.AreEqual(DungeonPopulationLimits.DefaultBossDefenseBandRatio, plan.BossDefenseBandRatio);
            Assert.AreEqual(1.0, plan.BossOffenseBandRatio);
            Assert.AreEqual(1.0, plan.BossDefenseBandRatio);

            Assert.AreSame(DungeonBandStandard.Empty, PinnacleScaling.BossPlan(null).BandStandard, "null reads as empty: NormalizeBoss no-ops");
        }

        [TestMethod]
        public void NormalizeBoss_ReadsOnlyThePlanFieldsBossPlanSets()
        {
            // The synthetic plan is safe only while NormalizeBoss (and the weapon half it calls) and NormalizeBossAttributes
            // read nothing else. A Threads change that reads another plan field fails HERE, not silently on a default.
            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DungeonCreatureNormalizer.cs");
            var boss = PooledLootSourceText.MethodBody(src, "internal static (int SkillsSet, int PartsChanged, bool RegenSet, int WeaponsChanged) NormalizeBoss(Creature creature, DungeonSpawnPlan plan)");
            var weapons = PooledLootSourceText.MethodBody(src, "internal static int NormalizeBossWeapons(Creature creature, DungeonSpawnPlan plan)");
            var attributes = PooledLootSourceText.MethodBody(src, "internal static int NormalizeBossAttributes(Creature creature, DungeonSpawnPlan plan)");

            Assert.AreEqual(0, System.Text.RegularExpressions.Regex.Matches(attributes, @"\bplan\b(?!\.)").Count, "the attribute half passes the plan nowhere");

            var read = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);

            foreach (var body in new[] { boss, weapons, attributes })
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"\bplan\.(\w+)"))
                    read.Add(m.Groups[1].Value);

            CollectionAssert.AreEqual(new[] { "BandStandard", "BossDefenseBandRatio", "BossOffenseBandRatio" }, read.ToArray());

            // the plan is passed on whole only to the weapon half pinned above
            var passed = System.Text.RegularExpressions.Regex.Matches(boss, @"\bplan\b(?!\.)");
            Assert.AreEqual(1, passed.Count, "plan passed on once");
            StringAssert.Contains(boss, "NormalizeBossWeapons(creature, plan)");
            Assert.AreEqual(0, System.Text.RegularExpressions.Regex.Matches(weapons, @"\bplan\b(?!\.)").Count);
        }

        [TestMethod]
        public void ApplyTier_RunsAfterTheUplift_NormalizesOnlyBossAndHead_AndSetsHealthLast()
        {
            var src = Src();
            var apply = PooledLootSourceText.MethodBody(src, "public static bool Apply(Creature creature, int knotLevel, PinnacleTier tier)");

            var uplift = apply.IndexOf("BandUplift.Apply(creature, knotLevel, standard)", StringComparison.Ordinal);
            var weapons = apply.IndexOf("ScaleWieldedWeapons(creature, damageRatio)", StringComparison.Ordinal);
            var floor = apply.IndexOf("WorldEventSpawner.ApplyHealth(creature, (int)Math.Min(floorTarget", StringComparison.Ordinal);
            var tier = apply.IndexOf("ApplyTier(creature, knotLevel, tier, standard)", StringComparison.Ordinal);

            Assert.IsTrue(uplift >= 0 && weapons > uplift && floor > weapons && tier > floor, "uplift, weapons, floor, then the tier");

            var body = PooledLootSourceText.MethodBody(src, "internal static string ApplyTier(");
            var eliteGate = body.IndexOf("if (tier != PinnacleTier.Elite)", StringComparison.Ordinal);
            var plan = body.IndexOf("var plan = BossPlan(standard);", StringComparison.Ordinal);
            var attributes = body.IndexOf("DungeonCreatureNormalizer.NormalizeBossAttributes(creature, plan)", StringComparison.Ordinal);
            var normalize = body.IndexOf("DungeonCreatureNormalizer.NormalizeBoss(creature, plan)", StringComparison.Ordinal);
            var zero = body.IndexOf("DungeonCreatureNormalizer.ZeroBossRatings(creature)", StringComparison.Ordinal);
            var dr = body.IndexOf("creature.DamageRating = BossDamageRating;", StringComparison.Ordinal);
            var drr = body.IndexOf("creature.DamageResistRating = BossDamageResistRating;", StringComparison.Ordinal);
            var gateEnd = body.IndexOf("}", drr, StringComparison.Ordinal);
            var level = body.IndexOf("creature.Level = TierLevel(tier, knotLevel);", StringComparison.Ordinal);
            var health = body.IndexOf("WorldEventSpawner.ApplyHealth(creature, (int)health)", StringComparison.Ordinal);

            Assert.IsTrue(eliteGate >= 0 && plan > eliteGate && attributes > plan && normalize > attributes && zero > normalize && dr > zero && drr > zero && gateEnd > drr,
                "boss pieces inside the non-Elite gate: attributes, normalize, ratings after the zeroing");
            Assert.IsTrue(level > gateEnd && health > level, "level for every tier, then health LAST");
            Assert.AreEqual(1, Count(body, "ApplyHealth("));
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(body.Substring(health), @"creature(\.\w+)+\s*=[^=]"), "nothing written to the creature after the health set");
        }

        // ---- behavioural: the tier step on a real (test-built) Creature, no settings read ----

        private static readonly (double Low, double High, long Top) DefaultDials = (Low, High, Top);

        private const uint Median = 200;

        private static DungeonBandStandard AttributeStandard()
        {
            var attributes = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Properties.PropertyAttribute, uint>();

            foreach (var a in DungeonCombatNormalizer.OffenseAttributes.Concat(DungeonCombatNormalizer.DefenseAttributes))
                attributes[a] = Median;

            var skills = new System.Collections.Generic.Dictionary<ACE.Entity.Enum.Skill, uint> { { ACE.Entity.Enum.Skill.MeleeDefense, 300 } };

            return DungeonBandStandard.ForTest(skills, 80, 100, attributeMedians: attributes);
        }

        [TestMethod]
        public void ApplyTier_Boss_AtL200_SetsAttributesRatingsLevelAndHealth()
        {
            var creature = TestCreatures.CreateDefender(endurance: 20, quickness: 40, coordination: 20);
            var standard = AttributeStandard();

            Assert.AreEqual(20u, creature.Attributes[ACE.Entity.Enum.Properties.PropertyAttribute.Endurance].StartingValue, "control: authored before");

            PinnacleScaling.ApplyTier(creature, 200, PinnacleTier.Boss, standard, DefaultDials);

            foreach (var a in DungeonCombatNormalizer.OffenseAttributes.Concat(DungeonCombatNormalizer.DefenseAttributes))
            {
                Assert.AreEqual(Median, creature.Attributes[a].StartingValue, $"{a} set to the band median x 1.0");
                Assert.AreEqual(0u, creature.Attributes[a].Ranks, $"{a} ranks cleared");
            }

            Assert.AreEqual(220, creature.Level);
            Assert.AreEqual(30, creature.DamageRating);
            Assert.AreEqual(15, creature.DamageResistRating);
            Assert.AreEqual(3302u, creature.Health.MaxValue, "3 x T(200), measured AFTER the Endurance change");
            Assert.AreEqual(creature.Health.MaxValue, creature.Health.Current, "refilled");
        }

        [TestMethod]
        public void ApplyTier_Head_AtL200_IsTheBossPipelineAtHeadHealth()
        {
            var creature = TestCreatures.CreateDefender(endurance: 20, quickness: 40, coordination: 20);

            PinnacleScaling.ApplyTier(creature, 200, PinnacleTier.Head, AttributeStandard(), DefaultDials);

            Assert.AreEqual(Median, creature.Attributes[ACE.Entity.Enum.Properties.PropertyAttribute.Endurance].StartingValue);
            Assert.AreEqual(220, creature.Level);
            Assert.AreEqual(30, creature.DamageRating);
            Assert.AreEqual(15, creature.DamageResistRating);
            Assert.AreEqual(4952u, creature.Health.MaxValue);
        }

        [TestMethod]
        public void ApplyTier_Elite_AtL200_LeavesAttributesAndRatings_SetsLevelAndHealth()
        {
            var creature = TestCreatures.CreateDefender(endurance: 20, quickness: 40, coordination: 20);

            PinnacleScaling.ApplyTier(creature, 200, PinnacleTier.Elite, AttributeStandard(), DefaultDials);

            Assert.AreEqual(20u, creature.Attributes[ACE.Entity.Enum.Properties.PropertyAttribute.Endurance].StartingValue, "Elite is not normalized");
            Assert.AreEqual(40u, creature.Attributes[ACE.Entity.Enum.Properties.PropertyAttribute.Quickness].StartingValue);
            Assert.IsNull(creature.DamageRating);
            Assert.IsNull(creature.DamageResistRating);
            Assert.AreEqual(200, creature.Level);
            Assert.AreEqual(1651u, creature.Health.MaxValue);
        }

        private static int Count(string text, string token) => (text.Length - text.Replace(token, string.Empty).Length) / token.Length;
    }
}
