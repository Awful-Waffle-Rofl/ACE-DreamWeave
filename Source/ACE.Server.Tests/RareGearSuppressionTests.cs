using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    [TestClass]
    public class RareGearSuppressionTests
    {
        [TestMethod]
        [DataRow(ItemType.MeleeWeapon)]
        [DataRow(ItemType.MissileWeapon)]
        [DataRow(ItemType.Caster)]
        [DataRow(ItemType.Armor)]
        [DataRow(ItemType.Jewelry)]
        public void IsSuppressedRareGear_GearTypes_ReturnsTrue(ItemType type)
        {
            Assert.IsTrue(LootGenerationFactory.IsSuppressedRareGear(type));
        }

        [TestMethod]
        [DataRow(ItemType.Gem)]
        [DataRow(ItemType.Misc)]
        [DataRow(ItemType.ManaStone)]
        [DataRow(ItemType.TinkeringMaterial)]
        [DataRow(ItemType.Clothing)]
        [DataRow(ItemType.Container)]
        public void IsSuppressedRareGear_NonGearTypes_ReturnsFalse(ItemType type)
        {
            Assert.IsFalse(LootGenerationFactory.IsSuppressedRareGear(type));
        }

        private static Weenie MakeWeenie(ItemType? type, uint wcid = 30352)
        {
            var w = new Weenie { WeenieClassId = wcid, PropertiesInt = new Dictionary<PropertyInt, int>() };
            if (type.HasValue)
                w.PropertiesInt[PropertyInt.ItemType] = (int)type.Value;
            return w;
        }

        [TestMethod]
        public void ShouldSuppressRare_NullWeenie_ReturnsFalse()
        {
            Assert.IsFalse(LootGenerationFactory.ShouldSuppressRare(true, null));
        }

        [TestMethod]
        public void ShouldSuppressRare_WeenieWithoutItemType_ReturnsFalse()
        {
            Assert.IsFalse(LootGenerationFactory.ShouldSuppressRare(true, MakeWeenie(null)));
        }

        [TestMethod]
        public void ShouldSuppressRare_KillSwitchOff_ArmorWeenie_ReturnsFalse()
        {
            Assert.IsFalse(LootGenerationFactory.ShouldSuppressRare(false, MakeWeenie(ItemType.Armor)));
        }

        [TestMethod]
        public void ShouldSuppressRare_KillSwitchOn_ArmorWeenie_ReturnsTrue()
        {
            Assert.IsTrue(LootGenerationFactory.ShouldSuppressRare(true, MakeWeenie(ItemType.Armor)));
        }

        [TestMethod]
        public void ShouldSuppressRare_KillSwitchOn_GemWeenie_ReturnsFalse()
        {
            Assert.IsFalse(LootGenerationFactory.ShouldSuppressRare(true, MakeWeenie(ItemType.Gem)));
        }

        [TestMethod]
        [DataRow(1008400u, ItemType.Armor)]
        [DataRow(1008421u, ItemType.Jewelry)]
        [DataRow(1000000u, ItemType.Armor)]
        [DataRow(1007452u, ItemType.Armor)]
        public void ShouldSuppressRare_KillSwitchOn_ForkWcidGear_ReturnsFalse(uint wcid, ItemType type)
        {
            Assert.IsFalse(LootGenerationFactory.ShouldSuppressRare(true, MakeWeenie(type, wcid)));
        }

        [TestMethod]
        public void ShouldSuppressRare_KillSwitchOn_RetailWcidJustBelowFloor_ReturnsTrue()
        {
            Assert.IsTrue(LootGenerationFactory.ShouldSuppressRare(true, MakeWeenie(ItemType.Armor, 999999)));
        }

        [TestMethod]
        public void ShouldSuppressRare_KillSwitchOn_RetailJewelry30352_ReturnsTrue()
        {
            Assert.IsTrue(LootGenerationFactory.ShouldSuppressRare(true, MakeWeenie(ItemType.Jewelry, 30352)));
        }

        [TestMethod]
        public void IsSuppressedRareGear_CombinedArmorAndClothing_ReturnsTrue()
        {
            Assert.IsTrue(LootGenerationFactory.IsSuppressedRareGear(ItemType.Armor | ItemType.Clothing));
        }
    }
}
