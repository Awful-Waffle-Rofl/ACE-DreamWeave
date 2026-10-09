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

        /// <summary>
        /// Credits N DISTINCT ledger keys in one or two statements against one context. DEPOSIT ONLY.
        ///
        /// Returns true when every key reported <see cref="AccountVaultStackAdjustResult.Applied"/>.
        /// <paramref name="perKey"/> is the real answer and is populated for every key supplied,
        /// whatever the return value; the per-key value carries exactly the obligations
        /// <see cref="TryAdjustAccountVaultStack"/>'s does, so only
        /// <see cref="AccountVaultStackAdjustResult.Refused"/> proves the ledger did not move.
        ///
        /// PRECONDITIONS, both refusing the WHOLE call rather than the offending key: every delta
        /// STRICTLY POSITIVE, and every key DISTINCT. Neither is hygiene. Positive deltas are what make
        /// the multi-row forms safe, because the non-negative guard can then never refuse and MATCHED
        /// equals PRESENT exactly. A repeated key would silently drop the second delta AND suppress the
        /// mismatch that would have caught it, producing an under-credit that presents as a clean
        /// success - which is why a caller builds its plan from a dictionary rather than a list.
        ///
        /// <paramref name="knownPresent"/> is the caller's in-memory index used as a HINT: keys it
        /// holds take the guarded UPDATE, the rest go straight to the multi-row upsert, which keeps the
        /// AUTO_INCREMENT burn at one id per genuinely new row. A stale hint is bounded, never fatal.
        ///
        /// Groups are independent: a batch can apply some keys and fail others. It is NOT atomic across
        /// keys and must never be assumed to be.
        /// </summary>
        bool TryAdjustAccountVaultStackBatch(uint accountId, IReadOnlyList<(uint Wcid, long Delta)> deltas, IReadOnlySet<uint> knownPresent,
                                             out IReadOnlyDictionary<uint, AccountVaultStackAdjustResult> perKey);

        int DeleteEmptyAccountVaultStacks(uint accountId);

        /// <summary>
        /// Counted item-class rows. NULL means the read failed; an empty list means the account holds
        /// no classes. Same rule as every other read here, and the same consequence for conflating the
        /// two: a store that read a failure as "holds none" would draw an empty panel over a full
        /// vault and then let a deposit open a duplicate class row.
        /// </summary>
        List<AccountVaultClass> GetAccountVaultClasses(uint accountId);

        /// <summary>
        /// Applies a signed ITEM-count delta and a signed pooled-VALUE delta to one class row, creating
        /// it from <paramref name="seed"/> if absent.
        ///
        /// Identical contract to <see cref="TryAdjustAccountVaultStack"/>, reusing the same four-value
        /// result rather than a parallel enum. Only
        /// <see cref="AccountVaultStackAdjustResult.Refused"/> proves the ledger did not move.
        ///
        /// Both columns move in ONE statement at the database, so a caller can never observe a count
        /// and a total that describe different numbers of items.
        ///
        /// <paramref name="newCount"/> and <paramref name="newTotalValue"/> are meaningful ONLY for
        /// <see cref="AccountVaultStackAdjustResult.Applied"/>.
        /// </summary>
        AccountVaultStackAdjustResult TryAdjustAccountVaultClass(uint accountId, string classKey, long countDelta, long valueDelta,
                                                                 AccountVaultClassSeed seed, out long newCount, out long newTotalValue);

        /// <summary>
        /// Credits N DISTINCT class keys in one or two statements against one context. DEPOSIT ONLY.
        ///
        /// <see cref="TryAdjustAccountVaultStackBatch"/>'s contract with a second column and a seed,
        /// and deliberately identical to it on every point: the same statement order, the same
        /// distinct-key and positive-delta preconditions refusing the whole call, the same rule that a
        /// key the UPDATE matched is never re-applied, and the same rule that a failed identifying read
        /// fails the WHOLE call rather than falling back to an upsert that would double-credit.
        ///
        /// ONE DIFFERENCE, and it is forced by the data rather than chosen: the COUNT delta must be
        /// strictly positive, while the VALUE delta need only be NON-NEGATIVE. An item's pooled value
        /// contribution is its Value clamped at zero, so a legitimately worthless item contributes
        /// exactly 0 and a strictly-positive rule there would refuse its whole sale. Every property the
        /// positive rule buys is carried by the count delta alone.
        ///
        /// A seed is required for EVERY key, not only the ones the caller believes are new: a key the
        /// caller called present can still turn out to be absent, and the row cannot be created without
        /// one.
        ///
        /// Groups are independent: a batch can apply some keys and fail others.
        /// </summary>
        bool TryAdjustAccountVaultClassBatch(uint accountId,
                                             IReadOnlyList<(string ClassKey, long CountDelta, long ValueDelta, AccountVaultClassSeed Seed)> groups,
                                             IReadOnlySet<string> knownPresent,
                                             out IReadOnlyDictionary<string, AccountVaultStackAdjustResult> perKey);

        int DeleteEmptyAccountVaultClasses(uint accountId);

        /// <summary>Sharing grants. NULL means the read failed.</summary>
        List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId);

        /// <summary>
        /// Every grant naming this character as grantee, ordered by owner account. NULL means the read
        /// failed; an empty list means nothing is shared with the character.
        /// </summary>
        List<AccountVaultGrant> GetAccountVaultGrantsForGrantee(uint granteeCharacterGuid);

        bool UpsertAccountVaultGrant(AccountVaultGrant row);

        int DeleteAccountVaultGrant(uint ownerAccountId, uint granteeCharacterGuid);

        bool AddAccountVaultLog(AccountVaultLog row);

        /// <summary>
        /// Appends N audit rows in ONE round trip. This is the ONLY method on this interface with no
        /// caller on a player's thread: <see cref="AccountVaultAuditWriter"/> owns it and drains its
        /// queue through it on a background thread.
        ///
        /// False means the write failed and those rows are gone. Like the single-row method that is a
        /// logging outage, never a storage outage: the operations the rows describe have already
        /// happened and no caller may undo one because its audit row did not land.
        /// </summary>
        bool AddAccountVaultLogBatch(List<AccountVaultLog> rows);

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

        public bool TryAdjustAccountVaultStackBatch(uint accountId, IReadOnlyList<(uint Wcid, long Delta)> deltas, IReadOnlySet<uint> knownPresent,
                                                    out IReadOnlyDictionary<uint, AccountVaultStackAdjustResult> perKey)
        {
            // Deliberately NOT wrapped in Read<T>, for exactly the reason the single-key adjust is not:
            // the DAO already classifies its own failures per key, and a wrapper here could only flatten
            // an escaped exception into one value for the whole batch - which would have to be Failed
            // for every key, and would then be indistinguishable from a statement that threw after
            // crediting part of the batch. The DAO catches everything it can reach; if anything ever
            // escapes it must surface rather than be silently collapsed into "unknown".
            return Db.TryAdjustAccountVaultStackBatch(accountId, deltas, knownPresent, out perKey);
        }

        public int DeleteEmptyAccountVaultStacks(uint accountId)
        {
            return Db.DeleteEmptyAccountVaultStacks(accountId);
        }

        public List<AccountVaultClass> GetAccountVaultClasses(uint accountId)
        {
            return Read($"GetAccountVaultClasses({accountId})", () => Db.GetAccountVaultClasses(accountId));
        }

        public AccountVaultStackAdjustResult TryAdjustAccountVaultClass(uint accountId, string classKey, long countDelta, long valueDelta,
                                                                       AccountVaultClassSeed seed, out long newCount, out long newTotalValue)
        {
            // NOT wrapped in Read<T>, for exactly the reason TryAdjustAccountVaultStack is not: the DAO
            // already classifies its own failures into the four outcomes, and a wrapper here could only
            // flatten an escaped exception into Failed, which would then be indistinguishable from an
            // UPDATE that threw.
            return Db.TryAdjustAccountVaultClass(accountId, classKey, countDelta, valueDelta, seed, out newCount, out newTotalValue);
        }

        public bool TryAdjustAccountVaultClassBatch(uint accountId,
                                                    IReadOnlyList<(string ClassKey, long CountDelta, long ValueDelta, AccountVaultClassSeed Seed)> groups,
                                                    IReadOnlySet<string> knownPresent,
                                                    out IReadOnlyDictionary<string, AccountVaultStackAdjustResult> perKey)
        {
            // NOT wrapped in Read<T>, for exactly the reason TryAdjustAccountVaultStackBatch is not:
            // the DAO already classifies its own failures per key, and a wrapper here could only flatten
            // an escaped exception into Failed for the whole batch, which would be indistinguishable
            // from a statement that threw after crediting part of it.
            return Db.TryAdjustAccountVaultClassBatch(accountId, groups, knownPresent, out perKey);
        }

        public int DeleteEmptyAccountVaultClasses(uint accountId)
        {
            return Db.DeleteEmptyAccountVaultClasses(accountId);
        }

        public List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId)
        {
            return Read($"GetAccountVaultGrants({ownerAccountId})", () => Db.GetAccountVaultGrants(ownerAccountId));
        }

        public List<AccountVaultGrant> GetAccountVaultGrantsForGrantee(uint granteeCharacterGuid)
        {
            return Read($"GetAccountVaultGrantsForGrantee(0x{granteeCharacterGuid:X8})", () => Db.GetAccountVaultGrantsForGrantee(granteeCharacterGuid));
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

        public bool AddAccountVaultLogBatch(List<AccountVaultLog> rows)
        {
            return Db.AddAccountVaultLogBatch(rows);
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
