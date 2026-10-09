using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Database.Adapter;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;

using Microsoft.EntityFrameworkCore;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ShardBiota = ACE.Database.Models.Shard.Biota;
using RuntimeBiota = ACE.Entity.Models.Biota;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Regression cover for the two shard-save failures that disconnected live players on 2026-09-07/08
    /// ("Server could not access your account information", i.e. CharacterError.AccountLogin, raised by
    /// Player_Tick when BiotaSaveFailed is set).
    ///
    /// Both are properties of <see cref="BiotaUpdater.UpdateDatabaseBiota"/>, so these tests run it against
    /// the REAL <see cref="ShardDbContext"/> model rather than a stand-in. The context is built on the MySQL
    /// provider with an explicit server version and never opens a connection: everything under test here is
    /// change-tracker and navigation-fixup behaviour, which is decided entirely in memory. A fake would have
    /// been worthless, because the bug IS EF Core's fixup behaviour.
    ///
    /// 1. "Collection was modified; enumeration operation may not execute." UpdateDatabaseBiota used to remove
    ///    from a navigation collection while enumerating that same collection. Safe for a dependent in the
    ///    Unchanged state (Remove only marks it Deleted), fatal for one in the Added state (Remove detaches it,
    ///    and fixup drops a detached dependent out of its principal's navigation immediately). Both halves are
    ///    pinned below, because the difference between them is the entire reason this survived for years and
    ///    then started firing.
    ///
    /// 2. MySQL 1062 "Duplicate entry '&lt;objectId&gt;-&lt;spellId&gt;-1'". The enchantment registry table has a
    ///    unique index over (object_Id, spell_Id, layer_Id) that is narrower than its primary key, so a runtime
    ///    registry holding one spell twice at one layer under different casters is unpersistable. The save must
    ///    cost the extra layer, not the player's session.
    /// </summary>
    [TestClass]
    public class BiotaUpdaterCollectionMutationTests
    {
        private const uint ObjectId = 0x5000001E;
        private const uint OtherCasterId = 0x50000099;
        private const int TinkeringSpellId = 4592;

        /// <summary>
        /// The real shard model on the real provider, with no connection behind it. DatabaseManager's
        /// auto-detect path is bypassed by passing the server version explicitly, so nothing here needs a
        /// running MySQL or a Config.js.
        /// </summary>
        private static ShardDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none",
                    new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;

            return new ShardDbContext(options);
        }

        private static ShardBiota AttachTarget(ShardDbContext context, params ushort[] boolTypes)
        {
            var target = new ShardBiota { Id = ObjectId, WeenieClassId = 1, WeenieType = 1 };

            foreach (var type in boolTypes)
                target.BiotaPropertiesBool.Add(new BiotaPropertiesBool { ObjectId = ObjectId, Type = type, Value = true });

            // Attach, not Add: this is the shape a cached biota has after being read back from the database,
            // which is what the cache-hit save path in ShardDatabaseWithCaching hands to UpdateDatabaseBiota.
            context.Attach(target);

            return target;
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 1: the EF Core behaviour that made removing-while-enumerating fatal
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void RemovingAnAddedDependent_ImmediatelyLeavesThePrincipalsNavigationCollection()
        {
            using var context = CreateContext();

            var target = AttachTarget(context, 1, 2);

            var added = new BiotaPropertiesBool { ObjectId = ObjectId, Type = 99, Value = true };
            target.BiotaPropertiesBool.Add(added);

            // What SaveChanges() does before it issues any SQL, and therefore the state the row is left in when
            // that SaveChanges() then fails: tracked, Added, still sitting in the navigation collection.
            context.ChangeTracker.DetectChanges();

            Assert.AreEqual(EntityState.Added, context.Entry(added).State, "fixup should have tracked the new child as Added");
            Assert.AreEqual(3, target.BiotaPropertiesBool.Count);

            context.BiotaPropertiesBool.Remove(added);

            // This is the mutation that broke the old foreach: Remove on an Added entity detaches it, and
            // navigation fixup takes a detached dependent out of the principal's collection right away.
            Assert.AreEqual(EntityState.Detached, context.Entry(added).State);
            Assert.AreEqual(2, target.BiotaPropertiesBool.Count, "an Added dependent must have been dropped from the navigation collection");
        }

        [TestMethod]
        public void RemovingAnUnchangedDependent_StaysInThePrincipalsNavigationCollection()
        {
            using var context = CreateContext();

            var target = AttachTarget(context, 1, 2);

            var persisted = target.BiotaPropertiesBool.First();

            context.BiotaPropertiesBool.Remove(persisted);

            // The ordinary case, and the reason removing-while-enumerating went unnoticed for years: a
            // persisted row is only MARKED Deleted, so the collection is not touched.
            Assert.AreEqual(EntityState.Deleted, context.Entry(persisted).State);
            Assert.AreEqual(2, target.BiotaPropertiesBool.Count);
        }

        [TestMethod]
        public void UpdateDatabaseBiota_WithAnAddedRowAbsentFromSource_DoesNotThrow()
        {
            using var context = CreateContext();

            // Types 1 and 2 are persisted rows; type 99 is the leftover of an earlier save whose SaveChanges()
            // failed on this same retained context, so it is still Added and no longer present in the source.
            var target = AttachTarget(context, 1, 2);

            var leftover = new BiotaPropertiesBool { ObjectId = ObjectId, Type = 99, Value = true };
            target.BiotaPropertiesBool.Add(leftover);
            context.ChangeTracker.DetectChanges();

            Assert.AreEqual(EntityState.Added, context.Entry(leftover).State, "the leftover of a failed save is tracked as Added");

            var source = new RuntimeBiota
            {
                Id = ObjectId,
                WeenieClassId = 1,
                PropertiesBool = new Dictionary<PropertyBool, bool> { { (PropertyBool)1, true } },
            };

            BiotaUpdater.UpdateDatabaseBiota(context, source, target);

            Assert.AreEqual(EntityState.Detached, context.Entry(leftover).State, "the stuck Added row should have been detached");
            Assert.IsFalse(target.BiotaPropertiesBool.Any(b => b.Type == 99));

            var droppedPersisted = target.BiotaPropertiesBool.Single(b => b.Type == 2);
            Assert.AreEqual(EntityState.Deleted, context.Entry(droppedPersisted).State, "a persisted row absent from source must still be deleted");

            var kept = target.BiotaPropertiesBool.Single(b => b.Type == 1);
            Assert.AreNotEqual(EntityState.Deleted, context.Entry(kept).State);
        }

        [TestMethod]
        public void UpdateDatabaseBiota_WithAnAddedEnchantmentRowAbsentFromSource_DoesNotThrow()
        {
            using var context = CreateContext();

            var target = new ShardBiota { Id = ObjectId, WeenieClassId = 1, WeenieType = 1 };
            target.BiotaPropertiesEnchantmentRegistry.Add(new BiotaPropertiesEnchantmentRegistry
            {
                ObjectId = ObjectId, SpellId = TinkeringSpellId, LayerId = 1, CasterObjectId = ObjectId,
            });
            context.Attach(target);

            // The exact prod shape: the duplicate-caster row that MySQL rejected is still Added on the
            // retained context, and by the next save the enchantment has left the runtime registry.
            var leftover = new BiotaPropertiesEnchantmentRegistry
            {
                ObjectId = ObjectId, SpellId = TinkeringSpellId, LayerId = 1, CasterObjectId = OtherCasterId,
            };
            target.BiotaPropertiesEnchantmentRegistry.Add(leftover);
            context.ChangeTracker.DetectChanges();

            Assert.AreEqual(EntityState.Added, context.Entry(leftover).State);

            var source = new RuntimeBiota { Id = ObjectId, WeenieClassId = 1 };

            BiotaUpdater.UpdateDatabaseBiota(context, source, target);

            Assert.AreEqual(EntityState.Detached, context.Entry(leftover).State);
            Assert.IsTrue(target.BiotaPropertiesEnchantmentRegistry.All(e => context.Entry(e).State == EntityState.Deleted),
                "an empty source registry must delete every persisted row");
        }

        // ---------------------------------------------------------------------------------------------
        // Defect 2: the unique index (object_Id, spell_Id, layer_Id) cannot hold two casters at one layer
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void UpdateDatabaseBiota_WithTwoSourceEnchantmentsAtOneLayer_StagesOnlyOneRow()
        {
            using var context = CreateContext();

            var target = new ShardBiota { Id = ObjectId, WeenieClassId = 1, WeenieType = 1 };
            target.BiotaPropertiesEnchantmentRegistry.Add(new BiotaPropertiesEnchantmentRegistry
            {
                ObjectId = ObjectId, SpellId = TinkeringSpellId, LayerId = 1, CasterObjectId = ObjectId, Duration = 5400,
            });
            context.Attach(target);

            var source = new RuntimeBiota
            {
                Id = ObjectId,
                WeenieClassId = 1,
                PropertiesEnchantmentRegistry = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry>
                {
                    // the player's own 90-minute self-cast
                    new ACE.Entity.Models.PropertiesEnchantmentRegistry
                    {
                        SpellId = TinkeringSpellId, LayerId = 1, CasterObjectId = ObjectId, Duration = 5400,
                    },
                    // and a second application of the same spell at the same layer from another caster,
                    // which the shard schema simply cannot store alongside it
                    new ACE.Entity.Models.PropertiesEnchantmentRegistry
                    {
                        SpellId = TinkeringSpellId, LayerId = 1, CasterObjectId = OtherCasterId, Duration = 60,
                    },
                },
            };

            BiotaUpdater.UpdateDatabaseBiota(context, source, target);

            // Newly staged rows only become tracked once change detection runs, which is what SaveChanges
            // does for itself.
            context.ChangeTracker.DetectChanges();
            var live = target.BiotaPropertiesEnchantmentRegistry
                .Where(e => context.Entry(e).State != EntityState.Deleted && context.Entry(e).State != EntityState.Detached)
                .ToList();

            Assert.AreEqual(1, live.Count, "only one row per (object, spell, layer) may be staged");
            Assert.AreEqual(ObjectId, live[0].CasterObjectId, "the first source entry wins, so the persisted row is updated rather than replaced");
            Assert.AreEqual(5400, live[0].Duration);

            var duplicateKeys = live.GroupBy(e => (e.SpellId, e.LayerId)).Any(g => g.Count() > 1);
            Assert.IsFalse(duplicateKeys, "no two staged rows may share (spell, layer)");
        }

        [TestMethod]
        public void UpdateDatabaseBiota_WithDistinctLayers_StagesBoth()
        {
            using var context = CreateContext();

            var target = new ShardBiota { Id = ObjectId, WeenieClassId = 1, WeenieType = 1 };
            context.Attach(target);

            var source = new RuntimeBiota
            {
                Id = ObjectId,
                WeenieClassId = 1,
                PropertiesEnchantmentRegistry = new List<ACE.Entity.Models.PropertiesEnchantmentRegistry>
                {
                    new ACE.Entity.Models.PropertiesEnchantmentRegistry { SpellId = TinkeringSpellId, LayerId = 1, CasterObjectId = ObjectId },
                    new ACE.Entity.Models.PropertiesEnchantmentRegistry { SpellId = TinkeringSpellId, LayerId = 2, CasterObjectId = OtherCasterId },
                },
            };

            BiotaUpdater.UpdateDatabaseBiota(context, source, target);

            // Newly staged rows only become tracked once change detection runs, which is what SaveChanges
            // does for itself.
            context.ChangeTracker.DetectChanges();
            var live = target.BiotaPropertiesEnchantmentRegistry
                .Where(e => context.Entry(e).State != EntityState.Deleted && context.Entry(e).State != EntityState.Detached)
                .ToList();

            Assert.AreEqual(2, live.Count, "distinct layers are exactly what the schema does allow, and must both persist");
            CollectionAssert.AreEquivalent(new[] { (ushort)1, (ushort)2 }, live.Select(e => e.LayerId).ToArray());
        }
    }
}
