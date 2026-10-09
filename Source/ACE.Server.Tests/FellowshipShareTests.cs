using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
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

    /// <summary>
    /// <see cref="Fellowship.GetPresentShareTotal"/> - the fellowship-size multiplier is driven by how many
    /// members would actually receive a share from THIS earner (a positive GetDistanceScalar), not by the
    /// whole roster. An absent fellow (scalar 0.0) neither receives nor dilutes. Distance rules themselves
    /// are out of scope here - this is pure denominator behavior.
    /// </summary>
    [TestClass]
    public class FellowshipPresentShareTests
    {
        private const double DefaultPlateau = 2.7;
        private const ulong Amount = 1000;

        [TestMethod]
        public void SoloEarner_ReturnsFullAmount()
        {
            Assert.AreEqual(1000ul, Fellowship.GetPresentShareTotal(Amount, new List<double> { 1.0 }, DefaultPlateau));
        }

        [TestMethod]
        public void TwoPresent_ReturnsRetailTwoShare()
        {
            Assert.AreEqual(750ul, Fellowship.GetPresentShareTotal(Amount, new List<double> { 1.0, 1.0 }, DefaultPlateau));
        }

        [TestMethod]
        public void OneAbsentFellow_EarnerKeepsSoloAmount()
        {
            // the behaviour change: an absent fellow (scalar 0.0) does not dilute the earner's share
            Assert.AreEqual(1000ul, Fellowship.GetPresentShareTotal(Amount, new List<double> { 1.0, 0.0 }, DefaultPlateau));
        }

        [TestMethod]
        public void ThreeAbsentFellows_EarnerKeepsSoloAmount()
        {
            Assert.AreEqual(1000ul, Fellowship.GetPresentShareTotal(Amount, new List<double> { 1.0, 0.0, 0.0, 0.0 }, DefaultPlateau));
        }

        [TestMethod]
        public void TwoPresentOneAbsent_AbsentIgnoredFromDenominator()
        {
            Assert.AreEqual(750ul, Fellowship.GetPresentShareTotal(Amount, new List<double> { 1.0, 1.0, 0.0 }, DefaultPlateau));
        }

        [TestMethod]
        public void FellowInTaperRange_CountsAsPresent()
        {
            // a fellow scaled to 0.5 by distance (the 600-1200 taper) is still present, not absent
            Assert.AreEqual(750ul, Fellowship.GetPresentShareTotal(Amount, new List<double> { 1.0, 0.5 }, DefaultPlateau));
        }

        [TestMethod]
        public void NinePresent_MatchesRetailPlateau()
        {
            var scalars = new List<double>();
            for (var i = 0; i < 9; i++)
                scalars.Add(1.0);

            Assert.AreEqual(300ul, Fellowship.GetPresentShareTotal(Amount, scalars, DefaultPlateau));
        }

        [TestMethod]
        public void NinePresentPlusElevenAbsent_AbsentMembersDoNotPushOntoThePlateauCurve()
        {
            var scalars = new List<double>();
            for (var i = 0; i < 9; i++)
                scalars.Add(1.0);
            for (var i = 0; i < 11; i++)
                scalars.Add(0.0);

            Assert.AreEqual(300ul, Fellowship.GetPresentShareTotal(Amount, scalars, DefaultPlateau));
        }

        [TestMethod]
        public void TwentyPresent_MatchesTodaysCapBehaviour()
        {
            var scalars = new List<double>();
            for (var i = 0; i < 20; i++)
                scalars.Add(1.0);

            Assert.AreEqual(135ul, Fellowship.GetPresentShareTotal(Amount, scalars, DefaultPlateau));
        }

        [TestMethod]
        public void EmptyOrAllAbsent_TreatedAsOnePresent()
        {
            // guard: the earner is always present, so an empty collection or an all-zero collection (which
            // should not happen in practice - the earner's own scalar is always 1.0) falls back to solo.
            Assert.AreEqual(1000ul, Fellowship.GetPresentShareTotal(Amount, new List<double>(), DefaultPlateau));
            Assert.AreEqual(1000ul, Fellowship.GetPresentShareTotal(Amount, new List<double> { 0.0 }, DefaultPlateau));
        }
    }

    /// <summary>
    /// <see cref="Fellowship.GetLevelWeight"/> - the per-member multiplier for the level-weighted
    /// (non-EvenShare) branch of SplitXp/SplitLuminance. Normalised so weights sum to the present count,
    /// which is what makes the weighted branch pay out the same group total as the EvenShare branch.
    /// </summary>
    [TestClass]
    public class FellowshipLevelWeightTests
    {
        private const double DefaultPlateau = 2.7;
        private const ulong Amount = 1000;

        [TestMethod]
        public void EqualXpToNext_EveryWeightIsExactlyOne()
        {
            foreach (var n in new[] { 2, 5, 9, 20 })
            {
                const ulong perMember = 1000;
                var sum = perMember * (ulong)n;

                for (var i = 0; i < n; i++)
                    Assert.AreEqual(1.0, Fellowship.GetLevelWeight(perMember, sum, n), 1e-9, $"n={n}");
            }
        }

        [TestMethod]
        public void MixedSet_WeightsSumToPresentCount()
        {
            ulong[] xpToNext = { 100, 300, 600 };
            var sum = xpToNext.Aggregate(0ul, (a, b) => a + b);
            var count = xpToNext.Length;

            var totalWeight = xpToNext.Sum(x => Fellowship.GetLevelWeight(x, sum, count));

            Assert.AreEqual((double)count, totalWeight, 1e-9);
        }

        [TestMethod]
        public void LowLevelMember_GetsSmallButNonZeroWeightAgainstHighLevel()
        {
            ulong low = 3_000;
            ulong high = 400_000;
            var sum = low + high;

            var lowWeight = Fellowship.GetLevelWeight(low, sum, 2);
            var highWeight = Fellowship.GetLevelWeight(high, sum, 2);

            Assert.IsTrue(lowWeight > 0.0, "low-level weight must be non-zero");
            Assert.IsTrue(lowWeight < highWeight, "low-level weight must be smaller than the high-level weight");
            Assert.AreEqual(2.0, lowWeight + highWeight, 1e-9);
        }

        [TestMethod]
        public void ZeroXpToNextLevelSum_ReturnsOne()
        {
            Assert.AreEqual(1.0, Fellowship.GetLevelWeight(0, 0, 3), 1e-9);
        }

        [TestMethod]
        public void NonPositivePresentCount_ReturnsOne()
        {
            Assert.AreEqual(1.0, Fellowship.GetLevelWeight(500, 1000, 0), 1e-9);
            Assert.AreEqual(1.0, Fellowship.GetLevelWeight(500, 1000, -1), 1e-9);
        }

        [TestMethod]
        public void GroupTotalParity_WeightedBranchPaysTheSameGroupTotalAsEvenShare()
        {
            // build totalAmount the same way SplitXp does (present-scalar-aware), then confirm that
            // sum(totalAmount * weight_i) == presentCount * totalAmount within rounding - i.e. the
            // weighted branch and the EvenShare branch distribute the same group total.
            ulong[] xpToNext = { 100, 300, 600 };
            var count = xpToNext.Length;
            var sum = xpToNext.Aggregate(0ul, (a, b) => a + b);

            var scalars = new List<double> { 1.0, 1.0, 1.0 };
            var totalAmount = Fellowship.GetPresentShareTotal(Amount, scalars, DefaultPlateau);

            var distributed = xpToNext.Sum(x => totalAmount * Fellowship.GetLevelWeight(x, sum, count));

            Assert.AreEqual(count * (double)totalAmount, distributed, 1e-6);
        }
    }

    /// <summary>
    /// <see cref="Fellowship.GetDistanceScalar(Position, Position)"/> - the pure indoor/outdoor, same-instance,
    /// same-indoor-landblock and distance-falloff rule shared by kill XP/luminance sharing and (as of the fix
    /// this covers) kill-task credit via <see cref="Fellowship.WithinRange(WorldObjects.Player, bool)"/>. Built
    /// directly against <see cref="Position"/> because PropertyManager reads throw under the test host, so a
    /// live Player cannot be constructed here.
    /// </summary>
    [TestClass]
    public class FellowshipDistanceScalarTests
    {
        // Outdoor cell ids have low-16-bit cell value < 0x100; indoor is >= 0x100 (LandblockId.Indoors).
        private const ushort OutdoorCell = 0x0001;
        private const ushort IndoorCell = 0x0100;

        private static Position MakePosition(byte landblockX, byte landblockY, ushort cell, float x, float y, uint instance = 0)
        {
            var blockCellId = ((uint)landblockX << 24) | ((uint)landblockY << 16) | cell;
            return new Position(blockCellId, x, y, 0f, 0f, 0f, 0f, 1f, instance);
        }

        [TestMethod]
        public void Outdoor_SameLandblock_Close_ReturnsFullShare()
        {
            var earner = MakePosition(10, 10, OutdoorCell, 0f, 0f);
            var fellow = MakePosition(10, 10, OutdoorCell, 10f, 10f);

            Assert.AreEqual(1.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void Outdoor_ExactlyAtMaxDistance_ReturnsFullShare()
        {
            var earner = MakePosition(10, 10, OutdoorCell, 0f, 0f);
            var fellow = MakePosition(10, 10, OutdoorCell, 600f, 0f);

            Assert.AreEqual(600.0, earner.Distance2D(fellow), 1e-3, "test setup: distance must be exactly MaxDistance");
            Assert.AreEqual(1.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void Outdoor_900ApartAcrossLandblocks_ReturnsHalfShare()
        {
            // dx = (5 - 0) * 192 + (0 - 60) = 900
            var earner = MakePosition(5, 0, OutdoorCell, 0f, 0f);
            var fellow = MakePosition(0, 0, OutdoorCell, 60f, 0f);

            Assert.AreEqual(900.0, earner.Distance2D(fellow), 1e-3, "test setup: distance must be 900");
            Assert.AreEqual(0.5, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void Outdoor_AtOrBeyond1200_ReturnsZero()
        {
            // dx = (7 - 0) * 192 = 1344
            var earner = MakePosition(7, 0, OutdoorCell, 0f, 0f);
            var fellow = MakePosition(0, 0, OutdoorCell, 0f, 0f);

            Assert.AreEqual(1344.0, earner.Distance2D(fellow), 1e-3, "test setup: distance must be >= 1200");
            Assert.AreEqual(0.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void IndoorVsOutdoor_ReturnsZero()
        {
            var earner = MakePosition(10, 10, OutdoorCell, 0f, 0f);
            var fellow = MakePosition(10, 10, IndoorCell, 5f, 5f);

            // guard against the test becoming a no-op if MakePosition's cell thresholds are ever wrong
            Assert.IsFalse(earner.Indoors);
            Assert.IsTrue(fellow.Indoors);

            Assert.AreEqual(0.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void Indoor_SameLandblockSameInstance_AtMaxDistance_ReturnsFullShare()
        {
            // Distance2D does not distinguish indoor from outdoor once the landblocks match, so the same
            // same-landblock branch (plain Euclidean distance, no *192 offset) and the same MaxDistance=600
            // cutoff apply here as in the outdoor same-landblock case above.
            var earner = MakePosition(20, 20, IndoorCell, 0f, 0f);
            var fellow = MakePosition(20, 20, IndoorCell, 600f, 0f);

            Assert.IsTrue(earner.Indoors && fellow.Indoors);
            Assert.AreEqual(earner.InstancedLandblock, fellow.InstancedLandblock);
            Assert.AreEqual(600.0, earner.Distance2D(fellow), 1e-3, "test setup: distance must be exactly MaxDistance");
            Assert.AreEqual(1.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void Indoor_DifferentLandblock_ReturnsZero()
        {
            var earner = MakePosition(20, 20, IndoorCell, 0f, 0f);
            var fellow = MakePosition(21, 20, IndoorCell, 0f, 0f);

            Assert.IsTrue(earner.Indoors && fellow.Indoors);
            Assert.AreNotEqual(earner.InstancedLandblock, fellow.InstancedLandblock);

            Assert.AreEqual(0.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void Outdoor_SameCoordinates_DifferentInstance_ReturnsZero()
        {
            var earner = MakePosition(10, 10, OutdoorCell, 0f, 0f, instance: 0);
            var fellow = MakePosition(10, 10, OutdoorCell, 0f, 0f, instance: 1);

            // if the instance guard were removed, this would fall through to the same-landblock branch and
            // return the full share (distance 0) - the assertion must fail in that case to be discriminating.
            Assert.AreEqual(0.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void Indoor_SameCoordinates_DifferentInstance_ReturnsZero()
        {
            var earner = MakePosition(20, 20, IndoorCell, 0f, 0f, instance: 0);
            var fellow = MakePosition(20, 20, IndoorCell, 0f, 0f, instance: 2);

            // NOTE: unlike the outdoor case above, this one does not by itself discriminate the new top-level
            // Instance guard - InstancedLandblock already folds Instance into its comparison, so the pre-existing
            // "different indoor landblock" check already zeroed this case out. Kept as a confirming regression
            // test for the indoor side of the instance rule, not as proof the guard exists.
            Assert.AreEqual(0.0, Fellowship.GetDistanceScalar(earner, fellow), 1e-9);
        }

        [TestMethod]
        public void NullEarner_ReturnsZeroWithoutThrowing()
        {
            var fellow = MakePosition(10, 10, OutdoorCell, 0f, 0f);

            Assert.AreEqual(0.0, Fellowship.GetDistanceScalar(null, fellow), 1e-9);
        }

        [TestMethod]
        public void NullFellow_ReturnsZeroWithoutThrowing()
        {
            var earner = MakePosition(10, 10, OutdoorCell, 0f, 0f);

            Assert.AreEqual(0.0, Fellowship.GetDistanceScalar(earner, null), 1e-9);
        }
    }
}
