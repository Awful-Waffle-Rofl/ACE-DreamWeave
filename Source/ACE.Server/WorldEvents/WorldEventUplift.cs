using System;

using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The World Events stat-uplift dials (world_events_stat_curve, world_events_threads_uplift,
    /// world_events_magic_attack_skill_cap) and the damage ceiling dials (world_events_damage_ceiling,
    /// world_events_damage_ceiling_ratio, world_events_damage_ceiling_reference_ratio), bundled so
    /// <see cref="WorldEventUplift.DialSource"/> hands them over in one call. Deliberately WE-owned: this code
    /// never reads a dynamic_dungeons_* key.
    /// </summary>
    public readonly struct WorldEventUpliftDials
    {
        /// <summary>Built-in world_events_stat_curve default (ON, owner ruling: new settings default on).</summary>
        public const bool DefaultStatCurve = true;

        /// <summary>Built-in world_events_threads_uplift default (ON).</summary>
        public const bool DefaultThreadsUplift = true;

        /// <summary>
        /// Built-in world_events_magic_attack_skill_cap default, a separate key from the Threads cap. World Events keeps the
        /// 2026-10-30 value (508); it is decoupled from the Threads default since 2026-11-10.
        /// </summary>
        public const double DefaultMagicAttackSkillCap = 508.0;

        /// <summary>Built-in world_events_damage_ceiling default (ON); the same value as the Threads switch, a separate key.</summary>
        public const bool DefaultDamageCeiling = ThreadDungeons.DamageCeiling.DefaultEnabled;

        /// <summary>Built-in world_events_damage_ceiling_ratio default: the Threads cap (a multiple of the median), a separate key.</summary>
        public const double DefaultDamageCeilingRatio = ThreadDungeons.DamageCeiling.DefaultCap;

        /// <summary>Built-in world_events_damage_ceiling_reference_ratio default: 0 = auto, as Threads; a separate key.</summary>
        public const double DefaultDamageCeilingReferenceRatio = ThreadDungeons.DamageCeiling.DefaultReferenceRatio;

        /// <summary>WE standard and health follow the Threads stat curve above the pivot; retail magic defense from 301.</summary>
        public readonly bool StatCurve;

        /// <summary>Spell tier, attribute raise, effective-skill top-up and defense ceiling on top of the band uplift.</summary>
        public readonly bool ThreadsUplift;

        /// <summary>Runtime magic-attack ceiling on guarded WE creatures; 0 = off.</summary>
        public readonly double MagicAttackSkillCap;

        /// <summary>The Threads per-hit damage ceiling on guarded WE creatures (WorldEventDamageCeiling).</summary>
        public readonly bool DamageCeiling;

        /// <summary>The damage ceiling's cap as a multiple of the slot level's median per-hit.</summary>
        public readonly double DamageCeilingRatio;

        /// <summary>The damage ceiling's reference worst ratio; 0 = auto (the pool's own worst predicted ratio).</summary>
        public readonly double DamageCeilingReferenceRatio;

        public WorldEventUpliftDials(bool statCurve, bool threadsUplift, double magicAttackSkillCap,
            bool damageCeiling = DefaultDamageCeiling, double damageCeilingRatio = DefaultDamageCeilingRatio,
            double damageCeilingReferenceRatio = DefaultDamageCeilingReferenceRatio)
        {
            StatCurve = statCurve;
            ThreadsUplift = threadsUplift;
            MagicAttackSkillCap = magicAttackSkillCap;
            DamageCeiling = damageCeiling;
            DamageCeilingRatio = damageCeilingRatio;
            DamageCeilingReferenceRatio = damageCeilingReferenceRatio;
        }

        public static WorldEventUpliftDials Defaults => new WorldEventUpliftDials(DefaultStatCurve, DefaultThreadsUplift, DefaultMagicAttackSkillCap,
            DefaultDamageCeiling, DefaultDamageCeilingRatio, DefaultDamageCeilingReferenceRatio);
    }

    /// <summary>Reads the World Events stat-uplift dials (see <see cref="WorldEventUpliftDials"/>).</summary>
    public static class WorldEventUplift
    {
        /// <summary>
        /// Test seam for the stat-uplift dials, the same contract as WorldEventRosterSelector.UpliftDialSource: a
        /// test may reassign it and must restore it afterward.
        /// </summary>
        internal static Func<WorldEventUpliftDials> DialSource = ReadFromProperties;

        /// <summary>The live dials, with the magic attack cap and the damage ceiling ratios sanitized (see <see cref="Sanitize"/>).</summary>
        public static WorldEventUpliftDials Current() => Sanitize(DialSource());

        internal static WorldEventUpliftDials ReadFromProperties()
        {
            try
            {
                return new WorldEventUpliftDials(
                    PropertyManager.GetBool("world_events_stat_curve", WorldEventUpliftDials.DefaultStatCurve).Item,
                    PropertyManager.GetBool("world_events_threads_uplift", WorldEventUpliftDials.DefaultThreadsUplift).Item,
                    PropertyManager.GetDouble("world_events_magic_attack_skill_cap", WorldEventUpliftDials.DefaultMagicAttackSkillCap).Item,
                    PropertyManager.GetBool("world_events_damage_ceiling", WorldEventUpliftDials.DefaultDamageCeiling).Item,
                    PropertyManager.GetDouble("world_events_damage_ceiling_ratio", WorldEventUpliftDials.DefaultDamageCeilingRatio).Item,
                    PropertyManager.GetDouble("world_events_damage_ceiling_reference_ratio", WorldEventUpliftDials.DefaultDamageCeilingReferenceRatio).Item);
            }
            catch (Exception)
            {
                // A unit test (or a not-yet-initialized server) has no shard config: read as the built-in defaults.
                return WorldEventUpliftDials.Defaults;
            }
        }

        /// <summary>
        /// The dials with the cap sanitized through DungeonCombatNormalizer.SanitizeMagicAttackSkillCap (NaN or
        /// Infinity reads as this key's own default (508), not the Threads default, negative as 0 = off, rounded, bounded above), and the two damage ceiling
        /// ratios through the Threads rules (DamageCeiling.SanitizeCap: NaN, Infinity or below 1 reads as the default;
        /// DamageCeiling.SanitizeReferenceRatio: NaN, Infinity or negative reads as 0 = auto).
        /// </summary>
        internal static WorldEventUpliftDials Sanitize(WorldEventUpliftDials dials) =>
            new WorldEventUpliftDials(dials.StatCurve, dials.ThreadsUplift,
                // NaN or Infinity reads as THIS key's default (508), not the Threads sanitizer's (490).
                DungeonCombatNormalizer.SanitizeMagicAttackSkillCap(double.IsNaN(dials.MagicAttackSkillCap) || double.IsInfinity(dials.MagicAttackSkillCap)
                    ? WorldEventUpliftDials.DefaultMagicAttackSkillCap : dials.MagicAttackSkillCap),
                dials.DamageCeiling,
                ThreadDungeons.DamageCeiling.SanitizeCap(dials.DamageCeilingRatio),
                ThreadDungeons.DamageCeiling.SanitizeReferenceRatio(dials.DamageCeilingReferenceRatio));
    }
}
