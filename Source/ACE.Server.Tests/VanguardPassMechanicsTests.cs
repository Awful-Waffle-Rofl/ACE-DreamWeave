using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Vanguard pass of 2026-11-06 (owner rulings): Cloaked in Power's Critical Thorns window (the pure parts
    /// live in CloakedInPowerAbilityTests; the wiring order lives in CloakProcWiringSourceScanTests), Crit Resist
    /// Rating's damage resist conversion, Damage Resist Rating's Thorns multiplier, Battle Hardened's three ranks (in
    /// ClassAbilityHandlerTests), Enhanced Health's price, and reprice wave 7.
    ///
    /// Every PropertyManager key read here is one of the new tunables, which DefaultPropertyManager seeds with the
    /// registered defaults. Tests that change a key restore it in a finally.
    /// </summary>
    [TestClass]
    public class VanguardPassMechanicsTests
    {
        [ClassInitialize]
        public static void Setup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        // ---- Crit Resist Rating -> Damage Resist Rating -------------------------------------------------------------

        [TestMethod]
        public void CritResistRating_Definition_IsOneCapPerRank_AndDescribesTheConversion()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.CritResistRating);

            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank, "was 2/2/2 before the 2026-11-06 Vanguard pass");
            Assert.AreEqual(3, def.MaxRank);
            StringAssert.Contains(ClassAbilityText.Describe(def, ClassAbilityText.Defaults), "adds part of your Critical Resist Rating to your Damage Resist Rating");
            Assert.AreEqual("+3, +30% of it as DRR", ClassAbilityText.RankTail(def, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("+10, +100% of it as DRR", ClassAbilityText.RankTail(def, 3, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void CritResistRating_ConversionTunables_DefaultTo30_60_100Percent()
        {
            Assert.AreEqual(0.30, PropertyManager.GetDouble("class_ability_critresistrating_damage_conversion_r1").Item, 1e-9);
            Assert.AreEqual(0.60, PropertyManager.GetDouble("class_ability_critresistrating_damage_conversion_r2").Item, 1e-9);
            Assert.AreEqual(1.00, PropertyManager.GetDouble("class_ability_critresistrating_damage_conversion_r3").Item, 1e-9);
        }

        [TestMethod]
        public void CritResistRating_AddsAShareOfTotalCritResistRating_ByRank()
        {
            // the grant is +3 / +6 / +10, so the rank is read back from it
            Assert.AreEqual(6, RatingAbility.CritResistRatingDamageConversion(3, 20), "rank 1: 30% of 20");
            Assert.AreEqual(12, RatingAbility.CritResistRatingDamageConversion(6, 20), "rank 2: 60% of 20");
            Assert.AreEqual(20, RatingAbility.CritResistRatingDamageConversion(10, 20), "rank 3: 100% of 20");
        }

        [TestMethod]
        public void CritResistRating_Conversion_RoundsHalfAwayFromZero()
        {
            Assert.AreEqual(5, RatingAbility.CritResistRatingDamageConversion(3, 15), "rank 1: 4.5 rounds away from zero to 5");
            Assert.AreEqual(2, RatingAbility.CritResistRatingDamageConversion(3, 5), "rank 1: 1.5 rounds away from zero to 2");
            Assert.AreEqual(1, RatingAbility.CritResistRatingDamageConversion(3, 3), "rank 1: 0.9 rounds to 1");
            Assert.AreEqual(0, RatingAbility.CritResistRatingDamageConversion(3, 1), "rank 1: 0.3 rounds to 0");
        }

        [TestMethod]
        public void CritResistRating_Conversion_IsZeroWithoutAGrant_AndNeverNegative()
        {
            Assert.AreEqual(0, RatingAbility.CritResistRatingDamageConversion(0, 20), "no grant (unlearned, or PK/arena suppression zeroed it) converts nothing");
            Assert.AreEqual(0, RatingAbility.CritResistRatingDamageConversion(4, 20), "a value the rank table does not produce is not a grant");
            Assert.AreEqual(0, RatingAbility.CritResistRatingDamageConversion(10, 0), "no crit resist rating, nothing to convert");
            Assert.AreEqual(0, RatingAbility.CritResistRatingDamageConversion(10, -15), "a negative total converts to 0, never a penalty");
        }

        [TestMethod]
        public void CritResistRating_Conversion_FollowsTheLiveTunable()
        {
            var key = "class_ability_critresistrating_damage_conversion_r2";
            var original = PropertyManager.GetDouble(key).Item;

            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(key, 0.5));
                Assert.AreEqual(10, RatingAbility.CritResistRatingDamageConversion(6, 20));
            }
            finally
            {
                PropertyManager.ModifyDouble(key, original);
            }
        }

        /// <summary>
        /// The conversion lands in GetDamageResistRating (owner ruling 2026-11-08; it was Damage Rating from 2026-11-06):
        /// only computed with a grant, the grant read through GetClassAbilityRating (so suppression zeroes it, which is
        /// what keeps it PvE only), and summed into the return. GetCritResistRating must not call back into
        /// GetDamageResistRating, or this recurses.
        /// </summary>
        [TestMethod]
        public void CritResistRating_ReadInGetDamageResistRating_IsGatedOnTheGrant_AndCannotRecurse_SourceScan()
        {
            var source = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Rating.cs");

            var drr = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(source, "public int GetDamageResistRating(CombatType? combatType = null, bool directDamage = true)");
            StringAssert.Contains(drr, "var critResistConversion = critResistGrant > 0");
            StringAssert.Contains(drr, "GetClassAbilityRating(ClassAbilityId.CritResistRating)");
            StringAssert.Contains(drr, "RatingAbility.CritResistRatingDamageConversion(critResistGrant, GetCritResistRating())");
            StringAssert.Contains(drr, "+ classAbilityBonus + critResistConversion;");

            var critResist = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(source, "public int GetCritResistRating()");
            Assert.IsFalse(critResist.Contains("GetDamageResistRating", StringComparison.Ordinal), "GetCritResistRating must never read GetDamageResistRating");
            Assert.IsFalse(critResist.Contains("GetDamageRating", StringComparison.Ordinal), "GetCritResistRating must never read GetDamageRating");
        }

        /// <summary>
        /// The conversion no longer feeds Damage Rating: GetDamageRating reads neither the Crit Resist Rating grant
        /// nor the conversion.
        /// </summary>
        [TestMethod]
        public void CritResistRating_NoLongerFeedsGetDamageRating_SourceScan()
        {
            var source = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Rating.cs");

            var damageRating = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(source, "public int GetDamageRating()");
            Assert.IsFalse(damageRating.Contains("CritResistRating", StringComparison.Ordinal), "GetDamageRating must not read the Crit Resist Rating grant or conversion");
            Assert.IsFalse(damageRating.Contains("critResistConversion", StringComparison.Ordinal), "GetDamageRating must not sum the crit resist conversion");
        }

        [TestMethod]
        public void CritResistRating_Text_NamesDamageResistRating()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.CritResistRating);
            var text = ClassAbilityText.Describe(def, ClassAbilityText.Defaults);

            StringAssert.Contains(text, "adds part of your Critical Resist Rating to your Damage Resist Rating.");
            Assert.IsFalse(text.Contains("to your Damage Rating", StringComparison.Ordinal), "the conversion no longer targets Damage Rating");
        }

        // ---- Damage Resist Rating -> Thorns -------------------------------------------------------------------

        [TestMethod]
        public void DamageResistRating_ThornsTunables_DefaultToHalf_One_OneAndAHalf()
        {
            Assert.AreEqual(0.5, PropertyManager.GetDouble("class_ability_damageresistrating_thorns_conversion_r1").Item, 1e-9);
            Assert.AreEqual(1.0, PropertyManager.GetDouble("class_ability_damageresistrating_thorns_conversion_r2").Item, 1e-9);
            Assert.AreEqual(1.5, PropertyManager.GetDouble("class_ability_damageresistrating_thorns_conversion_r3").Item, 1e-9);
        }

        [TestMethod]
        public void DamageResistRating_ThornsMultiplier_IsOnePlusFractionTimesTotalOverHundred_ByRank()
        {
            // total DRR 40: rank 1 (grant 3) -> 1 + 0.5 x 0.40 = 1.2 ; rank 2 (grant 6) -> 1.4 ; rank 3 (grant 10) -> 1.6
            Assert.AreEqual(1.2, RatingAbility.DamageResistRatingThornsMultiplier(3, 40), 1e-9);
            Assert.AreEqual(1.4, RatingAbility.DamageResistRatingThornsMultiplier(6, 40), 1e-9);
            Assert.AreEqual(1.6, RatingAbility.DamageResistRatingThornsMultiplier(10, 40), 1e-9);

            // a different total scales linearly
            Assert.AreEqual(1.0 + 1.5 * 1.5, RatingAbility.DamageResistRatingThornsMultiplier(10, 150), 1e-9);
        }

        [TestMethod]
        public void DamageResistRating_ThornsMultiplier_IsOneWithoutAGrant_AndNeverBelowOne()
        {
            Assert.AreEqual(1.0, RatingAbility.DamageResistRatingThornsMultiplier(0, 40), 1e-12, "no grant (unlearned or suppressed)");
            Assert.AreEqual(1.0, RatingAbility.DamageResistRatingThornsMultiplier(5, 40), 1e-12, "not a rank the table produces");
            Assert.AreEqual(1.0, RatingAbility.DamageResistRatingThornsMultiplier(10, 0), 1e-12, "no total");
            Assert.AreEqual(1.0, RatingAbility.DamageResistRatingThornsMultiplier(10, -60), 1e-12, "a negative total never shrinks the reflect");
        }

        [TestMethod]
        public void DamageResistRating_ThornsMultiplier_ComposesMultiplicativelyWithTheCritMultiplier()
        {
            var baseReflect = 100u;
            var drr = RatingAbility.DamageResistRatingThornsMultiplier(10, 40);                // 1.6
            var crit = CloakedInPowerAbility.CritThornsMultiplier(0);                           // 2.0

            Assert.AreEqual(320u, ThornsAbility.ScaleReflect(baseReflect, 1.0, crit, drr), "100 x 2.0 x 1.6, not 100 x (2.0 + 0.6)");
            Assert.AreEqual(160u, ThornsAbility.ScaleReflect(baseReflect, 1.0, 1.0, drr), "DRR alone");
            Assert.AreEqual(200u, ThornsAbility.ScaleReflect(baseReflect, 1.0, crit, 1.0), "crit alone");
        }

        [TestMethod]
        public void DamageResistRating_Definition_StaysOneCapPerRank_AndDescribesTheThornsBonus()
        {
            var def = ClassAbilityRegistry.Get(ClassAbilityId.DamageResistRating);

            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
            StringAssert.Contains(ClassAbilityText.Describe(def, ClassAbilityText.Defaults), "Thorns");
            StringAssert.EndsWith(ClassAbilityText.Describe(def, ClassAbilityText.Defaults), " Does nothing without Thorns or the Thorns gear mod.");
            Assert.AreEqual("+3, Thorns x(1 + 0.5% per DRR)", ClassAbilityText.RankTail(def, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("+10, Thorns x(1 + 1.5% per DRR)", ClassAbilityText.RankTail(def, 3, ClassAbilityText.Defaults));
        }

        [TestMethod]
        public void DamageResistRating_IsAppliedInsideTheSingleReflectSite_SourceScan()
        {
            var source = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityCombat.cs");
            var reflect = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(source,
                "public void ApplyThornsReflect(Creature attacker, DamageType damageType, double strengthMultiplier)");

            StringAssert.Contains(reflect, "GetClassAbilityRating(ClassAbilityId.DamageResistRating)");
            // Thread Shrine (2026-11-09): the mitigation blessing is taken back out of the total, so it never grows the reflect.
            StringAssert.Contains(reflect, "RatingAbility.DamageResistRatingThornsMultiplier(drrGrant, GetDamageResistRating() - ACE.Server.ThreadDungeons.ThreadBoons.DamageResistRatingFor(this))");
            StringAssert.Contains(reflect, "ThornsAbility.ScaleReflect(baseReflect, strengthMultiplier, critMultiplier, drrMultiplier)");

            // after the ownership gate, so it only scales an existing reflect
            Assert.IsTrue(reflect.IndexOf("if (!owned && gearMod <= 0.0)", StringComparison.Ordinal)
                < reflect.IndexOf("DamageResistRatingThornsMultiplier", StringComparison.Ordinal));
        }

        // ---- the Critical Thorns window, driven through the real reflect --------------------------------------

        private static (Player player, Creature attacker) Pair()
        {
            var player = ThornsCloseRangeTests.SeededThornsModPlayer();

            // GetCritDamageRating (read only while the window is open) reaches the enchantment manager
            player.Biota.PropertiesEnchantmentRegistry = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry>();
            typeof(WorldObject).GetField("EnchantmentManager").SetValue(player, new EnchantmentManagerWithCaching(player));

            var attacker = TestCreatures.CreateDefender(maxHealth: 5000);
            attacker.Health.Current = 5000;

            TestCreatures.AttachBarePhysics(player, 50.0f);
            TestCreatures.AttachBarePhysics(attacker, 51.0f);

            return (player, attacker);
        }

        [TestMethod]
        public void CriticalThorns_WindowClosed_ReflectIsTheBareAmount_Control()
        {
            var (player, attacker) = Pair();

            var expected = ThornsCloseRangeTests.ExpectedReflect(player);
            Assert.IsTrue(expected > 0, "harness control: a nonzero reflect");

            player.ApplyThornsReflect(attacker, DamageType.Slash, 1.0);

            Assert.AreEqual(5000u - expected, attacker.Health.Current);
        }

        [TestMethod]
        public void CriticalThorns_WindowOpen_ReflectIsTwiceTheBareAmount()
        {
            var (player, attacker) = Pair();
            var expected = ThornsCloseRangeTests.ExpectedReflect(player);

            player.OpenCriticalThornsWindow(Time.GetUnixTime(), 60.0);

            player.ApplyThornsReflect(attacker, DamageType.Slash, 1.0);

            Assert.AreEqual(5000u - 2u * expected, attacker.Health.Current, "critical reflect = base x 2.0 x the (neutral) crit damage rating mod");
        }

        [TestMethod]
        public void CriticalThorns_WindowOpen_TakesTheWearersCritDamageRatingMod()
        {
            var (player, attacker) = Pair();
            var expected = ThornsCloseRangeTests.ExpectedReflect(player);

            player.CritDamageRating = 50;       // x1.5
            player.OpenCriticalThornsWindow(Time.GetUnixTime(), 60.0);

            player.ApplyThornsReflect(attacker, DamageType.Slash, 1.0);

            var reflect = (uint)Math.Round(expected * 2.0 * 1.5);
            Assert.AreEqual(5000u - reflect, attacker.Health.Current, "2.0 x (100 + 50) / 100 = 3.0 x the bare reflect");
        }

        [TestMethod]
        public void CriticalThorns_WindowExpired_ReflectGoesBackToNormal()
        {
            var (player, attacker) = Pair();
            var expected = ThornsCloseRangeTests.ExpectedReflect(player);

            // a window that closed ten seconds ago
            player.OpenCriticalThornsWindow(Time.GetUnixTime() - 11.0, 1.0);

            Assert.IsFalse(player.IsCriticalThornsWindowOpen(Time.GetUnixTime()));

            player.ApplyThornsReflect(attacker, DamageType.Slash, 1.0);

            Assert.AreEqual(5000u - expected, attacker.Health.Current);
        }

        [TestMethod]
        public void CriticalThorns_AProcInsideTheWindowRefreshesItToNowPlusDuration()
        {
            var (player, _) = Pair();
            var now = Time.GetUnixTime();

            player.OpenCriticalThornsWindow(now, 1.0);
            Assert.AreEqual(now + 1.0, player.CriticalThornsWindowEnd, 1e-6);

            // 0.6 s later, 0.4 s left: refresh to now + duration, no stacking onto the time left
            player.OpenCriticalThornsWindow(now + 0.6, 1.0);
            Assert.AreEqual(now + 1.6, player.CriticalThornsWindowEnd, 1e-6);

            Assert.IsTrue(player.IsCriticalThornsWindowOpen(now + 1.5));
            Assert.IsFalse(player.IsCriticalThornsWindowOpen(now + 1.7));
        }

        // ---- Enhanced Health ----------------------------------------------------------------------------------

        [TestMethod]
        public void EnhancedHealth_IsOneCapPerRank()
        {
            var def = ClassAbilityRegistry.Get(EnhancedStatAbility.ClassIdForVital(PropertyAttribute2nd.MaxHealth));

            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank, "was 2/2/2 (premium) before the 2026-11-06 Vanguard pass");

            // Stamina and Mana are unchanged at 1/rank
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, ClassAbilityRegistry.Get(EnhancedStatAbility.ClassIdForVital(PropertyAttribute2nd.MaxMana)).CostPerRank);
        }

        // ---- reprice wave 7 -----------------------------------------------------------------------------------

        private static IReadOnlyList<RepriceWave> W => ClassAbilityRepricing.Waves;

        private static Func<ClassAbilityId, int> RankOf(Dictionary<ClassAbilityId, int> ranks) =>
            id => ranks.TryGetValue(id, out var rank) ? rank : 0;

        [TestMethod]
        public void Wave7_VanguardPass_PinsItsRowsAndOldCosts()
        {
            var wave = W.Single(w => w.Number == 7);

            Assert.AreEqual("Vanguard pass", wave.Label);
            Assert.AreEqual("Vanguard abilities", wave.Subject);
            Assert.AreEqual("the Vanguard trainer", wave.Trainer);
            Assert.IsTrue(ClassAbilityRepricing.LatestWave >= 7, "wave 7 is shipped (wave 8, the Rogue tier swap, followed it)");

            var health = EnhancedStatAbility.ClassIdForVital(PropertyAttribute2nd.MaxHealth);
            var rows = wave.Rows.ToDictionary(r => r.Ability);

            CollectionAssert.AreEquivalent(
                new[] { ClassAbilityId.CloakedInPower, ClassAbilityId.CritResistRating, ClassAbilityId.BattleHardened, health },
                rows.Keys.ToArray(), "Damage Resist Rating's price did not change, so it has no row");

            // the CostPerRank values on master before this pass (d462d3ae7)
            CollectionAssert.AreEqual(new[] { 1 }, rows[ClassAbilityId.CloakedInPower].OldCosts.ToArray(), "Cloaked in Power was a single rank");
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, rows[ClassAbilityId.CritResistRating].OldCosts.ToArray());
            CollectionAssert.AreEqual(new[] { 3 }, rows[ClassAbilityId.BattleHardened].OldCosts.ToArray(), "Battle Hardened was a single rank at 3");
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, rows[health].OldCosts.ToArray());

            foreach (var row in rows.Values)
                CollectionAssert.AreEqual(new[] { 1, 1, 1 }, row.NewCosts.ToArray(), $"{row.Ability}: 1/1/1");

            Assert.AreEqual(RepriceAction.Keep, rows[ClassAbilityId.CloakedInPower].Action);
            Assert.AreEqual(RepriceAction.Keep, rows[ClassAbilityId.CritResistRating].Action);
            Assert.AreEqual(RepriceAction.PromoteToPaid, rows[ClassAbilityId.BattleHardened].Action);
            Assert.AreEqual(RepriceAction.Keep, rows[health].Action);

            // the newest wave naming each ability matches its live ladder
            foreach (var row in rows.Values)
                CollectionAssert.AreEqual(ClassAbilityRegistry.Get(row.Ability).CostPerRank, row.NewCosts.ToArray(), $"{row.Ability}: live ladder");
        }

        [TestMethod]
        public void BattleHardenedOwner_EndsAtRank3_WithNoNetPointChange()
        {
            foreach (var appliedWave in new[] { 0, 1, 2, 3, 4, 5, 6 })
            {
                var rankOf = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.BattleHardened] = 1 });

                var promotions = ClassAbilityRepricing.PlanPromotions(W, appliedWave, rankOf).Where(p => p.Ability == ClassAbilityId.BattleHardened).ToList();

                Assert.AreEqual(1, promotions.Count, $"applied {appliedWave}: the owner is promoted exactly once");
                Assert.AreEqual(7, promotions[0].Wave);
                Assert.AreEqual(1, promotions[0].OldRank);
                Assert.AreEqual(3, promotions[0].NewRank, "rank 1 bought at 3 becomes rank 3 at 1/1/1");
                Assert.AreEqual(3, promotions[0].OldPaid);

                Assert.AreEqual(3, ClassAbilityRegistry.Get(ClassAbilityId.BattleHardened).CumulativeCost(3), "control: rank 3 costs 3 live, what the owner paid");
                Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(W, appliedWave, rankOf).Count, "never unlearned");
                Assert.AreEqual(0, ClassAbilityRepricing.RecomputePoints(20, 17, 3, 0, 0).Delta, "no net point change");
            }

            // a character already stamped at wave 7 is not promoted again
            var stamped = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.BattleHardened] = 3 });
            Assert.AreEqual(0, ClassAbilityRepricing.PlanPromotions(W, 7, stamped).Count);
        }

        [TestMethod]
        public void VanguardOwners_KeepTheirRanks_AndGetTheDifferenceBack()
        {
            var health = EnhancedStatAbility.ClassIdForVital(PropertyAttribute2nd.MaxHealth);

            // rank 3 Crit Resist Rating (paid 6, now 3) and rank 3 Enhanced Health (paid 6, now 3): 6 comes back
            var rankOf = RankOf(new Dictionary<ClassAbilityId, int>
            {
                [ClassAbilityId.CritResistRating] = 3,
                [health] = 3,
            });

            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(W, 6, rankOf).Count, "Keep never unlearns");
            Assert.AreEqual(6, ClassAbilityRepricing.ExpectedDelta(W, 6, rankOf, 0));

            // a rank 1 Cloaked in Power owner paid 1 and still owes 1: rank kept, nothing returned
            var cip = RankOf(new Dictionary<ClassAbilityId, int> { [ClassAbilityId.CloakedInPower] = 1 });
            Assert.AreEqual(0, ClassAbilityRepricing.PlanUnlearns(W, 6, cip).Count);
            Assert.AreEqual(0, ClassAbilityRepricing.ExpectedDelta(W, 6, cip, 0));
        }

        [TestMethod]
        public void Wave7_TokenRefunds_OnlyForTiersWhosePriceChanged()
        {
            // a carried old Crit Resist Rating token (bought at 2) is taken back at its old price
            var crr = ClassAbilityRepricing.DecideTokenRefund(W, 6, (int)ClassAbilityId.CritResistRating, 1, 1);
            Assert.IsTrue(crr.Refund);
            Assert.AreEqual(2, crr.UnitPrice);

            // a carried old Battle Hardened token (tier 1, bought at 3) is taken back at 3
            var bh = ClassAbilityRepricing.DecideTokenRefund(W, 6, (int)ClassAbilityId.BattleHardened, 1, 1);
            Assert.IsTrue(bh.Refund);
            Assert.AreEqual(3, bh.UnitPrice);

            // a Cloaked in Power rank-1 token kept its price (1 -> 1), so it stays in the pack
            Assert.IsFalse(ClassAbilityRepricing.DecideTokenRefund(W, 6, (int)ClassAbilityId.CloakedInPower, 1, 1).Refund);
        }

        // ---- the five Descriptions ----------------------------------------------------------------------------

        [TestMethod]
        public void Descriptions_AreAsciiAndMentionTheNewBehaviour()
        {
            var cip = ClassAbilityText.Describe(ClassAbilityRegistry.Get(ClassAbilityId.CloakedInPower), ClassAbilityText.Defaults);
            StringAssert.Contains(cip, "critical");
            var cipDef = ClassAbilityRegistry.Get(ClassAbilityId.CloakedInPower);
            Assert.AreEqual("0.5 seconds of critical Thorns", ClassAbilityText.RankTail(cipDef, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("1.5 seconds of critical Thorns", ClassAbilityText.RankTail(cipDef, 3, ClassAbilityText.Defaults));

            var bh = ClassAbilityText.Describe(ClassAbilityRegistry.Get(ClassAbilityId.BattleHardened), ClassAbilityText.Defaults);
            var bhDef = ClassAbilityRegistry.Get(ClassAbilityId.BattleHardened);
            Assert.AreEqual("0.025% less damage per Strength point", ClassAbilityText.RankTail(bhDef, 1, ClassAbilityText.Defaults));
            Assert.AreEqual("0.075% less damage per Strength point", ClassAbilityText.RankTail(bhDef, 3, ClassAbilityText.Defaults));

            foreach (var d in new[] { cip, bh, ClassAbilityText.Describe(ClassAbilityRegistry.Get(ClassAbilityId.CritResistRating), ClassAbilityText.Defaults), ClassAbilityText.Describe(ClassAbilityRegistry.Get(ClassAbilityId.DamageResistRating), ClassAbilityText.Defaults) })
                Assert.IsFalse(d.Any(c => c > 127), "token SQL and content-lint reject non-ASCII");
        }
    }
}
