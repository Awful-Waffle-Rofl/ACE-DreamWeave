using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.SalvageRoller;
using ACE.Server.SpellReroll;
using ACE.Server.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Salvage Roller batch state machine (Docs/SalvageRoller/DESIGN.md 5.4, 5.6, 5.9; plan step 7): the sell
    /// pipeline, the roll answer, the take, reconcile and the audit trail, driven through a fake host over the REAL
    /// AccountVaultStore (on FakeVaultBackend/FakeVaultWorld) and the real candidate engine.
    ///
    /// THE FAULT-INJECTION HALF. SalvageRollerManager.CrashHook stops the state machine at the start of each crash
    /// window W0-W5, as a crashed process would. The host keeps a PERSISTED model of every item (its parent and its
    /// full snapshot as of its last save, and whether it was destroyed), fed by every item save the host and the fake
    /// world see. A "restart" then throws away everything in memory - the store, the escrow containers' contents, the
    /// pack, the process maps - and rebuilds them from that persisted model alone, exactly as a server start would from
    /// the shard. Reconcile runs on the rebuilt world, and the test asserts the invariants of the plan:
    ///   - one biota per guid, and the guid unchanged;
    ///   - the batch stamp set exactly once (or never, for a cancelled batch);
    ///   - no batch row left behind;
    ///   - every value-moving step preceded by an audit row of the right kind.
    /// </summary>
    [TestClass]
    public class SalvageRollerBatchTests
    {
        private const uint Account = 4301;
        private const uint Character = 0x50004301;

        private static uint nextGuid = 0x7D6C0000;     // static guid range, clear of GuidManager and the other classes
        private static uint nextWcid = 993900;

        private static readonly string[] DoubleKeys =
        {
            "weapon_mod_special_chance_1", "weapon_mod_special_chance_2", "weapon_mod_special_chance_3", "weapon_mod_special_chance_4",
            "weapon_mod_magnitude_scale", "equipment_mod_potency_scale",
        };

        private static readonly string[] BoolKeys =
        {
            "weapon_mods_enabled", "equipment_mods_enabled", "serpentine_reroll_enabled", "weapon_mod_guarantee_damage_special", "allow_combat_mode_crafting",
        };

        private static readonly string[] LongKeys = { "salvage_roller_max_candidates", "account_vault_entry_cap", "account_vault_landblock" };

        private FakeVaultBackend backend;
        private Harness host;
        private Func<WorldObject, (SpellRerollPlan Plan, RerollRefusal Refusal)> savedPlanner;
        private Func<DateTime> savedClock;

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Setup()
        {
            foreach (var key in DoubleKeys)
                Assert.IsTrue(PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item), key);

            foreach (var key in BoolKeys)
                Assert.IsTrue(PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item), key);

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));

            foreach (var key in LongKeys)
                Assert.IsTrue(PropertyManager.ModifyLong(key, DefaultPropertyManager.DefaultLongProperties[key].Item), key);

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));

            VaultClassTestConfig.Seed();

            savedPlanner = SalvageRollerEngine.SerpentinePlanner;
            savedClock = SalvageRollerManager.Clock;

            SalvageRollerManager.CrashHook = null;
            SalvageRollerManager.ForgetProcessStateForTest();

            backend = new FakeVaultBackend();
            host = new Harness(backend);
        }

        [TestCleanup]
        public void Cleanup()
        {
            SalvageRollerManager.CrashHook = null;
            SalvageRollerManager.ForgetProcessStateForTest();
            SalvageRollerEngine.SerpentinePlanner = savedPlanner;
            SalvageRollerManager.Clock = savedClock;

            foreach (var key in DoubleKeys)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

            foreach (var key in BoolKeys)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);

            foreach (var key in LongKeys)
                PropertyManager.ModifyLong(key, DefaultPropertyManager.DefaultLongProperties[key].Item);
        }

        // =================================================================================================
        // fixtures
        // =================================================================================================

        private static WorldObject Weapon()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Clothing,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Roller Sword" } },
            };

            return new Clothing(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>A sword that already holds specials and sits above the bottom tier, so a result can move its tier either way.</summary>
        private static WorldObject RolledSword()
        {
            var sword = Weapon();

            for (var tries = 0; WeaponQualityTiers.Evaluate(sword) == WeaponQualityTier.None && tries < 500; tries++)
                WeaponModManager.ApplyReroll(sword, WeaponClassifier.Classify(sword), 10);

            Assert.AreNotEqual(WeaponQualityTier.None, WeaponQualityTiers.Evaluate(sword), "precondition: a sword above the bottom tier");

            return sword;
        }

        private static WorldObject Hammer(MaterialType material = MaterialType.Tourmaline, int charges = 2)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                    { PropertyInt.Structure, charges },
                    { PropertyInt.MaxStructure, 10 },
                    { PropertyInt.SalvageToolCharges, 10 },
                    { PropertyInt.MaterialType, (int)material },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, $"Hammer of {material}" } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>The verify a real basket would pass: the first item is the target, the rest are the fuel.</summary>
        private static SalvageRollerManager.DetachedVerifier Accept(SalvageRollerAction action) =>
            (IReadOnlyList<WorldObject> items, out SalvageRollerAction a, out WorldObject target, out List<WorldObject> hammers, out string refusal) =>
            {
                a = action;
                target = items[0];
                hammers = items.Skip(1).ToList();
                refusal = null;
                return true;
            };

        private sealed class Basket
        {
            public WorldObject Target;
            public List<WorldObject> Hammers;
            public DuoGemSnapshot Original;
            public List<WorldObject> All => new[] { Target }.Concat(Hammers).ToList();
        }

        /// <summary>A sold basket: detached from the pack and that detach saved (the sell path's half, before our code runs).</summary>
        private Basket Sold(WorldObject target = null, int charges = 2)
        {
            var basket = new Basket { Target = target ?? Weapon(), Hammers = new List<WorldObject> { Hammer(charges: charges), Hammer(charges: charges) } };
            basket.Original = DuoGemSnapshot.Capture(basket.Target);

            foreach (var item in basket.All)
                host.Persist(item);

            return basket;
        }

        private SalvageRollerManager.SellOutcome Sell(Basket basket, SalvageRollerAction action = SalvageRollerAction.TourmalineReroll)
        {
            return SalvageRollerManager.Sell(host, basket.All, Accept(action), item => host.LooseHandBack.Add(item));
        }

        private static string Canon(DuoGemSnapshot snapshot)
        {
            var item = snapshot.Item;

            string Table<T>(string name, Dictionary<int, T> table) =>
                name + ":" + string.Join(",", table.Where(kv => !(name == "i" && (kv.Key == (int)PropertyInt.CurrentWieldedLocation || kv.Key == (int)PropertyInt.PlacementPosition)))
                    .OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

            return string.Join("|",
                Table("i", item.Ints), Table("i64", item.Int64s), Table("f", item.Floats), Table("s", item.Strings),
                Table("did", item.DataIds), Table("b", item.Bools), Table("sb", item.SpellBook));
        }

        /// <summary>The original plus candidate k plus the stamp: what a correct take leaves, applied ONCE.</summary>
        private static string Expected(Basket basket, SalvageRollerCandidate candidate, ulong batchId)
        {
            var copy = SalvageRollerEngine.NewScratchCopy(basket.Original);
            SalvageRollerEngine.ApplyDiff(copy, candidate.Diff);
            copy.SetProperty(PropertyInt64.SalvageBatchApplied, (long)batchId);
            return Canon(DuoGemSnapshot.Capture(copy));
        }

        // =================================================================================================
        // the harness host: a shard model, a pack, a screen
        // =================================================================================================

        internal sealed class Harness : ISalvageRollerHost
        {
            public uint CharacterId => Character;
            public uint AccountId => Account;
            public string CharacterName => "Roller Tester";

            private readonly FakeVaultBackend backend;

            public FakeVaultWorld World;
            public AccountVaultStore Store;
            public readonly List<uint> EscrowGuids = new List<uint>();
            public uint VaultGuid;

            public Harness(FakeVaultBackend backend)
            {
                this.backend = backend;
                World = Hooked(new FakeVaultWorld());

                // one vault and one escrow container, seeded the way the escrow tests seed them
                var vault = NewContainer(FakeVaultWorld.NextGuid());
                VaultGuid = vault.Guid.Full;
                World.Containers[VaultGuid] = vault;
                backend.Vaults.Add(new ShardAccountVault { Id = 9101, AccountId = Account, ContainerGuid = VaultGuid, CreatedAt = new DateTime(2026, 1, 1), Kind = AccountVaultStore.VaultContainerKind });

                var escrow = NewContainer(FakeVaultWorld.NextGuid());
                EscrowGuids.Add(escrow.Guid.Full);
                World.Containers[escrow.Guid.Full] = escrow;
                backend.Vaults.Add(new ShardAccountVault { Id = 9102, AccountId = Account, ContainerGuid = escrow.Guid.Full, CreatedAt = new DateTime(2026, 1, 1).AddMinutes(1), Kind = AccountVaultStore.EscrowContainerKind });

                Store = new AccountVaultStore(Account, backend, World);
            }

            private FakeVaultWorld Hooked(FakeVaultWorld world)
            {
                world.InterceptCompletion = (wo, isDestroy, ok, callback) =>
                {
                    Complete(wo, isDestroy, ok, callback);
                    return true;
                };
                world.BeforeDestroyItem = wo =>
                {
                    // a removal set to fail never reaches the shard; a deferred one reaches it when it fires
                    if (!DeferSaves && !FailSaves.Contains(wo.Guid.Full))
                        MarkDestroyed(wo);

                    Log.Add($"MOVE:destroy:{wo.Guid.Full:X8}");
                };
                return world;
            }

            // ---- the shard's acknowledgements (step 7 review F9) ----

            /// <summary>When true every item save and every removal is HELD: nothing reaches the persisted model and no callback runs until <see cref="FireDeferred"/>.</summary>
            public bool DeferSaves;

            /// <summary>Guids whose writes report FAILURE (landed false) and persist nothing. Decided when the write is queued.</summary>
            public readonly HashSet<uint> FailSaves = new HashSet<uint>();

            public readonly List<Action> Deferred = new List<Action>();

            /// <summary>Runs every held write in FIFO order, including writes queued by the callbacks themselves.</summary>
            public void FireDeferred()
            {
                while (Deferred.Count > 0)
                {
                    var next = Deferred[0];
                    Deferred.RemoveAt(0);
                    next();
                }
            }

            /// <summary>Fires only the first held write.</summary>
            public void FireOne()
            {
                var next = Deferred[0];
                Deferred.RemoveAt(0);
                next();
            }

            private void MarkDestroyed(WorldObject wo)
            {
                if (Shard.TryGetValue(wo.Guid.Full, out var p))
                    p.Destroyed = true;
            }

            private void Complete(WorldObject wo, bool isDestroy, bool ok, Action<bool> callback)
            {
                if (wo == null || wo is Container)
                {
                    callback?.Invoke(ok);
                    return;
                }

                var landed = ok && !FailSaves.Contains(wo.Guid.Full);

                void Fire()
                {
                    if (landed)
                    {
                        if (isDestroy)
                            MarkDestroyed(wo);
                        else
                            Persist(wo);
                    }

                    callback?.Invoke(landed);
                }

                if (DeferSaves)
                    Deferred.Add(Fire);
                else
                    Fire();
            }

            private static Container NewContainer(uint guid)
            {
                var weenie = new Weenie
                {
                    WeenieClassId = AccountVaultStore.VaultContainerWcid,
                    WeenieType = WeenieType.Container,
                    PropertiesInt = new Dictionary<PropertyInt, int>
                    {
                        { PropertyInt.ItemType, (int)ItemType.Container },
                        { PropertyInt.ItemsCapacity, AccountVaultStore.VaultItemCapacity },
                        { PropertyInt.ContainersCapacity, 0 },
                    },
                    PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Account Escrow" } },
                };

                return new Container(weenie, new ObjectGuid(guid));
            }

            public AccountVaultStore GetStore() => Store;

            // ---- the shard rows ----

            public CharacterSalvageBatch Row;
            private ulong nextBatchId = 700;
            public bool FailAddBatch;

            public readonly List<SalvageRollerAudit> Audits = new List<SalvageRollerAudit>();
            public readonly HashSet<SalvageRollerAuditEvent> FailAudit = new HashSet<SalvageRollerAuditEvent>();

            /// <summary>Chronological: AUDIT:&lt;event&gt;, MOVE:&lt;kind&gt;:&lt;guid&gt;, ROWS:&lt;what&gt;.</summary>
            public readonly List<string> Log = new List<string>();

            private static CharacterSalvageBatch Clone(CharacterSalvageBatch b)
            {
                var copy = new CharacterSalvageBatch
                {
                    Id = b.Id, CharacterId = b.CharacterId, AccountId = b.AccountId, ItemGuid = b.ItemGuid, ItemWcid = b.ItemWcid,
                    Action = b.Action, State = b.State, ChosenSeq = b.ChosenSeq, RollCount = b.RollCount, CreatedAt = b.CreatedAt, Payload = b.Payload,
                };

                foreach (var i in b.Items)
                    copy.Items.Add(new CharacterSalvageBatchItem { Guid = i.Guid, BatchId = i.BatchId, Role = i.Role });

                return copy;
            }

            public bool TryReadBatch(out CharacterSalvageBatch batch)
            {
                batch = Row == null ? null : Clone(Row);
                return true;
            }

            public bool AddBatch(CharacterSalvageBatch batch)
            {
                if (FailAddBatch || Row != null)
                    return false;

                batch.Id = nextBatchId++;

                foreach (var i in batch.Items)
                    i.BatchId = batch.Id;

                Row = Clone(batch);
                Log.Add("ROWS:add");
                return true;
            }

            public bool TryCommitPending(ulong batchId, string payload)
            {
                if (FailCommit || Row == null || Row.Id != batchId || Row.State != (byte)SalvageBatchState.Escrowing)
                    return false;

                Row.State = (byte)SalvageBatchState.Pending;
                Row.Payload = payload;
                Log.Add("MOVE:pending");
                return !LoseCommitAck;
            }

            public bool TrySetTaking(ulong batchId, int seq)
            {
                if (Row == null || Row.Id != batchId || Row.State != (byte)SalvageBatchState.Pending)
                    return false;

                Row.State = (byte)SalvageBatchState.Taking;
                Row.ChosenSeq = seq;
                Log.Add("MOVE:taking");
                return true;
            }

            public bool DeleteBatchItem(uint guid)
            {
                var row = Row?.Items.FirstOrDefault(i => i.Guid == guid);

                if (row != null)
                {
                    Row.Items.Remove(row);
                    Log.Add($"ROWS:item:{guid:X8}");
                }

                return true;
            }

            /// <summary>When set, TryCommitPending COMMITS but reports failure: a lost acknowledgement.</summary>
            public bool LoseCommitAck;

            /// <summary>When set, TryCommitPending reports failure and commits nothing.</summary>
            public bool FailCommit;

            public bool DeleteBatch(ulong batchId)
            {
                if (Row != null && Row.Id == batchId)
                    Row = null;

                Log.Add("ROWS:delete");
                return true;
            }

            public bool AddAudit(SalvageRollerAudit row)
            {
                var ev = (SalvageRollerAuditEvent)row.Event;

                if (FailAudit.Contains(ev))
                    return false;

                Audits.Add(row);
                Log.Add($"AUDIT:{ev}");
                return true;
            }

            public bool TryReadAuditDetail(ulong batchId, SalvageRollerAuditEvent ev, out string detail)
            {
                detail = Audits.LastOrDefault(a => a.BatchId == batchId && a.Event == (byte)ev)?.Detail;
                return true;
            }

            // ---- the pack and the persisted model ----

            public readonly Dictionary<uint, WorldObject> Pack = new Dictionary<uint, WorldObject>();
            public bool PackFull;
            public bool RefusePut;

            public readonly List<WorldObject> LooseHandBack = new List<WorldObject>();

            /// <summary>
            /// Room for this many more items (null = unlimited). A partial cancel is what a pack with room for some of a
            /// batch's items, but not all, produces.
            /// </summary>
            public int? PackRoom;

            public sealed class Persisted
            {
                public uint? Container;
                public uint? Wielder;
                public Position Location;
                public DuoGemSnapshot Snapshot;
                public bool Destroyed;
            }

            public readonly Dictionary<uint, Persisted> Shard = new Dictionary<uint, Persisted>();

            /// <summary>Saves that changed an item's SalvageBatchApplied to a new value: "the stamp set".</summary>
            public int StampWrites;

            public void Persist(WorldObject wo)
            {
                if (wo == null || wo is Container)
                    return;

                var snapshot = DuoGemSnapshot.Capture(wo);
                Shard.TryGetValue(wo.Guid.Full, out var previous);

                long? Stamp(DuoGemSnapshot s) => s != null && s.Item.Int64s.TryGetValue((int)PropertyInt64.SalvageBatchApplied, out var v) ? v : (long?)null;

                var newStamp = Stamp(snapshot);

                if (newStamp.HasValue && newStamp != Stamp(previous?.Snapshot))
                    StampWrites++;

                if (wo.ContainerId != null && EscrowGuids.Contains(wo.ContainerId.Value) && previous?.Container != wo.ContainerId)
                    Log.Add($"MOVE:escrow:{wo.Guid.Full:X8}");

                Shard[wo.Guid.Full] = new Persisted
                {
                    Container = wo.ContainerId,
                    Wielder = wo.WielderId,
                    Location = wo.Location == null ? null : new Position(wo.Location),
                    Snapshot = snapshot,
                    Destroyed = previous?.Destroyed ?? false,
                };
            }

            /// <summary>Items the character wields. FindInPack searches these too, as the production host does (MyInventory | MyEquippedItems).</summary>
            public readonly Dictionary<uint, WorldObject> Equipped = new Dictionary<uint, WorldObject>();

            public WorldObject FindInPack(uint guid) => Pack.TryGetValue(guid, out var wo) ? wo : Equipped.TryGetValue(guid, out var worn) ? worn : null;

            public bool HasPackRoomFor(WorldObject item) => !PackFull && (PackRoom ?? 1) > 0;

            public bool TryPutInPack(WorldObject item)
            {
                if (PackFull || RefusePut)
                    return false;

                if (PackRoom.HasValue)
                {
                    if (PackRoom <= 0)
                        return false;

                    PackRoom--;
                }

                item.ContainerId = Character;
                Pack[item.Guid.Full] = item;
                Log.Add($"MOVE:pack:{item.Guid.Full:X8}");
                return true;
            }

            public void SaveItem(WorldObject item, Action<bool> landed) => Complete(item, false, true, landed);

            public void DestroyItem(WorldObject item, bool inPack, Action<bool> landed)
            {
                if (inPack)
                {
                    Pack.Remove(item.Guid.Full);
                    Equipped.Remove(item.Guid.Full);
                }

                if (!DeferSaves && !FailSaves.Contains(item.Guid.Full))
                    MarkDestroyed(item);

                Log.Add($"MOVE:destroy:{item.Guid.Full:X8}");
                Complete(item, true, true, landed);
            }

            public void RunOnOwnerThread(Action action) => action();

            /// <summary>Shard loads that produced an object: a second live copy if the original is still in memory.</summary>
            public int Loads;

            /// <summary>Shard lookups that came back Held (a biota something else holds).</summary>
            public int HeldLookups;

            public ShardBiotaState LookUpShard(uint guid, out WorldObject loose, out uint? containerId, out string holder)
            {
                loose = null;
                containerId = null;
                holder = null;

                if (!Shard.TryGetValue(guid, out var p) || p.Destroyed)
                    return ShardBiotaState.Absent;

                if (p.Container != null || p.Wielder != null || p.Location != null)
                {
                    HeldLookups++;
                    containerId = p.Container;
                    holder = p.Container != null ? $"container 0x{p.Container:X8}" : p.Wielder != null ? $"wielder 0x{p.Wielder:X8}" : "it has a Location: on a landblock";
                    return ShardBiotaState.Held;
                }

                Loads++;
                loose = Rehydrate(guid, p);
                return ShardBiotaState.Loose;
            }

            /// <summary>Shard-only removals (no live object), in order.</summary>
            public readonly List<uint> ShardRemovals = new List<uint>();

            public void RemoveShardBiota(uint guid, Action<bool> landed)
            {
                ShardRemovals.Add(guid);
                Log.Add($"MOVE:destroy:{guid:X8}");

                var ok = !FailSaves.Contains(guid);

                void Fire()
                {
                    if (ok && Shard.TryGetValue(guid, out var p))
                        p.Destroyed = true;

                    landed?.Invoke(ok);
                }

                if (DeferSaves)
                    Deferred.Add(Fire);
                else
                    Fire();
            }

            private static WorldObject Rehydrate(uint guid, Persisted p)
            {
                var wo = WorldObjectFactory.CreateWorldObject(p.Snapshot.BuildRestoredBiota(guid));

                if (p.Location != null)
                    wo.Location = new Position(p.Location);

                return wo;
            }

            // ---- the screen ----

            public readonly List<string> Chats = new List<string>();
            public readonly List<string> Tells = new List<string>();
            public ulong? AskedFor;
            public string Prompt;
            public bool RefuseAsk;
            public readonly List<(WorldObject Item, WeaponQualityTier? TierBefore)> AfterTakes = new List<(WorldObject, WeaponQualityTier?)>();

            public void Chat(string text) => Chats.Add(text);

            public void Tell(string text) => Tells.Add(text);

            public bool AskToRoll(ulong batchId, string prompt)
            {
                if (RefuseAsk)
                    return false;

                AskedFor = batchId;
                Prompt = prompt;
                return true;
            }

            /// <summary>For each AfterTake: whether the item's delivery save had already reached the shard when it ran.</summary>
            public readonly List<bool> AfterTakeSawItSaved = new List<bool>();

            public void AfterTake(WorldObject item, WeaponQualityTier? tierBefore)
            {
                AfterTakes.Add((item, tierBefore));
                AfterTakeSawItSaved.Add(Shard.TryGetValue(item.Guid.Full, out var p) && p.Container == Character);
            }

            // ---- the restart ----

            /// <summary>
            /// A process restart: every in-memory object is dropped and the world is rebuilt from the persisted model
            /// alone - escrow contents, the pack, a fresh store over the same index rows - and the process maps forgotten.
            /// </summary>
            public void Restart()
            {
                SalvageRollerManager.ForgetProcessStateForTest();

                // writes still in flight die with the process
                Deferred.Clear();
                DeferSaves = false;

                World = Hooked(new FakeVaultWorld());

                World.Containers[VaultGuid] = NewContainer(VaultGuid);

                foreach (var escrowGuid in EscrowGuids)
                {
                    var escrow = NewContainer(escrowGuid);

                    foreach (var kvp in Shard.Where(k => !k.Value.Destroyed && k.Value.Container == escrowGuid).ToList())
                        Assert.IsTrue(escrow.TryAddToInventory(Rehydrate(kvp.Key, kvp.Value)), "rebuilding escrow");

                    World.Containers[escrowGuid] = escrow;
                }

                Pack.Clear();

                foreach (var kvp in Shard.Where(k => !k.Value.Destroyed && k.Value.Container == Character).ToList())
                {
                    var item = Rehydrate(kvp.Key, kvp.Value);
                    item.ContainerId = Character;
                    Pack[kvp.Key] = item;
                }

                Store = new AccountVaultStore(Account, backend, World);
            }
        }

        // =================================================================================================
        // the invariants
        // =================================================================================================

        /// <summary>Which audit event must be the most recent before each kind of value move.</summary>
        private static readonly Dictionary<string, SalvageRollerAuditEvent[]> AuditBefore = new Dictionary<string, SalvageRollerAuditEvent[]>
        {
            ["escrow"] = new[] { SalvageRollerAuditEvent.Escrowed, SalvageRollerAuditEvent.DeliveryFailed },
            ["pending"] = new[] { SalvageRollerAuditEvent.Rolled },
            ["destroy"] = new[] { SalvageRollerAuditEvent.HammersConsumed, SalvageRollerAuditEvent.ReconcileForward },
            ["taking"] = new[] { SalvageRollerAuditEvent.Taken },
            ["pack"] = new[] { SalvageRollerAuditEvent.Taken, SalvageRollerAuditEvent.ReconcileForward, SalvageRollerAuditEvent.Aborted, SalvageRollerAuditEvent.ReconcileAbort, SalvageRollerAuditEvent.HammersConsumed },
        };

        private void AssertEveryMoveHadItsAuditFirst()
        {
            string lastAudit = null;

            foreach (var entry in host.Log)
            {
                // an Error row is a record for staff, not a step: it neither licenses nor interrupts a move
                if (entry == "AUDIT:Error")
                    continue;

                if (entry.StartsWith("AUDIT:"))
                {
                    lastAudit = entry.Substring(6);
                    continue;
                }

                if (!entry.StartsWith("MOVE:"))
                    continue;

                var kind = entry.Split(':')[1];
                var allowed = AuditBefore[kind].Select(e => e.ToString()).ToList();

                Assert.IsTrue(lastAudit != null && allowed.Contains(lastAudit),
                    $"{entry} was preceded by audit {lastAudit ?? "<none>"}, expected one of {string.Join("/", allowed)}. Log: {string.Join(" ", host.Log)}");
            }
        }

        /// <summary>Every audit detail names the character, the account and the batch, so it reads on its own.</summary>
        private void AssertAuditDetailsAreSelfContained()
        {
            foreach (var row in host.Audits)
            {
                var detail = System.Text.Json.Nodes.JsonNode.Parse(row.Detail).AsObject();

                Assert.AreEqual(((SalvageRollerAuditEvent)row.Event).ToString(), (string)detail["event"]);
                Assert.AreEqual(Character, (uint)detail["character"]["id"]);
                Assert.AreEqual(Account, (uint)detail["account"]);
                Assert.IsNotNull(detail["batch"], $"{(SalvageRollerAuditEvent)row.Event}: the batch row is in the detail");
                Assert.IsNotNull(detail["moves"], $"{(SalvageRollerAuditEvent)row.Event}: the state before and after is in the detail");
            }
        }

        private int Locations(uint guid)
        {
            var count = 0;

            if (host.Pack.ContainsKey(guid))
                count++;

            if (host.Store.IsEscrowReady(out _) && host.Store.FindEscrowed(guid) != null)
                count++;

            return count;
        }

        /// <summary>A cancelled batch: everything back in the pack untouched, nothing stamped, nothing destroyed, no rows.</summary>
        private void AssertCancelledCleanly(Basket basket)
        {
            Assert.IsNull(host.Row, "no batch row is left behind");
            Assert.AreEqual(0, SalvageRollerManager.StrandedCount, "nothing is left held in memory");
            Assert.AreEqual(0, host.StampWrites, "a cancelled batch never stamps anything");

            foreach (var item in basket.All)
            {
                var guid = item.Guid.Full;

                Assert.AreEqual(1, Locations(guid), $"0x{guid:X8}: one live copy, in one place");
                Assert.IsTrue(host.Pack.ContainsKey(guid), $"0x{guid:X8} is back in the pack");
                Assert.AreEqual(guid, host.Pack[guid].Guid.Full, "the guid is unchanged");
                Assert.AreEqual(Character, host.Shard[guid].Container, $"0x{guid:X8}: its pack placement is what the shard holds");
                Assert.IsFalse(host.Shard[guid].Destroyed);
            }

            Assert.AreEqual(Canon(basket.Original), Canon(host.Shard[basket.Target.Guid.Full].Snapshot), "the target is exactly as it was sold");

            AssertEveryMoveHadItsAuditFirst();
            AssertAuditDetailsAreSelfContained();
        }

        /// <summary>A taken batch: the target in the pack with result k applied ONCE and stamped ONCE, every Hammer destroyed, no rows.</summary>
        private void AssertTakenCleanly(Basket basket, SalvageRollerCandidate candidate, ulong batchId)
        {
            var guid = basket.Target.Guid.Full;

            Assert.IsNull(host.Row, "no batch row is left behind");
            Assert.AreEqual(0, SalvageRollerManager.StrandedCount, "nothing is left held in memory");
            Assert.AreEqual(1, host.StampWrites, "the stamp is set exactly once");

            Assert.AreEqual(1, Locations(guid), "one live copy of the target");
            Assert.IsTrue(host.Pack.ContainsKey(guid), "the target is in the pack");
            Assert.AreEqual(guid, host.Pack[guid].Guid.Full, "the guid is unchanged end to end");
            Assert.AreEqual(Character, host.Shard[guid].Container);
            Assert.IsFalse(host.Shard[guid].Destroyed);

            var expected = Expected(basket, candidate, batchId);
            Assert.AreEqual(expected, Canon(host.Shard[guid].Snapshot), "the shard holds original + result k + stamp, applied once");
            Assert.AreEqual(expected, Canon(DuoGemSnapshot.Capture(host.Pack[guid])), "and so does the live object");

            foreach (var hammer in basket.Hammers)
            {
                Assert.AreEqual(0, Locations(hammer.Guid.Full), $"Hammer 0x{hammer.Guid.Full:X8} is nowhere");
                Assert.IsTrue(host.Shard[hammer.Guid.Full].Destroyed, $"Hammer 0x{hammer.Guid.Full:X8} was destroyed");
            }

            AssertEveryMoveHadItsAuditFirst();
            AssertAuditDetailsAreSelfContained();
        }

        private static SalvageRollerCandidate Candidate(string payload, int seq)
        {
            Assert.IsTrue(SalvageRollerPayload.TryRead(payload, out var read, out var error), error);
            return read.Find(seq);
        }

        // =================================================================================================
        // the happy path (the control every crash case is measured against)
        // =================================================================================================

        /// <summary>
        /// Review F4: the fill after a Rolled Yes is keyed on the seller's guid (host.CharacterId). Drives OnRollAnswered
        /// with a real station holding a view for the seller AND for a second character, and checks that only the
        /// seller's view is filled, with exactly RollCount rows.
        /// </summary>
        [TestMethod]
        public void RolledYes_FillsTheSellersOwnView_WithRollCountRows_AndLeavesAnotherViewerAlone()
        {
            const uint OtherViewer = 0x50000077;

            var savedDisplayGuid = SalvageRollerManager.DisplayGuid;
            var nextDisplay = 0x71E40000u;

            try
            {
                SalvageRollerVendorTests.SeedVendorConstructible();
                SalvageRollerManager.DisplayGuid = () => new ObjectGuid(++nextDisplay);

                var station = SalvageRollerVendorTests.MakeStation();
                station.OpenView(host.CharacterId);
                station.OpenView(OtherViewer);

                var otherRow = new GenericObject(new Weenie
                {
                    WeenieClassId = 8100,
                    WeenieType = WeenieType.Generic,
                    PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "the other viewer's row" } },
                }, new ObjectGuid(++nextDisplay));

                station.SetRows(OtherViewer, new[] { new SalvageRollerCandidate { Seq = 1 } }, c => otherRow);
                Assert.AreEqual(1, station.RowCount(OtherViewer), "precondition: the other viewer holds one row");

                var basket = Sold(charges: 10);

                Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
                Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true, station));

                Assert.AreEqual(host.Row.RollCount, station.RowCount(host.CharacterId), "the seller's own view holds every candidate");
                Assert.AreEqual(1, station.RowCount(OtherViewer), "another viewer's view is untouched");
                Assert.IsFalse(otherRow.IsDestroyed);
                Assert.IsTrue(station.TryGetRowSeq(OtherViewer, otherRow.Guid, out var otherSeq) && otherSeq == 1);

                SalvageRollerVendorTests.AssertRegistryMatchesViews(station, "after the Rolled Yes fill");
            }
            finally
            {
                SalvageRollerManager.DisplayGuid = savedDisplayGuid;
                PropertyManager.ModifyBool("vendor_shop_uses_generator", DefaultPropertyManager.DefaultBooleanProperties["vendor_shop_uses_generator"].Item);
            }
        }

        // =================================================================================================
        // the station's open: escrow warm-up and the deferred fill
        // =================================================================================================

        /// <summary>Captures the open's scheduled looks instead of running them on a timer.</summary>
        private sealed class RecordedOpenSteps
        {
            public readonly List<Action> Scheduled = new List<Action>();
            public readonly List<double> Delays = new List<double>();
            public bool Viewing = true;
            public int Resends;

            public SalvageRollerManager.OpenSteps Steps => new SalvageRollerManager.OpenSteps
            {
                Schedule = (delay, action) => { Delays.Add(delay); Scheduled.Add(action); },
                StillViewing = () => Viewing,
                Resend = () => Resends++,
            };

            /// <summary>Runs the oldest scheduled look. False when nothing was scheduled.</summary>
            public bool RunNext()
            {
                if (Scheduled.Count == 0)
                    return false;

                var next = Scheduled[0];
                Scheduled.RemoveAt(0);
                next();
                return true;
            }
        }

        private Container Escrow => host.World.Containers[host.EscrowGuids[0]];

        /// <summary>A Pending batch for the harness character, its rows shown once, then the view emptied as a relog leaves it.</summary>
        private SalvageRollerVendorTests.TestStation PendingBatchAtAStation(ref uint nextDisplay)
        {
            SalvageRollerVendorTests.SeedVendorConstructible();

            var start = nextDisplay;
            var counter = start;
            SalvageRollerManager.DisplayGuid = () => new ObjectGuid(++counter);

            var station = SalvageRollerVendorTests.MakeStation();
            station.OpenView(host.CharacterId);

            var basket = Sold(charges: 10);

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true, station));
            Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State, "precondition: a Pending batch");

            station.ClearRows(host.CharacterId);
            host.Tells.Clear();
            host.Chats.Clear();

            nextDisplay = counter + 1000;
            return station;
        }

        [TestMethod]
        public void Open_WarmsTheEscrow_EvenWithNoBatch_SoTheFirstSaleDoesNotStartTheLoad()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();

            var station = SalvageRollerVendorTests.MakeStation();
            station.OpenView(host.CharacterId);
            var steps = new RecordedOpenSteps();

            Assert.IsFalse(host.Store.IsIndexLoaded, "precondition: a cold store");
            Assert.IsNull(host.Row, "precondition: no batch, so reconcile itself never touches the store");

            SalvageRollerManager.OpenStation(host, station, steps.Steps);

            Assert.IsTrue(host.Store.IsIndexLoaded, "the Use read the escrow, which is what starts its load");
            CollectionAssert.AreEqual(new[] { SalvageRollerManager.HowToMessage }, host.Chats);
            Assert.AreEqual(0, steps.Scheduled.Count, "no batch: nothing to wait for");
        }

        [TestMethod]
        public void Open_PendingBatchWithTheEscrowStillLoading_DefersTheFill_ThenShowsTheRowsAndResendsThePanel()
        {
            var savedDisplayGuid = SalvageRollerManager.DisplayGuid;
            var nextDisplay = 0x71E50000u;

            try
            {
                var station = PendingBatchAtAStation(ref nextDisplay);
                var steps = new RecordedOpenSteps();

                FakeVaultWorld.SetInventoryLoaded(Escrow, false);

                SalvageRollerManager.OpenStation(host, station, steps.Steps);

                Assert.AreEqual(0, station.RowCount(host.CharacterId), "nothing to show yet");
                Assert.AreEqual(0, host.Tells.Count, "and the player is NOT told to come back");
                Assert.AreEqual(1, steps.Scheduled.Count, "one deferred look is scheduled");
                Assert.AreEqual(SalvageRollerManager.EscrowWaitIntervalSeconds, steps.Delays[0]);
                Assert.IsTrue(SalvageRollerManager.IsOpenWaitingForEscrow(host.CharacterId));

                Assert.IsTrue(steps.RunNext());
                Assert.AreEqual(1, steps.Scheduled.Count, "still loading: it looks again");
                Assert.AreEqual(0, steps.Resends);

                FakeVaultWorld.SetInventoryLoaded(Escrow, true);

                Assert.IsTrue(steps.RunNext());

                Assert.AreEqual(host.Row.RollCount, station.RowCount(host.CharacterId), "the rows are filled once the escrow is loaded");
                Assert.AreEqual(1, steps.Resends, "and the panel is re-sent so the client shows them");
                Assert.AreEqual(0, steps.Scheduled.Count, "no further looks");
                Assert.AreEqual(0, host.Tells.Count);
                Assert.IsFalse(SalvageRollerManager.IsOpenWaitingForEscrow(host.CharacterId));
                Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State, "the batch itself is untouched by the wait");
                SalvageRollerVendorTests.AssertRegistryMatchesViews(station, "after the deferred fill");
            }
            finally
            {
                SalvageRollerManager.DisplayGuid = savedDisplayGuid;
                PropertyManager.ModifyBool("vendor_shop_uses_generator", DefaultPropertyManager.DefaultBooleanProperties["vendor_shop_uses_generator"].Item);
            }
        }

        [TestMethod]
        public void Open_PendingBatchWithTheEscrowNeverLoading_GivesUpAfterTheBound_WithTheExistingLine()
        {
            var savedDisplayGuid = SalvageRollerManager.DisplayGuid;
            var nextDisplay = 0x71E60000u;

            try
            {
                var station = PendingBatchAtAStation(ref nextDisplay);
                var steps = new RecordedOpenSteps();

                FakeVaultWorld.SetInventoryLoaded(Escrow, false);

                SalvageRollerManager.OpenStation(host, station, steps.Steps);

                var looks = 0;

                while (steps.RunNext())
                {
                    looks++;
                    Assert.IsTrue(looks <= SalvageRollerManager.EscrowWaitAttempts, "the wait is bounded");
                }

                Assert.AreEqual(SalvageRollerManager.EscrowWaitAttempts, looks);
                Assert.IsTrue(SalvageRollerManager.EscrowWaitAttempts * SalvageRollerManager.EscrowWaitIntervalSeconds <= 15, "the bound is seconds, not minutes");
                CollectionAssert.AreEqual(new[] { AccountVaultStore.EscrowUnavailableMessage }, host.Tells, "the last look says why, once");
                Assert.AreEqual(0, station.RowCount(host.CharacterId));
                Assert.IsFalse(SalvageRollerManager.IsOpenWaitingForEscrow(host.CharacterId));
            }
            finally
            {
                SalvageRollerManager.DisplayGuid = savedDisplayGuid;
                PropertyManager.ModifyBool("vendor_shop_uses_generator", DefaultPropertyManager.DefaultBooleanProperties["vendor_shop_uses_generator"].Item);
            }
        }

        [TestMethod]
        public void Open_DeferredFill_StandsDown_WhenThePlayerStopsViewing_OrUsesTheStationAgain()
        {
            var savedDisplayGuid = SalvageRollerManager.DisplayGuid;
            var nextDisplay = 0x71E70000u;

            try
            {
                var station = PendingBatchAtAStation(ref nextDisplay);

                FakeVaultWorld.SetInventoryLoaded(Escrow, false);

                // walked away: the look finds the player no longer viewing and does nothing at all
                var away = new RecordedOpenSteps();
                SalvageRollerManager.OpenStation(host, station, away.Steps);
                away.Viewing = false;
                FakeVaultWorld.SetInventoryLoaded(Escrow, true);

                Assert.IsTrue(away.RunNext());
                Assert.AreEqual(0, away.Scheduled.Count);
                Assert.AreEqual(0, away.Resends);
                Assert.AreEqual(0, station.RowCount(host.CharacterId));
                Assert.AreEqual(0, host.Tells.Count);
                Assert.IsFalse(SalvageRollerManager.IsOpenWaitingForEscrow(host.CharacterId));

                // a second Use while the first still waits: the first stands down, the second fills
                FakeVaultWorld.SetInventoryLoaded(Escrow, false);
                var first = new RecordedOpenSteps();
                var second = new RecordedOpenSteps();

                SalvageRollerManager.OpenStation(host, station, first.Steps);
                SalvageRollerManager.OpenStation(host, station, second.Steps);
                FakeVaultWorld.SetInventoryLoaded(Escrow, true);

                Assert.IsTrue(first.RunNext());
                Assert.AreEqual(0, first.Resends, "the older open stands down");
                Assert.AreEqual(0, station.RowCount(host.CharacterId));

                Assert.IsTrue(second.RunNext());
                Assert.AreEqual(1, second.Resends);
                Assert.AreEqual(host.Row.RollCount, station.RowCount(host.CharacterId));
            }
            finally
            {
                SalvageRollerManager.DisplayGuid = savedDisplayGuid;
                PropertyManager.ModifyBool("vendor_shop_uses_generator", DefaultPropertyManager.DefaultBooleanProperties["vendor_shop_uses_generator"].Item);
            }
        }

        [TestMethod]
        public void Open_ADegradedEscrow_IsNeverWaitedFor()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();

            host.World.FailLoadContainerGuids.Add(host.EscrowGuids[0]);
            host.Row = new CharacterSalvageBatch
            {
                Id = 9, CharacterId = Character, AccountId = Account, ItemGuid = 0x7D6CFFFF, State = (byte)SalvageBatchState.Pending, RollCount = 1,
            };

            var station = SalvageRollerVendorTests.MakeStation();
            station.OpenView(host.CharacterId);
            var steps = new RecordedOpenSteps();

            SalvageRollerManager.OpenStation(host, station, steps.Steps);

            Assert.IsTrue(host.Store.EscrowDegradedForTest, "precondition: the escrow is degraded");
            Assert.AreEqual(0, steps.Scheduled.Count, "a degraded escrow does not heal in seconds, so nothing waits");
            CollectionAssert.AreEqual(new[] { AccountVaultStore.EscrowDegradedMessage }, host.Tells);
        }

        /// <summary>Review F6: a wait whose player no longer has a view on the station (the sweep, a logout) does nothing.</summary>
        [TestMethod]
        public void Open_DeferredFill_StandsDown_WhenTheViewIsGone()
        {
            var savedDisplayGuid = SalvageRollerManager.DisplayGuid;
            var nextDisplay = 0x71E80000u;

            try
            {
                var station = PendingBatchAtAStation(ref nextDisplay);
                var steps = new RecordedOpenSteps();

                FakeVaultWorld.SetInventoryLoaded(Escrow, false);
                SalvageRollerManager.OpenStation(host, station, steps.Steps);

                station.DropView(host.CharacterId);
                FakeVaultWorld.SetInventoryLoaded(Escrow, true);

                Assert.IsTrue(steps.RunNext());
                Assert.AreEqual(0, steps.Scheduled.Count);
                Assert.AreEqual(0, steps.Resends);
                Assert.IsFalse(station.HasView(host.CharacterId), "the wait never re-creates a view");
                Assert.AreEqual(0, station.DefaultItemsForSale.Count, "and fills nothing");
                Assert.AreEqual(0, host.Tells.Count);
                Assert.IsFalse(SalvageRollerManager.IsOpenWaitingForEscrow(host.CharacterId));
            }
            finally
            {
                SalvageRollerManager.DisplayGuid = savedDisplayGuid;
                PropertyManager.ModifyBool("vendor_shop_uses_generator", DefaultPropertyManager.DefaultBooleanProperties["vendor_shop_uses_generator"].Item);
            }
        }

        /// <summary>Review F4: a store whose index read FAILED is not a load in progress, so the open does not wait on it.</summary>
        [TestMethod]
        public void Open_AFailedIndexRead_IsNotWaitedFor()
        {
            SalvageRollerVendorTests.SeedVendorConstructible();

            backend.FailVaultRead = true;
            host.Row = new CharacterSalvageBatch
            {
                Id = 9, CharacterId = Character, AccountId = Account, ItemGuid = 0x7D6CFFFF, State = (byte)SalvageBatchState.Pending, RollCount = 1,
            };

            var station = SalvageRollerVendorTests.MakeStation();
            station.OpenView(host.CharacterId);
            var steps = new RecordedOpenSteps();

            SalvageRollerManager.OpenStation(host, station, steps.Steps);

            Assert.IsTrue(host.Store.LastLoadFailed, "precondition: the index read failed");
            Assert.AreEqual(0, steps.Scheduled.Count, "every look would repeat the backend reads, so nothing waits");
            CollectionAssert.AreEqual(new[] { AccountVaultStore.EscrowUnavailableMessage }, host.Tells);
        }
        [TestMethod]
        public void SaleEscrowProbe_WhileTheEscrowIsLoading_RefusesWithTheLoadingLine_AndOtherwiseKeepsTheStoresOwn()
        {
            Assert.IsTrue(host.Store.IsEscrowReady(out _), "precondition: a ready escrow");
            Assert.AreEqual((true, (string)null), SalvageRollerEligibility.ProbeEscrow(host.Store));

            FakeVaultWorld.SetInventoryLoaded(Escrow, false);
            Assert.AreEqual((false, SalvageRollerEligibility.EscrowLoadingMessage), SalvageRollerEligibility.ProbeEscrow(host.Store));

            Assert.AreEqual((false, AccountVaultStore.EscrowUnavailableMessage), SalvageRollerEligibility.ProbeEscrow(null), "no store is not a load in progress");

            // a store whose index read FAILED is not loading: it keeps the store's own line
            var failing = new FakeVaultBackend { FailVaultRead = true };
            var failed = new AccountVaultStore(Account + 1, failing, new FakeVaultWorld());
            Assert.AreEqual((false, AccountVaultStore.EscrowUnavailableMessage), SalvageRollerEligibility.ProbeEscrow(failed));
        }
        [TestMethod]
        public void NoCrash_SellRollTake_LeavesExactlyTheChosenResult()
        {
            // A sword that already holds specials and sits above the bottom tier, so a reroll can move its tier either
            // way: that is what lets the tier-before assertion below tell "captured before the apply" from "after".
            var basket = Sold(RolledSword(), charges: 10);

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.IsNotNull(host.AskedFor);
            Assert.AreEqual((byte)SalvageBatchState.Escrowing, host.Row.State);
            Assert.AreEqual(20, host.Row.RollCount, "two Hammers of ten charges");
            StringAssert.StartsWith(host.Prompt, "Roll 20 times on Roller Sword with tourmaline? Every Hammer is spent, you must take one result, and the original is not returned.");

            foreach (var item in basket.All)
                Assert.IsNotNull(host.Store.FindEscrowed(item.Guid.Full), $"0x{item.Guid.Full:X8} is in escrow");

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
            Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State);
            Assert.IsFalse(host.Row.Items.Any(i => i.Role == (byte)SalvageBatchItemRole.Hammer), "the Hammer rows go with the Hammers");

            var batchId = host.Row.Id;
            var tierBefore = WeaponQualityTiers.Evaluate(host.Store.FindEscrowed(basket.Target.Guid.Full));

            // take a result whose tier differs from the original's, so a tier captured AFTER the apply reads differently
            Assert.IsTrue(SalvageRollerPayload.TryRead(host.Row.Payload, out var all, out _));
            var candidate = all.Candidates.FirstOrDefault(c => c.Summary.Tier != (int)tierBefore);

            if (candidate == null)
                Assert.Inconclusive($"none of 20 rolls moved the tier off {tierBefore}; the before/after discrimination needs one that does");

            var taken = SalvageRollerManager.Take(host, candidate.Seq);
            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, taken.Outcome, taken.Message);

            AssertTakenCleanly(basket, candidate, batchId);

            Assert.AreEqual(1, host.AfterTakes.Count);
            Assert.AreEqual(tierBefore, host.AfterTakes[0].TierBefore, "the tier is captured BEFORE the result is applied");
            Assert.IsFalse(host.AfterTakeSawItSaved[0], "the tier transition runs BEFORE the one save, so whatever it sets is in that save (step 7 review F7)");

            CollectionAssert.AreEqual(
                new[] { SalvageRollerAuditEvent.Sold, SalvageRollerAuditEvent.Escrowed, SalvageRollerAuditEvent.Rolled, SalvageRollerAuditEvent.HammersConsumed, SalvageRollerAuditEvent.Taken },
                host.Audits.Select(a => (SalvageRollerAuditEvent)a.Event).ToArray());

            Assert.AreEqual(SalvageRollerManager.ReconcileKind.NoBatch, SalvageRollerManager.Reconcile(host).Kind, "and there is nothing left to reconcile");
        }

        // =================================================================================================
        // a crash in each window, then a restart and reconcile
        // =================================================================================================

        [TestMethod]
        public void Crash_W0_DetachSavedNoRows_IsTheAcceptedLossWindow_NothingToReconcile()
        {
            var basket = Sold();
            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W0;

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Halted, Sell(basket));

            host.Restart();

            // DESIGN 5.6 W0: no row exists, so reconcile has nothing to find (the accepted risk of section 11)
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.NoBatch, SalvageRollerManager.Reconcile(host).Kind);
            Assert.IsNull(host.Row);
            Assert.AreEqual(0, host.Audits.Count, "nothing moved past the detach, so nothing was recorded");

            foreach (var item in basket.All)
                Assert.IsNull(host.Shard[item.Guid.Full].Container, "the items are as the detach save left them: unparented");
        }

        [TestMethod]
        public void Crash_W1_RowsWrittenNotEscrowed_ReconcileCancels()
        {
            var basket = Sold();
            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W1;

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Halted, Sell(basket));
            Assert.IsNotNull(host.Row);

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            AssertCancelledCleanly(basket);
            Assert.AreEqual(SalvageRollerAuditEvent.ReconcileAbort, (SalvageRollerAuditEvent)host.Audits.Last().Event);
        }

        [TestMethod]
        public void Crash_W2_EscrowedConfirmationOpen_ReconcileCancels()
        {
            var basket = Sold();
            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W2;

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Halted, Sell(basket));

            foreach (var item in basket.All)
                Assert.IsTrue(host.EscrowGuids.Contains(host.Shard[item.Guid.Full].Container ?? 0), "precondition: every item's escrow save landed");

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            AssertCancelledCleanly(basket);
        }

        [TestMethod]
        public void Crash_W3_PendingCommittedHammersNotDestroyed_ReconcileDestroysThem_ThenTheTakeWorks()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W3;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Halted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
            Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State);
            Assert.AreEqual(2, host.Row.Items.Count(i => i.Role == (byte)SalvageBatchItemRole.Hammer), "precondition: the Hammer rows survive the crash");

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Pending, result.Kind, result.Message);
            Assert.IsFalse(host.Row.Items.Any(i => i.Role == (byte)SalvageBatchItemRole.Hammer), "reconcile rolled the Hammers forward");

            // reconcile is safe to repeat
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Pending, SalvageRollerManager.Reconcile(host).Kind);

            var batchId = host.Row.Id;
            var candidate = Candidate(host.Row.Payload, 1);

            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, SalvageRollerManager.Take(host, 1).Outcome);

            AssertTakenCleanly(basket, candidate, batchId);
        }

        [TestMethod]
        public void Crash_W4_TakingCommittedSaveNotLanded_ReconcileAppliesOnceAndDelivers()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            var batchId = host.Row.Id;
            var candidate = Candidate(host.Row.Payload, 2);

            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W4;
            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Halted, SalvageRollerManager.Take(host, 2).Outcome);

            Assert.AreEqual((byte)SalvageBatchState.Taking, host.Row.State);
            Assert.AreEqual(0, host.StampWrites, "precondition: the result was applied in memory only");

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            AssertTakenCleanly(basket, candidate, batchId);
        }

        [TestMethod]
        public void Crash_W5_SaveLandedRowsNotDeleted_ReconcileOnlyDeletesTheRows()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            var batchId = host.Row.Id;
            var candidate = Candidate(host.Row.Payload, 4);

            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W5;
            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, SalvageRollerManager.Take(host, 4).Outcome, "the take itself completed; the crash is in the save callback");

            Assert.IsNotNull(host.Row, "precondition: the rows survived the crash");
            Assert.AreEqual(1, host.StampWrites, "precondition: the save that stamps it landed");

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            AssertTakenCleanly(basket, candidate, batchId);
        }

        // =================================================================================================
        // the rest of the state machine
        // =================================================================================================

        [TestMethod]
        public void AnsweringNo_CancelsAndHandsEverythingBack()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Aborted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, false));

            AssertCancelledCleanly(basket);
            Assert.AreEqual(SalvageRollerManager.AbortedMessage, host.Tells.Last());
        }

        [TestMethod]
        public void Reconcile_LeavesABatchWhoseConfirmationIsOpenAlone()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            var result = SalvageRollerManager.Reconcile(host);

            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Waiting, result.Kind);
            Assert.AreEqual((byte)SalvageBatchState.Escrowing, host.Row.State, "an open confirmation is never cancelled under the player");
            Assert.IsNotNull(host.Store.FindEscrowed(basket.Target.Guid.Full));
        }

        [TestMethod]
        public void ARollThatThrows_CancelsTheBatch()
        {
            SalvageRollerEngine.SerpentinePlanner = _ => (null, RerollRefusal.None);

            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket, SalvageRollerAction.SerpentineReroll));

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Aborted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            AssertCancelledCleanly(basket);
            Assert.AreEqual(SalvageRollerManager.RollFailedMessage, host.Tells.Last());
            StringAssert.Contains(host.Audits.Last().Detail, "the roll could not be made", "the exception text is in the record");
        }

        [TestMethod]
        public void TheRowsFailToWrite_EverythingIsHandedBackLoose_AndNothingIsEscrowed()
        {
            host.FailAddBatch = true;
            var basket = Sold();

            Assert.AreEqual(SalvageRollerManager.SellOutcome.HandedBack, Sell(basket));

            CollectionAssert.AreEquivalent(basket.All, host.LooseHandBack, "with no row, the hand-back is the only home");
            Assert.IsNull(host.Row);

            foreach (var item in basket.All)
                Assert.AreEqual(0, Locations(item.Guid.Full) - (host.Pack.ContainsKey(item.Guid.Full) ? 1 : 0), "never escrowed");
        }

        [TestMethod]
        public void AnAuditWriteFailure_RefusesTheStepItRecords()
        {
            // the Escrowed row fails: nothing is escrowed, and the cancel that follows has its own row
            var basket = Sold();
            host.FailAudit.Add(SalvageRollerAuditEvent.Escrowed);

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Aborted, Sell(basket));
            Assert.IsFalse(host.Log.Any(e => e.StartsWith("MOVE:escrow")), "no escrow save without its audit row");
            AssertCancelledCleanly(basket);
            Assert.AreEqual(0, host.Loads, "the items were live in memory, so none was reloaded from the shard as a second copy");

            // the Taken row fails: the take is refused and nothing moves
            host.FailAudit.Clear();
            var second = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(second));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            host.FailAudit.Add(SalvageRollerAuditEvent.Taken);
            var refused = SalvageRollerManager.Take(host, 1);

            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Refused, refused.Outcome);
            Assert.AreEqual(SalvageRollerManager.RecordFailedMessage, refused.Message);
            Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State, "Taking was never committed");
            Assert.IsNotNull(host.Store.FindEscrowed(second.Target.Guid.Full), "the target never left escrow");
            Assert.AreEqual(0, host.StampWrites);
        }

        [TestMethod]
        public void ADeliveryThatFails_ReturnsTheResultToEscrow_AndReconcileDeliversItWithoutApplyingTwice()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            var batchId = host.Row.Id;
            var candidate = Candidate(host.Row.Payload, 1);

            host.RefusePut = true;
            var deferred = SalvageRollerManager.Take(host, 1);

            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Deferred, deferred.Outcome);
            Assert.AreEqual((byte)SalvageBatchState.Taking, host.Row.State, "the row stays Taking");
            Assert.IsNotNull(host.Store.FindEscrowed(basket.Target.Guid.Full), "the result went back into escrow");
            Assert.AreEqual(1, host.StampWrites, "returned stamped, so the next attempt cannot apply it again");

            host.RefusePut = false;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            AssertTakenCleanly(basket, candidate, batchId);
        }

        [TestMethod]
        public void TheTake_RefusesBeforeAnyWrite_WithNoRoom_AnUnknownRow_OrAPayloadThatDoesNotMatchRollCount()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            var auditsBefore = host.Audits.Count;

            host.PackFull = true;
            Assert.AreEqual(SalvageRollerManager.TakeNeedsRoomMessage, SalvageRollerManager.Take(host, 1).Message);
            host.PackFull = false;

            Assert.AreEqual(SalvageRollerVendor.UnknownRowMessage, SalvageRollerManager.Take(host, 99).Message);

            // a payload one candidate short of roll_count (step 5 review F6, take half)
            Assert.IsTrue(SalvageRollerPayload.TryRead(host.Row.Payload, out var payload, out _));
            host.Row.Payload = new SalvageRollerPayload(payload.Candidates.Take(payload.Candidates.Count - 1)).Serialize();
            Assert.AreEqual(SalvageRollerManager.NeedsAttentionMessage, SalvageRollerManager.Take(host, 1).Message);

            var written = host.Audits.Skip(auditsBefore).ToList();
            Assert.IsFalse(written.Any(a => a.Event != (byte)SalvageRollerAuditEvent.Error), "no refusal recorded a step");
            Assert.AreEqual(1, written.Count, "the unreadable payload, and only it, left a record for staff (step 7 review F4)");
            Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State);
        }

        // =================================================================================================
        // step 7 review round 1
        // =================================================================================================

        private uint[] HammerGuids(Basket basket) => basket.Hammers.Select(h => h.Guid.Full).ToArray();

        private static Position Ground() => new Position(0xA9B40019, 84f, 7f, 94f, 0f, 0f, 0f, 1f, 0);

        /// <summary>A pack with room for the target only: the cancel delivers the target and leaves both Hammers in escrow.</summary>
        private Basket PartiallyCancelled()
        {
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            host.PackRoom = 1;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Aborted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, false));
            Assert.AreEqual(SalvageRollerManager.AbortIncompleteMessage, host.Tells.Last());

            var target = basket.Target.Guid.Full;
            Assert.IsTrue(host.Pack.ContainsKey(target), "precondition: the target went home");
            Assert.IsNotNull(host.Row, "the batch still holds the Hammers that did not fit");
            Assert.IsFalse(host.Row.Items.Any(i => i.Guid == target), "F1: the target is home and saved, so the batch no longer claims it");
            CollectionAssert.AreEquivalent(HammerGuids(basket), host.Row.Items.Select(i => i.Guid).ToArray(), "only the Hammers' rows stand");

            return basket;
        }

        [TestMethod]
        public void PartialCancel_ThenTheTargetIsDepositedInTheVault_TheNextUseFinishesTheCancel()
        {
            // reviewer probe 1 (step 7 review F1): before the fix the target's row outlived its delivery, so a target
            // the player then moved anywhere else was "nowhere to be found" and the batch could never be cancelled
            var basket = PartiallyCancelled();
            var target = basket.Target.Guid.Full;

            var moved = host.Pack[target];
            host.Pack.Remove(target);
            moved.ContainerId = host.VaultGuid;
            host.Persist(moved);

            host.PackRoom = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            Assert.IsNull(host.Row, "the batch is gone");
            Assert.AreEqual(host.VaultGuid, host.Shard[target].Container, "the deposited target is left where the player put it");
            Assert.IsFalse(host.Pack.ContainsKey(target));
            Assert.AreEqual(0, host.Loads);

            foreach (var hammer in HammerGuids(basket))
            {
                Assert.IsTrue(host.Pack.ContainsKey(hammer), $"Hammer 0x{hammer:X8} came home");
                Assert.AreEqual(Character, host.Shard[hammer].Container);
            }
        }

        [TestMethod]
        public void PartialCancel_ThenTheTargetIsDroppedOnTheGround_ReconcileNeverLoadsASecondCopy()
        {
            // reviewer probe 2 (step 7 review F1): before the fix reconcile loaded the dropped target from the shard as a
            // SECOND live copy of the guid already lying on the landblock
            var basket = PartiallyCancelled();
            var target = basket.Target.Guid.Full;

            var dropped = host.Pack[target];
            host.Pack.Remove(target);
            dropped.ContainerId = null;
            dropped.Location = Ground();
            host.Persist(dropped);

            host.PackRoom = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            Assert.AreEqual(0, host.Loads, "nothing was loaded from the shard");
            Assert.IsFalse(host.Pack.ContainsKey(target), "the target stays on the ground");
            Assert.IsNull(host.Row);
            Assert.IsNotNull(host.Shard[target].Location);
        }

        [TestMethod]
        public void ALooseBiotaWithALocation_IsNotLoaded_BecauseItIsOnALandblock()
        {
            // step 7 review F1(b): the backstop, reached when a row still names an item that is on the ground
            var basket = Sold();
            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W1;
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Halted, Sell(basket));

            var target = basket.Target.Guid.Full;
            host.Shard[target].Location = Ground();

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.NotNow, result.Kind, "the target cannot be brought home, so the cancel is incomplete");

            Assert.IsFalse(host.Pack.ContainsKey(target), "never loaded into the pack as a second copy");
            Assert.IsTrue(host.Audits.Any(a => a.Event == (byte)SalvageRollerAuditEvent.Error && a.Detail.Contains("has a Location")), "staff get a record naming why");
            CollectionAssert.AreEqual(new[] { target }, host.Row.Items.Select(i => i.Guid).ToArray(), "only the target's row stays, for staff");

            foreach (var hammer in HammerGuids(basket))
                Assert.IsTrue(host.Pack.ContainsKey(hammer), "the rest still came home");

            Assert.IsFalse(SalvageRollerManager.IsLoose(WithLocation(basket.Target), out var holder));
            StringAssert.Contains(holder, "Location");
        }

        [TestMethod]
        public void AbortAudit_ForAHammerHeldElsewhereInTheShard_SaysHeldWithAHolder_NotMissing()
        {
            // staff read the audit row alone: an item that still exists must never be reported MISSING
            var basket = Sold();
            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W1;
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Halted, Sell(basket));

            var hammer = HammerGuids(basket).First();
            host.Shard[hammer].Container = host.VaultGuid;

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            SalvageRollerManager.Reconcile(host);

            var abort = host.Audits.Single(a => a.Event == (byte)SalvageRollerAuditEvent.ReconcileAbort);
            var items = System.Text.Json.Nodes.JsonNode.Parse(abort.Detail)["items"].AsArray();
            var entry = items.Single(i => (uint)i["guid"] == hammer);

            Assert.AreEqual("Held", (string)entry["where"], "the Hammer still exists in the shard, held by something else");
            Assert.IsFalse(string.IsNullOrEmpty((string)entry["holder"]), "the audit row names the holder");
        }

        private static WorldObject WithLocation(WorldObject wo)
        {
            wo.ContainerId = null;
            wo.Location = Ground();
            return wo;
        }

        [TestMethod]
        public void ACancel_DeletesEachItemsRowOnlyWhenItsSaveLands_AndTheBatchRowLast()
        {
            // step 7 review F9: rows survive until the callbacks fire
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            host.DeferSaves = true;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Aborted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, false));
            Assert.AreEqual(SalvageRollerManager.AbortedMessage, host.Tells.Last());

            Assert.AreEqual(3, host.Row.Items.Count, "no save has landed, so every row stands");

            host.FireOne();
            Assert.AreEqual(2, host.Row.Items.Count, "one save landed: exactly that item's row went");
            Assert.IsFalse(host.Row.Items.Any(i => i.Guid == basket.Target.Guid.Full));

            host.FireDeferred();
            Assert.IsNull(host.Row, "the batch row goes once every item is home and saved");

            host.DeferSaves = false;
            AssertCancelledCleanly(basket);
        }

        [TestMethod]
        public void ACancelSaveThatDoesNotLand_KeepsThatItemsRow_AndTheNextUseFinishesIt()
        {
            // step 7 review F9: landed(false) keeps the row
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            var h0 = basket.Hammers[0].Guid.Full;
            host.FailSaves.Add(h0);

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Aborted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, false));

            Assert.IsNotNull(host.Row, "a save did not land, so the batch row stays");
            CollectionAssert.AreEqual(new[] { h0 }, host.Row.Items.Select(i => i.Guid).ToArray(), "and so does that item's row, and only that one");

            host.FailSaves.Clear();

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            AssertCancelledCleanly(basket);
        }

        [TestMethod]
        public void PendingAfterRestart_AHammerFoundOutsideEscrow_IsDestroyedWhereItIs_NotCountedAsConsumed()
        {
            // reviewer probe 3 (step 7 review F2(a)): before the fix a Hammer that was not in escrow counted as already
            // consumed and survived, so the roll was free
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W3;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Halted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            var h0 = basket.Hammers[0].Guid.Full;
            var h1 = basket.Hammers[1].Guid.Full;

            host.Shard[h0].Container = Character;   // the shard holds this Hammer in the pack
            host.Shard[h1].Container = null;        // and this one loose

            host.Restart();
            SalvageRollerManager.CrashHook = null;

            Assert.IsTrue(host.Pack.ContainsKey(h0), "precondition: the restart put Hammer 0 in the pack");

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Pending, result.Kind, result.Message);

            Assert.IsFalse(host.Pack.ContainsKey(h0), "the Hammer in the pack was consumed there");
            Assert.IsTrue(host.Shard[h0].Destroyed);
            Assert.IsTrue(host.Shard[h1].Destroyed, "the loose Hammer was loaded and consumed");
            Assert.IsFalse(host.Row.Items.Any(i => i.Role == (byte)SalvageBatchItemRole.Hammer));

            var batchId = host.Row.Id;
            var candidate = Candidate(host.Row.Payload, 1);

            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, SalvageRollerManager.Take(host, 1).Outcome);

            AssertTakenCleanly(basket, candidate, batchId);
        }

        [TestMethod]
        public void TheConfirmationOpensOnlyOnceEveryEscrowSaveHasLanded()
        {
            // step 7 review F2(b)
            var basket = Sold();
            host.DeferSaves = true;

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Escrowing, Sell(basket));
            Assert.IsNull(host.AskedFor, "nothing has landed, so nothing is asked");
            Assert.AreEqual(3, host.Deferred.Count, "one held escrow save per item");

            host.FireOne();
            host.FireOne();
            Assert.IsNull(host.AskedFor, "two of three saves landed: still not asked");

            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Waiting, SalvageRollerManager.Reconcile(host).Kind, "while the saves are out, reconcile leaves the batch alone");

            host.FireDeferred();
            Assert.IsNotNull(host.AskedFor, "the last save landed, so the question is put");
            Assert.AreEqual((byte)SalvageBatchState.Escrowing, host.Row.State);

            host.DeferSaves = false;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
        }

        [TestMethod]
        public void AnEscrowSaveThatDoesNotLand_CancelsTheBatchInsteadOfAsking()
        {
            // step 7 review F2(b)
            var basket = Sold();
            host.DeferSaves = true;
            host.FailSaves.Add(basket.Hammers[0].Guid.Full);

            Assert.AreEqual(SalvageRollerManager.SellOutcome.Escrowing, Sell(basket));

            host.FailSaves.Clear();
            host.FireDeferred();

            Assert.IsNull(host.AskedFor, "a roll is never offered over an escrow the shard did not record");
            Assert.IsFalse(SalvageRollerManager.IsAwaitingAnswer(host.Row?.Id ?? 0));
            Assert.AreEqual(SalvageRollerManager.StartFailedMessage, host.Tells.Last());

            host.DeferSaves = false;
            AssertCancelledCleanly(basket);
        }

        [TestMethod]
        public void ACommitWhoseAcknowledgementIsLost_RollsForward_AndNeverHandsTheHammersBack()
        {
            // step 7 review F2(c)
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            host.LoseCommitAck = true;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
            host.LoseCommitAck = false;

            Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State);
            Assert.IsFalse(host.Row.Items.Any(i => i.Role == (byte)SalvageBatchItemRole.Hammer), "the Hammers of the committed roll were consumed");

            foreach (var hammer in HammerGuids(basket))
            {
                Assert.IsFalse(host.Pack.ContainsKey(hammer), "no Hammer was handed back");
                Assert.IsTrue(host.Shard[hammer].Destroyed);
            }

            var batchId = host.Row.Id;
            var candidate = Candidate(host.Row.Payload, 1);

            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, SalvageRollerManager.Take(host, 1).Outcome);
            AssertTakenCleanly(basket, candidate, batchId);
        }

        [TestMethod]
        public void ACommitThatReallyFailed_StillCancels()
        {
            // the control for the test above: a false that IS a failure still aborts cleanly
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            host.FailCommit = true;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Aborted, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            Assert.AreEqual(SalvageRollerManager.RollFailedMessage, host.Tells.Last());
            AssertCancelledCleanly(basket);
        }

        [TestMethod]
        public void AbortBatch_RefusesUnlessAFreshReadShowsTheBatchEscrowing()
        {
            // step 7 review F2(c): a stale Escrowing copy must never cancel a committed batch
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            var stale = new CharacterSalvageBatch
            {
                Id = host.Row.Id, CharacterId = Character, AccountId = Account, ItemGuid = host.Row.ItemGuid, ItemWcid = host.Row.ItemWcid,
                Action = host.Row.Action, State = (byte)SalvageBatchState.Escrowing, RollCount = host.Row.RollCount,
            };

            foreach (var i in host.Row.Items)
                stale.Items.Add(new CharacterSalvageBatchItem { Guid = i.Guid, BatchId = i.BatchId, Role = i.Role });

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
            var audits = host.Audits.Count;

            Assert.AreEqual(SalvageRollerManager.AbortResult.NotEscrowing, SalvageRollerManager.AbortBatch(host, stale, SalvageRollerAuditEvent.Aborted, "a stale copy"));

            Assert.AreEqual((byte)SalvageBatchState.Pending, host.Row.State, "the committed batch is untouched");
            Assert.IsNotNull(host.Store.FindEscrowed(basket.Target.Guid.Full), "the target never left escrow");
            Assert.IsFalse(host.Pack.ContainsKey(basket.Target.Guid.Full));
            Assert.AreEqual(audits + 1, host.Audits.Count, "one record, and it is not a cancel");
            Assert.AreEqual((byte)SalvageRollerAuditEvent.Error, host.Audits.Last().Event);
        }

        [TestMethod]
        public void AnUnansweredConfirmation_IsAbandonedAfterLateAfterPlusAMargin_AndReconcileCancels()
        {
            // step 7 review F3
            var start = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            var now = start;
            SalvageRollerManager.Clock = () => now;

            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            var batchId = host.AskedFor.Value;

            now = start + TimeSpan.FromSeconds(20);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Waiting, SalvageRollerManager.Reconcile(host).Kind, "inside the answer window");

            now = start + SalvageRollerManager.AbandonAfter - TimeSpan.FromSeconds(1);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Waiting, SalvageRollerManager.Reconcile(host).Kind, "just inside the margin");

            now = start + SalvageRollerManager.AbandonAfter;
            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);
            Assert.IsFalse(SalvageRollerManager.IsAwaitingAnswer(batchId), "the abandoned entry is gone");

            AssertCancelledCleanly(basket);

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Ignored, SalvageRollerManager.OnRollAnswered(host, batchId, true), "a Yes that arrives later finds nothing to answer");
            Assert.IsTrue(SalvageRollerManager.AbandonAfter > Confirmation_ThreadGroupStart.LateAfter);
        }

        [TestMethod]
        public void ATakeThatFindsTheTargetMissing_WritesOneErrorAuditRow_AndAFailedErrorWriteChangesNothing()
        {
            // step 7 review F4
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            WorldObject pulled = null;
            Assert.IsTrue(host.Store.Enqueue(() => host.Store.TryReleaseEscrow(basket.Target.Guid.Full, out pulled, out _), out var thrown));
            Assert.IsNull(thrown);
            Assert.IsNotNull(pulled, "precondition: the target is out of escrow");

            var before = host.Audits.Count;

            var refused = SalvageRollerManager.Take(host, 1);
            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Refused, refused.Outcome);
            Assert.AreEqual(SalvageRollerManager.NeedsAttentionMessage, refused.Message);

            var written = host.Audits.Skip(before).ToList();
            Assert.AreEqual(1, written.Count, "exactly one row");
            Assert.AreEqual((byte)SalvageRollerAuditEvent.Error, written[0].Event);
            StringAssert.Contains(written[0].Detail, "missing");
            AssertAuditDetailsAreSelfContained();

            host.FailAudit.Add(SalvageRollerAuditEvent.Error);
            var again = SalvageRollerManager.Take(host, 1);

            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Refused, again.Outcome, "the Error row is a record, never a gate");
            Assert.AreEqual(SalvageRollerManager.NeedsAttentionMessage, again.Message);
            Assert.AreEqual(before + 1, host.Audits.Count);
        }

        [TestMethod]
        public void AHammerRowGoesOnlyWhenItsRemovalLands_AndARemovalThatFailsKeepsIt()
        {
            // step 7 review F6
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            var h0 = basket.Hammers[0].Guid.Full;
            var h1 = basket.Hammers[1].Guid.Full;

            host.DeferSaves = true;
            host.FailSaves.Add(h1);

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
            Assert.AreEqual(2, host.Row.Items.Count(i => i.Role == (byte)SalvageBatchItemRole.Hammer), "no removal has landed, so both rows stand");

            host.FireOne();
            CollectionAssert.AreEqual(new[] { h1 }, host.Row.Items.Where(i => i.Role == (byte)SalvageBatchItemRole.Hammer).Select(i => i.Guid).ToArray(), "Hammer 0's removal landed, so its row went");

            host.FireDeferred();
            CollectionAssert.AreEqual(new[] { h1 }, host.Row.Items.Where(i => i.Role == (byte)SalvageBatchItemRole.Hammer).Select(i => i.Guid).ToArray(), "Hammer 1's removal did not land, so its row stays");

            Assert.IsTrue(host.Shard[h0].Destroyed);
            Assert.IsFalse(host.Shard[h1].Destroyed);
        }

        [TestMethod]
        public void ADeliveryFinishedLater_TakesTheTierBeforeFromTheTakenAuditRow()
        {
            // step 7 review F7
            var basket = Sold(RolledSword(), charges: 10);
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            var batchId = host.Row.Id;
            var original = WeaponQualityTiers.Evaluate(host.Store.FindEscrowed(basket.Target.Guid.Full));

            Assert.IsTrue(SalvageRollerPayload.TryRead(host.Row.Payload, out var all, out _));
            var candidate = all.Candidates.FirstOrDefault(c => c.Summary.Tier != (int)original);

            if (candidate == null)
                Assert.Inconclusive($"none of 20 rolls moved the tier off {original}");

            host.RefusePut = true;
            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Deferred, SalvageRollerManager.Take(host, candidate.Seq).Outcome);
            Assert.AreEqual(0, host.AfterTakes.Count, "nothing was delivered, so no transition ran");
            host.RefusePut = false;

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            Assert.AreEqual(original, host.AfterTakes.Single().TierBefore, "the tier before is the one the Taken row recorded, not None and not the result's");
            Assert.IsFalse(host.AfterTakeSawItSaved.Single(), "and the transition ran before the delivery save");

            AssertTakenCleanly(basket, candidate, batchId);
        }

        [TestMethod]
        public void EveryItemSnapshotInTheAudit_IsTheWholeBiota_SoAllowedWielderSurvives()
        {
            // step 7 review F10: a refund is rebuilt from the audit rows alone
            const uint owner = 0x50009999;

            var sword = Weapon();
            sword.SetProperty(PropertyInstanceId.AllowedWielder, owner);

            var basket = Sold(sword);
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            var batchId = host.Row.Id;
            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, SalvageRollerManager.Take(host, 1).Outcome);

            foreach (var (ev, path) in new[] { (SalvageRollerAuditEvent.Sold, "target"), (SalvageRollerAuditEvent.Escrowed, "target"), (SalvageRollerAuditEvent.Rolled, "target"), (SalvageRollerAuditEvent.Taken, "before"), (SalvageRollerAuditEvent.Taken, "after") })
            {
                var row = host.Audits.Single(a => a.Event == (byte)ev);
                var item = System.Text.Json.Nodes.JsonNode.Parse(row.Detail)[path];
                var biota = SalvageRollerAuditSnapshot.Deserialize(item["snapshot"]);

                Assert.IsNotNull(biota, $"{ev}/{path}: the snapshot is a whole biota");
                Assert.AreEqual(sword.Guid.Full, biota.Id, $"{ev}/{path}: it names the item");
                Assert.AreEqual(sword.Guid.Full, (uint)item["guid"]);
                Assert.IsTrue(biota.PropertiesIID != null && biota.PropertiesIID.TryGetValue(PropertyInstanceId.AllowedWielder, out var wielder) && wielder == owner, $"{ev}/{path}: AllowedWielder survives");
            }

            var after = SalvageRollerAuditSnapshot.Deserialize(System.Text.Json.Nodes.JsonNode.Parse(host.Audits.Single(a => a.Event == (byte)SalvageRollerAuditEvent.Taken).Detail)["after"]["snapshot"]);
            Assert.AreEqual((long)batchId, after.PropertiesInt64[PropertyInt64.SalvageBatchApplied], "the post-take snapshot carries the stamp");
        }

        [TestMethod]
        public void TheFullBiotaSnapshot_RoundTripsEveryTable()
        {
            // step 7 review F10: every settable table of the runtime Biota is in the snapshot, by name, and the ones
            // DuoGemSnapshot drops come back with their values
            var weenie = new Weenie { WeenieClassId = 1 };
            var emote = new PropertiesEmote { Category = EmoteCategory.Use, Probability = 1f, Object = weenie };
            emote.PropertiesEmoteAction.Add(new PropertiesEmoteAction { Type = 7, Message = "hello" });
            weenie.PropertiesEmote = new List<PropertiesEmote> { emote };

            var biota = new ACE.Entity.Models.Biota
            {
                Id = 0x80004321,
                WeenieClassId = 7002,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesIID = new Dictionary<PropertyInstanceId, uint> { { PropertyInstanceId.AllowedWielder, 0x50001111 } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemWorkmanship, 8 } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, double.NaN } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Round Trip" } },
                PropertiesSpellBook = new Dictionary<int, float> { { 2366, 2f } },
                PropertiesPosition = new Dictionary<PositionType, PropertiesPosition> { { PositionType.Location, new PropertiesPosition { ObjCellId = 0xA9B40019, PositionX = 84f, RotationW = 1f } } },
                PropertiesEmote = new List<PropertiesEmote> { emote },
                PropertiesBook = new PropertiesBook { MaxNumPages = 3, MaxNumCharsPerPage = 1000 },
            };

            var json = SalvageRollerAuditSnapshot.Serialize(biota);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);

            Assert.AreEqual(SalvageRollerAuditSnapshot.Format, (string)node["format"]);

            foreach (var property in typeof(ACE.Entity.Models.Biota).GetProperties().Where(p => p.CanRead && p.CanWrite))
                Assert.IsTrue(node["biota"].AsObject().ContainsKey(property.Name), $"the snapshot carries Biota.{property.Name}");

            var back = SalvageRollerAuditSnapshot.Deserialize(node);

            Assert.AreEqual(0x50001111u, back.PropertiesIID[PropertyInstanceId.AllowedWielder]);
            Assert.IsTrue(double.IsNaN(back.PropertiesFloat[PropertyFloat.DamageVariance]));
            Assert.AreEqual("Round Trip", back.PropertiesString[PropertyString.Name]);
            Assert.AreEqual(2f, back.PropertiesSpellBook[2366]);
            Assert.AreEqual(0xA9B40019u, back.PropertiesPosition[PositionType.Location].ObjCellId);
            Assert.AreEqual("hello", back.PropertiesEmote.Single().PropertiesEmoteAction.Single().Message);
            Assert.IsNull(back.PropertiesEmote.Single().Object, "the emote's Weenie back-reference is not item data");
            Assert.AreEqual(3, back.PropertiesBook.MaxNumPages);
            Assert.AreSame(weenie, emote.Object, "serializing never touches the live biota");
        }

        // =================================================================================================
        // final review wave
        // =================================================================================================

        private int HammerRows() => host.Row?.Items.Count(i => i.Role == (byte)SalvageBatchItemRole.Hammer) ?? 0;

        [TestMethod]
        public void AFastSecondUse_BeforeTheHammerRemovalsLand_KeepsTheirRows_AndARestartLeaksNoHammer()
        {
            // the final review's F6 probe: the Hammers' removals are still queued when reconcile runs. Before the fix the
            // shard copy (still in escrow) read as "missing", the rows went at once, and a restart left both in escrow.
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            host.DeferSaves = true;
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
            Assert.AreEqual(2, HammerRows(), "precondition: no removal has landed");

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Pending, result.Kind, result.Message);
            Assert.AreEqual(2, HammerRows(), "a Hammer still recorded in escrow is not consumed until its removal lands");
            Assert.IsFalse(host.Audits.Any(a => a.Event == (byte)SalvageRollerAuditEvent.Error), "an expected in-flight removal is not a refund");
            Assert.AreEqual(2, host.ShardRemovals.Count, "each was removed from the shard by guid, queued behind the first removal");

            host.Restart();   // the queued removals die with the process

            foreach (var hammer in HammerGuids(basket))
                Assert.IsNotNull(host.Store.FindEscrowed(hammer), "precondition: the restart brought the Hammer back into escrow");

            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Pending, SalvageRollerManager.Reconcile(host).Kind);
            Assert.AreEqual(0, HammerRows());

            foreach (var hammer in HammerGuids(basket))
            {
                Assert.IsNull(host.Store.FindEscrowed(hammer), $"Hammer 0x{hammer:X8} did not leak into escrow");
                Assert.IsTrue(host.Shard[hammer].Destroyed);
            }

            var batchId = host.Row.Id;
            var candidate = Candidate(host.Row.Payload, 1);

            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, SalvageRollerManager.Take(host, 1).Outcome);
            AssertTakenCleanly(basket, candidate, batchId);
        }

        [TestMethod]
        public void AHammerRemovalThatFailsInARunningProcess_KeepsItsRow_AndTheNextUseRemovesTheBiota()
        {
            // the round-1 untested residual: a failed removal with no restart. The in-memory copy is already gone.
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));

            var h0 = basket.Hammers[0].Guid.Full;
            host.FailSaves.Add(h0);

            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));
            CollectionAssert.AreEqual(new[] { h0 }, host.Row.Items.Where(i => i.Role == (byte)SalvageBatchItemRole.Hammer).Select(i => i.Guid).ToArray());
            Assert.IsNull(host.Store.FindEscrowed(h0), "precondition: the in-memory copy is gone");
            Assert.IsFalse(host.Shard[h0].Destroyed, "precondition: the shard still holds it in escrow");

            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Pending, SalvageRollerManager.Reconcile(host).Kind);
            Assert.AreEqual(1, HammerRows(), "landed(false): the row stays");
            Assert.IsFalse(host.Shard[h0].Destroyed);

            host.FailSaves.Clear();

            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Pending, SalvageRollerManager.Reconcile(host).Kind);
            Assert.AreEqual(0, HammerRows(), "the removal landed, so the row went");
            Assert.IsTrue(host.Shard[h0].Destroyed, "and the biota is gone from the shard");
            Assert.IsFalse(host.Audits.Any(a => a.Event == (byte)SalvageRollerAuditEvent.Error));
        }

        [TestMethod]
        public void AResultEquippedBetweenDeliveryAndReconcile_IsFoundThere_AndReconcileFinishes()
        {
            // final review item 2: FindInPack covers equipped items (production: MyInventory | MyEquippedItems, pinned
            // in SalvageRollerSchemaTests)
            var basket = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Asked, Sell(basket));
            Assert.AreEqual(SalvageRollerManager.RollOutcome.Rolled, SalvageRollerManager.OnRollAnswered(host, host.AskedFor.Value, true));

            SalvageRollerManager.CrashHook = p => p == SalvageRollerCrashPoint.W5;
            Assert.AreEqual(SalvageRollerManager.TakeOutcome.Delivered, SalvageRollerManager.Take(host, 4).Outcome);
            SalvageRollerManager.CrashHook = null;
            Assert.IsNotNull(host.Row, "precondition: delivered, rows left");

            var guid = basket.Target.Guid.Full;
            var item = host.Pack[guid];
            host.Pack.Remove(guid);
            item.ContainerId = null;
            item.WielderId = Character;
            host.Equipped[guid] = item;
            host.Persist(item);

            var result = SalvageRollerManager.Reconcile(host);
            Assert.AreEqual(SalvageRollerManager.ReconcileKind.Finished, result.Kind, result.Message);

            Assert.IsNull(host.Row);
            Assert.AreEqual(0, host.Loads);
            Assert.AreSame(item, host.Equipped[guid], "still the one live copy, still equipped");
            Assert.AreEqual(1, host.StampWrites);
            Assert.IsFalse(host.Audits.Any(a => a.Event == (byte)SalvageRollerAuditEvent.Error));
        }

        [TestMethod]
        public void ABusyConfirmation_SaysHandedBackOnlyWhenEverythingWas()
        {
            // final review item 5
            host.RefuseAsk = true;

            var all = Sold();
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Aborted, Sell(all));
            Assert.AreEqual(SalvageRollerManager.ConfirmationBusyMessage, host.Tells.Last());

            var partial = Sold();
            host.PackRoom = 1;
            Assert.AreEqual(SalvageRollerManager.SellOutcome.Aborted, Sell(partial));
            Assert.AreEqual(SalvageRollerManager.ConfirmationBusyIncompleteMessage, host.Tells.Last());
            Assert.IsFalse(host.Tells.Last().Contains("handed back"), "an incomplete cancel never claims the items were handed back");
        }

        [TestMethod]
        public void TheEscrowingStatus_SaysCancel_NotFinish()
        {
            var batch = new CharacterSalvageBatch { Action = (byte)SalvageRollerAction.TourmalineReroll, State = (byte)SalvageBatchState.Escrowing, RollCount = 10 };
            var text = SalvageRollerManager.DescribeStatus(batch);

            StringAssert.Contains(text, "Use the Salvage Roller to cancel it.");
            Assert.IsFalse(text.Contains("finish"), "an unrolled batch can only be cancelled");
        }

        [TestMethod]
        public void SortForDisplay_WeaponsByTierThenTotalThenSeq_SerpentineBySeq()
        {
            SalvageRollerCandidate C(int seq, int tier, double total) =>
                new SalvageRollerCandidate { Seq = seq, Diff = new SalvageRollerDiff(), Summary = new SalvageRollerSummary { Tier = tier, Total = total } };

            var cands = new[] { C(1, 1, 150), C(2, 3, 320), C(3, 3, 410), C(4, 1, 150), C(5, 0, 0) };

            CollectionAssert.AreEqual(new[] { 3, 2, 1, 4, 5 }, SalvageRollerManager.SortForDisplay(SalvageRollerAction.TourmalineReroll, cands).Select(c => c.Seq).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2, 1, 4, 5 }, SalvageRollerManager.SortForDisplay(SalvageRollerAction.ObsidianReroll, cands).Select(c => c.Seq).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, SalvageRollerManager.SortForDisplay(SalvageRollerAction.SerpentineReroll, cands).Select(c => c.Seq).ToArray());
        }

        private static SalvageRollerCandidate AspectCandidate(int seq, int attack, int defense, int utility) =>
            new SalvageRollerCandidate
            {
                Seq = seq,
                Diff = new SalvageRollerDiff(),
                Summary = SalvageRollerEngine.GreenJadeSummary(new AspectCounts(attack, defense, utility)),
            };

        [TestMethod]
        public void SortForDisplay_GreenJade_LeadingCountDesc_ThenRunnerUpDesc_ThenRollOrder()
        {
            var cands = new[]
            {
                AspectCandidate(1, 3, 3, 3),   // lead 3, runner-up 3
                AspectCandidate(2, 8, 1, 1),   // lead 8, runner-up 1
                AspectCandidate(3, 5, 5, 0),   // lead 5, runner-up 5
                AspectCandidate(4, 1, 8, 1),   // lead 8, runner-up 1 (ties #2: roll order)
                AspectCandidate(5, 7, 3, 0),   // lead 7, runner-up 3
                AspectCandidate(6, 7, 2, 1),   // lead 7, runner-up 2
                AspectCandidate(7, 4, 4, 2),   // lead 4, runner-up 4
            };

            CollectionAssert.AreEqual(new[] { 2, 4, 5, 6, 3, 7, 1 },
                SalvageRollerManager.SortForDisplay(SalvageRollerAction.GreenJadeSwap, cands).Select(c => c.Seq).ToArray());

            // the same candidates under Serpentine stay in roll order, so the green jade branch is what orders them
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7 },
                SalvageRollerManager.SortForDisplay(SalvageRollerAction.SerpentineReroll, cands).Select(c => c.Seq).ToArray());
        }

        [TestMethod]
        public void AspectLeadSuffix_NamesTheLeadingTypeAndCount_TiesNameEveryLeader()
        {
            Assert.AreEqual("Defense 8", SalvageRollerManager.AspectLeadSuffix(new AspectCounts(1, 8, 1)));
            Assert.AreEqual("Attack 6", SalvageRollerManager.AspectLeadSuffix(new AspectCounts(6, 2, 2)));
            Assert.AreEqual("Utility 4", SalvageRollerManager.AspectLeadSuffix(new AspectCounts(3, 3, 4)));
            Assert.AreEqual("Attack 5 Defense 5", SalvageRollerManager.AspectLeadSuffix(new AspectCounts(5, 5, 0)));
            Assert.AreEqual("Defense 4 Utility 4", SalvageRollerManager.AspectLeadSuffix(new AspectCounts(2, 4, 4)));
            Assert.AreEqual("Attack 3 Defense 3 Utility 3", SalvageRollerManager.AspectLeadSuffix(new AspectCounts(3, 3, 3)));
            Assert.AreEqual("no aspects", SalvageRollerManager.AspectLeadSuffix(new AspectCounts(0, 0, 0)));
        }

        [TestMethod]
        public void MaterializeDisplay_GreenJade_RowNameCarriesTheLeadingType_AndSearchFindsIt()
        {
            var savedDisplayGuid = SalvageRollerManager.DisplayGuid;
            uint counter = 0x7D5A0000;

            try
            {
                SalvageRollerManager.DisplayGuid = () => new ObjectGuid(++counter);

                var ring = JewelryAspectApplicatorTests.Jewelry("Test Ring", EquipMask.FingerWearLeft | EquipMask.FingerWearRight, 2, 2, 2);
                var original = DuoGemSnapshot.Capture(ring);

                SalvageRollerCandidate Cand(int seq, int a, int d, int u)
                {
                    var c = AspectCandidate(seq, a, d, u);
                    c.Diff.Ints[(int)PropertyInt.JewelryAspectAttack] = a;
                    c.Diff.Ints[(int)PropertyInt.JewelryAspectDefense] = d;
                    c.Diff.Ints[(int)PropertyInt.JewelryAspectUtility] = u;
                    return c;
                }

                var defense = SalvageRollerManager.MaterializeDisplay(original, Cand(3, 1, 8, 1), SalvageRollerAction.GreenJadeSwap);
                var tie = SalvageRollerManager.MaterializeDisplay(original, Cand(4, 5, 5, 0), SalvageRollerAction.GreenJadeSwap);
                var attack = SalvageRollerManager.MaterializeDisplay(original, Cand(5, 6, 2, 2), SalvageRollerAction.GreenJadeSwap);

                Assert.AreEqual("Test Ring (#3, Defense 8)", defense.Name);
                Assert.AreEqual("Test Ring (#4, Attack 5 Defense 5)", tie.Name);
                Assert.AreEqual("Test Ring (#5, Attack 6)", attack.Name);

                // the row carries the candidate's counts, not the original's
                Assert.AreEqual(new AspectCounts(1, 8, 1), JewelryAspectMath.ReadCounts(defense));

                // `/salvageroller search defense` runs the vendor's own matcher (ItemTextSearch) over the row: it finds the Defense-led
                // rows (including the tie) and leaves the Attack-led one out
                Assert.IsTrue(ItemTextSearch.TryCompile("defense", out var filter, out _));
                Assert.IsTrue(ItemTextSearch.IsMatch(filter, ItemTextSearch.SearchText(defense)));
                Assert.IsTrue(ItemTextSearch.IsMatch(filter, ItemTextSearch.SearchText(tie)));
                Assert.IsFalse(ItemTextSearch.IsMatch(filter, ItemTextSearch.SearchText(attack)));
            }
            finally
            {
                SalvageRollerManager.DisplayGuid = savedDisplayGuid;
            }
        }

        [TestMethod]
        public void DescribeStatus_NamesTheStateAndTheAction()
        {
            Assert.AreEqual(SalvageRollerManager.StatusNoBatchMessage, SalvageRollerManager.DescribeStatus((CharacterSalvageBatch)null));

            var batch = new CharacterSalvageBatch { Action = (byte)SalvageRollerAction.AmethystSwap, State = (byte)SalvageBatchState.Pending, RollCount = 30 };
            StringAssert.Contains(SalvageRollerManager.DescribeStatus(batch), "amethyst swaps, 30 rolls");

            batch.Action = (byte)SalvageRollerAction.GreenJadeSwap;
            StringAssert.Contains(SalvageRollerManager.DescribeStatus(batch), "green jade swaps, 30 rolls");
            batch.Action = (byte)SalvageRollerAction.AmethystSwap;

            batch.State = (byte)SalvageBatchState.Taking;
            batch.ChosenSeq = 12;
            StringAssert.Contains(SalvageRollerManager.DescribeStatus(batch), "result #12");
        }
    }
}
