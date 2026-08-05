using System.Collections.Generic;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.World;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Regression coverage for the WieldSkillType branch in LootGenerationFactory_Caster.MutateCaster
    /// (private static, reached here via reflection - there is no public seam for just this switch).
    ///
    /// The bug: caster_elemental.txt's tier 4-8 mutation blocks all set WieldRequirements = RawSkill
    /// (Source/ACE.Server/Entity/Mutations/Casters/caster_elemental.txt), and MutateCaster's own
    /// post-mutation switch on that condition originally read:
    ///     if (wo.W_DamageType == DamageType.Nether) WieldSkillType = VoidMagic;
    ///     else                                       WieldSkillType = WarMagic;
    /// Retail never shipped a Health-typed caster, so the "everything non-Nether is WarMagic" else
    /// branch was a safe assumption until this fork added one (the Sanguine caster family). A
    /// Health-typed caster that rolled a tier 4-8 mutation would silently come out demanding WarMagic
    /// to wield instead of LifeMagic - exactly backwards for a life-magic item, and with no error or
    /// log to flag it.
    ///
    /// Fire and Nether are asserted alongside Health specifically to prove the fix left retail
    /// behaviour alone - a fix that widened the Nether case or changed the WarMagic default would
    /// pass a Health-only test while quietly breaking every existing elemental caster family.
    ///
    /// Fixtures are database-free and dat-free: a bare Caster built from an in-memory Weenie, with
    /// isMagical = false so MutateCaster never reaches AssignMagic/CasterSlotSpells (which need spell
    /// data this test does not set up) and TsysMutationData left unset so GetMaterialType takes its
    /// DB-free GetDefaultMaterialType fallback (LootGenerationFactory.cs:425-429) rather than querying
    /// DatabaseManager.World.
    /// </summary>
    [TestClass]
    public class LootGenerationFactoryCasterWieldSkillTests
    {
        private static uint nextGuid = 0x7E000000;
        private static uint nextWcid = 992000;

        private static MethodInfo _mutateCaster;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            _mutateCaster = typeof(LootGenerationFactory).GetMethod(
                "MutateCaster",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(_mutateCaster, "LootGenerationFactory.MutateCaster was not found by reflection - " +
                "signature or name changed, this test needs to move with it.");
        }

        private static WorldObject MakeCaster(DamageType damageType)
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

            return new Caster(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>
        /// Runs MutateCaster at a tier whose caster_elemental.txt mutation block sets
        /// WieldRequirements = RawSkill (tier 6 always does - see the script), then returns the
        /// resulting WieldSkillType. isMagical = false and roll = null keep the run inside the
        /// DB-free / spell-free surface described in the class remarks.
        /// </summary>
        private static int? RunMutateCasterAndGetWieldSkillType(DamageType damageType, int tier = 6)
        {
            var wo = MakeCaster(damageType);
            var profile = new TreasureDeath { Tier = tier };

            _mutateCaster.Invoke(null, new object[] { wo, profile, false, null });

            return wo.WieldSkillType;
        }

        [TestMethod]
        public void MutateCaster_HealthDamageType_WieldsOnLifeMagic()
        {
            var wieldSkillType = RunMutateCasterAndGetWieldSkillType(DamageType.Health);

            Assert.AreEqual((int)Skill.LifeMagic, wieldSkillType,
                "a Health-typed caster (the Sanguine caster family) that rolls a tier 4-8 mutation must " +
                "wield on Life Magic, not fall into the WarMagic default meant for retail's elemental casters.");
        }

        [TestMethod]
        public void MutateCaster_FireDamageType_StillWieldsOnWarMagic()
        {
            var wieldSkillType = RunMutateCasterAndGetWieldSkillType(DamageType.Fire);

            Assert.AreEqual((int)Skill.WarMagic, wieldSkillType,
                "retail elemental casters (fire and its siblings) must keep wielding on WarMagic - " +
                "the Health fix must not have widened or altered this branch.");
        }

        [TestMethod]
        public void MutateCaster_NetherDamageType_StillWieldsOnVoidMagic()
        {
            var wieldSkillType = RunMutateCasterAndGetWieldSkillType(DamageType.Nether);

            Assert.AreEqual((int)Skill.VoidMagic, wieldSkillType,
                "Nether casters must keep wielding on VoidMagic - the Health fix must not have " +
                "disturbed the existing Nether branch either.");
        }
    }
}
