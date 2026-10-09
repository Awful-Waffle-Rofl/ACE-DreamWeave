using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Database.Adapter;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

using Microsoft.EntityFrameworkCore;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ShardBiota = ACE.Database.Models.Shard.Biota;
using RuntimeBiota = ACE.Entity.Models.Biota;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A shared cooldown must be ONE registry entry per cooldown spell id, restarted in place.
    ///
    /// Soul Tether (Player.CanSkipCombatPetSummonCooldown) lets a summoning essence through
    /// WorldObject.CheckUseRequirements while the shared combat-essence cooldown is still live, and
    /// WorldObject.OnActivate then calls EnchantmentManager.StartCooldown unconditionally. StartCooldown used
    /// to blind-append a new layer-1 entry, leaving two entries for one spell id at one layer - a state the
    /// shard's unique index over (object_Id, spell_Id, layer_Id) cannot store, so BiotaUpdater kept the older,
    /// shorter one and warned on every save.
    ///
    /// The refresh keeps the surviving entry's CasterObjectId and LayerId, because those are part of the
    /// shard primary key: a stable key makes the save an UPDATE instead of a delete-plus-insert under the
    /// unique index. The BiotaUpdater tests below pin that through the real ShardDbContext model.
    ///
    /// No Player is constructed (it fails in this test host's static initializer, see
    /// ClassAbilityEnchantmentBandTests), so the manager tests run StartCooldown on a bare GenericObject.
    /// </summary>
    [TestClass]
    public class CooldownRefreshTests
    {
        /// <summary>The combat essence shared cooldown id; any value works, this one mirrors the real case.</summary>
        private const int SharedCooldownId = 213;

        private static int CooldownSpellId => (int)(EnchantmentManager.SpellCategory_Cooldown | SharedCooldownId);

        private static WorldObject CreateBareWorldObject(uint id)
        {
            var biota = new RuntimeBiota
            {
                Id = id,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        private static WorldObject CreateCooldownItem(uint id, double duration)
        {
            var item = CreateBareWorldObject(id);
            item.CooldownId = SharedCooldownId;
            item.CooldownDuration = duration;
            return item;
        }

        private static List<PropertiesEnchantmentRegistry> CooldownEntries(WorldObject wo)
        {
            return wo.Biota.PropertiesEnchantmentRegistry.Where(e => e.SpellId == CooldownSpellId).ToList();
        }

        // ---------------------------------------------------------------------------------------------
        // EnchantmentManager.StartCooldown
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void StartCooldown_FirstUse_AddsOneEntryAtLayerOne()
        {
            var owner = CreateBareWorldObject(0x50000101);
            var essence = CreateCooldownItem(0x80000101, 45.0);

            Assert.IsTrue(owner.EnchantmentManager.StartCooldown(essence));

            var entries = CooldownEntries(owner);
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual((ushort)1, entries[0].LayerId);
            Assert.AreEqual(essence.Guid.Full, entries[0].CasterObjectId);
            Assert.AreEqual(45.0, entries[0].Duration);
            Assert.AreEqual(0.0, entries[0].StartTime);
            Assert.IsFalse(owner.EnchantmentManager.CheckCooldown(SharedCooldownId));
        }

        [TestMethod]
        public void StartCooldown_SameItemWhileLive_RestartsTheOneEntryAtFullDuration()
        {
            var owner = CreateBareWorldObject(0x50000102);
            var essence = CreateCooldownItem(0x80000102, 45.0);

            owner.EnchantmentManager.StartCooldown(essence);
            var first = CooldownEntries(owner).Single();
            first.StartTime = -30;  // 30 of 45 seconds elapsed

            owner.EnchantmentManager.StartCooldown(essence);

            var entries = CooldownEntries(owner);
            Assert.AreEqual(1, entries.Count, "a Soul Tether resummon must not append a second cooldown entry");
            Assert.AreSame(first, entries[0], "the existing entry is refreshed in place");
            Assert.AreEqual(0.0, entries[0].StartTime);
            Assert.AreEqual(45f, owner.EnchantmentManager.GetCooldown(SharedCooldownId), "the cooldown restarts at full length");
        }

        [TestMethod]
        public void StartCooldown_DifferentItemSharingTheCooldown_RefreshesInPlaceAndKeepsTheKey()
        {
            var owner = CreateBareWorldObject(0x50000103);
            var essenceA = CreateCooldownItem(0x80000103, 45.0);
            var essenceB = CreateCooldownItem(0x80000104, 60.0);

            owner.EnchantmentManager.StartCooldown(essenceA);
            var first = CooldownEntries(owner).Single();
            first.StartTime = -40;

            owner.EnchantmentManager.StartCooldown(essenceB);

            var entries = CooldownEntries(owner);
            Assert.AreEqual(1, entries.Count, "one entry per cooldown spell id, whatever the caster");
            Assert.AreSame(first, entries[0]);
            Assert.AreEqual(essenceA.Guid.Full, entries[0].CasterObjectId,
                "CasterObjectId is part of the shard primary key and must not change, or the save becomes a delete plus insert under the unique index");
            Assert.AreEqual((ushort)1, entries[0].LayerId);
            Assert.AreEqual(0.0, entries[0].StartTime);
            Assert.AreEqual(60.0, entries[0].Duration, "the restart takes the duration of the item just used");
        }

        [TestMethod]
        public void StartCooldown_WithAPreexistingDuplicate_CollapsesToOneEntry()
        {
            var owner = CreateBareWorldObject(0x50000105);
            var essenceA = CreateCooldownItem(0x80000105, 45.0);
            var essenceB = CreateCooldownItem(0x80000106, 45.0);

            // The state the old blind append left behind: two layer-1 entries for one cooldown spell id.
            owner.Biota.PropertiesEnchantmentRegistry.AddEnchantment(new PropertiesEnchantmentRegistry
            {
                SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = essenceA.Guid.Full, StartTime = -20, Duration = 45,
                SpellCategory = (SpellCategory)EnchantmentManager.SpellCategory_Cooldown,
            }, owner.BiotaDatabaseLock);
            owner.Biota.PropertiesEnchantmentRegistry.AddEnchantment(new PropertiesEnchantmentRegistry
            {
                SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = essenceB.Guid.Full, StartTime = 0, Duration = 45,
                SpellCategory = (SpellCategory)EnchantmentManager.SpellCategory_Cooldown,
            }, owner.BiotaDatabaseLock);

            owner.EnchantmentManager.StartCooldown(essenceB);

            var entries = CooldownEntries(owner);
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual(essenceA.Guid.Full, entries[0].CasterObjectId,
                "the FIRST entry survives, because it is the one BiotaUpdater persisted when both existed");
            Assert.AreEqual(0.0, entries[0].StartTime);
        }

        [TestMethod]
        public void StartCooldown_OtherCooldownIds_AreUntouched()
        {
            var owner = CreateBareWorldObject(0x50000107);
            var essence = CreateCooldownItem(0x80000107, 45.0);
            var gem = CreateBareWorldObject(0x80000108);
            gem.CooldownId = 5;
            gem.CooldownDuration = 180.0;

            owner.EnchantmentManager.StartCooldown(gem);
            owner.EnchantmentManager.StartCooldown(essence);
            owner.EnchantmentManager.StartCooldown(essence);

            Assert.AreEqual(2, owner.Biota.PropertiesEnchantmentRegistry.Count);
            Assert.IsTrue(owner.Biota.PropertiesEnchantmentRegistry.All(e => e.LayerId == 1), "distinct cooldown spell ids all stay at layer 1");
        }

        [TestMethod]
        public void StartCooldown_ItemWithoutCooldownId_DoesNothing()
        {
            var owner = CreateBareWorldObject(0x50000109);
            var item = CreateBareWorldObject(0x80000109);

            Assert.IsFalse(owner.EnchantmentManager.StartCooldown(item));
            Assert.AreEqual(0, owner.Biota.PropertiesEnchantmentRegistry?.Count ?? 0);
        }

        // ---------------------------------------------------------------------------------------------
        // PropertiesEnchantmentRegistryExtensions.AddOrRefreshBySpell
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void AddOrRefreshBySpell_OnlyTouchesTheSameSpellId()
        {
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            var other = new PropertiesEnchantmentRegistry { SpellId = 4592, LayerId = 1, CasterObjectId = 1, Duration = 5400, StartTime = -100 };
            var registry = new List<PropertiesEnchantmentRegistry> { other };

            var added = registry.AddOrRefreshBySpell(new PropertiesEnchantmentRegistry { SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = 2, Duration = 45 }, rwLock, out var removed);

            Assert.IsNull(removed);
            Assert.AreEqual(2, registry.Count);
            Assert.AreEqual(-100, other.StartTime, "an unrelated spell must not be refreshed");
            Assert.AreEqual(2u, added.CasterObjectId, "with nothing to refresh the new entry is appended as given");
        }

        [TestMethod]
        public void AddOrRefreshBySpell_RemovesExtrasByReference_EvenWithTheSameCaster()
        {
            // Two entries from the SAME caster at the SAME layer: no field tells them apart, so any field-based
            // match could remove the survivor instead of the extra. The removal here must be by reference.
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            var survivor = new PropertiesEnchantmentRegistry { SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = 7, StartTime = -10, Duration = 45 };
            var extra = new PropertiesEnchantmentRegistry { SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = 7, StartTime = -5, Duration = 45 };
            var registry = new List<PropertiesEnchantmentRegistry> { survivor, extra };

            var result = registry.AddOrRefreshBySpell(new PropertiesEnchantmentRegistry { SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = 7, Duration = 45 }, rwLock, out var removed);

            Assert.AreSame(survivor, result);
            Assert.AreEqual(1, registry.Count);
            Assert.AreSame(survivor, registry[0]);
            Assert.AreEqual(1, removed.Count);
            Assert.AreSame(extra, removed[0]);
        }

        // ---------------------------------------------------------------------------------------------
        // Persistence: the refreshed entry saves as an UPDATE in one SaveChanges
        // ---------------------------------------------------------------------------------------------

        private const uint ObjectId = 0x5000010A;
        private const uint EssenceA = 0x8000010A;
        private const uint EssenceB = 0x8000010B;

        /// <summary>The real shard model on the real provider with no connection behind it, as in BiotaUpdaterCollectionMutationTests.</summary>
        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        private static ShardBiota AttachTargetWithPersistedCooldown(ShardDbContext context, uint caster, double startTime)
        {
            var target = new ShardBiota { Id = ObjectId, WeenieClassId = 1, WeenieType = 1 };
            target.BiotaPropertiesEnchantmentRegistry.Add(new BiotaPropertiesEnchantmentRegistry
            {
                ObjectId = ObjectId, SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = caster,
                StartTime = startTime, Duration = 45, SpellCategory = EnchantmentManager.SpellCategory_Cooldown,
            });

            // Attach: the shape a cached biota has after being read back from the database.
            context.Attach(target);

            return target;
        }

        [TestMethod]
        public void UpdateDatabaseBiota_CooldownRestartedByAnotherItem_IsASingleModifiedRow()
        {
            using var context = CreateContext();

            var target = AttachTargetWithPersistedCooldown(context, EssenceA, -40);

            // Run the real refresh against a runtime registry mirroring the persisted row, with essence B
            // restarting the shared cooldown.
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            var registry = new List<PropertiesEnchantmentRegistry>
            {
                new PropertiesEnchantmentRegistry
                {
                    SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = EssenceA, StartTime = -40, Duration = 45,
                    SpellCategory = (SpellCategory)EnchantmentManager.SpellCategory_Cooldown,
                },
            };
            registry.AddOrRefreshBySpell(new PropertiesEnchantmentRegistry
            {
                SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = EssenceB, StartTime = 0, Duration = 60,
                SpellCategory = (SpellCategory)EnchantmentManager.SpellCategory_Cooldown,
            }, rwLock, out _);

            var source = new RuntimeBiota { Id = ObjectId, WeenieClassId = 1, PropertiesEnchantmentRegistry = registry };

            BiotaUpdater.UpdateDatabaseBiota(context, source, target);
            context.ChangeTracker.DetectChanges();

            var states = context.ChangeTracker.Entries<BiotaPropertiesEnchantmentRegistry>().Select(e => e.State).ToList();

            Assert.AreEqual(1, states.Count, "exactly one tracked registry row");
            Assert.AreEqual(EntityState.Modified, states[0], "the restart must be an UPDATE of the existing row");

            var row = target.BiotaPropertiesEnchantmentRegistry.Single();
            Assert.AreEqual(EssenceA, row.CasterObjectId);
            Assert.AreEqual(0.0, row.StartTime);
            Assert.AreEqual(60.0, row.Duration);

            var entry = context.Entry(row);
            Assert.IsFalse(entry.Property(r => r.CasterObjectId).IsModified, "no key property is modified");
            Assert.IsFalse(entry.Property(r => r.LayerId).IsModified);
            Assert.IsFalse(entry.Property(r => r.SpellId).IsModified);
        }

        [TestMethod]
        public void UpdateDatabaseBiota_CasterChangeWouldHaveBeenADeletePlusInsert()
        {
            // The contrast that justifies keeping the caster: had the refresh rewritten CasterObjectId, the save
            // would stage a DELETE of the old row and an INSERT of a new one with the same
            // (object_Id, spell_Id, layer_Id), whose success depends on EF Core ordering the delete first.
            using var context = CreateContext();

            var target = AttachTargetWithPersistedCooldown(context, EssenceA, -40);

            var source = new RuntimeBiota
            {
                Id = ObjectId,
                WeenieClassId = 1,
                PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry>
                {
                    new PropertiesEnchantmentRegistry { SpellId = CooldownSpellId, LayerId = 1, CasterObjectId = EssenceB, StartTime = 0, Duration = 60 },
                },
            };

            BiotaUpdater.UpdateDatabaseBiota(context, source, target);
            context.ChangeTracker.DetectChanges();

            var states = context.ChangeTracker.Entries<BiotaPropertiesEnchantmentRegistry>()
                .Select(e => e.State).OrderBy(s => s).ToList();

            CollectionAssert.AreEqual(new[] { EntityState.Deleted, EntityState.Added }.OrderBy(s => s).ToList(), states);
        }
    }
}
