using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AccountActivityIndexTests
    {
        private const double Now = 1_800_000_000.0;
        private const double Day = 86400.0;

        [TestMethod]
        public void Build_KeepsMaxPerAccount()
        {
            var index = AccountActivityIndex.Build(new (uint, double)[]
            {
                (1, Now - 10 * Day), (1, Now - 2 * Day), (1, Now - 30 * Day), (2, Now - 5 * Day),
            });

            Assert.AreEqual(Now - 2 * Day, index.LastSeen(1));
            Assert.AreEqual(Now - 5 * Day, index.LastSeen(2));
            Assert.IsNull(index.LastSeen(3));
        }

        [TestMethod]
        public void CountActive_UsesWindow()
        {
            var index = AccountActivityIndex.Build(new (uint, double)[]
            {
                (1, Now - 1 * Day), (2, Now - 7 * Day), (3, Now - 8 * Day),
            });

            Assert.AreEqual(2, index.CountActive(Now, 7));
            Assert.AreEqual(3, index.CountActive(Now, 8));
            Assert.IsTrue(index.IsActive(2, Now, 7));
            Assert.IsFalse(index.IsActive(3, Now, 7));
            Assert.IsFalse(index.IsActive(99, Now, 7));
        }

        [TestMethod]
        public void Touch_MakesAccountActiveAtOnce()
        {
            var index = AccountActivityIndex.Build(new (uint, double)[] { (1, Now - 40 * Day) });
            Assert.IsFalse(index.IsActive(1, Now, 7));

            index.Touch(1, Now);
            Assert.IsTrue(index.IsActive(1, Now, 7));
            Assert.AreEqual(Now, index.LastSeen(1));

            index.Touch(1, Now - 100 * Day); // never moves backward
            Assert.AreEqual(Now, index.LastSeen(1));
        }

        [TestMethod]
        public void SeenAt_OnlineIsNow_OfflineIsMaxOfLoginAndLogoff()
        {
            Assert.AreEqual(Now, AccountActivityIndex.SeenAt(true, Now, 5.0, 6.0));
            Assert.AreEqual(900.0, AccountActivityIndex.SeenAt(false, Now, 900.0, 800.0));
            Assert.AreEqual(900.0, AccountActivityIndex.SeenAt(false, Now, 800.0, 900.0));
            Assert.AreEqual(800.0, AccountActivityIndex.SeenAt(false, Now, null, 800.0));
            Assert.IsNull(AccountActivityIndex.SeenAt(false, Now, null, null));
        }

        [TestMethod]
        public void SeenAt_LoginOnAnAlt_MakesTheAccountActive()
        {
            // Character A offline for 40 days, alt B logged off 1 day ago, same account 7.
            var a = AccountActivityIndex.SeenAt(false, Now, Now - 41 * Day, Now - 40 * Day);
            var b = AccountActivityIndex.SeenAt(false, Now, Now - 2 * Day, Now - 1 * Day);
            var index = AccountActivityIndex.Build(new (uint, double)[] { (7, a.Value), (7, b.Value) });
            Assert.AreEqual(Now - 1 * Day, index.LastSeen(7));
            Assert.IsTrue(index.IsActive(7, Now, 7));
        }
    }
}
