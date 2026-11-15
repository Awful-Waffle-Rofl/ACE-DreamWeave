using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Frays
{
    /// <summary>
    /// The Frays' creatures match the camper: a creature is lifted from its own level to the camper's level plus the
    /// Heat curve's extra levels, on the same per-level stat curve Threads uses (<see cref="DungeonStatCurve"/>):
    /// attributes, attack and defense skills, body-part damage and armour, with Threads' live defense rate above 375.
    /// Health is Threads' band-standard health curve (<see cref="DungeonHealthCurve"/>) at the creature's level, read
    /// from the same dynamic_dungeons_health_curve settings, so a change to either Threads curve moves the Frays too.
    /// Upward only for stats: a creature already above the target keeps them. The player never chooses a level.
    /// </summary>
    public static class FraysLevelScale
    {
        /// <summary>The level a camper's creature spawns at: their level, plus the Heat's extra levels.</summary>
        public static int TargetLevel(int playerLevel, int heat, double levelsPerHeat) =>
            Math.Max(1, playerLevel + (int)Math.Round(Math.Max(0, heat) * levelsPerHeat));

        /// <summary>The level a Threads-shaped creature is built at: the camper's own level, or (heatLevels) plus the Heat curve's levels;
        /// never above maxLevel (0 or less = no cap), the highest Thread a player can run.</summary>
        public static int ShapedLevel(int playerLevel, int heat, bool heatLevels, int maxLevel = 0) =>
            Math.Max(1, Math.Min(heatLevels ? TargetLevel(playerLevel, heat) : playerLevel, maxLevel > 0 ? maxLevel : int.MaxValue));

        /// <summary>The level a camper's creature spawns at on the Heat curve (heat_curve_levels).</summary>
        public static int TargetLevel(int playerLevel, int heat) =>
            Math.Max(1, playerLevel + (int)Math.Round(FraysCurve.LevelsAdded(heat)));

        /// <summary>
        /// How much more health a standard creature has at <paramref name="heat"/> than at Heat 0 for this camper: the Heat's
        /// health curve (heat_curve_health) times Threads' health curve at the level health is read at, as SpawnOne builds it.
        /// Never under 1. Kill pay counts health removed with it (kill_pay_by_health).
        /// </summary>
        public static double HeatHealthMult(int camperLevel, int heat, bool threadsShaped)
        {
            double At(int h)
            {
                var creatureLevel = threadsShaped
                    ? ShapedLevel(camperLevel, h, FraysSettings.Toggle(FraysSettings.ThreadsShapeHeatLevels), FraysSettings.Int(FraysSettings.ThreadsShapeMaxLevel))
                    : FraysSettings.Toggle(FraysSettings.LevelScaling) ? TargetLevel(camperLevel, h) : camperLevel;
                var curve = CurveHealth(HealthLevel(creatureLevel, camperLevel, h, FraysSettings.Int(FraysSettings.HealthCamperLevelCap)));
                return FraysCurve.Health(h) * (curve > 0 ? curve : 1);
            }

            var zero = At(0);
            return zero > 0 ? Math.Max(1, At(Math.Max(0, heat)) / zero) : 1;
        }

        /// <summary>
        /// The level a creature's health is read at: the camper's level, capped at <paramref name="camperLevelCap"/> (0 or less =
        /// no cap), plus the Heat's added levels, and never above the creature's own level. Heat still raises health past the
        /// cap; only the camper's own part is held there.
        /// </summary>
        public static int HealthLevel(int creatureLevel, int camperLevel, int heat, int camperLevelCap)
        {
            if (camperLevelCap <= 0 || camperLevel <= camperLevelCap)
                return creatureLevel;

            return Math.Min(creatureLevel, TargetLevel(camperLevelCap, heat));
        }

        /// <summary>Threads' band-standard health at <paramref name="level"/>, or 0 when Threads' health curve is off.</summary>
        public static double CurveHealth(int level)
        {
            try
            {
                if (!PropertyManager.GetBool("dynamic_dungeons_health_curve", DungeonPopulationLimits.DefaultHealthCurveEnabled).Item)
                    return 0;

                return DungeonHealthCurve.Target(level,
                    PropertyManager.GetDouble("dynamic_dungeons_health_curve_anchor_low", DungeonPopulationLimits.DefaultHealthCurveAnchorLow).Item,
                    PropertyManager.GetDouble("dynamic_dungeons_health_curve_anchor_high", DungeonPopulationLimits.DefaultHealthCurveAnchorHigh).Item,
                    PropertyManager.GetLong("dynamic_dungeons_health_curve_top_level", DungeonPopulationLimits.DefaultHealthCurveTopLevel).Item);
            }
            catch
            {
                // No property store (unit tests): the compiled Threads defaults.
                return DungeonHealthCurve.Target(level, DungeonHealthCurve.DefaultAnchorLow, DungeonHealthCurve.DefaultAnchorHigh);
            }
        }

        /// <summary>Threads' live defense-curve rate above level 375.</summary>
        public static double DefenseRate()
        {
            try
            {
                return DungeonStatCurve.SanitizeDefenseRate(PropertyManager.GetDouble("dynamic_dungeons_defense_curve_rate_above_375", DungeonPopulationLimits.DefaultDefenseCurveRateAbove375).Item);
            }
            catch
            {
                return DungeonStatCurve.DefaultDefenseRateAbove375;
            }
        }

        /// <summary>The level defense_cap is set at.</summary>
        public const int DefenseCapLevel = 300;

        /// <summary>
        /// The highest effective melee, missile and magic defense a Frays creature of <paramref name="level"/> has:
        /// <paramref name="capAt300"/> at level 300, moving <paramref name="perLevel"/> for each level away from it,
        /// never below 1. 0 when the cap is off.
        /// </summary>
        public static uint DefenseCapFor(int level, double capAt300, double perLevel)
        {
            if (!(capAt300 > 0))
                return 0;

            var cap = capAt300 + perLevel * (level - DefenseCapLevel);
            return (uint)Math.Clamp(Math.Round(cap), 1, int.MaxValue);
        }

        /// <summary>
        /// Puts the Frays defense ceiling on a live creature (before EnterWorld): its effective melee, missile and magic
        /// defense, buffs included, never go above the cap for its level. Only ever lowers. A creature authored
        /// far above its peers (or immune, 9999) is brought into line, so every family can be hit.
        /// </summary>
        public static void ApplyDefenseCap(Creature creature)
        {
            var cap = DefenseCapFor(creature.Level ?? 1, FraysSettings.Number(FraysSettings.DefenseCap), FraysSettings.Number(FraysSettings.DefenseCapPerLevel));

            if (cap == 0)
                return;

            creature.DefenseSkillCeilings ??= new Dictionary<Skill, uint>();

            foreach (var skill in new[] { Skill.MeleeDefense, Skill.MissileDefense, Skill.MagicDefense })
            {
                if (!creature.DefenseSkillCeilings.TryGetValue(skill, out var existing) || existing > cap)
                    creature.DefenseSkillCeilings[skill] = cap;
            }
        }

        /// <summary>Sets a live creature's base max health to <paramref name="target"/> (up or down), before EnterWorld.</summary>
        public static void SetMaxHealth(Creature creature, double target)
        {
            if (double.IsNaN(target) || target < 1)
                return;

            var desired = (long)Math.Round(Math.Min(target, int.MaxValue));
            var start = (long)creature.Health.StartingValue + desired - creature.Health.GetMaxValue(false);
            creature.Health.StartingValue = (uint)Math.Clamp(start, 1, int.MaxValue);
        }

        /// <summary>
        /// Lifts a live creature (before EnterWorld) from its own level to <paramref name="target"/>. Health is not
        /// written here: the caller sets it from <see cref="CurveHealth"/>.
        /// </summary>
        public static void Apply(Creature creature, int target)
        {
            var from = creature.Level ?? 1;

            if (target <= from)
                return;

            var defenseRate = DefenseRate();

            // Attributes, each on its own slope (Endurance also feeds health and stamina).
            foreach (var attr in BandUplift.PrimaryAttributes)
            {
                if (!creature.Attributes.TryGetValue(attr, out var a) || a == null || a.Base == 0)
                    continue;

                var axis = DungeonStatCurve.AxisOf(attr);

                if (axis == null)
                    continue;

                a.StartingValue = DungeonStatCurve.Scale(a.Base, DungeonStatCurve.Ratio(axis.Value, from, target, defenseRate, DungeonStatCurve.DefaultMagicDefenseRateAbove300, 1.0, 0));
                a.Ranks = 0;
            }

            // Attack and defense skills the creature was authored with (never adds one).
            var skills = creature.Biota?.PropertiesSkill?.Keys.ToList();

            if (skills != null)
            {
                foreach (var skill in skills)
                {
                    var axis = DungeonStatCurve.AxisOf(skill);
                    var cs = axis == null ? null : creature.GetCreatureSkill(skill, false);

                    if (cs == null || cs.InitLevel == 0)
                        continue;

                    cs.InitLevel = DungeonStatCurve.Scale(cs.InitLevel, DungeonStatCurve.Ratio(axis.Value, from, target, defenseRate, DungeonStatCurve.DefaultMagicDefenseRateAbove300, 1.0, 0));
                }
            }

            // Body-part damage and armour (cloned per instance by BandUplift, never written into the cached weenie).
            var parts = creature.Biota?.PropertiesBodyPart;

            if (parts != null && parts.Count > 0)
            {
                var maxDamage = (uint)Math.Max(0, parts.Values.Where(p => p != null).Select(p => p.DVal).DefaultIfEmpty(0).Max());
                var maxArmor = (uint)Math.Max(0, parts.Values.Where(p => p != null).Select(p => p.BaseArmor).DefaultIfEmpty(0).Max());

                BandUplift.ApplyBodyPartUplift(creature.Biota,
                    DungeonStatCurve.Scale(maxDamage, DungeonStatCurve.Ratio(DungeonStatAxis.Damage, from, target, defenseRate, DungeonStatCurve.DefaultMagicDefenseRateAbove300, 1.0, 0)),
                    DungeonStatCurve.Scale(maxArmor, DungeonStatCurve.Ratio(DungeonStatAxis.Armor, from, target, defenseRate, DungeonStatCurve.DefaultMagicDefenseRateAbove300, 1.0, 0)));
            }

            creature.Level = target;
            creature.Stamina.Current = creature.Stamina.MaxValue;
            creature.Mana.Current = creature.Mana.MaxValue;
        }
    }
}
