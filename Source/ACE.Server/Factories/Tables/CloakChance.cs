using System;
using System.Collections.Generic;

using ACE.Common;
using ACE.Database.Models.World;
using ACE.Entity.Enum;
using ACE.Server.Factories.Entity;

namespace ACE.Server.Factories.Tables
{
    public static class CloakChance
    {
        private static ChanceTable<int> T1_ItemMaxLevel = new ChanceTable<int>()
        {
            ( 1, 1.0f )
        };

        private static ChanceTable<int> T2_ItemMaxLevel = new ChanceTable<int>()
        {
            ( 1, 0.99f ),
            ( 2, 0.01f ),
        };

        private static ChanceTable<int> T3_T4_ItemMaxLevel = new ChanceTable<int>()
        {
            ( 1, 0.44f ),
            ( 2, 0.55f ),
            ( 3, 0.01f ),
        };

        private static ChanceTable<int> T5_ItemMaxLevel = new ChanceTable<int>()
        {
            ( 1, 0.04f ),
            ( 2, 0.40f ),
            ( 3, 0.55f ),
            ( 4, 0.01f ),
        };

        private static ChanceTable<int> T6_ItemMaxLevel = new ChanceTable<int>()
        {
            ( 1, 0.04f ),
            ( 2, 0.30f ),
            ( 3, 0.65f ),
            ( 4, 0.01f ),
        };

        private static ChanceTable<int> T7_T8_ItemMaxLevel = new ChanceTable<int>()
        {
            ( 2, 0.45f ),
            ( 3, 0.50f ),
            ( 4, 0.04f ),
            ( 5, 0.01f ),
        };

        private static List<ChanceTable<int>> cloakLevels = new List<ChanceTable<int>>()
        {
            T1_ItemMaxLevel,
            T2_ItemMaxLevel,
            T3_T4_ItemMaxLevel,
            T3_T4_ItemMaxLevel,
            T5_ItemMaxLevel,
            T6_ItemMaxLevel,
            T7_T8_ItemMaxLevel,
            T7_T8_ItemMaxLevel,
        };

        public static int Roll_ItemMaxLevel(TreasureDeath profile)
        {
            var table = cloakLevels[profile.Tier - 1];

            return table.Roll(profile.LootQualityMod);
        }


        private static readonly List<EquipmentSet> cloakSets = new List<EquipmentSet>()
        {
            EquipmentSet.CloakAlchemy,
            EquipmentSet.CloakArcaneLore,
            EquipmentSet.CloakArmorTinkering,
            EquipmentSet.CloakAssessPerson,
            EquipmentSet.CloakLightWeapons,
            EquipmentSet.CloakMissileWeapons,
            EquipmentSet.CloakCooking,
            EquipmentSet.CloakCreatureEnchantment,
            EquipmentSet.CloakFinesseWeapons,
            EquipmentSet.CloakDeception,
            EquipmentSet.CloakFletching,
            EquipmentSet.CloakHealing,
            EquipmentSet.CloakItemEnchantment,
            EquipmentSet.CloakItemTinkering,
            EquipmentSet.CloakLeadership,
            EquipmentSet.CloakLifeMagic,
            EquipmentSet.CloakLoyalty,
            EquipmentSet.CloakMagicDefense,
            EquipmentSet.CloakMagicItemTinkering,
            EquipmentSet.CloakManaConversion,
            EquipmentSet.CloakMeleeDefense,
            EquipmentSet.CloakMissileDefense,
            EquipmentSet.CloakSalvaging,
            EquipmentSet.CloakHeavyWeapons,
            EquipmentSet.CloakTwoHandedCombat,
            EquipmentSet.CloakVoidMagic,
            EquipmentSet.CloakWarMagic,
            EquipmentSet.CloakWeaponTinkering,
            EquipmentSet.CloakAssessCreature,
            EquipmentSet.CloakDirtyFighting,
            EquipmentSet.CloakDualWield,
            EquipmentSet.CloakRecklessness,
            EquipmentSet.CloakShield,
            EquipmentSet.CloakSneakAttack,
            EquipmentSet.CloakSummoning,
        };

        public static EquipmentSet RollEquipmentSet()
        {
            // verify: even chance per set
            var rng = ThreadSafeRandom.Next(0, cloakSets.Count - 1);

            return cloakSets[rng];
        }

        private static readonly List<SpellId> surgeSpells = new List<SpellId>()
        {
            SpellId.AcidRing,           // Searing Disc
            SpellId.BladeRing,          // Horizon's Blades
            SpellId.FlameRing,          // Cassius' Ring of Fire
            SpellId.ForceRing,          // Nuhmudira's Spines
            SpellId.FrostRing,          // Halo of Frost
            SpellId.LightningRing,      // Eye of the Storm
            SpellId.ShockwaveRing,      // Tectonic Rifts
            SpellId.NetherRing,         // Clouded Soul

            SpellId.CloakAllSkill,      // Cloaked in Skill

            SpellId.CloakMagicDLower,   // Shroud of Darkness (Magic)
            SpellId.CloakMeleeDLower,   // Shroud of Darkness (Melee)
            SpellId.CloakMissileDLower, // Shroud of Darkness (Missile)
        };

        public static SpellId RollProcSpell()
        {
            // verify: even chance for each spell, including Damage Reduction Proc?
            var rng = ThreadSafeRandom.Next(0, surgeSpells.Count);

            // handle Damage Reduction proc
            if (rng == surgeSpells.Count)
                return SpellId.Undef;

            return surgeSpells[rng];
        }

        /// <summary>
        /// The upgraded Knot cloak's proc pool (ACE.Server.KnotLoot.UpgradedCloak): the retail pool with each surge
        /// ring at level II, Cloaked in Skill and the three Shrouds unchanged. Eye of the Storm II is 6194, never the
        /// duplicate EyeOfTheStormIIAlt (6197). Together with damage reduction this is 13 equal outcomes, the same
        /// shape as <see cref="RollProcSpell"/>.
        /// </summary>
        public static readonly IReadOnlyList<SpellId> UpgradedSurgeSpells = new List<SpellId>()
        {
            SpellId.SearingDiscII,          // 6189
            SpellId.HorizonsBladesII,       // 6190
            SpellId.CassiusRingOfFireII,    // 6191
            SpellId.NuhmudirasSpinesII,     // 6192
            SpellId.HaloOfFrostII,          // 6193
            SpellId.EyeOfTheStormII,        // 6194
            SpellId.TectonicRiftsII,        // 6196
            SpellId.CloudedSoulII,          // 6195

            SpellId.CloakAllSkill,          // Cloaked in Skill

            SpellId.CloakMagicDLower,       // Shroud of Darkness (Magic)
            SpellId.CloakMeleeDLower,       // Shroud of Darkness (Melee)
            SpellId.CloakMissileDLower,     // Shroud of Darkness (Missile)
        };

        /// <summary>
        /// Pure: the upgraded proc for a draw <paramref name="r"/> in [0,1). Outcome i = floor(r x 13), clamped; the
        /// last outcome is damage reduction (SpellId.Undef). NaN or negative reads as 0.
        /// </summary>
        public static SpellId RollUpgradedProcSpell(double r)
        {
            var outcomes = UpgradedSurgeSpells.Count + 1;

            if (double.IsNaN(r) || r < 0)
                r = 0;

            var index = (int)Math.Min(Math.Floor(r * outcomes), outcomes - 1);

            // handle Damage Reduction proc
            if (index == UpgradedSurgeSpells.Count)
                return SpellId.Undef;

            return UpgradedSurgeSpells[index];
        }
    }
}
