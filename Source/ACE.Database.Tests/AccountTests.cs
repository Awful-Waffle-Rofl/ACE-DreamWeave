using System;
using System.Net;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Auth;
using ACE.Entity.Enum;

[assembly: DoNotParallelize]

namespace ACE.Database.Tests
{
    [TestClass]
    public class AccountTests
    {
        private static readonly AuthenticationDatabase authDb = new AuthenticationDatabase();

        // created lazily (not in ClassInitialize) so a missing database skips tests instead of failing them.
        // the name is unique per run: these tests write to whatever ace_auth database Config.js points at,
        // so they must not collide with rows left behind by previous runs. Point Config.js at a disposable
        // database, never a production one.
        private static string accountName;
        private static uint accountId;

        private const string accountPassword = "testpassword1";

        private static void EnsureTestAccount()
        {
            TestEnvironment.RequireAuthDatabase(authDb);

            if (accountName != null)
                return;

            var name = "test" + Guid.NewGuid().ToString("N");
            var account = authDb.CreateAccount(name, accountPassword, AccessLevel.Player, IPAddress.Parse("127.0.0.1"));

            accountId = account.AccountId;
            accountName = name;
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void CreateAccount_GetAccountByName_ReturnsAccount()
        {
            EnsureTestAccount();

            var results = authDb.GetAccountByName(accountName);
            Assert.IsNotNull(results);
            Assert.AreEqual(accountId, results.AccountId);
            Assert.AreEqual((uint)AccessLevel.Player, results.AccessLevel);
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void UpdateAccountAccessLevelToSentinelAndBackToPlayer_ReturnsAccount()
        {
            EnsureTestAccount();

            authDb.UpdateAccountAccessLevel(accountId, AccessLevel.Sentinel);
            var results = authDb.GetAccountByName(accountName);
            Assert.IsNotNull(results);
            Assert.AreEqual((uint)AccessLevel.Sentinel, results.AccessLevel);

            authDb.UpdateAccountAccessLevel(accountId, AccessLevel.Player);
            var results2 = authDb.GetAccountByName(accountName);
            Assert.IsNotNull(results2);
            Assert.AreEqual((uint)AccessLevel.Player, results2.AccessLevel);
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GetAccountIdByName_ReturnsAccount()
        {
            EnsureTestAccount();

            var id = authDb.GetAccountIdByName(accountName);
            Assert.AreEqual(accountId, id);

            var results = authDb.GetAccountById(id);
            Assert.IsNotNull(results);
            Assert.AreEqual(id, results.AccountId);
            Assert.AreEqual(accountName, results.AccountName);
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GetAccountByName_TestPassword_ReturnsMatch()
        {
            EnsureTestAccount();

            var results = authDb.GetAccountByName(accountName);
            Assert.IsNotNull(results);
            Assert.IsTrue(results.PasswordMatches(accountPassword));
        }

        [TestMethod]
        [TestCategory("RequiresMySql")]
        public void GetAccountByName_TestPassword_ReturnsNoMatch()
        {
            EnsureTestAccount();

            var results = authDb.GetAccountByName(accountName);
            Assert.IsNotNull(results);
            Assert.IsFalse(results.PasswordMatches("wrongpassword"));
        }
    }
}
