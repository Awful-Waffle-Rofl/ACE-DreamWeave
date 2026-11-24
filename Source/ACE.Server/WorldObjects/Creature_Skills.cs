using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects.Entity;

namespace ACE.Server.WorldObjects
{
    partial class Creature
    {
        public readonly Dictionary<Skill, CreatureSkill> Skills = new Dictionary<Skill, CreatureSkill>();

        /// <summary>
        /// Non-persisted per-instance ceiling on this creature's melee/missile/magic defense skills - NOT a
        /// biota property and never read for a Player. Written only by
        /// DungeonCreatureNormalizer.StripCombatTraits, for a non-boss Thread run creature whose authored
        /// EFFECTIVE skill (attribute-formula contribution + InitLevel) exceeds the run's band-standard cap
        /// (dynamic_dungeons_defense_skill_cap_offset). Null, or a skill missing from the dictionary, means "no
        /// ceiling" - the case for every creature outside a Thread run. Read by CreatureSkill.Base/Current via
        /// <see cref="GetDefenseSkillCeiling"/>.
        /// </summary>
        public Dictionary<Skill, uint> DefenseSkillCeilings;

        /// <summary>This creature's defense-skill ceiling for <paramref name="skill"/>, or null when none is set.</summary>
        public uint? GetDefenseSkillCeiling(Skill skill)
            => DefenseSkillCeilings != null && DefenseSkillCeilings.TryGetValue(skill, out var v) ? v : (uint?)null;

        /// <summary>
        /// Non-persisted per-instance ceiling on this creature's pre-enchantment MAGIC ATTACK skills (War, Life, Void, Creature
        /// Enchantment), owner ruling 2026-10-30. Written only by ThreadDungeonSpawner.PrepareForPlacement for a Thread run
        /// creature, from dynamic_dungeons_magic_attack_skill_cap; never set for a Player or for a World Events or Pinnacle
        /// creature. Null means no ceiling. Kept apart from <see cref="DefenseSkillCeilings"/> so the uplift code that reads the
        /// defense ceilings is unaffected. Read by CreatureSkill.Base/Current via <see cref="GetMagicAttackSkillCeiling"/>.
        /// </summary>
        public uint? MagicAttackSkillCeiling;

        /// <summary>This creature's magic attack ceiling for <paramref name="skill"/>: the ceiling for the four magic attack schools, null for every other skill.</summary>
        public uint? GetMagicAttackSkillCeiling(Skill skill)
            => MagicAttackSkillCeiling.HasValue && ACE.Server.ThreadDungeons.DungeonCombatNormalizer.IsMagicAttackSkill(skill) ? MagicAttackSkillCeiling : (uint?)null;

        /// <summary>
        /// Non-persisted per-instance ceiling on this creature's pre-enchantment MISSILE DEFENSE skill. Written only by
        /// ThreadDungeonSpawner.PrepareForPlacement for a Thread run creature, from dynamic_dungeons_missile_defense_skill_cap;
        /// never set for a Player or for a World Events or Pinnacle creature. Null means no ceiling. Kept apart from
        /// <see cref="DefenseSkillCeilings"/> (the effective ceiling is the lower of the two) so the uplift code that reads the
        /// defense ceilings is unaffected. Read by CreatureSkill.Base/Current via <see cref="GetMissileDefenseSkillCeiling"/>.
        /// </summary>
        public uint? MissileDefenseSkillCeiling;

        /// <summary>This creature's missile defense ceiling for <paramref name="skill"/>: the ceiling for Missile Defense only, null for every other skill.</summary>
        public uint? GetMissileDefenseSkillCeiling(Skill skill)
            => MissileDefenseSkillCeiling.HasValue && skill == Skill.MissileDefense ? MissileDefenseSkillCeiling : (uint?)null;

        /// <summary>
        /// Non-persisted per-instance Threads band cap on this creature's MELEE DEFENSE (the band's effective median plus
        /// dynamic_dungeons_defense_skill_cap_offset), recorded for EVERY non-boss Thread run creature whether or not its own
        /// skill exceeds the cap. Written only by ThreadDungeonSpawner.ApplyMeleeDefenseBandCap; never set for a Player, a
        /// boss, or a World Events or Pinnacle creature. Null means no band cap. It does NOT clamp the skill (that is
        /// <see cref="DefenseSkillCeilings"/>' job, and the uplift code reads and counts those): it only bounds the wielded
        /// weapon's melee defense lift, through <see cref="GetMeleeDefenseWeaponBound"/>.
        /// </summary>
        public uint? MeleeDefenseBandCap;

        /// <summary>
        /// The value the wielded weapon's melee defense modifier may not lift this creature above (together with Current; see
        /// DungeonCombatNormalizer.BoundWeaponLiftedDefense): the melee defense ceiling when one is set, else the Threads band
        /// cap, else null (no bound - every creature outside a Thread run).
        /// </summary>
        public uint? GetMeleeDefenseWeaponBound()
            => GetDefenseSkillCeiling(Skill.MeleeDefense) ?? MeleeDefenseBandCap;

        /// <summary>
        /// This will get a CreatureSkill wrapper around the BiotaPropertiesSkill record for this player.
        /// If the skill doesn't exist for this Biota, one will be created with a status of Untrained.
        /// </summary>
        /// <param name="add">If the skill doesn't exist for this biota, adds it</param>
        public CreatureSkill GetCreatureSkill(Skill skill, bool add = true)
        {
            if (Skills.TryGetValue(skill, out var value))
                return value;

            PropertiesSkill propertiesSkill;
            if (add)
            {
                propertiesSkill = Biota.GetOrAddSkill(skill, BiotaDatabaseLock, out var skillAdded);

                if (skillAdded)
                {
                    propertiesSkill.SAC = SkillAdvancementClass.Untrained;
                    ChangesDetected = true;
                }

                Skills[skill] = new CreatureSkill(this, skill, propertiesSkill);
            }
            else
            {
                propertiesSkill = Biota.GetSkill(skill, BiotaDatabaseLock);

                if (propertiesSkill != null)
                    Skills[skill] = new CreatureSkill(this, skill, propertiesSkill);
                else
                    return null;
            }

            return Skills[skill];
        }

        public CreatureSkill GetCreatureSkill(MagicSchool skill)
        {
            switch (skill)
            {
                case MagicSchool.CreatureEnchantment:
                    return GetCreatureSkill(Skill.CreatureEnchantment);
                case MagicSchool.ItemEnchantment:
                    return GetCreatureSkill(Skill.ItemEnchantment);
                case MagicSchool.LifeMagic:
                    return GetCreatureSkill(Skill.LifeMagic);
                case MagicSchool.VoidMagic:
                    return GetCreatureSkill(Skill.VoidMagic);
                case MagicSchool.WarMagic:
                    return GetCreatureSkill(Skill.WarMagic);
            }
            return null;
        }


        /// <summary>
        /// This is an IPlayer wrapper that is used by the AllegianceManager to handle passup. You shouldn't be using these anywhere else. Reference GetCreatureSkill() directly.
        /// </summary>
        public uint GetCurrentLoyalty()
        {
            return GetCreatureSkill(Skill.Loyalty).Current;
        }

        /// <summary>
        /// This is an IPlayer wrapper that is used by the AllegianceManager to handle passup. You shouldn't be using these anywhere else. Reference GetCreatureSkill() directly.
        /// </summary>
        public uint GetCurrentLeadership()
        {
            return GetCreatureSkill(Skill.Leadership).Current;
        }
    }
}
