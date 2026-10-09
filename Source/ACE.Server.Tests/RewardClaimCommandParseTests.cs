using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.RewardClaims;

namespace ACE.Server.Tests
{
    [TestClass]
    public class RewardClaimCommandParseTests
    {
        [TestMethod]
        public void WrongArity_OrVerb_IsUsage()
        {
            Assert.AreEqual(RewardClaimCommandKind.Usage, RewardClaimCommandText.Parse(null).Kind);
            Assert.AreEqual(RewardClaimCommandKind.Usage, RewardClaimCommandText.Parse(new string[0]).Kind);
            Assert.AreEqual(RewardClaimCommandKind.Usage, RewardClaimCommandText.Parse(new[] { "show", "AssayRowTigerEyeHammer", "account" }).Kind);
            Assert.AreEqual(RewardClaimCommandKind.Usage, RewardClaimCommandText.Parse(new[] { "wipe", "AssayRowTigerEyeHammer", "account", "bob" }).Kind);
            Assert.AreEqual(RewardClaimCommandKind.Usage, RewardClaimCommandText.Parse(new[] { "show", "AssayRowTigerEyeHammer", "character", "bob" }).Kind);
        }

        [TestMethod]
        public void UnlistedKey_IsRefused()
        {
            var r = RewardClaimCommandText.Parse(new[] { "clear", "assayrowtigereyehammer", "account", "bob" });

            Assert.AreEqual(RewardClaimCommandKind.Clear, r.Kind);
            Assert.IsNotNull(r.Error);
            StringAssert.Contains(r.Error, "is not a reward claim key");

            Assert.IsNotNull(RewardClaimCommandText.Parse(new[] { "show", "QuestStampSeen_Foo", "ip", "1.2.3.4" }).Error);
        }

        [TestMethod]
        public void Account_Parses()
        {
            var r = RewardClaimCommandText.Parse(new[] { "SHOW", "AssayRowObsidianHammer", "Account", "bob" });

            Assert.AreEqual(RewardClaimCommandKind.Show, r.Kind);
            Assert.IsNull(r.Error);
            Assert.AreEqual("AssayRowObsidianHammer", r.Key);
            Assert.AreEqual("bob", r.AccountName);
            Assert.IsNull(r.IpKey);
        }

        [TestMethod]
        public void Ip_ParsesAndNormalizes()
        {
            var r = RewardClaimCommandText.Parse(new[] { "clear", "AssayRowAmethystHammer", "ip", "::ffff:10.1.2.3" });

            Assert.AreEqual(RewardClaimCommandKind.Clear, r.Kind);
            Assert.IsNull(r.Error);
            Assert.AreEqual("10.1.2.3", r.IpKey);
            Assert.IsNull(r.AccountName);
        }

        [TestMethod]
        public void Ip_Garbage_OrBareInteger_IsRefused()
        {
            Assert.IsNotNull(RewardClaimCommandText.Parse(new[] { "show", "AssayRowAmethystHammer", "ip", "not-an-ip" }).Error);
            Assert.IsNotNull(RewardClaimCommandText.Parse(new[] { "show", "AssayRowAmethystHammer", "ip", "5" }).Error);
        }

        [TestMethod]
        public void Usage_SaysClearFreesBoth()
        {
            StringAssert.Contains(RewardClaimCommandText.UsageText, "frees both the account AND the IP");
            Assert.IsTrue(RewardClaimCommandText.UsageText.All(c => c == '\n' || (c >= 0x20 && c < 0x7F)));
        }

        [TestMethod]
        public void SeasonScopedKey_Bare_ResolvesToTheActiveSeason()
        {
            try
            {
                RewardClaimService.ActiveSpeedSeasonSource = () => 7;

                var r = RewardClaimCommandText.Parse(new[] { "show", "ProvingGroundsSpeedTier1@claim", "account", "bob" });

                Assert.IsNull(r.Error);
                Assert.AreEqual("ProvingGroundsSpeedTier1@claim#S7", r.Key);
            }
            finally
            {
                RewardClaimService.ResetForTesting(null);
            }
        }

        [TestMethod]
        public void SeasonScopedKey_Bare_NoActiveSeason_IsRefusedWithAHint()
        {
            try
            {
                RewardClaimService.ActiveSpeedSeasonSource = () => null;

                var r = RewardClaimCommandText.Parse(new[] { "clear", "ProvingGroundsSpeedTier1@claim", "account", "bob" });

                Assert.IsNotNull(r.Error);
                StringAssert.Contains(r.Error, "ProvingGroundsSpeedTier1@claim#S");
            }
            finally
            {
                RewardClaimService.ResetForTesting(null);
            }
        }

