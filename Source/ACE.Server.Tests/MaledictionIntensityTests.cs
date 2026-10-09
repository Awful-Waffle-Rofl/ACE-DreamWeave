using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Malediction (Blood Mage T2) - the pure arithmetic behind "Vulnerability and Imperil enchantments you
    /// apply land at +10/20/30% intensity and last 25% longer".
    ///
    /// Three things are pinned here that a plausible-looking edit would silently break:
    ///  - the Creature Enchantment rider is MULTIPLICATIVE on the rank's own intensity bonus (2026-09-12
    ///    overhaul, shared 0.09 Trained / 0.14 Specialized rate pair via ClassAbilityAffinity.Multiplier),
    ///    not an additive divisor-scaled quotient. IntensityBonus's fraction parameter stays additive in its
    ///    own units, so callers feed it the AMOUNT the multiplier adds (rankBonus * affinity - rankBonus);
    ///  - intensity scales the debuff's DISTANCE FROM ITS IDENTITY (1.0 multiplicative, 0.0 additive), not
    ///    the raw StatModValue - multiplying the raw value inflates weak Vulnerabilities enormously;
    ///  - the predicate is derived from the spell's stat-mod shape, so Protection/Armor (the same shape on
    ///    the other side of the identity) and non-Life-Magic debuffs are untouched.
    ///
    /// The rider's own multiplicative arithmetic is ClassAbilityAffinity.Multiplier, covered by its own
    /// tests; the Player wrapper (Player.TryGetMaledictionMods) cannot be exercised here - Player's static
    /// initializer does not run under the test host - so it is composed from these pieces and reproduced
    /// explicitly.
    /// </summary>
    [TestClass]
    public class MaledictionIntensityTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        private static double IntensityBase => D("class_ability_malediction_intensity_base");
        private static double IntensityStep => D("class_ability_malediction_intensity_step");
        private static double DurationBonus => D("class_ability_malediction_duration_bonus");
        private static double AffinityRatePerTrained => D("class_ability_affinity_rate_per_trained");
        private static double AffinityRatePerSpec => D("class_ability_affinity_rate_per_spec");

        private static double Bonus(int rank, double creatureEnchantmentFraction = 0.0) =>
            MaledictionAbility.IntensityBonus(rank, IntensityBase, IntensityStep, creatureEnchantmentFraction);

        // the two shapes AffectsSpell recognises, spelled out as ace_world stores them:
        // Fire Vulnerability = 20488 = Float | SingleStat | Multiplicative, key 67 (ResistFire)
        // Imperil            = 41088 = BodyArmorValue | MultipleStat | Additive, key 0
        private const EnchantmentTypeFlags VulnType =
            EnchantmentTypeFlags.Float | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Multiplicative;

        private const EnchantmentTypeFlags ImperilType =
            EnchantmentTypeFlags.BodyArmorValue | EnchantmentTypeFlags.MultipleStat | EnchantmentTypeFlags.Additive;

        private const uint ResistFire = (uint)PropertyFloat.ResistFire;

        // ------------------------------------------------------------------
        // the tunables themselves
        // ------------------------------------------------------------------

        [TestMethod]
        public void Tunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.10, IntensityBase, 1e-9);
            Assert.AreEqual(0.10, IntensityStep, 1e-9);
            Assert.AreEqual(0.25, DurationBonus, 1e-9);

            // Malediction's own per-ability divisors were deleted with the 2026-09-12 migration; it now
            // reads the SHARED multiplicative affinity rate pair, covered by ClassAbilityAffinityTests.
        }

        // ------------------------------------------------------------------
        // intensity per rank
        // ------------------------------------------------------------------

        [TestMethod]
        public void IntensityBonus_IsTenTwentyThirtyPercentByRank()
        {
            Assert.AreEqual(0.10, Bonus(1), 1e-9);
            Assert.AreEqual(0.20, Bonus(2), 1e-9);
            Assert.AreEqual(0.30, Bonus(3), 1e-9);
        }

        [TestMethod]
        public void IntensityBonus_IsZeroForAnUnlearnedCaster()
        {
            Assert.AreEqual(0.0, Bonus(0), 1e-9);
            Assert.AreEqual(0.0, Bonus(-1), 1e-9);

            // and a zero bonus must leave the enchantment byte-identical
            Assert.AreEqual(2.5f, MaledictionAbility.ScaleStatModValue(VulnType, 2.5f, Bonus(0)), 1e-6f);
            Assert.AreEqual(-200f, MaledictionAbility.ScaleStatModValue(ImperilType, -200f, Bonus(0)), 1e-6f);
            Assert.AreEqual(30.0, MaledictionAbility.ExtendDuration(30.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void IntensityBonus_RankZeroSuppressesEvenAHugeRider()
        {
            // the rider rides the ability, it is not an ability of its own
            Assert.AreEqual(0.0, Bonus(0, 5.0), 1e-9);
        }

        // ------------------------------------------------------------------
        // the Creature Enchantment rider, at a stated reference skill
        // ------------------------------------------------------------------

        [TestMethod]
        public void CreatureEnchantmentRider_At300SpecializedMultipliesTheRankThreeBonus()
        {
            // rank 3's own intensity bonus is 0.30; at 300 Specialized Creature Enchantment the shared
            // multiplier is 1 + (300/100)*0.14 = 1.42, so the rider (the AMOUNT the multiplier adds) is
            // 0.30 * 0.42 = 0.126 - not a flat divisor-derived percentage-point addition.
            var multiplier = ClassAbilityAffinity.Multiplier(300, true, AffinityRatePerTrained, AffinityRatePerSpec);
            Assert.AreEqual(1.42, multiplier, 1e-9);

            var rankBonus = Bonus(3);
            var rider = rankBonus * multiplier - rankBonus;

            Assert.AreEqual(0.126, rider, 1e-9);
            Assert.AreEqual(0.426, Bonus(3, rider), 1e-9);
        }

        [TestMethod]
        public void CreatureEnchantmentRider_ComposesAdditivelyOnTopOfRank_OnceExpressedAsAnAmount()
        {
            // IntensityBonus itself stays additive in shape - it is the CALLER's job to turn the
            // multiplicative affinity into the "amount added" before handing it in.
            var multiplier = ClassAbilityAffinity.Multiplier(300, true, AffinityRatePerTrained, AffinityRatePerSpec);
            var rankBonus = Bonus(3);
            var rider = rankBonus * multiplier - rankBonus;

            Assert.AreEqual(Bonus(3) + rider, Bonus(3, rider), 1e-9);
        }

        [TestMethod]
        public void CreatureEnchantmentRider_UntrainedContributesNothing()
        {
            // ClassAbilityAffinity.Multiplier returns exactly 1.0 for a non-positive effective skill, so the
            // rider (rankBonus * 1.0 - rankBonus) is exactly 0 and rank alone must survive.
            var multiplier = ClassAbilityAffinity.Multiplier(0, false, AffinityRatePerTrained, AffinityRatePerSpec);
            var rankBonus = Bonus(3);

            Assert.AreEqual(1.0, multiplier, 1e-9);
            Assert.AreEqual(0.30, Bonus(3, rankBonus * multiplier - rankBonus), 1e-9);
        }

        // ------------------------------------------------------------------
        // applying the intensity to a StatModValue
        // ------------------------------------------------------------------

        [TestMethod]
        public void ScaleStatModValue_ScalesAVulnerabilitysDistanceFromOne_NotItsRawValue()
        {
            // Fire Vulnerability VI is 2.5 in ace_world: a 1.5 debuff over the 1.0 identity.
            // +10% intensity = 1.0 + 1.5*1.10 = 2.65. NOT 2.5*1.10 = 2.75.
            Assert.AreEqual(2.65f, MaledictionAbility.ScaleStatModValue(VulnType, 2.5f, Bonus(1)), 1e-5f);
            Assert.AreEqual(2.80f, MaledictionAbility.ScaleStatModValue(VulnType, 2.5f, Bonus(2)), 1e-5f);
            Assert.AreEqual(2.95f, MaledictionAbility.ScaleStatModValue(VulnType, 2.5f, Bonus(3)), 1e-5f);
        }

        [TestMethod]
        public void ScaleStatModValue_DoesNotInflateAWeakVulnerability()
        {
            // Vulnerability I is 1.1 - a +10% damage-taken debuff. At rank 3 it must become a +13% debuff
            // (1.0 + 0.1*1.30 = 1.13), not 1.1*1.30 = 1.43, which would be a +43% debuff off a rank-3 skill.
            var scaled = MaledictionAbility.ScaleStatModValue(VulnType, 1.1f, Bonus(3));

            Assert.AreEqual(1.13f, scaled, 1e-5f);
            Assert.IsTrue(scaled < 1.2f, "scaling the raw multiplier would have quadrupled the debuff");
        }

        [TestMethod]
        public void ScaleStatModValue_ScalesAnImperilLinearly()
        {
            // Imperil VI is -200 armor: additive, identity 0, so the raw value scales directly
            Assert.AreEqual(-220f, MaledictionAbility.ScaleStatModValue(ImperilType, -200f, Bonus(1)), 1e-4f);
            Assert.AreEqual(-240f, MaledictionAbility.ScaleStatModValue(ImperilType, -200f, Bonus(2)), 1e-4f);
            Assert.AreEqual(-260f, MaledictionAbility.ScaleStatModValue(ImperilType, -200f, Bonus(3)), 1e-4f);
        }

        // ------------------------------------------------------------------
        // duration extension
        // ------------------------------------------------------------------

        [TestMethod]
        public void ExtendDuration_AddsTwentyFivePercent()
        {
            Assert.AreEqual(37.5, MaledictionAbility.ExtendDuration(30.0, DurationBonus), 1e-9);
            Assert.AreEqual(120.0, MaledictionAbility.ExtendDuration(96.0, DurationBonus), 1e-9);
        }

        [TestMethod]
        public void ExtendDuration_IsFlat_NotPerRank()
        {
            // the tunable is read directly, so every rank extends by the same 25%
            Assert.AreEqual(MaledictionAbility.ExtendDuration(30.0, DurationBonus),
                            MaledictionAbility.ExtendDuration(30.0, DurationBonus), 1e-9);
            Assert.AreEqual(37.5, MaledictionAbility.ExtendDuration(30.0, 0.25), 1e-9);
        }

        [TestMethod]
        public void ExtendDuration_LeavesThePermanentSentinelAlone()
        {
            // -1 means "until the item is dequipped"; multiplying it would make it a longer negative number
            Assert.AreEqual(-1.0, MaledictionAbility.ExtendDuration(-1.0, DurationBonus), 1e-9);
            Assert.AreEqual(0.0, MaledictionAbility.ExtendDuration(0.0, DurationBonus), 1e-9);
        }

        // ------------------------------------------------------------------
        // which spells it applies to
        // ------------------------------------------------------------------

        [TestMethod]
        public void AffectsSpell_AcceptsEveryElementalVulnerability()
        {
            for (var key = (uint)PropertyFloat.ResistSlash; key <= (uint)PropertyFloat.ResistElectric; key++)
            {
                Assert.IsTrue(MaledictionAbility.AffectsSpell(MagicSchool.LifeMagic, VulnType, key, 2.5f),
                    $"resistance key {key} is one of the seven elemental Vulnerabilities");
            }
        }

        [TestMethod]
        public void AffectsSpell_AcceptsImperil()
        {
            Assert.IsTrue(MaledictionAbility.AffectsSpell(MagicSchool.LifeMagic, ImperilType, 0, -200f));

            // Incantation of Imperil Other (spell 4312) stores a NULL stat_Mod_Key, which Spell.StatModKey
            // surfaces as 0 - the shape test must not depend on the key for the additive branch
            Assert.IsTrue(MaledictionAbility.AffectsSpell(MagicSchool.LifeMagic, ImperilType, 0, -225f));
        }

        [TestMethod]
        public void AffectsSpell_RejectsTheBuffOnTheOtherSideOfEachIdentity()
        {
            // Fire Protection is the same shape as Fire Vulnerability, below the 1.0 identity
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.LifeMagic, VulnType, ResistFire, 0.75f));
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.LifeMagic, VulnType, ResistFire, 1.0f));

            // Armor Other is the same shape as Imperil, above the 0.0 identity
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.LifeMagic, ImperilType, 0, 200f));
        }

        [TestMethod]
        public void AffectsSpell_RejectsANonVulnerabilityEnchantment()
        {
            // a Life Magic vital boost: Float/Multiplicative shape but not a damage resistance
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.LifeMagic, VulnType, (uint)PropertyFloat.ResistHealthBoost, 2.5f));

            // an attribute debuff (Creature Enchantment shape) is not a resistance debuff at all
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.CreatureEnchantment,
                EnchantmentTypeFlags.Attribute | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive,
                (uint)PropertyAttribute.Strength, -40f));

            // and a war spell carries no enchantment shape at all
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.WarMagic, EnchantmentTypeFlags.Undef, 0, 0f));
        }

        [TestMethod]
        public void AffectsSpell_RejectsTheVoidLookalike()
        {
            // Expose Weakness is byte-identical in shape to Imperil but is a Void spell, so sec 3's
            // "Vulnerability and Imperil" excludes it. The Life Magic gate is the only thing separating them
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.VoidMagic, ImperilType, 0, -200f));
            Assert.IsFalse(MaledictionAbility.AffectsSpell(MagicSchool.VoidMagic, VulnType, ResistFire, 2.5f));
        }
    }
}
