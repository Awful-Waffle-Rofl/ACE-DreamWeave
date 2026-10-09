using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Server.Entity.CapacityUpgrades;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Account-wide capacity upgrade counts (/mule upgrade, /market upgrade) and the purchase path,
    /// with an in-memory cache in front of the account_capacity_upgrade table. Modelled on
    /// <see cref="AccountBankManager"/>.
    ///
    /// THE CACHE IS NOT THE AUTHORITY. A purchase hands the count the price was quoted against to the
    /// DAO's guarded increment, which refuses it (PriceChanged) if the database disagrees. The cache
    /// only decides what price is QUOTED, never what is charged for which count.
    ///
    /// A FAILED READ IS NEVER CACHED. <see cref="GetCount"/> returns 0 after a failed load, which is the
    /// right answer for a caller adding a capacity bonus (the player briefly sees base capacity, and the
    /// next call retries) and the WRONG answer for a caller quoting a price (it would quote the n = 0
    /// price). The purchase flow must use <see cref="TryGetCount"/> and refuse on FALSE.
    ///
    /// LOCK ORDER: <see cref="TryPurchase"/> holds this account's upgrade gate and, inside it, the
    /// account's bank gate (via <see cref="AccountBankManager.TryAdjustVia"/>). Never the other way
    /// round: nothing that holds a bank gate may call into this class.
    /// </summary>
    public static class AccountCapacityUpgradeManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// One cache entry per account. <see cref="Loaded"/> is the whole contract: FALSE means
        /// <see cref="Counts"/> is meaningless, which is how a failed read stays distinguishable from an
        /// account that owns nothing.
        /// </summary>
        private sealed class Entry
        {
            /// <summary>
            /// Serializes loads, purchases and cache writes for this account. A purchase holds it across
            /// the database transaction, which is what makes two purchases on one account quote and pay
            /// in sequence rather than both against the same count.
            /// </summary>
            public readonly object Gate = new object();

            /// <summary>
            /// COPY-ON-WRITE: never mutated after assignment, only replaced under <see cref="Gate"/>, so
            /// the lock-free read in TryGetCount never observes a dictionary mid-write.
            /// </summary>
            public volatile Dictionary<CapacityUpgradeKind, int> Counts = new Dictionary<CapacityUpgradeKind, int>();

            public volatile bool Loaded;
        }

        private static readonly ConcurrentDictionary<uint, Entry> entries = new ConcurrentDictionary<uint, Entry>();

        private static ICapacityUpgradeStorage storage = new ShardCapacityUpgradeStorage();

        /// <summary>Test seam for the per-attempt purchase token. Must produce 32 lower-case hex chars.</summary>
        internal static Func<string> TokenSource = NewToken;

        private static string NewToken() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// Swaps the storage, restores the token source and empties the cache. TEST SEAM ONLY. Pass null
        /// to restore the production storage.
        /// </summary>
        internal static void ResetForTesting(ICapacityUpgradeStorage testStorage)
        {
            storage = testStorage ?? new ShardCapacityUpgradeStorage();
            TokenSource = NewToken;
            entries.Clear();
        }

        private static Entry GetEntry(uint accountId) => entries.GetOrAdd(accountId, _ => new Entry());

        /// <summary>
        /// How many upgrades of <paramref name="kind"/> the account owns. RETURNS 0 WHEN THE COUNT IS
        /// UNAVAILABLE (logged, not cached) - fit for adding a capacity bonus, NOT for quoting a price.
        /// </summary>
        public static int GetCount(uint accountId, CapacityUpgradeKind kind)
        {
            TryGetCount(accountId, kind, out var count);

            return count;
        }

        /// <summary>
        /// How many upgrades of <paramref name="kind"/> the account owns, with availability answered
        /// separately. FALSE means the count could not be established and <paramref name="count"/> is 0
        /// as a placeholder, not a reading. A purchase must refuse on FALSE.
        /// </summary>
        public static bool TryGetCount(uint accountId, CapacityUpgradeKind kind, out int count)
        {
            var entry = GetEntry(accountId);

            // Lock-free fast path, the same shape as AccountBankManager.TryGetBalance: a loaded entry is
            // served without waiting on a purchase that holds the gate across its transaction.
            if (entry.Loaded)
            {
                count = entry.Counts.TryGetValue(kind, out var cached) ? cached : 0;
                return true;
            }

            lock (entry.Gate)
            {
                if (!entry.Loaded)
                {
                    (bool Ok, Dictionary<CapacityUpgradeKind, int> Counts) read;

                    try
                    {
                        read = storage.GetAccountCapacityUpgrades(accountId);
                    }
                    catch (Exception ex)
                    {
                        log.Error($"[UPGRADE] count load for account {accountId} threw: {ex.GetFullMessage()}");
                        read = (false, null);
                    }

                    if (!read.Ok || read.Counts == null)
                    {
                        // NOT CACHED, and the entry stays unloaded so the next call retries.
                        log.Error($"[UPGRADE] could not read the capacity upgrade counts for account {accountId}. Reporting them as unavailable; the next read retries.");

                        count = 0;
                        return false;
                    }

                    entry.Counts = new Dictionary<CapacityUpgradeKind, int>(read.Counts);
                    entry.Loaded = true;
                }

                count = entry.Counts.TryGetValue(kind, out var owned) ? owned : 0;
                return true;
            }
        }

        /// <summary>
        /// Buys one upgrade of <paramref name="kind"/> for the account, paying
        /// <paramref name="costPyreals"/> from the BANKED pyreal pool, atomically with the count moving
        /// from <paramref name="expectedCount"/> to expectedCount + 1. The price must have been quoted
        /// (CapacityUpgradePricing.TryQuote) against <paramref name="expectedCount"/>, which must have
        /// come from <see cref="TryGetCount"/>.
        ///
        /// Runs through <see cref="AccountBankManager.TryAdjustVia"/>, so the bank cache holds the
        /// post-debit balance after an Applied purchase and is dropped after every other outcome.
        ///
        /// Cache handling per outcome:
        ///   - Applied            the cached count for this kind becomes expectedCount + 1.
        ///   - InsufficientFunds  counts untouched (the bank cache is dropped by the bank rule).
        ///   - PriceChanged       this account's counts are dropped, so the next quote re-reads.
        ///   - InvalidRequest     nothing ran; nothing is touched.
        ///   - Unknown            this account's counts AND its bank balance are dropped, and an
        ///                        UPGRADE STATE UNKNOWN line is logged. Never tell the player it failed.
        /// </summary>
        public static CapacityUpgradePurchaseResult TryPurchase(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, uint characterGuid)
        {
            return TryPurchase(accountId, kind, expectedCount, costMmd, costPyreals, characterGuid, out _);
        }

        /// <summary>
        /// <see cref="TryPurchase(uint, CapacityUpgradeKind, int, long, long, uint)"/>, also reporting
        /// the post-debit bank balance. <paramref name="newBalance"/> is meaningful only for Applied.
        /// </summary>
        public static CapacityUpgradePurchaseResult TryPurchase(uint accountId, CapacityUpgradeKind kind, int expectedCount, long costMmd, long costPyreals, uint characterGuid, out long newBalance)
        {
            newBalance = 0;

            var token = (TokenSource ?? NewToken)();

            // Checked here as well as in the DAO so a malformed call never reaches the bank gate, where
            // it would be mapped to a refusal and needlessly drop the cached balance.
            if (!ShardDatabase.IsValidCapacityUpgradePurchase(accountId, kind, expectedCount, costMmd, costPyreals, token))
            {
                log.Error($"[UPGRADE] TryPurchase refused a malformed request: account {accountId}, kind {(byte)kind}, expected {expectedCount}, cost {costMmd} MMD / {costPyreals} pyreals.");
                return CapacityUpgradePurchaseResult.InvalidRequest;
            }

            var entry = GetEntry(accountId);

            lock (entry.Gate)
            {
                // Unknown until the storage answers: if it throws, TryAdjustVia catches it and this is
                // what the caller sees, which is the only honest value for an escaped exception.
                var purchase = CapacityUpgradePurchaseResult.Unknown;

                AccountBankManager.TryAdjustVia(accountId, (out long balance) =>
                {
                    purchase = storage.TryPurchaseCapacityUpgrade(accountId, kind, expectedCount, costMmd, costPyreals, token, characterGuid, out balance);

                    return ToBankResult(purchase);
                },
                $"capacity upgrade purchase for account {accountId}, kind {kind}, expected {expectedCount}, cost {costPyreals} pyreals, token {token}",
                out var bankBalance);

                switch (purchase)
                {
                    case CapacityUpgradePurchaseResult.Applied:
                        newBalance = bankBalance;

                        // Only a loaded entry is updated. An unloaded one has no other kinds to vouch
                        // for, and its next load reads this commit from the database anyway.
                        if (entry.Loaded)
                        {
                            var updated = new Dictionary<CapacityUpgradeKind, int>(entry.Counts);
                            updated[kind] = expectedCount + 1;
                            entry.Counts = updated;
                        }
                        break;

                    case CapacityUpgradePurchaseResult.PriceChanged:
                        entry.Loaded = false;
                        break;

                    case CapacityUpgradePurchaseResult.Unknown:
                        entry.Loaded = false;
                        AccountBankManager.Invalidate(accountId);

                        log.Error($"UPGRADE STATE UNKNOWN: account {accountId}, kind {kind}, expected count {expectedCount}, cost {costMmd} MMD / {costPyreals} pyreals, token {token}. Whether the debit and the increment committed is unknown; check account_capacity_upgrade_purchase for this token.");
                        break;
                }

                return purchase;
            }
        }

        /// <summary>
        /// The bank's view of a purchase outcome, which drives TryAdjustVia's cache rule. Every refusal
        /// rolls the debit back in the same transaction, so the pyreals provably did not move.
        /// </summary>
        internal static AccountBankAdjustResult ToBankResult(CapacityUpgradePurchaseResult purchase)
        {
            switch (purchase)
            {
                case CapacityUpgradePurchaseResult.Applied:
                    return AccountBankAdjustResult.Applied;

                case CapacityUpgradePurchaseResult.InsufficientFunds:
                case CapacityUpgradePurchaseResult.PriceChanged:
                case CapacityUpgradePurchaseResult.InvalidRequest:
                    return AccountBankAdjustResult.Refused;

                default:
                    return AccountBankAdjustResult.Failed;
            }
        }

        /// <summary>
        /// Drops this account's cached counts so the next read reloads them. Cheap and always safe: the
        /// cache is never the authority.
        /// </summary>
        public static void Invalidate(uint accountId)
        {
            if (entries.TryGetValue(accountId, out var entry))
            {
                lock (entry.Gate)
                {
                    entry.Loaded = false;
                }
            }
        }
    }
}
