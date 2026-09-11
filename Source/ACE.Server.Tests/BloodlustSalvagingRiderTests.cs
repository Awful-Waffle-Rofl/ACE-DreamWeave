using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Bloodlust (Berserker T3) - the Salvaging lifesteal rider, and specifically that SPECIALIZED
    /// Salvaging keeps paying it.
    ///
    /// The trap this pins: Salvaging looks unspecializable (the dat prices the trained -> specialized
    /// upgrade at 999 credits, so no player can buy it with skill credits), and the tunable's own
    /// description used to assert outright that "Salvaging cannot specialize". It can. Retail sells an
    /// augmentation - PropertyInt.AugmentationSpecializeSalvaging, the
    /// "gemaugmentationtinkeringspecsalv" gem - that sets the skill to SkillAdvancementClass.Specialized
    /// permanently, and Player_Skills re-applies it after an Asheron's Castle / Enlightenment reset.
    ///
    /// Player.GetClassAbilityScaling branches on that advancement class and hands ClassAbilityScaling the
    /// per-SPEC divisor, so anything that treats the spec branch as unreachable (a 0 divisor, a "same as
    /// trained, since it can't happen" comment that invites a later edit) silently zeroes the rider for
    /// exactly the players who paid for the augmentation. The two divisors are pinned EQUAL here on
    /// purpose: aug-spec Salvaging pays the same per point as trained Salvaging, no more and no less.
    ///
    /// Player itself cannot be exercised here (its static initializer does not run under the test host),
    /// so the wrapper's spec branch is reproduced by passing isSpecialized: true.
    /// </summary>
    [TestClass]
    public class BloodlustSalvagingRiderTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        private static double PerRank => D("class_ability_bloodlust_percent_per_rank");
        private static double PerTrained => D("class_ability_bloodlust_salvage_per_trained");
        private static double PerSpec => D("class_ability_bloodlust_salvage_per_spec");

        /// <summary>Reproduces the ability fraction BloodlustAbility.ModifyOutgoingDamage builds.</summary>
        private static double AbilityFraction(int rank, double salvaging, bool specialized) =>
            rank * PerRank + ClassAbilityScaling.Compute(salvaging, specialized, PerTrained, PerSpec) * 0.01;

        // ------------------------------------------------------------------
        // the tunables themselves
        // ------------------------------------------------------------------

        [TestMethod]
        public void SalvageDivisors_SpecIsPinnedEqualToTrained()
        {
            Assert.AreEqual(150.0, PerTrained, 1e-9);
            Assert.AreEqual(PerTrained, PerSpec, 1e-9,
                "Bloodlust's Salvaging rider is intentionally ratio-neutral: an aug-specialized Salvaging " +
                "must pay the same per point as a trained one. Changing only one of these breaks that.");
        }

        [TestMethod]
        public void SalvageSpecDivisor_IsPositive()
        {
            // a 0 (or negative) divisor makes ClassAbilityScaling.Compute return 0, which would delete the
            // rider outright for aug-spec players rather than merely retuning it
            Assert.IsTrue(PerSpec > 0.0, "a non-positive spec divisor silently zeroes the rider");
        }

        [TestMethod]
        public void SpecializingSalvagingIsReachable_ViaTheRetailAugmentation()
        {
            // the mechanism that makes the spec branch live at all - Player.AugSpecSkills maps this
            // property onto Skill.Salvaging, and AugmentationDevice grants it from AugmentationType.Salvage
            Assert.AreEqual(224, (int)PropertyInt.AugmentationSpecializeSalvaging);
        }

        // ------------------------------------------------------------------
        // the rider, at a stated reference skill
        // ------------------------------------------------------------------

        [TestMethod]
        public void SalvagingRider_SpecializedPaysExactlyWhatTrainedPays()
        {
            foreach (var salvaging in new[] { 1.0, 150.0, 300.0, 400.0, 418.0 })
            {
                Assert.AreEqual(
                    ClassAbilityScaling.Compute(salvaging, false, PerTrained, PerSpec),
                    ClassAbilityScaling.Compute(salvaging, true, PerTrained, PerSpec),
                    1e-9,
                    $"specialized Salvaging {salvaging} must pay the trained rate, not a different one");
            }
        }

        [TestMethod]
        public void SalvagingRider_SpecializedContributesTheSameLifestealAsTrained()
        {
            // 300 Salvaging / 150 = +2 percentage points of lifesteal on top of rank 3's 3%
            Assert.AreEqual(0.05, AbilityFraction(3, 300, false), 1e-9);
            Assert.AreEqual(0.05, AbilityFraction(3, 300, true), 1e-9);
        }

        [TestMethod]
        public void SalvagingRider_SpecializedIsNeverZeroedOut()
        {
            // the regression this file exists for: spec must not collapse to the rank-only fraction
            Assert.IsTrue(AbilityFraction(3, 400, true) > 3 * PerRank,
                "an aug-specialized Salvaging still has to move the lifesteal above the flat per-rank value");
        }

        [TestMethod]
        public void SalvagingRider_RankZeroStillCarriesTheRider()
        {
            // Bloodlust's dispatch only runs for a learned rank, but the fraction itself is rank-linear
            // plus the rider - the rider is not gated on rank, in either advancement class
            Assert.AreEqual(400.0 / 150.0 * 0.01, AbilityFraction(0, 400, true), 1e-9);
            Assert.AreEqual(AbilityFraction(0, 400, false), AbilityFraction(0, 400, true), 1e-9);
        }
    }
}
