using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Server.Entity.AccountBank;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The account-wide banked pyreal pool, and the in-memory cache in front of it.
    ///
    /// WHY THERE IS A CACHE AT ALL. Player.BankedPyreals is a READ on a very hot path:
    /// UpdateCoinValue -> SendSpendableCoinValue -> GetSpendableCoinValue -> BankedPyreals fires from
    /// roughly twenty sites in Player_Inventory.cs alone, so every inventory move would otherwise cost
    /// a database round trip on the world thread. Writes are rare by comparison and always go to the
    /// database first.
    ///
    /// WHY THE CACHE IS NOT THE AUTHORITY. Every mutation goes through <see cref="TryAdjust"/>, which
    /// hands the delta to the row's own guarded UPDATE and takes the answer from there. The cache is
    /// only ever a copy of what the database last said. That is what makes the brief window in which
    /// two characters on one account can both be live - a logging-out player and a newly entering one,
    /// because the in-world guard in CharacterHandler is per-CHARACTER while the login guard is
    /// per-account - harmless rather than a lost update.
    ///
    /// A FAILED READ IS NEVER CACHED. <see cref="GetBalance"/> returning 0 after a failed load means
    /// "unavailable", not "empty", and the entry is left unloaded so the next call retries. Callers
    /// that must be able to tell the two apart use <see cref="TryGetBalance"/>; callers that only
    /// display or pre-check use <see cref="GetBalance"/>, because the authoritative check is the WHERE
    /// guard on the mutation that follows, not the number shown.
    ///
    /// THE PER-ACCOUNT GATE replaces Player_Bank's bankBalanceLock for pyreals. It does NOT make a
    /// read-modify-write safe - nothing here does one - it serializes the load and the cache write so
    /// that two threads on one account cannot interleave a stale load over a fresh delta.
    ///
    /// THE BULK SNAPSHOT (<see cref="GetAllBalances"/>) IS A SECOND, SEPARATE CACHE and is not part of
    /// any of the above. It serves ranking and telemetry - /top bank and the analytics flush - never a
    /// spend, and it is deliberately not wired to the per-account cache in either direction. See its own
    /// doc for why.
    /// </summary>
    public static class AccountBankManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// One cache entry per account. <see cref="Loaded"/> is the whole contract: FALSE means
        /// <see cref="Balance"/> is meaningless and must not be shown or trusted, which is how a failed
        /// read stays distinguishable from a genuine zero.
        /// </summary>
        private sealed class Entry
        {
            /// <summary>Serializes loads and cache writes for this account. Never held across a player action.</summary>
            public readonly object Gate = new object();

            public long Balance;

            public bool Loaded;
        }

        private static readonly ConcurrentDictionary<uint, Entry> entries = new ConcurrentDictionary<uint, Entry>();

        /// <summary>
        /// The production backend. Stateless - every member forwards to
        /// DatabaseManager.Shard.BaseDatabase - so one instance for the process is enough.
        /// </summary>
        private static IAccountBankBackend backend = new ShardAccountBankBackend();

        /// <summary>
        /// Swaps the backend and empties the cache. TEST SEAM ONLY.
        ///
        /// The manager is process-wide static state, which is what the hot read path needs and what
        /// makes it untestable by construction unless there is a way in. Everything else about the
        /// class is deliberately shaped so that this is the only mutable global: the backend is read
        /// through this one field, and the cache is emptied here so a test can never inherit another
        /// test's balances.
        /// </summary>
        internal static void ResetForTesting(IAccountBankBackend testBackend)
        {
            backend = testBackend ?? new ShardAccountBankBackend();
            entries.Clear();

            // The bulk snapshot is separate global state and has to be cleared here too, or a test
            // inherits the previous test's balances through a cache the entries.Clear() above does not
            // reach - which would fail in exactly the direction that looks like a pass.
            lock (allBalancesGate)
            {
                allBalances = null;
                allBalancesReadUtc = DateTime.MinValue;
            }
        }

        private static Entry GetEntry(uint accountId) => entries.GetOrAdd(accountId, _ => new Entry());

        /// <summary>
        /// Ages the bulk snapshot out without discarding it, so the next <see cref="GetAllBalances"/>
        /// call re-reads. TEST SEAM ONLY.
        ///
        /// It exists because the one property worth pinning about a failed bulk read - that it keeps
        /// serving the snapshot already in hand instead of presenting as empty - is unreachable
        /// otherwise: a test cannot advance the clock, and <see cref="ResetForTesting"/> drops the
        /// snapshot it needs to still be there. Without this the test passes by never consulting the
        /// broken backend at all, which is a test that cannot fail.
        /// </summary>
        internal static void ExpireBulkSnapshotForTesting()
        {
            lock (allBalancesGate)
                allBalancesReadUtc = DateTime.MinValue;
        }

        #region Bulk snapshot (ranking and telemetry only)

        /// <summary>
        /// Guards the bulk snapshot. SEPARATE from the per-account <see cref="Entry.Gate"/>s on purpose,
        /// and unlike those it IS held across a database read - which is safe precisely because it is
        /// separate: a slow bulk read can delay another ranking caller, and can never delay or block the
        /// balance a player is spending. Holding it also collapses a burst of simultaneous callers into
        /// one query instead of a thundering herd.
        /// </summary>
        private static readonly object allBalancesGate = new object();

        private static IReadOnlyDictionary<uint, long> allBalances;

        private static DateTime allBalancesReadUtc = DateTime.MinValue;

        /// <summary>
        /// How long one bulk snapshot is served before the next call re-reads. Short enough that a
        /// leaderboard is not visibly stale, long enough that a room full of players spamming the
        /// command costs one query rather than one each.
        /// </summary>
        private static readonly TimeSpan AllBalancesTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Every account's pooled balance, as account id to balance, from a snapshot at most
        /// <see cref="AllBalancesTtl"/> old. NULL means no snapshot is available - the read failed and
        /// this process has never held one.
        ///
        /// FOR RANKING AND TELEMETRY ONLY, never for a spend, a refusal or an affordability check. It is
        /// taken outside the per-account gate, so an entry can be stale the moment it is read; only
        /// TryAdjust's guarded UPDATE adjudicates. It deliberately does NOT feed or invalidate the
        /// per-account <see cref="entries"/> cache, and TryAdjust deliberately does not invalidate it:
        /// conflating the two would put a whole-table read in the path of the money players actually
        /// spend, which is the one path this class exists to keep cheap.
        ///
        /// A FAILED READ IS NEVER CACHED, the same rule the per-account load follows and for the same
        /// reason. On failure the previous snapshot is served if one is still in hand - a number up to a
        /// few minutes old beats showing an empty board - and the read timestamp is NOT advanced, so the
        /// very next call retries rather than waiting out a TTL it never earned. With no previous
        /// snapshot the answer is null, and callers must say "unavailable" rather than render zeros:
        /// a broken database and an empty leaderboard must not look the same.
        /// </summary>
        public static IReadOnlyDictionary<uint, long> GetAllBalances()
        {
            lock (allBalancesGate)
            {
                if (allBalances != null && DateTime.UtcNow - allBalancesReadUtc < AllBalancesTtl)
                    return allBalances;

                Dictionary<uint, long> read;

                try
                {
                    read = backend?.GetAllAccountBankBalances();
                }
                catch (Exception ex)
                {
                    // The DAO and the production backend both catch and return null; this is the belt to
                    // their braces, matching the per-account load.
                    log.Error($"[BANK] the bulk banked pyreal snapshot threw: {ex.GetFullMessage()}");
                    read = null;
                }

                if (read == null)
                {
                    log.Error($"[BANK] could not read the bulk banked pyreal snapshot. {(allBalances != null ? "Serving the previous snapshot" : "No snapshot is available")}; the next call retries.");

                    // Not cached, and the timestamp is left where it was, so this does not start a fresh
                    // TTL window over a failure.
                    return allBalances;
                }

                allBalances = read;
                allBalancesReadUtc = DateTime.UtcNow;

                return allBalances;
            }
        }

        #endregion

        /// <summary>
        /// This account's banked pyreals, loading them once per account and serving the cache after.
        ///
        /// RETURNS 0 WHEN THE BALANCE IS UNAVAILABLE, and that is a deliberate choice for the callers
        /// this method has: the /bank display, the client's spendable-coin figure, and the
        /// affordability pre-checks in Vendor and SpellTutor. All three want a number, and all three
        /// are followed by a mutation whose WHERE guard is the real adjudicator - so an unavailable
        /// balance shows as 0, refuses the purchase the player was about to make, and corrects itself
        /// on the next read. What it must NOT do is persist: a failed load leaves the entry unloaded,
        /// so nothing serves that zero twice without asking the database again.
        ///
        /// Any caller that has to distinguish "unavailable" from "empty" must use
        /// <see cref="TryGetBalance"/> instead.
        /// </summary>
        public static long GetBalance(uint accountId)
        {
            TryGetBalance(accountId, out var balance);

            return balance;
        }

        /// <summary>
        /// This account's banked pyreals, with the availability answered separately. FALSE means the
        /// balance could not be established and <paramref name="balance"/> is 0 as a placeholder, not
        /// as a reading.
        ///
        /// A previously loaded balance is served even after a later read fails, because a slightly
        /// stale number the database will re-adjudicate anyway beats telling a player their bank is
        /// gone. Only an account this process has never successfully read returns false.
        /// </summary>
        public static bool TryGetBalance(uint accountId, out long balance)
        {
            var entry = GetEntry(accountId);

            if (entry.Loaded)
            {
                balance = entry.Balance;
                return true;
            }

            lock (entry.Gate)
            {
                // Re-checked under the gate: two threads that missed the cache together must not both
                // issue the load, and the loser must see the winner's result rather than its own.
                if (entry.Loaded)
                {
                    balance = entry.Balance;
                    return true;
                }

                long? read;

                try
                {
                    read = backend?.GetAccountBankBalance(accountId);
                }
                catch (Exception ex)
                {
                    // The DAO and the production backend both catch and return null; this is the belt
                    // to their braces, because an exception escaping here would surface in the middle
                    // of an inventory move.
                    log.Error($"[BANK] balance load for account {accountId} threw: {ex.GetFullMessage()}");
                    read = null;
                }

                if (read == null)
                {
                    // NOT CACHED, and the entry stays unloaded. Writing 0 here is the phantom-empty-bank
                    // bug: it would survive for the life of the entry and be indistinguishable from an
                    // account that genuinely holds nothing.
                    log.Error($"[BANK] could not read the banked pyreal balance for account {accountId}. Reporting it as unavailable; the next read retries.");

                    balance = 0;
                    return false;
                }

                entry.Balance = read.Value;
                entry.Loaded = true;

                balance = entry.Balance;
                return true;
            }
        }

        /// <summary>
        /// Applies a signed delta to this account's pool. THE ONLY WAY BANKED PYREALS MOVE.
        ///
        /// Database first, always: the delta is handed straight to the guarded UPDATE and the cache is
        /// updated from its answer, never the other way round. The cache is not consulted at all, so a
        /// stale entry cannot authorise or block anything.
        ///
        /// Cache handling per outcome:
        ///   - Applied              the new balance is known; cache it.
        ///   - AppliedCountUnknown  the pyreals moved but the resulting balance is not known; drop the
        ///                          entry so the next read goes to the database.
        ///   - Failed               nobody knows whether the pyreals moved; drop the entry for the same
        ///                          reason, with more urgency.
        ///   - Refused              the balance provably did not move, so the cached number is still
        ///                          arithmetically valid - but a refusal reached here at all means the
        ///                          caller checked affordability against a cache that disagrees with
        ///                          the ledger, so the entry is dropped too. It is the one signal we
        ///                          get that the copy is wrong.
        ///
        /// <paramref name="newBalance"/> is meaningful ONLY for
        /// <see cref="AccountBankAdjustResult.Applied"/>.
        /// </summary>
        public static AccountBankAdjustResult TryAdjust(uint accountId, long delta, out long newBalance)
        {
            return TryAdjustVia(accountId, (out long balance) => backend.TryAdjustAccountBank(accountId, delta, out balance), $"TryAdjust for account {accountId}, delta {delta}", out newBalance);
        }

        /// <summary>
        /// One mutation that moves this account's banked pyreals, reported in the bank's own terms.
        /// <paramref name="newBalance"/> must be the post-mutation balance when the return is
        /// <see cref="AccountBankAdjustResult.Applied"/>, and is ignored otherwise.
        /// </summary>
        internal delegate AccountBankAdjustResult BankMutation(out long newBalance);

        /// <summary>
        /// Runs a mutation that moves banked pyreals under this account's gate and applies
        /// <see cref="TryAdjust"/>'s per-outcome cache rule to its answer. This is how a caller whose
        /// debit lives inside a LARGER transaction than a plain delta (a capacity upgrade purchase, which
        /// debits and increments a count in one commit) keeps the bank cache from holding a pre-debit
        /// balance. <see cref="TryAdjust"/> itself is this method with the plain guarded UPDATE.
        ///
        /// The mutation MUST report the bank's view of what happened: Applied with the new balance when
        /// the pyreals moved and the balance is known, AppliedCountUnknown when they moved and it is not,
        /// Refused when they provably did not move, and Failed when nobody knows. A caller with a richer
        /// outcome of its own captures it in a local inside the delegate.
        ///
        /// <paramref name="operation"/> names the call in the log line written if the mutation throws.
        ///
        /// LOCK ORDER: the account's bank gate is held while <paramref name="apply"/> runs. A caller
        /// holding another lock of its own (AccountCapacityUpgradeManager's per-account gate) must always
        /// take that lock BEFORE this one, and nothing may call back out of <paramref name="apply"/> into
        /// code that takes it.
        /// </summary>
        internal static AccountBankAdjustResult TryAdjustVia(uint accountId, BankMutation apply, string operation, out long newBalance)
        {
            newBalance = 0;

            var entry = GetEntry(accountId);

            lock (entry.Gate)
            {
                AccountBankAdjustResult result;

                try
                {
                    result = apply(out newBalance);
                }
                catch (Exception ex)
                {
                    // An escaped exception cannot be reported as anything but "unknown": the statement
                    // may have committed before the throw. Never Refused, which would tell the caller
                    // the balance provably did not move.
                    log.Error($"[BANK] {operation} threw: {ex.GetFullMessage()}. Whether it committed is UNKNOWN.");

                    entry.Loaded = false;
                    newBalance = 0;

                    return AccountBankAdjustResult.Failed;
                }

                if (result == AccountBankAdjustResult.Applied)
                {
                    entry.Balance = newBalance;
                    entry.Loaded = true;
                }
                else
                {
                    entry.Loaded = false;
                }

                return result;
            }
        }

        /// <summary>
        /// Drops this account's cached balance without dropping the entry, so the next read reloads it.
        /// Cheap and always safe: the cache is never the authority.
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

        /// <summary>
        /// Forgets this account entirely, entry and gate included. Called at logout, and ONLY when no
        /// other character on the account is still online - otherwise the survivor would keep reading
        /// through an entry nobody owns while a new one is created beside it.
        ///
        /// If a thread is mid-<see cref="TryAdjust"/> on the removed entry when this runs, its cache
        /// write lands on an orphan and is simply discarded. That is not a correctness problem for the
        /// same reason the whole cache is not: the delta itself was adjudicated by the database, and
        /// the next reader loads from there.
        /// </summary>
        public static void Release(uint accountId)
        {
            entries.TryRemove(accountId, out _);
        }

        /// <summary>
        /// Claims the one-and-only fold of a character's legacy per-character banked pyreals into its
        /// account's pool. TRUE only when this call inserted the row, and only that caller may credit.
        ///
        /// FALSE does not mean "already folded" - see <see cref="HasFold"/>.
        /// </summary>
        public static bool TryClaimFold(uint characterGuid, uint accountId, long amount)
        {
            try
            {
                return backend.TryClaimAccountBankFold(characterGuid, accountId, amount);
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] TryClaimFold for character 0x{characterGuid:X8}, account {accountId}, amount {amount} threw: {ex.GetFullMessage()}");
                return false;
            }
        }

        /// <summary>
        /// Whether this character has already been folded. NULL means the question could not be
        /// answered, which is NOT "no" - the caller must leave the legacy property in place and let a
        /// later login retry, because removing it on a guess destroys a balance nothing has credited.
        /// </summary>
        public static bool? HasFold(uint characterGuid)
        {
            try
            {
                return backend.HasAccountBankFold(characterGuid);
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] HasFold for character 0x{characterGuid:X8} threw: {ex.GetFullMessage()}");
                return null;
            }
        }
    }
}
