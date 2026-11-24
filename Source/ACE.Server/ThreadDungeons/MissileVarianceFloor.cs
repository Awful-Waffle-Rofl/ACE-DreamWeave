using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The Threads missile variance floor (dynamic_dungeons_missile_variance_floor, owner decision 2026-11-23): every missile
    /// damage source a Thread run creature carries - ammunition and thrown weapons, equipped or in its pack - has its
    /// DamageVariance raised to at least the floor at spawn, so a missile hit's average-to-max ratio matches a melee hit's.
    ///
    /// WHY. The archetype uplift and the damage ceiling both normalize a creature's MAX hit (body-part DVal, or an item's
    /// Damage) and never read variance (DamageCeiling.cs PerHitSource, ArchetypeUplift.Apply). The engine rolls a hit
    /// uniformly between Max x (1 - Variance) and Max (BaseDamageMod.MinDamage, DamageEvent.GetBaseDamage). Retail body parts
    /// carry DVar around 0.55-0.75, ammunition and thrown weapons 0.25-0.5, so with the max equalized a missile hit averaged
    /// about 25% above the same creature's melee hit (L500 probe on prod build 8f2872c46, 700 plans: missile avg/max median
    /// 0.85, all melee trash 0.656).
    ///
    /// THE DEFAULT. 0.69: the variance at which a missile hit's avg/max, 1 - v/2, equals the count-weighted median avg/max
    /// of every melee trash pick in that probe (body and weapon together, 304 picks, 0.6563): v = 2 x (1 - 0.6563) = 0.687,
    /// rounded to 0.69.
    ///
    /// WHAT IT TOUCHES. Only DamageVariance, only upward, only on Ammunition and Missile (thrown) items. The max hit (Damage)
    /// is never touched, so the uplift, the ceiling and its median read exactly what they read before. Launchers are skipped:
    /// a monster's missile base damage is the AMMO's GetDamageMod (Monster_Missile.GetMissileDamage), whose variance is the
    /// ammo's own (WorldObject.GetBaseDamage); the fired projectile is never read for base damage (DamageEvent's
    /// GetBaseDamage(Creature) goes through Creature.GetBaseDamage, and Creature_Missile.LaunchProjectile stores the equipped
    /// ammo itself as ProjectileAmmo), so flooring the carried ammo is enough. Per instance: PropertiesFloat is deep-cloned
    /// into each item's biota, never shared with the cached weenie.
    /// </summary>
    public static class MissileVarianceFloor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string Tunable = "dynamic_dungeons_missile_variance_floor";

        /// <summary>Default of dynamic_dungeons_missile_variance_floor (ships ON). See the class comment for the derivation.</summary>
        public const double DefaultFloor = 0.69;

        /// <summary>The largest floor the code applies: a variance of 1 would let a hit roll 0.</summary>
        public const double MaxFloor = 0.95;

        /// <summary>The floor as the code reads it: NaN or Infinity reads as the default, a value at or below 0 is off (0), above 0.95 reads 0.95.</summary>
        public static double Sanitize(double configured)
        {
            if (double.IsNaN(configured) || double.IsInfinity(configured))
                return DefaultFloor;

            if (configured <= 0.0)
                return 0.0;

            return Math.Min(configured, MaxFloor);
        }

        /// <summary>True when the configured value is not the value the code uses (NaN, Infinity, negative, or above the maximum).</summary>
        public static bool NeedsWarning(double configured) => !configured.Equals(Sanitize(configured));

        // The last out-of-range value warned about: Read runs once per spawned creature, so a misconfigured dial warns once per
        // distinct value rather than once per creature.
        private static double? lastWarned;

        /// <summary>
        /// The floor in use: the tunable through <see cref="Sanitize"/>, read with the compiled default as its explicit
        /// fallback, warning (once per distinct value) when the value in use differs from the one configured, as the sibling
        /// dials do (ThreadDungeonSpawner.ReadMissileDefenseSkillCapDial). A failed read propagates like theirs, with ONE
        /// exception: with no Config.js loaded at all (ConfigManager.Config null, which only an offline harness or a unit test
        /// that never seeded the key can be in) the shard config read cannot run, and the default is used with a warning. A
        /// running server always has a Config, so a transient shard read failure there is never turned into the default.
        /// </summary>
        public static double Read()
        {
            double configured;

            try
            {
                configured = PropertyManager.GetDouble(Tunable, DefaultFloor).Item;
            }
            catch (NullReferenceException) when (ConfigManager.Config == null)
            {
                log.Warn($"[DYNDUNGEON] {Tunable} could not be read (no Config.js loaded); using the default {DefaultFloor}");
                return DefaultFloor;
            }

            var used = Sanitize(configured);

            if (NeedsWarning(configured) && !configured.Equals(lastWarned))
            {
                lastWarned = configured;
                log.Warn($"[DYNDUNGEON] {Tunable} is configured as {configured} which is out of range (0 to {MaxFloor}); using {used} instead");
            }

            return used;
        }

        /// <summary>The variance a source ends up with (pure): the larger of its own and the floor; a floor of 0 or less leaves it.</summary>
        public static double Floored(double variance, double floor)
            => floor > 0.0 && !(variance >= floor) ? floor : variance;

        /// <summary>True for an item whose DamageVariance a monster missile hit reads: ammunition and thrown weapons.</summary>
        public static bool IsMissileDamageSource(WorldObject item)
            => item != null && (item.WeenieType == WeenieType.Ammunition || item.WeenieType == WeenieType.Missile);

        /// <summary>
        /// Raises every missile damage source in the creature's EquippedObjects and Inventory to the floor. Returns one
        /// (wcid, name, from, to) per item it changed; empty for a null creature or a floor of 0.
        /// </summary>
        public static List<(uint Wcid, string Name, double From, double To)> Apply(Creature creature, double floor)
        {
            var changed = new List<(uint, string, double, double)>();

            if (creature == null || !(floor > 0.0))
                return changed;

            var items = creature.EquippedObjects.Values.Concat(creature.Inventory.Values).Where(IsMissileDamageSource).Distinct().ToList();

            foreach (var item in items)
            {
                var from = item.GetProperty(PropertyFloat.DamageVariance) ?? 0.0;
                var to = Floored(from, floor);

                if (to == from)
                    continue;

                item.SetProperty(PropertyFloat.DamageVariance, to);
                changed.Add((item.WeenieClassId, item.Name, from, to));
            }

            return changed;
        }
    }
}
