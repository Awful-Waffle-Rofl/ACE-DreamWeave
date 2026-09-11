using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using log4net;

using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Which two families a RANDOM composition is allowed to draw (two-family composition, 2026-08-29).
    ///
    /// The problem this exists to solve: a random run picked family "margul", whose whole catalog sits at
    /// level 135-240, for a low-level crowd, and the trash pool's nearest-band fall-through
    /// (WorldEventRosterSelector.SelectPool) then handed that crowd level-135 monsters. Drawing TWO
    /// families and requiring the FIRST of them to reach down to a low level means the union roster always
    /// contains something a low-level crowd can fight, whatever the second family looks like.
    ///
    /// Entirely pure over its <see cref="WorldEventCatalog"/> argument apart from
    /// <see cref="FloorLevelSource"/> (a test seam over one server property), so the whole ordering and
    /// degrade ladder is unit-testable under TECH-DESIGN D6.
    ///
    /// An EXPLICIT "--family" never comes here at all: an admin naming a family (or two) is trusted, and
    /// the composer skips the constraint check entirely.
    /// </summary>
    public static class WorldEventFamilyPairing
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The built-in value of world_events_family_floor_level: the level at or below which family A must
        /// have a catalog member. This is a REAL default, not a "0 means use the JSON value" override dial -
        /// it applies on every random composition unless an operator changes it.
        /// </summary>
        public const int DefaultFloorLevel = 50;

        /// <summary>
        /// How constrained the pair list that was actually returned is. The composer logs it once per run so
        /// a run that could not honour the full rule says so in the log rather than silently degrading.
        ///
        /// Ordered by preference: <see cref="FloorAndCaster"/> is the rule as designed; each following
        /// value drops one requirement because nothing in the catalog could satisfy the previous one.
        /// </summary>
        public enum PairTier
        {
            /// <summary>A reaches the floor AND at least one of A/B has a caster. The intended outcome.</summary>
            FloorAndCaster,

            /// <summary>A reaches the floor, but no pair starting from such an A has a caster on either side.</summary>
            FloorOnly,

            /// <summary>No family reaches the floor at all; every ordered pair is a candidate.</summary>
            Unconstrained,

            /// <summary>The catalog has fewer than two families with members - there is no pair to draw.</summary>
            Single
        }

        /// <summary>
        /// Test seam for world_events_family_floor_level. Null-coalesces to the property read in practice
        /// (this field is never actually null - it is assigned the method group below - but a test may
        /// reassign it to a fixed value and must restore it afterward). Mirrors
        /// <see cref="WorldEventRosterSelector.DialSource"/>, which exists for the same reason: a
        /// PropertyManager read THROWS in a unit test, which has no shard config at all.
        /// </summary>
        internal static Func<int> FloorLevelSource = ResolveFloorLevel;

        /// <summary>
        /// Inner seam over the RAW configured value, so the non-positive fallback in
        /// <see cref="ResolveFloorLevel"/> can be tested without a shard database. Production reads
        /// world_events_family_floor_level through PropertyManager.
        /// </summary>
        internal static Func<long> ConfiguredFloorLevelSource = ReadConfiguredFloorLevel;

        /// <summary>The effective floor level for this composition, through <see cref="FloorLevelSource"/>.</summary>
        public static int FloorLevel()
        {
            return FloorLevelSource();
        }

        /// <summary>
        /// world_events_family_floor_level through PropertyManager. A property read that throws - a unit
        /// test has no shard config - and any non-positive configured value both read as
        /// <see cref="DefaultFloorLevel"/>, which is the only safe direction here: a floor of 0 would admit
        /// no family at all and silently degrade every run to Unconstrained.
        /// </summary>
        private static int ResolveFloorLevel()
        {
            try
            {
                var configured = ConfiguredFloorLevelSource();

                if (configured <= 0 || configured > int.MaxValue)
                    return DefaultFloorLevel;

                return (int)configured;
            }
            catch (Exception ex)
            {
                log.Error("[WORLDEVENT] could not read world_events_family_floor_level; falling back to the built-in default", ex);

                return DefaultFloorLevel;
            }
        }

        private static long ReadConfiguredFloorLevel()
        {
            return PropertyManager.GetLong("world_events_family_floor_level", DefaultFloorLevel).Item;
        }

        /// <summary>
        /// Every ordered (a, b) family pair a random composition may draw from, most constrained tier that
        /// still has candidates, with <paramref name="tier"/> reporting which tier that was.
        ///
        /// Ordered pairs, not unordered: the two slots are NOT interchangeable. Slot A is the one the floor
        /// rule binds, so (low, high) is a candidate while (high, low) is not - that is what guarantees the
        /// union roster reaches down to a low-level crowd. Every pair is enumerated in ordinal id order,
        /// A outer and B inner, so the list is identical for a given catalog on every call and a seeded
        /// shuffle by the caller reproduces one run exactly.
        ///
        /// The degrade ladder, in order, stopping at the first tier with any candidate:
        ///   FloorAndCaster - profile[a].MinLevel &lt;= floorLevel, and profile[a] or profile[b] HasCaster;
        ///   FloorOnly      - profile[a].MinLevel &lt;= floorLevel, caster requirement dropped;
        ///   Unconstrained  - every ordered pair of distinct families;
        ///   Single         - fewer than two eligible families, so one "pair" of (a, null), or nothing.
        ///
        /// A family is eligible when the catalog lists at least one member for it (which is what
        /// WorldEventComposer.TryResolveFamily requires) AND its profile has at least one non-boss member -
        /// a family made up entirely of named bosses can never fill a wave or a champion slot, so pairing it
        /// would produce a run with half an empty roster. Its profile MinLevel is 0, so without this it
        /// would also qualify as the low-level half of every pair for free.
        /// </summary>
        public static List<(string a, string b)> CandidatePairs(WorldEventCatalog catalog, int floorLevel,
            out PairTier tier)
        {
            var pairs = new List<(string a, string b)>();

            var ids = new List<string>();

            if (catalog != null)
            {
                ids = catalog.Families
                    .Where(kv => kv.Value != null && kv.Value.Count > 0)
                    .Select(kv => kv.Key)
                    .Where(id => catalog.Profile(id).MemberCount > 0)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
            }

            if (ids.Count < 2)
            {
                tier = PairTier.Single;

                if (ids.Count == 1)
                    pairs.Add((ids[0], null));

                return pairs;
            }

            var floorAndCaster = new List<(string a, string b)>();
            var floorOnly = new List<(string a, string b)>();
            var unconstrained = new List<(string a, string b)>();

            foreach (var a in ids)
            {
                var profileA = catalog.Profile(a);

                foreach (var b in ids)
                {
                    if (string.Equals(a, b, StringComparison.Ordinal))
                        continue;

                    unconstrained.Add((a, b));

                    if (profileA.MinLevel > floorLevel)
                        continue;

                    floorOnly.Add((a, b));

                    if (profileA.HasCaster || catalog.Profile(b).HasCaster)
                        floorAndCaster.Add((a, b));
                }
            }

            if (floorAndCaster.Count > 0)
            {
                tier = PairTier.FloorAndCaster;
                return floorAndCaster;
            }

            if (floorOnly.Count > 0)
            {
                tier = PairTier.FloorOnly;
                return floorOnly;
            }

            tier = PairTier.Unconstrained;
            return unconstrained;
        }
    }
}
