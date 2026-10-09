using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Bloodlust (Berserker T3) - the Salvaging lifesteal affinity, and specifically that SPECIALIZED
    /// Salvaging pays exactly what TRAINED Salvaging pays and no more.
    ///
    /// The trap this pins: Salvaging looks unspecializable (the dat prices the trained -> specialized
    /// upgrade at 999 credits, so no player can buy it with skill credits), and the tunable's own
    /// description used to assert outright that "Salvaging cannot specialize". It can. Retail sells an
    /// augmentation - PropertyInt.AugmentationSpecializeSalvaging, the
    /// "gemaugmentationtinkeringspecsalv" gem - that sets the skill to SkillAdvancementClass.Specialized
    /// permanently, and Player_Skills re-applies it after an Asheron's Castle / Enlightenment reset.
    ///
    /// So the spec branch is LIVE code, and it is reached WITHOUT spending a skill credit. That is the whole
    /// reason the two rates are pinned equal: every other affinity ability charges a credit for the tighter
    /// specialized rate, and Bloodlust's source does not, so paying it the shared spec rate would hand aug
    /// holders power no build trades anything for. Two failure directions are both guarded below - a spec
    /// rate BELOW trained silently taxes the augmentation, and a spec rate ABOVE trained (for instance by
    /// "simplifying" this back onto the shared pair, whose two rates are deliberately UNEQUAL to each
    /// other) is the regression this file exists for.
    ///
    /// REWRITTEN 2026-09-12 for the multiplicative affinity primitive. This file previously reproduced
    /// ClassAbilityScaling.Compute and the two class_ability_bloodlust_salvage_per_* DIVISORS, which the
    /// ability stopped reading in the overhaul - six green tests that proved nothing about live lifesteal.
    /// The keys are gone and the assertions below drive ClassAbilityAffinity.Multiplier, which is what
    /// BloodlustAbility.ModifyOutgoingDamage actually calls.
    ///
    /// Player itself cannot be exercised here (its static initializer does not run under the test host), so
    /// the Player wrapper's spec branch is reproduced by passing isSpecialized: true.
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
        private static double RateTrained => D("class_ability_affinity_bloodlust_rate_per_trained");
        private static double RateSpec => D("class_ability_affinity_bloodlust_rate_per_spec");

        /// <summary>
        /// Reproduces the ability fraction BloodlustAbility.ModifyOutgoingDamage builds: the rank bonus
        /// MULTIPLIED by the affinity factor, not a rider added beside it.
        /// </summary>
        private static double AbilityFraction(int rank, double salvaging, bool specialized) =>
            rank * PerRank * ClassAbilityAffinity.Multiplier(salvaging, specialized, RateTrained, RateSpec);

        // ------------------------------------------------------------------
        // the tunables themselves
        // ------------------------------------------------------------------

        [TestMethod]
        public void SalvageRates_SpecIsPinnedEqualToTrained()
        {
            Assert.AreEqual(0.12, RateTrained, 1e-9);
            Assert.AreEqual(RateTrained, RateSpec, 1e-9,
                "Bloodlust's Salvaging affinity is intentionally ratio-neutral: an aug-specialized Salvaging " +
                "must pay the same per point as a trained one. Changing only one of these breaks that.");
        }

        [TestMethod]
        public void SalvageRates_AreBloodlustsOwn_NotTheSharedPair()
        {
            // the regression route this file guards: dropping Bloodlust back onto the shared overload. The
            // shared pair is deliberately UNEQUAL, so inheriting it silently unpins spec from trained.
            Assert.AreNotEqual(D("class_ability_affinity_rate_per_trained"), D("class_ability_affinity_rate_per_spec"),
                "the shared affinity pair is expected to be unequal - if it ever becomes equal, Bloodlust's " +
                "own pair is no longer load-bearing and this whole file should be revisited");

            Assert.AreNotEqual(D("class_ability_affinity_rate_per_spec"), RateSpec, 1e-9,
                "Bloodlust must NOT scale at the shared specialized rate - that is power no build pays for");
        }

        [TestMethod]
        public void SalvageSpecRate_IsPositive()
        {
            // a 0 (or negative) rate makes ClassAbilityAffinity.Multiplier return exactly 1.0, which would
            // delete the affinity outright for aug-spec players rather than merely retuning it
            Assert.IsTrue(RateSpec > 0.0, "a non-positive spec rate silently flattens the affinity to 1.0");
        }

        [TestMethod]
        public void SpecializingSalvagingIsReachable_ViaTheRetailAugmentation()
        {
            // the mechanism that makes the spec branch live at all - Player.AugSpecSkills maps this
            // property onto Skill.Salvaging, and AugmentationDevice grants it from AugmentationType.Salvage
            Assert.AreEqual(224, (int)PropertyInt.AugmentationSpecializeSalvaging);
        }

        // ------------------------------------------------------------------
        // the affinity, at a stated reference skill
        // ------------------------------------------------------------------

        [TestMethod]
        public void SalvagingAffinity_SpecializedPaysExactlyWhatTrainedPays()
        {
            foreach (var salvaging in new[] { 1.0, 150.0, 300.0, 400.0, 418.0 })
            {
                Assert.AreEqual(
                    ClassAbilityAffinity.Multiplier(salvaging, false, RateTrained, RateSpec),
                    ClassAbilityAffinity.Multiplier(salvaging, true, RateTrained, RateSpec),
                    1e-9,
                    $"specialized Salvaging {salvaging} must pay the trained rate, not a different one");
            }
        }

        [TestMethod]
        public void SalvagingAffinity_SpecializedContributesTheSameLifestealAsTrained()
        {
            // 300 Salvaging at 0.12 per 100 = a 1.36x factor on rank 3's 4.5%, giving 6.12%
            Assert.AreEqual(0.0612, AbilityFraction(3, 300, false), 1e-9);
            Assert.AreEqual(0.0612, AbilityFraction(3, 300, true), 1e-9);
        }

        [TestMethod]
        public void SalvagingAffinity_SpecializedIsNeverZeroedOut()
        {
            // the regression this file exists for: spec must not collapse to the rank-only fraction
            Assert.IsTrue(AbilityFraction(3, 400, true) > 3 * PerRank,
                "an aug-specialized Salvaging still has to move the lifesteal above the flat per-rank value");
        }

        [TestMethod]
        public void SalvagingAffinity_IsNeutralNotNegative_WithoutTheSkill()
        {
            // Multiplier floors at exactly 1.0, so an Untrained/Inactive Salvaging leaves the heal
            // bit-identical to rank alone rather than shrinking it
            Assert.AreEqual(3 * PerRank, AbilityFraction(3, 0, false), 1e-9);
            Assert.AreEqual(3 * PerRank, AbilityFraction(3, 0, true), 1e-9);
        }

        [TestMethod]
        public void SalvagingAffinity_IsGatedOnRank_UnlikeTheLegacyRider()
        {
            // BEHAVIOR CHANGE, pinned deliberately. The retired additive rider paid out at rank 0 (it was
            // summed beside the rank term, not gated on it); a multiplier on a zero rank bonus is zero. No
            // live path feeds rank 0 here - the dispatch only runs for a learned rank - but the difference
            // is real and a future reader comparing against the old file should see it asserted, not infer
            // that something was lost.
            Assert.AreEqual(0.0, AbilityFraction(0, 400, false), 1e-9);
            Assert.AreEqual(0.0, AbilityFraction(0, 400, true), 1e-9);
        }
    }
}