        [TestMethod]
        public void SeasonScopedKey_Bare_OnClear_IsRefused_NamingTheCurrentStoredKey()
        {
            try
            {
                RewardClaimService.ActiveSpeedSeasonSource = () => 7;

                var clear = RewardClaimCommandText.Parse(new[] { "clear", "ProvingGroundsSpeedTier2@claim", "account", "bob" });

                Assert.AreEqual(RewardClaimCommandKind.Clear, clear.Kind);
                Assert.IsNotNull(clear.Error, "a bare periodic key must not clear the current period implicitly");
                StringAssert.Contains(clear.Error, "ProvingGroundsSpeedTier2@claim#S7");

                var show = RewardClaimCommandText.Parse(new[] { "show", "ProvingGroundsSpeedTier2@claim", "account", "bob" });
                Assert.IsNull(show.Error, "show may stay bare");
                Assert.AreEqual("ProvingGroundsSpeedTier2@claim#S7", show.Key);

                var explicitClear = RewardClaimCommandText.Parse(new[] { "clear", "ProvingGroundsSpeedTier2@claim#S7", "account", "bob" });
                Assert.IsNull(explicitClear.Error);
                Assert.AreEqual("ProvingGroundsSpeedTier2@claim#S7", explicitClear.Key);
            }
            finally
            {
                RewardClaimService.ResetForTesting(null);
            }
        }

        [TestMethod]
        public void SeasonScopedKey_ExplicitSeason_IsAcceptedVerbatim_EvenWithNoActiveSeason()
        {
            try
            {
                RewardClaimService.ActiveSpeedSeasonSource = () => null;

                var r = RewardClaimCommandText.Parse(new[] { "clear", "ProvingGroundsSpeedTier3@claim#S3", "ip", "10.0.0.1" });

                Assert.IsNull(r.Error);
                Assert.AreEqual("ProvingGroundsSpeedTier3@claim#S3", r.Key);
                Assert.IsNotNull(RewardClaimCommandText.Parse(new[] { "clear", "ProvingGroundsSpeedTier3@claim#Sx", "ip", "10.0.0.1" }).Error);
                Assert.IsNotNull(RewardClaimCommandText.Parse(new[] { "clear", "ProvingGroundsAttackTier1@claim#S3", "ip", "10.0.0.1" }).Error, "a Forever key takes no suffix");
            }
            finally
            {
                RewardClaimService.ResetForTesting(null);
            }
        }

        [TestMethod]
        public void Execute_ClearsOnlyTheNamedSeason()
        {
            var store = new FakeRewardClaimStore();
            store.Rows.Add(new RewardClaim { ClaimKey = "ProvingGroundsSpeedTier1@claim#S3", AccountId = 7, IpKey = "10.0.0.1", ClaimToken = "a" });
            store.Rows.Add(new RewardClaim { ClaimKey = "ProvingGroundsSpeedTier1@claim#S4", AccountId = 7, IpKey = "10.0.0.1", ClaimToken = "b" });

            var reply = RewardClaimCommands.Execute(store, _ => 7u, RewardClaimCommandText.Parse(new[] { "clear", "ProvingGroundsSpeedTier1@claim#S3", "account", "bob" }), out var cleared);

            Assert.IsTrue(cleared, reply);
            Assert.AreEqual("ProvingGroundsSpeedTier1@claim#S4", store.Rows.Single().ClaimKey);
        }

        [TestMethod]
        public void Execute_Show_Clear_AndUnknownAccount()
        {
            var store = new FakeRewardClaimStore();
            store.Rows.Add(new RewardClaim { ClaimKey = "AssayRowTigerEyeHammer", AccountId = 7, IpKey = "10.0.0.1", IpAddress = "10.0.0.1", CharacterId = 0x50000007, NpcWcid = 1002511, ClaimToken = "t1" });
            store.Rows.Add(new RewardClaim { ClaimKey = "AssayRowTigerEyeHammer", AccountId = 8, IpKey = null, IpAddress = "10.0.0.1", CharacterId = 0x50000008, NpcWcid = 1002511, ClaimToken = "t2" });

            Func<string, uint> lookup = name => name == "bob" ? 7u : 0u;

            var unknown = RewardClaimCommands.Execute(store, lookup, RewardClaimCommandText.Parse(new[] { "clear", "AssayRowTigerEyeHammer", "account", "nobody" }), out var clearedUnknown);
            StringAssert.Contains(unknown, "No account named 'nobody'");
            Assert.IsFalse(clearedUnknown);
            Assert.AreEqual(0, store.DeleteCalls);

            var show = RewardClaimCommands.Execute(store, lookup, RewardClaimCommandText.Parse(new[] { "show", "AssayRowTigerEyeHammer", "ip", "10.0.0.1" }), out _);
            StringAssert.Contains(show, "1 row(s)");
            StringAssert.Contains(show, "account 7");

            var clear = RewardClaimCommands.Execute(store, lookup, RewardClaimCommandText.Parse(new[] { "clear", "AssayRowTigerEyeHammer", "account", "bob" }), out var cleared);
            Assert.IsTrue(cleared);
            StringAssert.Contains(clear, "Deleted 1");
            Assert.AreEqual(1, store.Rows.Count);
            Assert.AreEqual(8u, store.Rows.Single().AccountId, "only the matched row is deleted");

            var again = RewardClaimCommands.Execute(store, lookup, RewardClaimCommandText.Parse(new[] { "clear", "AssayRowTigerEyeHammer", "account", "bob" }), out var clearedAgain);
            Assert.IsFalse(clearedAgain);
            StringAssert.Contains(again, "nothing deleted");
        }
    }
}
