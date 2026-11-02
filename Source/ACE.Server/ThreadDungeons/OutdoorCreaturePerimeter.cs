using System;

using ACE.Entity;
using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3b: one run creature's perimeter binding (R10-R14). Stamped on the creature
    /// (Creature.P_OutdoorPerimeter) by ThreadDungeonSpawner BEFORE EnterWorld, and null on every other creature in the
    /// game, so each runtime hook costs one null check off the outdoor path. Immutable; every distance is HORIZONTAL global
    /// metres from the circle centre (R4), and every rule is OutdoorPerimeterRules' (never re-implemented here).
    /// </summary>
    public sealed class OutdoorCreaturePerimeter
    {
        /// <summary>R12's chat line to a player whose strike was refused from outside the circle.</summary>
        public const string RefusedStrikeText = "Your strike unravels before it lands; the Thread holds only those within it.";

        /// <summary>R12's rate limit: at most one refusal line per player per this many seconds.</summary>
        public const double RefusedStrikeNoticeIntervalS = 5.0;

        public double CentreGx { get; }
        public double CentreGy { get; }
        public double Radius { get; }

        /// <summary>True when the creature was spawned at an "interior" point (R13).</summary>
        public bool Interior { get; }

        public OutdoorPerimeterSettings Settings { get; }

        public OutdoorCreaturePerimeter(double centreGx, double centreGy, double radius, bool interior, OutdoorPerimeterSettings settings)
        {
            CentreGx = centreGx;
            CentreGy = centreGy;
            Radius = radius;
            Interior = interior;
            Settings = settings;
        }

        /// <summary>Horizontal metres from the circle centre to a landblock-local position.</summary>
        public double DistanceTo(ushort landblock, double x, double y)
        {
            var dx = OutdoorSiteGeometry.ToGlobalX(landblock, x) - CentreGx;
            var dy = OutdoorSiteGeometry.ToGlobalY(landblock, y) - CentreGy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>Horizontal metres from the circle centre to <paramref name="position"/>; +infinity for no position (treated as outside).</summary>
        public double DistanceOf(Position position)
            => position == null ? double.PositiveInfinity : DistanceTo(position.LandblockId.Landblock, position.PositionX, position.PositionY);

        /// <summary>R11(a): this creature, standing at <paramref name="position"/>, must go home.</summary>
        public bool ShouldReturnHome(Position position) => OutdoorPerimeterRules.ShouldReturnHome(DistanceOf(position), Radius, Settings);

        /// <summary>R11(b): may a target at <paramref name="position"/> be held? Non-players always may.</summary>
        public bool IsValidTarget(bool targetIsPlayer, Position position)
            => !targetIsPlayer || OutdoorPerimeterRules.IsValidTarget(true, DistanceOf(position), Radius);

        /// <summary>R12: is a hit from an attacker at <paramref name="position"/> refused?</summary>
        public bool ShouldRefuseDamage(bool attackerIsPlayer, Position position)
            => attackerIsPlayer && OutdoorPerimeterRules.ShouldRefuseDamage(true, DistanceOf(position), Radius);

        /// <summary>R12's per-player rate limit, pure: true when no line was sent in the last <see cref="RefusedStrikeNoticeIntervalS"/>.</summary>
        public static bool RefusedStrikeNoticeDue(double now, double lastSentAt) => now - lastSentAt >= RefusedStrikeNoticeIntervalS;

        /// <summary>
        /// R11(b) and R13 for one target candidate, pure. A Player past R is never accepted (every candidate, held or
        /// new). R13 gates ACQUISITION only: an interior-spawned creature needs <paramref name="isVisible"/> to take a NEW
        /// target, but a target it already holds (<paramref name="held"/>: its AttackTarget or TauntTarget) is never
        /// dropped for line of sight, so a fight at a doorway does not end the moment the target steps out of view.
        /// <paramref name="isVisible"/> is called only when its answer decides.
        /// </summary>
        public bool AcceptsCandidate(bool isPlayer, Position at, bool held, Func<bool> isVisible)
            => IsValidTarget(isPlayer, at) && (!Interior || held || isVisible());

        /// <summary>The alert path's line-of-sight throttle: at most one check per creature per this many seconds.</summary>
        public const double AlertLosIntervalS = 0.5;

        /// <summary>Pure: is a new line-of-sight check due on the alert path?</summary>
        public static bool AlertLosCheckDue(double now, double lastCheckAt) => now - lastCheckAt >= AlertLosIntervalS;

        /// <summary>
        /// R11(b) and R13 on the AlertMonster path (acquisition: a player moving near an idle creature), pure. The past-R
        /// test is cheap and runs every time. An interior creature's line-of-sight check (a ray cast) runs at most once
        /// per <see cref="AlertLosIntervalS"/>; between checks the alert is refused, which is the cached answer in every
        /// case that reaches here again (an accepted alert wakes the creature, and AlertMonster only alerts an idle one).
        /// <paramref name="lastCheckAt"/> is the creature's own throttle clock.
        /// </summary>
        public bool AcceptsAlert(bool isPlayer, Position at, double now, ref double lastCheckAt, Func<bool> isVisible)
        {
            if (!IsValidTarget(isPlayer, at))
                return false;

            if (!Interior)
                return true;

            if (!AlertLosCheckDue(now, lastCheckAt))
                return false;

            lastCheckAt = now;
            return isVisible();
        }
    }

    /// <summary>
    /// The per-run half of the binding, built once when the run populates (world thread, every tunable read there), then
    /// handed each point by <see cref="Bind"/> on the landblock threads. Pure.
    /// </summary>
    public sealed class OutdoorRunPerimeter
    {
        public OutdoorSiteDef Site { get; }
        public OutdoorPos Drop { get; }
        public OutdoorPerimeterSettings Settings { get; }

        /// <summary>The mob_awareness_range multiplier in force: a creature's effective awareness is its property times this.</summary>
        public double MobAwarenessRange { get; }

        public double CentreGx { get; }
        public double CentreGy { get; }
        public double Radius => Site.Circle.Radius;

        private OutdoorRunPerimeter(OutdoorSiteDef site, OutdoorPos drop, OutdoorPerimeterSettings settings, double mobAwarenessRange)
        {
            Site = site;
            Drop = drop;
            Settings = settings;
            MobAwarenessRange = mobAwarenessRange;
            var centre = OutdoorSiteGeometry.Centre(site.Circle);
            CentreGx = centre.Gx;
            CentreGy = centre.Gy;
        }

        /// <summary>
        /// The run's binding source, or null for a dungeon row or an outdoor row with no circle or no parseable entry.
        /// A booted server never reads an unset key as 0 here: PropertyManager.Initialize pre-seeds every registered default
        /// (DefaultPropertyManager.LoadDefaultProperties), and both keys are registered. The sanitizing below guards against
        /// an ADMIN writing a bad value: a NaN or negative containment margin, or a NaN, zero or negative interior awareness
        /// (which would blind every interior creature), falls back to the compiled default.
        /// </summary>
        public static OutdoorRunPerimeter For(DungeonEntryDef dungeon, OutdoorPerimeterSettings settings, double mobAwarenessRange)
        {
            if (dungeon == null || !dungeon.IsOutdoor || dungeon.Outdoor?.Circle == null || settings == null)
                return null;

            var drop = DungeonPopulationBuilder.OutdoorDrop(dungeon);
            if (drop == null)
                return null;

            var margin = settings.ContainmentMarginM;
            if (double.IsNaN(margin) || double.IsInfinity(margin) || margin < 0)
                margin = OutdoorSiteTunables.DefaultContainmentMarginM;

            var interior = settings.InteriorAwarenessM;
            if (double.IsNaN(interior) || double.IsInfinity(interior) || interior <= 0)
                interior = OutdoorSiteTunables.DefaultInteriorAwarenessM;

            return new OutdoorRunPerimeter(dungeon.Outdoor, drop.Value, settings with { ContainmentMarginM = margin, InteriorAwarenessM = interior }, mobAwarenessRange);
        }

        /// <summary>The binding for a creature spawned at <paramref name="point"/>.</summary>
        public OutdoorCreaturePerimeter Bind(DungeonSpawnPointDef point)
            => new OutdoorCreaturePerimeter(CentreGx, CentreGy, Radius, OutdoorPopulation.IsInterior(point), Settings);

        /// <summary>Horizontal metres from <paramref name="point"/> to the drop (R14).</summary>
        public double PointDistanceToDrop(DungeonSpawnPointDef point)
            => point == null ? 0.0 : OutdoorSiteGeometry.HorizontalDistance(new OutdoorPos((ushort)(point.Cell >> 16), point.X, point.Y), Drop);

        /// <summary>
        /// R13/R14: the awareness PROPERTY value to write so the creature's EFFECTIVE awareness (property x
        /// mob_awareness_range) is clamped by OutdoorPerimeterRules.ClampAwareness, or null to leave it alone (no clamp
        /// needed, or no usable multiplier). <paramref name="authored"/> is the property as authored (null reads as
        /// <paramref name="fallback"/>, the engine default). Never raises a value.
        /// </summary>
        public static double? ClampedAwarenessProperty(double? authored, double fallback, double mobAwarenessRange, bool interior, double pointDistToDrop, OutdoorPerimeterSettings settings)
        {
            if (double.IsNaN(mobAwarenessRange) || double.IsInfinity(mobAwarenessRange) || mobAwarenessRange <= 0)
                return null;

            var raw = authored ?? fallback;
            var effective = (float)(raw * mobAwarenessRange);
            var clamped = OutdoorPerimeterRules.ClampAwareness(effective, interior, pointDistToDrop, settings);

            if (clamped >= effective)
                return null;

            return clamped / mobAwarenessRange;
        }
    }
}
