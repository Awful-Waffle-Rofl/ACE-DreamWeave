using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Models.Shard;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Every code path that mutates a character's class-ability-point (CAP) counters
    /// (AvailableClassAbilityPoints / TotalClassAbilityPointsEarned), named by the write site it
    /// represents. The enum and its lowercase snake code (<see cref="ToCode"/>) are the fixed
    /// vocabulary the `character_cap_ledger.reason` column stores (see
    /// Database/Updates/Shard/2026-09-10-00-Add-Character-Cap-Ledger.sql's header for why that column
    /// is a varchar rather than this enum's underlying int).
    ///
    /// AS OF ROUND 2 EVERY SITE BELOW IS FUNNELLED. Both counters are get-only properties, and the
    /// single mutator Player.AdjustClassAbilityPoints (Player_ClassAbilities.cs:567) is the only thing
    /// that writes either of them; it appends one `character_cap_ledger` row per call. Each citation
    /// below therefore names the AdjustClassAbilityPoints call (or the grant helper that reaches it)
    /// rather than a bare property assignment, because no bare assignment exists any more - a new one
    /// would not compile. CapFunnelTests asserts that over the whole server project.
    ///
    /// <see cref="Player_ClassAbilities.TryBuyClassAbilityToken"/> (Player_ClassAbilities.cs:971)
    /// deliberately has NO reason code here: it spends Luminance only (via
    /// TrySpendLuminanceIncludingBank) and touches neither counter - verified against
    /// Player_ClassAbilities.cs, where the method never reads or writes either property. It exists
    /// purely as a developer-only (AccessLevel.Developer) test-token spawner, distinct from the
    /// player-facing purchase (VoucherBuy, Player_ClassAbilityTokens.cs's
    /// TryPurchaseClassAbilityVoucher), which does spend CAP.
    ///
    /// Line numbers here were re-derived against the tree AFTER round 2's edits landed. They will
    /// move again with the next edit to any of those files, and nothing checks them automatically.
    /// </summary>
    public enum CapLedgerReason
    {
        /// <summary>Player_ClassAbilities.cs:1137, the AdjustClassAbilityPoints call in LearnClassAbility (Player_ClassAbilities.cs:1124) - spends Available on the next rank.</summary>
        Learn,

        /// <summary>Player_ClassAbilities.cs:1297, the AdjustClassAbilityPoints call in UnlearnClassAbility (Player_ClassAbilities.cs:1259, where this is the DEFAULT reason) - refunds a skill's cumulative cost to Available.</summary>
        Unlearn,

        /// <summary>ClassAbilityTrainer.cs:418, the UnlearnClassAbility(ignoreFee: true, reason: Respec, batchId: ...) call inside HandleRespec's (ClassAbilityTrainer.cs:340) per-ability loop over every learned class ability. Every row that loop emits shares one 32-char batch_Id.</summary>
        Respec,

        /// <summary>Player_ClassAbilities.cs:726, GrantMilestoneClassAbilityPoints - pays the level-milestone entitlement via GrantClassAbilityPoints (Player_ClassAbilities.cs:669), whose single write is Player_ClassAbilities.cs:682.</summary>
        GrantMilestone,

        /// <summary>Player_ClassAbilities.cs:781, GrantEnlightenmentClassAbilityPoints - pays the enlightenment-milestone entitlement via GrantClassAbilityPoints.</summary>
        GrantEnlightenment,

        /// <summary>Player_ClassAbilities.cs:848, TryBuyClassAbilityPoints - buys points with Luminance at the piecewise curve price.</summary>
        BuyLum,

        /// <summary>Player_ClassAbilities.cs:919, TryBuyClassAbilityPointsWithXp - buys points with AvailableExperience.</summary>
        BuyXp,

        /// <summary>
        /// TWO write sites, both developer tooling: ClassAbilityCommands.cs:531, HandleGrantSkillPoints
        /// (ClassAbilityCommands.cs:502, command attribute at :499) - the /grantabilitypoints admin
        /// command; and StageTestCommands.cs:207, HandleMyAbilityPoints - the stage-shard
        /// /myabilitypoints self-grant. There is NO lifetime cap for either to bypass (the no-caps
        /// redesign); they skip the EARNING, not a cap. Each row's `detail` carries the caller's own
        /// sourceText ("an admin grant" / "a stage test grant"), so the two stay distinguishable.
        /// </summary>
        GrantAdmin,

        /// <summary>Player_ClassAbilityTokens.cs:143, in TryPurchaseClassAbilityVoucher (Player_ClassAbilityTokens.cs:108) - debits Available up front for a prepaid training token.</summary>
        VoucherBuy,

        /// <summary>
        /// Player_ClassAbilityTokens.cs:192, in ApplyClassAbilityRankPrepaid
        /// (Player_ClassAbilityTokens.cs:182) - a prepaid rank being applied. BOTH DELTAS ARE ZERO and
        /// the row is still written: the spend was already recorded as VoucherBuy at purchase, and this
        /// row is the only evidence the voucher was consumed. Round 2 replaced the old shape, which
        /// credited the rank cost, let LearnClassAbility debit it, and undid the credit on failure -
        /// three ledger rows and one false delta_Available for a single operation.
        /// </summary>
        VoucherApply,

        /// <summary>Player_ClassAbilityTokens.cs:249, in RefundUnusedVoucher (Player_ClassAbilityTokens.cs:205) - refunds an unused, still-live voucher's rank cost to Available.</summary>
        VoucherRefund,

        /// <summary>Player_ClassAbilityTokens.cs:291, in TryRefundRetiredVoucher (Player_ClassAbilityTokens.cs:271) - refunds an unused voucher for an ability that has since been retired, priced from RetiredClassAbilities.</summary>
        VoucherRefundRetired,

        /// <summary>Player_Bank.cs:1714, the DebitBankedAlternateCurrency branch for ClassAbilityPointCurrencyWcid (wcid 1001010, Player_Bank.cs:54; branch test at Player_Bank.cs:1707) - a vendor purchase debiting Available as the alternate-currency balance. Written as a delta against the absolute value TryDebitClassAbilityPoints approved.</summary>
        VendorDebit,

        /// <summary>Player_ClassAbilities.cs:1221, in SweepRetiredClassAbilities (Player_ClassAbilities.cs:1189) - refunds orphaned quest-registry rows for abilities that no longer resolve, crediting Available.</summary>
        SweepRetired,

        /// <summary>Player_Facets.cs:2245, the AdjustClassAbilityPoints call inside a facet switch, applied as a delta against the absolute value FacetPools.AvailableClassAbilityPointsAfterSwap (Player_Facets.cs:2109) computes from the outgoing/incoming facets' ability spend. Unreachable on prod, which holds zero `character_facet` rows - wired for completeness, not because facets are suspected.</summary>
        FacetSwitch,

        /// <summary>Written by SQL, not by C#: Database/Updates/Shard/2026-09-10-01-Correct-Cap-Available-Rez-Bro.sql inserts one 'admin_correct' row per corrected character (Rez 1342177315 +1, Bro 1342177612 +2). Reserved for any future one-off correction; still no C# write site.</summary>
        AdminCorrect,

        /// <summary>
        /// Gem.cs:168, the ClassAbilityPointValue consumable branch - a Gem weenie carrying PropertyInt
        /// 9020 (ClassAbilityPointValue, Source/property-registry.tsv:196) grants that many points when
        /// used, via GrantClassAbilityPoints. ADDED IN ROUND 2, not present in the round 1 enum: this is
        /// a live player-facing CAP-gain avenue, not dev tooling. Two shipped weenies use it today, both
        /// granting 1 point - Proving Grounds Commendation (wcid 1000304, `Content/sql/weenies/1000304
        /// Proving Grounds Commendation.sql:32`) and Meridian Commendation (wcid 1001407,
        /// `Content/sql/weenies/1001407 Meridian Commendation.sql:32`). It needs its own code rather than
        /// sharing GrantAdmin's, because an incident read has to be able to tell a point a player earned
        /// from content apart from one an admin handed out; the item's Name is carried into the row's
        /// `detail`, so the specific gem is still identifiable.
        /// </summary>
        GrantItem,
    }

    /// <summary>
    /// Pure math for the CAP audit ledger, kept static and Player-free so it is unit-testable
    /// without a live Player (the DB layer, EF context, and reason-enum wiring live elsewhere).
    /// </summary>
    public static class CapLedger
    {
        /// <summary>
        /// Maps a <see cref="CapLedgerReason"/> to the fixed lowercase snake code stored in the
        /// `character_cap_ledger.reason` column. Every code is <= 32 characters to fit that column.
        /// </summary>
        public static string ToCode(this CapLedgerReason reason)
        {
            switch (reason)
            {
                case CapLedgerReason.Learn: return "learn";
                case CapLedgerReason.Unlearn: return "unlearn";
                case CapLedgerReason.Respec: return "respec";
                case CapLedgerReason.GrantMilestone: return "grant_milestone";
                case CapLedgerReason.GrantEnlightenment: return "grant_enlightenment";
                case CapLedgerReason.BuyLum: return "buy_lum";
                case CapLedgerReason.BuyXp: return "buy_xp";
                case CapLedgerReason.GrantAdmin: return "grant_admin";
                case CapLedgerReason.VoucherBuy: return "voucher_buy";
                case CapLedgerReason.VoucherApply: return "voucher_apply";
                case CapLedgerReason.VoucherRefund: return "voucher_refund";
                case CapLedgerReason.VoucherRefundRetired: return "voucher_refund_retired";
                case CapLedgerReason.VendorDebit: return "vendor_debit";
                case CapLedgerReason.SweepRetired: return "sweep_retired";
                case CapLedgerReason.FacetSwitch: return "facet_switch";
                case CapLedgerReason.AdminCorrect: return "admin_correct";
                case CapLedgerReason.GrantItem: return "grant_item";
                default:
                    throw new ArgumentOutOfRangeException(nameof(reason), reason, "no reason code mapped for this CapLedgerReason member");
            }
        }

        /// <summary>
        /// The amount of a character's lifetime CAP earnings that the three tracked counters cannot
        /// account for: totalEarned - available - ownedCost - sinkSpend. Zero means the character's
        /// ledger is balanced; nonzero flags a shortfall (positive) or an overcredit (negative) an
        /// incident sweep should investigate.
        /// </summary>
        public static int Unexplained(int totalEarned, int available, int ownedCost, int sinkSpend)
        {
            return totalEarned - available - ownedCost - sinkSpend;
        }

        /// <summary>
        /// True for exactly the reasons that debit Available WITHOUT touching Total: buying a
        /// prepaid training voucher (<see cref="CapLedgerReason.VoucherBuy"/>) and a vendor
        /// alternate-currency debit (<see cref="CapLedgerReason.VendorDebit"/>). Both of those
        /// spends' refund counterparts (VoucherRefund, VoucherRefundRetired) credit Available back
        /// on the same character, so they are already netted out by summing delta_Available over
        /// the ledger - they do not need their own entry here. Every other reason either changes
        /// Total alongside Available (grants) or is priced by OwnedCost (Learn/Unlearn/Respec/
        /// SweepRetired), so it is not a "sink" in this sense.
        /// </summary>
        public static bool IsSinkReason(CapLedgerReason reason)
        {
            return reason == CapLedgerReason.VoucherBuy || reason == CapLedgerReason.VendorDebit;
        }

        /// <summary>
        /// The `character_cap_ledger.reason` codes (<see cref="ToCode"/>) for exactly the reasons
        /// <see cref="IsSinkReason"/> marks as a sink. Derived from IsSinkReason rather than
        /// hardcoded, so the two can never drift - a ledger row only ever carries the string code,
        /// never the enum, so a reader (round 3's login audit) needs this string-keyed form of the
        /// same rule.
        /// </summary>
        private static readonly HashSet<string> SinkReasonCodes = new HashSet<string>(
            Enum.GetValues(typeof(CapLedgerReason)).Cast<CapLedgerReason>().Where(IsSinkReason).Select(ToCode));

        /// <summary>String-code form of <see cref="IsSinkReason"/>, for a ledger row read back from the database.</summary>
        public static bool IsSinkReasonCode(string reasonCode)
        {
            return reasonCode != null && SinkReasonCodes.Contains(reasonCode);
        }

        /// <summary>
        /// Signed spend total (positive = spent) for exactly the sink reasons (<see cref="IsSinkReasonCode"/>)
        /// found in a character's ledger rows, in the sign convention <see cref="Unexplained"/> expects: a
        /// sink debits delta_Available (a negative number in the row), so this NEGATES the summed deltas
        /// to produce a positive spend figure that Unexplained then subtracts back out.
        /// </summary>
        public static int SinkSpend(IEnumerable<CharacterCapLedger> ledgerRows)
        {
            return -ledgerRows.Where(r => IsSinkReasonCode(r.Reason)).Sum(r => r.DeltaAvailable);
        }

        /// <summary>
        /// Counts abilities whose newest ledger row carrying a non-null <see cref="CharacterCapLedger.RankAfter"/>
        /// disagrees with the ability's live rank. This is the sharp signal for the standing hypothesis
        /// that a learn's point debit can persist while its rank write does not - kept pure and
        /// Player-free so it is unit-testable without a live Player.
        ///
        /// <paramref name="liveRanksByAbility"/> is a plain snapshot dictionary, DELIBERATELY not a
        /// callback/delegate: this method's own caller (Player_CapAudit.cs's login audit) runs from a
        /// database worker-thread callback, and a live lookup delegate (ClassAbilityRegistry.TryGetByName
        /// + Player.GetClassAbilityRank) would read the world thread's unsynchronized rank cache from
        /// that worker thread - a real data race, found in code review, against the indexer write in
        /// ApplyClassAbilityRankCore and the .Remove in UnlearnClassAbility. A dictionary snapshot taken
        /// on the world thread before the queued read is the only safe shape. An ability missing from
        /// the snapshot is treated as rank 0, matching Player.GetClassAbilityRank's own not-found default.
        ///
        /// "Newest" is by <see cref="CharacterCapLedger.Id"/> (the append order), not by scan order or
        /// timestamp, so the result does not depend on how the caller ordered ledgerRows.
        /// </summary>
        public static int CountRankDivergences(IEnumerable<CharacterCapLedger> ledgerRows, IReadOnlyDictionary<string, int> liveRanksByAbility)
        {
            var newestRankAfterByAbility = new Dictionary<string, (uint Id, int RankAfter)>();

            foreach (var row in ledgerRows)
            {
                if (row.Ability == null || row.RankAfter == null)
                    continue;

                if (!newestRankAfterByAbility.TryGetValue(row.Ability, out var existing) || row.Id > existing.Id)
                    newestRankAfterByAbility[row.Ability] = (row.Id, row.RankAfter.Value);
            }

            var divergences = 0;

            foreach (var kvp in newestRankAfterByAbility)
            {
                var liveRank = liveRanksByAbility.TryGetValue(kvp.Key, out var rank) ? rank : 0;

                if (liveRank != kvp.Value.RankAfter)
                    divergences++;
            }

            return divergences;
        }

        /// <summary>
        /// The FirstDetectedAt value round 3's login audit passes on each `character_cap_audit` upsert:
        /// the current time when out of balance, or null when balanced. Extracted as pure static so the
        /// balanced/unbalanced ternary is unit-testable on its own, separate from the SQL that decides
        /// whether that value actually sticks (<see cref="CoalesceFirstDetectedAt"/>).
        /// </summary>
        public static DateTime? FirstDetectedAtToWrite(bool isBalanced, DateTime now)
        {
            return isBalanced ? (DateTime?)null : now;
        }

        /// <summary>
        /// Mirrors the audit upsert's SQL exactly: `COALESCE(first_Detected_At, VALUES(first_Detected_At))`
        /// - keep the existing value once one is set, otherwise take the incoming one. This lets the
        /// "first-seen timestamp survives a later upsert" behavior be unit-tested in ACE.Server.Tests,
        /// which has no database: chaining <see cref="FirstDetectedAtToWrite"/> and this method across a
        /// simulated two-upsert sequence proves the C#-side write logic is correct, but it exercises a
        /// C# mirror of the formula, not the real SQL against MySQL - it cannot prove the DAO/migration
        /// actually behaves this way in production. That needs a live check (see the CAP audit ledger's
        /// VERIFY-QUEUE entries).
        /// </summary>
        public static DateTime? CoalesceFirstDetectedAt(DateTime? existing, DateTime? incoming)
        {
            return existing ?? incoming;
        }
    }
}
