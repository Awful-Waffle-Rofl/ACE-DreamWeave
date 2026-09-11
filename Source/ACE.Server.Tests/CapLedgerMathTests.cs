using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure math for the CAP audit ledger (round 1). CapLedger is Player-free, so these run without
    /// a live Player.
    /// </summary>
    [TestClass]
    public class CapLedgerMathTests
    {
        [TestMethod]
        public void Unexplained_BalancedCharacter_IsZero()
        {
            Assert.AreEqual(0, CapLedger.Unexplained(totalEarned: 10, available: 3, ownedCost: 7, sinkSpend: 0));
        }

        [TestMethod]
        public void Unexplained_VendorSpend_NettedBySinkSpend()
        {
            // A vendor spend of 2 is accounted for when sinkSpend correctly carries it...
            Assert.AreEqual(0, CapLedger.Unexplained(totalEarned: 10, available: 1, ownedCost: 7, sinkSpend: 2));

            // ...and shows up as unexplained when sinkSpend is (wrongly) left at 0.
            Assert.AreEqual(2, CapLedger.Unexplained(totalEarned: 10, available: 1, ownedCost: 7, sinkSpend: 0));
        }

        [TestMethod]
        public void Unexplained_ProdCharacter_Rez()
        {
            // Total 22, no 9017 row so Available 0, owned ranks priced 21.
            Assert.AreEqual(1, CapLedger.Unexplained(totalEarned: 22, available: 0, ownedCost: 21, sinkSpend: 0));
        }

        [TestMethod]
        public void Unexplained_ProdCharacter_Bro()
        {
            // Total 9, Available 2, owned ranks priced 5.
            Assert.AreEqual(2, CapLedger.Unexplained(totalEarned: 9, available: 2, ownedCost: 5, sinkSpend: 0));
        }

        [TestMethod]
        public void ToCode_PinnedLiterals()
        {
            Assert.AreEqual("learn", CapLedgerReason.Learn.ToCode());
            Assert.AreEqual("unlearn", CapLedgerReason.Unlearn.ToCode());
            Assert.AreEqual("respec", CapLedgerReason.Respec.ToCode());
            Assert.AreEqual("grant_milestone", CapLedgerReason.GrantMilestone.ToCode());
            Assert.AreEqual("grant_enlightenment", CapLedgerReason.GrantEnlightenment.ToCode());
            Assert.AreEqual("buy_lum", CapLedgerReason.BuyLum.ToCode());
            Assert.AreEqual("buy_xp", CapLedgerReason.BuyXp.ToCode());
            Assert.AreEqual("grant_admin", CapLedgerReason.GrantAdmin.ToCode());
            Assert.AreEqual("voucher_buy", CapLedgerReason.VoucherBuy.ToCode());
            Assert.AreEqual("voucher_apply", CapLedgerReason.VoucherApply.ToCode());
            Assert.AreEqual("voucher_refund", CapLedgerReason.VoucherRefund.ToCode());
            Assert.AreEqual("voucher_refund_retired", CapLedgerReason.VoucherRefundRetired.ToCode());
            Assert.AreEqual("vendor_debit", CapLedgerReason.VendorDebit.ToCode());
            Assert.AreEqual("sweep_retired", CapLedgerReason.SweepRetired.ToCode());
            Assert.AreEqual("facet_switch", CapLedgerReason.FacetSwitch.ToCode());
            Assert.AreEqual("admin_correct", CapLedgerReason.AdminCorrect.ToCode());
            Assert.AreEqual("grant_item", CapLedgerReason.GrantItem.ToCode());
        }

        [TestMethod]
        public void ToCode_AllCodesAreDistinctNonEmptyAndFitTheColumn()
        {
            var codes = Enum.GetValues(typeof(CapLedgerReason))
                .Cast<CapLedgerReason>()
                .Select(r => r.ToCode())
                .ToList();

            foreach (var code in codes)
            {
                Assert.IsFalse(string.IsNullOrEmpty(code), "every reason code must be non-empty");
                Assert.IsTrue(code.Length <= 32, $"reason code '{code}' exceeds the 32-char reason column width");
            }

            var distinct = codes.Distinct().ToList();
            Assert.AreEqual(codes.Count, distinct.Count, "every CapLedgerReason member must map to a distinct code");
        }

        [TestMethod]
        public void CapLedgerReason_HasExactlySeventeenMembers()
        {
            // Guards against a member being added without a corresponding ToCode branch: an 18th
            // member fails this assertion even if ToCode silently returns something for it.
            //
            // Was 16 through round 1. Round 2's funnel found a SEVENTEENTH write site the round 1
            // survey missed - Gem.cs's ClassAbilityPointValue consumable, a live player-facing
            // CAP-gain avenue with two shipped weenies behind it - and CapLedgerReason.GrantItem was
            // appended for it. Appended at the END deliberately: every existing member keeps its
            // ordinal, and the column stores ToCode's string anyway, so no already-written row moves.
            Assert.AreEqual(17, Enum.GetValues(typeof(CapLedgerReason)).Length);
        }

        [TestMethod]
        public void IsSinkReason_TrueForExactlyTwoMembers()
        {
            var sinkReasons = Enum.GetValues(typeof(CapLedgerReason))
                .Cast<CapLedgerReason>()
                .Where(CapLedger.IsSinkReason)
                .ToList();

            CollectionAssert.AreEquivalent(
                new[] { CapLedgerReason.VoucherBuy, CapLedgerReason.VendorDebit },
                sinkReasons);
        }

        // Round 3: the login audit's sinkSpend sign convention, and the rank-divergence counter it
        // feeds into CapLedger.Unexplained. Both are pure and Player-free, same as everything else in
        // this file.

        [TestMethod]
        public void SinkSpend_VoucherBuyOnly_NegatesTheDebitIntoAPositiveSpend()
        {
            var rows = new List<CharacterCapLedger>
            {
                new CharacterCapLedger { Id = 1, CharacterId = 1, CharacterName = "Test", Reason = CapLedgerReason.VoucherBuy.ToCode(), DeltaAvailable = -2, DeltaTotal = 0 },
            };

            var sinkSpend = CapLedger.SinkSpend(rows);
            Assert.AreEqual(2, sinkSpend, "a voucher_buy of -2 must produce a sinkSpend of +2, not -2");

            // totalEarned 5, available 3 (5 earned, 2 spent on the voucher), ownedCost 0 -> balances
            // exactly when sinkSpend correctly carries the 2-point debit.
            Assert.AreEqual(0, CapLedger.Unexplained(totalEarned: 5, available: 3, ownedCost: 0, sinkSpend: sinkSpend));
        }

        [TestMethod]
        public void SinkSpend_IgnoresNonSinkReasons()
        {
            var rows = new List<CharacterCapLedger>
            {
                new CharacterCapLedger { Id = 1, Reason = CapLedgerReason.Learn.ToCode(), DeltaAvailable = -3 },
                new CharacterCapLedger { Id = 2, Reason = CapLedgerReason.GrantMilestone.ToCode(), DeltaAvailable = 5, DeltaTotal = 5 },
            };

            Assert.AreEqual(0, CapLedger.SinkSpend(rows), "Learn and GrantMilestone are priced by ownedCost/Total, not sinkSpend");
        }

        [TestMethod]
        public void CountRankDivergences_NewestRowPerAbility_FlagsOnlyTheDisagreement()
        {
            var rows = new List<CharacterCapLedger>
            {
                new CharacterCapLedger { Id = 1, Ability = "multishot", RankAfter = 1 },
                new CharacterCapLedger { Id = 2, Ability = "multishot", RankAfter = 2 }, // newest for multishot
                new CharacterCapLedger { Id = 3, Ability = "parry", RankAfter = 1 },     // newest for parry, agrees
                new CharacterCapLedger { Id = 4, Ability = null, RankAfter = 3 },        // no ability - ignored
                new CharacterCapLedger { Id = 5, Ability = "taunt", RankAfter = null },  // no rankAfter - ignored
            };

            // multishot: live rank 1 disagrees with the newest ledger row's rankAfter of 2.
            // parry: live rank 1 agrees. taunt: absent from the snapshot entirely (never learned).
            var liveRanks = new Dictionary<string, int> { ["multishot"] = 1, ["parry"] = 1 };

            Assert.AreEqual(1, CapLedger.CountRankDivergences(rows, liveRanks));
        }

        [TestMethod]
        public void CountRankDivergences_PicksNewestById_NotScanOrder()
        {
            var rows = new List<CharacterCapLedger>
            {
                new CharacterCapLedger { Id = 5, Ability = "multishot", RankAfter = 2 },
                new CharacterCapLedger { Id = 1, Ability = "multishot", RankAfter = 1 },
            };

            // The id-5 row is newest despite appearing first in the list; a live rank of 2 must agree
            // with it, not with the id-1 row.
            var liveRanks = new Dictionary<string, int> { ["multishot"] = 2 };

            Assert.AreEqual(0, CapLedger.CountRankDivergences(rows, liveRanks));
        }

        [TestMethod]
        public void CountRankDivergences_AbilityMissingFromSnapshot_TreatedAsRankZero()
        {
            // Mirrors GetClassAbilityRank's own not-found default: an ability absent from the
            // snapshot (never learned, or unlearned back to 0) is rank 0, not a divergence by itself.
            var rows = new List<CharacterCapLedger>
            {
                new CharacterCapLedger { Id = 1, Ability = "taunt", RankAfter = 0 },
            };

            Assert.AreEqual(0, CapLedger.CountRankDivergences(rows, new Dictionary<string, int>()));
        }

        [TestMethod]
        public void CountRankDivergences_NoRows_IsZero()
        {
            Assert.AreEqual(0, CapLedger.CountRankDivergences(new List<CharacterCapLedger>(), new Dictionary<string, int>()));
        }

        // Round 3 code review fix: FirstDetectedAt was hardcoded null on every upsert, so the audit
        // upsert's COALESCE(first_Detected_At, VALUES(first_Detected_At)) was always COALESCE(null,
        // null) and the column could never hold a value. These two pure helpers are the fix's two
        // halves - the write-side ternary, and a C#-side mirror of the SQL's COALESCE formula - chained
        // here to simulate the two-upsert sequence the real fix depends on.

        [TestMethod]
        public void FirstDetectedAtToWrite_Balanced_IsNull()
        {
            Assert.IsNull(CapLedger.FirstDetectedAtToWrite(isBalanced: true, now: DateTime.UtcNow));
        }

        [TestMethod]
        public void FirstDetectedAtToWrite_Unbalanced_IsTheGivenTimestamp()
        {
            var now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(now, CapLedger.FirstDetectedAtToWrite(isBalanced: false, now: now));
        }

        [TestMethod]
        public void CoalesceFirstDetectedAt_NoExistingValue_TakesTheIncomingOne()
        {
            var t1 = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(t1, CapLedger.CoalesceFirstDetectedAt(existing: null, incoming: t1));
        }

        [TestMethod]
        public void CoalesceFirstDetectedAt_ExistingValue_SurvivesAnyIncomingValue()
        {
            var t1 = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var t2 = t1.AddDays(1);

            Assert.AreEqual(t1, CapLedger.CoalesceFirstDetectedAt(existing: t1, incoming: t2));
            Assert.AreEqual(t1, CapLedger.CoalesceFirstDetectedAt(existing: t1, incoming: null));
        }

        /// <summary>
        /// Simulates the exact two-upsert sequence from the finding: unbalanced at T1 with no prior
        /// row, then unbalanced again at T2 - T1 must survive both. This chains
        /// FirstDetectedAtToWrite (the C# write-side ternary) with CoalesceFirstDetectedAt (a mirror of
        /// the upsert SQL's COALESCE) entirely in memory. It proves the C#-side logic is internally
        /// consistent; it CANNOT prove the real DAO/migration behaves this way against MySQL, since
        /// ACE.Server.Tests has no database - that needs a live check.
        /// </summary>
        [TestMethod]
        public void FirstDetectedAtSequence_TwoUnbalancedUpserts_PreservesTheFirstTimestamp()
        {
            var t1 = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);
            var t2 = t1.AddDays(1);

            var afterFirstUpsert = CapLedger.CoalesceFirstDetectedAt(
                existing: null,
                incoming: CapLedger.FirstDetectedAtToWrite(isBalanced: false, now: t1));
            Assert.AreEqual(t1, afterFirstUpsert);

            var afterSecondUpsert = CapLedger.CoalesceFirstDetectedAt(
                existing: afterFirstUpsert,
                incoming: CapLedger.FirstDetectedAtToWrite(isBalanced: false, now: t2));
            Assert.AreEqual(t1, afterSecondUpsert, "the first-seen timestamp must survive a later upsert, even though the write itself always sends 'now'");
        }
    }
}
