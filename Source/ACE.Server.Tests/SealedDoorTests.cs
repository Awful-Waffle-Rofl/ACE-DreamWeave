using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Door.SealedShut (the speed course boss fight's Seal door). A Door and a Creature can be built in this harness; a
    /// Player cannot (see the "new Player(" notes elsewhere in the suite), so the "player behind the door" case is pinned
    /// in source: the SealedShut check returns before ActOnUse computes `behind`, the path a locked door opens from.
    /// </summary>
    [TestClass]
    public class SealedDoorTests
    {
        private static uint nextGuid = 0x7F0D0000;

        [ClassInitialize]
        public static void Init(TestContext context) => TestGameTables.EnsureInitialized(); // a Creature's vitals need the tables

        private static Door ClosedDoor()
        {
            var door = new Door(new Weenie
            {
                WeenieClassId = 1006850,
                WeenieType = WeenieType.Door,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Sealed Test Door" } },
            }, new ObjectGuid(nextGuid++));

            Assert.IsFalse(door.IsOpen, "fixture: the door starts closed");
            return door;
        }

        private static Creature DoorOpeningCreature()
            => new Creature(new Weenie
            {
                WeenieClassId = 993501,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.AiOptions, 1 } },
            }, new ObjectGuid(nextGuid++));

        [TestMethod]
        public void Sealed_door_refuses_an_AI_creature_that_can_open_doors()
        {
            var door = ClosedDoor();
            var creature = DoorOpeningCreature();
            Assert.AreNotEqual(0, creature.AiOptions, "fixture: this creature opens doors (Door.OnCollideObject)");

            door.SealedShut = true;
            door.OnCollideObject(creature);

            Assert.IsFalse(door.IsOpen, "a sealed door stays shut when a door-opening creature walks into it");
        }

        [TestMethod]
        public void Sealed_door_ignores_a_direct_use()
        {
            var door = ClosedDoor();
            door.SealedShut = true;

            door.ActOnUse(DoorOpeningCreature());

            Assert.IsFalse(door.IsOpen);
        }

        [TestMethod]
        public void Control_unsealed_door_opens_for_the_same_creature()
        {
            var door = ClosedDoor();
            door.OnCollideObject(DoorOpeningCreature());

            Assert.IsTrue(door.IsOpen, "control: without the seal the same collision opens the door, so the sealed test discriminates");
        }

        [TestMethod]
        public void Source_SealedShut_returns_before_the_behind_case_a_player_opens_from()
        {
            string doorCs = null;

            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && doorCs == null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "Source", "ACE.Server", "WorldObjects", "Door.cs");
                if (File.Exists(candidate))
                    doorCs = candidate;
            }

            Assert.IsNotNull(doorCs, "could not find Source/ACE.Server/WorldObjects/Door.cs by walking up from " + AppContext.BaseDirectory);

            var text = File.ReadAllText(doorCs);
            var actOnUse = text.IndexOf("public override void ActOnUse(WorldObject worldObject)", StringComparison.Ordinal);
            Assert.IsTrue(actOnUse >= 0, "Door.ActOnUse not found");

            var sealedCheck = text.IndexOf("if (SealedShut)", actOnUse, StringComparison.Ordinal);
            var behind = text.IndexOf("var behind", actOnUse, StringComparison.Ordinal);
            var open = text.IndexOf("Open(worldObject.Guid)", actOnUse, StringComparison.Ordinal);

            Assert.IsTrue(sealedCheck > actOnUse && behind > 0 && open > 0, "ActOnUse's SealedShut check, behind case and Open must all exist");
            Assert.IsTrue(sealedCheck < behind && sealedCheck < open, "the SealedShut check must come before the behind case and the Open");

            var sealedBlockEnd = text.IndexOf("return;", sealedCheck, StringComparison.Ordinal);
            Assert.IsTrue(sealedBlockEnd > sealedCheck && sealedBlockEnd < behind, "the SealedShut block must return before `behind` is computed");
        }
    }
}
