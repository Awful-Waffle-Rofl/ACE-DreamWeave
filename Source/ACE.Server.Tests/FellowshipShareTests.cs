using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The fellowship EvenShare XP curve (<see cref="Fellowship.GetMemberSharePercent(int, double)"/>).
    ///
    /// Two properties matter and are easy to break:
    ///   1. Sizes 1-9 must reproduce the retail table exactly, so raising 'fellowship_max_members' can never
    ///      change what an existing 9-or-fewer fellowship earns.
    ///   2. No roster size may return a share above the retail plateau. The implementation this replaced was
    ///      a switch over cases 1-9 that fell through to `return 1.0`, which would have handed every member of
    ///      a 10-person fellowship the full unsplit XP.
    /// </summary>
    [TestClass]
    public class FellowshipShareTests
    {
        private const double DefaultPlateau = 2.7;   // = 9 * 0.3, the retail total at the top of the table

        /// <summary>Retail per-member share for sizes 1..9.</summary>
        private static readonly double[] Retail = { 1.0, .75, .6, .55, .5, .45, .4, .35, .3 };

        [TestMethod]
        public void Sizes1Through9_MatchRetailTableExactly()
        {
            for (var size = 1; size <= 9; size++)
                Assert.AreEqual(Retail[size - 1], Fellowship.GetMemberSharePercent(size, DefaultPlateau), 1e-9, $"size {size}");
        }

        [TestMethod]
        public void RetailTotalGroupXp_PlateausAndNeverExceeds2Point8()
        {
            // total group throughput = size * per-member share. Retail rises to 2.8x at 7-8 and eases to 2.7x at 9.
            for (var size = 1; size <= 9; size++)
            {
                var total = size * Fellowship.GetMemberSharePercent(size, DefaultPlateau);
                Assert.IsTrue(total <= 2.8 + 1e-9, $"size {size} total {total} exceeded the retail plateau");
            }

            Assert.AreEqual(2.8, 7 * Fellowship.GetMemberSharePercent(7, DefaultPlateau), 1e-9);
            Assert.AreEqual(2.8, 8 * Fellowship.GetMemberSharePercent(8, DefaultPlateau), 1e-9);
            Assert.AreEqual(2.7, 9 * Fellowship.GetMemberSharePercent(9, DefaultPlateau), 1e-9);
        }

        [TestMethod]
        public void PastRetailTable_HoldsTotalGroupXpFlatAtThePlateau()
        {
            for (var size = 10; size <= Fellowship.AbsoluteMaxFellows; size++)
                Assert.AreEqual(DefaultPlateau, size * Fellowship.GetMemberSharePercent(size, DefaultPlateau), 1e-9, $"size {size}");
        }

        [TestMethod]
        public void GroupTotalIsIdenticalAt9And20()
        {
            // the design requirement: a fellowship of 9 must not be behind a fellowship of 20. Past the retail
            // table total group XP is flat, so the two are exactly equal and there is no reason to zerg.
            var at9 = 9 * Fellowship.GetMemberSharePercent(9, DefaultPlateau);
            var at20 = 20 * Fellowship.GetMemberSharePercent(20, DefaultPlateau);

            Assert.AreEqual(at9, at20, 1e-9);
            Assert.AreEqual(2.7, at20, 1e-9);
        }

        [TestMethod]
        public void PerMemberShareAtTheDefaultCapOf20()
        {
            // 2.7 / 20 - each member of a full 20-fellowship earns 13.5% of the kill
            Assert.AreEqual(0.135, Fellowship.GetMemberSharePercent(20, DefaultPlateau), 1e-9);
        }

        [TestMethod]
        public void GrowingPast9NeverRaisesTotalGroupXp()
        {
            // no fellowship size may out-earn the 7-8 member retail peak in aggregate
            var peak = 8 * Fellowship.GetMemberSharePercent(8, DefaultPlateau);

            for (var size = 9; size <= Fellowship.AbsoluteMaxFellows; size++)
            {
                var total = size * Fellowship.GetMemberSharePercent(size, DefaultPlateau);
                Assert.IsTrue(total <= peak + 1e-9, $"size {size} total {total} exceeded the {peak} peak");
            }
        }

        [TestMethod]
        public void CurveIsContinuousAcrossTheRetailBoundary()
        {
            // 9 comes from the table, 10 from the formula; the per-member share must not jump between them
            var at9 = Fellowship.GetMemberSharePercent(9, DefaultPlateau);
            var at10 = Fellowship.GetMemberSharePercent(10, DefaultPlateau);

            Assert.AreEqual(0.3, at9, 1e-9);
            Assert.AreEqual(0.27, at10, 1e-9);
            Assert.IsTrue(at10 < at9, "per-member share must keep falling as the fellowship grows");
        }

        [TestMethod]
        public void PerMemberShareIsMonotonicallyNonIncreasing()
        {
            // adding a member must never raise anyone's individual share, at any size
            for (var size = 2; size <= Fellowship.AbsoluteMaxFellows; size++)
            {
                var prev = Fellowship.GetMemberSharePercent(size - 1, DefaultPlateau);
                var curr = Fellowship.GetMemberSharePercent(size, DefaultPlateau);

                Assert.IsTrue(curr <= prev + 1e-9, $"share rose from {prev} to {curr} going from {size - 1} to {size} members");
            }
        }

        [TestMethod]
        public void NoSizeAboveOneEverReturnsFullShare()
        {
            // regression: the old switch fell through to `return 1.0` for any count > 9
            for (var size = 2; size <= Fellowship.AbsoluteMaxFellows; size++)
                Assert.IsTrue(Fellowship.GetMemberSharePercent(size, DefaultPlateau) < 1.0, $"size {size} returned a full share");
        }

        [TestMethod]
        public void DegenerateSizes_ReturnFullShare()
        {
            Assert.AreEqual(1.0, Fellowship.GetMemberSharePercent(1, DefaultPlateau), 1e-9);
            Assert.AreEqual(1.0, Fellowship.GetMemberSharePercent(0, DefaultPlateau), 1e-9);
            Assert.AreEqual(1.0, Fellowship.GetMemberSharePercent(-5, DefaultPlateau), 1e-9);
        }

        [TestMethod]
        public void NonPositivePlateau_FallsBackToRetailInsteadOfZeroingXp()
        {
            // a misconfigured 'fellowship_share_group_plateau' must not silently zero out everyone's XP
            foreach (var badPlateau in new[] { 0.0, -1.0 })
            {
                var share = Fellowship.GetMemberSharePercent(12, badPlateau);

                Assert.IsTrue(share > 0.0, $"plateau {badPlateau} produced a zero share");
                Assert.AreEqual(DefaultPlateau, 12 * share, 1e-9, $"plateau {badPlateau} should fall back to retail");
            }
        }

        [TestMethod]
        public void RaisingThePlateau_ScalesOnlyBeyondTheRetailTable()
        {
            // a server that wants big fellowships to out-earn small ones raises the plateau; sizes 1-9 must not move
            const double raised = 4.0;

            for (var size = 1; size <= 9; size++)
                Assert.AreEqual(Retail[size - 1], Fellowship.GetMemberSharePercent(size, raised), 1e-9, $"size {size} moved");

            Assert.AreEqual(raised, 15 * Fellowship.GetMemberSharePercent(15, raised), 1e-9);
        }
    }
}
