using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two account_vault_setting members of IAccountVaultBackend, driven against the recording fake.
    ///
    /// As with AccountMuleFormBackendTests, the point is the THREE-WAY answer: "no row" (which the
    /// store layer will read as the defaults, salvage combine ON) must stay distinguishable from "the
    /// read failed" (which must never be read as the defaults).
    /// </summary>
    [TestClass]
    public class AccountVaultSettingBackendTests
    {
        private const uint AccountId = 4242;

        private static AccountVaultSetting Row(bool salvageCombine, uint setBy = 0x50000001)
        {
            return new AccountVaultSetting
            {
                AccountId = AccountId,
                SalvageCombine = salvageCombine,
                SetByCharacterGuid = setBy,
            };
        }

        [TestMethod]
        public void Read_WithNoRow_IsOkWithNullRow()
        {
            var backend = new FakeVaultBackend();

            var (ok, row) = backend.GetAccountVaultSetting(AccountId);

            Assert.IsTrue(ok, "an account that has never changed a setting is not a read failure");
            Assert.IsNull(row);
            Assert.AreEqual(1, backend.VaultSettingReads);
        }

        [TestMethod]
        public void Upsert_ThenRead_RoundTripsEveryField()
        {
            var backend = new FakeVaultBackend();

            Assert.IsTrue(backend.UpsertAccountVaultSetting(Row(false)));

            var (ok, row) = backend.GetAccountVaultSetting(AccountId);

            Assert.IsTrue(ok);
            Assert.IsNotNull(row);
            Assert.AreEqual(AccountId, row.AccountId);
            Assert.IsFalse(row.SalvageCombine);
            Assert.AreEqual(0x50000001u, row.SetByCharacterGuid);
        }

        [TestMethod]
        public void Upsert_Twice_OverwritesRatherThanAddingASecondRow()
        {
            var backend = new FakeVaultBackend();

            backend.UpsertAccountVaultSetting(Row(false, 0x50000001));
            backend.UpsertAccountVaultSetting(Row(true, 0x50000002));

            var (ok, row) = backend.GetAccountVaultSetting(AccountId);

            Assert.IsTrue(ok);
            Assert.IsTrue(row.SalvageCombine, "the second upsert must replace the first value");
            Assert.AreEqual(0x50000002u, row.SetByCharacterGuid);
            Assert.AreEqual(1, backend.VaultSettings.Count);
            Assert.AreEqual(2, backend.UpsertVaultSettingCalls);
        }

        [TestMethod]
        public void Read_ThatFails_IsDistinguishableFromNoRow()
        {
            var backend = new FakeVaultBackend();

            backend.UpsertAccountVaultSetting(Row(false));
            backend.FailVaultSettingRead = true;

            var (ok, row) = backend.GetAccountVaultSetting(AccountId);

            Assert.IsFalse(ok, "a failed read must report not-ok even though the row exists");
            Assert.IsNull(row);
        }

        [TestMethod]
        public void Upsert_ThatFails_ReportsFalseAndLeavesTheStoredRowAlone()
        {
            var backend = new FakeVaultBackend();

            backend.UpsertAccountVaultSetting(Row(false));

            backend.FailVaultSettingUpsert = true;
            Assert.IsFalse(backend.UpsertAccountVaultSetting(Row(true)));

            backend.FailVaultSettingUpsert = false;
            var (ok, row) = backend.GetAccountVaultSetting(AccountId);

            Assert.IsTrue(ok);
            Assert.IsFalse(row.SalvageCombine, "a failed upsert must not have replaced the stored setting");
        }
    }
}
