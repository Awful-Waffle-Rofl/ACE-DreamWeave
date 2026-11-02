using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using ACE.Server.Tests.Pvp;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Player.HandleDestroyBonded on a seeded Player (no database, no world, no landblock) whose Session is a recording
    /// fake. Bug: a wielded "destroyed on death" item was reported as lost (and named in the death message) but stayed
    /// wielded, because the pack-only consume failed for an equipped item and its result was ignored.
    /// </summary>
    [TestClass]
    public class DestroyBondedDeathTests
    {
        // Static-range guids: WorldObject.Destroy recycles a DYNAMIC guid through GuidManager, whose allocator needs the database.
        private static uint nextGuid = 0x7D100000;

        private static readonly FieldInfo ShardField = typeof(ACE.Database.DatabaseManager).GetField("<Shard>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static);

        private object savedShard;

        [TestInitialize]
        public void Setup()
        {
            // GameMessage writers encode text as windows-1252, which the server registers at startup.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            // The dequip path ends in DeepSave -> DatabaseManager.Shard.SaveBiotasInParallel, which only queues a task.
            // A shard with an empty, never-drained queue takes that call without a database or a worker thread.
            Assert.IsNotNull(ShardField, "DatabaseManager.Shard backing field was not found by reflection");
            savedShard = ShardField.GetValue(null);

            var shard = (ACE.Database.SerializedShardDatabase)RuntimeHelpers.GetUninitializedObject(typeof(ACE.Database.SerializedShardDatabase));
            var queue = typeof(ACE.Database.SerializedShardDatabase).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(queue, "SerializedShardDatabase._queue was not found by reflection");
            shardQueue = Activator.CreateInstance(queue.FieldType);
            queue.SetValue(shard, shardQueue);
            ShardField.SetValue(null, shard);

            SeedArenaPortalWeenies();
        }

        private List<string> seededNames = new List<string>();
        private bool seededWcidAdded;
        private const uint SeededWcid = 0x7FFF0001;

        /// <summary>
        /// HandleDestroyBonded's warning goes through Player's static logger, and touching any Player static runs Player's static
        /// constructor, which reads ten arena portal weenies (Player_Location.cs) from the World database. If that constructor
        /// throws it stays failed for every later test in the process, so a regression that reached the warning would poison the
        /// suite. Seed the lookups for this class and take them out again in Cleanup.
        /// </summary>
        private void SeedArenaPortalWeenies()
        {
            var world = ACE.Database.DatabaseManager.World;
            var nameToId = (System.Collections.Concurrent.ConcurrentDictionary<string, uint>)typeof(ACE.Database.WorldDatabaseWithEntityCache)
                .GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(world);
            var byId = (System.Collections.Concurrent.ConcurrentDictionary<uint, Weenie>)typeof(ACE.Database.WorldDatabaseWithEntityCache)
                .GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(world);
            Assert.IsNotNull(nameToId, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache moved; update DestroyBondedDeathTests");
            Assert.IsNotNull(byId, "WorldDatabaseWithEntityCache.weenieCache moved; update DestroyBondedDeathTests");

            seededNames = Enumerable.Range(1, 5).SelectMany(i => new[] { "portalpkarenanew" + i, "portalpklarenanew" + i }).Where(n => !nameToId.ContainsKey(n)).ToList();
            seededWcidAdded = !byId.ContainsKey(SeededWcid);

            foreach (var name in seededNames)
                nameToId[name] = SeededWcid;

            if (seededWcidAdded)
                byId[SeededWcid] = new Weenie { WeenieClassId = SeededWcid, WeenieType = WeenieType.Portal };
        }

        private void UnseedArenaPortalWeenies()
        {
            var world = ACE.Database.DatabaseManager.World;
            var nameToId = (System.Collections.Concurrent.ConcurrentDictionary<string, uint>)typeof(ACE.Database.WorldDatabaseWithEntityCache)
                .GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(world);
            var byId = (System.Collections.Concurrent.ConcurrentDictionary<uint, Weenie>)typeof(ACE.Database.WorldDatabaseWithEntityCache)
                .GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(world);

            foreach (var name in seededNames)
                nameToId?.TryRemove(name, out _);

            if (seededWcidAdded)
                byId?.TryRemove(SeededWcid, out _);
        }

        private object shardQueue;

        /// <summary>The fake shard's queued operations in FIFO order: "save" for a queued save task, "remove" for a queued biota delete.</summary>
        private List<string> QueuedOperations()
        {
            var toArray = shardQueue.GetType().GetMethod("ToArray");
            Assert.IsNotNull(toArray, "BlockingCollection.ToArray moved; update DestroyBondedDeathTests");
            var entries = (Array)toArray.Invoke(shardQueue, null);
            var result = new List<string>();

            foreach (var entry in entries)
            {
                var type = entry.GetType();
                var removeField = type.GetField("RemoveBiotaItem");
                var saveField = type.GetField("SaveBiotaItem");
                var taskField = type.GetField("Task");
                Assert.IsNotNull(removeField, "SerializedShardDatabase.QueueEntry.RemoveBiotaItem moved; update DestroyBondedDeathTests");
                Assert.IsNotNull(saveField, "SerializedShardDatabase.QueueEntry.SaveBiotaItem moved; update DestroyBondedDeathTests");
                Assert.IsNotNull(taskField, "SerializedShardDatabase.QueueEntry.Task moved; update DestroyBondedDeathTests");

                var remove = removeField.GetValue(entry);
                var save = saveField.GetValue(entry);
                var task = taskField.GetValue(entry);

                result.Add(remove != null ? "remove" : save != null || task != null ? "save" : "other");
            }

            return result;
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (ShardField != null)
                ShardField.SetValue(null, savedShard);

            UnseedArenaPortalWeenies();
        }

        private static Player PlayerWithSession(CombatMode combatMode = CombatMode.NonCombat)
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();
            SeedInitializers(player);

            // A stance swap (HandleActionChangeCombatMode) needs a landblock and a motion table, so a test that reaches it throws;
            // that is what the Melee test below relies on to prove the swap is skipped.
            PvpTemplatePlayerTests.SetField(typeof(Creature), player, "<CombatMode>k__BackingField", combatMode);

            // the ObjDesc broadcast on dequip reads the hair textures off the character row
            // IsPlussed also makes Player.Name (formatted by the both-fail warning) skip a ConfigManager.Config read
            StoreWithoutClassInit(player, "<Character>k__BackingField", new ACE.Database.Models.Shard.Character { IsPlussed = true });

            var bundles = new NetworkBundle[(int)GameMessageGroup.QueueMax];
            var locks = new object[bundles.Length];

            for (var i = 0; i < bundles.Length; i++)
            {
                locks[i] = new object();
                bundles[i] = new NetworkBundle();
            }

            var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
            var network = (NetworkSession)RuntimeHelpers.GetUninitializedObject(typeof(NetworkSession));

            PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "session", session);
            PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "currentBundleLocks", locks);
            PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "currentBundles", bundles);

            // the client-object-book hook (PickupEvent / DeleteObject) calls these constructor-built delegates
            PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "enqueueIntoBundle", new Action<GameMessage>(m => { }));
            PvpTemplatePlayerTests.SetField(typeof(NetworkSession), network, "noteBookFailure", new Action<Exception>(e => { }));

            session.Network = network;
            PvpTemplatePlayerTests.SetField(typeof(Session), session, "<Player>k__BackingField", player);
            PvpTemplatePlayerTests.SetField(typeof(Session), session, "<Account>k__BackingField", "destroy-bonded-test");

            // FieldInfo.SetValue would run Player's static constructor (it reads the World database); a plain stfld does not.
            StoreWithoutClassInit(player, "<Session>k__BackingField", session);

            return player;
        }

        private static void StoreWithoutClassInit(Player player, string name, object value)
        {
            var field = typeof(Player).GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"Player.{name} was not found by reflection");

            var store = new System.Reflection.Emit.DynamicMethod("store_" + name, typeof(void), new[] { typeof(object), typeof(object) }, typeof(Player).Module, true);
            var il = store.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, typeof(Player));
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, field.FieldType);
            il.Emit(System.Reflection.Emit.OpCodes.Stfld, field);
            il.Emit(System.Reflection.Emit.OpCodes.Ret);

            ((Action<object, object>)store.CreateDelegate(typeof(Action<object, object>)))(player, value);
        }

        private static GenericObject Dagger(BondedStatus bonded, bool wielded)
        {
            var item = (GenericObject)RuntimeHelpers.GetUninitializedObject(typeof(GenericObject));

            var biota = new Biota
            {
                WeenieType = WeenieType.Generic,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Bonded, (int)bonded } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Dagger" } },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
            };

            if (wielded)
                biota.PropertiesInt[PropertyInt.CurrentWieldedLocation] = (int)EquipMask.MeleeWeapon;

            PvpTemplatePlayerTests.SeedWorldObject(item, biota, Interlocked.Increment(ref nextGuid));

            // field initializers do not run on an uninitialized object; Dequip's ClearChild writes Location, which reads these
            SeedInitializers(item);

            return item;
        }

        private static void SeedInitializers(WorldObject wo)
        {
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), wo, "positionCache", new Dictionary<PositionType, Position>());
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), wo, "ephemeralPositions", new Dictionary<PositionType, Position>());
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), wo, "<Sequences>k__BackingField", new ACE.Server.Network.Sequence.SequenceManager());
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), wo, "<Children>k__BackingField", new List<HeldItem>());
            wo.EmoteManager = new ACE.Server.WorldObjects.Managers.EmoteManager(wo);
        }

        private static void Wield(Player player, WorldObject item) => player.EquippedObjects[item.Guid] = item;

        private static void Pack(Player player, WorldObject item)
        {
            item.PlacementPosition = player.Inventory.Count;
            player.Inventory[item.Guid] = item;
        }

        [TestMethod]
        public void WieldedDestroyBondedItem_IsRemovedFromEquipmentAndReported()
        {
            var player = PlayerWithSession();
            var dagger = Dagger(BondedStatus.Destroy, wielded: true);
            Wield(player, dagger);

            var destroyed = player.HandleDestroyBonded();

            CollectionAssert.AreEqual(new List<WorldObject> { dagger }, destroyed, "the wielded item is reported as destroyed");
            Assert.IsFalse(player.EquippedObjects.ContainsKey(dagger.Guid), "the wielded item must no longer be wielded");
            Assert.IsFalse(player.GetAllPossessions().Contains(dagger), "the wielded item must no longer be a possession");
            Assert.IsTrue(dagger.IsDestroyed, "the item was actually destroyed, not just unwielded");
        }

        [TestMethod]
        public void WieldedDestroyBondedItem_InMeleeStance_IsRemovedWithoutAStanceSwap()
        {
            var player = PlayerWithSession(CombatMode.Melee);
            var dagger = Dagger(BondedStatus.Destroy, wielded: true);
            Wield(player, dagger);

            var destroyed = player.HandleDestroyBonded();

            CollectionAssert.AreEqual(new List<WorldObject> { dagger }, destroyed);
            Assert.IsFalse(player.EquippedObjects.ContainsKey(dagger.Guid));
            Assert.IsTrue(dagger.IsDestroyed);
            Assert.AreEqual(CombatMode.Melee, player.CombatMode, "a dying player must not be swapped out of its stance");
        }

        [TestMethod]
        public void WieldedPersistedDestroyBondedItem_QueuesTheSaveBeforeTheRemove()
        {
            var player = PlayerWithSession();
            var dagger = Dagger(BondedStatus.Destroy, wielded: true);
            // a biota that exists in the shard, so Destroy() queues a delete rather than skipping it
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), dagger, "biotaOriginatedFromDatabase", true);
            Wield(player, dagger);

            player.HandleDestroyBonded();

            var ops = QueuedOperations();
            Assert.AreEqual(1, ops.Count(o => o == "save"), "the dequip saves the item once: " + string.Join(",", ops));
            Assert.AreEqual(1, ops.Count(o => o == "remove"), "Destroy() queues one delete: " + string.Join(",", ops));
            Assert.IsTrue(ops.IndexOf("save") < ops.IndexOf("remove"), "save must precede remove (FIFO), or the row is resurrected: " + string.Join(",", ops));
        }

        [TestMethod]
        public void DestroyBondedItemInsideADestroyBondedPack_IsReportedOnceItsPackIsDestroyed()
        {
            var player = PlayerWithSession();
            var pack = (Container)RuntimeHelpers.GetUninitializedObject(typeof(Container));
            var biota = new Biota
            {
                WeenieType = WeenieType.Container,
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Bonded, (int)BondedStatus.Destroy } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Pack" } },
                PropertiesIID = new Dictionary<PropertyInstanceId, uint>(),
            };
            PvpTemplatePlayerTests.SeedWorldObject(pack, biota, Interlocked.Increment(ref nextGuid));
            SeedInitializers(pack);
            PvpTemplatePlayerTests.SetField(typeof(Container), pack, "<Inventory>k__BackingField", new Dictionary<ObjectGuid, WorldObject>());

            var dagger = Dagger(BondedStatus.Destroy, wielded: false);
            pack.Inventory[dagger.Guid] = dagger;
            Pack(player, pack);

            var destroyed = player.HandleDestroyBonded();

            CollectionAssert.AreEquivalent(new List<WorldObject> { pack, dagger }, destroyed, "the pack and the item inside it are both reported");
            Assert.IsTrue(pack.IsDestroyed);
            Assert.IsTrue(dagger.IsDestroyed, "destroying the pack cascades to its contents");
        }

        [TestMethod]
        public void DestroyBondedItemInNeitherPackNorEquipment_IsNotReported()
        {
            // Reaches the both-fail branch: the item is enumerated (EquippedObjects.Values) but filed under a key that is
            // not its guid, so the pack consume (Inventory only) and the dequip (EquippedObjects.Remove by guid) both miss it.
            var player = PlayerWithSession();
            var dagger = Dagger(BondedStatus.Destroy, wielded: true);
            player.EquippedObjects[new ObjectGuid(dagger.Guid.Full + 0x100)] = dagger;

            var destroyed = player.HandleDestroyBonded();

            Assert.AreEqual(0, destroyed.Count, "an item neither call could remove must not be reported as lost");
            Assert.IsFalse(dagger.IsDestroyed);
            Assert.AreEqual(1, player.EquippedObjects.Count);
        }

        [TestMethod]
        public void TwoWieldedDestroyBondedItems_AreBothRemoved()
        {
            var player = PlayerWithSession();
            var left = Dagger(BondedStatus.Destroy, wielded: true);
            var right = Dagger(BondedStatus.Destroy, wielded: true);
            Wield(player, left);
            Wield(player, right);

            var destroyed = player.HandleDestroyBonded();

            CollectionAssert.AreEquivalent(new List<WorldObject> { left, right }, destroyed);
            Assert.AreEqual(0, player.EquippedObjects.Count, "neither dagger stays wielded");
        }

        [TestMethod]
        public void PackedDestroyBondedItem_IsStillDestroyedAndReported()
        {
            var player = PlayerWithSession();
            var dagger = Dagger(BondedStatus.Destroy, wielded: false);
            Pack(player, dagger);

            var destroyed = player.HandleDestroyBonded();

            CollectionAssert.AreEqual(new List<WorldObject> { dagger }, destroyed);
            Assert.IsFalse(player.Inventory.ContainsKey(dagger.Guid), "the packed item is gone from the pack");
            Assert.IsTrue(dagger.IsDestroyed);
        }

        [TestMethod]
        public void NonDestroyBondedItems_AreUntouched()
        {
            var player = PlayerWithSession();
            var wornNormal = Dagger(BondedStatus.Normal, wielded: true);
            var wornBonded = Dagger(BondedStatus.Bonded, wielded: true);
            var packedNormal = Dagger(BondedStatus.Normal, wielded: false);
            Wield(player, wornNormal);
            Wield(player, wornBonded);
            Pack(player, packedNormal);

            var destroyed = player.HandleDestroyBonded();

            Assert.AreEqual(0, destroyed.Count);
            Assert.IsTrue(player.EquippedObjects.ContainsKey(wornNormal.Guid));
            Assert.IsTrue(player.EquippedObjects.ContainsKey(wornBonded.Guid));
            Assert.IsTrue(player.Inventory.ContainsKey(packedNormal.Guid));
            Assert.IsFalse(wornNormal.IsDestroyed || wornBonded.IsDestroyed || packedNormal.IsDestroyed);
        }
    }
}
