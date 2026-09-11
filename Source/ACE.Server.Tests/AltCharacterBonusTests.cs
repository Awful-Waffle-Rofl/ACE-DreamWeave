using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Boundary math for the alt character bonus (<see cref="AltCharacterBonus"/>): a character's progression
    /// score must fold enlightenment above level so any enlightenment outranks a max-level un-enlightened
    /// character, the "below" comparison must require at least the configured gap (equality or a same-side
    /// deficit smaller than the gap = no bonus, so the alt stops boosting once it is within the gap), and the
    /// multiplier must only scale positive amounts and never overflow a long.
    /// </summary>
    [TestClass]
    public class AltCharacterBonusTests
    {
        private const int MaxLevel = 275;

        [TestMethod]
        public void GetProgression_IsLevelAlone()
        {
            Assert.AreEqual(1, AltCharacterBonus.GetProgression(1, 0, MaxLevel));
            Assert.AreEqual(200, AltCharacterBonus.GetProgression(200, 0, MaxLevel));
            Assert.AreEqual(275, AltCharacterBonus.GetProgression(275, 0, MaxLevel));
            Assert.AreEqual(531, AltCharacterBonus.GetProgression(531, 0, MaxLevel));
        }

        /// <summary>
        /// Enlightenment is RETIRED and no longer contributes to the score (XP-LANE-SPEC sec 4). These
        /// assertions are the inverse of the ones they replaced, which pinned enlightenment as worth a whole
        /// max-level climb. The reason the term had to go: the retirement credit converts an enlightened
        /// character's count into LEVEL, so counting it again would score the same progress twice and hand
        /// every alt on the account a permanent catch-up bonus.
        /// </summary>
        [TestMethod]
        public void GetProgression_IgnoresEnlightenment()
        {
            Assert.AreEqual(1, AltCharacterBonus.GetProgression(1, 1, MaxLevel));
            Assert.AreEqual(0, AltCharacterBonus.GetProgression(0, 3, MaxLevel));

            // an enlightened character no longer outranks a max-level one on the count alone
            Assert.IsTrue(AltCharacterBonus.GetProgression(1, 20, MaxLevel) < AltCharacterBonus.GetProgression(MaxLevel, 0, MaxLevel));

            // and the credited level is what carries it instead: ENL 20 credits to level 531
            Assert.IsTrue(AltCharacterBonus.GetProgression(531, 20, MaxLevel) > AltCharacterBonus.GetProgression(MaxLevel, 0, MaxLevel));
        }

        [TestMethod]
        public void GetProgression_ClampsNegativeInputs()
        {
            Assert.AreEqual(0, AltCharacterBonus.GetProgression(-5, -5, MaxLevel));
            Assert.AreEqual(0, AltCharacterBonus.GetProgression(0, 0, -5));
        }

        [TestMethod]
        public void IsBelow_RequiresConfiguredGap()
        {
            const long gap = 5;

            // exactly 5 behind => bonus active (the gap boundary itself qualifies)
            Assert.IsTrue(AltCharacterBonus.IsBelow(195, 200, gap));
            // 4 behind => not enough of a gap yet => no bonus
            Assert.IsFalse(AltCharacterBonus.IsBelow(196, 200, gap));
            // exactly caught up (0 behind) => no bonus
            Assert.IsFalse(AltCharacterBonus.IsBelow(200, 200, gap));
            // ahead (negative deficit), e.g. the account's own high-water mark => no bonus
            Assert.IsFalse(AltCharacterBonus.IsBelow(276, 200, gap));
            // far below => still active
            Assert.IsTrue(AltCharacterBonus.IsBelow(1, 200, gap));
        }

        [TestMethod]
        public void IsBelow_GapZeroRestoresOldStrictBehaviour()
        {
            // gap 0: any nonzero deficit qualifies, but equality still does not (never <=)
            Assert.IsTrue(AltCharacterBonus.IsBelow(1, 200, 0));
            Assert.IsTrue(AltCharacterBonus.IsBelow(199, 200, 0));
            Assert.IsFalse(AltCharacterBonus.IsBelow(200, 200, 0));
            Assert.IsFalse(AltCharacterBonus.IsBelow(276, 200, 0));
        }

        [TestMethod]
        public void IsBelow_NegativeGapClampsToZero()
        {
            // a bad config row (negative gap) must not invert the logic - behaves as gap 0
            Assert.IsTrue(AltCharacterBonus.IsBelow(199, 200, -5));
            Assert.IsFalse(AltCharacterBonus.IsBelow(200, 200, -5));
        }

        [TestMethod]
        public void Apply_DoublesAtFullMultiplier()
        {
            // +100% => x2 (the headline "double experience")
            Assert.AreEqual(2000, AltCharacterBonus.Apply(1000, 1.0));
            // +50% => x1.5
            Assert.AreEqual(1500, AltCharacterBonus.Apply(1000, 0.5));
        }

        [TestMethod]
        public void Apply_ComposesMultiplicativelyWithOfflineBonus()
        {
            // a fresh alt with banked offline time: offline bonus doubles, then this doubles again => 4x
            var afterOffline = OfflineBonus.Apply(1000, remaining: 60, multiplier: 1.0);
            var afterAlt = AltCharacterBonus.Apply(afterOffline, 1.0);

            Assert.AreEqual(4000, afterAlt);
        }

        [TestMethod]
        public void Apply_NoOpForNonPositiveAmountOrMultiplier()
        {
            Assert.AreEqual(0, AltCharacterBonus.Apply(0, 1.0));
            Assert.AreEqual(-10, AltCharacterBonus.Apply(-10, 1.0));
            Assert.AreEqual(1000, AltCharacterBonus.Apply(1000, 0));
            Assert.AreEqual(1000, AltCharacterBonus.Apply(1000, -1.0));
        }

        [TestMethod]
        public void Apply_OverflowGuardReturnsUnboostedAmount()
        {
            Assert.AreEqual(long.MaxValue / 2, AltCharacterBonus.Apply(long.MaxValue / 2, 1e300));
        }
    }
}
