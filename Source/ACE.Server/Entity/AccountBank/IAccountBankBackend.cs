using System;
using System.Collections.Generic;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;

namespace ACE.Server.Entity.AccountBank
{
    /// <summary>
    /// The manager's view of the two account_bank* tables. One method per DAO method, same signatures,
    /// so <see cref="ShardAccountBankBackend"/> is a thin delegation and the tests can drive a
    /// recording fake with no MySQL instance anywhere.
    ///
    /// NULL FROM A READ MEANS THE READ FAILED. It never means "this account has no pyreals", which is a
    /// plain 0. The two must never be conflated, and this is the sharpest edge in the whole feature: a
    /// failure read as zero would show a player an empty bank, and - if it were then cached - would
    /// leave the process serving that zero for as long as the entry lived. Nothing caches a null.
    ///
    /// Note that no member here mentions the EF entity type ACE.Database.Models.Shard.AccountBank.
    /// That is convenient rather than accidental: inside the namespace ACE.Server.Entity.AccountBank
    /// the enclosing namespace's own name wins, and naming the entity unqualified would be reported as
    /// "a namespace but is used like a type" (the vault seam next door carries a using-alias for
    /// exactly that reason). The manager works in balances, not rows, so the question does not arise.
    /// </summary>
    public interface IAccountBankBackend
    {
        /// <summary>
        /// One account's banked pyreal balance. NULL means the read FAILED; 0 means the account has no
        /// pool row yet, which is the ordinary state of an account that has never banked anything.
        /// </summary>
        long? GetAccountBankBalance(uint accountId);

        /// <summary>
        /// Every account's balance in one read, as account id to balance. NULL means the read FAILED;
        /// an EMPTY dictionary means the read succeeded over a table in which no account has banked yet,
        /// and an account absent from a non-null result has no pool row, which is balance 0.
        ///
        /// FOR RANKING AND TELEMETRY ONLY. It is taken outside the per-account gate, so it is a snapshot
        /// and never an authority: nothing may spend, refuse or adjudicate against it.
        /// </summary>
        Dictionary<uint, long> GetAllAccountBankBalances();

        /// <summary>
        /// Applies a signed delta to one account's pool, creating the row if absent and the delta is a
        /// credit. See <see cref="AccountBankAdjustResult"/> for what each of the four values obliges a
        /// caller to do; the short form is that Applied and AppliedCountUnknown both mean the pyreals
        /// moved, Refused means they provably did not, and Failed means nobody knows.
        ///
        /// <paramref name="newBalance"/> is meaningful ONLY for
        /// <see cref="AccountBankAdjustResult.Applied"/>. It is 0 for every other value, and 0 is not a
        /// balance there.
        /// </summary>
        AccountBankAdjustResult TryAdjustAccountBank(uint accountId, long delta, out long newBalance);

        /// <summary>
        /// Claims the one-and-only fold of a character's legacy per-character balance into its
        /// account's pool. TRUE only when this call inserted the row.
        ///
        /// FALSE covers both a duplicate key and a database failure, which demand opposite responses -
        /// see <see cref="HasAccountBankFold"/>.
        /// </summary>
        bool TryClaimAccountBankFold(uint characterGuid, uint accountId, long amount);

        /// <summary>Whether a fold row already exists for this character. NULL means the read FAILED.</summary>
        bool? HasAccountBankFold(uint characterGuid);
    }

    /// <summary>
    /// Production backend: a straight delegation to the bank DAO on
    /// <see cref="DatabaseManager.Shard"/>'s base <see cref="ShardDatabase"/>.
    ///
    /// These are ledger operations, not biota operations, so they deliberately do NOT go through
    /// SerializedShardDatabase's worker thread - see the header on
    /// ACE.Database/ShardDatabase_AccountBank.cs. Serialization for one account comes from
    /// <see cref="ACE.Server.Managers.AccountBankManager"/>'s per-account gate instead, and the row's
    /// own WHERE guard is what actually adjudicates.
    ///
    /// The reads are wrapped so that a database failure surfaces as null rather than as an exception
    /// escaping into a player's command handler. That guarantee belongs to the contract on
    /// <see cref="IAccountBankBackend"/>, so it is asserted here rather than assumed of the DAO: if the
    /// DAO already catches and returns null the wrapper never fires, and if it ever stops doing so the
    /// contract still holds.
    /// </summary>
    public class ShardAccountBankBackend : IAccountBankBackend
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static ShardDatabase Db => DatabaseManager.Shard.BaseDatabase;

        public long? GetAccountBankBalance(uint accountId)
        {
            try
            {
                return Db.GetAccountBankBalance(accountId);
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] GetAccountBankBalance({accountId}) failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        public Dictionary<uint, long> GetAllAccountBankBalances()
        {
            try
            {
                return Db.GetAllAccountBankBalances();
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] GetAllAccountBankBalances() failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        public AccountBankAdjustResult TryAdjustAccountBank(uint accountId, long delta, out long newBalance)
        {
            // Deliberately NOT wrapped the way the reads are. The DAO already classifies its own
            // failures into the four outcomes, and a wrapper here could only turn an escaped exception
            // into one flat value - which would have to be Failed, and would then be indistinguishable
            // from an UPDATE that threw. The DAO catches everything it can reach; if anything ever does
            // escape it must surface rather than be silently collapsed into "unknown".
            return Db.TryAdjustAccountBank(accountId, delta, out newBalance);
        }

        public bool TryClaimAccountBankFold(uint characterGuid, uint accountId, long amount)
        {
            return Db.TryClaimAccountBankFold(characterGuid, accountId, amount);
        }

        public bool? HasAccountBankFold(uint characterGuid)
        {
            try
            {
                return Db.HasAccountBankFold(characterGuid);
            }
            catch (Exception ex)
            {
                log.Error($"[BANK] HasAccountBankFold(0x{characterGuid:X8}) failed: {ex.GetFullMessage()}");
                return null;
            }
        }
    }
}
