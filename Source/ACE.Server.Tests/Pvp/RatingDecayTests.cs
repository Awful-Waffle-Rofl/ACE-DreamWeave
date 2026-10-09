using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// Read-time rating decay (Docs/Pvp/DESIGN.md "Rating" > "Decay"): grace period, whole weeks only,
    /// the floor, and below-floor ratings not decaying.
    /// </summary>
    [TestClass]
    public class RatingDecayTests
    {
        private static readonly DateTime LastMatch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void WithinGracePeriod_NoDecay()
        {
            var now = LastMatch.AddDays(14); // exactly at the grace boundary, not past it

            var result = RatingDecay.Apply(1800, LastMatch, now, graceDays: 14, pointsPerWeek: 25, floor: 1500);

            Assert.AreEqual(1800, result);
        }

        [TestMethod]
        public void OneFullWeekPastGrace_LosesOneWeekOfPoints()
        {
            var now = LastMatch.AddDays(14 + 7);

            var result = RatingDecay.Apply(1800, LastMatch, now, graceDays: 14, pointsPerWeek: 25, floor: 1500);

            Assert.AreEqual(1775, result);
        }

        [TestMethod]
        public void PartialWeekPastGrace_DoesNotCountAsAFullWeek()
        {
            var now = LastMatch.AddDays(14 + 6); // 6 of 7 days into the second week

            var result = RatingDecay.Apply(1800, LastMatch, now, graceDays: 14, pointsPerWeek: 25, floor: 1500);

            Assert.AreEqual(1800, result);
        }

        [TestMethod]
        public void DecayNeverDropsBelowTheFloor()
        {
            var now = LastMatch.AddDays(14 + 7 * 20); // 20 full weeks past grace - would be 1550 - 500 without a floor

            var result = RatingDecay.Apply(1550, LastMatch, now, graceDays: 14, pointsPerWeek: 25, floor: 1500);

            Assert.AreEqual(1500, result);
        }

        [TestMethod]
        public void RatingAlreadyBelowFloor_DoesNotDecay()
        {
            var now = LastMatch.AddDays(14 + 7 * 5);

            var result = RatingDecay.Apply(1400, LastMatch, now, graceDays: 14, pointsPerWeek: 25, floor: 1500);

            Assert.AreEqual(1400, result);
        }

        [TestMethod]
        public void RatingExactlyAtFloor_DoesNotDecay()
        {
            var now = LastMatch.AddDays(14 + 7 * 5);

            var result = RatingDecay.Apply(1500, LastMatch, now, graceDays: 14, pointsPerWeek: 25, floor: 1500);

            Assert.AreEqual(1500, result);
        }
    }
}
