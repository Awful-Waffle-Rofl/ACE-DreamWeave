using System;
using System.Collections.Generic;

using ACE.Entity;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The ONE definition of "near the vulnerable crystal" in Attack/Defend (Docs/Pvp/ATTACK-DEFEND.md "Kill chip and heal",
    /// Docs/Pvp/BATTLEGROUNDS.md "Performance stats"). The kill chip and heal (<see cref="CrystalKillRules.Decide"/>) and the per-player
    /// performance ledger (<see cref="BattlegroundPerformanceLedger"/>) both ask it, so the two can never disagree about which deaths and
    /// hits count as near. Pure: the caller supplies every input, the destroyed set included.
    /// </summary>
    public static class CrystalProximity
    {
        /// <summary>
        /// The plan index of the crystal <paramref name="position"/> is near, or -1 when it is near none. In sequential play
        /// (<paramref name="sequence"/> not null) only the vulnerable crystal (<see cref="CrystalSequence.Current"/>) counts; in any-order
        /// play the standing crystal nearest the position, if it is within range. A crystal <paramref name="isStanding"/> reports fallen is
        /// skipped (null treats every crystal as standing). -1 for no crystals, a range that is 0, NaN, infinite or negative, no position,
        /// or a position outside the match instance. The distance is <see cref="DistanceToSite"/> (3D, inclusive of the range).
        /// </summary>
        /// <param name="matchLandblock">The match space's landblock id (high 16 bits of every cell in it), which the sites' cells sit in.</param>
        /// <param name="matchInstance">The match space's instance; a position in any other instance is never near.</param>
        public static int VulnerableIndexNear(IReadOnlyList<PlannedCrystal> crystals, CrystalSequence sequence, uint matchLandblock, uint matchInstance, double range, Position position, Func<int, bool> isStanding = null)
        {
            if (crystals == null || crystals.Count == 0)
                return -1;

            if (double.IsNaN(range) || double.IsInfinity(range) || range <= 0.0 || position == null || position.Instance != matchInstance)
                return -1;

            if (sequence != null)
            {
                var current = sequence.Current;

                if (current >= 0 && current < crystals.Count && (isStanding?.Invoke(current) ?? true) && DistanceToSite(position, crystals[current].Site, matchLandblock, matchInstance) <= range)
                    return current;

                return -1;
            }

            var index = -1;
            var best = double.MaxValue;

            foreach (var crystal in crystals)
            {
                if (!(isStanding?.Invoke(crystal.Index) ?? true))
                    continue;

                var distance = DistanceToSite(position, crystal.Site, matchLandblock, matchInstance);

                if (distance <= range && distance < best)
                {
                    best = distance;
                    index = crystal.Index;
                }
            }

            return index;
        }

        /// <summary>
        /// <see cref="VulnerableIndexNear(IReadOnlyList{PlannedCrystal}, CrystalSequence, uint, uint, double, Position, Func{int, bool})"/>
        /// over site positions built ONCE (<see cref="SitePositions"/>, element i for <c>crystals[i]</c>), for the performance ledger, which
        /// asks on every PvP hit: same rule, same distances, no allocation (an indexed loop, no enumerator, no per-call Position).
        /// -1 when the two lists disagree in length. Pure.
        /// </summary>
        public static int VulnerableIndexNear(IReadOnlyList<PlannedCrystal> crystals, Position[] sitePositions, CrystalSequence sequence, uint matchInstance, double range, Position position, Func<int, bool> isStanding = null)
        {
            if (crystals == null || crystals.Count == 0 || sitePositions == null || sitePositions.Length != crystals.Count)
                return -1;

            if (double.IsNaN(range) || double.IsInfinity(range) || range <= 0.0 || position == null || position.Instance != matchInstance)
                return -1;

            if (sequence != null)
            {
                var current = sequence.Current;

                if (current >= 0 && current < crystals.Count && (isStanding?.Invoke(current) ?? true) && position.DistanceTo(sitePositions[current]) <= range)
                    return current;

                return -1;
            }

            var index = -1;
            var best = double.MaxValue;

            for (var i = 0; i < crystals.Count; i++)
            {
                var crystal = crystals[i];

                if (crystal == null || !(isStanding?.Invoke(crystal.Index) ?? true))
                    continue;

                double distance = sitePositions[i] == null ? double.MaxValue : position.DistanceTo(sitePositions[i]);

                if (distance <= range && distance < best)
                {
                    best = distance;
                    index = crystal.Index;
                }
            }

            return index;
        }

        /// <summary>
        /// Each crystal's site as a Position in the match space, element i for <c>crystals[i]</c>, built exactly as
        /// <see cref="DistanceToSite"/> builds it (null for a crystal with no site). Pure.
        /// </summary>
        public static Position[] SitePositions(IReadOnlyList<PlannedCrystal> crystals, uint matchLandblock, uint matchInstance)
        {
            if (crystals == null)
                return Array.Empty<Position>();

            var result = new Position[crystals.Count];

            for (var i = 0; i < crystals.Count; i++)
            {
                var site = crystals[i]?.Site;

                if (site != null)
                    result[i] = new Position(((matchLandblock & 0xFFFF) << 16) | site.CellLow, site.X, site.Y, site.Z, 0f, 0f, 0f, 1f, matchInstance);
            }

            return result;
        }

        /// <summary>True when <see cref="VulnerableIndexNear(IReadOnlyList{PlannedCrystal}, CrystalSequence, uint, uint, double, Position, Func{int, bool})"/> finds a crystal. Pure.</summary>
        public static bool IsNearVulnerable(IReadOnlyList<PlannedCrystal> crystals, CrystalSequence sequence, uint matchLandblock, uint matchInstance, double range, Position position, Func<int, bool> isStanding = null)
            => VulnerableIndexNear(crystals, sequence, matchLandblock, matchInstance, range, position, isStanding) >= 0;

        /// <summary>
        /// The 3D distance from <paramref name="position"/> to a crystal site, with the same function Creature.GetDistance uses
        /// (ACE.Entity.Position.DistanceTo: plain x, y, z in the landblock frame for two points in one landblock). Pure.
        /// </summary>
        public static double DistanceToSite(Position position, BattlegroundCrystalSite site, uint matchLandblock, uint matchInstance)
        {
            if (position == null || site == null)
                return double.MaxValue;

            var sitePosition = new Position(((matchLandblock & 0xFFFF) << 16) | site.CellLow, site.X, site.Y, site.Z, 0f, 0f, 0f, 1f, matchInstance);

            return position.DistanceTo(sitePosition);
        }
    }
}
