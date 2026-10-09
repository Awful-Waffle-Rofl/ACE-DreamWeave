using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// THE guard behind salvage affinity, and the reason DungeonSalvageAffinity exists at all.
    ///
    /// Player_Crafting.HandleSalvaging skips any item where `Workmanship == null || Retained` using a BARE
    /// continue - no player message and no log line, unlike the MaterialType check immediately above it, which
    /// at least warns. WorldObject.Workmanship returns null exactly when PropertyInt.ItemWorkmanship is null,
    /// and none of the three shipped base weenies carries it (verified against ace_world on 2026-09-06: wcids
    /// 243 dinnerplate, 2393 gemamethyst and 2398 gemtourmaline all have ItemType and Value but no
    /// ItemWorkmanship). So an injected item that skipped the workmanship stamp would drop, appraise correctly
    /// as its material, and be SILENTLY IGNORED by the salvage panel - it would look completely right and do
    /// nothing.
    ///
    /// Stamp is tested rather than TryCreate because WorldObjectFactory needs the world database, which the
    /// unit-test harness has not got; the object here is built the same DB-free way
    /// ThreadDungeonSpawnerLootStripTests builds one.
    ///
    /// Internals are visible via InternalsVisibleTo in ACE.Server.csproj:15.
    /// </summary>
    [TestClass]
    public class DungeonSalvageAffinityTests
    {
        /// <summary>
        /// A stand-in for one of the three base weenies in the shape that matters here: no MaterialType and no
        /// ItemWorkmanship, which is exactly what the real ones carry.
        /// </summary>
        private static WorldObject BaseItem(uint wcid, uint guid)
            => new GenericObject(new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic }, new ObjectGuid(guid));

        [TestMethod]
        public void The_base_weenies_carry_no_workmanship_of_their_own()
        {
            // The premise the whole feature rests on. If a base ever started carrying ItemWorkmanship this
            // would stop being load-bearing, and the test below would pass for the wrong reason.
            var item = BaseItem(2398, 0x70000001);

            Assert.IsNull(item.ItemWorkmanship, "guard: the base carries no workmanship");
            Assert.IsNull(item.Workmanship, "guard: and so Workmanship is null, which HandleSalvaging skips silently");
        }

        [TestMethod]
        public void An_injected_item_gets_a_workmanship_and_the_expected_material()
        {
            // All three shipped materials, with their real base wcids.
            var cases = new[]
            {
                (Wcid: 2398u, Material: (int)MaterialType.Tourmaline),
                (Wcid: 2393u, Material: (int)MaterialType.Amethyst),
                (Wcid: 243u,  Material: (int)MaterialType.Obsidian),
            };

            var guid = 0x70000010u;

            foreach (var (wcid, material) in cases)
            {
                var item = BaseItem(wcid, guid++);

                DungeonSalvageAffinity.Stamp(item, material, tier: 6);

                Assert.IsNotNull(item.Workmanship,
                    $"wcid {wcid}: without a workmanship HandleSalvaging skips this item with a bare continue and the player is told nothing");
                Assert.IsTrue(item.Workmanship >= 1.0f && item.Workmanship <= 10.0f, $"wcid {wcid}: workmanship {item.Workmanship} out of range");
                Assert.AreEqual((MaterialType)material, item.MaterialType, $"wcid {wcid}: material");
            }
        }

        [TestMethod]
        public void The_material_values_the_shipped_rows_name_are_the_right_ones()
        {
            // Cheap, and it is what makes the numbers in modifiers.json readable at a glance.
            Assert.AreEqual(43, (int)MaterialType.Tourmaline);
            Assert.AreEqual(12, (int)MaterialType.Amethyst);
            Assert.AreEqual(69, (int)MaterialType.Obsidian);
        }

        [TestMethod]
        public void Stamping_a_null_item_is_tolerated()
        {
            DungeonSalvageAffinity.Stamp(null, (int)MaterialType.Tourmaline, 6);
        }

        [TestMethod]
        public void A_zero_base_wcid_creates_nothing()
        {
            // The one TryCreate branch reachable without a world database.
            Assert.IsNull(DungeonSalvageAffinity.TryCreate(0, (int)MaterialType.Tourmaline, 6));
        }

        // NOT COVERED HERE, and not fakeable in this harness:
        //
        //  * TryCreate's happy path and the death-path injection itself. WorldObjectFactory needs the world
        //    database and Creature_Death.GenerateTreasure needs a landblock, an EnterWorld and a killer.
        //  * HandleSalvaging end to end against an injected item, which needs a live Player.
        //
        // Both are live-verification items: open a run on a gem carrying an affinity modifier, kill until an
        // extra gem or dinner plate appears on a corpse, and confirm the salvage panel accepts it.
    }
}
