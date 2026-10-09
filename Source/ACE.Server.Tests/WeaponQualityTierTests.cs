using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Locks the quality-tier ladder. Pure arithmetic over WeaponQualityTiers, no WorldObject needed.
    ///
    /// The thing most worth pinning here is the SCRIPT FALLBACK. Elite (350) and God (375) are declared
    /// with thresholds but carry no visual yet, and the naive reading - "the tier you earned owns the
    /// effect" - would mean a 360 weapon evaluates as Elite, finds no script, and loses the aura it had
    /// at 340 for being better. ScriptFor walks down the ladder to prevent exactly that, and it stops
    /// being exercised the moment Elite gains a script of its own, so it needs a test now.
    /// </summary>
    [TestClass]
    public class WeaponQualityTierTests
    {
        [TestMethod]
        public void FromTotal_BelowExceptional_IsNone()
        {
            Assert.AreEqual(WeaponQualityTier.None, WeaponQualityTiers.FromTotal(0));
            Assert.AreEqual(WeaponQualityTier.None, WeaponQualityTiers.FromTotal(299));
        }

        [TestMethod]
        public void FromTotal_ThresholdsAreInclusive()
        {
            // Repo-owner directive 2026-08-08: >= not >. Exactly on the number earns the tier.
            Assert.AreEqual(WeaponQualityTier.Exceptional, WeaponQualityTiers.FromTotal(300));
            Assert.AreEqual(WeaponQualityTier.Elite, WeaponQualityTiers.FromTotal(350));
            Assert.AreEqual(WeaponQualityTier.God, WeaponQualityTiers.FromTotal(375));
        }

        [TestMethod]
        public void FromTotal_BetweenThresholds_TakesTheLowerTier()
        {
            Assert.AreEqual(WeaponQualityTier.Exceptional, WeaponQualityTiers.FromTotal(349));
            Assert.AreEqual(WeaponQualityTier.Elite, WeaponQualityTiers.FromTotal(374));
        }

        [TestMethod]
        public void FromTotal_AtTheCeiling_IsGod()
        {
            // MaxSpecials 4 x IntensityPercent 100 = 400, the highest total reachable.
            Assert.AreEqual(WeaponQualityTier.God, WeaponQualityTiers.FromTotal(400));
        }

        [TestMethod]
        public void ScriptFor_None_HasNoEffect()
        {
            Assert.IsFalse(WeaponQualityTiers.ScriptFor(WeaponQualityTier.None).HasValue);
        }

        [TestMethod]
        public void ScriptFor_Exceptional_HasAnEffect()
        {
            Assert.IsTrue(WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional).HasValue);
        }

        [TestMethod]
        public void ScriptFor_EveryTier_HasAnEffectAndTheyAreDistinct()
        {
            var exceptional = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Exceptional);
            var elite = WeaponQualityTiers.ScriptFor(WeaponQualityTier.Elite);
            var god = WeaponQualityTiers.ScriptFor(WeaponQualityTier.God);

            Assert.IsTrue(exceptional.HasValue);
            Assert.IsTrue(elite.HasValue);
            Assert.IsTrue(god.HasValue);

            // The tiers must LOOK different or the ladder communicates nothing to a player. This is the
            // assertion that would catch a copy-paste when a fourth tier is added.
            CollectionAssert.AllItemsAreUnique(new[] { exceptional.Value, elite.Value, god.Value });
        }

        [TestMethod]
        public void ScriptFor_NeverReturnsNullForAnEarnedTier()
        {
            // The fallback in ScriptFor is currently a no-op, since every tier carries its own script.
            // It is kept as a guard: a future tier declared with a threshold but no effect must inherit
            // the one below rather than strip the aura from a weapon good enough to reach it. This test
            // states the invariant so that intent survives the fallback looking redundant.
            foreach (var tier in new[] { WeaponQualityTier.Exceptional, WeaponQualityTier.Elite, WeaponQualityTier.God })
                Assert.IsTrue(WeaponQualityTiers.ScriptFor(tier).HasValue, $"{tier} resolved to no script");
        }

        [TestMethod]
        public void ThresholdsAndNames_AreDeclaredForEveryTier()
        {
            Assert.AreEqual(300, WeaponQualityTiers.ThresholdFor(WeaponQualityTier.Exceptional));
            Assert.AreEqual(350, WeaponQualityTiers.ThresholdFor(WeaponQualityTier.Elite));
            Assert.AreEqual(375, WeaponQualityTiers.ThresholdFor(WeaponQualityTier.God));

            Assert.AreEqual("Exceptional", WeaponQualityTiers.NameFor(WeaponQualityTier.Exceptional));
            Assert.AreEqual("Elite", WeaponQualityTiers.NameFor(WeaponQualityTier.Elite));
            Assert.AreEqual("God", WeaponQualityTiers.NameFor(WeaponQualityTier.God));
        }

        [TestMethod]
        public void TotalIntensity_NullWeapon_IsZero()
        {
            Assert.AreEqual(0, WeaponQualityTiers.TotalIntensity(null));
        }
    }
}
