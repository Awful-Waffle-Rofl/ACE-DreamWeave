using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Auth;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// AdminAuthorizer.Check - the ONLY place in the admin panel design that enforces the ban check
    /// (the market's own login never checks bans), and the only gate deciding Admin vs not (PLAN-P1.md
    /// section 2, DESIGN.md section 7.1/8). Pure unit tests: a fake Func&lt;uint, Account&gt; reader, no DB.
    /// </summary>
    [TestClass]
    public class AdminAuthorizerTests
    {
        private const uint AccountId = 4242;

        private static readonly DateTime FixedNow = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

        [DataTestMethod]
        [DataRow(0u)]
        [DataRow(1u)]
        [DataRow(2u)]
        [DataRow(3u)]
        [DataRow(4u)]
        public void Check_AccessLevels0To4_NotAdmin(uint accessLevel)
        {
            var authorizer = new AdminAuthorizer(id => new Account { AccountId = id, AccountName = "acct", AccessLevel = accessLevel }, () => FixedNow);

            var result = authorizer.Check(AccountId, out var principal);

            Assert.AreEqual(AdminCheck.NotAdmin, result);
            Assert.AreEqual(default(AdminPrincipal), principal);
        }

        [TestMethod]
        public void Check_Level6_NotAdmin()
        {
            // Equality, not >=: a level above Admin (5) must still be refused.
            var authorizer = new AdminAuthorizer(id => new Account { AccountId = id, AccountName = "acct", AccessLevel = 6 }, () => FixedNow);

            Assert.AreEqual(AdminCheck.NotAdmin, authorizer.Check(AccountId, out _));
        }

        [TestMethod]
        public void Check_Admin_ReturnsName()
        {
            var authorizer = new AdminAuthorizer(id => new Account { AccountId = id, AccountName = "weft", AccessLevel = 5 }, () => FixedNow);

            var result = authorizer.Check(AccountId, out var principal);

            Assert.AreEqual(AdminCheck.Admin, result);
            Assert.AreEqual(AccountId, principal.AccountId);
            Assert.AreEqual("weft", principal.AccountName);
            Assert.AreEqual(ACE.Entity.Enum.AccessLevel.Admin, principal.Level, "the principal carries the account's access level for the command list's permitted field (PLAN-P4.md section 2)");
        }

        [TestMethod]
        public void Check_MissingAccount_NotAdmin()
        {
            var authorizer = new AdminAuthorizer(id => null, () => FixedNow);

            Assert.AreEqual(AdminCheck.NotAdmin, authorizer.Check(AccountId, out _));
        }

        [TestMethod]
        public void Check_AccountIdZero_NeverReads()
        {
            var reads = 0;
            var authorizer = new AdminAuthorizer(id => { reads++; return new Account { AccountId = id, AccessLevel = 5 }; }, () => FixedNow);

            var result = authorizer.Check(0, out var principal);

            Assert.AreEqual(AdminCheck.NotAdmin, result);
            Assert.AreEqual(0, reads, "account id 0 can never resolve to a row, so the reader must not be called at all");
        }

        [TestMethod]
        public void Check_BannedAdmin_NotAdmin()
        {
            var authorizer = new AdminAuthorizer(id => new Account
            {
                AccountId = id,
                AccountName = "banned",
                AccessLevel = 5,
                BanExpireTime = FixedNow.AddDays(1),
            }, () => FixedNow);

            Assert.AreEqual(AdminCheck.NotAdmin, authorizer.Check(AccountId, out _),
                "the market's own login never checks bans, so AdminAuthorizer is the only place this enforces");
        }

        [TestMethod]
        public void Check_ExpiredBan_Admin()
        {
            var authorizer = new AdminAuthorizer(id => new Account
            {
                AccountId = id,
                AccountName = "formerlybanned",
                AccessLevel = 5,
                BanExpireTime = FixedNow.AddDays(-1),
            }, () => FixedNow);

            Assert.AreEqual(AdminCheck.Admin, authorizer.Check(AccountId, out var principal));
            Assert.AreEqual("formerlybanned", principal.AccountName);
        }

        [TestMethod]
        public void Check_ReaderThrows_Unavailable()
        {
            var authorizer = new AdminAuthorizer(id => throw new InvalidOperationException("db down"), () => FixedNow);

            Assert.AreEqual(AdminCheck.Unavailable, authorizer.Check(AccountId, out var principal));
            Assert.AreEqual(default(AdminPrincipal), principal);
        }

        [TestMethod]
        public void Check_NoReader_Unavailable()
        {
            var authorizer = new AdminAuthorizer(null, () => FixedNow);

            Assert.AreEqual(AdminCheck.Unavailable, authorizer.Check(AccountId, out _));
        }

        [TestMethod]
        public void Check_DemotedBetweenCalls_SecondRefused()
        {
            var level = 5u;
            var authorizer = new AdminAuthorizer(id => new Account { AccountId = id, AccountName = "demoted", AccessLevel = level }, () => FixedNow);

            Assert.AreEqual(AdminCheck.Admin, authorizer.Check(AccountId, out _), "first call: still Admin");

            level = 4;

            Assert.AreEqual(AdminCheck.NotAdmin, authorizer.Check(AccountId, out _),
                "second call: no caching - the demotion must be reflected on the very next call");
        }
    }
}
