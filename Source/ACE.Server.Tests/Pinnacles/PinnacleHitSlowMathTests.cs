using ACE.Entity.Enum;
using ACE.Server.Pinnacles;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// The hazard hit slow's pure math: the Run-skill debuff sized so the run RATE drops by a target fraction
    /// (MovementSystem.GetRunRate inverted), and the timed attack-speed factor. No world, no PropertyManager.
    /// </summary>
    [TestClass]
    public class PinnacleHitSlowMathTests
    {
        [TestMethod]
        public void RunPointsFor_Run900_DropsBelowTheCap()
            => Assert.AreEqual(101, PinnacleHitSlowMath.RunPointsFor(900, 0.25)); // 4.5 -> 3.375 unreachable above 799; s' = 799

        [TestMethod]
        public void RunPointsFor_Run500_InvertsTheCurve()
            => Assert.AreEqual(340, PinnacleHitSlowMath.RunPointsFor(500, 0.25), 1); // r 2.964 -> 2.223 -> s' 160

        [TestMethod]
        public void RunPointsFor_ZeroFraction_IsZero()
            => Assert.AreEqual(0, PinnacleHitSlowMath.RunPointsFor(500, 0.0));

        [TestMethod]
        public void RunPointsFor_SmallFractionAboveTheCap_StillLandsBelowIt()
        {
            // 4.5 x 0.95 = 4.275 sits above the curve's asymptote (3.75), so the inverse has no finite answer; the
            // nearest reachable point below the 800 jump is 799
            Assert.AreEqual(201, PinnacleHitSlowMath.RunPointsFor(1000, 0.05));
        }

        [TestMethod]
        public void RunPointsFor_NeverNegative_NeverMoreThanTheSkill()
        {
            Assert.AreEqual(0, PinnacleHitSlowMath.RunPointsFor(0, 0.25), "no run skill, nothing to take");
            Assert.AreEqual(0, PinnacleHitSlowMath.RunPointsFor(500, -0.5), "a negative fraction is no slow");
            Assert.AreEqual(0, PinnacleHitSlowMath.RunPointsFor(500, double.NaN), "NaN is no slow");
            Assert.AreEqual(500, PinnacleHitSlowMath.RunPointsFor(500, 1.0), "a full stop takes the whole skill");
            Assert.AreEqual(500, PinnacleHitSlowMath.RunPointsFor(500, 0.9), "below the curve's floor it takes the whole skill");
        }

        [TestMethod]
        public void RunPointsFor_TheSlowedRateIsTheTarget()
        {
            // the discriminating check: the rate at (run - points) is the target rate, within one skill point
            foreach (var run in new[] { 100, 250, 400, 650, 799 })
            {
                var points = PinnacleHitSlowMath.RunPointsFor(run, 0.25);
                var target = ACE.Server.Physics.Animation.MovementSystem.GetRunRate(0f, run, 1f) * 0.75;
                var slowed = ACE.Server.Physics.Animation.MovementSystem.GetRunRate(0f, run - points, 1f);

                Assert.AreEqual(target, slowed, 0.01, $"run {run}");
            }
        }

        [TestMethod]
        public void AttackFactorAt_ExpiresAndDefaultsToOne()
        {
            Assert.AreEqual(0.8, PinnacleHitSlowMath.AttackFactorAt(0.8, 10, 9.9), 1e-9);
            Assert.AreEqual(1.0, PinnacleHitSlowMath.AttackFactorAt(0.8, 10, 10), 1e-9);
        }

        [TestMethod]
        public void AttackFactorAt_UnsetIsExactlyOne_AndTheFactorIsClamped()
        {
            // a player never hit carries the field defaults (1.0, until 0): exactly 1, so GetAnimSpeed is unchanged
            Assert.AreEqual(1.0, PinnacleHitSlowMath.AttackFactorAt(1.0, 0, 1.7e9));
            Assert.AreEqual(0.1, PinnacleHitSlowMath.AttackFactorAt(0.0, 10, 5), 1e-9, "floored at 0.1");
            Assert.AreEqual(1.0, PinnacleHitSlowMath.AttackFactorAt(2.0, 10, 5), 1e-9, "never a speed-up");
            Assert.AreEqual(1.0, PinnacleHitSlowMath.AttackFactorAt(double.NaN, 10, 5), 1e-9, "NaN is no slow");
        }

        [TestMethod]
        public void SlowIdentity_MatchesRetailLeadenFeet()
        {
            Assert.AreEqual(1005u, (uint)SpellId.LeadenFeetOther6);
            Assert.AreEqual(36880u, (uint)(EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.SingleStat | EnchantmentTypeFlags.Additive));
            Assert.AreEqual(36880u, (uint)PinnacleHitSlowMath.RunSlowStatModType);
        }
    }
}
