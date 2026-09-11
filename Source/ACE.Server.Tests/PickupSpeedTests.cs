using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pick-up animation speed pure composition rule (ACE.Server/Entity/PickupSpeed.cs). Exercises the
    /// explicit-parameter Compute overload directly rather than through GetPickupAnimationSpeed, because
    /// PropertyManager needs a live shard config table this project cannot provide - the same reason
    /// SalvageForgeTests avoids the PropertyManager-backed overload.
    /// </summary>
    [TestClass]
    public class PickupSpeedTests
    {
        // Shipped defaults - pickup_animation_speed=1.0, pickup_speed_quest_bonus=0.5, pickup_animation_speed_max=3.0.
        private const double DefaultBase = 1.0;
        private const double DefaultPerBoon = 0.5;
        private const double DefaultMax = 3.0;

        /// <summary>
        /// Default table: 0 boons is unchanged, each boon adds +50%, capped at the max (reached at 4 boons,
        /// and still capped - not reset to 1.0 - at 5).
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 1.0)]
        [DataRow(1, 1.5)]
        [DataRow(2, 2.0)]
        [DataRow(3, 2.5)]
        [DataRow(4, 3.0)]
        [DataRow(5, 3.0)]
        public void Compute_DefaultTable_MatchesExpected(int boonCount, double expected)
        {
            Assert.AreEqual(expected, PickupSpeed.Compute(DefaultBase, boonCount, DefaultPerBoon, DefaultMax), 0.0001);
        }

        /// <summary>
        /// Garbage base speeds - zero, negative, non-finite, or out of the accepted [0.1, 10.0] range - are
        /// treated as 1.0 before composition, matching the original reject-to-1.0 sanitize semantics.
        /// </summary>
        [DataTestMethod]
        [DataRow(0.0)]
        [DataRow(-1.0)]
        [DataRow(double.NaN)]
        [DataRow(double.PositiveInfinity)]
        [DataRow(double.NegativeInfinity)]
        [DataRow(15.0)]
        [DataRow(0.05)]
        public void Compute_GarbageBase_TreatedAsOne(double garbageBase)
        {
            Assert.AreEqual(1.5, PickupSpeed.Compute(garbageBase, 1, DefaultPerBoon, DefaultMax), 0.0001);
        }

        /// <summary>
        /// A negative boon count behaves the same as zero boons.
        /// </summary>
        [TestMethod]
        public void Compute_NegativeBoonCount_TreatedAsZero()
        {
            Assert.AreEqual(1.0, PickupSpeed.Compute(DefaultBase, -3, DefaultPerBoon, DefaultMax), 0.0001);
        }

        /// <summary>
        /// A negative or non-finite perBoon clamps to 0, so boons contribute nothing.
        /// </summary>
        [DataTestMethod]
        [DataRow(-1.0)]
        [DataRow(double.NaN)]
        [DataRow(double.NegativeInfinity)]
        public void Compute_NegativeOrNonFinitePerBoon_ClampsToZero(double perBoon)
        {
            Assert.AreEqual(1.0, PickupSpeed.Compute(DefaultBase, 4, perBoon, DefaultMax), 0.0001);
        }

        /// <summary>
        /// perBoon above 10 clamps down to 10 rather than being rejected.
        /// </summary>
        [TestMethod]
        public void Compute_PerBoonAboveTen_ClampsToTen()
        {
            // base 1.0, 1 boon, perBoon clamped to 10 -> 1 * (1 + 1*10) = 11, clamped to max 10 (global max ceiling)
            Assert.AreEqual(10.0, PickupSpeed.Compute(1.0, 1, 999.0, 10.0), 0.0001);
        }

        /// <summary>
        /// max below the 1.0 floor clamps up to 1.0; non-finite max also becomes 1.0.
        /// </summary>
        [DataTestMethod]
        [DataRow(0.5)]
        [DataRow(0.0)]
        [DataRow(-5.0)]
        public void Compute_MaxBelowFloor_ClampsToOne(double max)
        {
            Assert.AreEqual(1.0, PickupSpeed.Compute(DefaultBase, 0, DefaultPerBoon, max), 0.0001);
        }

        [TestMethod]
        public void Compute_NonFiniteMax_TreatedAsOne()
        {
            Assert.AreEqual(1.0, PickupSpeed.Compute(DefaultBase, 0, DefaultPerBoon, double.NaN), 0.0001);
        }

        /// <summary>
        /// A huge max is clamped down to the 10.0 ceiling.
        /// </summary>
        [TestMethod]
        public void Compute_HugeMax_ClampsToTen()
        {
            Assert.AreEqual(10.0, PickupSpeed.Compute(1.0, 20, 1.0, 1000.0), 0.0001);
        }

        /// <summary>
        /// A composed value that exceeds max is CLAMPED to max, never reset to 1.0 - this is the load-bearing
        /// fix over the old reject-based sanitize.
        /// </summary>
        [TestMethod]
        public void Compute_ComposedExceedsMax_ClampsToMaxNotOne()
        {
            var result = PickupSpeed.Compute(1.0, 10, 0.5, 3.0);
            Assert.AreEqual(3.0, result, 0.0001);
            Assert.AreNotEqual(1.0, result, 0.0001);
        }

        // ================================================================
        // ===== Custom Dreamweave pick-up augmentation (six-arg) =========
        // ================================================================

        // Shipped default for custom_aug_pickup_speed_bonus.
        private const double DefaultPerAug = 0.10;

        /// <summary>
        /// The four-argument overload must keep behaving EXACTLY as it did before the augmentation term
        /// existed - it now delegates with augCount 0, and every pre-existing test above binds to it
        /// unchanged. This pins the delegation itself: the two overloads agree whenever augCount is 0.
        /// </summary>
        [DataTestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(4)]
        [DataRow(9)]
        public void Compute_FourArgOverload_MatchesSixArgWithZeroAugs(int boonCount)
        {
            Assert.AreEqual(
                PickupSpeed.Compute(DefaultBase, boonCount, DefaultPerBoon, 0, DefaultPerAug, DefaultMax),
                PickupSpeed.Compute(DefaultBase, boonCount, DefaultPerBoon, DefaultMax),
                0.0001);
        }

        /// <summary>
        /// Augmentations and boons compose ADDITIVELY inside one term: base * (1 + boons*perBoon +
        /// augs*perAug). Deliberately exercised with a raised max so the cap is not what is being measured -
        /// see Compute_BoonsAndAugs_ShareOneClamp for the capped case.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 0, 1.0)]
        [DataRow(0, 1, 1.1)]
        [DataRow(0, 5, 1.5)]
        [DataRow(1, 0, 1.5)]
        [DataRow(1, 1, 1.6)]      // 1 + 0.5 + 0.1
        [DataRow(2, 3, 2.3)]      // 1 + 1.0 + 0.3
        [DataRow(4, 10, 4.0)]     // 1 + 2.0 + 1.0
        public void Compute_BoonsAndAugs_AddWithinOneTerm(int boonCount, int augCount, double expected)
        {
            // max 10.0 - the global ceiling - so nothing here is clamped.
            Assert.AreEqual(expected, PickupSpeed.Compute(DefaultBase, boonCount, DefaultPerBoon, augCount, DefaultPerAug, 10.0), 0.0001);
        }

        /// <summary>
        /// One clamp covers both sources: a player holding boons AND augmentations cannot exceed max by
        /// stacking them, and the composed value is clamped to max rather than reset to 1.0.
        /// </summary>
        [TestMethod]
        public void Compute_BoonsAndAugs_ShareOneClamp()
        {
            // uncapped = 1 + 2*0.5 + 20*0.1 = 4.0, over the default max of 3.0
            var result = PickupSpeed.Compute(DefaultBase, 2, DefaultPerBoon, 20, DefaultPerAug, DefaultMax);

            Assert.AreEqual(DefaultMax, result, 0.0001);
            Assert.AreNotEqual(1.0, result, 0.0001);
        }

        /// <summary>
        /// A negative augmentation count behaves as zero, and a negative or non-finite perAug contributes
        /// nothing - a corrupt property or a mis-set tunable must never SLOW a player down.
        /// </summary>
        [DataTestMethod]
        [DataRow(-3, 0.10)]
        [DataRow(4, -1.0)]
        [DataRow(4, double.NaN)]
        [DataRow(4, double.NegativeInfinity)]
        public void Compute_GarbageAugInputs_ContributeNothing(int augCount, double perAug)
        {
            Assert.AreEqual(1.0, PickupSpeed.Compute(DefaultBase, 0, DefaultPerBoon, augCount, perAug, DefaultMax), 0.0001);
        }

        /// <summary>
        /// IsCapped reports the COMBINED figure. This is the accepted-collision mitigation: with the shipped
        /// defaults four Quickhand boons alone already reach the 3.0 ceiling, so a player buying pick-up
        /// augmentations on top of that gets nothing until an operator raises pickup_animation_speed_max -
        /// and IsCapped is what tells them so.
        /// </summary>
        [TestMethod]
        public void IsCapped_TrueWhenCombinedFigureExceedsMax()
        {
            // boons alone: exactly 3.0, AT the cap but not over it.
            Assert.IsFalse(PickupSpeed.IsCapped(DefaultBase, 4, DefaultPerBoon, 0, DefaultPerAug, DefaultMax));

            // one augmentation on top pushes the uncapped figure to 3.1, which IS over.
            Assert.IsTrue(PickupSpeed.IsCapped(DefaultBase, 4, DefaultPerBoon, 1, DefaultPerAug, DefaultMax));

            // and Compute still returns the ceiling, confirming the aug is genuinely inert here.
            Assert.AreEqual(DefaultMax, PickupSpeed.Compute(DefaultBase, 4, DefaultPerBoon, 1, DefaultPerAug, DefaultMax), 0.0001);
        }

        /// <summary>
        /// Augmentations alone can reach the cap with no boons at all.
        /// </summary>
        [TestMethod]
        public void IsCapped_TrueForAugmentationsAlone()
        {
            // 1 + 25*0.1 = 3.5, over the default max of 3.0
            Assert.IsTrue(PickupSpeed.IsCapped(DefaultBase, 0, DefaultPerBoon, 25, DefaultPerAug, DefaultMax));
            Assert.AreEqual(DefaultMax, PickupSpeed.Compute(DefaultBase, 0, DefaultPerBoon, 25, DefaultPerAug, DefaultMax), 0.0001);
        }

        /// <summary>
        /// The four-argument IsCapped overload is likewise unchanged for a player with no augmentations.
        /// </summary>
        [TestMethod]
        public void IsCapped_FourArgOverload_MatchesSixArgWithZeroAugs()
        {
            Assert.AreEqual(
                PickupSpeed.IsCapped(DefaultBase, 10, DefaultPerBoon, 0, DefaultPerAug, DefaultMax),
                PickupSpeed.IsCapped(DefaultBase, 10, DefaultPerBoon, DefaultMax));

            Assert.AreEqual(
                PickupSpeed.IsCapped(DefaultBase, 1, DefaultPerBoon, 0, DefaultPerAug, DefaultMax),
                PickupSpeed.IsCapped(DefaultBase, 1, DefaultPerBoon, DefaultMax));
        }

        /// <summary>
        /// PercentBonus rounds the SUM once rather than rounding each source, so the printed percent is the
        /// same quantity Compute multiplies by.
        /// </summary>
        [DataTestMethod]
        [DataRow(0, 0, 0)]
        [DataRow(1, 0, 50)]
        [DataRow(0, 1, 10)]
        [DataRow(2, 3, 130)]
        public void PercentBonus_CombinesBothSources(int boonCount, int augCount, int expected)
        {
            Assert.AreEqual(expected, PickupSpeed.PercentBonus(boonCount, DefaultPerBoon, augCount, DefaultPerAug));
        }
    }

    /// <summary>
    /// /pickupspeed's pure status-line formatter (PickupSpeed.FormatPickupSpeedStatus). Covers the four
    /// line-inclusion rules (always-line, zero-boons hint, capped note, non-default-base note) and the exact
    /// always-line text for the default table.
    /// </summary>
    [TestClass]
    public class FormatPickupSpeedStatusTests
    {
        private const double DefaultBase = 1.0;
        private const double DefaultPerBoon = 0.5;
        private const double DefaultMax = 3.0;

        [DataTestMethod]
        [DataRow(0, "Pick-up speed bonus: 0 percent (1.00x pick-up speed).")]
        [DataRow(1, "Pick-up speed bonus: 50 percent (1.50x pick-up speed).")]
        [DataRow(4, "Pick-up speed bonus: 200 percent (3.00x pick-up speed).")]
        public void AlwaysLine_MatchesExactText_DefaultTable(int boonCount, string expectedFirstLine)
        {
            var lines = PickupSpeed.FormatPickupSpeedStatus(boonCount, DefaultBase, DefaultPerBoon, DefaultMax);
            Assert.AreEqual(expectedFirstLine, lines[0]);
        }

        [TestMethod]
        public void ZeroBoons_IncludesHintLine()
        {
            var lines = PickupSpeed.FormatPickupSpeedStatus(0, DefaultBase, DefaultPerBoon, DefaultMax);
            CollectionAssert.Contains(lines, "You have not earned any pick-up speed bonuses yet. They are granted by quest rewards.");
        }

        [TestMethod]
        public void NonZeroBoons_OmitsHintLine()
        {
            var lines = PickupSpeed.FormatPickupSpeedStatus(1, DefaultBase, DefaultPerBoon, DefaultMax);
            CollectionAssert.DoesNotContain(lines, "You have not earned any pick-up speed bonuses yet. They are granted by quest rewards.");
        }

        [TestMethod]
        public void UnderCap_OmitsCappedNote()
        {
            // 1 boon: uncapped composed = 1.5, under max 3.0
            var lines = PickupSpeed.FormatPickupSpeedStatus(1, DefaultBase, DefaultPerBoon, DefaultMax);
            CollectionAssert.DoesNotContain(lines, "This is the maximum bonus this server allows.");
        }

        [TestMethod]
        public void AtOrOverCap_IncludesCappedNote()
        {
            // 10 boons: uncapped composed = 1.0 * (1 + 10*0.5) = 6.0, over max 3.0
            var lines = PickupSpeed.FormatPickupSpeedStatus(10, DefaultBase, DefaultPerBoon, DefaultMax);
            CollectionAssert.Contains(lines, "This is the maximum bonus this server allows.");
        }

        [TestMethod]
        public void ExactlyAtCap_OmitsCappedNote()
        {
            // 4 boons: uncapped composed = 1.0 * (1 + 4*0.5) = 3.0, exactly at max - not OVER it
            var lines = PickupSpeed.FormatPickupSpeedStatus(4, DefaultBase, DefaultPerBoon, DefaultMax);
            CollectionAssert.DoesNotContain(lines, "This is the maximum bonus this server allows.");
        }

        [TestMethod]
        public void DefaultBaseSpeed_OmitsServerWideNote()
        {
            var lines = PickupSpeed.FormatPickupSpeedStatus(1, DefaultBase, DefaultPerBoon, DefaultMax);
            foreach (var line in lines)
                Assert.IsFalse(line.StartsWith("Server-wide pick-up speed"));
        }

        [TestMethod]
        public void NonDefaultBaseSpeed_IncludesServerWideNote()
        {
            var lines = PickupSpeed.FormatPickupSpeedStatus(0, 2.0, DefaultPerBoon, DefaultMax);
            CollectionAssert.Contains(lines, "Server-wide pick-up speed is currently set to 2x, which is included in the figure above.");
        }

        [TestMethod]
        public void NonDefaultBaseSpeed_AlwaysLine_ComposesWithBase()
        {
            // base 2.0, 0 boons -> composed = 2.0, clamped to max 3.0 -> 2.00
            var lines = PickupSpeed.FormatPickupSpeedStatus(0, 2.0, DefaultPerBoon, DefaultMax);
            Assert.AreEqual("Pick-up speed bonus: 0 percent (2.00x pick-up speed).", lines[0]);
        }

        /// <summary>
        /// The status line reports the COMBINED figure. PickupSpeed's class contract is that the
        /// /pickupspeed wording always agrees with Compute, so a line quoting only the boon half would be a
        /// lie the moment a Custom Dreamweave pick-up augmentation is bought.
        /// </summary>
        [TestMethod]
        public void AlwaysLine_IncludesTheAugmentationHalf()
        {
            // 1 boon + 2 augs at the shipped defaults: 50 + 20 = 70 percent, 1.70x
            var lines = PickupSpeed.FormatPickupSpeedStatus(1, DefaultBase, DefaultPerBoon, 2, 0.10, DefaultMax);

            Assert.AreEqual("Pick-up speed bonus: 70 percent (1.70x pick-up speed).", lines[0]);
        }

        /// <summary>
        /// The "no bonuses yet" hint is suppressed by an augmentation as well as by a boon - a player who
        /// bought one from Bo and has no quest boon must not be told they have earned nothing.
        /// </summary>
        [TestMethod]
        public void AugmentationsWithoutBoons_OmitsHintLine()
        {
            var lines = PickupSpeed.FormatPickupSpeedStatus(0, DefaultBase, DefaultPerBoon, 1, 0.10, DefaultMax);

            CollectionAssert.DoesNotContain(lines, "You have not earned any pick-up speed bonuses yet. They are granted by quest rewards.");
            Assert.AreEqual("Pick-up speed bonus: 10 percent (1.10x pick-up speed).", lines[0]);
        }

        /// <summary>
        /// The capped note fires on the COMBINED figure. This is the honest report of the accepted default
        /// collision: four boons alone sit exactly AT the 3.0 ceiling (no note), and one augmentation on top
        /// pushes past it, at which point the player is told the bonus is being held down rather than left
        /// to wonder why the number stopped moving.
        /// </summary>
        [TestMethod]
        public void CombinedFigureOverCap_IncludesCappedNote()
        {
            var atCap = PickupSpeed.FormatPickupSpeedStatus(4, DefaultBase, DefaultPerBoon, 0, 0.10, DefaultMax);
            CollectionAssert.DoesNotContain(atCap, "This is the maximum bonus this server allows.");

            var overCap = PickupSpeed.FormatPickupSpeedStatus(4, DefaultBase, DefaultPerBoon, 1, 0.10, DefaultMax);
            CollectionAssert.Contains(overCap, "This is the maximum bonus this server allows.");
            Assert.AreEqual("Pick-up speed bonus: 210 percent (3.00x pick-up speed).", overCap[0]);
        }

        [TestMethod]
        public void CappedCase_AllFourLinesAsExpected()
        {
            // base 2.0, 4 boons: uncapped = 2.0 * (1 + 4*0.5) = 6.0, over max 3.0 -> capped note fires;
            // base != 1.0 -> server-wide note fires; boonCount != 0 -> no hint line.
            var lines = PickupSpeed.FormatPickupSpeedStatus(4, 2.0, DefaultPerBoon, DefaultMax);

            Assert.AreEqual("Pick-up speed bonus: 200 percent (3.00x pick-up speed).", lines[0]);
            CollectionAssert.DoesNotContain(lines, "You have not earned any pick-up speed bonuses yet. They are granted by quest rewards.");
            CollectionAssert.Contains(lines, "This is the maximum bonus this server allows.");
            CollectionAssert.Contains(lines, "Server-wide pick-up speed is currently set to 2x, which is included in the figure above.");
            Assert.AreEqual(3, lines.Length);
        }
    }
}
