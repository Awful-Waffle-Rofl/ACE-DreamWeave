using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Paragon weapon mods are a SEPARATE LAYER from the Tourmaline specials: Tourmaline reroll, Amethyst swap,
    /// ClearSpecials, SpecialCount, the quality-tier intensity and the integrity gate must neither see nor touch
    /// the Paragon slots. Every tunable these paths read is seeded per test and restored afterwards.
    /// </summary>
    [TestClass]
    public class WeaponModParagonIsolationTests
    {
        private static readonly string[] DoubleKeys =
        {
            "weapon_mod_special_chance_1", "weapon_mod_special_chance_2", "weapon_mod_special_chance_3", "weapon_mod_special_chance_4",
            "weapon_mod_magnitude_scale",
        };

        private static readonly string[] BoolKeys = { "weapon_mods_enabled", "weapon_mod_guarantee_damage_special" };

        private readonly Dictionary<string, double> priorDoubles = new Dictionary<string, double>();
        private readonly Dictionary<string, bool> priorBools = new Dictionary<string, bool>();

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            foreach (var key in DoubleKeys)
            {
                priorDoubles[key] = PropertyManager.GetDouble(key).Item;
                Assert.IsTrue(PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item), key);
            }

            foreach (var key in BoolKeys)
            {
                priorBools[key] = PropertyManager.GetBool(key).Item;
                Assert.IsTrue(PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item), key);
            }

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0));
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var kvp in priorDoubles)
                PropertyManager.ModifyDouble(kvp.Key, kvp.Value);

            foreach (var kvp in priorBools)
                PropertyManager.ModifyBool(kvp.Key, kvp.Value);
        }

        private static readonly WeaponModId[] FullGems =
            { WeaponModId.Devastation, WeaponModId.Ambush, WeaponModId.Devastation, WeaponModId.ShieldBypass, WeaponModId.Heft };

        private static WorldObject GemmedWeapon(WeaponClass weaponClass = WeaponClass.Melee)
        {
            var weapon = WeaponModParagonMathTests.WeaponWithGems(weaponClass, FullGems);
            weapon.SetProperty(PropertyInt.ParagonModTokensSpent, 250);
            return weapon;
        }

        private static void AssertGemsIntact(WorldObject weapon, string after)
        {
            for (var i = 0; i < ParagonModRegistry.MaxGems; i++)
                Assert.AreEqual((int)FullGems[i], weapon.GetProperty(ParagonModRegistry.Slots[i]), $"{after}: slot {i + 1}");

            Assert.AreEqual(250, weapon.GetProperty(PropertyInt.ParagonModTokensSpent), $"{after}: tokens spent");
        }

        private static WeaponModDefinition Row(WeaponModId id)
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition));
            return definition;
        }

        // ================= the special-handling paths leave the slots alone =================

        [TestMethod]
        public void ClearSpecials_LeavesSlotsUntouched()
        {
            var weapon = GemmedWeapon();
            var devastation = Row(WeaponModId.Devastation);
            WeaponModTinkerSet.ApplySpecial(weapon, devastation, 3);

            WeaponModTinkerSet.ClearSpecials(weapon);

            Assert.IsNull(weapon.GetProperty(devastation.Record), "control: the Tourmaline record was cleared");
            AssertGemsIntact(weapon, "ClearSpecials");
        }

        [TestMethod]
        public void TourmalineReroll_LeavesSlotsUntouched()
        {
            for (var i = 0; i < 20; i++)
            {
                var weapon = GemmedWeapon();
                WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Bloodthirst), 2);

                Assert.IsNotNull(WeaponModManager.ApplyReroll(weapon, WeaponClass.Melee, 10.0), "control: the reroll ran");
                AssertGemsIntact(weapon, "ApplyReroll");
            }
        }

        [TestMethod]
        public void AmethystSwap_LeavesSlotsUntouched()
        {
            for (var i = 0; i < 20; i++)
            {
                var weapon = GemmedWeapon();
                WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Bloodthirst), 2);

                Assert.IsNotNull(WeaponModManager.ApplySwap(weapon, WeaponClass.Melee, 10.0), "control: the swap ran");
                AssertGemsIntact(weapon, "ApplySwap");
            }
        }

        // ================= the special-reading paths do not see the slots =================

        [TestMethod]
        public void SpecialCount_AndReadSpecials_IgnoreSlots()
        {
            var weapon = GemmedWeapon();

            Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon), "gems alone are not specials");
            Assert.AreEqual(0, WeaponModTinkerSet.ReadSpecials(weapon).Count);

            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 3);
            Assert.AreEqual(1, WeaponModTinkerSet.SpecialCount(weapon), "one Tourmaline special, five gems");
        }

        [TestMethod]
        public void QualityTierIntensity_IgnoresSlots()
        {
            var gemmed = GemmedWeapon();
            var plain = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

            Assert.AreEqual(0, WeaponQualityTiers.TotalIntensity(gemmed), "gems alone carry no intensity");

            WeaponModTinkerSet.ApplySpecialAtFraction(gemmed, Row(WeaponModId.Ambush), 0.5, 1.0);
            WeaponModTinkerSet.ApplySpecialAtFraction(plain, Row(WeaponModId.Ambush), 0.5, 1.0);

            Assert.AreEqual(WeaponQualityTiers.TotalIntensity(plain), WeaponQualityTiers.TotalIntensity(gemmed),
                "the Ambush gem must not raise the Ambush special's intensity");
            Assert.AreEqual(WeaponQualityTiers.Evaluate(plain), WeaponQualityTiers.Evaluate(gemmed));
        }

        [TestMethod]
        public void IntegrityGate_IgnoresSlots()
        {
            var plain = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);
            var gemmed = WeaponModTestKit.MakeHandTinkered(WeaponClass.Melee);

            for (var i = 0; i < FullGems.Length; i++)
                gemmed.SetProperty(ParagonModRegistry.Slots[i], (int)FullGems[i]);

            Assert.AreEqual(WeaponModTinkerSet.PassesIntegrityGate(plain), WeaponModTinkerSet.PassesIntegrityGate(gemmed));
        }

        [TestMethod]
        public void SlotProperties_AreNotWeaponModRecords()
        {
            foreach (var slot in ParagonModRegistry.Slots)
                Assert.IsNull(WeaponModSuppression.TierARowFor(slot), $"{slot} must not be any Tier A row's native");
        }
    }
}
