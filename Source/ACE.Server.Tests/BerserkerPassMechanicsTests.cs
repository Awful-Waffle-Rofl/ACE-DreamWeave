using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Berserker pass (2026-10-24, owner rulings): the pure halves of Heavy Blows, Intimidate, Berserk Rage,
    /// the reworked Whirlwind and Damage Rating's crit damage conversion, plus source pins for the call sites
    /// a live Player is needed to exercise (Player's static initializer does not run under the test host).
    /// Every PropertyManager key read here is seeded from the code defaults in ClassInitialize.
    /// </summary>
    [TestClass]
    public class BerserkerPassMechanicsTests
    {
        [ClassInitialize]
        public static void Setup(TestContext context) => DefaultPropertyManager.LoadDefaultProperties();

        // ---- Heavy Blows --------------------------------------------------------------------------------

        [TestMethod]
        public void HeavyBlows_FullPowerRank3_IsFiftyPercent_ZeroPowerIsUnchanged()
        {
            const double stretch = 0.25;

            // retail full power = 0.5 + 1.0; stretched = 0.5 + 1.0 x (1 + 0.25 x 3) = 2.25, ratio 1.5
            Assert.AreEqual(1.5, HeavyBlowsAbility.PowerRatio(1.5, 3, stretch, 1.0), 1e-12);
            Assert.AreEqual(1.0 + 0.25 / 1.5, HeavyBlowsAbility.PowerRatio(1.5, 1, stretch, 1.0), 1e-12);

            // a zero-power swing (power mod 0.5) has no power part to stretch
            Assert.AreEqual(1.0, HeavyBlowsAbility.PowerRatio(0.5, 3, stretch, 1.0), 1e-12);

            // unlearned, or a non-positive tunable, is exactly neutral
            Assert.AreEqual(1.0, HeavyBlowsAbility.PowerRatio(1.5, 0, stretch, 1.0), 1e-12);
            Assert.AreEqual(1.0, HeavyBlowsAbility.PowerRatio(1.5, 3, 0.0, 1.0), 1e-12);
        }

        [TestMethod]
        public void HeavyBlows_AffinityMultipliesTheStretch()
        {
            // 0.5 + 1.0 x (1 + 0.25 x 3 x 1.34) = 2.505, over retail 1.5
            Assert.AreEqual(2.505 / 1.5, HeavyBlowsAbility.PowerRatio(1.5, 3, 0.25, 1.34), 1e-12);
        }

        [TestMethod]
        public void HeavyBlows_IsMeleeOnly_AndDeclaresWeaponTinkering()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.HeavyBlows);
            Assert.AreEqual(Skill.WeaponTinkering, def.AffinitySkill);
            Assert.AreEqual(2, def.Tier);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);

            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/HeavyBlowsAbility.cs"),
                "public void ModifyOutgoingDamage(");
            StringAssert.Contains(body, "damageEvent.CombatType != CombatType.Melee");
        }

        [TestMethod]
        public void HeavyBlows_DoesNotEditTheSharedPowerMod()
        {
            // owner ruling: PvE-only through the outgoing hook, never by changing Player.GetPowerMod
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs"),
                "public override float GetPowerMod(WorldObject weapon)");
            Assert.IsFalse(body.Contains("HeavyBlows", StringComparison.Ordinal));
            StringAssert.Contains(body, "return PowerLevel + 0.5f;");
        }

        // ---- Intimidate ---------------------------------------------------------------------------------

        [TestMethod]
        public void Intimidate_ReducesDefenseByFifteenPerRank_FlooredAtZero()
        {
            Assert.AreEqual(285u, IntimidateAbility.ReducedDefense(300, 1, 15, 1.0));
            Assert.AreEqual(255u, IntimidateAbility.ReducedDefense(300, 3, 15, 1.0));
            Assert.AreEqual(0u, IntimidateAbility.ReducedDefense(20, 3, 15, 1.0), "floored at 0, never wraps");
            Assert.AreEqual(300u, IntimidateAbility.ReducedDefense(300, 0, 15, 1.0), "rank 0 is neutral");
            Assert.AreEqual(300u, IntimidateAbility.ReducedDefense(300, 3, 0, 1.0), "a zero tunable is neutral");

            // Recklessness affinity multiplies the reduction: 15 x 1.34 = 20.1 -> 279.9 rounds to 280
            Assert.AreEqual(280u, IntimidateAbility.ReducedDefense(300, 1, 15, 1.34));
        }

        [TestMethod]
        public void Intimidate_IsAppliedBeforeTheEvadeRoll_OnlyForAPlayerAgainstANonPlayer()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/Entity/DamageEvent.cs"),
                "public float GetEvadeChance(Creature attacker, Creature defender)");

            var hook = body.IndexOf("IntimidateAbility.ApplyToEvadeRoll(intimidatingPlayer, rollDefenseSkill)", StringComparison.Ordinal);
            var roll = body.IndexOf("SkillCheck.GetSkillChance(EffectiveAttackSkill, rollDefenseSkill)", StringComparison.Ordinal);

            Assert.IsTrue(hook > 0 && roll > hook, "the reduction lands before the roll reads the defense skill");
            StringAssert.Contains(body, "if (attacker is Player intimidatingPlayer && defender is not Player)");
        }

        [TestMethod]
        public void Intimidate_ReadsTheSuppressionAwareRank_AndTheRecklessGate()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/IntimidateAbility.cs"),
                "public static uint ApplyToEvadeRoll(");

            // TryGetClassAbility is the PK/arena-suppressed RANK read. GetClassAbilityRating returns the Rating
            // family's +3/6/10 grant, which as a "rank" made the drop 45/90/150 instead of 15/30/45 (review F1).
            StringAssert.Contains(body, "attacker.TryGetClassAbility(ClassAbilityId.Intimidate, out var rank)");
            Assert.IsFalse(body.Contains("GetClassAbilityRating", StringComparison.Ordinal), "a grant is not a rank");
            StringAssert.Contains(body, "ReducedDefenseForRank(rollDefenseSkill, rank,");
            StringAssert.Contains(body, "attacker.IsRecklessAttackQualifying()");
            Assert.AreEqual(Skill.Recklessness, ClassAbilityRegistry.Get(ClassAbilityId.Intimidate).AffinitySkill);
        }

        [TestMethod]
        public void Intimidate_RawRankEndToEnd_Is15_30_45()
        {
            var perRank = PropertyManager.GetDouble(IntimidateAbility.DefensePerRankKey).Item;

            Assert.AreEqual(285u, IntimidateAbility.ReducedDefenseForRank(300, 1, perRank, 1.0), "rank 1 vs 300 defense, no affinity");
            Assert.AreEqual(270u, IntimidateAbility.ReducedDefenseForRank(300, 2, perRank, 1.0));
            Assert.AreEqual(255u, IntimidateAbility.ReducedDefenseForRank(300, 3, perRank, 1.0));
            Assert.AreEqual(300u, IntimidateAbility.ReducedDefenseForRank(300, 0, perRank, 1.0));
            Assert.AreEqual(255u, IntimidateAbility.ReducedDefenseForRank(300, 10, perRank, 1.0), "a stray grant-sized value clamps to rank 3, never 150");
        }

        // ---- Berserk Rage -------------------------------------------------------------------------------

        [TestMethod]
        public void BerserkRage_OnlyEnemyDamageTriggers_NeverAHazard()
        {
            Assert.IsTrue(BerserkRageAbility.IsEnemyDamage(false, true, false, 0), "a monster's melee, missile or spell hit");
            Assert.IsTrue(BerserkRageAbility.IsEnemyDamage(false, false, true, 5), "a DoT tick with an enemy-creature share");

            Assert.IsFalse(BerserkRageAbility.IsEnemyDamage(true, true, false, 0), "hazards never trigger it");
            Assert.IsFalse(BerserkRageAbility.IsEnemyDamage(true, false, true, 5));
            Assert.IsFalse(BerserkRageAbility.IsEnemyDamage(false, false, false, 0), "PvP, self or an unattributed hit");
            Assert.IsFalse(BerserkRageAbility.IsEnemyDamage(false, false, true, 0), "a DoT tick with no enemy share (PvP DoT)");
        }

        [TestMethod]
        public void BerserkRage_SaveLeavesExactlyOneHealth()
        {
            Assert.AreEqual(99u, BerserkRageAbility.SaveDamage(500, 100), "lethal: 100 health keeps 1");
            Assert.AreEqual(99u, BerserkRageAbility.SaveDamage(100, 100), "exactly lethal is lethal");
            Assert.AreEqual(0u, BerserkRageAbility.SaveDamage(40, 1), "at 1 health the blow lands as nothing");
            Assert.AreEqual(50u, BerserkRageAbility.SaveDamage(50, 100), "a non-lethal blow is untouched");
        }

        [TestMethod]
        public void BerserkRage_Immunity_BlocksEverythingButHazardsAndPlayers()
        {
            Assert.AreEqual(0u, BerserkRageImmunity.DamageAfterImmunity(250, false, false, false, 0), "a monster hit is blocked");
            Assert.AreEqual(250u, BerserkRageImmunity.DamageAfterImmunity(250, true, false, false, 0), "hazards bypass all wards");
            Assert.AreEqual(250u, BerserkRageImmunity.DamageAfterImmunity(250, false, true, false, 0), "PvP / self is not blocked (PvE only)");

            // a DoT tick may mix casters: only the enemy-creature share is removed
            Assert.AreEqual(30u, BerserkRageImmunity.DamageAfterImmunity(100, false, false, true, 70));
            Assert.AreEqual(0u, BerserkRageImmunity.DamageAfterImmunity(100, false, false, true, 100));
            Assert.AreEqual(100u, BerserkRageImmunity.DamageAfterImmunity(100, false, false, true, 0));
        }

        [TestMethod]
        public void BerserkRage_HandlersTolerateANullDefender()
        {
            var context = new PreWriteDamageContext { DamageType = DamageType.Slash, Damage = 100 };

            ((IPreWriteDamageAbility)ClassAbilityRegistry.GetHandler(ClassAbilityId.BerserkRage)).ModifyIncomingDamage(null, 3, context);
            BerserkRageAbility.Immunity.ModifyIncomingDamage(null, 3, context);

            Assert.AreEqual(100u, context.Damage);
        }

        [TestMethod]
        public void BerserkRage_CooldownKeysAreTunables_AndTheDefinitionMatchesTheRuling()
        {
            Assert.AreEqual(90.0, PropertyManager.GetDouble(BerserkRageAbility.CooldownKey(1)).Item, 1e-9);
            Assert.AreEqual(60.0, PropertyManager.GetDouble(BerserkRageAbility.CooldownKey(2)).Item, 1e-9);
            Assert.AreEqual(30.0, PropertyManager.GetDouble(BerserkRageAbility.CooldownKey(3)).Item, 1e-9);
            Assert.AreEqual(BerserkRageAbility.CooldownKey(3), BerserkRageAbility.CooldownKey(9), "rank clamps to 3");

            var def = ClassAbilityRegistry.Get(ClassAbilityId.BerserkRage);
            Assert.AreEqual(3, def.Tier);
            Assert.IsNull(def.AffinitySkill, "no affinity");
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
        }

        [TestMethod]
        public void BerserkRage_CooldownSurvivesRelog_ViaAStaticMapKeyedByCharacter()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_BerserkRage.cs");
            StringAssert.Contains(src, "private static readonly ConcurrentDictionary<uint, double> berserkRageReadyTimes");
            StringAssert.Contains(src, "berserkRageReadyTimes[Guid.Full] =");

            // the PK-facet reset clears the window but never the cooldown
            var reset = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs"),
                "public void ResetClassAbilityTransientState()");
            StringAssert.Contains(reset, "berserkRageWindowEndTime = 0;");
            Assert.IsFalse(reset.Contains("berserkRageReadyTimes", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BerserkRage_EnragedBonus_ReachesEveryMagicDamageSite()
        {
            var buffs = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs");
            Assert.AreEqual(3, Regex.Matches(buffs, @"mod \*= GetBerserkRageDamageMod\(\);").Count, "war/void, Harm and life projectile");

            var dot = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs");
            StringAssert.Contains(dot, "tickAmount *= sourcePlayer.GetBerserkRageDamageMod();");
        }

        [TestMethod]
        public void BerserkRage_PlaysTheStrengthUpEffect()
        {
            // AttribUpRed is the TargetEffect of every Strength Self / Other spell in the client dat (probed 2026-10-24)
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_BerserkRage.cs");
            StringAssert.Contains(PooledLootSourceText.MethodBody(src, "public void StartBerserkRage("), "ApplyVisualEffects(PlayScript.AttribUpRed);");
        }

        // ---- Whirlwind ----------------------------------------------------------------------------------

        [TestMethod]
        public void Whirlwind_ExtraTargetsAreTheRank_AndTheSurchargeIsPerRank()
        {
            Assert.AreEqual(0, WhirlwindAbility.ExtraTargets(0));
            Assert.AreEqual(1, WhirlwindAbility.ExtraTargets(1));
            Assert.AreEqual(3, WhirlwindAbility.ExtraTargets(3));
            Assert.AreEqual(3, WhirlwindAbility.ExtraTargets(7), "clamped to MaxRank");

            Assert.AreEqual(35, WhirlwindAbility.Surcharge(100, PropertyManager.GetDouble(WhirlwindAbility.SurchargeKey(1)).Item));
            Assert.AreEqual(75, WhirlwindAbility.Surcharge(100, PropertyManager.GetDouble(WhirlwindAbility.SurchargeKey(2)).Item));
            Assert.AreEqual(100, WhirlwindAbility.Surcharge(100, PropertyManager.GetDouble(WhirlwindAbility.SurchargeKey(3)).Item));
            Assert.AreEqual(0, WhirlwindAbility.Surcharge(100, 0.0));
            Assert.AreEqual(1, WhirlwindAbility.Surcharge(1, 0.35), "a base-1 swing still pays at least 1 (review F2)");
            Assert.AreEqual(1, WhirlwindAbility.Surcharge(2, 0.2), "0.4 rounds to 0 and is floored at 1");

            var apply = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs"),
                "public int ApplyWhirlwindStamina(");
            StringAssert.Contains(apply, "if (surcharge <= 0)");

            var def = ClassAbilityRegistry.Get(ClassAbilityId.Whirlwind);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
        }

        [TestMethod]
        public void Whirlwind_CleaveTargetIsNullSafe_ForUnarmedSwings()
        {
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Melee.cs"),
                "public List<Creature> GetCleaveTarget(");
            StringAssert.Contains(body, "weapon?.CleaveTargets ?? 0");
        }

        [TestMethod]
        public void HeavyBlowsAndBerserkRage_RunBeforeBloodlust_SoLifestealSeesBoth()
        {
            // review F3: Heavy Blows sits where Savage Blows did, ahead of Break Armor and Bloodlust. Owner ruling
            // 2026-10-24: Berserk Rage's x1.5 FEEDS lifesteal too, so it also precedes Bloodlust
            var order = ClassAbilityRegistry.OutgoingDamageAbilities.Select(h => h.Definition.Id).ToList();

            var heavy = order.IndexOf(ClassAbilityId.HeavyBlows);
            Assert.IsTrue(heavy >= 0);
            Assert.IsTrue(heavy < order.IndexOf(ClassAbilityId.BreakArmor), "before Break Armor");
            Assert.IsTrue(heavy < order.IndexOf(ClassAbilityId.Bloodlust), "before Bloodlust");
            var rage = order.IndexOf(ClassAbilityId.BerserkRage);
            Assert.IsTrue(rage >= 0);
            Assert.IsTrue(rage < order.IndexOf(ClassAbilityId.Bloodlust), "rage before Bloodlust: the x1.5 feeds lifesteal");
        }

        [TestMethod]
        public void DamageRating_CritDamageConversion_OnlyReadsCritDamageRatingWithAGrant()
        {
            // review F6: GetCritDamageRating is computed only when the Damage Rating grant is non-zero
            var body = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Rating.cs"), "public int GetDamageRating()");
            StringAssert.Contains(body, "var critDamageConversion = classAbilityBonus > 0");
        }

        // ---- Damage Rating ------------------------------------------------------------------------------

        [TestMethod]
        public void DamageRating_AddsAShareOfTotalCritDamageRating_ByRank()
        {
            // the grant is +3 / +6 / +10, so the rank is read back from it
            Assert.AreEqual(6, RatingAbility.DamageRatingCritDamageConversion(3, 20), "rank 1: 30% of 20");
            Assert.AreEqual(12, RatingAbility.DamageRatingCritDamageConversion(6, 20), "rank 2: 60% of 20");
            Assert.AreEqual(20, RatingAbility.DamageRatingCritDamageConversion(10, 20), "rank 3: 100% of 20");
            Assert.AreEqual(5, RatingAbility.DamageRatingCritDamageConversion(3, 15), "4.5 rounds away from zero");

            Assert.AreEqual(0, RatingAbility.DamageRatingCritDamageConversion(0, 20), "no grant (unlearned or suppressed) converts nothing");
            Assert.AreEqual(0, RatingAbility.DamageRatingCritDamageConversion(10, 0), "no crit damage rating, nothing to convert");
        }

        // ---- retirements --------------------------------------------------------------------------------

        [TestMethod]
        public void RetiredBerserkerAbilities_RefundTheirHistoricalCost()
        {
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("savageblows", 3, out var savage, out _));
            Assert.AreEqual(3, savage, "Savage Blows was 1/1/1");

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("vengeance", 3, out var vengeance, out _));
            Assert.AreEqual(6, vengeance, "Vengeance was 2/2/2");

            Assert.IsTrue(RetiredClassAbilities.TryGetRefund("enhanced_stamina", 2, out var stamina, out _));
            Assert.AreEqual(2, stamina, "Enhanced Stamina was 1/1/1");

            // unused prepaid tokens resolve by id once the ability leaves the registry
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.Vengeance, 2, out var token, out _));
            Assert.AreEqual(2, token);
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(ClassAbilityId.SavageBlows, 1, out _, out _));
            Assert.IsTrue(RetiredClassAbilities.TryGetRefund(EnhancedStatAbility.ClassIdForVital(ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxStamina), 1, out _, out _));

            // and none of the three is still registered, which is what makes the login sweep pick their rows up
            Assert.IsFalse(ClassAbilityRegistry.TryGetByName("savageblows", out _));
            Assert.IsFalse(ClassAbilityRegistry.TryGetByName("vengeance", out _));
            Assert.IsFalse(ClassAbilityRegistry.TryGetByName("enhanced_stamina", out _));
        }
    }
}
