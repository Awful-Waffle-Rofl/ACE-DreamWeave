using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Regression coverage for CasterSlotSpells.Roll's DamageType.Health branch (the Sanguine caster
    /// family's bonus slot spell).
    ///
    /// The bug: Roll() originally read
    ///     IsOrb(wo) ? orbSpells : W_DamageType == Nether ? netherSpells : wandStaffSpells
    /// with no case for Health, so a Sanguine wand/staff fell to wandStaffSpells (Flame Bolt, Force
    /// Bolt, Shock Wave, Acid Stream, Frost Bolt, Lightning Bolt, Whirling Blade) - a War Magic bonus
    /// cast on a life-magic item. Fixed by adding a lifeSpells table and a Health case ahead of the
    /// wandStaffSpells fallback.
    ///
    /// Fire and Nether are asserted alongside Health specifically to prove the fix left retail
    /// behaviour alone, same discipline as LootGenerationFactoryCasterWieldSkillTests for the sibling
    /// WieldSkillType fix.
    ///
    /// wcid is deliberately NOT W_ORB_CLASS for any fixture here, so every roll exercises the
    /// DamageType branch rather than IsOrb's orb branch (IsOrb is untouched by this fix - see
    /// CasterSlotSpells.Roll's own comment on why the Sanguine Orb still routes here correctly).
    ///
    /// Fixtures are database-free and dat-free: a bare Caster built from an in-memory Weenie.
    /// </summary>
    [TestClass]
    public class CasterSlotSpellsHealthTests
    {
        private static uint nextGuid = 0x7F000000;
        private static uint nextWcid = 993000;

        private static readonly HashSet<SpellId> AllowedLifeSpells = new HashSet<SpellId>
        {
            SpellId.HarmOther1,
            SpellId.HealOther1,
            SpellId.DrainHealth1,
            SpellId.DrainStamina1,
            SpellId.DrainMana1,
        };

        private static readonly HashSet<SpellId> WarBoltSpells = new HashSet<SpellId>
        {
            SpellId.WhirlingBlade1,
            SpellId.ForceBolt1,
            SpellId.ShockWave1,
            SpellId.AcidStream1,
            SpellId.FlameBolt1,
            SpellId.FrostBolt1,
            SpellId.LightningBolt1,
        };

        private static readonly HashSet<SpellId> NetherSpells = new HashSet<SpellId>
        {
            SpellId.Corruption1,
            SpellId.NetherArc1,
            SpellId.NetherBolt1,
            SpellId.Corrosion1,
            SpellId.CurseWeakness1,
            SpellId.CurseFestering1,
            SpellId.CurseDestructionOther1,
        };

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

        [TestMethod]
        public void Roll_HealthDamageType_OnlyRollsTheFiveAllowedLifeSpells()
        {
            var wo = MakeCaster(DamageType.Health);

            for (var i = 0; i < 200; i++)
            {
                var spell = CasterSlotSpells.Roll(wo);

                Assert.IsTrue(AllowedLifeSpells.Contains(spell),
                    $"a Health-typed caster rolled {spell}, which is not one of the five allowed life spells. " +
                    "If this is Martyr's Hecatomb, Curse of Raven Fury, or any other health-costing spell, " +
                    "see lifeSpells' comment for why those are deliberately excluded - the item has no way " +
                    "to charge the caster's own health for a bonus cast.");
            }
        }

        [TestMethod]
        public void Roll_FireDamageType_StillRollsWarBoltSpells()
        {
            var wo = MakeCaster(DamageType.Fire);

            for (var i = 0; i < 200; i++)
            {
                var spell = CasterSlotSpells.Roll(wo);

                Assert.IsTrue(WarBoltSpells.Contains(spell),
                    $"a Fire-typed caster rolled {spell}, outside the established war-bolt set - " +
                    "the Health fix must not have disturbed the wandStaffSpells fallback.");
            }
        }

        [TestMethod]
        public void Roll_NetherDamageType_StillRollsNetherSpells()
        {
            var wo = MakeCaster(DamageType.Nether);

            for (var i = 0; i < 200; i++)
            {
                var spell = CasterSlotSpells.Roll(wo);

                Assert.IsTrue(NetherSpells.Contains(spell),
                    $"a Nether-typed caster rolled {spell}, outside the established nether set - " +
                    "the Health fix must not have disturbed the existing Nether branch.");
            }
        }
    }
}
