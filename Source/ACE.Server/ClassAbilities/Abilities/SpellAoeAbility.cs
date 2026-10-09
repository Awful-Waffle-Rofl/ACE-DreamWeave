using System;

using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// When one of the player's Arc spells successfully damages a monster, that struck target
    /// radiates a copy of the same spell at every other hostile creature within a short radius - so it
    /// reads as a blast bursting outward from the target. There is no cap on the number of neighbors
    /// hit; each secondary spell is an independent hit (its own resistance and crit rolls) scaled to a
    /// fraction of normal damage that grows with RANK (server tunables, default 15 / 30 / 50%).
    ///
    /// NO LONGER WAR MAGIC ONLY, as of 2026-09-13: the school test came off the shared spell-hit dispatch
    /// in SpellProjectile.OnCollideObject, so an Arc cast of ANY school radiates. The ARC restriction is
    /// this handler's own and is unchanged - it is a whole-pack radiate, not a per-bolt rider.
    ///
    /// The mana surcharge on a qualifying Arc cast (class_ability_spellaoe_mana_surcharge, +100%) is
    /// deliberately FLAT across ranks - the ladder buys blast damage, not a cheaper cast - and is applied
    /// in Player.GetClassAbilityManaSurcharge, not here. It widened with the damage: an Arc cast of any
    /// school now pays it.
    ///
    /// This owns only the target-selection policy (radius, damage fraction, and the hostile-creature
    /// filter). The projectile mechanics live in SpellProjectile.SpawnClassAbilityAoeChild, and the core
    /// dispatch that invokes this (every landed damaging spell projectile, no cascading from AOE children)
    /// lives in SpellProjectile.OnCollideObject.
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
            Description = "When one of your Arc spells of any school damages a monster, the same spell blasts outward from " +
                          "it to every other nearby enemy (no limit on how many) for 15% / 30% / 50% of the " +
                          "spell's damage by rank. Qualifying casts cost additional mana. " +
                          "Higher Mana Conversion increases each blast's damage.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // flat 1/rank (class ability overhaul repricing, 2026-09-12)
            Implemented = true,
            AffinitySkill = Skill.ManaConversion,
        };

        /// <summary>
        /// TRUE when a landed projectile may radiate: an Arc, and not a class-ability-spawned secondary copy
        /// (a Spell AOE child - radiating off one would cascade into an unbounded chain of blasts). An Echo
        /// Cast recast is NOT such a copy (owner ruling 2026-09-21: an echo is a full second cast, AOE
        /// included), so it radiates; the AOE children it spawns carry IsClassAbilitySpawned and stop there.
        /// Pure so the recursion guard is unit-testable without a live projectile.
        /// </summary>
        public static bool CanRadiate(bool isClassAbilitySpawned, ProjectileSpellType spellType)
        {
            return !isClassAbilitySpawned && spellType == ProjectileSpellType.Arc;
        }

        public void OnSpellHit(Player caster, int rank, Creature primaryTarget, SpellProjectile projectile)
        {
            // The dispatch fires for every landed damaging-spell hit of any school; Spell AOE only
            // radiates from an Arc cast (a normal cast or an Echo Cast recast) - never a Bolt/Blast/Volley,
            // and never a Spell AOE child. See CanRadiate.
            if (!CanRadiate(projectile.IsClassAbilitySpawned, projectile.SpellType))
                return;

            var radius = (float)PropertyManager.GetDouble("class_ability_spellaoe_radius").Item;
            var damageMult = RadiatedDamageMult(caster, rank);

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

                if (Player.IsObjectiveProbeRefused(caster, creature))
                    continue;

                if (!caster.CanDamage(creature) || caster.CheckPKStatusVsTarget(creature, null) != null)
                    continue;

                // Radius measured from the struck target, not the caster. DistanceTo is the 3D
                // (dx,dy,dz) distance, so this is a SPHERE, not a ground-plane circle - a neighbor a
                // floor above or below is correctly out of reach. Getting the blast to actually land on
                // sloped ground is the LAUNCH VECTOR's job, not this check's: see
                // SpellProjectile.GetAoeChildAimOffset.
                if (primaryTarget.Location.DistanceTo(creature.Location) > radius)
                    continue;

                projectile.SpawnClassAbilityAoeChild(caster, primaryTarget, creature, damageMult);
            }
        }

        /// <summary>
        /// The RANK's base fraction of normal spell damage, before the Mana Conversion rider and the
        /// Resonance gear mod: 15 / 30 / 50% at ranks 1/2/3, from the
        /// class_ability_spellaoe_damage_mult_rN tunables.
        ///
        /// Rank is CLAMPED rather than rejected, the same convention the rest of the class-ability family
        /// uses (see SanguineWardMath.SelfCostFraction): a persisted rank above MaxRank behaves as max
        /// rank, and a rank at or below 0 falls back to rank 1 rather than returning a zero-damage blast.
        /// The dispatch only reaches this handler for a player who owns the ability, so rank 0 is a
        /// defensive floor, not a live case.
        /// </summary>
        public static double BaseDamageMult(int rank, double r1, double r2, double r3) => rank switch
        {
            <= 1 => r1,
            2 => r2,
            _ => r3,
        };

        /// <summary>
        /// The fraction of normal damage each radiated blast lands: the RANK's base fraction (see
        /// <see cref="BaseDamageMult"/>) scaled up with the caster's Mana Conversion toward a shared cap
        /// (class_ability_spellaoe_damage_mult_cap, 75%). The Mana Conversion rider is now the shared
        /// MULTIPLICATIVE affinity model (2026-09-12 overhaul, Trained/Specialized-gated); returns the
        /// rank base when the caster is null.
        ///
        /// The clamp's FLOOR is the rank's own base, so the rider and the gear mod can only ever add; the
        /// CEILING is rank-independent, and only rank 3 realistically approaches it.
        /// </summary>
        private static float RadiatedDamageMult(Player caster, int rank)
        {
            var baseMult = BaseDamageMult(rank,
                PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r1").Item,
                PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r2").Item,
                PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r3").Item);

            var cap = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_cap").Item;

            if (caster == null)
                return (float)baseMult;

            // Mana Conversion is MULTIPLICATIVE on the rank's base fraction (2026-09-12 overhaul), not a
            // dual-ratio additive rider. The amount the multiplier adds (baseMult * affinity - baseMult) is
            // what feeds the additive damage-fraction expression below, unchanged in shape.
            var affinity = caster.GetClassAbilityAffinityMultiplier(Skill.ManaConversion);
            var rider = baseMult * affinity - baseMult;

            // Resonance equipment mod (MACHINERY): another additive term on the radiated-damage fraction,
            // still bounded by the same cap as the Mana Conversion rider. Only reachable from this handler,
            // which runs solely for a caster who owns Spell AOE - there is no radiated blast to amplify
            // otherwise.
            var gearMod = caster.GetEquippedModValue(EquipmentModId.Resonance);

            return (float)Math.Clamp(baseMult + rider + gearMod, baseMult, cap);
        }

        /// <summary>
        /// Mirrors the terms in RadiatedDamageMult above - the two must stay in step. "Skill" here is
        /// THIS RANK's base fraction (a table lookup, not a per-rank product); Affinity is the raw
        /// (unclamped) Mana Conversion rider and Gear is the raw Resonance mod, with Effective applying
        /// the same Math.Clamp(baseMult + rider + gearMod, baseMult, cap) RadiatedDamageMult uses.
        /// CapNote is set only when the clamp actually reduced this call's value.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var baseMult = BaseDamageMult(rank,
                PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r1").Item,
                PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r2").Item,
                PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_r3").Item);

            var cap = PropertyManager.GetDouble("class_ability_spellaoe_damage_mult_cap").Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.ManaConversion);
            var rider = baseMult * multiplier - baseMult;

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
