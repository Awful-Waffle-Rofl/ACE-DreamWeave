using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A full corpse refuses loot (Container.TryAddToInventory returns false). The refused item must be handed
    /// back to the caller for placement in the world, never silently dropped on the floor of the call site.
    /// </summary>
    [TestClass]
    public class CorpseOverflowLootTests
    {
        private static uint nextGuid = 0x7F100000;

        private static Container CreateContainer(int itemCapacity)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemsCapacity, itemCapacity },
                    { PropertyInt.ContainersCapacity, 0 },
                },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject CreateItem()
        {
            var weenie = new Weenie { WeenieClassId = 9100, WeenieType = WeenieType.Stackable, PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.StackSize, 1 }, { PropertyInt.MaxStackSize, 1 } } };
            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void Full_container_refuses_the_item_and_leaves_it_unowned()
        {
            var container = CreateContainer(1);

            Assert.IsTrue(container.TryAddToInventory(CreateItem()), "first item fits");

            var refused = CreateItem();
            Assert.IsFalse(container.TryAddToInventory(refused), "second item is refused");

            Assert.AreEqual(1, container.Inventory.Count);
            Assert.IsNull(refused.ContainerId, "a refused item must not claim the container");
            Assert.IsFalse(container.Inventory.ContainsKey(refused.Guid));
        }

        [TestMethod]
        public void AddToCorpseOrCollect_returns_the_refused_item_and_not_the_accepted_one()
        {
            var corpse = CreateContainer(1);
            var refused = new List<WorldObject>();
            var accepted = CreateItem();
            var overflow = CreateItem();

            // The helper takes the Container base type (Corpse derives from it), so a plain Container exercises it.
            Assert.IsTrue(Creature.AddToCorpseOrCollect(corpse, accepted, refused), "room: accepted");
            Assert.AreEqual(0, refused.Count, "an accepted item is not collected");

            Assert.IsFalse(Creature.AddToCorpseOrCollect(corpse, overflow, refused), "full: refused");
            Assert.AreEqual(1, refused.Count);
            Assert.AreSame(overflow, refused[0]);
            Assert.IsFalse(corpse.Inventory.ContainsKey(overflow.Guid));
        }

        [TestMethod]
        public void AddToCorpseOrCollect_with_no_corpse_collects_everything()
        {
            var refused = new List<WorldObject>();
            var item = CreateItem();

            Assert.IsFalse(Creature.AddToCorpseOrCollect(null, item, refused));
            Assert.AreSame(item, refused.Single());
        }

        [TestMethod]
        public void GenerateTreasure_adds_to_the_corpse_only_through_the_collecting_helper()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");

            foreach (var signature in new[]
            {
                "private List<WorldObject> GenerateTreasure(DamageHistoryInfo killer, Corpse corpse)",
                "private List<WorldObject> GenerateTreasure_Olthoi(DamageHistoryInfo killer, Corpse corpse)",
            })
            {
                var body = PooledLootSourceText.MethodBody(src, signature);

                Assert.IsFalse(body.Contains("corpse.TryAddToInventory("), "direct corpse add in " + signature + " bypasses the refusal handling");
                StringAssert.Contains(body, "AddToCorpseOrCollect(corpse,");
            }
        }

        [TestMethod]
        public void CreateCorpse_places_every_refused_item_in_the_world()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");
            var body = PooledLootSourceText.MethodBody(src, "void CreateCorpse(DamageHistoryInfo killer");

            // Both generators feed the one `overflow` list: the Olthoi slag path is not dropped on the floor.
            StringAssert.Contains(body, "? GenerateTreasure(killer, corpse)\n                    : GenerateTreasure_Olthoi(killer, corpse);");
            StringAssert.Contains(body, "DropLootInWorld(overflow, corpseLocation, Guid);");
            StringAssert.Contains(body, "[LOOT][OVERFLOW]");
            StringAssert.Contains(body, "DropLootInWorld(loot, Location, Guid);");
        }

        [TestMethod]
        public void TryGenerateRare_drops_a_refused_rare_in_the_world()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Corpse.cs");
            var body = PooledLootSourceText.MethodBody(src, "public void TryGenerateRare(DamageHistoryInfo killer)");

            StringAssert.Contains(body, "placed = Creature.DropLootInWorld(new[] { wo }, Location, Guid);");
            // the ground drop runs the same flags and bookkeeping as a corpse add
            Assert.IsTrue(body.IndexOf("if (placed)") > body.IndexOf("placed = Creature.DropLootInWorld"), "flags and bookkeeping follow the drop");
            Assert.IsTrue(body.IndexOf("ApplyRareFoundBookkeeping(") > body.IndexOf("if (placed)"));
        }

        [TestMethod]
        public void Wielded_block_uses_the_helper_result_and_never_adds_to_droppedItems_directly()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");
            var body = PooledLootSourceText.MethodBody(src, "private List<WorldObject> GenerateTreasure(DamageHistoryInfo killer, Corpse corpse)");

            StringAssert.Contains(body, "var took = AddToCorpseOrCollect(corpse, item, droppedItems);");
            StringAssert.Contains(body, "if (took)\n                    EnqueueBroadcast(");
            Assert.IsFalse(body.Contains("droppedItems.Add("), "every collection into droppedItems goes through AddToCorpseOrCollect");
        }

        [TestMethod]
        public void Olthoi_slag_is_collected_into_the_returned_list()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs");
            var body = PooledLootSourceText.MethodBody(src, "private List<WorldObject> GenerateTreasure_Olthoi(DamageHistoryInfo killer, Corpse corpse)");

            StringAssert.Contains(body, "AddToCorpseOrCollect(corpse, slag, refused);");
            StringAssert.Contains(body, "return refused;");
            Assert.AreEqual(1, body.Split(new[] { "new List<WorldObject>()" }, System.StringSplitOptions.None).Length - 1, "only the one returned list is created");
        }
    }
}
