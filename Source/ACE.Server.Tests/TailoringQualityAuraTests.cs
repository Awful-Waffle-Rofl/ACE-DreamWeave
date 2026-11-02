using System;
using System.Collections.Generic;
using System.IO;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Tailoring must not strip a weapon's earned quality aura (Exceptional/Elite/God) nor hand one to an
    /// unearned weapon. Exercises the internal helpers Tailoring.IntermediateScript and
    /// Tailoring.ResolveTailoredScript directly, since the real paths need a live Player; a source scan pins
    /// that SetCommonProperties and UpdateCommonProps actually call them. The three tier ids below are
    /// hardcoded on purpose, so a ladder change forces a conscious test edit.
    /// </summary>
    [TestClass]
    public class TailoringQualityAuraTests
    {
        private const uint Exceptional = 0x33001186;
        private const uint Elite = 0x330011BE;
        private const uint God = 0x3300101B;
        private const uint LookOwned = 0x33000001;

        private static uint nextGuid = 0x7F5A0000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // WeaponModTinkerSet.ReadSpecials reads weapon_mod_magnitude_scale.
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static WorldObject Weapon() => new MeleeWeapon(new Weenie
        {
            WeenieClassId = 1001912,
            WeenieType = WeenieType.MeleeWeapon,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Tailor Test Sword" } },
            PropertiesInt = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                { PropertyInt.WeaponSkill, (int)Skill.HeavyWeapons },
                { PropertyInt.ItemWorkmanship, 10 },
                { PropertyInt.EncumbranceVal, 100 },
                { PropertyInt.Value, 500 },
            },
        }, new ObjectGuid(nextGuid++));

        private static WorldObject WeaponWithPerfect(params WeaponModId[] ids)
        {
            var weapon = Weapon();

            foreach (var id in ids)
            {
                var definition = WeaponModRegistry.Get(id);
                WeaponModTinkerSet.ApplySpecial(weapon, definition, WeaponModValue.MaxMagnitude(definition));
            }

            return weapon;
        }

        private static WorldObject ExceptionalWeapon() =>
            WeaponWithPerfect(WeaponModId.Ambush, WeaponModId.Quickening, WeaponModId.SecondWind);

        [TestMethod]
        public void IsTierScript_TrueForAllThree_FalseForNullZeroAndOther()
        {
            Assert.IsTrue(WeaponQualityTiers.IsTierScript(Exceptional));
            Assert.IsTrue(WeaponQualityTiers.IsTierScript(Elite));
            Assert.IsTrue(WeaponQualityTiers.IsTierScript(God));
            Assert.IsFalse(WeaponQualityTiers.IsTierScript(null));
            Assert.IsFalse(WeaponQualityTiers.IsTierScript(0u));
            Assert.IsFalse(WeaponQualityTiers.IsTierScript(LookOwned));
        }

        [TestMethod]
        public void KitFromTieredWeapon_CarriesNoScript()
        {
            Assert.IsNull(Tailoring.IntermediateScript(Exceptional));
            Assert.IsNull(Tailoring.IntermediateScript(Elite));
            Assert.IsNull(Tailoring.IntermediateScript(God));
        }

        [TestMethod]
        public void KitFromNonTierScript_KeepsIt_AndNullStaysNull()
        {
            Assert.AreEqual((uint?)LookOwned, Tailoring.IntermediateScript(LookOwned));
            Assert.IsNull(Tailoring.IntermediateScript(null));
        }

        [TestMethod]
        public void PlainKit_OntoExceptionalWeapon_TargetKeepsItsTierScript()
        {
            var target = ExceptionalWeapon();
            Assert.AreEqual(WeaponQualityTier.Exceptional, WeaponQualityTiers.Evaluate(target), "precondition");
            target.VisualEffectScript = Exceptional;

            var kit = Weapon();

            Assert.AreEqual((uint?)Exceptional, Tailoring.ResolveTailoredScript(kit, target));
        }

        [TestMethod]
        public void PlainKit_OntoGodWeapon_ResolvesGodScript()
        {
            var target = WeaponWithPerfect(WeaponModId.Ambush, WeaponModId.Quickening, WeaponModId.SecondWind, WeaponModId.Focus);
            Assert.AreEqual(WeaponQualityTier.God, WeaponQualityTiers.Evaluate(target), "precondition");

            Assert.AreEqual((uint?)God, Tailoring.ResolveTailoredScript(Weapon(), target));
        }

        [TestMethod]
        public void LegacyKitWithTierScript_OntoSpecialFreeWeapon_TargetEndsWithNull()
        {
            var kit = Weapon();
            kit.VisualEffectScript = God;
            var target = Weapon();
            target.VisualEffectScript = Elite;

            Assert.IsNull(Tailoring.ResolveTailoredScript(kit, target));
        }

        [TestMethod]
        public void LegacyKitWithTierScript_OntoExceptionalWeapon_TargetGetsItsOwnNotTheKits()
        {
            var kit = Weapon();
            kit.VisualEffectScript = God;

            Assert.AreEqual((uint?)Exceptional, Tailoring.ResolveTailoredScript(kit, ExceptionalWeapon()));
        }

        [TestMethod]
        public void KitWithNonTierScript_OntoTieredWeapon_KitScriptWins()
        {
            var kit = Weapon();
            kit.VisualEffectScript = LookOwned;

            Assert.AreEqual((uint?)LookOwned, Tailoring.ResolveTailoredScript(kit, ExceptionalWeapon()));
        }

        [TestMethod]
        public void ArmorTarget_WithTierScript_PlainKitClearsIt_LookOwnedKitWins()
        {
            var armor = new Clothing(new Weenie
            {
                WeenieClassId = 1001913,
                WeenieType = WeenieType.Clothing,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Tailor Test Robe" } },
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Armor } },
            }, new ObjectGuid(nextGuid++));
            armor.VisualEffectScript = God;

            var lookOwnedKit = Weapon();
            lookOwnedKit.VisualEffectScript = LookOwned;

            Assert.IsNull(Tailoring.ResolveTailoredScript(Weapon(), armor));
            Assert.AreEqual((uint?)LookOwned, Tailoring.ResolveTailoredScript(lookOwnedKit, armor));
        }

        [TestMethod]
        public void TailoringSource_RoutesBothScriptAssignmentsThroughTheHelpers()
        {
            const string relative = "Source/ACE.Server/Entity/Tailoring.cs";
            var native = relative.Replace('/', Path.DirectorySeparatorChar);
            string path = null;

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }

            Assert.IsNotNull(path, $"Could not find {relative} by walking up from {AppContext.BaseDirectory}.");

            var text = File.ReadAllText(path);

            StringAssert.Contains(text, "target.VisualEffectScript = IntermediateScript(source.VisualEffectScript);");
            StringAssert.Contains(text, "target.VisualEffectScript = ResolveTailoredScript(source, target);");
            Assert.IsFalse(text.Contains("target.VisualEffectScript = source.VisualEffectScript"),
                "a raw script copy has returned to Tailoring.cs");
        }

        [TestMethod]
        public void KitWithZeroScript_IsTreatedAsNone()
        {
            var kit = Weapon();
            kit.VisualEffectScript = 0u;

            Assert.IsNull(Tailoring.ResolveTailoredScript(kit, Weapon()));
        }
    }
}
