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
    ///  - the Life Magic rider tunables are DIVISORS, not per-point rates, so 50.0 means "+1% per 50 points"
    ///    and produces +8% at 400 Life Magic. Written as a rate (0.02) it compiles and is 2500x wrong;
    ///  - intensity scales the debuff's DISTANCE FROM ITS IDENTITY (1.0 multiplicative, 0.0 additive), not
    ///    the raw StatModValue - multiplying the raw value inflates weak Vulnerabilities enormously;
    ///  - the predicate is derived from the spell's stat-mod shape, so Protection/Armor (the same shape on
    ///    the other side of the identity) and non-Life-Magic debuffs are untouched.
    ///
    /// The rider's own dual-ratio arithmetic is ClassAbilityScaling.Compute, covered by its own tests; the
    /// Player wrapper (Player.TryGetMaledictionMods) cannot be exercised here - Player's static initializer
    /// does not run under the test host - so it is composed from these pieces and reproduced explicitly.
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
        private static double PerTrained => D("class_ability_malediction_lifemagic_per_trained");
        private static double PerSpec => D("class_ability_malediction_lifemagic_per_spec");

        private static double Bonus(int rank, double lifeMagicFraction = 0.0) =>
            MaledictionAbility.IntensityBonus(rank, IntensityBase, IntensityStep, lifeMagicFraction);

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

            // DIVISORS - "points of Life Magic per +1%", so larger is weaker. NOT rates: 50.0 here means
            // +0.02 percentage points per point of Life Magic, which is the design's "+1% per 50".
            Assert.AreEqual(50.0, PerTrained, 1e-9);
            Assert.AreEqual(37.0, PerSpec, 1e-9);
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
        // the Life Magic rider, at a stated reference skill
        // ------------------------------------------------------------------

        [TestMethod]
        public void LifeMagicRider_At400IsEightPercentTrainedAndTenPointEightSpecialized()
        {
            // GetClassAbilityScaling returns percentage POINTS; the call site multiplies by 0.01
            var trained = ClassAbilityScaling.Compute(400, false, PerTrained, PerSpec) * 0.01;
            var specialized = ClassAbilityScaling.Compute(400, true, PerTrained, PerSpec) * 0.01;

            Assert.AreEqual(0.08, trained, 1e-9, "400 Life Magic / 50 = +8 percentage points");
            Assert.AreEqual(400.0 / 37.0 * 0.01, specialized, 1e-9);
            Assert.AreEqual(0.1081, specialized, 5e-5, "400 Life Magic / 37 = +10.8 percentage points");
        }

        [TestMethod]
        public void LifeMagicRider_ComposesAdditivelyOnTopOfRank()
        {
            var trained = ClassAbilityScaling.Compute(400, false, PerTrained, PerSpec) * 0.01;

            // rank 3 (+30%) plus a trained 400 Life Magic (+8%) = +38% intensity
            Assert.AreEqual(0.38, Bonus(3, trained), 1e-9);

            // and it is additive, not multiplicative: 0.30 * 1.08 would be 0.324
            Assert.AreNotEqual(0.324, Bonus(3, trained), 1e-3);
        }

        [TestMethod]
        public void LifeMagicRider_UntrainedContributesNothing()
        {
            // Player.GetClassAbilityScaling returns 0 for an untrained source; rank alone must survive
            Assert.AreEqual(0.30, Bonus(3, 0.0), 1e-9);
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
