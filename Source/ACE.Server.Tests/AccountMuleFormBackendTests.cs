using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two new IAccountVaultBackend members, driven against the recording fake.
    ///
    /// The point of this class is the THREE-WAY answer. A single-row read has to distinguish "the
    /// account has no saved look" from "the read failed", and both are naturally null, so the
    /// signature carries a second channel. These tests are what keep that channel from being quietly
    /// collapsed back into a bare reference by a later refactor.
    /// </summary>
    [TestClass]
    public class AccountMuleFormBackendTests
    {
        private const uint AccountId = 4242;

        private static AccountMuleForm Row(uint wcid = 1234, string name = "Tusker Guard")
        {
            return new AccountMuleForm
            {
                AccountId = AccountId,
                FormWcid = wcid,
                FormName = name,
                SetByCharacterGuid = 0x50000001,
                SetUnixTime = 1756684800.0,
            };
        }

        [TestMethod]
        public void Upsert_ThenRead_RoundTripsEveryField()
        {
            var backend = new FakeVaultBackend();

            Assert.IsTrue(backend.UpsertAccountMuleForm(Row()));

            var (ok, row) = backend.GetAccountMuleForm(AccountId);

            Assert.IsTrue(ok, "a successful read must report ok");
            Assert.IsNotNull(row);
            Assert.AreEqual(AccountId, row.AccountId);
            Assert.AreEqual(1234u, row.FormWcid);
            Assert.AreEqual("Tusker Guard", row.FormName);
            Assert.AreEqual(0x50000001u, row.SetByCharacterGuid);
            Assert.AreEqual(1756684800.0, row.SetUnixTime, 0.0001);
        }

        [TestMethod]
        public void Upsert_ReplacesTheExistingLook_RatherThanAddingASecondRow()
        {
            var backend = new FakeVaultBackend();

            backend.UpsertAccountMuleForm(Row(1234, "Tusker Guard"));
            backend.UpsertAccountMuleForm(Row(5678, "Olthoi Soldier"));

            var (ok, row) = backend.GetAccountMuleForm(AccountId);

            Assert.IsTrue(ok);
            Assert.AreEqual(5678u, row.FormWcid);
            Assert.AreEqual("Olthoi Soldier", row.FormName);
            Assert.AreEqual(2, backend.UpsertMuleFormCalls);
        }

        [TestMethod]
        public void Read_WithNoRow_IsNotAFailure()
        {
            var backend = new FakeVaultBackend();

            var (ok, row) = backend.GetAccountMuleForm(AccountId);

            Assert.IsTrue(ok, "an account that has never earned a form is not a read failure");
            Assert.IsNull(row);
            Assert.AreEqual(1, backend.MuleFormReads);
        }

        [TestMethod]
        public void Read_ThatFails_IsDistinguishableFromNoRow()
        {
            var backend = new FakeVaultBackend { FailMuleFormRead = true };

            backend.UpsertAccountMuleForm(Row());

            var (ok, row) = backend.GetAccountMuleForm(AccountId);

            Assert.IsFalse(ok, "a failed read must report not-ok even though the row exists");
            Assert.IsNull(row);
        }

        [TestMethod]
        public void Upsert_ThatFails_ReportsFalseAndLeavesTheStoredRowAlone()
        {
            var backend = new FakeVaultBackend();

            backend.UpsertAccountMuleForm(Row(1234, "Tusker Guard"));

            backend.FailMuleFormUpsert = true;
            Assert.IsFalse(backend.UpsertAccountMuleForm(Row(5678, "Olthoi Soldier")));

            backend.FailMuleFormUpsert = false;
            var (ok, row) = backend.GetAccountMuleForm(AccountId);

            Assert.IsTrue(ok);
            Assert.AreEqual(1234u, row.FormWcid, "a failed upsert must not have replaced the saved look");
        }
    }
}
