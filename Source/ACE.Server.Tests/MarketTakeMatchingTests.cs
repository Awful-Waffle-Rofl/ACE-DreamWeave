using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The order-fill take: CountMatching's preflight and TryTakeMatching's all-or-nothing custody.
    ///
    /// SCOPE, so a green run is never mistaken for coverage of production: these drive
    /// FakeMarketItemStore, NOT VaultMarketItemStore. This assembly has no live vault
    /// (AccountVaultManager.GetStore hardcodes the production shard backend and offers no injection
    /// seam), so the real store is compile-checked and reviewed but not exercised here. The fake's own
    /// accounting is pinned against the real algorithm by MarketFakeMatchingTakeTests.
    ///
    /// Referencing MarketSalvageMaterials.IsFullBagOf drags in that class's wcidByMaterial field
    /// initializer, which reads Player.MaterialSalvage and so forces Player's static type initializer -
    /// which unconditionally calls DatabaseManager.World.GetCachedWeenie for "portalmarketplace" and 10
    /// PK-arena portal names (Player_Location.cs) and NREs with no live world database.
    /// MuleSummonTests.cs, PortalDestinationGuardTests.cs and MarketSalvageMaterialsTests.cs all hit the
    /// identical trap and fix it the identical way, copied here: seed those weenie names straight into
    /// WorldDatabaseWithEntityCache's private caches by reflection before anything touches Player.
    /// </summary>
    [TestClass]
    public class MarketTakeMatchingTests
    {
        private static uint nextWcid = 90900;

        private static bool playerStaticFieldsSeeded;

        private static void EnsurePlayerStaticFieldsSeeded()
        {
            if (playerStaticFieldsSeeded)
                return;

            var cacheField = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");
            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<uint, Weenie>)cacheField.GetValue(DatabaseManager.World);
            var nameCache = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);

            foreach (var name in new[]
            {
                "portalmarketplace",
                "portalpkarenanew1", "portalpkarenanew2", "portalpkarenanew3", "portalpkarenanew4", "portalpkarenanew5",
                "portalpklarenanew1", "portalpklarenanew2", "portalpklarenanew3", "portalpklarenanew4", "portalpklarenanew5",
            })
            {
                var wcid = ++nextWcid;
                cache[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic };
                nameCache[name.ToLower()] = wcid;
            }

            playerStaticFieldsSeeded = true;
        }

        [TestInitialize]
        public void TestInitialize()
        {
            EnsurePlayerStaticFieldsSeeded();
        }

        private const uint Seller = 8101;
        private const uint GraniteBagWcid = 21013; // any wcid: the predicate keys on MaterialType, not wcid
        private static readonly MarketActor SellerActor = new MarketActor(Seller, 0x50000401, "Bagseller");

        private static Func<WorldObject, bool> FullGranite => wo => MarketSalvageMaterials.IsFullBagOf(wo, (int)MaterialType.Granite);

        private static WorldObject Bag(int structure, int workmanship)
        {
            var bag = FakeVaultWorld.MakeSalvageBag(GraniteBagWcid, structure, workmanship * 10, 10, 100);
            bag.MaterialType = MaterialType.Granite;
            return bag;
        }

        private static WorldObject Tool()
        {
            var tool = Bag(100, 5);
            tool.SetProperty(PropertyInt.SalvageToolCharges, 10);
            return tool;
        }

        [TestMethod]
        public void TryTakeMatching_TakesAcrossTwoWorkmanshipGroups()
        {
            var store = new FakeMarketItemStore();
            store.SeedGroupOf(Seller, new List<WorldObject> { Bag(100, 4), Bag(100, 4), Bag(100, 4) });
            store.SeedGroupOf(Seller, new List<WorldObject> { Bag(100, 7), Bag(100, 7) });

            Assert.AreEqual(5, store.CountMatching(Seller, FullGranite));

            List<WorldObject> taken = null; var error = MarketError.None; var ok = false;
            store.RunSerialized(Seller, () => ok = store.TryTakeMatching(Seller, FullGranite, 5, SellerActor, out taken, out _, out error));

            Assert.IsTrue(ok, $"got {error}");
            Assert.AreEqual(5, taken.Count);
            Assert.AreEqual(0, store.CountMatching(Seller, FullGranite));
        }

        [TestMethod]
        public void TryTakeMatching_SkipsAPartialBagAndASalvageTool()
        {
            var store = new FakeMarketItemStore();
            store.SeedItem(Seller, Bag(60, 4));    // partial
            store.SeedItem(Seller, Tool());        // full, but a Hammer
            store.SeedItem(Seller, Bag(100, 4));   // the one real match

            Assert.AreEqual(1, store.CountMatching(Seller, FullGranite));

            List<WorldObject> taken = null; var error = MarketError.None; var ok = false;
            store.RunSerialized(Seller, () => ok = store.TryTakeMatching(Seller, FullGranite, 1, SellerActor, out taken, out _, out error));

            Assert.IsTrue(ok);
            Assert.AreEqual(1, taken.Count);
            // (ushort), not a bare 100: WorldObject.Structure is ushort?, which Assert.AreEqual<T>
            // cannot infer T from against an int literal (CS0411). Same cast SalvageCombineTests uses.
            Assert.AreEqual((ushort)100, taken[0].Structure);
            Assert.AreEqual(0, taken[0].GetProperty(PropertyInt.SalvageToolCharges) ?? 0);
        }

        [TestMethod]
        public void TryTakeMatching_Shortfall_ReturnsEverythingTakenAndReportsNoMatchingItems()
        {
            var store = new FakeMarketItemStore();
            store.SeedGroupOf(Seller, new List<WorldObject> { Bag(100, 4), Bag(100, 4) });

            List<WorldObject> taken = null; var error = MarketError.None; var ok = true;
            store.RunSerialized(Seller, () => ok = store.TryTakeMatching(Seller, FullGranite, 3, SellerActor, out taken, out _, out error));

            Assert.IsFalse(ok);
            Assert.AreEqual(MarketError.NoMatchingItems, error);
            Assert.AreEqual(0, taken.Count);
            Assert.AreEqual(2, store.CountMatching(Seller, FullGranite), "a shortfall must leave the seller whole");
            Assert.AreEqual(1, store.ReturnCalls);
        }

        [TestMethod]
        public void TryTakeMatching_FiresThePreWithdrawHookForEveryRowTouched()
        {
            var store = new FakeMarketItemStore();
            store.SeedGroupOf(Seller, new List<WorldObject> { Bag(100, 4), Bag(100, 4) });
            var hookCalls = 0;
            AccountVaultStore.PreWithdrawHook = (_, _, _, _) => hookCalls++;
            try
            {
                List<WorldObject> taken = null; var error = MarketError.None;
                store.RunSerialized(Seller, () => store.TryTakeMatching(Seller, FullGranite, 2, SellerActor, out taken, out _, out error));
                Assert.AreEqual(1, hookCalls, "one row, one hook call: this is what auto-delists a sell listing over the bags");
            }
            finally { AccountVaultStore.PreWithdrawHook = null; }
        }

        // ---------------------------------------------------------------------------------------
        // COLLAPSED LEDGER ROWS.
        //
        // These drive VaultMarketItemStore.CountMatchingEntries DIRECTLY - the real production
        // method, not the fake - which is possible because it is internal static and takes its
        // entries, its predicate and its world source as arguments. Its sibling TryTakeMatching is
        // NOT reachable from this assembly at all (AccountVaultManager.GetStore hardcodes the
        // production shard backend and offers no injection seam), so the take's own ledger branch is
        // covered by reading plus the fake-driven tests below and in MarketOrderFillTests.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The narrowest possible IAccountVaultWorldSource: it builds one registered object per wcid
        /// and counts what it built and destroyed, which is everything the ledger probe touches.
        ///
        /// Deliberately NOT FakeVaultWorld, whose CreateNewWorldObject returns a generic Stackable for
        /// every wcid - a probe of a salvage wcid would then carry no MaterialType and no Structure
        /// and could never match, so a green test would prove the predicate never ran.
        /// </summary>
        private sealed class ProbeWorld : IAccountVaultWorldSource
        {
            private readonly Dictionary<uint, Func<WorldObject>> factories = new Dictionary<uint, Func<WorldObject>>();

            public int Created;
            public int Destroyed;

            public ProbeWorld Register(uint wcid, Func<WorldObject> factory)
            {
                factories[wcid] = factory;
                return this;
            }

            public WorldObject CreateNewWorldObject(uint weenieClassId)
            {
                Created++;
                return factories.TryGetValue(weenieClassId, out var factory) ? factory() : null;
            }

            public void DestroyItem(WorldObject item) => Destroyed++;

            public VaultContainerLoad LoadContainer(uint containerGuid, out Container container)
            {
                container = null;
                return VaultContainerLoad.Absent;
            }

            public bool IsPristine(WorldObject item) => false;

            public void SaveBiota(WorldObject worldObject, Action<bool> callback = null) => callback?.Invoke(true);

            public bool TryResolveCharacter(string characterName, out uint characterGuid, out string canonicalName, out uint accountId)
            {
                characterGuid = 0;
                canonicalName = null;
                accountId = 0;
                return false;
            }
        }

        /// <summary>
        /// THE BUG THIS FIXES. A forge-fresh full bag is provably identical to a fresh instance of its
        /// own weenie, so VaultCollapse collapses it into a ledger row on deposit - which means the
        /// NORMAL shape for the item a bag order wants has no biota to test. Skipping those rows told
        /// a seller holding three of exactly the wanted item that they held none of it.
        /// </summary>
        [TestMethod]
        public void CountMatchingEntries_ALedgerRowOfTheWantedItem_CountsOncePerUnit()
        {
            var world = new ProbeWorld().Register(GraniteBagWcid, () => Bag(100, 4));
            var entries = new List<VaultEntry> { VaultEntry.ForLedger(GraniteBagWcid, 3) };

            Assert.AreEqual(3, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, world));
            Assert.AreEqual(1, world.Created, "one probe, not one per unit");
            Assert.AreEqual(1, world.Destroyed, "and the probe is destroyed even when it matched");
        }

        /// <summary>
        /// The negative half, and the one that makes the positive above mean anything: a ledger row of
        /// a DIFFERENT item counts zero, so the count is the predicate's answer rather than the row's
        /// mere presence. The full bag alongside it is the control - the same call must still see it.
        /// </summary>
        [TestMethod]
        public void CountMatchingEntries_ALedgerRowOfAnotherItem_CountsZeroForIt()
        {
            const uint ironBagWcid = 21014;

            var world = new ProbeWorld()
                .Register(GraniteBagWcid, () => Bag(100, 4))
                .Register(ironBagWcid, () =>
                {
                    var iron = FakeVaultWorld.MakeSalvageBag(ironBagWcid, 100, 40, 10, 100);
                    iron.MaterialType = MaterialType.Iron;
                    return iron;
                });

            var entries = new List<VaultEntry>
            {
                VaultEntry.ForLedger(ironBagWcid, 9),
                VaultEntry.ForLedger(GraniteBagWcid, 2),
            };

            Assert.AreEqual(2, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, world),
                "nine bags of the wrong material are worth nothing to a granite order");
        }

        /// <summary>A Hammer sitting on a ledger row is still a Hammer: a BAG predicate must refuse it.</summary>
        [TestMethod]
        public void CountMatchingEntries_ALedgerRowOfHammers_CountsZeroForABagOrder()
        {
            var world = new ProbeWorld().Register(GraniteBagWcid, Tool);
            var entries = new List<VaultEntry> { VaultEntry.ForLedger(GraniteBagWcid, 4) };

            Assert.AreEqual(0, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, world));
        }

        /// <summary>
        /// Two rows of one wcid probe ONCE. The answer cannot differ between them - a probe is a fresh
        /// instance of the wcid and nothing else - and a probe costs an object construction plus a
        /// destroy on a path that runs on every fill attempt.
        /// </summary>
        [TestMethod]
        public void CountMatchingEntries_TwoRowsOfOneWcid_ProbeTheWcidOnce()
        {
            var world = new ProbeWorld().Register(GraniteBagWcid, () => Bag(100, 4));

            var entries = new List<VaultEntry>
            {
                VaultEntry.ForLedger(GraniteBagWcid, 2),
                VaultEntry.ForLedger(GraniteBagWcid, 5),
            };

            Assert.AreEqual(7, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, world));
            Assert.AreEqual(1, world.Created, "the second row must reuse the first row's answer");
        }

        /// <summary>
        /// A STACKABLE wcid contributes nothing even when the predicate passes, and this is the clause
        /// that keeps the count honest rather than an arbitrary exclusion. TryTakeMatching must deliver
        /// exactly count OBJECTS (its own final gate and MarketManager_Orders both test taken.Count),
        /// while a ledger withdraw of k units of a stackable packs them into ceil(k / MaxStackSize)
        /// objects - so counting those units would promise a number no take could ever deliver.
        ///
        /// The MaxStackSize 1 case is the control: same wcid, same predicate, same probe shape.
        /// </summary>
        [TestMethod]
        public void CountMatchingEntries_AStackableLedgerWcid_CountsZero()
        {
            var stackable = new ProbeWorld().Register(GraniteBagWcid, () =>
            {
                var bag = Bag(100, 4);
                bag.SetProperty(PropertyInt.MaxStackSize, 5);
                return bag;
            });

            var single = new ProbeWorld().Register(GraniteBagWcid, () => Bag(100, 4));
            var entries = new List<VaultEntry> { VaultEntry.ForLedger(GraniteBagWcid, 5) };

            Assert.AreEqual(5, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, single),
                "control: the identical row DOES count when one unit is one object");

            Assert.AreEqual(0, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, stackable),
                "a unit that is not an object cannot be promised to a count the take must match exactly");
        }

        /// <summary>
        /// A wcid the world can no longer build answers zero rather than throwing or guessing - the
        /// same direction WithdrawFromLedger takes when its own probe comes back null.
        /// </summary>
        [TestMethod]
        public void CountMatchingEntries_AnUninstantiableLedgerWcid_CountsZero()
        {
            var world = new ProbeWorld();   // nothing registered
            var entries = new List<VaultEntry> { VaultEntry.ForLedger(GraniteBagWcid, 6) };

            Assert.AreEqual(0, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, world));
            Assert.AreEqual(0, world.Destroyed, "there was nothing to destroy");
        }

        /// <summary>Stored biotas and ledger units are counted by the same call, and they add.</summary>
        [TestMethod]
        public void CountMatchingEntries_StoredBiotasAndLedgerUnits_AddTogether()
        {
            var world = new ProbeWorld().Register(GraniteBagWcid, () => Bag(100, 4));

            var entries = new List<VaultEntry>
            {
                VaultEntry.ForGroup(new List<WorldObject> { Bag(100, 7), Bag(100, 7) }),
                VaultEntry.ForItem(Bag(60, 4)),   // partial: never counts
                VaultEntry.ForLedger(GraniteBagWcid, 3),
            };

            Assert.AreEqual(5, VaultMarketItemStore.CountMatchingEntries(entries, FullGranite, world));
        }

        /// <summary>
        /// The store's OWN unwind, driven through the fake because the real one is unreachable here.
        /// A shortfall must put ledger-derived units back on the LEDGER, never file them as stored
        /// biotas: AccountVaultStore.TryReturnWithdrawn skips the entry cap on the reasoning that a
        /// stored biota was already counted while it sat in the vault, which is false for a
        /// ledger-derived object and turns one entry into one entry per unit.
        /// </summary>
        [TestMethod]
        public void TryTakeMatching_LedgerShortfall_PutsTheUnitsBackOnTheLedger()
        {
            var store = new FakeMarketItemStore();
            store.SeedLedger(Seller, GraniteBagWcid, 1, () => Bag(100, 4));

            Assert.AreEqual(1, store.CountMatching(Seller, FullGranite), "control: the ledger row is visible to the count");

            List<WorldObject> taken = null; var error = MarketError.None; var ok = true;
            store.RunSerialized(Seller, () => ok = store.TryTakeMatching(Seller, FullGranite, 2, SellerActor, out taken, out _, out error));

            Assert.IsFalse(ok);
            Assert.AreEqual(MarketError.NoMatchingItems, error);
            Assert.AreEqual(0, taken.Count);
            Assert.AreEqual(1, store.LedgerCount(Seller, GraniteBagWcid), "the unit went back where it came from");
            Assert.AreEqual(0, store.Items.TryGetValue(Seller, out var loose) ? loose.Count : 0,
                "and NOT as a stored biota, which would convert one capped entry into one per unit");
        }
    }
}
