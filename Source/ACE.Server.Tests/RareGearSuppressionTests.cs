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

        private static Weenie MakeWeenie(ItemType? type)
        {
            var w = new Weenie { PropertiesInt = new Dictionary<PropertyInt, int>() };
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
        public void IsSuppressedRareGear_CombinedArmorAndClothing_ReturnsTrue()
        {
            Assert.IsTrue(LootGenerationFactory.IsSuppressedRareGear(ItemType.Armor | ItemType.Clothing));
        }
    }
}
