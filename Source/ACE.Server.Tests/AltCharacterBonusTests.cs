using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Boundary math for the alt character bonus (<see cref="AltCharacterBonus"/>): a character's progression
    /// score must fold enlightenment above level so any enlightenment outranks a max-level un-enlightened
    /// character, the "below" comparison must be strict (equality = no bonus, so the alt stops boosting exactly
    /// when it catches up), and the multiplier must only scale positive amounts and never overflow a long.
    /// </summary>
    [TestClass]
    public class AltCharacterBonusTests
    {
        private const int MaxLevel = 275;

        [TestMethod]
        public void GetProgression_LevelOnlyWhenUnenlightened()
        {
            Assert.AreEqual(1, AltCharacterBonus.GetProgression(1, 0, MaxLevel));
            Assert.AreEqual(200, AltCharacterBonus.GetProgression(200, 0, MaxLevel));
            Assert.AreEqual(275, AltCharacterBonus.GetProgression(275, 0, MaxLevel));
        }

        [TestMethod]
        public void GetProgression_EnlightenmentAddsAFullLevelClimb()
        {
            // one enlightenment is worth a whole max-level climb
            Assert.AreEqual(276, AltCharacterBonus.GetProgression(1, 1, MaxLevel));
            Assert.AreEqual(825, AltCharacterBonus.GetProgression(0, 3, MaxLevel));
        }

        [TestMethod]
        public void GetProgression_AnyEnlightenmentOutranksMaxLevel()
        {
            var enlightenedFresh = AltCharacterBonus.GetProgression(1, 1, MaxLevel);
            var maxLevelUnenlightened = AltCharacterBonus.GetProgression(MaxLevel, 0, MaxLevel);

            Assert.IsTrue(enlightenedFresh > maxLevelUnenlightened);
        }

        [TestMethod]
        public void GetProgression_FreshEnlightenmentDipsBelowPreEnlightenmentScore()
        {
            // The alt-character progression score folds every character's enlightenment at the FIXED global
            // cap (275), so levels earned above 275 (the enlightenment overage) count toward the score. When a
            // character enlightens, its level resets to 1 while enlightenment climbs by one, which briefly
            // LOWERS its progression below the pre-enlightenment value - the desirable consequence being that a
            // freshly enlightened character re-qualifies for its own catch-up bonus for the ~5 levels it takes
            // to re-earn that overage.
            var beforeEnl = AltCharacterBonus.GetProgression(280, 1, MaxLevel);  // enl 1 at its personal cap (280)
            var afterEnl = AltCharacterBonus.GetProgression(1, 2, MaxLevel);     // just enlightened to enl 2, back to level 1

            Assert.IsTrue(afterEnl < beforeEnl, "a fresh enlightenment should dip below the pre-enlightenment progression");

            // it climbs back to the pre-enlightenment score after re-leveling the 5 levels earned above the base cap
            Assert.AreEqual(beforeEnl, AltCharacterBonus.GetProgression(5, 2, MaxLevel));
        }

        [TestMethod]
        public void GetProgression_ClampsNegativeInputs()
        {
            Assert.AreEqual(0, AltCharacterBonus.GetProgression(-5, -5, MaxLevel));
            Assert.AreEqual(0, AltCharacterBonus.GetProgression(0, 0, -5));
        }

        [TestMethod]
        public void IsBelow_StrictComparison()
        {
            // a fresh alt below a level-200 main => bonus active
            Assert.IsTrue(AltCharacterBonus.IsBelow(1, 200));
            // exactly caught up => no bonus (this is the "until it reaches an equal level" cutoff)
            Assert.IsFalse(AltCharacterBonus.IsBelow(200, 200));
            // past it (e.g. the account's own high-water mark) => no bonus
            Assert.IsFalse(AltCharacterBonus.IsBelow(276, 200));
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
