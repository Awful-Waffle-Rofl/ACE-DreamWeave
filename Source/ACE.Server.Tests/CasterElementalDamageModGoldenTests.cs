using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.Tests.Pvp;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Golden values for WorldObject.GetCasterElementalDamageModifier, captured on the code BEFORE the Summoner
    /// casters extraction of SumCasterElementalDamageMod. The first three plus the Attunement variant pass on the
    /// unmodified function; SummonerFocus_NeverBoostsTheWielder is the one the new guard turns on.
    /// </summary>
    [TestClass]
    public class CasterElementalDamageModGoldenTests
    {
        private static uint nextGuid = 0x7E300000;

        [ClassInitialize]
        public static void Init(TestContext _)
        {
            TestGameTables.EnsureInitialized();
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);
        }

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", (bool)DefaultPropertyManager.DefaultBooleanProperties["weapon_mods_enabled"].Item);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", DefaultPropertyManager.DefaultDoubleProperties["weapon_mod_magnitude_scale"].Item);
        }

        private static Caster CreateCaster(DamageType damageType, double edm, bool focus = false, double attunementFraction = 0.0)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 2472,
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.Caster } },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.ElementalDamageMod, edm } },
                PropertiesBool = new Dictionary<PropertyBool, bool>(),
            };

            if (damageType != DamageType.Undef)
                weenie.PropertiesInt.Add(PropertyInt.DamageType, (int)damageType);

            if (focus)
                weenie.PropertiesBool[PropertyBool.SummonerFocus] = true;

            if (attunementFraction > 0.0)
                weenie.PropertiesFloat[PropertyFloat.WeaponModAttunement] = attunementFraction;

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        private static float Mod(DamageType casterType, double edm, bool vsPlayer, DamageType spell, double attunementFraction = 0.0)
        {
            var caster = CreateCaster(casterType, edm, attunementFraction: attunementFraction);
            var wielder = TestCreatures.CreateAttacker();
            Creature target = vsPlayer ? PvpTemplatePlayerTests.SeededPlayer() : TestCreatures.CreateAttacker();
            return WorldObject.GetCasterElementalDamageModifier(caster, wielder, target, spell);
        }

        private static float ModFocus(double edm, DamageType spell)
        {
            var caster = CreateCaster(DamageType.Undef, edm, focus: true);
            var wielder = TestCreatures.CreateAttacker();
            return WorldObject.GetCasterElementalDamageModifier(caster, wielder, TestCreatures.CreateAttacker(), spell);
        }

        /// <summary>An enchantment manager that reports a fixed elemental damage bonus, standing in for a spell like Spirit Drinker.</summary>
        private sealed class FixedEnchantments : EnchantmentManagerWithCaching
        {
            private readonly float bonus;
            public FixedEnchantments(WorldObject obj, float bonus) : base(obj) { this.bonus = bonus; }
            public override float GetElementalDamageMod() => bonus;
        }

        private static float ModWithEnchantments(float wielderBonus, float weaponBonus)
        {
            var caster = CreateCaster(DamageType.Fire, 1.12);
            var wielder = TestCreatures.CreateAttacker();
            caster.EnchantmentManager = new FixedEnchantments(caster, weaponBonus);
            wielder.EnchantmentManager = new FixedEnchantments(wielder, wielderBonus);
            return WorldObject.GetCasterElementalDamageModifier(caster, wielder, TestCreatures.CreateAttacker(), DamageType.Fire);
        }

        // golden values: 1.12 + 0.05 and 1.12 + 0.03, captured on the pre-extraction function (see the fix report)
        [TestMethod] public void WielderEnchantment_IsAddedIntoTheSum() => Assert.AreEqual(1.17f, ModWithEnchantments(0.05f, 0f), 1e-6f);
        [TestMethod] public void WeaponEnchantment_IsAddedIntoTheSum() => Assert.AreEqual(1.15f, ModWithEnchantments(0f, 0.03f), 1e-6f);

        [TestMethod] public void FireCaster_VsCreature_IsStoredMod() => Assert.AreEqual(1.12f, Mod(DamageType.Fire, 1.12, vsPlayer: false, spell: DamageType.Fire), 1e-6f);
        [TestMethod] public void FireCaster_VsPlayer_HalvesTheBonus() => Assert.AreEqual(1.06f, Mod(DamageType.Fire, 1.12, vsPlayer: true, spell: DamageType.Fire), 1e-6f);
        [TestMethod] public void WrongSpellType_IsOne() => Assert.AreEqual(1.0f, Mod(DamageType.Fire, 1.12, vsPlayer: false, spell: DamageType.Cold), 1e-6f);

        [TestMethod]
        public void Attunement_IsAddedIntoTheSum()
        {
            var probe = CreateCaster(DamageType.Fire, 1.12, attunementFraction: 1.0);
            var attunement = WeaponModCombat.ReadWeaponOnly(probe, WeaponModId.Attunement, true, 1.0);
            Assert.IsTrue(attunement > 0.0, "the seeded record must read as a positive magnitude");

            Assert.AreEqual((float)(1.12 + attunement), Mod(DamageType.Fire, 1.12, vsPlayer: false, spell: DamageType.Fire, attunementFraction: 1.0), 1e-6f);
        }

        // fails (compile error) until the SummonerFocus enum member exists, and fails by value until the guard exists
        [TestMethod] public void SummonerFocus_NeverBoostsTheWielder() => Assert.AreEqual(1.0f, ModFocus(1.14, spell: DamageType.Undef), 1e-6f);
    }
}
