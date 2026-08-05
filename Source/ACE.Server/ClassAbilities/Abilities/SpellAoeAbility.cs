using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// When one of the player's Arc war spells successfully damages a monster, that struck target
    /// radiates a copy of the same spell at every other hostile creature within a short radius - so it
    /// reads as a blast bursting outward from the target. There is no cap on the number of neighbors
    /// hit; each secondary spell is an independent hit (its own resistance and crit rolls) scaled to a
    /// fraction of normal damage (server tunable, default 50%).
    ///
    /// This owns only the target-selection policy (radius, damage fraction, and the hostile-creature
    /// filter). The projectile mechanics live in SpellProjectile.SpawnClassAbilityAoeChild, and the core
    /// dispatch that invokes this (Arc war spells only, no cascading from AOE children) lives in
    /// SpellProjectile.OnCollideObject.
    /// </summary>
    public class SpellAoeAbility : ISpellHitAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.SpellAoe,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 1,
            Name = "spellaoe",
            DisplayName = "Spell AOE",
            Description = "When one of your Arc war spells damages a monster, the same spell blasts outward from " +
                          "it to every other nearby enemy (no limit on how many) for half damage.",
            MaxRank = 1,
            CostPerRank = new[] { 1 },   // Tier-1 GC (single rank): always 1 point (the class's power splash)
            Implemented = true,
        };

        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            // The dispatch fires for every landed war-spell hit; Spell AOE only radiates from a
            // first-generation Arc cast - never a Bolt/Blast/Volley, and never a class-ability-spawned
            // copy (that would cascade into an unbounded chain of blasts).
            if (projectile.IsClassAbilitySpawned || projectile.SpellType != ProjectileSpellType.Arc)
                return;

            var radius = (float)PropertyManager.GetDouble("class_ability_spellaoe_radius").Item;
            var damageMult = RadiatedDamageMult(caster);

            // The struck target may have just died from this hit; its WorldObject lingers (removal is
            // deferred) but guard against a torn-down Location/PhysicsObj before reading them below.
            if (radius <= 0.0f || caster.PhysicsObj == null || primaryTarget.Location == null || primaryTarget.PhysicsObj == null)
                return;

            // Same visible-objects source and monster filters used by GetMultiShotTargets / Taunt.
            // Collected into its own list first, since spawning children adds objects to the landblock.
            var visible = caster.PhysicsObj.ObjMaint.GetVisibleObjectsValuesWhere(o => o.WeenieObj.WorldObject != null);

            foreach (var obj in visible)
            {
                var creature = obj.WeenieObj.WorldObject as Creature;

                if (creature == null || creature == primaryTarget || creature == caster)
                    continue;

                if (creature.IsDead || creature.Teleporting)
                    continue;

                // PvP exclusion: the blast never targets players
                if (creature is Player)
                    continue;

                if (creature is CombatPet)
                    continue;

                if (!caster.CanDamage(creature) || caster.CheckPKStatusVsTarget(creature, null) != null)
                    continue;

                // radius measured from the struck target, not the caster
                if (primaryTarget.Location.DistanceTo(creature.Location) > radius)
                    continue;

                projectile.SpawnClassAbilityAoeChild(caster, primaryTarget, creature, damageMult);
            }
        }

        /// <summary>
        /// The fraction of normal damage each radiated blast lands: a base fraction that scales up with
        /// the caster's Mana Conversion toward a cap (SKILL-TABLES-PREVIEW: 50% base, up to 75%). The
        /// Mana Conversion rider uses the shared dual-ratio model (Trained-gated); returns the base when
        /// the caster is null.
        /// </summary>
        private static float RadiatedDamageMult(Player caster)
        {
            var baseMult = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult").Item;
            var cap = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_cap").Item;

            if (caster == null)
                return (float)baseMult;

            // +1% of damage fraction per divisor points of effective Mana Conversion (dual-ratio)
            var rider = caster.GetClassAbilityScaling(Skill.ManaConversion,
                PropertyManager.GetDouble("class_ability_spellaoe_manaconv_per_trained").Item,
                PropertyManager.GetDouble("class_ability_spellaoe_manaconv_per_spec").Item) * 0.01;

            // Resonance equipment mod (MACHINERY): another additive term on the radiated-damage fraction,
            // still bounded by the same cap as the Mana Conversion rider. Only reachable from this handler,
            // which runs solely for a caster who owns Spell AOE - there is no radiated blast to amplify
            // otherwise.
            var gearMod = caster.GetEquippedModValue(EquipmentModId.Resonance);

            return (float)Math.Clamp(baseMult + rider + gearMod, baseMult, cap);
        }

        /// <summary>
        /// Mirrors the terms in RadiatedDamageMult above - the two must stay in step. Single-rank ability,
        /// so "Skill" here is the BASE fraction (not a per-rank product); Affinity is the raw (unclamped)
        /// Mana Conversion rider and Gear is the raw Resonance mod, with Effective applying the same
        /// Math.Clamp(baseMult + rider + gearMod, baseMult, cap) RadiatedDamageMult uses. CapNote is set
        /// only when the clamp actually reduced this call's value.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseMult = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult").Item;
            var cap = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_cap").Item;

            var rider = player.GetClassAbilityScaling(Skill.ManaConversion,
                PropertyManager.GetDouble("class_ability_spellaoe_manaconv_per_trained").Item,
                PropertyManager.GetDouble("class_ability_spellaoe_manaconv_per_spec").Item) * 0.01;

            var gearMod = player.GetEquippedModValue(EquipmentModId.Resonance);

            var uncapped = baseMult + rider + gearMod;
            var clamped = Math.Clamp(uncapped, baseMult, cap);

            var skill = baseMult * 100.0;
            var affinity = rider * 100.0;
            var gear = gearMod * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = clamped * 100.0,
                Unit = "%",
                Label = "radiated",
                Per = null,
                CapNote = clamped < uncapped - 0.0000001 ? "aoe cap" : null,
            };
        }
    }
}
