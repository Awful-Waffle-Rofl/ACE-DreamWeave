using System;
using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The "average Thread monster of level N" the Character Details view measures a player against: the effective
    /// skills a median non-boss Threads creature of that level carries, after the three runtime ceilings a spawned
    /// creature gets. INTERNAL and never serialized: the details response carries only player-side percentages, never
    /// a monster stat (owner ruling).
    ///
    /// Every number comes off the SAME band standard a solo run's plan carries at that level
    /// (DungeonPopulationBuilder.ThreadsStandardAt, the one derivation Build itself calls), so the view cannot drift
    /// from what a Threads run actually fields. No level clamp: above the authored roster the stat curve carries it.
    ///
    /// Attack skill per category is the HIGHEST per-skill effective median among that category's skills (the
    /// precedent DungeonCombatNormalizer.HighestAttackMedian sets for a boss): each median is taken over only the
    /// members carrying that skill, so a typical attacker of the category uses its best school, not an average that
    /// counts non-users as zero. A category with no median is 0, which the projector reads as "no row".
    /// </summary>
    public sealed class ThreadBaselineCreature
    {
        public static readonly Skill[] MeleeAttackSkills =
        {
            Skill.TwoHandedCombat, Skill.HeavyWeapons, Skill.LightWeapons, Skill.FinesseWeapons, Skill.Axe, Skill.Dagger, Skill.Mace,
            Skill.Spear, Skill.Staff, Skill.Sword, Skill.UnarmedCombat,
        };

        public static readonly Skill[] MissileAttackSkills =
        {
            Skill.MissileWeapons, Skill.Bow, Skill.Crossbow, Skill.Sling, Skill.ThrownWeapon,
        };

        public static readonly Skill[] MagicAttackSkills =
        {
            Skill.WarMagic, Skill.LifeMagic, Skill.VoidMagic, Skill.CreatureEnchantment,
        };

        public int Level { get; }
        public uint MeleeDefense { get; }
        public uint MissileDefense { get; }
        public uint MagicDefense { get; }
        public uint MeleeAttack { get; }
        public uint MissileAttack { get; }
        public uint MagicAttack { get; }
        public int CritResistRating { get; }

        internal ThreadBaselineCreature(int level, uint meleeDefense, uint missileDefense, uint magicDefense, uint meleeAttack, uint missileAttack,
            uint magicAttack, int critResistRating)
        {
            Level = level;
            MeleeDefense = meleeDefense;
            MissileDefense = missileDefense;
            MagicDefense = magicDefense;
            MeleeAttack = meleeAttack;
            MissileAttack = missileAttack;
            MagicAttack = magicAttack;
            CritResistRating = critResistRating;
        }

        /// <summary>
        /// The baseline over <paramref name="standard"/> with the runtime ceilings <paramref name="limits"/> configures,
        /// applied exactly as the spawner applies them to a non-boss creature: the defense-skill ceiling (median + offset,
        /// only while dynamic_dungeons_strip_combat_traits is on, CombatTraitGuard), the missile defense ceiling and the
        /// magic attack ceiling (ThreadDungeonSpawner.ApplyMissileDefenseCeiling / ApplyMagicAttackCeiling). Null when the
        /// standard is empty. Pure.
        /// </summary>
        public static ThreadBaselineCreature FromStandard(int level, DungeonBandStandard standard, DungeonPopulationLimits limits)
        {
            if (standard == null || standard.IsEmpty)
                return null;

            // The plan's own sanitize of the offset (DungeonPopulationBuilder.Build): NaN/Infinity reads as the default,
            // a negative value disables the cap.
            var offset = double.IsNaN(limits.DefenseSkillCapOffset) || double.IsInfinity(limits.DefenseSkillCapOffset)
                ? DungeonPopulationLimits.DefaultDefenseSkillCapOffset
                : limits.DefenseSkillCapOffset;

            uint Defense(Skill skill)
            {
                var value = standard.EffectiveSkillMedianFor(skill);
                var cap = limits.StripCombatTraits ? DungeonCombatNormalizer.DefenseSkillCap(standard, skill, offset) : 0u;

                return cap == 0 ? value : DungeonCombatNormalizer.ClampToDefenseCeiling(value, cap);
            }

            uint Highest(Skill[] skills)
            {
                var best = 0u;

                foreach (var skill in skills)
                    best = Math.Max(best, standard.EffectiveSkillMedianFor(skill));

                return best;
            }

            var missileDefense = DungeonCombatNormalizer.ClampToDefenseCeiling(Defense(Skill.MissileDefense),
                DungeonCombatNormalizer.MissileDefenseCeilingFor(DungeonCombatNormalizer.SanitizeMissileDefenseSkillCap(limits.MissileDefenseSkillCap)));

            var magicAttack = DungeonCombatNormalizer.ClampToDefenseCeiling(Highest(MagicAttackSkills),
                DungeonCombatNormalizer.MagicAttackCeilingFor(DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(limits.MagicAttackSkillCap)));

            return new ThreadBaselineCreature(level, Defense(Skill.MeleeDefense), missileDefense, Defense(Skill.MagicDefense),
                Highest(MeleeAttackSkills), Highest(MissileAttackSkills), magicAttack, (int)Math.Min(int.MaxValue, standard.CritResistRatingMedian));
        }
    }

    /// <summary>
    /// The live baseline provider: <see cref="ThreadBaselineCreature"/> at a level from the live species tables, the live
    /// weenie profiles and the live dynamic_dungeons_* dials (a solo run's limits). Memoised per (level, dials, tables)
    /// behind its own lock and bounded; cleared with the plan cache (ThreadPlanCache.Invalidate), so a content reload
    /// or weenie-cache clear is seen on the next read. Called on the world thread.
    /// </summary>
    public static class ThreadBaseline
    {
        /// <summary>The memo's bound. A level is one entry per dial set; reaching the bound clears the map.</summary>
        public const int MaxEntries = 512;

        private static readonly object memoLock = new object();
        private static readonly Dictionary<(int Level, string Dials), ThreadBaselineCreature> memo = new Dictionary<(int, string), ThreadBaselineCreature>();
        private static object memoTables;

        /// <summary>Drops every memoised baseline. Safe from any thread.</summary>
        public static void Clear()
        {
            lock (memoLock)
            {
                memo.Clear();
                memoTables = null;
            }
        }

        /// <summary>How many baselines are memoised. Tests only.</summary>
        internal static int Count
        {
            get { lock (memoLock) return memo.Count; }
        }

        /// <summary>The live baseline at <paramref name="level"/>, or null when the band holds no usable sample.</summary>
        public static ThreadBaselineCreature Live(int level)
        {
            var limits = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);

            return For(level, limits, WorldEventHooks.Store.SpeciesTables, ThreadDungeonSpawner.LiveLevelOf, ThreadDungeonSpawner.LiveProfileOf);
        }

        /// <summary>The memoised core of <see cref="Live"/>, with every input passed in (tests).</summary>
        internal static ThreadBaselineCreature For(int level, DungeonPopulationLimits limits, IReadOnlyDictionary<string, SpeciesTableDef> species,
            Func<uint, int> levelOf, Func<uint, DungeonStatProfile> profileOf)
        {
            if (level <= 0 || species == null)
                return null;

            var key = (level, DialsKey(limits));

            lock (memoLock)
            {
                // a reloaded axis store is a new tables instance: everything memoised against the old one is stale
                if (!ReferenceEquals(memoTables, species))
                {
                    memo.Clear();
                    memoTables = species;
                }

                if (memo.TryGetValue(key, out var cached))
                    return cached;
            }

            var standard = DungeonPopulationBuilder.ThreadsStandardAt(level, limits, species, levelOf, profileOf);
            var value = ThreadBaselineCreature.FromStandard(level, standard, limits);

            lock (memoLock)
            {
                if (!ReferenceEquals(memoTables, species))
                    return value;

                if (memo.Count >= MaxEntries)
                    memo.Clear();

                memo[key] = value;
            }

            return value;
        }

        /// <summary>Every dial ThreadsStandardAt and FromStandard read, so a changed dial is a different key.</summary>
        internal static string DialsKey(DungeonPopulationLimits l)
            => string.Join("|", l.StatCurve, l.ReachUp, R(l.RosterBandLow), R(l.RosterBandHigh), R(l.BandLowFloorRatio), R(l.DefenseCurveRateAbove375),
                R(l.MagicDefenseCurveRateAbove300), R(l.DamageCurveRateAbovePivot), l.StripCombatTraits, R(l.DefenseSkillCapOffset),
                R(l.MagicAttackSkillCap), R(l.MissileDefenseSkillCap), R(l.WeaponAttackCurveRateAbove400), R(l.MeleeDefenseCurveRateAbove400));

        private static string R(double value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }
}
