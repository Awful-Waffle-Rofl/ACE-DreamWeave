using System;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers.CharacterSheets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure helpers behind CharacterSheetProjector.Project and the wire names of the sheet DTOs.
    /// Project itself needs a live Player, which this harness cannot build.
    /// </summary>
    [TestClass]
    public class CharacterSheetProjectorTests
    {
        [TestMethod]
        public void AdvancementName_OnlyTrainedAndSpecializedAreNamed()
        {
            Assert.AreEqual("specialized", CharacterSheetProjector.AdvancementName(SkillAdvancementClass.Specialized));
            Assert.AreEqual("trained", CharacterSheetProjector.AdvancementName(SkillAdvancementClass.Trained));
            Assert.IsNull(CharacterSheetProjector.AdvancementName(SkillAdvancementClass.Untrained));
            Assert.IsNull(CharacterSheetProjector.AdvancementName(SkillAdvancementClass.Inactive));
        }

        [TestMethod]
        public void OrderSkills_SpecializedFirstThenTrained_EachAlphabetical()
        {
            var input = new[]
            {
                new SheetSkill { Name = "Axe", Advancement = "trained" },
                new SheetSkill { Name = "War Magic", Advancement = "specialized" },
                new SheetSkill { Name = "Alchemy", Advancement = "specialized" },
            };
            CollectionAssert.AreEqual(new[] { "Alchemy", "War Magic", "Axe" },
                CharacterSheetProjector.OrderSkills(input).Select(s => s.Name).ToArray());
        }

        [TestMethod]
        public void SlotName_UsesTheEnumNameAndUnknownForNone()
        {
            Assert.AreEqual("MeleeWeapon", CharacterSheetProjector.SlotName(EquipMask.MeleeWeapon));
            Assert.AreEqual("Unknown", CharacterSheetProjector.SlotName(null));
            Assert.AreEqual("Unknown", CharacterSheetProjector.SlotName((EquipMask)0));
        }

        [TestMethod]
        public void SlotName_MultiSlotItem_IsOnePrimaryFlagName()
        {
            // chest plus abdomen armor: EquipMask.ToString would read "ChestArmor, AbdomenArmor"
            Assert.AreEqual("ChestArmor", CharacterSheetProjector.SlotName(EquipMask.ChestArmor | EquipMask.AbdomenArmor));
            Assert.AreEqual("UpperLegArmor", CharacterSheetProjector.SlotName(EquipMask.UpperLegArmor | EquipMask.LowerLegArmor));
            Assert.AreEqual("Unknown", CharacterSheetProjector.SlotName((EquipMask)0x80000000), "a set bit with no single-flag member name");
        }

        [TestMethod]
        public void OrderItems_BySlotThenName()
        {
            var input = new[]
            {
                new SheetItem { Name = "Ring B", Slot = "RightRing" },
                new SheetItem { Name = "Sword", Slot = "MeleeWeapon" },
                new SheetItem { Name = "Amulet", Slot = "RightRing" },
                new SheetItem { Name = "Cap", Slot = "HeadWear" },
            };
            CollectionAssert.AreEqual(new[] { "Cap", "Sword", "Amulet", "Ring B" },
                CharacterSheetProjector.OrderItems(input).Select(i => i.Name).ToArray());
        }

        [TestMethod]
        public void AttributeOrder_IsTheSixAttributesInSheetOrder()
        {
            CollectionAssert.AreEqual(
                new[] { PropertyAttribute.Strength, PropertyAttribute.Endurance, PropertyAttribute.Coordination, PropertyAttribute.Quickness, PropertyAttribute.Focus, PropertyAttribute.Self },
                CharacterSheetProjector.AttributeOrder.Select(a => a.key).ToArray());

            CollectionAssert.AreEqual(
                new[] { "Strength", "Endurance", "Coordination", "Quickness", "Focus", "Self" },
                CharacterSheetProjector.AttributeOrder.Select(a => a.name).ToArray());
        }

        [TestMethod]
        public void VitalOrder_IsHealthStaminaManaInSheetOrder()
        {
            CollectionAssert.AreEqual(
                new[] { PropertyAttribute2nd.MaxHealth, PropertyAttribute2nd.MaxStamina, PropertyAttribute2nd.MaxMana },
                CharacterSheetProjector.VitalOrder.Select(v => v.key).ToArray());

            CollectionAssert.AreEqual(
                new[] { "Health", "Stamina", "Mana" },
                CharacterSheetProjector.VitalOrder.Select(v => v.name).ToArray());
        }

        [TestMethod]
        public void Dtos_SerializeToTheWireNames()
        {
            var json = JsonSerializer.Serialize(new CharacterSheet
            {
                Name = "Weftwalker", Level = 275, GeneratedAt = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc),
                Attributes = { new SheetStat { Name = "Strength", Base = 100, Current = 110 } },
                Vitals = { new SheetStat { Name = "Health", Base = 500, Current = 550 } },
                Equipped = { new SheetItem { ItemGuid = 1, Name = "Sword", Slot = "MeleeWeapon", Snapshot = new ACE.Server.Managers.Market.ListingSnapshot() } },
                Skills = { new SheetSkill { Name = "Axe", Advancement = "trained", Base = 10, Current = 12 } },
                ClassAbilities = { new SheetAbility { Name = "KineticCharge", DisplayName = "Kinetic Charge", Class = "Vanguard", Tier = 1, Rank = 2, MaxRank = 3 } },
                Ranks = { new SheetRank { Board = "level", Title = "Level", Rank = 1, ScoreText = "Level 275" } },
            }, ACE.Server.Managers.Market.MarketApiHost.Json);

            foreach (var name in new[] { "\"name\"", "\"level\"", "\"generated_at\"", "\"attributes\"", "\"vitals\"", "\"equipped\"", "\"item_guid\"", "\"slot\"", "\"snapshot\"", "\"skills\"",
                                         "\"advancement\"", "\"base\"", "\"current\"", "\"class_abilities\"", "\"display_name\"", "\"class\"",
                                         "\"tier\"", "\"rank\"", "\"max_rank\"", "\"ranks\"", "\"board\"", "\"title\"", "\"score_text\"" })
                StringAssert.Contains(json, name);

            var linkJson = JsonSerializer.Serialize(
                new SheetLink { Enabled = true, Slug = "Ab3dE5gH9k", Url = "https://char.example.com/Ab3dE5gH9k" },
                ACE.Server.Managers.Market.MarketApiHost.Json);

            foreach (var name in new[] { "\"enabled\"", "\"slug\"", "\"url\"" })
                StringAssert.Contains(linkJson, name);

            var requestJson = JsonSerializer.Serialize(
                new ACE.Server.Managers.Market.MarketApiHost.SheetLinkRequest { Rotate = true },
                ACE.Server.Managers.Market.MarketApiHost.Json);

            StringAssert.Contains(requestJson, "\"rotate\"");

            // a disabled link omits slug and url entirely (WhenWritingNull), as the yaml SheetLink schema says
            var disabledJson = JsonSerializer.Serialize(new SheetLink { Enabled = false }, ACE.Server.Managers.Market.MarketApiHost.Json);

            StringAssert.Contains(disabledJson, "\"enabled\"");
            Assert.IsFalse(disabledJson.Contains("\"slug\""), disabledJson);
            Assert.IsFalse(disabledJson.Contains("\"url\""), disabledJson);
        }
    }
}
