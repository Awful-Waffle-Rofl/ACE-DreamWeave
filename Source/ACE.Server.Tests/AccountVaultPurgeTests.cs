using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

// The EF entity, which is the shape GetDynamicObjectsByLandblock returns and the shape the spawn
// filter works on - never the runtime ACE.Entity.Models.Biota.
using ShardBiota = ACE.Database.Models.Shard.Biota;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Mule Vendor risk R1: PurgeOrphanedBiotas deletes any biota with no Container, Wielder or
    /// Location, and separately deletes children whose parent container is missing - so a purged vault
    /// takes every item in it with it. A vault container has no Container and no Wielder.
    ///
    /// Two independent mitigations exist and BOTH are asserted separately here. A single test on the
    /// combined outcome would pass with one guard silently removed, and that is precisely the failure
    /// mode being defended against: mitigation 1 can be undone by a content edit, mitigation 2 by a
    /// merge from upstream. Severity is catastrophic with no recovery, and the purge ships disabled,
    /// so this stays dormant until an admin enables it years later.
    ///
    /// Neither assertion may be satisfiable by PROSE. That is not a style preference: an earlier
    /// revision of this file asserted only that AccountVaultStore.cs CONTAINED the string
    /// "account_vault_landblock", which a hook comment naming the config key already satisfied on code
    /// that assigned no Location at all. A guard a comment can pass is the silent-removal failure it
    /// was written to prevent. So mitigation 1 is asserted BEHAVIOURALLY - a real store, driven through
    /// its two seams, must put a real Location in the configured landblock on a real container, before
    /// the save - and mitigation 2, which needs a live shard database to exercise, is asserted against
    /// the source with every comment stripped out first.
    /// </summary>
    [TestClass]
    public class AccountVaultPurgeTests
    {
        private const uint OwnerAccount = 4101;
        private const uint OwnerCharacter = 0x50000011;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Vaultowner");

        /// <summary>
        /// Restored in cleanup. PropertyManager's cache is process-wide static state and MSTest runs
        /// this assembly single-threaded, so a test that leaves a non-default landblock behind would
        /// hand it to whatever runs next.
        /// </summary>
        private long savedLandblock;

        /// <summary>Restored in cleanup for the same reason. Nothing here deposits near a cap.</summary>
        private long savedEntryCap;

        [TestInitialize]
        public void Setup()
        {
            // The registered defaults rather than PropertyManager.GetLong: an uncached GetLong falls
            // through to DatabaseManager.ShardConfig, which no unit test has. Reading them also asserts
            // both keys are registered, since an unregistered key is exactly what would send GetLong to
            // the database.
            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("account_vault_entry_cap"),
                "account_vault_entry_cap is missing from DefaultLongProperties");

            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey("account_vault_landblock"),
                "account_vault_landblock is missing from DefaultLongProperties");

            savedEntryCap = DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item;
            savedLandblock = DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", savedEntryCap));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock", savedLandblock));
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyLong("account_vault_landblock", savedLandblock);
            PropertyManager.ModifyLong("account_vault_entry_cap", savedEntryCap);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Database by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        // ---------------------------------------------------------------- mitigation 1

        [TestMethod]
        public void Mitigation1_StoreGivesEveryVaultContainerALocation()
        {
            // Two DIFFERENT configured landblocks, checked one after the other. One value alone could
            // be passed by a hard-coded constant; a comment naming the config key passes neither. Only
            // code that actually reads account_vault_landblock and actually assigns container.Location
            // satisfies both rounds.
            AssertNewVaultContainerIsLocatedIn(0x4242);
            AssertNewVaultContainerIsLocatedIn(0x1357);
        }

        private static void AssertNewVaultContainerIsLocatedIn(ushort landblock)
        {
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock", landblock),
                "account_vault_landblock is missing from DefaultLongProperties");

            var backend = new FakeVaultBackend();
            var inner = new FakeVaultWorld();

            // Control: a container straight off the world source has NO Location, so any Location seen
            // below provably came from the store rather than from the weenie or the fake. Without this,
            // a fake that happened to supply one would make the whole test vacuous.
            var bare = inner.CreateNewWorldObject(AccountVaultStore.VaultContainerWcid);
            Assert.IsNull(bare.Location, "a freshly created vault container must start with no Location, or this test proves nothing");

            inner.CreatedContainers.Clear();

            var world = new LocationRecordingWorld(inner);

            var store = new AccountVaultStore(OwnerAccount, backend, world);

            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(FakeVaultWorld.MakeStack(1234, 1, 100), Owner, out reason));

            Assert.IsTrue(ok, reason);

            var container = inner.CreatedContainers.Single();

            Assert.IsNotNull(container.Location,
                "a vault container with no Location is exactly what the orphan purge's keep-test deletes");

            Assert.AreEqual(landblock, container.Location.LandblockId.Landblock,
                "the Location must sit in the landblock named by account_vault_landblock, read from config rather than hard-coded");

            Assert.AreNotEqual(0u, container.Location.Cell & 0xFFFFu,
                "the stored ObjCellId must be the cell the store asked for, not one the Position constructor derived: a low word of 0 sends the constructor through SetPosition, which happens to derive the same landblock and the same cell today but is not what we asked it to store");

            // Ordering. The Location must already be on the object when the biota save is enqueued, or
            // the persisted row carries no Location and the guard exists only in memory - which is
            // useless, because the purge reads the database and not the running server.
            Assert.IsTrue(world.FirstSavedLocations.TryGetValue(container.Guid.Full, out var atFirstSave),
                "the new vault container's biota was never saved");

            Assert.IsNotNull(atFirstSave,
                "the Location must be assigned BEFORE the container's biota is saved, not after it");

            Assert.AreEqual(landblock, atFirstSave.LandblockId.Landblock);
        }

        /// <summary>
        /// Records what every object's Location was at the moment of its FIRST save, then delegates
        /// everything to the ordinary fake. Only the first save is kept, because a later save picking
        /// the Location up would prove nothing about the one save that races the account_vault index
        /// row.
        /// </summary>
        private class LocationRecordingWorld : IAccountVaultWorldSource
        {
            private readonly FakeVaultWorld inner;

            public readonly Dictionary<uint, Position> FirstSavedLocations = new Dictionary<uint, Position>();

            public LocationRecordingWorld(FakeVaultWorld inner) => this.inner = inner;

            public WorldObject CreateNewWorldObject(uint weenieClassId) => inner.CreateNewWorldObject(weenieClassId);

            public VaultContainerLoad LoadContainer(uint containerGuid, out Container container) => inner.LoadContainer(containerGuid, out container);

            public bool IsPristine(WorldObject item) => inner.IsPristine(item);

            public void SaveBiota(WorldObject worldObject, Action<bool> callback = null)
            {
                if (worldObject != null)
                {
                    lock (FirstSavedLocations)
                    {
                        if (!FirstSavedLocations.ContainsKey(worldObject.Guid.Full))
                        {
                            var location = worldObject.Location;

                            FirstSavedLocations[worldObject.Guid.Full] = location == null ? null : new Position(location);
                        }
                    }
                }

                inner.SaveBiota(worldObject, callback);
            }

            public void DestroyItem(WorldObject item) => inner.DestroyItem(item);

            public bool TryResolveCharacter(string characterName, out uint characterGuid, out string canonicalName, out uint accountId)
                => inner.TryResolveCharacter(characterName, out characterGuid, out canonicalName, out accountId);
        }

        // ------------------------------------------------- mitigation 1, residual half

        /// <summary>
        /// The Location that keeps the orphan purge away is ALSO what makes a vault container match
        /// every clause of GetDynamicObjectsByLandblock. Without the spawn filter, activating the
        /// reserved landblock puts every account's vault into the world as a lootable backpack.
        /// </summary>
        [TestMethod]
        public void ReservedLandblockActivation_SpawnsNothing()
        {
            var reserved = (ushort)DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock", reserved));

            var dynamics = new List<ShardBiota>
            {
                new ShardBiota { Id = 0x80000101 },
                new ShardBiota { Id = 0x80000102 },
            };

            // Control: an ordinary landblock is untouched, and the untouched case hands back the SAME
            // list instance. A filter that dropped everything everywhere would pass the assertion below
            // and break every landblock on the server.
            var elsewhere = AccountVaultSpawnFilter.Filter(0x00AB, 0, dynamics);
            Assert.AreSame(dynamics, elsewhere, "a landblock that is not reserved must be handed back untouched");

            var kept = AccountVaultSpawnFilter.Filter(reserved, 0, dynamics);
            Assert.AreEqual(0, kept.Count, "nothing in the reserved landblock may enter the world");

            // A config change leaves already-created containers behind in the previous landblock, so the
            // registered default stays reserved alongside whatever is configured now.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock", 0xA9B4));

            Assert.AreEqual(0, AccountVaultSpawnFilter.Filter(0xA9B4, 0, dynamics).Count,
                "the currently configured landblock must be reserved");

            Assert.AreEqual(0, AccountVaultSpawnFilter.Filter(reserved, 0, dynamics).Count,
                "the registered default must STAY reserved after a config change, or containers created before it are exposed");
        }

        /// <summary>
        /// The narrower predicate: a container this process knows about is kept out of the world in ANY
        /// landblock, which is what covers a Location moved by a careless content edit.
        /// </summary>
        [TestMethod]
        public void KnownVaultContainer_IsSuppressedOutsideTheReservedLandblock()
        {
            const uint vaultGuid = 0x80000201;
            const uint ordinaryGuid = 0x80000202;
            const ushort liveLandblock = 0x00AB;

            var dynamics = new List<ShardBiota>
            {
                new ShardBiota { Id = vaultGuid },
                new ShardBiota { Id = ordinaryGuid },
            };

            // Control: before registration nothing is suppressed, so the assertion below is about the
            // registry and not about the landblock.
            Assert.AreSame(dynamics, AccountVaultSpawnFilter.Filter(liveLandblock, 0, dynamics),
                "an unregistered guid in an ordinary landblock must not be suppressed");

            AccountVaultSpawnFilter.Register(vaultGuid);

            try
            {
                var kept = AccountVaultSpawnFilter.Filter(liveLandblock, 0, dynamics);

                Assert.AreEqual(1, kept.Count, "only the vault container may be suppressed");
                Assert.AreEqual(ordinaryGuid, kept[0].Id, "an ordinary dynamic object must still enter the world");
            }
            finally
            {
                AccountVaultSpawnFilter.Unregister(vaultGuid);
            }

            Assert.AreSame(dynamics, AccountVaultSpawnFilter.Filter(liveLandblock, 0, dynamics),
                "unregistering must free the guid again, or a recycled guid stays suppressed forever");
        }

        /// <summary>
        /// The gap neither other predicate can see, and the one the startup seed exists for: an admin
        /// points account_vault_landblock at a live landblock A, containers are created there, and the
        /// admin later sets it back. Those containers are now in neither the configured landblock nor
        /// the registered default, and the incremental registry only knows accounts this process has
        /// touched - which landblock activation normally precedes.
        /// </summary>
        [TestMethod]
        public void SeedFromTheWholeTable_CoversContainersInALandblockThatIsNoLongerReserved()
        {
            const ushort strandedLandblock = 0x00CD;

            var reserved = (ushort)DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock", reserved));
            Assert.AreNotEqual(reserved, strandedLandblock, "the stranded landblock must not be a reserved one, or this test proves nothing");

            // Several accounts, because the seed is a whole-table read and a per-account one would pass
            // a single-account test.
            var backend = new FakeVaultBackend();

            var stranded = new uint[] { 0x80000301, 0x80000302, 0x80000303 };
            var accounts = new uint[] { 5001, 5002, 5002 };

            for (var i = 0; i < stranded.Length; i++)
            {
                backend.Vaults.Add(new ShardAccountVault
                {
                    Id = (uint)(6100 + i),
                    AccountId = accounts[i],
                    ContainerGuid = stranded[i],
                    CreatedAt = new DateTime(2026, 1, 1).AddMinutes(i),
                });
            }

            var dynamics = stranded.Select(id => new ShardBiota { Id = id }).ToList();

            // Control: unseeded, in a landblock that is not reserved, every one of them would enter the
            // world. This is the defect, reproduced.
            Assert.AreSame(dynamics, AccountVaultSpawnFilter.Filter(strandedLandblock, 0, dynamics),
                "without the seed these containers are not suppressed anywhere, which is the gap being closed");

            var guids = backend.GetAllAccountVaultContainerGuids();

            Assert.IsNotNull(guids, "the fake backend read must succeed here");
            Assert.AreEqual(stranded.Length, guids.Count, "the seed must read the WHOLE table, across every account");

            Assert.IsTrue(AccountVaultSpawnFilter.Seed(guids));

            try
            {
                Assert.AreEqual(0, AccountVaultSpawnFilter.Filter(strandedLandblock, 0, dynamics).Count,
                    "after seeding, a stranded vault container must be suppressed in whatever landblock it sits in");

                // And in an unrelated third landblock, because the guid predicate is landblock-blind by
                // design.
                Assert.AreEqual(0, AccountVaultSpawnFilter.Filter(0x0011, 0, dynamics).Count);

                // Control: seeding must not have made the filter suppress everything everywhere.
                var innocent = new List<ShardBiota> { new ShardBiota { Id = 0x80000399 } };
                Assert.AreSame(innocent, AccountVaultSpawnFilter.Filter(strandedLandblock, 0, innocent),
                    "an ordinary dynamic object must still enter the world after a seed");
            }
            finally
            {
                foreach (var id in stranded)
                    AccountVaultSpawnFilter.Unregister(id);
            }
        }

        /// <summary>
        /// NULL from the seed read means the read FAILED; EMPTY means no account owns a vault. Treating
        /// a failure as an empty table is the silent version of the failure this whole class prevents,
        /// because both leave the filter looking identically healthy.
        /// </summary>
        [TestMethod]
        public void SeedNullRead_IsNotTreatedAsAnEmptyTable()
        {
            const uint strandedGuid = 0x80000401;

            var backend = new FakeVaultBackend();

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 6200,
                AccountId = 5003,
                ContainerGuid = strandedGuid,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            backend.FailVaultRead = true;

            var failed = backend.GetAllAccountVaultContainerGuids();

            Assert.IsNull(failed, "a failed read must report null, never an empty list");
            Assert.IsFalse(AccountVaultSpawnFilter.Seed(failed), "a null read must be refused, not seeded");
            Assert.IsFalse(AccountVaultSpawnFilter.IsKnownVaultContainer(strandedGuid),
                "a refused seed must register nothing");

            // An EMPTY table is a successful seed, not a failure. This is the other half of the
            // distinction and the case a new shard is actually in.
            Assert.IsTrue(AccountVaultSpawnFilter.Seed(new FakeVaultBackend().GetAllAccountVaultContainerGuids()),
                "an empty account_vault is a successful seed; only a failed read is a failure");

            // Control: with the read working, the same guid IS learned - so the assertion above is about
            // the null and not about the guid being unreachable.
            backend.FailVaultRead = false;

            Assert.IsTrue(AccountVaultSpawnFilter.Seed(backend.GetAllAccountVaultContainerGuids()));

            try
            {
                Assert.IsTrue(AccountVaultSpawnFilter.IsKnownVaultContainer(strandedGuid));
            }
            finally
            {
                AccountVaultSpawnFilter.Unregister(strandedGuid);
            }
        }

        /// <summary>
        /// Executes the PRODUCTION seed path - AccountVaultManager.TrySeedSpawnFilter over a backend -
        /// rather than calling AccountVaultSpawnFilter.Seed directly.
        ///
        /// The distinction is the whole point of this test. Every other seed assertion here calls Seed
        /// itself and so proves nothing about the method Program actually invokes: someone could drop
        /// the Seed call from TrySeedSpawnFilter, invert its null check or point it at the wrong DAO
        /// method, leave the call-site text in Program.cs untouched, and watch every test stay green
        /// while the server boots with an unseeded filter.
        ///
        /// Note on IsSeeded: it is a process-wide latch that never goes false again, and MSTest gives
        /// no cross-class ordering guarantee, so "stays false" is asserted as "does not CHANGE", which
        /// is the same property in the only form that is sound however the runner orders these.
        /// </summary>
        [TestMethod]
        public void ProductionSeedPath_ReadsTheBackend_AndRefusesAFailedRead()
        {
            const uint guidA = 0x80000601;
            const uint guidB = 0x80000602;

            var backend = new FakeVaultBackend();

            foreach (var (guid, account, order) in new[] { (guidA, 5101u, 0), (guidB, 5102u, 1) })
            {
                backend.Vaults.Add(new ShardAccountVault
                {
                    Id = (uint)(6300 + order),
                    AccountId = account,
                    ContainerGuid = guid,
                    CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
                });
            }

            Assert.IsFalse(AccountVaultSpawnFilter.IsKnownVaultContainer(guidA), "control: these guids must be unknown before the seed");
            Assert.IsFalse(AccountVaultSpawnFilter.IsKnownVaultContainer(guidB));

            try
            {
                // 1. A FAILED read. The method must report false and register nothing, and must not
                //    move the seeded latch in either direction.
                var seededBefore = AccountVaultSpawnFilter.IsSeeded;
                var countBefore = AccountVaultSpawnFilter.KnownCount;

                backend.FailVaultRead = true;

                Assert.IsFalse(AccountVaultManager.TrySeedSpawnFilter(backend, "test-failed-read"),
                    "a null read must be refused by the production path, not treated as an empty table");

                Assert.AreEqual(seededBefore, AccountVaultSpawnFilter.IsSeeded,
                    "a failed seed must not change the seeded flag in either direction");

                Assert.AreEqual(countBefore, AccountVaultSpawnFilter.KnownCount, "a refused seed must register nothing");
                Assert.IsFalse(AccountVaultSpawnFilter.IsKnownVaultContainer(guidA));

                // 2. A read that THROWS. The startup path must swallow it the same way, because an
                //    exception escaping Initialize would abort the boot over a contained risk.
                backend.FailVaultRead = false;
                backend.ThrowOnVaultRead = true;

                Assert.IsFalse(AccountVaultManager.TrySeedSpawnFilter(backend, "test-throwing-read"),
                    "a throwing read must be caught and reported as a failed seed, never allowed to escape");

                Assert.IsFalse(AccountVaultSpawnFilter.IsKnownVaultContainer(guidA));

                // 3. The RETRY recovering. Same backend, failure cleared - which is exactly what
                //    AccountVaultManager.Tick does after a failed startup seed.
                backend.ThrowOnVaultRead = false;

                Assert.IsTrue(AccountVaultManager.TrySeedSpawnFilter(backend, "test-retry"),
                    "the retry must succeed once the backend recovers");

                Assert.IsTrue(AccountVaultSpawnFilter.IsSeeded, "a successful seed must latch IsSeeded");

                Assert.IsTrue(AccountVaultSpawnFilter.IsKnownVaultContainer(guidA),
                    "the production path must register every guid the backend returned");

                Assert.IsTrue(AccountVaultSpawnFilter.IsKnownVaultContainer(guidB));

                Assert.AreEqual(countBefore + 2, AccountVaultSpawnFilter.KnownCount,
                    "the production path must register exactly the two guids the backend held");

                // 4. And an EMPTY table is a success, not a failure - the ordinary state of a new shard.
                Assert.IsTrue(AccountVaultManager.TrySeedSpawnFilter(new FakeVaultBackend(), "test-empty"),
                    "an empty account_vault is a successful seed");
            }
            finally
            {
                AccountVaultSpawnFilter.Unregister(guidA);
                AccountVaultSpawnFilter.Unregister(guidB);
            }
        }

        /// <summary>
        /// Seed MERGES, it never replaces. A later "simplification" to
        /// knownContainers = new HashSet&lt;uint&gt;(containerGuids) compiles and passes every other test
        /// here, while silently discarding every guid Register learned - which on a live server is the
        /// vault containers created between boot and the retry that finally succeeded.
        /// </summary>
        [TestMethod]
        public void Seed_MergesIntoTheRegistry_AndNeverReplacesIt()
        {
            const uint registered = 0x80000701;
            const uint seeded = 0x80000702;

            AccountVaultSpawnFilter.Register(registered);

            try
            {
                Assert.IsTrue(AccountVaultSpawnFilter.IsKnownVaultContainer(registered), "control: Register must work at all");

                Assert.IsTrue(AccountVaultSpawnFilter.Seed(new[] { seeded }));

                Assert.IsTrue(AccountVaultSpawnFilter.IsKnownVaultContainer(registered),
                    "Seed must MERGE: a guid learned from Register must survive a later seed, or a retry after a failed startup read discards every vault created in between");

                Assert.IsTrue(AccountVaultSpawnFilter.IsKnownVaultContainer(seeded));
            }
            finally
            {
                AccountVaultSpawnFilter.Unregister(registered);
                AccountVaultSpawnFilter.Unregister(seeded);
            }
        }

        /// <summary>
        /// The seed is only a guard if startup runs it, and runs it before anything can activate a
        /// landblock. Source-level, with comments stripped, for the same reason mitigation 2 is.
        ///
        /// Kept alongside ProductionSeedPath_ReadsTheBackend_AndRefusesAFailedRead rather than replaced
        /// by it: that test proves the METHOD works, this one proves Program CALLS it and calls it early
        /// enough. Neither covers the other.
        /// </summary>
        [TestMethod]
        public void StartupSeedsTheSpawnFilterBeforeAnyLandblockCanActivate()
        {
            var code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Server", "Program.cs")));

            var iSeed = code.IndexOf("AccountVaultManager.Initialize()", StringComparison.Ordinal);

            Assert.IsTrue(iSeed >= 0,
                "Program must seed the vault spawn filter at startup, or the widest R1 guard is never loaded");

            foreach (var later in new[] { "WorldManager.Initialize()", "SocketManager.Initialize()" })
            {
                var iLater = code.IndexOf(later, StringComparison.Ordinal);

                Assert.IsTrue(iLater > iSeed,
                    $"the vault spawn filter must be seeded before {later}; a landblock that activates first would spawn every account's vault");
            }
        }

        /// <summary>
        /// The spawn filter must NEVER delete what it drops - that is live player property, and this is
        /// the one place it differs from WorldEventOrphanFilter, which it is otherwise modelled on.
        /// Asserted against the source with comments stripped, because "make it consistent with the
        /// filter above it" is a plausible and catastrophic tidy-up.
        ///
        /// Fix round A, A9: scoped to the AccountVaultSpawnFilter class body, and renamed to match what
        /// it actually asserts. It previously scanned the WHOLE of AccountVaultStore.cs for the literal
        /// "RemoveBiota" under the name SpawnFilter_NeverDeletesWhatItDrops - a claim about the file
        /// that is simply false, since DepositToLedger calls world.DestroyItem(item) and that reaches
        /// RemoveBiotaFromDatabase through WorldObject.Destroy. It passed only because the identifier
        /// itself does not appear in the file, so it was a check on spelling rather than on behaviour,
        /// and any correct future deletion helper named RemoveBiota* anywhere in the file would have
        /// turned it red for no reason.
        /// </summary>
        [TestMethod]
        public void SpawnFilter_Filter_NeverCallsRemoveBiota()
        {
            var code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Server", "Entity", "AccountVault", "AccountVaultStore.cs")));

            var iFilter = code.IndexOf("public static class AccountVaultSpawnFilter", StringComparison.Ordinal);

            Assert.IsTrue(iFilter >= 0,
                "AccountVaultSpawnFilter was not found in AccountVaultStore.cs - has it moved? This test must follow it, not silently scan nothing.");

            var filterBody = code.Substring(iFilter);

            Assert.IsFalse(filterBody.Contains("RemoveBiota"),
                "the vault spawn filter must never delete a biota; what it drops stays in the shard and stays reachable through the vendor");
        }

        /// <summary>
        /// The filter is only a guard if the landblock actually calls it, and calls it on the list it
        /// then spawns. Source-level, with comments stripped, for the same reason mitigation 2 is.
        /// </summary>
        [TestMethod]
        public void LandblockCallsTheSpawnFilterBeforeSpawning()
        {
            var code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "Source", "ACE.Server", "Entity", "Landblock.cs")));

            var iFilter = code.IndexOf("AccountVaultSpawnFilter.Filter(", StringComparison.Ordinal);
            var iCreate = code.IndexOf("WorldObjectFactory.CreateWorldObjects(dynamics)", StringComparison.Ordinal);

            Assert.IsTrue(iFilter >= 0,
                "SpawnDynamicShardObjects must run the vault spawn filter, or activating the reserved landblock spawns every account's vault");

            Assert.IsTrue(iCreate > iFilter,
                "the filter must run BEFORE the biotas are turned into world objects; filtering afterwards filters nothing");

            StringAssert.Contains(code, "dynamics = AccountVault.AccountVaultSpawnFilter.Filter(",
                "the filtered list must be assigned back, or the call is a no-op");
        }

        // ---------------------------------------------------------------- mitigation 2

        [TestMethod]
        public void Mitigation2_PurgeKeepTestExemptsVaultContainers()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Database", "ShardDatabaseOfflineTools.cs");

            var raw = File.ReadAllText(path);
            var code = StripComments(raw);

            // Control. If the stripper stopped stripping, every assertion below could be satisfied by a
            // comment, which is the exact defect this file was rewritten to close.
            StringAssert.Contains(raw, "redundant second half of DESIGN 7.2",
                "the exemption's own comment is the control for the stripper; keep it or replace this control");

            Assert.IsFalse(code.Contains("redundant second half of DESIGN 7.2"),
                "comment stripping is not working, so these assertions could be satisfied by prose");

            StringAssert.Contains(code, "vaultContainers.Contains(",
                "the orphan purge keep-test must exempt vault containers independently of their Location");

            StringAssert.Contains(code, "context.AccountVault",
                "the exemption set must be built from the account_vault index rather than from a hard-coded list");

            StringAssert.Contains(code, "ContainerGuid",
                "the exemption set must be keyed on account_vault.container_Guid, which is what the purge compares against biota ids");

            // Placement. Every assertion above still passes with the exemption moved below
            // results.Add, or into another method in this file, at which point it is dead code that
            // deletes every vault anyway. A carelessly resolved upstream merge conflict is exactly how
            // that happens, and it is the threat mitigation 2 exists for.
            var iExempt = code.IndexOf("vaultContainers.Contains(", StringComparison.Ordinal);
            var iForeach = code.IndexOf("foreach (var kvp in biotas)", StringComparison.Ordinal);
            var iAdd = code.IndexOf("results.Add(kvp.Key)", StringComparison.Ordinal);

            Assert.IsTrue(iForeach >= 0 && iExempt > iForeach && iExempt < iAdd,
                "the exemption must sit inside the keep-test loop and BEFORE results.Add; an exemption after the add is not an exemption");
        }

        // ---------------------------------------------------------------- dormancy

        [TestMethod]
        public void PurgeStillShipsDisabled()
        {
            // Not a guard, a reminder: this whole risk is dormant only while the flag is false. If this
            // assertion ever fails, both mitigations above must be verified live before the change lands.
            //
            // BOTH files, because Program_Setup scaffolds Config.js.docker rather than Config.js.example
            // when it detects a container, and stage and prod both run in containers - so the example
            // file alone says nothing about what a deployed server actually runs with.
            foreach (var name in new[] { "Config.js.example", "Config.js.docker" })
            {
                var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", name);

                Assert.IsTrue(File.Exists(path), $"{name} is missing; the purge dormancy guard has nothing to read");

                StringAssert.Contains(File.ReadAllText(path), "\"PurgeOrphanedBiotas\": false",
                    $"{name} must ship the orphan purge disabled");
            }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Removes block comments and line comments, leaving string literals alone so that a "//"
        /// inside one is not mistaken for the start of a comment.
        ///
        /// `internal` rather than `private` (fix round A, A10) so PersonalVendorTests' own source scans
        /// can reuse it instead of growing a third copy - MuleSummonTests already carries a second.
        /// </summary>
        internal static string StripComments(string source)
        {
            // Block comments first, so a // sitting inside one cannot survive as code.
            var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

            var result = new StringBuilder();

            foreach (var line in withoutBlocks.Split('\n'))
            {
                result.Append(StripLineComment(line));
                result.Append('\n');
            }

            return result.ToString();
        }

        private static string StripLineComment(string line)
        {
            var inString = false;

            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (inString && c == '\\')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    return line.Substring(0, i);
            }

            return line;
        }
    }
}
