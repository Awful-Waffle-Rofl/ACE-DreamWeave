using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T3 game-changer: a landed war spell has a 6/12/18% chance (by rank) to immediately
    /// re-cast itself at the same target - no mana, no wind-up. The echo chance scales with Magic Item
    /// Tinkering (a finely tuned casting implement holds the echo). The recast reuses the normal
    /// projectile machinery (WorldObject.CreateSpellProjectiles), so it is a faithful copy of the
    /// original spell (all its projectiles, full damage, its own resist/crit rolls) and still benefits
    /// from Overchannel / Void Damage; it is flagged IsClassAbilitySpawned so it never echoes again and
    /// never re-fires item procs.
    ///
    /// ISpellHitAbility: dispatched from SpellProjectile.OnCollideObject for every landed war-spell hit;
    /// this handler self-restricts to a first-generation projectile (no cascade).
    /// </summary>
    public class EchoCastAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.EchoCast,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "echocast",
            DisplayName = "Echo Cast",
            Description = "Your landed war spells have a 6/12/18% chance (by rank) to immediately re-cast " +
                          "themselves at the same target for free - no mana, no wind-up. Higher Magic Item " +
                          "Tinkering increases the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 5, 5, 5 },
            Implemented = true,
        };

        /// <summary>
        /// The echo chance at a given rank: base + step*(rank-1) + the Magic Item Tinkering rider + the
        /// equipment-mod term. Pure for testability (mirrors the other reactive-chance handlers). Returns 0
        /// for rank &lt;= 0.
        ///
        /// <paramref name="gearModChance"/> is the Echo Cast equipment mod, a MACHINERY mod: it amplifies
        /// the ability's own roll and stays behind the same rank check. Defaults to 0, which reproduces the
        /// pre-equipment-mod behavior exactly.
        /// </summary>
        public static float EchoChance(int rank, double chanceBase, double chanceStep, double tinkerFraction, double gearModChance = 0.0)
        {
            if (rank <= 0)
                return 0.0f;

            return (float)Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + Math.Max(0.0, tinkerFraction) + Math.Max(0.0, gearModChance));
        }

        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            // No cascade: an echo (or any class-ability-spawned copy) never echoes again.
            if (projectile.IsClassAbilitySpawned)
                return;

            // Don't echo at a corpse (the primary hit frequently kills the target) or a torn-down target.
            if (primaryTarget.IsDead || primaryTarget.PhysicsObj == null || caster.PhysicsObj == null)
                return;

            var tinker = caster.GetClassAbilityScaling(Skill.MagicItemTinkering,
                PropertyManager.GetDouble("class_ability_echocast_tinker_per_trained").Item,
                PropertyManager.GetDouble("class_ability_echocast_tinker_per_spec").Item) * 0.01;

            // Echo Cast equipment mod (MACHINERY): +pp on this ability's own recast roll. Read inside the
            // handler, which only runs for a caster who owns Echo Cast.
            var chance = EchoChance(rank,
                PropertyManager.GetDouble("class_ability_echocast_chance_base").Item,
                PropertyManager.GetDouble("class_ability_echocast_chance_step").Item,
                tinker,
                caster.GetEquippedModValue(EquipmentModId.EchoCast));

            if (ThreadSafeRandom.Next(0.0f, 1.0f) > chance)
                return;

            // Re-cast the same war spell at the same target: no mana, no wind-up. fromProc suppresses
            // item procs; flag the spawned copies so they can't echo or radiate further.
            var echoes = caster.CreateSpellProjectiles(projectile.Spell, primaryTarget, projectile.ProjectileLauncher,
                projectile.IsWeaponSpell, fromProc: true);

            foreach (var echo in echoes)
                echo.IsClassAbilitySpawned = true;
        }

        /// <summary>
        /// Mirrors the terms fed into EchoChance() above (x100 for display) - the two must stay in step.
        /// EchoChance takes no affinity-cap parameter, so this readout never clamps the Magic Item
        /// Tinkering rider and CapNote is always null.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_echocast_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_echocast_chance_step").Item;

            var skillChance = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            var affinityFraction = Math.Max(0.0, player.GetClassAbilityScaling(Skill.MagicItemTinkering,
                PropertyManager.GetDouble("class_ability_echocast_tinker_per_trained").Item,
                PropertyManager.GetDouble("class_ability_echocast_tinker_per_spec").Item) * 0.01);

            var gearChance = Math.Max(0.0, player.GetEquippedModValue(EquipmentModId.EchoCast));

            var skill = skillChance * 100.0;
            var affinity = affinityFraction * 100.0;
            var gear = gearChance * 100.0;
            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "pp",
                Label = "recast",
                Per = null,
                CapNote = null,
            };
        }
    }
}
