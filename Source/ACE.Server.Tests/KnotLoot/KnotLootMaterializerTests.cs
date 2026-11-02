using System.Collections.Generic;
using System.Linq;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.KnotLoot;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>The materializer reads no settings; items are built from hand-made weenies.</summary>
    [TestClass]
    public class KnotLootMaterializerTests
    {
        private static uint nextGuid = 0x7F200000;

        private static WorldObject Make(uint wcid, WeenieType type)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = type,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)(type == WeenieType.Stackable ? ItemType.Misc : ItemType.Armor) },
                    { PropertyInt.MaxStackSize, 1000 },
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.Value, 10 },
                    { PropertyInt.EncumbranceVal, 1 },
                },
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Create(uint wcid) => Make(wcid, wcid == 20630 || wcid == 43901 ? WeenieType.Stackable : WeenieType.Generic);

        [TestMethod]
        public void Materialize_StampsOnlyLevelTableAndU()
        {
            var drops = new List<KnotLootDrop>
            {
                new(1007451, 1, KnotTraitId.ShieldTrade, 0.5),
                new(20630, 38, null, 0.0),
            };

            var items = KnotLootMaterializer.Create(drops, "grael", 300, Create);

            Assert.AreEqual(2, items.Count);

            var gear = items[0];
            Assert.AreEqual(300, gear.GetProperty(PropertyInt.KnotLootLevel));
            Assert.AreEqual("grael", gear.GetProperty(PropertyString.KnotLootTable));
            Assert.AreEqual(0.5, gear.GetProperty(PropertyFloat.KnotTraitShieldTrade).Value, 1e-9);
            Assert.IsNull(gear.GetProperty(PropertyFloat.KnotTraitCastSpeed));
            Assert.IsNull(gear.GetProperty(PropertyFloat.KnotTraitFrugalHand));
            Assert.IsNull(gear.GetProperty(PropertyFloat.KnotTraitWristTrade));
            Assert.IsNull(gear.GetProperty(PropertyInt.ArmorLevel));

            var cash = items[1];
            Assert.AreEqual(38, cash.StackSize);
            Assert.IsNull(cash.GetProperty(PropertyInt.KnotLootLevel));
            Assert.IsNull(cash.GetProperty(PropertyString.KnotLootTable));
            Assert.IsNull(cash.GetProperty(PropertyFloat.KnotTraitShieldTrade));
        }

        [TestMethod]
        public void Materialize_BatonPick_StampsLevelTableAndOverpoweringIntentU()
        {
            var drops = new List<KnotLootDrop>
            {
                new(1007603, 1, KnotTraitId.OverpoweringIntent, 0.25),
                new(1007604, 1, KnotTraitId.OverpoweringIntent, 0.75),
            };

            var items = KnotLootMaterializer.Create(drops, "matron", 350, Create);

            Assert.AreEqual(2, items.Count);

            foreach (var (baton, u) in new[] { (items[0], 0.25), (items[1], 0.75) })
            {
                Assert.AreEqual(350, baton.GetProperty(PropertyInt.KnotLootLevel), "9093 stamped");
                Assert.AreEqual("matron", baton.GetProperty(PropertyString.KnotLootTable), "string 9029 stamped");
                Assert.AreEqual(u, baton.GetProperty(PropertyFloat.KnotTraitOverpoweringIntent).Value, 1e-9, "9020 stamped");
                Assert.IsNull(baton.GetProperty(PropertyFloat.KnotTraitCastSpeed));
                Assert.IsNull(baton.GetProperty(PropertyFloat.KnotTraitShieldTrade));
                Assert.IsNull(baton.GetProperty(PropertyFloat.KnotTraitFrugalHand));
                Assert.IsNull(baton.GetProperty(PropertyFloat.KnotTraitWristTrade));
            }

            Assert.AreEqual(1007603u, items[0].WeenieClassId);
            Assert.AreEqual(1007604u, items[1].WeenieClassId);
        }

        [TestMethod]
        public void Materialize_NullCreate_IsEmpty_AndWarnsWithTableAndWcid()
        {
            var appender = new MemoryAppender();
            var hierarchy = (Hierarchy)LogManager.GetRepository(typeof(KnotLootMaterializer).Assembly);
            var root = hierarchy.Root;
            var priorLevel = root.Level;
            var priorConfigured = hierarchy.Configured;

            try
            {
                root.AddAppender(appender);
                root.Level = Level.All;
                hierarchy.Configured = true;

                var items = KnotLootMaterializer.Create(new List<KnotLootDrop> { new(1007603, 1, KnotTraitId.OverpoweringIntent, 0.5) }, "matron", 300, _ => null);

                Assert.AreEqual(0, items.Count);

                var warns = appender.GetEvents().Where(e => e.Level == Level.Warn && e.RenderedMessage.Contains("[KNOTLOOT]")).Select(e => e.RenderedMessage).ToList();
                Assert.AreEqual(1, warns.Count, "exactly one warn for the one skipped drop: " + string.Join(" | ", warns));
                StringAssert.Contains(warns[0], "table 'matron'");
                StringAssert.Contains(warns[0], "wcid 1007603");
            }
            finally
            {
                root.RemoveAppender(appender);
                root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;
            }
        }

        [TestMethod]
        public void Materialize_CurrencyStack_ClampsToMaxStackSize()
        {
            var drops = new List<KnotLootDrop>
            {
                new(20630, 5000, null, 0.9),
                new(43901, 999, null, 0.1),
            };

            var items = KnotLootMaterializer.Create(drops, "grael", 300, Create);

            Assert.AreEqual(1000, (int?)items[0].MaxStackSize,"harness control: the stub's cap is 1000");
            Assert.AreEqual(1000, items[0].StackSize, "5000 is clamped to the item's MaxStackSize");
            Assert.AreEqual(999, items[1].StackSize, "control: a stack under the cap is untouched");
        }

        [TestMethod]
        public void Materialize_NullFromFactory_IsSkipped()
        {
            var drops = new List<KnotLootDrop>
            {
                new(1007451, 1, KnotTraitId.ShieldTrade, 0.5),
                new(43901, 41, null, 0.0),
            };

            var items = KnotLootMaterializer.Create(drops, "grael", 300, wcid => wcid == 1007451 ? null : Create(wcid));

            Assert.AreEqual(1, items.Count);
            Assert.AreEqual(41, items[0].StackSize);
        }
    }
}
