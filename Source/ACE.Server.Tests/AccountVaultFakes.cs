using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Recording in-memory stand-in for the four account_vault* tables.
    ///
    /// It models the two DAO behaviours the store's correctness rests on:
    ///
    /// - a read that FAILS returns null, and a read that finds nothing returns an empty list. The
    ///   failure switches exist so the "refuses rather than creating a vault" tests can drive that
    ///   path, which is the one that would otherwise regress silently.
    ///
    /// - TryAdjustAccountVaultStack refuses a delta that would take the count below zero and reports
    ///   that as false, with the ledger unmoved.
    ///
    /// The adjust is deliberately implemented as a NON-atomic read-modify-write with an optional
    /// delay between the two halves. The real DAO applies the delta in one guarded UPDATE, so this is
    /// not a model of it - it is a model of what the store must survive when the guard is not there,
    /// which is what makes the concurrency tests able to fail.
    /// </summary>
    internal class FakeVaultBackend : IAccountVaultBackend
    {
        public readonly List<ShardAccountVault> Vaults = new List<ShardAccountVault>();
        public readonly List<AccountVaultStack> Stacks = new List<AccountVaultStack>();
        public readonly List<AccountVaultGrant> Grants = new List<AccountVaultGrant>();
        public readonly List<AccountVaultLog> Logs = new List<AccountVaultLog>();

        public bool FailVaultRead;
        public bool FailStackRead;
        public bool FailGrantRead;
        public bool FailLogRead;

        public bool FailAddVault;

        /// <summary>
        /// Wcids whose ledger adjust is REFUSED: the ledger provably does not move. This is what the
        /// pre-F1 bool false meant at every call site, so every test written against the old switch
        /// keeps its exact meaning.
        /// </summary>
        public readonly HashSet<uint> FailLedgerAdjustWcids = new HashSet<uint>();

        /// <summary>
        /// Fix round 2, F1: wcids whose adjust APPLIES the delta and then reports
        /// AppliedCountUnknown - the real DAO's "guarded UPDATE committed, LINQ read-back failed" case.
        /// The delta really is applied here, which is what lets a test assert that the store re-reads
        /// the ledger and arrives at the right number without ever being told it.
        /// </summary>
        public readonly HashSet<uint> LedgerAdjustCountUnknownWcids = new HashSet<uint>();

        /// <summary>
        /// Fix round 2, F1: wcids whose adjust reports Failed - the UPDATE threw, so nobody knows
        /// whether it committed. Modelled as NOT applying the delta, which is the harder half for the
        /// callers: every caller has to be correct for a Failed that did not land AND for one that did,
        /// and only the not-landed half is observable from a fake.
        /// </summary>
        public readonly HashSet<uint> LedgerAdjustFailedWcids = new HashSet<uint>();

        /// <summary>Adjust calls, so a test can prove a second attempt was or was not made.</summary>
        public int LedgerAdjustCalls;

        /// <summary>Milliseconds slept between the ledger read and the ledger write. 0 by default.</summary>
        public int LedgerAdjustDelayMs;

        public int VaultReads;

        /// <summary>
        /// Reads of the stack ledger. Fix round 2, F1: the store re-reads the ledger after a mutation
        /// that committed with an unknown resulting count, and this counter is how a test observes that
        /// the re-read genuinely HAPPENED rather than inferring it from a count that could also be
        /// right by accident.
        /// </summary>
        public int StackReads;

        public int GrantReads;
        public int AddedVaults;
        public int DeletedEmptyStackCalls;

        /// <summary>
        /// Write counts, so a test can assert that a repeat grant on identical terms costs NOTHING.
        /// Grants.Count cannot see this: the upsert updates the existing row rather than adding one, so
        /// a re-grant that rewrote the row and appended an audit row would leave the list length
        /// unchanged and look identical to a re-grant that did nothing at all.
        /// </summary>
        public int UpsertGrantCalls;

        public int AddLogCalls;

        /// <summary>Every count this ledger has ever held. Asserted never to go negative.</summary>
        public readonly List<long> LedgerCountHistory = new List<long>();

        private uint nextRowId = 1;

        public List<ShardAccountVault> GetAccountVaults(uint accountId)
        {
            VaultReads++;

            if (FailVaultRead)
                return null;

            return Vaults.Where(v => v.AccountId == accountId).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id).ToList();
        }

        /// <summary>
        /// The whole-table read the spawn filter seeds from. Honours <see cref="FailVaultRead"/> the
        /// same way GetAccountVaults does, because the seeding path's whole contract is that a null
        /// read is not an empty table.
        /// </summary>
        /// <summary>
        /// Makes the whole-table read THROW rather than return null. A DAO failure normally surfaces as
        /// null, but AccountVaultManager.TrySeedSpawnFilter also wraps the call in a try/catch so that
        /// nothing can escape into the startup path, and that catch is only reachable from here.
        /// </summary>
        public bool ThrowOnVaultRead;

        public List<uint> GetAllAccountVaultContainerGuids()
        {
            VaultReads++;

            if (ThrowOnVaultRead)
                throw new InvalidOperationException("simulated shard failure reading account_vault");

            if (FailVaultRead)
                return null;

            return Vaults.Select(v => v.ContainerGuid).ToList();
        }

        public bool AddAccountVault(ShardAccountVault row)
        {
            if (FailAddVault)
                return false;

            row.Id = nextRowId++;
            Vaults.Add(row);
            AddedVaults++;
            return true;
        }

        public bool DeleteAccountVault(uint id)
        {
            return Vaults.RemoveAll(v => v.Id == id) > 0;
        }

        public List<AccountVaultStack> GetAccountVaultStacks(uint accountId)
        {
            StackReads++;

            if (FailStackRead)
                return null;

            return Stacks.Where(s => s.AccountId == accountId).OrderBy(s => s.Wcid).ToList();
        }

        public AccountVaultStackAdjustResult TryAdjustAccountVaultStack(uint accountId, uint wcid, long delta, out long newCount)
        {
            newCount = 0;

            LedgerAdjustCalls++;

            if (FailLedgerAdjustWcids.Contains(wcid))
                return AccountVaultStackAdjustResult.Refused;

            // Modelled as the UPDATE throwing: nothing is applied, and nobody can tell that from the
            // outside. See LedgerAdjustFailedWcids.
            if (LedgerAdjustFailedWcids.Contains(wcid))
                return AccountVaultStackAdjustResult.Failed;

            var row = Stacks.FirstOrDefault(s => s.AccountId == accountId && s.Wcid == wcid);

            if (row == null)
            {
                row = new AccountVaultStack { Id = nextRowId++, AccountId = accountId, Wcid = wcid, Count = 0 };
                Stacks.Add(row);
            }

            // Read half.
            var current = row.Count;

            if (LedgerAdjustDelayMs > 0)
                Thread.Sleep(LedgerAdjustDelayMs);

            var proposed = current + delta;

            if (proposed < 0)
                return AccountVaultStackAdjustResult.Refused;   // refused; the ledger did not move

            // Write half.
            row.Count = proposed;
            LedgerCountHistory.Add(proposed);

            // The delta IS applied above before this returns - that is the whole point of this case:
            // only the read-back is lost, so newCount stays at 0 and the caller must not use it.
            if (LedgerAdjustCountUnknownWcids.Contains(wcid))
                return AccountVaultStackAdjustResult.AppliedCountUnknown;

            newCount = proposed;
            return AccountVaultStackAdjustResult.Applied;
        }

        /// <summary>
        /// Fix round 2, F4: how many further DeleteEmptyAccountVaultStacks calls must THROW. This is
        /// the one hook in the ledger withdraw path that runs AFTER the debit and AFTER the built
        /// stacks have been handed to the caller's out parameter, which is exactly the shape the
        /// vendor's throw handling has to survive: the vault is already short, the objects already
        /// exist, and the call never returns.
        /// </summary>
        public int ThrowFromDeleteEmptyStacksCount;

        public int DeleteEmptyAccountVaultStacks(uint accountId)
        {
            if (ThrowFromDeleteEmptyStacksCount > 0)
            {
                ThrowFromDeleteEmptyStacksCount--;
                throw new InvalidOperationException($"fake failure reaping empty ledger rows for account {accountId}");
            }

            DeletedEmptyStackCalls++;

            return Stacks.RemoveAll(s => s.AccountId == accountId && s.Count <= 0);
        }

        public List<AccountVaultGrant> GetAccountVaultGrants(uint ownerAccountId)
        {
            GrantReads++;

            if (FailGrantRead)
                return null;

            return Grants.Where(g => g.OwnerAccountId == ownerAccountId).OrderBy(g => g.GranteeCharacterName).ToList();
        }

        public bool UpsertAccountVaultGrant(AccountVaultGrant row)
        {
            UpsertGrantCalls++;

            var existing = Grants.FirstOrDefault(g =>
                g.OwnerAccountId == row.OwnerAccountId && g.GranteeCharacterGuid == row.GranteeCharacterGuid);

            if (existing == null)
            {
                row.Id = nextRowId++;
                Grants.Add(row);
            }
            else
            {
                existing.CanWithdraw = row.CanWithdraw;
                existing.GranteeCharacterName = row.GranteeCharacterName;
                existing.GrantedAt = row.GrantedAt;
            }

            return true;
        }

        public int DeleteAccountVaultGrant(uint ownerAccountId, uint granteeCharacterGuid)
        {
            return Grants.RemoveAll(g => g.OwnerAccountId == ownerAccountId && g.GranteeCharacterGuid == granteeCharacterGuid);
        }

        public bool AddAccountVaultLog(AccountVaultLog row)
        {
            AddLogCalls++;

            row.Id = nextRowId++;
            Logs.Add(row);
            return true;
        }

        public List<AccountVaultLog> GetAccountVaultLog(uint ownerAccountId, int limit)
        {
            if (FailLogRead)
                return null;

            return Logs.Where(l => l.OwnerAccountId == ownerAccountId)
                .OrderByDescending(l => l.Id)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        /// <summary>
        /// Saved mule forms, one per account. A dictionary rather than a list because the real table
        /// is keyed by account_Id and the write is an upsert; a list would let a test pass while the
        /// production upsert was silently appending duplicates.
        /// </summary>
        public readonly Dictionary<uint, AccountMuleForm> MuleForms = new Dictionary<uint, AccountMuleForm>();

        /// <summary>
        /// Makes the form read report the FAILURE answer, (false, null). Distinct from simply having
        /// no row, which is the success answer (true, null) - the store must cache one and refuse to
        /// cache the other.
        /// </summary>
        public bool FailMuleFormRead;

        /// <summary>Makes the form upsert report false with the stored row left untouched.</summary>
        public bool FailMuleFormUpsert;

        /// <summary>Form reads, so a test can prove the store caches rather than re-reading.</summary>
        public int MuleFormReads;

        /// <summary>Form writes, so a test can see a write that the stored row cannot reveal.</summary>
        public int UpsertMuleFormCalls;

        public (bool Ok, AccountMuleForm Row) GetAccountMuleForm(uint accountId)
        {
            MuleFormReads++;

            if (FailMuleFormRead)
                return (false, null);

            return (true, MuleForms.TryGetValue(accountId, out var row) ? row : null);
        }

        public bool UpsertAccountMuleForm(AccountMuleForm row)
        {
            UpsertMuleFormCalls++;

            if (FailMuleFormUpsert)
                return false;

            MuleForms[row.AccountId] = row;
            return true;
        }

        // ---- account_vault_barrel ----

        /// <summary>
        /// Every barrel row ever written here, in insertion order. The real table is never deleted
        /// from - a purge stamps purged_At and keeps the row - so this list models it exactly by never
        /// removing anything either.
        /// </summary>
        public readonly List<AccountVaultBarrel> Barrels = new List<AccountVaultBarrel>();

        /// <summary>Makes the two list reads report the FAILURE answer, null, rather than an empty list.</summary>
        public bool FailBarrelRead;

        /// <summary>Makes <see cref="AddAccountVaultBarrel"/> report false with nothing recorded.</summary>
        public bool FailAddBarrel;

        /// <summary>Makes <see cref="UpdateAccountVaultBarrel"/> report false with the row left untouched.</summary>
        public bool FailUpdateBarrel;

        public List<AccountVaultBarrel> GetAccountVaultBarrels(uint accountId, int limit)
        {
            if (FailBarrelRead)
                return null;

            return Barrels.Where(b => b.AccountId == accountId)
                .OrderByDescending(b => b.BarreledAt)
                .ThenByDescending(b => b.Id)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        /// <summary>
        /// Returns the LIVE row object, not a copy, which is what makes an in-place stamp visible to a
        /// test the way the real DAO's re-read of the table is. <see cref="FailBarrelRead"/> is
        /// honoured here too, because the restore path refuses on null and that arm needs to be
        /// reachable.
        /// </summary>
        public AccountVaultBarrel GetAccountVaultBarrel(uint id)
        {
            if (FailBarrelRead)
                return null;

            return Barrels.FirstOrDefault(b => b.Id == id);
        }

        public List<AccountVaultBarrel> GetExpiredAccountVaultBarrels(DateTime cutoffUtc, int limit)
        {
            if (FailBarrelRead)
                return null;

            return Barrels.Where(b => b.RestoredAt == null && b.PurgedAt == null && b.BarreledAt < cutoffUtc)
                .OrderBy(b => b.BarreledAt)
                .ThenBy(b => b.Id)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public bool AddAccountVaultBarrel(AccountVaultBarrel row)
        {
            if (FailAddBarrel || row == null)
                return false;

            // The real column carries a CURRENT_TIMESTAMP default and the DAO stamps UtcNow over it
            // when the caller left it at the CLR default; model that, or a row seeded without a time
            // would sort as DateTime.MinValue and always look expired.
            if (row.BarreledAt == default)
                row.BarreledAt = DateTime.UtcNow;

            row.Id = nextRowId++;
            Barrels.Add(row);
            return true;
        }

        public bool UpdateAccountVaultBarrel(AccountVaultBarrel row)
        {
            if (FailUpdateBarrel || row == null)
                return false;

            var existing = Barrels.FirstOrDefault(b => b.Id == row.Id);

            if (existing == null)
                return false;

            // Only the two terminal stamps, exactly as the DAO does: everything else on the row is a
            // snapshot of the barreling and must not be rewritten by a later event.
            existing.RestoredAt = row.RestoredAt;
            existing.PurgedAt = row.PurgedAt;

            return true;
        }
    }

    /// <summary>
    /// In-memory stand-in for everything the store needs from the world-object layer.
    ///
    /// Objects are built from hand-made weenies with STATIC-range guids, so Destroy() and the guid
    /// manager are never reached, following the same discipline as ContainerStackTests.
    ///
    /// <see cref="MaxConcurrentCalls"/> is the serialization detector: every call in here brackets a
    /// short sleep with an increment and a decrement, so a store that stopped serializing its
    /// mutations would record a maximum above 1 and the concurrency tests would fail rather than
    /// merely observing that two tasks completed.
    /// </summary>
    internal class FakeVaultWorld : IAccountVaultWorldSource
    {
        private static int nextGuid = 0x7F200000;

        /// <summary>What the vault weenie claims. 300 by default, so the byte wrap (R7) is live.</summary>
        public int SeedVaultItemsCapacity = 300;

        public int ItemMaxStackSize = 100;

        /// <summary>What IsPristine answers. False keeps the item's biota, which is the vault path.</summary>
        public bool PristineResult;

        public readonly HashSet<uint> FailCreateWcids = new HashSet<uint>();

        /// <summary>
        /// How many further DestroyItem calls must THROW before the next one behaves. Set to 1 for
        /// "throw once, then succeed". This models a failure in the teardown half of a ledger return -
        /// the half that runs AFTER the ledger credit has already committed - which is the only way to
        /// reach the caller's retry with the units already restored.
        /// </summary>
        public int ThrowFromDestroyItemCount;

        public readonly List<Container> CreatedContainers = new List<Container>();
        public readonly List<WorldObject> Destroyed = new List<WorldObject>();
        public readonly List<WorldObject> Saved = new List<WorldObject>();

        public readonly Dictionary<uint, Container> Containers = new Dictionary<uint, Container>();

        public readonly Dictionary<string, (uint guid, string name, uint accountId)> Characters =
            new Dictionary<string, (uint, string, uint)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sleep inside every call, to widen the window a lost serialization would open.</summary>
        public int CallDelayMs;

        private int concurrentCalls;
        private int maxConcurrentCalls;

        public int MaxConcurrentCalls => Volatile.Read(ref maxConcurrentCalls);

        private IDisposable EnterCall()
        {
            var now = Interlocked.Increment(ref concurrentCalls);

            while (true)
            {
                var observed = Volatile.Read(ref maxConcurrentCalls);

                if (now <= observed || Interlocked.CompareExchange(ref maxConcurrentCalls, now, observed) == observed)
                    break;
            }

            if (CallDelayMs > 0)
                Thread.Sleep(CallDelayMs);

            return new CallScope(this);
        }

        private sealed class CallScope : IDisposable
        {
            private readonly FakeVaultWorld owner;

            public CallScope(FakeVaultWorld owner) => this.owner = owner;

            public void Dispose() => Interlocked.Decrement(ref owner.concurrentCalls);
        }

        public WorldObject CreateNewWorldObject(uint weenieClassId)
        {
            using (EnterCall())
            {
                if (FailCreateWcids.Contains(weenieClassId))
                    return null;

                if (weenieClassId == AccountVaultStore.VaultContainerWcid)
                {
                    var container = MakeContainer(SeedVaultItemsCapacity);
                    CreatedContainers.Add(container);
                    Containers[container.Guid.Full] = container;
                    return container;
                }

                return MakeStack(weenieClassId, 1, ItemMaxStackSize);
            }
        }

        /// <summary>
        /// Container guids whose load reports a READ FAILURE rather than an absence. The two are
        /// modelled separately here because the store treats them as opposites: an absent biota drops
        /// one index row, an unreadable one refuses the whole account.
        /// </summary>
        public readonly HashSet<uint> FailLoadContainerGuids = new HashSet<uint>();

        public VaultContainerLoad LoadContainer(uint containerGuid, out Container container)
        {
            using (EnterCall())
            {
                container = null;

                if (FailLoadContainerGuids.Contains(containerGuid))
                    return VaultContainerLoad.Failed;

                if (!Containers.TryGetValue(containerGuid, out container))
                    return VaultContainerLoad.Absent;

                return VaultContainerLoad.Loaded;
            }
        }

        public bool IsPristine(WorldObject item)
        {
            using (EnterCall())
                return PristineResult;
        }

        /// <summary>When true every save reports FAILURE to its callback and records nothing.</summary>
        public bool FailSaveBiota;

        /// <summary>
        /// Per-guid countdown of saves that must report FAILURE before the next one succeeds. Set an
        /// entry to 1 for "fail this object's first save, then behave". Same shape as
        /// <see cref="ThrowFromDestroyItemCount"/>, keyed the way <see cref="FailLoadContainerGuids"/>
        /// is, because a retry test has to fail ONE object rather than the whole world source: the
        /// blanket <see cref="FailSaveBiota"/> switch also fails the vault container's own save, which
        /// aborts the deposit long before the path under test is reached.
        /// </summary>
        public readonly Dictionary<uint, int> FailSaveBiotaCounts = new Dictionary<uint, int>();

        private readonly Dictionary<uint, int> saveBiotaCalls = new Dictionary<uint, int>();

        /// <summary>
        /// How many times SaveBiota has been ATTEMPTED for this object, failures included.
        /// <see cref="Saved"/> counts only the successes, so it cannot distinguish "never retried"
        /// from "retried and failed again" - which is the whole question a retry test asks.
        /// </summary>
        public int SaveBiotaCalls(WorldObject worldObject)
        {
            if (worldObject == null)
                return 0;

            return saveBiotaCalls.TryGetValue(worldObject.Guid.Full, out var calls) ? calls : 0;
        }

        /// <summary>
        /// Review round, finding 1: how many further SaveBiota calls must THROW rather than report
        /// failure. Distinct from <see cref="FailSaveBiota"/> and
        /// <see cref="FailSaveBiotaCounts"/>, which model a save that fails politely - this models the
        /// real production throw the store's retry path can hit, SerializedShardDatabase's
        /// BlockingCollection.Add after CompleteAdding() at shutdown.
        /// </summary>
        public int ThrowFromSaveBiotaCount;

        /// <summary>
        /// Runs INSIDE SaveBiota, before its callback. This is the observation seam for the store's
        /// mid-mutation window, and it exists because that window is otherwise unreachable from a
        /// single-threaded test.
        ///
        /// Every vault mutation applies its change to the vault container under stateLock, RELEASES
        /// that lock, calls SaveBiota - a real database round-trip in production - and only then runs
        /// Interlocked.Increment(ref version). A reader taking stateLock inside that gap sees mutated
        /// vaults carrying an unchanged version. Hooking here puts a test on exactly that thread at
        /// exactly that moment, deterministically and with no sleeps.
        /// </summary>
        public Action<WorldObject> DuringSaveBiota;

        /// <summary>
        /// Completes the save inline and answers the callback before returning. The real world source
        /// answers it on the database worker thread instead; the store's SaveAndWait is written to
        /// handle both, and this covers the inline case.
        /// </summary>
        public void SaveBiota(WorldObject worldObject, Action<bool> callback = null)
        {
            using (EnterCall())
            {
                DuringSaveBiota?.Invoke(worldObject);

                var ok = !FailSaveBiota;

                if (worldObject != null)
                {
                    var guid = worldObject.Guid.Full;

                    saveBiotaCalls.TryGetValue(guid, out var calls);
                    saveBiotaCalls[guid] = calls + 1;

                    // AFTER the attempt is counted, deliberately: a test asserting that the throwing
                    // call was actually REACHED needs the count to move, or it cannot tell a throw that
                    // happened from a call that never ran.
                    if (ThrowFromSaveBiotaCount > 0)
                    {
                        ThrowFromSaveBiotaCount--;
                        throw new InvalidOperationException($"fake shard failure saving 0x{guid:X8}");
                    }

                    if (FailSaveBiotaCounts.TryGetValue(guid, out var failuresLeft) && failuresLeft > 0)
                    {
                        FailSaveBiotaCounts[guid] = failuresLeft - 1;
                        ok = false;
                    }
                }

                if (ok)
                    Saved.Add(worldObject);

                callback?.Invoke(ok);
            }
        }

        public void DestroyItem(WorldObject item)
        {
            using (EnterCall())
            {
                if (ThrowFromDestroyItemCount > 0)
                {
                    ThrowFromDestroyItemCount--;
                    throw new InvalidOperationException($"fake teardown failure destroying 0x{item.Guid.Full:X8}");
                }

                Destroyed.Add(item);
            }
        }

        public bool TryResolveCharacter(string characterName, out uint characterGuid, out string canonicalName, out uint accountId)
        {
            characterGuid = 0;
            canonicalName = null;
            accountId = 0;

            if (characterName == null || !Characters.TryGetValue(characterName, out var found))
                return false;

            characterGuid = found.guid;
            canonicalName = found.name;
            accountId = found.accountId;
            return true;
        }

        // ---- object construction helpers, also used directly by the tests to seed vaults ----

        public static uint NextGuid()
        {
            return (uint)Interlocked.Increment(ref nextGuid);
        }

        public static Container MakeContainer(int itemsCapacity, int containersCapacity = 0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = AccountVaultStore.VaultContainerWcid,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Container },
                    { PropertyInt.ItemsCapacity, itemsCapacity },
                    { PropertyInt.ContainersCapacity, containersCapacity },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Backpack" },
                },
            };

            return new Container(weenie, new ObjectGuid(NextGuid()));
        }

        public static Stackable MakeStack(uint wcid, int stackSize, int maxStackSize)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    // A real weenie always declares an ItemType, and PersonalVendor.NeedsDisplayProxy
                    // reads it: an item with no ItemType at all is treated as unfilable by the client's
                    // vendor panel and is presented through a display proxy. A fake with the property
                    // missing therefore modelled the rarest case rather than the ordinary one.
                    { PropertyInt.ItemType, (int)ItemType.Misc },
                    { PropertyInt.StackSize, stackSize },
                    { PropertyInt.MaxStackSize, maxStackSize },
                    { PropertyInt.StackUnitEncumbrance, 1 },
                    { PropertyInt.StackUnitValue, 1 },
                    { PropertyInt.EncumbranceVal, stackSize },
                    { PropertyInt.Value, stackSize },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, $"Test Item {wcid}" },
                },
            };

            return new Stackable(weenie, new ObjectGuid(NextGuid()));
        }

        /// <summary>
        /// A salvage-bag-shaped object, for the read-time grouping rule.
        ///
        /// Two halves matter and neither is decoration. The weenie carries ItemType.TinkeringMaterial
        /// and MaxStackSize 1, which is what real salvage weenies 20980-21075 carry (verified against
        /// ace_world 2026-08-31: WeenieType 44 CraftTool, ItemType 0x40000000, MaxStackSize 1,
        /// MaxStructure 100). And the five per-instance properties a real bag gets from
        /// Player_Crafting.TryAddSalvage (Player_Crafting.cs:249-286) - Structure, ItemWorkmanship,
        /// NumItemsInMaterial, Value and the derived Name - are stamped ON THE INSTANCE and are absent
        /// from the weenie, exactly as they are in production. That absence is why nothing
        /// salvage-shaped can ever be pristine, and therefore why a bag is a STORED BIOTA rather than a
        /// ledger row - the only shape grouping applies to.
        /// </summary>
        /// <remarks>
        /// itemWorkmanship and numItemsInMaterial are NULLABLE so a test can seed a bag carrying
        /// neither - the shape the bucket key's "no workmanship at all" sentinel exists for, and the
        /// one a corrupted item must never be bucketed with.
        /// </remarks>
        public static Stackable MakeSalvageBag(uint wcid, int structure, int? itemWorkmanship, int? numItemsInMaterial, int value, string name = null)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStackSize, 1 },
                    { PropertyInt.StackUnitEncumbrance, 100 },
                    { PropertyInt.StackUnitValue, 0 },
                    { PropertyInt.EncumbranceVal, 100 },
                    { PropertyInt.MaxStructure, 100 },
                },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Salvage" },
                },
            };

            var bag = new Stackable(weenie, new ObjectGuid(NextGuid()));

            bag.Structure = (ushort)structure;
            bag.ItemWorkmanship = itemWorkmanship;
            bag.NumItemsInMaterial = numItemsInMaterial;
            bag.Value = value;
            bag.Name = name ?? $"Salvage ({structure})";

            return bag;
        }

        /// <summary>
        /// Container.InventoryLoaded has a private setter and is normally flipped by the async
        /// inventory load, which needs a live shard. Reflection is the only way to reach the R4 state
        /// from a unit test, and reaching it through the real property is the point: a test that
        /// modelled "unloaded" with its own flag would not be testing what the store reads.
        /// </summary>
        public static void SetInventoryLoaded(Container container, bool value)
        {
            var property = typeof(Container).GetProperty(nameof(Container.InventoryLoaded), BindingFlags.Public | BindingFlags.Instance);

            property.GetSetMethod(nonPublic: true).Invoke(container, new object[] { value });
        }
    }
}
