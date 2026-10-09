using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Drain Health's fellowship surplus - the allocation math only.
    ///
    /// Retail Drain caps its transfer at the CASTER'S OWN missing health, so a caster at full health scales
    /// the entire spell to zero and it deals no damage. The fork widens the receiving capacity to the caster
    /// plus their nearby fellows; whatever the caster cannot absorb is split here.
    ///
    /// Only the arithmetic is testable in isolation - the eligibility half (fellowship, landblock, range)
    /// needs a live Player, whose static initializer cannot run under the test host. These tests pin the
    /// three properties the game depends on: the split is weighted to the MOST HURT, nobody ever receives
    /// more than they are missing, and no point is lost to rounding while capacity remains.
    /// </summary>
    [TestClass]
    public class DrainSurplusDistributionTests
    {
        // ------------------------------------------------------------------
        // weighting - the most hurt fellow gets the most
        // ------------------------------------------------------------------

        [TestMethod]
        public void Distribute_TwoFellowsUnequalMissing_SplitsInProportion()
        {
            // 300 missing vs 100 missing, 200 surplus -> 3:1
            var result = DrainSurplusDistribution.Distribute(200, new uint[] { 300, 100 });

            Assert.AreEqual(150u, result[0], "The fellow missing 300 must take three quarters of the surplus.");
            Assert.AreEqual(50u, result[1], "The fellow missing 100 must take one quarter of the surplus.");
            Assert.AreEqual(200u, Total(result), "The whole surplus must be handed out - it is under the total missing.");
        }

        [TestMethod]
        public void Distribute_UnequalMissing_MostHurtNeverReceivesLess()
        {
            var missing = new uint[] { 50, 300, 120 };

            var result = DrainSurplusDistribution.Distribute(100, missing);

            Assert.IsTrue(result[1] > result[2] && result[2] > result[0],
                $"Allocation must be ordered by missing health, got {string.Join(",", result)} for missing {string.Join(",", missing)}.");
        }

        [TestMethod]
        public void Distribute_EqualMissing_SplitsEvenly()
        {
            var result = DrainSurplusDistribution.Distribute(100, new uint[] { 500, 500 });

            Assert.AreEqual(50u, result[0]);
            Assert.AreEqual(50u, result[1]);
        }

        // ------------------------------------------------------------------
        // the per-fellow cap
        // ------------------------------------------------------------------

        [TestMethod]
        public void Distribute_SurplusExceedsTotalMissing_TopsEveryoneUpAndLosesTheRest()
        {
            var result = DrainSurplusDistribution.Distribute(1000, new uint[] { 300, 100 });

            Assert.AreEqual(300u, result[0], "A fellow is never given more than they are missing.");
            Assert.AreEqual(100u, result[1], "A fellow is never given more than they are missing.");
            Assert.AreEqual(400u, Total(result), "The 600 points of surplus beyond the total missing are simply lost.");
        }

        [TestMethod]
        public void Distribute_SurplusExactlyTotalMissing_TopsEveryoneUp()
        {
            var result = DrainSurplusDistribution.Distribute(400, new uint[] { 300, 100 });

            Assert.AreEqual(300u, result[0]);
            Assert.AreEqual(100u, result[1]);
        }

        [TestMethod]
        public void Distribute_NoAllocationEverExceedsMissing()
        {
            var missing = new uint[] { 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233 };

            for (uint surplus = 0; surplus <= 700; surplus++)
            {
                var result = DrainSurplusDistribution.Distribute(surplus, missing);

                for (var i = 0; i < missing.Length; i++)
                    Assert.IsTrue(result[i] <= missing[i],
                        $"surplus {surplus}: fellow {i} was given {result[i]} but is only missing {missing[i]}.");
            }
        }

        // ------------------------------------------------------------------
        // conservation - nothing invented, nothing lost while capacity remains
        // ------------------------------------------------------------------

        [TestMethod]
        public void Distribute_AlwaysHandsOutMinOfSurplusAndTotalMissing()
        {
            var missing = new uint[] { 7, 11, 13, 400, 1 };
            var totalMissing = missing.Aggregate(0u, (a, b) => a + b);

            for (uint surplus = 0; surplus <= 900; surplus++)
            {
                var result = DrainSurplusDistribution.Distribute(surplus, missing);

                Assert.AreEqual(Math.Min(surplus, totalMissing), Total(result),
                    $"surplus {surplus} must distribute exactly min(surplus, {totalMissing}).");
            }
        }

        [TestMethod]
        public void Distribute_AwkwardRounding_LosesNoPoint()
        {
            // 10 across three equal fellows is 3.33 each - the leftover point must still be handed out
            var result = DrainSurplusDistribution.Distribute(10, new uint[] { 100, 100, 100 });

            Assert.AreEqual(10u, Total(result), $"Got {string.Join(",", result)}.");
            Assert.IsTrue(result.All(r => r == 3 || r == 4), $"Got {string.Join(",", result)}.");
        }

        [TestMethod]
        public void Distribute_SinglePointSurplus_GoesToTheMostHurt()
        {
            var result = DrainSurplusDistribution.Distribute(1, new uint[] { 10, 900 });

            Assert.AreEqual(0u, result[0]);
            Assert.AreEqual(1u, result[1]);
        }

        // ------------------------------------------------------------------
        // degenerate inputs - these are the "behave exactly as today" paths
        // ------------------------------------------------------------------

        [TestMethod]
        public void Distribute_NoFellows_ReturnsEmpty()
        {
            Assert.AreEqual(0, DrainSurplusDistribution.Distribute(500, Array.Empty<uint>()).Length,
                "A caster with no eligible fellows must have no surplus to route anywhere.");
        }

        [TestMethod]
        public void Distribute_NullFellows_ReturnsEmpty()
        {
            Assert.AreEqual(0, DrainSurplusDistribution.Distribute(500, null).Length);
        }

        [TestMethod]
        public void Distribute_ZeroSurplus_GivesNothing()
        {
            var result = DrainSurplusDistribution.Distribute(0, new uint[] { 300, 100 });

            Assert.AreEqual(0u, Total(result));
        }

        [TestMethod]
        public void Distribute_AllFellowsAtFullHealth_GivesNothing()
        {
            var result = DrainSurplusDistribution.Distribute(500, new uint[] { 0, 0, 0 });

            Assert.AreEqual(0u, Total(result), "Nobody is missing health, so the whole surplus is lost.");
        }

        [TestMethod]
        public void Distribute_MixOfHurtAndFullHealthFellows_SkipsTheHealthy()
        {
            var result = DrainSurplusDistribution.Distribute(90, new uint[] { 0, 200, 0, 100 });

            Assert.AreEqual(0u, result[0], "A fellow at full health must receive nothing.");
            Assert.AreEqual(0u, result[2], "A fellow at full health must receive nothing.");
            Assert.AreEqual(60u, result[1]);
            Assert.AreEqual(30u, result[3]);
        }

        [TestMethod]
        public void Distribute_SingleFellow_TakesEverythingUpToTheirMissing()
        {
            Assert.AreEqual(120u, DrainSurplusDistribution.Distribute(120, new uint[] { 400 })[0]);
            Assert.AreEqual(400u, DrainSurplusDistribution.Distribute(9000, new uint[] { 400 })[0]);
        }

        // ------------------------------------------------------------------
        // headroom - the roster ceiling is Fellowship.AbsoluteMaxFellows (100)
        // ------------------------------------------------------------------

        [TestMethod]
        public void Distribute_FullRoster_StaysConsistent()
        {
            var missing = new List<uint>();

            for (var i = 0; i < Fellowship.AbsoluteMaxFellows; i++)
                missing.Add((uint)(i + 1));

            var totalMissing = missing.Aggregate(0u, (a, b) => a + b);

            var result = DrainSurplusDistribution.Distribute(2000, missing);

            Assert.AreEqual(Math.Min(2000u, totalMissing), Total(result));

            for (var i = 0; i < missing.Count; i++)
                Assert.IsTrue(result[i] <= missing[i]);
        }

        // ------------------------------------------------------------------
        // the recipient visual - pinning a value that was VERIFIED, not guessed
        // ------------------------------------------------------------------

        /// <summary>
        /// WorldObject.DrainSurplusHealEffect broadcasts PlayScript.HealthUpRed at a fellow who catches part
        /// of the surplus. That value is not an inference from the enum's colour suffix - it was read out of
        /// portal.dat, where all twelve retail Heal Self / Heal Other tiers (spellIds 5, 6, 1157-1166) carry
        /// SpellBase.TargetEffect = 0x1F. Nothing else in this repo plays a HealthUp* script, so if someone
        /// ever renumbers the enum there is no other consumer to catch it. This test is that consumer.
        /// </summary>
        [TestMethod]
        public void HealthUpRed_IsStillTheRetailHealTargetEffect()
        {
            Assert.AreEqual(0x1F, (int)PlayScript.HealthUpRed,
                "portal.dat's Heal Self / Heal Other TargetEffect is 0x1F. If HealthUpRed no longer equals it, the Drain surplus is broadcasting the wrong visual.");

            Assert.AreEqual(0x20, (int)PlayScript.HealthDownRed,
                "0x20 is Harm / Drain Health Other's own TargetEffect - the value immediately above the heal, and the one that would be silently picked up by an off-by-one.");
        }

        private static uint Total(uint[] allocations) => allocations.Aggregate(0u, (a, b) => a + b);
    }
}
