using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure truth-table coverage for TinkerLock.IsRefused(bool, bool, bool) - the injectable core
    /// used by the production wrapper (which needs a real WorldObject/PropertyManager/database and
    /// so is not exercised here; see BluespireTinkerLockContentTests for the content-side pins and
    /// TinkerLockSourceOrderTests for the three call sites).
    /// </summary>
    [TestClass]
    public class TinkerLockTests
    {
        [TestMethod]
        public void Unlocked_NeverRefused()
        {
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: false, hasCookbookRow: false));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: false, hasCookbookRow: true));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: true, hasCookbookRow: false));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: true, hasCookbookRow: true));
        }

        [TestMethod]
        public void Locked_ManaStoneSource_NeverRefused()
        {
            // Recharging is not tinkering (owner ruling), regardless of any cook_book row.
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: true, hasCookbookRow: false));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: true, hasCookbookRow: true));
        }

        [TestMethod]
        public void Locked_NonManaStoneSource_RefusedWithoutACookbookRow()
        {
            Assert.IsTrue(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: false, hasCookbookRow: false));
        }

        [TestMethod]
        public void Locked_NonManaStoneSource_AllowedWithACookbookRow()
        {
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: false, hasCookbookRow: true));
        }
    }
}
