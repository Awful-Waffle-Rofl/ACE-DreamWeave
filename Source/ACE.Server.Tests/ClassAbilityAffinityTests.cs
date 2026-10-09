using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins <see cref="ClassAbilityAffinity.Multiplier"/>, the MULTIPLICATIVE affinity primitive that scales
    /// a class ability's own rank bonus ("+R of the bonus per 100 points of the affinity skill").
    ///
    /// The pure static only. The Player wrapper cannot be exercised here - this harness can never construct
    /// a live Player, and a PropertyManager read throws without a live config - so nothing below touches
    /// either.
    ///
    /// The neutral cases assert with delta 0.0 (bit-exact) rather than a tolerance, deliberately: the
    /// migration's standing invariant is that at zero affinity skill a migrated ability's output is
    /// BIT-IDENTICAL to rank alone. A 1e-9 tolerance here would let a formula that returns 1.0000000001
    /// pass while silently perturbing every damage number in the game.
    /// </summary>
    [TestClass]
    public class ClassAbilityAffinityTests
    {
        private const double RatePerTrained = 0.12;
        private const double RatePerSpec = 0.17;

        /// <summary>Zero affinity skill is exactly neutral, both advancement classes.</summary>
        [TestMethod]
        public void ZeroEffectiveSkill_IsExactlyNeutral()
        {
            Assert.AreEqual(1.0, ClassAbilityAffinity.Multiplier(0.0, false, RatePerTrained, RatePerSpec), 0.0,
                "zero affinity skill must leave the ability's bonus bit-identical to rank alone");

            Assert.AreEqual(1.0, ClassAbilityAffinity.Multiplier(0.0, true, RatePerTrained, RatePerSpec), 0.0,
                "zero affinity skill must leave the ability's bonus bit-identical to rank alone, Specialized too");
        }

        /// <summary>
        /// A negative effective value (debuffed below zero) is clamped to neutral, never to a PENALTY -
        /// the primitive's contract is "never below 1.0".
        /// </summary>
        [TestMethod]
        public void NegativeEffectiveSkill_IsExactlyNeutral()
        {
            Assert.AreEqual(1.0, ClassAbilityAffinity.Multiplier(-1.0, false, RatePerTrained, RatePerSpec), 0.0);
            Assert.AreEqual(1.0, ClassAbilityAffinity.Multiplier(-400.0, true, RatePerTrained, RatePerSpec), 0.0);
        }

        /// <summary>
        /// A zero or negative rate disables the rider outright, at any skill value. This is how a tunable
        /// set to 0 turns affinity off for an ability without deleting the call site.
        /// </summary>
        [TestMethod]
        public void NonPositiveRate_IsExactlyNeutral()
        {
            Assert.AreEqual(1.0, ClassAbilityAffinity.Multiplier(400.0, false, 0.0, RatePerSpec), 0.0);
            Assert.AreEqual(1.0, ClassAbilityAffinity.Multiplier(400.0, true, RatePerTrained, 0.0), 0.0);
            Assert.AreEqual(1.0, ClassAbilityAffinity.Multiplier(400.0, false, -0.12, RatePerSpec), 0.0);
        }

        /// <summary>The headline contract: +rate of the bonus per 100 points of affinity skill.</summary>
        [TestMethod]
        public void StandardRates_ScalePerHundredPointsOfSkill()
        {
            // 100 Trained at 0.12 -> +12% of the ability's own bonus
            Assert.AreEqual(1.12, ClassAbilityAffinity.Multiplier(100.0, false, RatePerTrained, RatePerSpec), 1e-12);

            // 300 Specialized at 0.17 -> +51%
            Assert.AreEqual(1.51, ClassAbilityAffinity.Multiplier(300.0, true, RatePerTrained, RatePerSpec), 1e-12);

            // linear and uncapped: 400 Trained -> +48%, 400 Specialized -> +68%
            Assert.AreEqual(1.48, ClassAbilityAffinity.Multiplier(400.0, false, RatePerTrained, RatePerSpec), 1e-12);
            Assert.AreEqual(1.68, ClassAbilityAffinity.Multiplier(400.0, true, RatePerTrained, RatePerSpec), 1e-12);

            // fractions of 100 scale proportionally - there is no floor or step
            Assert.AreEqual(1.06, ClassAbilityAffinity.Multiplier(50.0, false, RatePerTrained, RatePerSpec), 1e-12);

            // arbitrary off-standard rates (no ability uses these), to pin that the primitive carries no
            // baked-in constant
            Assert.AreEqual(1.40, ClassAbilityAffinity.Multiplier(200.0, false, 0.20, 0.28), 1e-12);
            Assert.AreEqual(1.56, ClassAbilityAffinity.Multiplier(200.0, true, 0.20, 0.28), 1e-12);
        }

        /// <summary>
        /// The Specialized rate is selected ONLY when isSpecialized is true - a transposed pair of
        /// arguments at a call site would otherwise be invisible.
        /// </summary>
        [TestMethod]
        public void SpecializedRate_IsSelectedOnlyWhenSpecialized()
        {
            // distinct, deliberately unequal rates so neither can stand in for the other
            const double trained = 0.10;
            const double specialized = 0.50;

            Assert.AreEqual(1.10, ClassAbilityAffinity.Multiplier(100.0, false, trained, specialized), 1e-12);
            Assert.AreEqual(1.50, ClassAbilityAffinity.Multiplier(100.0, true, trained, specialized), 1e-12);

            // and a Specialized source is strictly stronger at the standard rates
            Assert.IsTrue(ClassAbilityAffinity.Multiplier(250.0, true, RatePerTrained, RatePerSpec)
                        > ClassAbilityAffinity.Multiplier(250.0, false, RatePerTrained, RatePerSpec));
        }
    }
}
