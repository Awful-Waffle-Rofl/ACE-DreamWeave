using System;
using System.Collections.Generic;
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
    /// it on its re-fires. Neither gets the ramp and neither advances the count. An ECHO CAST wisp cast is a
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
            DescriptionTemplate = "In one wall spell cast, each projectile that lands after the first hits harder than the one " +
                          "before. Each Echo Cast wisp cast counts as a separate cast.",
            // Live text: the step for rank n is BonusKey(n) = class_ability_volatilewall_bonus_r + n (BonusKey; read in GetReadout and in
            // Player_ArchmageResistance.cs:106-107). Built by interpolation, which the source-scan guard accepts from this file alone (it sees the quoted
            // prefix followed by an interpolation hole and a rank digit). The token tail differs in shape per
            // rank ("the step rises to ..."), so it is a per-rank array. Its multiplier lists are DamageMultiplier at 1 and 2
            // earlier landed hits over the neutral-affinity PerProjectileBonus (the methods Player.ApplyVolatileWall calls, which
            // reads the rank's key through PerProjectileBonus(values, ...)); the leading 1.0x is the first landed hit (no earlier hits).
            RankTailTemplate = "+{ranks:class_ability_volatilewall_bonus_r|pct}% per earlier hit (1.0x, {fn:second_hit|x}, " +
                          "{fn:third_hit|x}...)",
            TextFormulas = new Dictionary<string, TextFormula>
            {
                ["second_hit"] = new TextFormula(
                    (values, rank) => DamageMultiplier(1, PerProjectileBonus(values, rank, 1.0)),
                    "class_ability_volatilewall_bonus_r1",
                    "class_ability_volatilewall_bonus_r2",
                    "class_ability_volatilewall_bonus_r3"),
                ["third_hit"] = new TextFormula(
                    (values, rank) => DamageMultiplier(2, PerProjectileBonus(values, rank, 1.0)),
                    "class_ability_volatilewall_bonus_r1",
                    "class_ability_volatilewall_bonus_r2",
                    "class_ability_volatilewall_bonus_r3"),
            },
            AllowedLiterals = new[]
            {
                ("(1.0x, ", "structural: the first landed projectile of a wall cast deals base damage, exactly 1.0x"),
            },
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
        /// <see cref="PerProjectileBonus(int, double, double)"/> with the rank's bonus tunable (<see cref="BonusKey"/>) read through
        /// <paramref name="values"/>. Player.ApplyVolatileWall calls this with <see cref="ClassAbilityText.Live"/> and the War Magic
        /// affinity; the live text passes the neutral 1.0. 0 at rank 0.
        /// </summary>
        public static double PerProjectileBonus(ITunableValues values, int rank, double affinityMultiplier) =>
            PerProjectileBonus(rank, RankBonus(values, rank), affinityMultiplier);

        /// <summary>The rank's bonus tunable (<see cref="BonusKey"/>) read through <paramref name="values"/>; 0 at rank 0. The readout passes <see cref="ClassAbilityText.Live"/>.</summary>
        public static double RankBonus(ITunableValues values, int rank) => rank <= 0 ? 0.0 : values.GetDouble(BonusKey(rank));

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
            var rankBonus = RankBonus(ClassAbilityText.Live, rank);

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
