using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins <see cref="Player.ShouldShowFacetUnlockNotice"/>, the pure decision behind the one-time
    /// facet slot-2 unlock notice (<see cref="Player.SendFacetUnlockNoticeIfDue"/>). ACE.Server.Tests
    /// has no database and cannot construct a live Player, so everything that reads a property or sends a
    /// message is instance-only and untestable here; this file covers only the gate logic, which was
    /// deliberately extracted into a stateless static for exactly that reason.
    /// </summary>
    [TestClass]
    public class FacetUnlockNoticeTests
    {
        [TestMethod]
        public void ShouldShowFacetUnlockNotice_AllConditionsMet_ReturnsTrue()
        {
            Assert.IsTrue(Player.ShouldShowFacetUnlockNotice(
                facetsEnabled: true, noticeAlreadyShown: false, isMuleBlocked: false, level: 300, requiredLevelForSlot2: 300));
        }

        [TestMethod]
        public void ShouldShowFacetUnlockNotice_LevelAboveThreshold_ReturnsTrue()
        {
            Assert.IsTrue(Player.ShouldShowFacetUnlockNotice(
                facetsEnabled: true, noticeAlreadyShown: false, isMuleBlocked: false, level: 301, requiredLevelForSlot2: 300));
        }

        [TestMethod]
        public void ShouldShowFacetUnlockNotice_LevelBelowThreshold_ReturnsFalse()
        {
            Assert.IsFalse(Player.ShouldShowFacetUnlockNotice(
                facetsEnabled: true, noticeAlreadyShown: false, isMuleBlocked: false, level: 299, requiredLevelForSlot2: 300));
        }

        [TestMethod]
        public void ShouldShowFacetUnlockNotice_FacetsDisabled_ReturnsFalse_EvenIfOtherwiseEligible()
        {
            Assert.IsFalse(Player.ShouldShowFacetUnlockNotice(
                facetsEnabled: false, noticeAlreadyShown: false, isMuleBlocked: false, level: 300, requiredLevelForSlot2: 300));
        }

        [TestMethod]
        public void ShouldShowFacetUnlockNotice_NoticeAlreadyShown_ReturnsFalse_EvenIfOtherwiseEligible()
        {
            Assert.IsFalse(Player.ShouldShowFacetUnlockNotice(
                facetsEnabled: true, noticeAlreadyShown: true, isMuleBlocked: false, level: 300, requiredLevelForSlot2: 300));
        }

        [TestMethod]
        public void ShouldShowFacetUnlockNotice_MuleBlocked_ReturnsFalse_EvenIfOtherwiseEligible()
        {
            Assert.IsFalse(Player.ShouldShowFacetUnlockNotice(
                facetsEnabled: true, noticeAlreadyShown: false, isMuleBlocked: true, level: 300, requiredLevelForSlot2: 300));
        }

        [TestMethod]
        public void ShouldShowFacetUnlockNotice_AllGatesFailing_ReturnsFalse()
        {
            Assert.IsFalse(Player.ShouldShowFacetUnlockNotice(
                facetsEnabled: false, noticeAlreadyShown: true, isMuleBlocked: true, level: 1, requiredLevelForSlot2: 300));
        }
    }
}
