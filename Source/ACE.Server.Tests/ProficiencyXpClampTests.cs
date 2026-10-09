using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Proficiency XP is clamped to the room a character has left below the XP chart's hard ceiling. The
    /// clamp used to be taken against the RETAIL chart's level-275 total (Player.GetMaxLevel) rather than the
    /// character's own ceiling, so once levels were uncapped every character carrying more total XP than
    /// level 275 requires produced a NEGATIVE remainder. That went straight to GrantXP, which adds the amount
    /// to both TotalExperience and AvailableExperience, so each successful skill check subtracted the
    /// character's progress and snapped their total back to the level-275 value (observed in prod on a level
    /// 276 character losing ~70M total AND unassigned XP at a time).
    /// </summary>
    [TestClass]
    public class ProficiencyXpClampTests
    {
        [TestMethod]
        public void NegativeRemaining_GrantsNothing_NeverSubtracts()
        {
            // the prod case: total experience already past the level the clamp was measured against
            Assert.AreEqual(0L, Proficiency.ClampToRemaining(220, -8_273_267));
        }

        [TestMethod]
        public void ZeroRemaining_GrantsNothing()
        {
            Assert.AreEqual(0L, Proficiency.ClampToRemaining(220, 0));
        }

        [TestMethod]
        public void RemainingSmallerThanGrant_ClampsToRemaining()
        {
            Assert.AreEqual(50L, Proficiency.ClampToRemaining(220, 50));
        }

        [TestMethod]
        public void RemainingLargerThanGrant_PassesTheGrantThrough()
        {
            Assert.AreEqual(220L, Proficiency.ClampToRemaining(220, 6_928_485_678));
        }

        [TestMethod]
        public void NonPositiveGrant_GrantsNothing()
        {
            Assert.AreEqual(0L, Proficiency.ClampToRemaining(0, 1000));
            Assert.AreEqual(0L, Proficiency.ClampToRemaining(-5, 1000));
        }
    }
}
