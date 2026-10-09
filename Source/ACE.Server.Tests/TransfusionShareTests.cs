using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Transfusion's DELIVERY FRACTION - how much of a Drain Health surplus reaches the fellows.
    ///
    /// Rank sets the base fraction (40/70/100% at ranks 1/2/3) and a dual-ratio Healing rider is ADDITIVE on
    /// top of it, deliberately uncapped, so a skilled healer delivers more than the drain produced. The
    /// fraction is applied at distribution time only, after every cap has run, which is what makes Drain
    /// damage identical at every rank - that property is the whole reason this lever was chosen.
    ///
    /// The rider's own dual-ratio arithmetic is ClassAbilityScaling.Compute, covered by its own tests; what
    /// is pinned here is that Transfusion composes it ADDITIVELY and does not clamp the result at 1.0.
    /// </summary>
    [TestClass]
    public class TransfusionShareTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        private static double R1 => D("class_ability_transfusion_share_r1");
        private static double R2 => D("class_ability_transfusion_share_r2");
        private static double R3 => D("class_ability_transfusion_share_r3");

        private static double Share(int rank, double rider = 0.0) =>
            DrainSurplusDistribution.ShareFraction(rank, R1, R2, R3, rider);

        // ------------------------------------------------------------------
        // the tunables themselves
        // ------------------------------------------------------------------

        [TestMethod]
        public void Tunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.40, R1, 1e-9);
            Assert.AreEqual(0.70, R2, 1e-9);
            Assert.AreEqual(1.00, R3, 1e-9);

            // Transfusion's own per-ability Healing divisors were deleted with the 2026-09-12 migration; it
            // now reads the SHARED multiplicative affinity rate pair, covered by ClassAbilityAffinityTests.
        }

        // ------------------------------------------------------------------
        // the per-rank fraction
        // ------------------------------------------------------------------

        [TestMethod]
        public void ShareFraction_ScalesFortyToOneHundredPercentByRank()
        {
            Assert.AreEqual(0.40, Share(1), 1e-9);
            Assert.AreEqual(0.70, Share(2), 1e-9);
            Assert.AreEqual(1.00, Share(3), 1e-9);
        }

        [TestMethod]
        public void ShareFraction_RankZero_DeliversNothing()
        {
            Assert.AreEqual(0.0, Share(0), 1e-9,
                "Rank 0 is the retail path - though the eligibility gate rejects it before this is ever reached.");
        }

        [TestMethod]
        public void ShareFraction_NegativeRank_DeliversNothing()
        {
            Assert.AreEqual(0.0, Share(-1), 1e-9);
        }

        [TestMethod]
        public void ShareFraction_RankAboveMax_BehavesAsMaxRank()
        {
            // the eligibility gate treats ANY rank >= 1 as owning the ability, so an out-of-range persisted
            // rank must not silently deliver zero - it clamps to the top rank instead
            Assert.AreEqual(Share(3), Share(4), 1e-9);
            Assert.AreEqual(Share(3), Share(99), 1e-9);
        }

        [TestMethod]
        public void ShareFraction_RankZeroWithRider_StillDeliversNothing()
        {
            Assert.AreEqual(0.0, Share(0, 0.50), 1e-9,
                "The rider must never resurrect an unlearned ability.");
        }

        // ------------------------------------------------------------------
        // the Healing rider - additive, and uncapped
        // ------------------------------------------------------------------

        [TestMethod]
        public void ShareFraction_RiderIsAdditiveNotMultiplicative()
        {
            Assert.AreEqual(0.48, Share(1, 0.08), 1e-9, "0.40 + 0.08, not 0.40 * 1.08.");
            Assert.AreEqual(0.78, Share(2, 0.08), 1e-9);
            Assert.AreEqual(1.08, Share(3, 0.08), 1e-9);
        }

        [TestMethod]
        public void ShareFraction_At300HealingMatchesTheSharedMultiplicativeModel()
        {
            // Healing is now MULTIPLICATIVE on the rank's own share fraction (2026-09-12 overhaul), via the
            // shared 0.09 Trained / 0.14 Specialized rate pair - not a per-ability divisor. Reproduced here
            // so the shared rate cannot drift away from the stated design without a test failing.
            var rateTrained = D("class_ability_affinity_rate_per_trained");
            var rateSpec = D("class_ability_affinity_rate_per_spec");

            var trainedMultiplier = ClassAbilityAffinity.Multiplier(300, false, rateTrained, rateSpec);
            var specializedMultiplier = ClassAbilityAffinity.Multiplier(300, true, rateTrained, rateSpec);

            Assert.AreEqual(1.27, trainedMultiplier, 1e-9);
            Assert.AreEqual(1.42, specializedMultiplier, 1e-9);

            var rankBonus = Share(3);
            var trainedRider = rankBonus * trainedMultiplier - rankBonus;
            var specializedRider = rankBonus * specializedMultiplier - rankBonus;

            Assert.AreEqual(1.27, Share(3, trainedRider), 1e-9, "100% -> 127% delivered at rank 3 with 300 Trained Healing.");
            Assert.AreEqual(1.42, Share(3, specializedRider), 1e-9, "142% delivered at rank 3 with 300 Specialized Healing.");

            Assert.IsTrue(specializedRider > trainedRider, "Specialized must always out-scale Trained on the same skill value.");
        }

        [TestMethod]
        public void ShareFraction_IsNotClampedAtOne()
        {
            // over 100% is INTENDED - Distribute caps each fellow at their own missing health, so the extra
            // reaches more of the party instead of overhealing anyone
            Assert.IsTrue(Share(3, 0.50) > 1.0, $"Expected an uncapped share, got {Share(3, 0.50)}.");
            Assert.AreEqual(1.50, Share(3, 0.50), 1e-9);
        }

        [TestMethod]
        public void ShareFraction_NeverGoesNegative()
        {
            // an untrained Healing source returns 0 from GetClassAbilityScaling, but pin the floor anyway
            Assert.AreEqual(0.0, Share(1, -5.0), 1e-9);
        }

        // ------------------------------------------------------------------
        // ApplyShare - the fraction in whole points
        // ------------------------------------------------------------------

        [TestMethod]
        public void ApplyShare_ScalesTheSurplus()
        {
            Assert.AreEqual(40u, DrainSurplusDistribution.ApplyShare(100, 0.40));
            Assert.AreEqual(70u, DrainSurplusDistribution.ApplyShare(100, 0.70));
            Assert.AreEqual(100u, DrainSurplusDistribution.ApplyShare(100, 1.00));
        }

        [TestMethod]
        public void ApplyShare_OverOneHundredPercent_ExceedsTheSurplus()
        {
            Assert.AreEqual(108u, DrainSurplusDistribution.ApplyShare(100, 1.08));
            Assert.AreEqual(112u, DrainSurplusDistribution.ApplyShare(100, 1.12));
        }

        [TestMethod]
        public void ApplyShare_ZeroFractionOrZeroSurplus_GivesNothing()
        {
            Assert.AreEqual(0u, DrainSurplusDistribution.ApplyShare(500, 0.0));
            Assert.AreEqual(0u, DrainSurplusDistribution.ApplyShare(0, 1.0));
            Assert.AreEqual(0u, DrainSurplusDistribution.ApplyShare(500, -1.0));
        }

        [TestMethod]
        public void ApplyShare_Saturates_NeverWraps()
        {
            Assert.AreEqual(uint.MaxValue, DrainSurplusDistribution.ApplyShare(uint.MaxValue, 2.0),
                "A uint multiply that wrapped would turn a huge share into a tiny one.");
        }

        // ------------------------------------------------------------------
        // composition with Distribute - the reason over-100% is safe
        // ------------------------------------------------------------------

        [TestMethod]
        public void OverOneHundredPercentShare_ReachesMorePartyMembers_WithoutOverhealingAnyone()
        {
            var missing = new uint[] { 200, 200, 200 };

            // a 500-point surplus at rank 3 with a 112% share delivers 560 - more than the drain produced
            var delivered = DrainSurplusDistribution.ApplyShare(500, Share(3, 0.12));

            Assert.AreEqual(560u, delivered);

            var allocations = DrainSurplusDistribution.Distribute(delivered, missing);

            for (var i = 0; i < missing.Length; i++)
                Assert.IsTrue(allocations[i] <= missing[i],
                    $"fellow {i} was given {allocations[i]} but is only missing {missing[i]} - the over-100% share must never overheal.");

            Assert.AreEqual(560u, allocations.Aggregate(0u, (a, b) => a + b),
                "560 fits inside 600 points of total missing, so all of it should land.");
        }

        [TestMethod]
        public void OverOneHundredPercentShare_BeyondTotalMissing_IsStillLost()
        {
            var missing = new uint[] { 50, 50 };

            var delivered = DrainSurplusDistribution.ApplyShare(500, Share(3, 0.50));

            Assert.AreEqual(750u, delivered);

            var allocations = DrainSurplusDistribution.Distribute(delivered, missing);

            Assert.AreEqual(100u, allocations.Aggregate(0u, (a, b) => a + b),
                "Everyone is topped up and the rest evaporates - it is never returned to the caster or the drain.");
        }

        [TestMethod]
        public void LowerRank_DeliversStrictlyLess_ToTheSameParty()
        {
            var missing = new uint[] { 300, 300 };

            var atR1 = DrainSurplusDistribution.Distribute(DrainSurplusDistribution.ApplyShare(400, Share(1)), missing).Aggregate(0u, (a, b) => a + b);
            var atR2 = DrainSurplusDistribution.Distribute(DrainSurplusDistribution.ApplyShare(400, Share(2)), missing).Aggregate(0u, (a, b) => a + b);
            var atR3 = DrainSurplusDistribution.Distribute(DrainSurplusDistribution.ApplyShare(400, Share(3)), missing).Aggregate(0u, (a, b) => a + b);

            Assert.AreEqual(160u, atR1);
            Assert.AreEqual(280u, atR2);
            Assert.AreEqual(400u, atR3);

            Assert.IsTrue(atR1 < atR2 && atR2 < atR3, "Rank must be a strict improvement in delivery.");
        }
    }
}
