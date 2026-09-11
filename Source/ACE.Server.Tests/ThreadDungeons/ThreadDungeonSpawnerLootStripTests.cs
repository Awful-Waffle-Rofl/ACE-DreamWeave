using System.Collections.Generic;
using System.Collections.ObjectModel;

using ACE.Entity;
using ACE.Entity.Adapter;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The reference-sharing rule behind ThreadDungeonSpawner.StripCreateList.
    ///
    /// PropertiesCreateList is one of the four collections a WorldObject shares BY REFERENCE with the CACHED
    /// weenie: WorldObject.cs:125 builds the biota with referenceWeenieCollectionsForCommonProperties: true,
    /// and WeenieConverter.cs:77-83 then does a bare `result.PropertiesCreateList = weenie.PropertiesCreateList`.
    /// One cached weenie serves every instance of that wcid on the server, so Clear()-ing that collection on
    /// one dynamic-dungeon monster would strip the create list from every instance of the same creature
    /// server-wide - retail landblocks, vendors and quest NPCs included - until the world cache was
    /// invalidated. Only an ASSIGNMENT of a fresh collection is safe.
    ///
    /// These tests drive the real WeenieConverter rather than a stand-in, so the sharing they assert is the
    /// sharing the server actually performs. What they deliberately do NOT cover is stated in the file's last
    /// test-free note below.
    ///
    /// Internals are visible via InternalsVisibleTo in ACE.Server.csproj:15.
    /// </summary>
    [TestClass]
    public class ThreadDungeonSpawnerLootStripTests
    {
        private const uint CreatureWcid = 7979;   // Virulent Grievver, whose Contain row is the Vial of Organic Acid
        private const uint QuestItemWcid = 9098;  // Vial of Organic Acid, PropertyString.Quest = Feb01CLQuest5

        /// <summary>
        /// A weenie shaped like a roster creature that carries a create list: one Contain row (the quest item
        /// Creature_Death.cs:819-822 would instantiate at death) and one Wield row (the weapon
        /// Creature_Equipment.cs:611-636 hands it at construction, which the strip must not be able to reach).
        /// </summary>
        private static Weenie CachedWeenie()
        {
            var weenie = new Weenie
            {
                WeenieClassId = CreatureWcid,
                WeenieType = WeenieType.Creature,
                PropertiesCreateList = new Collection<PropertiesCreateList>()
            };

            weenie.PropertiesCreateList.Add(new PropertiesCreateList
            {
                WeenieClassId = QuestItemWcid,
                DestinationType = DestinationType.Contain,
                StackSize = 1,
                Palette = 0,
                Shade = 0,
                TryToBond = false
            });

            weenie.PropertiesCreateList.Add(new PropertiesCreateList
            {
                WeenieClassId = 30604,
                DestinationType = DestinationType.Wield,
                StackSize = 1,
                Palette = 0,
                Shade = 0,
                TryToBond = false
            });

            return weenie;
        }

        /// <summary>The exact conversion WorldObject.cs:125 performs for a new instance of a cached weenie.</summary>
        private static Biota NewInstanceOf(Weenie weenie, uint guid) =>
            WeenieConverter.ConvertToBiota(weenie, guid, false, true);

        [TestMethod]
        public void A_new_instance_shares_its_create_list_with_the_cached_weenie()
        {
            // The premise every other test here rests on. If this ever stops holding, the strip is harmless
            // by construction and these tests are measuring nothing - so assert it explicitly rather than
            // trusting the comment.
            var weenie = CachedWeenie();
            var biota = NewInstanceOf(weenie, 0x70000001);

            Assert.AreSame(weenie.PropertiesCreateList, biota.PropertiesCreateList,
                "WeenieConverter no longer reference-shares PropertiesCreateList; the strip's whole hazard has changed.");
        }

        [TestMethod]
        public void Stripping_one_instance_leaves_the_cached_weenie_intact()
        {
            var weenie = CachedWeenie();
            var biota = NewInstanceOf(weenie, 0x70000001);

            ThreadDungeonSpawner.StripCreateList(biota);

            Assert.AreEqual(0, biota.PropertiesCreateList.Count, "the run creature kept its create list");
            Assert.AreEqual(2, weenie.PropertiesCreateList.Count,
                "the CACHED weenie lost create-list rows; every instance of this wcid server-wide is now affected");
        }

        [TestMethod]
        public void Stripping_one_instance_leaves_a_sibling_instance_intact()
        {
            // The production symptom, in the shape it would actually appear: two objects built from the same
            // cached weenie, one of them a dynamic-dungeon monster and one of them anything else on the
            // server. Stripping the first must not touch the second.
            var weenie = CachedWeenie();
            var runCreature = NewInstanceOf(weenie, 0x70000001);
            var retailCreature = NewInstanceOf(weenie, 0x70000002);

            ThreadDungeonSpawner.StripCreateList(runCreature);

            Assert.AreEqual(0, runCreature.PropertiesCreateList.Count);
            Assert.AreEqual(2, retailCreature.PropertiesCreateList.Count,
                "a sibling instance lost its create list, so the strip mutated the shared collection");
        }

        [TestMethod]
        public void The_stripped_instance_gets_its_own_collection_not_a_shared_one()
        {
            var weenie = CachedWeenie();
            var biota = NewInstanceOf(weenie, 0x70000001);

            ThreadDungeonSpawner.StripCreateList(biota);

            Assert.AreNotSame(weenie.PropertiesCreateList, biota.PropertiesCreateList);
        }

        [TestMethod]
        public void Stripping_a_creature_with_no_create_list_is_a_no_op()
        {
            // A roster wcid with no create list at all: 353 of the 1544 shipped roster wcids are in this
            // shape, and ConvertToBiota leaves PropertiesCreateList null for them (instantiateEmptyCollections
            // is false at WorldObject.cs:125).
            var weenie = new Weenie { WeenieClassId = CreatureWcid, WeenieType = WeenieType.Creature };
            var biota = NewInstanceOf(weenie, 0x70000001);

            Assert.IsNull(biota.PropertiesCreateList);

            ThreadDungeonSpawner.StripCreateList(biota);

            Assert.IsNotNull(biota.PropertiesCreateList);
            Assert.AreEqual(0, biota.PropertiesCreateList.Count);
        }

        [TestMethod]
        public void A_null_biota_is_tolerated()
        {
            ThreadDungeonSpawner.StripCreateList(null);
        }

        /// <summary>A DB-free item WorldObject with the given starting DestinationType.</summary>
        private static WorldObject MakeItem(uint wcid, uint guid, DestinationType destinationType)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic
            };

            var item = new GenericObject(weenie, new ObjectGuid(guid));
            item.DestinationType = destinationType;
            return item;
        }

        [TestMethod]
        public void ClearDestinationType_clears_a_treasure_marked_item()
        {
            var item = MakeItem(30000, 0x70000001, DestinationType.Treasure);

            ThreadDungeonSpawner.ClearDestinationType(new[] { item });

            Assert.AreEqual(DestinationType.Undef, item.DestinationType);
        }

        [TestMethod]
        public void ClearDestinationType_sweeps_every_element_not_just_the_first()
        {
            // The already-Undef item goes FIRST and the Wield item goes SECOND: a sweep that stops after
            // one element would leave the Wield item untouched, so this ordering is what actually proves
            // every element is visited rather than just the head of the list.
            var alreadyUndefItem = MakeItem(30001, 0x70000002, DestinationType.Undef);
            var wieldItem = MakeItem(30000, 0x70000001, DestinationType.Wield);

            ThreadDungeonSpawner.ClearDestinationType(new List<WorldObject> { alreadyUndefItem, wieldItem });

            Assert.AreEqual(DestinationType.Undef, alreadyUndefItem.DestinationType);
            Assert.AreEqual(DestinationType.Undef, wieldItem.DestinationType);
        }

        [TestMethod]
        public void ClearDestinationType_tolerates_a_null_enumerable_and_null_elements()
        {
            ThreadDungeonSpawner.ClearDestinationType(null);

            var item = MakeItem(30000, 0x70000001, DestinationType.Wield);
            ThreadDungeonSpawner.ClearDestinationType(new List<WorldObject> { item, null });
        }

        // NOT COVERED HERE, and not fakeable in this harness:
        //
        //  * StripCorpseTransfer. It walks Creature.Inventory and Creature.EquippedObjects, and a live
        //    Creature cannot be constructed under the test harness - the constructor runs
        //    SetEphemeralValues, which calls GenerateWieldList -> WorldObjectFactory.CreateNewWorldObject ->
        //    DatabaseManager.World, and CreateListSelect additionally reads the trophy_drop_rate tunable,
        //    which throws with no configured PropertyManager.
        //  * The end-to-end death path. Creature_Death.GenerateTreasure needs a landblock, an EnterWorld and
        //    a killer, none of which exist here.
        //
        // Both are live-verification items: kill a run monster whose retail weenie carries a Contain row and
        // confirm the corpse holds only the profile roll.
    }
}
