using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.Managers;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The World Events side of the combat-trait guard (world_events_strip_combat_traits): which spawns get
    /// it, and the four dials it runs on. The writes themselves are ThreadDungeons.CombatTraitGuard.Apply,
    /// shared with Threads; this class owns only the WE-specific decisions, all pure so they can be tested
    /// without a live Creature.
    ///
    /// WHO GETS IT. Every roster-drawn WAVE monster (trash, elite, overflow champion), uplifted or not, and
    /// the FAMILY champion (spawned through the Boss kind but drawn from the same roster). A NAMED boss never
    /// does: it has its own health and stat regime, and an immunity it carries is authored on purpose.
    ///
    /// DIALS ARE WE-OWNED. This code reads only world_events_* keys. Their DEFAULTS equal the Threads code
    /// defaults for the two category dials (same guard, same numbers) and are tighter for the defense cap
    /// (50 against Threads' 100): a wave monster fights a crowd, not a four-player group.
    /// </summary>
    public static class WorldEventCombatGuard
    {
        public const bool DefaultStripCombatTraits = true;

        public const double DefaultCategoryResistFloor = DungeonPopulationLimits.DefaultCategoryResistFloor;

        public const double DefaultCategoryArmorModCeiling = DungeonPopulationLimits.DefaultCategoryArmorModCeiling;

        /// <summary>Cap on a creature's effective defense skill: the band's effective median plus this. Negative disables.</summary>
        public const double DefaultDefenseSkillCapOffset = 50.0;

        public readonly struct GuardDials
        {
            public readonly bool Enabled;
            public readonly double CategoryResistFloor;
            public readonly double CategoryArmorModCeiling;
            public readonly double DefenseSkillCapOffset;

            public GuardDials(bool enabled, double categoryResistFloor, double categoryArmorModCeiling, double defenseSkillCapOffset)
            {
                Enabled = enabled;
                CategoryResistFloor = categoryResistFloor;
                CategoryArmorModCeiling = categoryArmorModCeiling;
                DefenseSkillCapOffset = defenseSkillCapOffset;
            }

            public static GuardDials Defaults => new GuardDials(DefaultStripCombatTraits, DefaultCategoryResistFloor,
                DefaultCategoryArmorModCeiling, DefaultDefenseSkillCapOffset);

            /// <summary>The numeric dials in the shape the shared guard takes.</summary>
            public CombatGuardDials ToCombatDials()
                => new CombatGuardDials(CategoryResistFloor, CategoryArmorModCeiling, DefenseSkillCapOffset);
        }

        /// <summary>Test seam, same contract as WorldEventRosterSelector.DialSource: reassign and restore.</summary>
        internal static Func<GuardDials> DialSource = ReadFromProperties;

        private static GuardDials ReadFromProperties()
        {
            try
            {
                return new GuardDials(
                    PropertyManager.GetBool("world_events_strip_combat_traits", DefaultStripCombatTraits).Item,
                    PropertyManager.GetDouble("world_events_category_resist_floor", DefaultCategoryResistFloor).Item,
                    PropertyManager.GetDouble("world_events_category_armor_mod_ceiling", DefaultCategoryArmorModCeiling).Item,
                    PropertyManager.GetDouble("world_events_defense_skill_cap_offset", DefaultDefenseSkillCapOffset).Item);
            }
            catch (Exception)
            {
                // No shard config (a unit test, or a server that has not initialized yet): the built-in defaults.
                return GuardDials.Defaults;
            }
        }

        /// <summary>The live dials, sanitized.</summary>
        internal static GuardDials Current() => Sanitize(DialSource());

        /// <summary>
        /// The same sanitizing rules the Threads reader applies to the same three numbers: a NaN, Infinity or
        /// negative category dial reads as the default and is clamped at its ceiling (0 disables); the
        /// defense-cap offset reads NaN/Infinity as the default, keeps a negative value (which DISABLES the
        /// cap) and clamps at its ceiling.
        /// </summary>
        internal static GuardDials Sanitize(GuardDials dials)
        {
            return new GuardDials(dials.Enabled,
                SanitizeCategory(dials.CategoryResistFloor, DefaultCategoryResistFloor, DungeonPopulationLimits.MaxCategoryResistFloor),
                SanitizeCategory(dials.CategoryArmorModCeiling, DefaultCategoryArmorModCeiling, DungeonPopulationLimits.MaxCategoryArmorModCeiling),
                SanitizeOffset(dials.DefenseSkillCapOffset));
        }

        private static double SanitizeCategory(double value, double fallback, double ceiling)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                return fallback;

            return Math.Min(value, ceiling);
        }

        private static double SanitizeOffset(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return DefaultDefenseSkillCapOffset;

            if (value < 0)
                return value;

            return Math.Min(value, DungeonPopulationLimits.MaxDefenseSkillCapOffset);
        }

        /// <summary>
        /// True when a spawn of <paramref name="kind"/> is guarded: Wave-kind always, the Boss kind only when
        /// it is NOT the named boss (<paramref name="isNamedBoss"/>), and nothing else - sources, objectives,
        /// NPCs, decor and generated adds are not roster monsters.
        /// </summary>
        public static bool AppliesTo(WorldEventSpawnKind kind, bool isNamedBoss)
        {
            switch (kind)
            {
                case WorldEventSpawnKind.Wave:
                    return true;

                case WorldEventSpawnKind.Boss:
                    return !isNamedBoss;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The effective-defense ceiling the guard would impose on <paramref name="skill"/> against a band
        /// standard, or 0 for "no cap" - the shared DungeonCombatNormalizer rule under WE's dial.
        /// </summary>
        public static uint DefenseCapFor(DungeonBandStandard standard, ACE.Entity.Enum.Skill skill, GuardDials dials)
            => DungeonCombatNormalizer.DefenseSkillCap(standard, skill, dials.DefenseSkillCapOffset);
    }
}
