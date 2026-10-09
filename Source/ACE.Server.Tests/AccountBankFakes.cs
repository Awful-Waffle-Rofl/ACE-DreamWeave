using System.Collections.Generic;

using ACE.Database;
using ACE.Server.Entity.AccountBank;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Recording in-memory stand-in for the two account_bank* tables, mirroring FakeVaultBackend's
    /// conventions (see AccountVaultFakes.cs).
    ///
    /// Models the real DAO contract from Docs/AccountBank/DESIGN.md section 2:
    /// - TryAdjustAccountBank applies a signed delta with a guarded UPDATE: the balance may never go
    ///   negative and may never exceed long.MaxValue, computed without overflowing the check itself.
    ///   A debit against a missing account row is refused and creates no row; a credit against a
    ///   missing row creates it (ensure-row-on-credit, matching the real DAO's
    ///   "INSERT ... ON DUPLICATE KEY UPDATE only when the UPDATE matched nothing AND delta > 0").
    ///   A ZERO delta is neither: it issues no statement, reports Applied with the current balance,
    ///   and creates no row.
    /// - TryClaimAccountBankFold is a single claim per character: true only the first time.
    /// - GetAccountBankBalance returns null on a forced read failure, never a phantom zero.
    /// </summary>
    internal class FakeAccountBankBackend : IAccountBankBackend
    {
        /// <summary>account id -> balance. Absence means no row, i.e. balance 0 with nothing to credit against on a debit.</summary>
        public readonly Dictionary<uint, long> Balances = new Dictionary<uint, long>();

        /// <summary>character guid -> account id, amount. One entry per successful claim.</summary>
        public readonly Dictionary<uint, (uint AccountId, long Amount)> Folds = new Dictionary<uint, (uint, long)>();

        /// <summary>When set, every GetAccountBankBalance call returns null (a failed read) instead of the real value.</summary>
        public bool FailBalanceRead;

        /// <summary>When set, every GetAllAccountBankBalances call returns null (a failed read) instead of a snapshot. Separate from FailBalanceRead so a test can break the bulk read without breaking the per-account one.</summary>
        public bool FailAllBalancesRead;

        /// <summary>When set, every TryAdjustAccountBank call returns Failed without touching the ledger.</summary>
        public bool FailAdjust;

        /// <summary>When set, the next successful TryAdjustAccountBank call reports AppliedCountUnknown instead of Applied - the ledger still moves, only the read-back is lost.</summary>
        public bool NextAdjustCountUnknown;

        public int BalanceReadCalls;
        public int AllBalanceReadCalls;
        public int AdjustCalls;
        public int ClaimCalls;

        public long? GetAccountBankBalance(uint accountId)
        {
            BalanceReadCalls++;

            if (FailBalanceRead)
                return null;

            return Balances.TryGetValue(accountId, out var balance) ? balance : 0;
        }

        /// <summary>
        /// The bulk read. Returns a COPY, matching the real DAO: a caller holding the result must not
        /// see later ledger movement appear in a snapshot it already took. Null on a forced failure,
        /// never an empty dictionary - the whole null-versus-empty contract turns on that.
        /// </summary>
        public Dictionary<uint, long> GetAllAccountBankBalances()
        {
            AllBalanceReadCalls++;

            if (FailAllBalancesRead)
                return null;

            return new Dictionary<uint, long>(Balances);
        }

        public AccountBankAdjustResult TryAdjustAccountBank(uint accountId, long delta, out long newBalance)
        {
            newBalance = 0;
            AdjustCalls++;

            if (FailAdjust)
                return AccountBankAdjustResult.Failed;

            // A ZERO DELTA IS A PURE READ, and it has to be handled before the missing-row branch or
            // the fake refuses what the real thing allows. ShardDatabase_AccountBank.TryAdjustAccountBank
            // issues no statement at all for delta == 0 - it falls straight through to the read-back -
            // so it reports Applied with the current balance, and creates NO row when the account has
            // none (a no-op has no business establishing that an account has a pool).
            //
            // It deliberately does not consume NextAdjustCountUnknown either. AppliedCountUnknown means
            // "the delta committed but the read-back was lost", and nothing commits on a zero delta -
            // the real DAO reports a lost read-back there as Failed, which is what FailAdjust covers.
            if (delta == 0)
            {
                newBalance = Balances.TryGetValue(accountId, out var existing) ? existing : 0;
                return AccountBankAdjustResult.Applied;
            }

            var hasRow = Balances.TryGetValue(accountId, out var current);

            if (!hasRow)
            {
                // No row: a debit (delta < 0) is refused outright, matching "the account has no pool
                // row and the delta was not a credit". A credit (delta > 0) creates the row below.
                if (delta < 0)
                    return AccountBankAdjustResult.Refused;

                current = 0;
            }

            // Guarded WHERE, written the overflow-safe way per DESIGN section 2: never balance + delta,
            // which can overflow the check itself.
            if (delta < 0 && current + delta < 0)
                return AccountBankAdjustResult.Refused;

            if (delta > 0 && current > long.MaxValue - delta)
                return AccountBankAdjustResult.Refused;

            var updated = current + delta;
            Balances[accountId] = updated;

            if (NextAdjustCountUnknown)
            {
                NextAdjustCountUnknown = false;
                return AccountBankAdjustResult.AppliedCountUnknown;
            }

            newBalance = updated;
            return AccountBankAdjustResult.Applied;
        }

        public bool TryClaimAccountBankFold(uint characterGuid, uint accountId, long amount)
        {
            ClaimCalls++;

            if (Folds.ContainsKey(characterGuid))
                return false;

            Folds[characterGuid] = (accountId, amount);
            return true;
        }

        public bool? HasAccountBankFold(uint characterGuid)
        {
            return Folds.ContainsKey(characterGuid);
        }
    }
}
