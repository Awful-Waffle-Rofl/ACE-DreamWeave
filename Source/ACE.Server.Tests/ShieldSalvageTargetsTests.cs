using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Use-on-target gate for the four weapon-mod materials on shields. Tourmaline, Amethyst, Black Opal and
    /// Fire Opal bags (and the Tourmaline / Amethyst hammers, which are the same ItemType and MaterialType) carry
    /// TargetType 33025 (melee, missile, caster); shields are ItemType Armor, so the gate refused them before
    /// any crafting code ran.
    /// </summary>
    [TestClass]
    public class ShieldSalvageTargetsTests
    {
        private static uint nextWcid = 9_400_000;
        private static uint nextGuid = 0x7E000000;

        private static readonly MaterialType[] Four =
        {
            MaterialType.Tourmaline, MaterialType.Amethyst, MaterialType.BlackOpal, MaterialType.FireOpal,
        };

        private static WorldObject Make(ItemType itemType, int? targetType, MaterialType? material, CombatUse? combatUse = null, uint? wcid = null)
        {
            var ints = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)itemType } };

            if (targetType.HasValue)
                ints[PropertyInt.TargetType] = targetType.Value;
            if (material.HasValue)
                ints[PropertyInt.MaterialType] = (int)material.Value;
            if (combatUse.HasValue)
                ints[PropertyInt.CombatUse] = (int)combatUse.Value;

            var weenie = new Weenie
            {
                WeenieClassId = wcid ?? nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = ints,
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Bag(MaterialType m, int? target) => Make(ItemType.TinkeringMaterial, target, m);
        private static WorldObject Gem(MaterialType m, int target) => Make(ItemType.Gem, target, m);
        private static WorldObject Shield() => Make(ItemType.Armor, null, null, CombatUse.Shield);
        private static WorldObject Chest() => Make(ItemType.Armor, null, null);
        private static WorldObject Sword() => Make(ItemType.MeleeWeapon, null, null);

        [TestMethod]
        public void EffectiveTargetType_WidensOnlyTheFour()
        {
            foreach (var m in Four)
                Assert.AreEqual((ItemType)33027, ShieldSalvageTargets.EffectiveTargetType(Bag(m, 33025)));

            Assert.AreEqual((ItemType)257, ShieldSalvageTargets.EffectiveTargetType(Bag(MaterialType.Sunstone, 257)));
            Assert.AreEqual((ItemType)33025, ShieldSalvageTargets.EffectiveTargetType(Gem(MaterialType.BlackOpal, 33025)));
        }

        [TestMethod]
        public void EffectiveTargetType_NullStaysNullWhenNotWidened()
        {
            Assert.IsNull(ShieldSalvageTargets.EffectiveTargetType(Make(ItemType.Misc, null, null)));
        }

        [TestMethod]
        public void IsCompatible_ShieldAdmittedForTheFour()
        {
            var shield = Shield();

            foreach (var m in Four)
                Assert.IsTrue(ShieldSalvageTargets.IsCompatible(Bag(m, 33025), shield), m.ToString());

            Assert.IsFalse(ShieldSalvageTargets.IsCompatible(Bag(MaterialType.Sunstone, 33025), shield));
            Assert.IsFalse(ShieldSalvageTargets.IsCompatible(Bag(MaterialType.Iron, 257), shield));
            Assert.IsFalse(ShieldSalvageTargets.IsCompatible(Gem(MaterialType.BlackOpal, 33025), shield));
        }

        [TestMethod]
        public void IsCompatible_NonShieldArmor_IsRefused()
        {
            var chest = Chest();

            foreach (var m in Four)
                Assert.IsFalse(ShieldSalvageTargets.IsCompatible(Bag(m, 33025), chest), m.ToString());
        }

        [TestMethod]
        public void IsCompatible_WeaponsUnchanged()
        {
            var sword = Sword();

            foreach (var m in Four)
                Assert.IsTrue(ShieldSalvageTargets.IsCompatible(Bag(m, 33025), sword), m.ToString());

            Assert.IsTrue(ShieldSalvageTargets.IsCompatible(Bag(MaterialType.Iron, 257), sword));
        }

        [TestMethod]
        public void FoolproofMiscBags_Widen_ButOnlyForShields()
        {
            var shield = Shield();
            var chest = Chest();

            foreach (var (wcid, m) in new[] { (36621u, MaterialType.BlackOpal), (36623u, MaterialType.FireOpal) })
            {
                var bag = Make(ItemType.Misc, 33025, m, null, wcid);
                Assert.IsTrue(ShieldSalvageTargets.WidensToShields(bag), wcid.ToString());
                Assert.AreEqual((ItemType)33027, ShieldSalvageTargets.EffectiveTargetType(bag));
                Assert.IsTrue(ShieldSalvageTargets.IsCompatible(bag, shield), wcid.ToString());
                Assert.IsFalse(ShieldSalvageTargets.IsCompatible(bag, chest), wcid.ToString());
            }
        }

        [TestMethod]
        public void MiscWithMaterialButNotFoolproof_IsNotWidened()
        {
            var misc = Make(ItemType.Misc, 33025, MaterialType.BlackOpal);
            Assert.IsFalse(ShieldSalvageTargets.WidensToShields(misc));
            Assert.AreEqual((ItemType)33025, ShieldSalvageTargets.EffectiveTargetType(misc));
            Assert.IsFalse(ShieldSalvageTargets.IsCompatible(misc, Shield()));
        }

        [TestMethod]
        public void WidenedBagWithNullTargetType_YieldsArmor_AdmitsOnlyShields()
        {
            foreach (var m in Four)
            {
                var bag = Bag(m, null);
                Assert.AreEqual(ItemType.Armor, ShieldSalvageTargets.EffectiveTargetType(bag));
                Assert.IsTrue(ShieldSalvageTargets.IsCompatible(bag, Shield()));
                Assert.IsFalse(ShieldSalvageTargets.IsCompatible(bag, Chest()));
                Assert.IsFalse(ShieldSalvageTargets.IsCompatible(bag, Sword()));
            }
        }

        [TestMethod]
        public void BothSites_UseTheHelper_SourceScan()
        {
            var use = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Use.cs");
            StringAssert.Contains(use, "ShieldSalvageTargets.IsCompatible(sourceItem, target)");
            Assert.IsFalse(use.Contains("((sourceItem.TargetType ?? ItemType.None) & target.ItemType)"));

            var net = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/WorldObject_Networking.cs");
            var count = System.Text.RegularExpressions.Regex.Matches(net, System.Text.RegularExpressions.Regex.Escape("ShieldSalvageTargets.EffectiveTargetType(this)")).Count;
            Assert.AreEqual(2, count);
        }
    }
}
