using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AvatarLedgerTests
    {
        // Defaults: soft cap 500, falloff 0.25, hard cap 3x soft = 1500.
        private static readonly AvatarTuning T = AvatarTuning.Defaults;
        private static readonly DateTime Today = new DateTime(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        private static AllegianceAvatarContribution Row(uint avatarId, uint accountId, DateTime day, ulong raw, ulong credited, uint character = 0) =>
            new AllegianceAvatarContribution { AvatarId = avatarId, AccountId = accountId, Day = day, RawXp = raw, Credited = credited, LastCharacterId = character };

        [TestMethod]
        public void Credit_AppliesDailyCurvePerAccount()
        {
            var caps = new AvatarAccountCaps();

            // 500 linear, then 100 past the soft cap at 0.25 = 25.
            Assert.AreEqual(525L, caps.Credit(1, Today, 600, T));
            // A different account has its own cap.
            Assert.AreEqual(400L, caps.Credit(2, Today, 400, T));
            // Account 1 is already past soft: 100 more credits 25.
            Assert.AreEqual(25L, caps.Credit(1, Today, 100, T));
        }

        [TestMethod]
        public void Credit_TwoCharactersSameAccount_ShareOneCap()
        {
            var caps = new AvatarAccountCaps();
            var ledger = new AvatarLedger(7);

            // Two characters of account 1 contribute 300 each to the same Avatar.
            var first = caps.Credit(1, Today, 300, T);
            ledger.Record(1, 1001, 300, first, Today);
            var second = caps.Credit(1, Today, 300, T);
            ledger.Record(1, 1002, 300, second, Today);

            Assert.AreEqual(300L, first);
            Assert.AreEqual(200L + 25L, second);

            var rows = ledger.TakeDirty();
            Assert.AreEqual(1, rows.Count, "one row per account per day, never per character");
            Assert.AreEqual(600UL, rows[0].RawXp);
            Assert.AreEqual(525UL, rows[0].Credited);
            Assert.AreEqual(1002U, rows[0].LastCharacterId);
        }

        [TestMethod]
        public void Credit_SameAccountTwoAvatars_ShareOneCap()
        {
            var caps = new AvatarAccountCaps();
            var avatarA = new AvatarLedger(7);
            var avatarB = new AvatarLedger(8);

            var a = caps.Credit(1, Today, 400, T);
            avatarA.Record(1, 1001, 400, a, Today);
            var b = caps.Credit(1, Today, 400, T);
            avatarB.Record(1, 2001, 400, b, Today);

            Assert.AreEqual(400L, a);
            // Only 100 of the soft cap is left for the second Avatar, then 300 * 0.25.
            Assert.AreEqual(100L + 75L, b);
            Assert.AreEqual(575L, caps.CreditedToday(1, Today));
        }

        [TestMethod]
        public void Credit_NewUtcDay_ResetsCap()
        {
            var caps = new AvatarAccountCaps();

            Assert.AreEqual(500L, caps.Credit(1, Today, 500, T));
            Assert.AreEqual(25L, caps.Credit(1, Today, 100, T));
            Assert.AreEqual(100L, caps.Credit(1, Today.AddDays(1), 100, T));
        }

        [TestMethod]
        public void Load_RebuildsTodaysCapFromLedgerRows()
        {
            var caps = new AvatarAccountCaps();
            // Raw and credited differ, so a swap of the two columns fails: curve(600 + 100) = 550, minus 525 = 25.
            // Swapped (raw 525, credited 600) it would credit curve(625) - 600 = 0.
            caps.Restore(new[] { Row(7, 1, Today, 600, 525) });

            Assert.AreEqual(25L, caps.Credit(1, Today, 100, T));
        }

        [TestMethod]
        public void Credit_IsIndependentOfBatchSplit_PastTheSoftCap()
        {
            var caps = new AvatarAccountCaps();
            Assert.AreEqual(500L, caps.Credit(1, Today, 500, T));

            // 250 batches of 2 points past the soft cap. Flooring each batch's falloff part alone would give
            // floor(2 x 0.25) = 0 every time; the day's 500 extra raw points are worth 125.
            long past = 0;
            for (var i = 0; i < 250; i++)
                past += caps.Credit(1, Today, 2, T);

            Assert.AreEqual(125L, past);
            Assert.AreEqual(625L, caps.CreditedToday(1, Today));
        }

        [TestMethod]
        public void Credit_OnePointBatches_PastTheSoftCap_MatchesOneBatch()
        {
            var caps = new AvatarAccountCaps();
            caps.Credit(1, Today, 500, T);

            // 400 one-point batches (the manager calls once per Avatar per account, so two Avatars split
            // an account's raw the same way): 400 extra raw points credit 100 in total.
            long past = 0;
            for (var i = 0; i < 400; i++)
                past += caps.Credit(1, Today, 1, T);

            Assert.AreEqual(100L, past);
        }

        [TestMethod]
        public void Restore_SumsCreditedAcrossAvatars_AndIgnoresOlderDays()
        {
            var caps = new AvatarAccountCaps();
            caps.Restore(new[]
            {
                // Raw 600 and credited 525 summed over two Avatars; a raw/credited swap would credit nothing below.
                Row(7, 1, Today, 400, 350),
                Row(8, 1, Today, 200, 175),
                Row(7, 2, Today.AddDays(-1), 500, 500),
            });

            Assert.AreEqual(525L, caps.CreditedToday(1, Today));
            Assert.AreEqual(0L, caps.CreditedToday(2, Today));
            Assert.AreEqual(25L, caps.Credit(1, Today, 100, T));
            Assert.AreEqual(100L, caps.Credit(2, Today, 100, T));
        }

        [TestMethod]
        public void Ledger_TakeDirty_ClearsDirty_AndMarkDirtyResubmitsAbsoluteValues()
        {
            var ledger = new AvatarLedger(7);
            ledger.Record(1, 1001, 100, 100, Today);

            var first = ledger.TakeDirty();
            Assert.AreEqual(1, first.Count);
            Assert.AreEqual(0, ledger.TakeDirty().Count, "taken rows are no longer dirty");

            ledger.MarkDirty(first);
            var second = ledger.TakeDirty();
            Assert.AreEqual(1, second.Count);
            Assert.AreEqual(100UL, second[0].Credited, "the retry carries the absolute total, not a doubled one");
            Assert.AreEqual(7U, second[0].AvatarId);
            Assert.AreEqual(Today, second[0].Day);
        }

        [TestMethod]
        public void Ledger_Restore_IsNotDirty_AndRecordAddsOnTop()
        {
            var ledger = new AvatarLedger(7);
            ledger.Restore(new[] { Row(7, 1, Today, 50, 40, 1001), Row(8, 1, Today, 999, 999) });

            Assert.AreEqual(0, ledger.TakeDirty().Count, "restored rows are already stored");

            ledger.Record(1, 1002, 10, 10, Today);
            var rows = ledger.TakeDirty();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(60UL, rows[0].RawXp);
            Assert.AreEqual(50UL, rows[0].Credited, "the other Avatar's row is not restored into this ledger");
        }

        [TestMethod]
        public void Ledger_Top_SumsCreditedSinceDay_OrderedDescending()
        {
            var ledger = new AvatarLedger(7);
            ledger.Record(1, 0, 100, 100, Today.AddDays(-40)); // before the window
            ledger.Record(1, 0, 100, 100, Today.AddDays(-2));
            ledger.Record(2, 0, 300, 300, Today);
            ledger.Record(3, 0, 150, 150, Today.AddDays(-1));
            ledger.Record(3, 0, 150, 150, Today);
            ledger.Record(4, 0, 300, 300, Today.AddDays(-29));

            var top = ledger.Top(Today.AddDays(-30), 3);

            CollectionAssert.AreEqual(
                new[] { (2U, 300L), (3U, 300L), (4U, 300L) },
                top.Select(r => (r.AccountId, r.Credited)).ToArray(),
                "ties break on the lower account id");

            var all = ledger.Top(Today.AddDays(-30), 10);
            Assert.AreEqual(4, all.Count);
            Assert.AreEqual((1U, 100L), (all[3].AccountId, all[3].Credited), "the row from 40 days ago is outside the window");
        }
    }
}
