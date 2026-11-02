using System;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// VOLATILE WALL (Archmage T3, Archmage pass 2026-10-24). Within ONE wall cast (the AcidWall..SlashingWall
    /// spell categories, ProjectileSpellType.Wall), each projectile that lands after the first hits harder:
    /// the first landed projectile deals base damage, the second x(1 + b), the third x(1 + 2b), and so on,
    /// where b = 3/6/10% by rank (class_ability_volatilewall_bonus_r1/_r2/_r3) multiplied by the War Magic
    /// affinity. Owner example at 10%: 1.0x, 1.1x, 1.2x. ADDITIVE in b, never compounding.
    ///
    /// THE COUNT IS PER CAST, held in a shared <see cref="CastCounter"/> that WorldObject_Magic
    /// .LaunchSpellProjectiles creates once per player wall cast and stamps onto every projectile of that cast.
    /// ONLY THE WALL'S OWN PROJECTILES COUNT (owner ruling 2026-10-24): SpellProjectile.CopyPerCastStamps never
    /// copies the counter, so a Spell AOE child (SpawnClassAbilityAoeChild) has none, and CascadeAbility clears
    /// it on its re-fires. Neither gets the ramp and neither advances the count. An ECHO CAST recast is a
    /// separate cast and keeps the fresh counter its own launch created.
    ///
    /// READ AND ADVANCED IN ONE PLACE: the war/void branch of SpellProjectile.CalculateDamage, inside its PvE
    /// block (player caster, non-player target), through Player.ApplyVolatileWall. The multiplier reads the
    /// count of hits that landed BEFORE this one, then the count advances. A resisted projectile returns from
    /// CalculateDamage before that block and so neither counts nor is boosted. PvP hits never reach the block.
    ///
    /// Bespoke integration; carries IPassiveStatAbility because it hooks no dispatched combat event.
    /// </summary>
    public class VolatileWallAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        /// <summary>
        /// The shared per-cast landed-hit count. A reference type ON PURPOSE: every projectile of one cast
        /// holds the same instance, so a hit by any of them advances the count all of them read. ATOMIC: nothing
        /// guarantees one cast's projectiles resolve on a single thread (landblock groups tick in parallel), so
        /// the read-and-advance is one Interlocked operation and every landed hit gets a distinct prior count.
        /// </summary>
        public sealed class CastCounter
        {
            private int landed;

            public int Landed => Volatile.Read(ref landed);

            /// <summary>Records one landed hit; returns how many had landed BEFORE it.</summary>
            public int RecordLanded() => Interlocked.Increment(ref landed) - 1;
        }

        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.VolatileWall,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 3,
            Name = "volatile_wall",
            DisplayName = "Volatile Wall",
            Description = "Within a single wall spell cast, each projectile that lands after the first deals more " +
                          "damage than the one before: +3/6/10% (by rank) for the second, double that for the third, " +
                          "and so on. Echo Cast copies count as a separate cast. Higher War Magic increases the bonus.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.WarMagic,
        };

        /// <summary>The per-rank tunable key for rank 1-3 (clamped).</summary>
        public static string BonusKey(int rank) => $"class_ability_volatilewall_bonus_r{Math.Clamp(rank, 1, 3)}";

        /// <summary>The per-projectile bonus b at a rank: the rank's tunable x the affinity multiplier. 0 at rank 0.</summary>
        public static double PerProjectileBonus(int rank, double rankBonus, double affinityMultiplier)
        {
            if (rank <= 0)
                return 0.0;

            return Math.Max(0.0, rankBonus) * Math.Max(0.0, affinityMultiplier);
        }

        /// <summary>
        /// The damage multiplier for a hit that lands after <paramref name="priorLanded"/> earlier hits of the
        /// same cast: 1 + priorLanded x b. Pure.
        /// </summary>
        public static float DamageMultiplier(int priorLanded, double perProjectileBonus)
        {
            if (priorLanded <= 0 || perProjectileBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + priorLanded * perProjectileBonus);
        }

        /// <summary>
        /// Mirrors PerProjectileBonus (x100 for display): the per-landed-hit step, rank term plus the AMOUNT the
        /// War Magic multiplier adds. No equipment mod exists for this ability, so Gear is 0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank <= 0 ? 0.0 : PropertyManager.GetDouble(BonusKey(rank)).Item;

            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.WarMagic);

            var skill = rankBonus * 100.0;
            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = 0.0,
                Effective = skill + affinity,
                Unit = "%",
                Label = "wall dmg",
                Per = "/landed hit",
                CapNote = null,
            };
        }
    }
}
