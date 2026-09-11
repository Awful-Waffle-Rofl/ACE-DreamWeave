using System;
using System.Collections.Generic;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Database.Models.Shard;

// The EF entity ACE.Database.Models.Shard.AccountVault cannot be named unqualified from inside the
// namespace ACE.Server.Entity.AccountVault - the enclosing namespace's own name wins, and the
// compiler reports it as "a namespace but is used like a type". The alias is the whole fix; the
// signatures below are still exactly the DAO's.
using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The store's view of the four account_vault* tables. One method per Task 2 DAO method, same
    /// signatures, so <see cref="ShardAccountVaultBackend"/> is a thin delegation and the tests can
    /// drive a recording fake with no MySQL instance anywhere.
    ///
    /// NULL FROM A READ MEANS THE READ FAILED. An EMPTY list means the account genuinely owns nothing.
    /// The two must never be conflated, and this is not a style point: DESIGN 7.1 has insertion scan
    /// the account's vaults oldest-first and create a new vault only when all are full, so a transient
    /// shard failure read as "this account owns no vaults" makes the player's stored items appear to
    /// vanish and leaves behind an orphan container that can never be reaped (R2 forbids destroying a
    /// container that might be non-empty). Every caller of these four reads refuses the operation on
    /// null instead of proceeding.
    /// </summary>
    public interface IAccountVaultBackend
    {
        /// <summary>Vault index rows, oldest-first. NULL means the read failed.</summary>
        List<ShardAccountVault> GetAccountVaults(uint accountId);

        /// <summary>
        /// Every vault container guid on the shard, across every account. Read once at process start
        /// to seed <see cref="AccountVaultSpawnFilter"/>; never on a landblock activation.
        ///
        /// NULL means the read failed. An EMPTY list means no account owns a vault yet, which is the
        /// ordinary state of a new shard - and the difference matters here more than anywhere else in
        /// this interface, because an empty seed and a failed seed leave the filter in the same
        /// visible state and only one of them is correct.
        /// </summary>
        List<uint> GetAllAccountVaultContainerGuids();

        bool AddAccountVault(ShardAccountVault row);

        bool DeleteAccountVault(uint id);

        /// <summary>Ledger rows. NULL means the read failed.</summary>
        List<AccountVaultStack> GetAccountVaultStacks(uint accountId);

        /// <summary>
        /// Applies a signed delta to one ledger row, creating it if absent.
        ///
        /// Fix round 2, F1: this used to return a bool documented as "false means the ledger did NOT
        /// move; it is never probably fine". The claim was sound and the signature could not keep it -
        /// false was ALSO what a database failure returned, and a failure after the delta committed is
        /// exactly the case where the ledger DID move. Callers unwound on it, which on the withdraw
        /// path meant a balancing Return audit row plus nothing delivered: an item loss that the audit
        /// trail records as a non-event.
        ///
        /// Only <see cref="AccountVaultStackAdjustResult.Refused"/> now carries the old promise. See
        /// that enum for what each of the four values obliges a caller to do; the short form is that
        /// Applied and AppliedCountUnknown both mean the units moved, and Failed means nobody knows.
        ///
        /// <paramref name="newCount"/> is meaningful ONLY for
        /// <see cref="AccountVaultStackAdjustResult.Applied"/>. It is 0 for every other value, and 0 is
        /// not a count there.
        /// </summary>
        AccountVaultStackAdjustResult TryAdjustAccountVaultStack(uint accountId, uint wcid, long delta, out long newCount);

        int DeleteEmptyAccountVaultStacks(uint accountId);

        /// <summary>Sharing grants. NULL means the read failed.</summary>
        List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId);

        bool UpsertAccountVaultGrant(AccountVaultGrant row);

        int DeleteAccountVaultGrant(uint ownerAccountId, uint granteeCharacterGuid);

        bool AddAccountVaultLog(AccountVaultLog row);

        /// <summary>Most recent audit rows. NULL means the read failed.</summary>
        List<AccountVaultLog> GetAccountVaultLog(uint ownerAccountId, int limit);

        /// <summary>
        /// One account's saved mule form (Mule Form Token design section 5.2). Cosmetic data: the
        /// creature body the account's /mule vendor wears.
        ///
        /// TWO CHANNELS, DELIBERATELY, AND THIS IS THE ONE READ ON THIS INTERFACE THAT NEEDS THEM.
        /// Every other read here answers with a list, so the file-level rule "null means the read
        /// failed, empty means the account owns nothing" separates the two cases for free. This one
        /// returns a single row, where "no saved look" and "the read failed" would both be a bare
        /// null and mean opposite things.
        ///
        /// <c>Ok == false</c> means the READ FAILED and Row is meaningless. <c>Ok == true</c> with a
        /// null Row means the account has genuinely never saved a look, which is the ordinary state
        /// of every account. Callers branch on Ok before they branch on Row, because
        /// <see cref="AccountVaultStore"/> caches this answer for the store's lifetime and caching a
        /// transient failure as "no look" would strip an earned form from every summon until the
        /// process restarts.
        /// </summary>
        (bool Ok, AccountMuleForm Row) GetAccountMuleForm(uint accountId);

        /// <summary>
        /// Saves or replaces one account's mule form. Returns false if the write failed, in which
        /// case the caller must leave its cached look alone and tell the player the vault is
        /// unavailable rather than pretending the look was saved.
        /// </summary>
        bool UpsertAccountMuleForm(AccountMuleForm row);

        /// <summary>Barrel rows for one account, newest first. NULL means the read failed.</summary>
        List<AccountVaultBarrel> GetAccountVaultBarrels(uint accountId, int limit);

        /// <summary>One barrel row by id, or null. A null return CANNOT distinguish absent from failed, so callers refuse on it.</summary>
        AccountVaultBarrel GetAccountVaultBarrel(uint id);

        /// <summary>Rows still holding an item: neither restored nor purged, barreled before the cutoff. NULL means the read failed.</summary>
        List<AccountVaultBarrel> GetExpiredAccountVaultBarrels(DateTime cutoffUtc, int limit);

        bool AddAccountVaultBarrel(AccountVaultBarrel row);

        bool UpdateAccountVaultBarrel(AccountVaultBarrel row);
    }

    /// <summary>
    /// Production backend: a straight delegation to the vault DAO on
    /// <see cref="DatabaseManager.Shard"/>'s base <see cref="ShardDatabase"/>.
    ///
    /// These are index and ledger operations, not biota operations, so they deliberately do NOT go
    /// through SerializedShardDatabase's worker thread - see the header on
    /// ACE.Database/ShardDatabase_AccountVault.cs. Serialization for one account comes from
    /// <see cref="AccountVaultStore"/>'s own mutation queue instead.
    ///
    /// The four reads are wrapped so that a database failure surfaces as null rather than as an
    /// exception escaping into a player's transaction handler. That guarantee belongs to the contract
    /// on <see cref="IAccountVaultBackend"/>, so it is asserted here rather than assumed of the DAO:
    /// if the DAO already catches and returns null the wrapper never fires, and if it ever stops
    /// doing so the contract still holds.
    /// </summary>
    public class ShardAccountVaultBackend : IAccountVaultBackend
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static ShardDatabase Db => DatabaseManager.Shard.BaseDatabase;

        private static T Read<T>(string what, Func<T> read) where T : class
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] {what} failed: {ex.GetFullMessage()}");
                return null;
            }
        }

        public List<ShardAccountVault> GetAccountVaults(uint accountId)
        {
            return Read($"GetAccountVaults({accountId})", () => Db.GetAccountVaults(accountId));
        }

        public List<uint> GetAllAccountVaultContainerGuids()
        {
            return Read("GetAllAccountVaultContainerGuids()", () => Db.GetAllAccountVaultContainerGuids());
        }

        public bool AddAccountVault(ShardAccountVault row)
        {
            return Db.AddAccountVault(row);
        }

        public bool DeleteAccountVault(uint id)
        {
            return Db.DeleteAccountVault(id);
        }

        public List<AccountVaultStack> GetAccountVaultStacks(uint accountId)
        {
            return Read($"GetAccountVaultStacks({accountId})", () => Db.GetAccountVaultStacks(accountId));
        }

        public AccountVaultStackAdjustResult TryAdjustAccountVaultStack(uint accountId, uint wcid, long delta, out long newCount)
        {
            // Deliberately NOT wrapped in Read<T> the way the four list reads are. The DAO already
            // classifies its own failures into the four outcomes, and a wrapper here could only turn an
            // escaped exception into one flat value - which would have to be Failed, and would then be
            // indistinguishable from an UPDATE that threw. The DAO catches everything it can reach, so
            // nothing is expected to escape; if anything ever does it must surface rather than be
            // silently collapsed into "unknown".
            return Db.TryAdjustAccountVaultStack(accountId, wcid, delta, out newCount);
        }

        public int DeleteEmptyAccountVaultStacks(uint accountId)
        {
            return Db.DeleteEmptyAccountVaultStacks(accountId);
        }

        public List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId)
        {
            return Read($"GetAccountVaultGrants({ownerAccountId})", () => Db.GetAccountVaultGrants(ownerAccountId));
        }

        public bool UpsertAccountVaultGrant(AccountVaultGrant row)
        {
            return Db.UpsertAccountVaultGrant(row);
        }

        public int DeleteAccountVaultGrant(uint ownerAccountId, uint granteeCharacterGuid)
        {
            return Db.DeleteAccountVaultGrant(ownerAccountId, granteeCharacterGuid);
        }

        public bool AddAccountVaultLog(AccountVaultLog row)
        {
            return Db.AddAccountVaultLog(row);
        }

        public List<AccountVaultLog> GetAccountVaultLog(uint ownerAccountId, int limit)
        {
            return Read($"GetAccountVaultLog({ownerAccountId})", () => Db.GetAccountVaultLog(ownerAccountId, limit));
        }

        /// <summary>
        /// Deliberately NOT wrapped in <see cref="Read{T}"/>: that helper's failure answer is null,
        /// which is precisely the value this signature exists to stop meaning two things. The
        /// equivalent guard is written out here so an escaped exception surfaces as (false, null) -
        /// the failure answer - rather than as (true, null), which would read as "no saved look".
        /// </summary>
        public (bool Ok, AccountMuleForm Row) GetAccountMuleForm(uint accountId)
        {
            try
            {
                return Db.GetAccountMuleForm(accountId);
            }
            catch (Exception ex)
            {
                log.Error($"[MULEFORM] GetAccountMuleForm({accountId}) failed: {ex.GetFullMessage()}");
                return (false, null);
            }
        }

        public bool UpsertAccountMuleForm(AccountMuleForm row)
        {
            return Db.UpsertAccountMuleForm(row);
        }

        /// <summary>
        /// Wrapped in <see cref="Read{T}"/> like the other list reads, and for the same reason: NULL
        /// means the read FAILED, an EMPTY list means the account has never barreled anything, and
        /// nothing above may conflate the two.
        /// </summary>
        public List<AccountVaultBarrel> GetAccountVaultBarrels(uint accountId, int limit)
        {
            return Read($"GetAccountVaultBarrels({accountId})", () => Db.GetAccountVaultBarrels(accountId, limit));
        }

        /// <summary>
        /// A single row, so null here really is ambiguous - it is both "no such row" and "the read
        /// failed". That is why the restore path refuses on null rather than reporting "already
        /// restored" or anything else it might infer: the one thing it must never do is act.
        /// </summary>
        public AccountVaultBarrel GetAccountVaultBarrel(uint id)
        {
            return Read($"GetAccountVaultBarrel({id})", () => Db.GetAccountVaultBarrel(id));
        }

        public List<AccountVaultBarrel> GetExpiredAccountVaultBarrels(DateTime cutoffUtc, int limit)
        {
            return Read($"GetExpiredAccountVaultBarrels({cutoffUtc:o})", () => Db.GetExpiredAccountVaultBarrels(cutoffUtc, limit));
        }

        public bool AddAccountVaultBarrel(AccountVaultBarrel row)
        {
            return Db.AddAccountVaultBarrel(row);
        }

        public bool UpdateAccountVaultBarrel(AccountVaultBarrel row)
        {
            return Db.UpdateAccountVaultBarrel(row);
        }
    }
}
