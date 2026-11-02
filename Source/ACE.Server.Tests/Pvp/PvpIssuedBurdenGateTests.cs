using System.Collections.Generic;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// A template REPLACES Strength, so a low-Strength template can leave a player over the pack's burden gate
    /// (HasEnoughBurdenToAddToInventory: load + item &lt;= 3 x capacity). That gate compares the WHOLE load, so it refuses even a
    /// 0-burden item. Issued kit items weigh 0, so the issue bypasses the pack gate (burdenCheck: false); the equip path
    /// (Creature_Equipment.TryEquipObject) has no burden gate to bypass.
    /// </summary>
    [TestClass]
    public class PvpIssuedBurdenGateTests
    {
        private static Player OverCapPlayer()
        {
            var player = PvpTemplatePlayerTests.SeededPlayer();

            var attributes = new Dictionary<PropertyAttribute, CreatureAttribute>
            {
                [PropertyAttribute.Strength] = new CreatureAttribute(player, PropertyAttribute.Strength)
            };

            PvpTemplatePlayerTests.SetField(typeof(Creature), player, "Attributes", attributes);

            player.Biota.PropertiesInt[PropertyInt.ItemsCapacity] = 102;

            var ceiling = player.GetEncumbranceCapacity() * 3;
            Assert.IsTrue(ceiling > 0, "fixture: the seeded Strength gives a capacity");

            player.EncumbranceVal = ceiling + 1000;

            return player;
        }

        [TestMethod]
        public void OverCapPlayer_IsRefusedAZeroBurdenItem_ByTheDefaultPackGate()
        {
            var player = OverCapPlayer();
            var item = PvpTemplatePlayerTests.Item(issued: true);
            item.EncumbranceVal = 0;

            Assert.IsFalse(player.TryAddToInventory(item, out _), "control: the gate compares the whole load, so even a 0-burden item is refused");
            Assert.AreEqual(0, player.Inventory.Count);
        }

        [TestMethod]
        public void OverCapPlayer_TakesAZeroBurdenIssuedItem_WithTheBurdenCheckOff_AndStaysAtTheSameLoad()
        {
            var player = OverCapPlayer();
            var before = player.EncumbranceVal;
            var item = PvpTemplatePlayerTests.Item(issued: true);
            item.EncumbranceVal = 0;
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), item, "positionCache", new Dictionary<PositionType, ACE.Entity.Position>());
            PvpTemplatePlayerTests.SetField(typeof(WorldObject), item, "ephemeralPositions", new Dictionary<PositionType, ACE.Entity.Position>());

            Assert.IsTrue(player.TryAddToInventory(item, out _, burdenCheck: false));
            Assert.AreEqual(1, player.Inventory.Count);
            Assert.AreEqual(before, player.EncumbranceVal, "a weightless item adds nothing to the load");
        }

        [TestMethod]
        public void TheIssuePath_PassesBurdenCheckFalse_ThroughTheNetworkedCreate()
        {
            var text = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_PvpTemplate.cs");
            var start = text.IndexOf("private bool TryIssuePvpTemplateKitItem(", System.StringComparison.Ordinal);
            Assert.IsTrue(start > 0);
            var next = Regex.Match(text.Substring(start + 10), @"(?m)^        (public|private|internal|protected)\b");
            var body = next.Success ? text.Substring(start, next.Index + 10) : text.Substring(start);

            Assert.IsTrue(Regex.IsMatch(body, @"(?m)^\s*if \(TryCreateInInventoryWithNetworking\(item, out _, burdenCheck: false\)\)"),
                "the pack issue must skip the burden gate");

            var inv = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Inventory.cs");
            StringAssert.Contains(inv, "TryAddToInventory(item, out container, burdenCheck: burdenCheck)");
        }
    }
}
