using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using log4net;

using ACE.Common.Extensions;
using ACE.Database;
using ACE.Server.Entity.AccountVault;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Mule Vendor: the VaultManager of DESIGN section 5. It owns the lifetime of
    /// <see cref="AccountVaultStore"/> instances and nothing else - every mutation, every
    /// authorization decision and the capacity cap live on the store, which is the authoritative half
    /// of the feature.
    ///
    /// One store per account, loaded on demand. A store is only ever a cache over the shard: dropping
    /// one discards no state, and the next <see cref="GetStore"/> rebuilds it from account_vault,
    /// account_vault_stack and the container biotas.
    /// </summary>
    public static class AccountVaultManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly ConcurrentDictionary<uint, AccountVaultStore> stores = new ConcurrentDictionary<uint, AccountVaultStore>();

        /// <summary>
        /// How often the idle sweep actually walks the store table. Ticked from the world heartbeat,
        /// so this is what keeps it from being O(stores) per tick.
        /// </summary>
        private const double SweepIntervalSeconds = 60.0;

        private static double nextSweepUnixTime;

        /// <summary>
        /// How often <see cref="Tick"/> retries a FAILED spawn-filter seed. Only reached while the seed
        /// has never succeeded, so on a healthy server this counter is never consulted twice.
        /// </summary>
        private const double SeedRetryIntervalSeconds = 60.0;

        private static double nextSeedRetryUnixTime;

        /// <summary>
        /// Seeds <see cref="AccountVaultSpawnFilter"/> from the whole account_vault table. Called once
        /// from Program's startup sequence, BEFORE anything can activate a landblock.
        ///
        /// This is the third and widest of the three mechanisms guarding risk R1 (DESIGN 7.2). A vault
        /// container's Location is a bookkeeping pointer that keeps the orphan purge away, and it also
        /// makes the container match every clause of GetDynamicObjectsByLandblock - so a landblock
        /// activation would otherwise put every account's vault into the live world as a lootable
        /// backpack. The filter's landblock predicate covers the landblocks account_vault_landblock
        /// names now and ships with; this covers a container created while it named a third one.
        ///
        /// A FAILED read is not treated as an empty table. The server still boots - refusing to boot
        /// over this would trade a contained risk for a certain outage, and the landblock predicate
        /// still stands on its own - but the failure is logged at ERROR and retried from
        /// <see cref="Tick"/> until it succeeds, so it can neither pass silently nor stay broken for
        /// the life of the process.
        /// </summary>
        public static void Initialize()
        {
            TrySeedSpawnFilter(shardBackend, "startup");
        }

        /// <summary>
        /// The production backend. Stateless - every member forwards to
        /// DatabaseManager.Shard.BaseDatabase - so one instance for the process is enough.
        /// </summary>
        private static readonly IAccountVaultBackend shardBackend = new ShardAccountVaultBackend();

        /// <summary>
        /// Takes its backend as a parameter rather than reaching for
        /// <see cref="DatabaseManager"/> itself, and is internal rather than private, for one reason:
        /// otherwise NOTHING executes this method in a test and it can be gutted - the Seed call
        /// dropped, the null check inverted, the wrong DAO method called - while every test stays green
        /// and the server boots in production with an unseeded filter. A source scan of Program.cs
        /// guards the call SITE; only this seam can guard the body.
        /// </summary>
        internal static bool TrySeedSpawnFilter(IAccountVaultBackend backend, string when)
        {
            List<uint> guids;

            try
            {
                guids = backend?.GetAllAccountVaultContainerGuids();
            }
            catch (Exception ex)
            {
                // The DAO already catches and returns null; this is the belt to its braces, because the
                // one thing that must not happen here is an exception escaping into the startup path.
                log.Error($"[VAULT] spawn filter seed ({when}) threw: {ex.GetFullMessage()}");
                guids = null;
            }

            // NULL means the read FAILED. It is never read as "no account owns a vault", which is what
            // an empty list means and is the ordinary state of a new shard.
            if (guids == null || !AccountVaultSpawnFilter.Seed(guids))
            {
                log.Error($"[VAULT] could not read account_vault to seed the vault spawn filter ({when}). The server is running with an UNSEEDED filter: vault containers are still kept out of the world in the reserved landblock and in any landblock this process has learned about, but a container created while account_vault_landblock named some other landblock would enter the world if that landblock activates. Retrying every {SeedRetryIntervalSeconds:N0} s.");
                return false;
            }

            log.Info($"[VAULT] vault spawn filter seeded ({when}) with {AccountVaultSpawnFilter.KnownCount} vault container guid(s).");
            return true;
        }

        /// <summary>
        /// The store for one account, created on first use.
        ///
        /// The returned store may not be ready yet: its vault containers load their inventories
        /// asynchronously, so callers check <see cref="AccountVaultStore.IsLoaded"/> before reading or
        /// mutating (risk R4). Returns null only for account id 0, which is not a real account.
        /// </summary>
        public static AccountVaultStore GetStore(uint accountId)
        {
            if (accountId == 0)
                return null;

            while (true)
            {
                var store = stores.GetOrAdd(accountId, id => new AccountVaultStore(id));

                if (!store.IsEvicted)
                    return store;

                // The sweep retired this one between our GetOrAdd and this read. Handing it back would
                // give the caller a store whose queue refuses everything; leaving it in the table would
                // make that permanent. Drop this exact instance - never whichever instance happens to
                // be there now - and go round again.
                stores.TryRemove(new KeyValuePair<uint, AccountVaultStore>(accountId, store));
            }
        }

        /// <summary>
        /// The store for one account ONLY if one is already live - never creates one, and skips a store
        /// the idle sweep has retired. For callers that must not start a load or build a store as a
        /// side effect of merely asking (/mule search all's warm check).
        /// </summary>
        public static bool TryGetExistingStore(uint accountId, out AccountVaultStore store)
        {
            store = null;

            if (accountId == 0)
                return false;

            if (!stores.TryGetValue(accountId, out var found) || found.IsEvicted)
                return false;

            store = found;
            return true;
        }

        /// <summary>
        /// Test-only: drops one account's store from the process-wide table.
        ///
        /// It exists because <see cref="stores"/> is process-static and the test host runs every test
        /// class in ONE process. A test that calls the real <see cref="GetStore"/> therefore leaves a
        /// store behind that is backed by the real DatabaseManager.Shard, and anything that later walks
        /// the table - the idle sweep, the warm queue - will find it and act on it in some unrelated
        /// test's run. That is the same failure class as PropertyManager's static caches, through a
        /// different static, and it has already cost one CI failure: a leftover store from a concurrency
        /// test was picked up by the (since retired) background fold rotation during a later WorldManager
        /// startup test, which read a shard schema CI does not create and logged at ERROR.
        ///
        /// Returns whether a store was actually removed, so a test can assert it cleaned up the thing
        /// it thought it had created.
        /// </summary>
        internal static bool ForgetStoreForTest(uint accountId)
        {
            return stores.TryRemove(accountId, out _);
        }

        /// <summary>
        /// Every grant naming this character as grantee, ordered by owner account. NULL means the read
        /// FAILED, never "nothing is shared" - see IAccountVaultBackend's read contract.
        /// </summary>
        public static List<ACE.Database.Models.Shard.AccountVaultGrant> GetGrantsForGrantee(uint granteeCharacterGuid)
        {
            return shardBackend.GetAccountVaultGrantsForGrantee(granteeCharacterGuid);
        }

        #region Warm queue (/mule search all)

        /// <summary>
        /// Most accounts the warm queue holds at once. A request past this is dropped, not queued - the
        /// player simply sees "still loading" once more and asks again.
        /// </summary>
        internal const int WarmQueueCapacity = 64;

        /// <summary>
        /// Minimum spacing between two warm-ups, server-wide. A warm-up is a synchronous shard load on
        /// the world heartbeat (index, ledger, one biota per vault - AccountVaultStore.IsLoaded), so
        /// this bounds how much of any one tick a burst of /mule search all can spend on them.
        /// </summary>
        internal const double WarmIntervalSeconds = 0.25;

        private static readonly object warmLock = new object();
        private static readonly Queue<uint> warmQueue = new Queue<uint>();
        private static readonly HashSet<uint> warmQueued = new HashSet<uint>();
        private static double nextWarmUnixTime;

        /// <summary>
        /// Asks for this account's store to be loaded soon, off the caller's path. Deduplicated (an
        /// account already queued is not queued twice) and capped at <see cref="WarmQueueCapacity"/>.
        /// Returns whether the account is now queued.
        /// </summary>
        public static bool RequestWarm(uint accountId)
        {
            if (accountId == 0)
                return false;

            lock (warmLock)
            {
                if (warmQueued.Contains(accountId))
                    return true;

                if (warmQueue.Count >= WarmQueueCapacity)
                    return false;

                warmQueue.Enqueue(accountId);
                warmQueued.Add(accountId);
                return true;
            }
        }

        /// <summary>Queued warm-ups, for tests and admin readouts.</summary>
        internal static int WarmQueueCount
        {
            get { lock (warmLock) return warmQueue.Count; }
        }

        /// <summary>Test-only reset of the process-wide warm queue and its rate limit.</summary>
        internal static void ResetWarmQueueForTest()
        {
            lock (warmLock)
            {
                warmQueue.Clear();
                warmQueued.Clear();
                nextWarmUnixTime = 0;
            }
        }

        /// <summary>
        /// Warms at most ONE queued store, and only when <see cref="WarmIntervalSeconds"/> has passed
        /// since the last one. Returns the account warmed, or 0 when nothing ran.
        ///
        /// <paramref name="storeFor"/> is the seam that lets a test drive this with fake-backed stores;
        /// production passes <see cref="GetStore"/>. Reading IsLoaded is what performs the load. Every
        /// warm-up runs inside its own try/catch: this is called from the world heartbeat, and a throw
        /// escaping here would skip the idle sweep that follows it for every other account.
        /// </summary>
        internal static uint DrainWarmQueue(double currentUnixTime, Func<uint, AccountVaultStore> storeFor)
        {
            uint accountId;

            lock (warmLock)
            {
                if (warmQueue.Count == 0 || currentUnixTime < nextWarmUnixTime)
                    return 0;

                accountId = warmQueue.Dequeue();
                warmQueued.Remove(accountId);
                nextWarmUnixTime = currentUnixTime + WarmIntervalSeconds;
            }

            try
            {
                var store = storeFor(accountId);

                if (store != null)
                    _ = store.IsLoaded;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] warm-up of account {accountId}'s store threw: {ex.GetFullMessage()}");
            }

            return accountId;
        }

        #endregion

        /// <summary>
        /// Recomputes the cached AugmentationMuleSpace and /mule upgrade bonuses on this account's
        /// store, if one is loaded. Called by the Custom Dreamweave Augmentation broker after a
        /// MuleSpace trade, and by CapacityUpgradeBroker after a /mule upgrade purchase, so the extra
        /// entries apply to the current session rather than the next store load.
        ///
        /// Deliberately does NOT use GetStore: creating a store for an account that has never opened its
        /// vault would start an index load and a container rehydrate for no reason, and the store that
        /// account eventually creates computes both bonuses in its own constructor anyway.
        ///
        /// Must not be called while holding a store's stateLock - see
        /// <see cref="AccountVaultStore.RefreshCapacityBonuses"/> for why.
        /// </summary>
        public static void RefreshCapacityBonuses(uint accountId)
        {
            if (accountId == 0)
                return;

            if (stores.TryGetValue(accountId, out var store))
                store.RefreshCapacityBonuses();
        }

        /// <summary>
        /// Resolves a character name to the account that owns it, for the sharing commands.
        ///
        /// Works for an offline character as well as an online one: PlayerManager keeps both in one
        /// name index. False when the name is unknown or the owning account cannot be determined.
        /// </summary>
        public static bool TryResolveAccountForCharacter(string characterName, out uint accountId, out uint characterGuid)
        {
            return TryResolveCharacter(characterName, out characterGuid, out _, out accountId) && accountId != 0;
        }

        /// <summary>
        /// The fuller form the store needs: guid, canonical name as spelled by the game, and the
        /// owning account.
        ///
        /// The account id is read shard-side off Character.AccountId rather than through an auth
        /// lookup (DESIGN section 6), with the IPlayer's own resolved Account as the fallback.
        /// </summary>
        internal static bool TryResolveCharacter(string characterName, out uint characterGuid, out string canonicalName, out uint accountId)
        {
            characterGuid = 0;
            canonicalName = null;
            accountId = 0;

            if (string.IsNullOrWhiteSpace(characterName))
                return false;

            var player = PlayerManager.FindByName(characterName.Trim());

            if (player == null)
                return false;

            characterGuid = player.Guid.Full;
            canonicalName = player.Name;

            try
            {
                var stub = DatabaseManager.Shard.BaseDatabase.GetCharacterStubByGuid(characterGuid);

                if (stub != null)
                    accountId = stub.AccountId;
            }
            catch (Exception ex)
            {
                log.Error($"[VAULT] could not read the character stub for 0x{characterGuid:X8}: {ex.GetFullMessage()}");
            }

            if (accountId == 0)
                accountId = player.Account?.AccountId ?? 0;

            return true;
        }

        /// <summary>
        /// Drops stores that no window is looking at and that have no work in flight, once they have
        /// sat untouched for <see cref="AccountVaultStore.IdleEvictionSeconds"/>.
        ///
        /// Every guard lives inside <see cref="AccountVaultStore.TryEvict"/>, not here, and that is
        /// deliberate rather than tidiness. Checked from out here they raced: a caller that obtained a
        /// store reference just before the sweep and enqueued just after would mutate a store this
        /// method had already dropped, while the next GetStore built a second one - two mutation queues
        /// over one account, which is R3 reopened. The store decides and marks itself retired under the
        /// same lock its Enqueue takes, so exactly one of the two wins and the other sees it.
        ///
        /// Dropping a store loses nothing: it owns no state the shard does not, and the next GetStore
        /// rebuilds it. The guards exist so that the rebuild does not land in the middle of a player's
        /// transaction and answer "still loading".
        /// </summary>
        public static void Tick(double currentUnixTime)
        {
            // Only ever true on a server whose startup seed failed. Self-heals a transient shard outage
            // rather than leaving the widest R1 guard off for the life of the process.
            if (!AccountVaultSpawnFilter.IsSeeded && currentUnixTime >= nextSeedRetryUnixTime)
            {
                nextSeedRetryUnixTime = currentUnixTime + SeedRetryIntervalSeconds;

                TrySeedSpawnFilter(shardBackend, "retry");
            }

            // /mule search all's warm-ups. Before the sweep's early return, so they run every heartbeat
            // rather than once a minute; self-rate-limited to one store per WarmIntervalSeconds.
            DrainWarmQueue(currentUnixTime, GetStore);

            // NOTHING FOLDS FROM HERE ANY MORE, and that is deliberate rather than an omission.
            //
            // A background fold rotation used to run on this heartbeat, one store per second, to drift
            // already-stored biotas into counted class rows. It was removed once the migration it existed
            // to perform had finished on prod (2026-09-26, 132 of 132 accounts, 15,901 items folded): from
            // then on it was permanent world-loop cost for work that cannot come back, because the deposit
            // path collapses a classifiable item on arrival and a failed class withdraw refunds the class
            // row rather than materializing a biota.
            //
            // The residual cases - a deposit taken while account_vault_class_storage was false, a barrel
            // restore of a pre-migration bag, a shard restored from a pre-migration backup - are answered
            // by an operator re-running /vaultclassfold, which is re-runnable and idempotent. See
            // AccountVaultFoldMigration and Runbook section 16.
            if (currentUnixTime < nextSweepUnixTime)
                return;

            nextSweepUnixTime = currentUnixTime + SweepIntervalSeconds;

            foreach (var kvp in stores)
            {
                if (kvp.Value.TryEvict(currentUnixTime))
                    stores.TryRemove(kvp);
            }
        }

        /// <summary>Store count, for admin readouts and tests. Not part of any gameplay path.</summary>
        public static int StoreCount => stores.Count;
    }
}
