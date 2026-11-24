using System.Collections.Generic;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Summoner caster loot overrides (Docs/SummonerCasters): a Caster carrying PropertyBool.SummonerFocus and
    /// DamageType.Undef must roll the elemental mutation script, wield on Summoning, take its bonus slot spell
    /// from the support/orb table and its aptitude cantrip from Summoning. Fixtures are database-free and
    /// dat-free (isMagical = false, no roll), like LootGenerationFactoryCasterWieldSkillTests, so CI covers them.
    /// </summary>
    [TestClass]
    public class SummonerCasterLootTests
    {
        private static uint nextGuid = 0x7D000000;
        private static uint nextWcid = 994000;

        private static MethodInfo _mutateCaster;
        private static MethodInfo _adjustForDamageType;

        private static readonly HashSet<SpellId> SupportSpells = new HashSet<SpellId>
        {
            SpellId.StrengthOther1, SpellId.EnduranceOther1, SpellId.CoordinationOther1, SpellId.QuicknessOther1,
            SpellId.FocusOther1, SpellId.WillpowerOther1, SpellId.FealtyOther1, SpellId.HealOther1,
            SpellId.RevitalizeOther1, SpellId.ManaBoostOther1, SpellId.RegenerationOther1,
            SpellId.RejuvenationOther1, SpellId.ManaRenewalOther1,
        };

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            _mutateCaster = typeof(LootGenerationFactory).GetMethod("MutateCaster", BindingFlags.NonPublic | BindingFlags.Static);
            _adjustForDamageType = typeof(LootGenerationFactory).GetMethod("AdjustForDamageType", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(_mutateCaster, "LootGenerationFactory.MutateCaster was not found by reflection.");
            Assert.IsNotNull(_adjustForDamageType, "LootGenerationFactory.AdjustForDamageType was not found by reflection.");
        }

        internal static WorldObject MakeCaster(DamageType damageType, bool focus)
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = nextWcid++,
                ClassName = "testcaster",
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                    { PropertyInt.ValidLocations, (int)EquipMask.Held },
                    { PropertyInt.DamageType, (int)damageType },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Test Caster" } },
            };

            if (focus)
                weenie.PropertiesBool = new Dictionary<PropertyBool, bool> { { PropertyBool.SummonerFocus, true } };

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        private static void Mutate(WorldObject wo, int tier) =>
            _mutateCaster.Invoke(null, new object[] { wo, new TreasureDeath { Tier = tier }, false, null });

        [DataTestMethod]
        [DataRow(7)]
        [DataRow(8)]
        public void Mutated_focus_rolls_the_elemental_script(int tier)
        {
            for (var i = 0; i < 50; i++)
            {
                var wo = MakeCaster(DamageType.Undef, true);
                Mutate(wo, tier);

                Assert.IsTrue(wo.ElementalDamageMod >= 1.10 - 1e-9, $"tier {tier} roll {i}: ElementalDamageMod {wo.ElementalDamageMod}");
                Assert.AreEqual(WieldRequirement.RawSkill, wo.WieldRequirements);
                Assert.AreEqual((int)Skill.Summoning, wo.WieldSkillType);
                Assert.IsTrue(wo.GetProperty(PropertyBool.SummonerFocus) == true, "the marker must survive mutation");
            }
        }

        [TestMethod]
        public void Plain_undef_caster_still_rolls_non_elemental()
        {
            var wo = MakeCaster(DamageType.Undef, false);
            Mutate(wo, 7);

            Assert.IsTrue(wo.ElementalDamageMod == null || wo.ElementalDamageMod == 1.0, $"ElementalDamageMod {wo.ElementalDamageMod}");
            Assert.AreNotEqual((int)Skill.Summoning, wo.WieldSkillType);
        }

        [TestMethod]
        public void Slot_spell_comes_from_the_support_table()
        {
            var focus = MakeCaster(DamageType.Undef, true);
            for (var i = 0; i < 500; i++)
            {
                var spell = CasterSlotSpells.Roll(focus);
                Assert.IsTrue(SupportSpells.Contains(spell), $"{spell} is not a support/orb spell");
            }
        }

        [TestMethod]
        public void Plain_undef_caster_slot_spell_is_not_forced_to_the_support_table()
        {
            var plain = MakeCaster(DamageType.Undef, false);
            var sawNonSupport = false;
            for (var i = 0; i < 500; i++)
                if (!SupportSpells.Contains(CasterSlotSpells.Roll(plain)))
                    sawNonSupport = true;

            Assert.IsTrue(sawNonSupport, "control: an unmarked Undef caster uses the wand/staff table");
        }

        [TestMethod]
        public void Aptitude_cantrip_is_summoning()
        {
            var focus = MakeCaster(DamageType.Undef, true);
            for (var i = 0; i < 20; i++)
                Assert.AreEqual(SpellId.CantripSummoningProwess1,
                    (SpellId)_adjustForDamageType.Invoke(null, new object[] { focus, SpellId.CANTRIPWARMAGICAPTITUDE1 }));
        }
    }
}
