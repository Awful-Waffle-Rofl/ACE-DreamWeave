using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons.Defs;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Open World Threads WP3b: the pure halves of outdoor population. Nothing here reads PropertyManager, a landblock
    /// or the world database, so every rule is unit tested directly.
    ///
    ///   - <see cref="Assign"/>: which spawn point each drawn creature gets (R6's interior quota, the height fit, and
    ///     overflow scatter with the 2 m spacing rule).
    ///   - <see cref="PlacementGroups{T}"/>: the per-landblock placement queues (R22's placement half): entries grouped by
    ///     their point's landblock so each one is created and entered on its own landblock's action queue.
    /// </summary>
    public static class OutdoorPopulation
    {
        public const string KindInterior = "interior";
        public const string KindOutdoor = "outdoor";

        /// <summary>No two creatures of an outdoor run are assigned within this many horizontal metres of each other.</summary>
        public const double MinSpacingM = 2.0;

        /// <summary>The scatter rings around a reused point: radius step and the slots on the first ring (ring n has n x this many).</summary>
        public const double ScatterRingStepM = 2.5;
        public const int ScatterSlotsFirstRing = 6;
        public const int ScatterRings = 3;

        public static bool IsInterior(DungeonSpawnPointDef p) => p != null && string.Equals(p.Kind, KindInterior, StringComparison.Ordinal);

        /// <summary>
        /// Does a creature of <paramref name="wcid"/> fit <paramref name="point"/>? A point with no recorded
        /// maxCollisionHeight, or no height source, admits everything; otherwise the creature's movement height
        /// (DungeonFitFilter.MovementHeight, scaled) must not exceed it.
        /// </summary>
        public static bool Fits(uint wcid, DungeonSpawnPointDef point, Func<uint, float> movementHeightOf)
        {
            if (point?.MaxCollisionHeight == null || movementHeightOf == null || point.MaxCollisionHeight.Value <= 0f)
                return true;

            return DungeonFitFilter.Fits(movementHeightOf(wcid), point.MaxCollisionHeight.Value, 0.0);
        }

        /// <summary>
        /// The k-th scatter offset (k from 0) around a reused point, in metres east/north: ring r (1-based) has
        /// r x <see cref="ScatterSlotsFirstRing"/> evenly spaced slots at radius r x <see cref="ScatterRingStepM"/>, so
        /// neighbouring slots on a ring are at least 2.5 m apart and the rings are 2.5 m apart. Null past the last ring.
        /// </summary>
        public static (double Dx, double Dy)? ScatterOffset(int k)
        {
            if (k < 0) return null;

            for (var ring = 1; ring <= ScatterRings; ring++)
            {
                var slots = ring * ScatterSlotsFirstRing;
                if (k < slots)
                {
                    var angle = 2.0 * Math.PI * k / slots + (ring - 1) * Math.PI / slots;
                    var radius = ring * ScatterRingStepM;
                    return (radius * Math.Cos(angle), radius * Math.Sin(angle));
                }
                k -= slots;
            }

            return null;
        }

        /// <summary>
        /// A scatter copy of an OUTDOOR point, offset by (<paramref name="dx"/>, <paramref name="dy"/>) metres and still on
        /// the same landblock (null when the offset would cross a landblock edge, or the point is interior: a building's
        /// cells are never scattered into). The cell is recomputed from the new local x,y (24 m outdoor cells), z is the
        /// source z and is snapped to the terrain at placement (<see cref="DungeonSpawnPointDef.Scattered"/>).
        /// </summary>
        public static DungeonSpawnPointDef ScatterCopy(DungeonSpawnPointDef p, double dx, double dy)
        {
            if (p == null || IsInterior(p) || (p.Cell & 0xFFFF) >= 0x100)
                return null;

            var x = p.X + dx;
            var y = p.Y + dy;

            // Half a metre clear of every edge, the same margin physics' own scatter keeps (PhysicsObj.SetScatterPositionInternal).
            if (x < 0.5 || x > OutdoorSiteGeometry.BlockLength - 0.5 || y < 0.5 || y > OutdoorSiteGeometry.BlockLength - 0.5)
                return null;

            var cellX = (uint)(x / 24.0);
            var cellY = (uint)(y / 24.0);
            var cell = (p.Cell & 0xFFFF0000u) | (cellX * 8 + cellY + 1);

            return new DungeonSpawnPointDef
            {
                Cell = cell,
                X = (float)x,
                Y = (float)y,
                Z = p.Z,
                QW = p.QW, QX = p.QX, QY = p.QY, QZ = p.QZ,
                Clearance = p.Clearance,
                Depth = p.Depth,
                Curated = true,
                Source = p.Source,
                Kind = KindOutdoor,
                MaxCollisionHeight = p.MaxCollisionHeight,
                Scattered = true,
            };
        }

        /// <summary>Global horizontal position of a point, in the OutdoorSiteGeometry frame.</summary>
        public static (double Gx, double Gy) GlobalOf(DungeonSpawnPointDef p)
        {
            var landblock = (ushort)(p.Cell >> 16);
            return OutdoorSiteGeometry.ToGlobal(landblock, p.X, p.Y);
        }

        /// <summary>Two interior spots this far apart vertically are on different floors of a building (the spacing rule's stacked-floor exemption).</summary>
        public const double StackedFloorDzM = 2.5;

        /// <summary>A placed or candidate spot as the spacing rule sees it: global x,y, local z, its cell, and whether that cell is interior.</summary>
        public readonly struct Spot
        {
            public readonly double Gx;
            public readonly double Gy;
            public readonly double Z;
            public readonly uint Cell;

            public Spot(double gx, double gy, double z, uint cell) { Gx = gx; Gy = gy; Z = z; Cell = cell; }

            /// <summary>An EnvCell (cell index 0x100 and up) is inside a building; 0x0001-0x0040 are the outdoor land cells.</summary>
            public bool Interior => (Cell & 0xFFFF) >= 0x100;
        }

        public static Spot SpotOf(DungeonSpawnPointDef p)
        {
            var (gx, gy) = GlobalOf(p);
            return new Spot(gx, gy, p.Z, p.Cell);
        }

        /// <summary>A spot from a position key (cell, local x, y, z), e.g. DungeonPlacementFallback.KeyOf.</summary>
        public static Spot SpotOf(uint cell, float x, float y, float z)
        {
            var (gx, gy) = OutdoorSiteGeometry.ToGlobal((ushort)(cell >> 16), x, y);
            return new Spot(gx, gy, z, cell);
        }

        /// <summary>
        /// The spacing rule for one pair, pure. Two INTERIOR spots on different floors (|dz| &gt;=
        /// <see cref="StackedFloorDzM"/>) are clear, so a building's stacked rooms do not cost each other their points.
        /// Interior spots on the same floor, in the same cell or in adjacent cells alike, and every other pair, are
        /// clear only at <see cref="MinSpacingM"/> horizontal metres or more.
        /// </summary>
        public static bool Clear(Spot a, Spot b)
        {
            if (a.Interior && b.Interior && Math.Abs(a.Z - b.Z) >= StackedFloorDzM)
                return true;

            var dx = a.Gx - b.Gx;
            var dy = a.Gy - b.Gy;
            return dx * dx + dy * dy >= MinSpacingM * MinSpacingM;
        }

        /// <summary><see cref="Clear"/> against every spot in <paramref name="taken"/>.</summary>
        public static bool ClearOfAll(Spot s, IEnumerable<Spot> taken)
        {
            foreach (var t in taken)
                if (!Clear(s, t))
                    return false;

            return true;
        }

        private static bool ClearOfAll(DungeonSpawnPointDef p, List<Spot> taken) => ClearOfAll(SpotOf(p), taken);

        /// <summary>
        /// Assigns spawn points to the drawn creatures of an OUTDOOR run (R6). <paramref name="wcids"/> is the draw in pick
        /// order; the result pairs each placed pick index with its point, in pick-index order. Picks with no admissible
        /// point are left out and counted in <paramref name="dropped"/>.
        ///
        ///   1. QUOTA. Interior points first (shuffled), then outdoor points (shuffled): every point takes ONE creature,
        ///      the first remaining pick that fits it (<see cref="Fits"/>), before any point takes a second. An interior point
        ///      no remaining creature fits stays empty, and the creatures go to other points.
        ///   2. OVERFLOW. Picks beyond the point count reuse OUTDOOR points round-robin (in the same order), each reuse at a
        ///      scatter slot (<see cref="ScatterOffset"/>, <see cref="ScatterCopy"/>) that <paramref name="admitsScatter"/>(source, copy)
        ///      accepts.
        ///      Interior points are never reused.
        ///   3. SPACING. No two assignments ever land within the spacing rule (<see cref="Clear"/>) of each other or of a
        ///      <paramref name="reserved"/> spot (the boss anchor), curated points included; a slot or point that would is
        ///      skipped.
        ///
        /// The two shuffles each draw <c>rng.Next()</c> once per point of their kind and nothing else does, so the
        /// assignment is deterministic per seed.
        /// </summary>
        public static List<(int Pick, DungeonSpawnPointDef Point)> Assign(IReadOnlyList<uint> wcids, IReadOnlyList<DungeonSpawnPointDef> points,
            Random rng, Func<uint, float> movementHeightOf, Func<DungeonSpawnPointDef, DungeonSpawnPointDef, bool> admitsScatter,
            IEnumerable<DungeonSpawnPointDef> reserved, out int dropped)
        {
            var result = new List<(int Pick, DungeonSpawnPointDef Point)>();
            dropped = 0;

            if (wcids == null || wcids.Count == 0)
                return result;

            var usable = (points ?? Array.Empty<DungeonSpawnPointDef>()).Where(p => p != null && p.Curated).ToList();
            var interior = usable.Where(IsInterior).OrderBy(_ => rng.Next()).ToList();
            var outdoor = usable.Where(p => !IsInterior(p)).OrderBy(_ => rng.Next()).ToList();
            var order = interior.Concat(outdoor).ToList();

            var remaining = Enumerable.Range(0, wcids.Count).ToList();

            // Spots already spoken for before any pick is placed (the boss anchor): nothing lands within the spacing rule of them.
            var taken = (reserved ?? Array.Empty<DungeonSpawnPointDef>()).Where(r => r != null).Select(SpotOf).ToList();

            // 1. the quota: one creature per point, interior first.
            foreach (var point in order)
            {
                if (remaining.Count == 0)
                    break;

                if (!ClearOfAll(point, taken))
                    continue;

                var at = remaining.FindIndex(i => Fits(wcids[i], point, movementHeightOf));
                if (at < 0)
                    continue;

                var pick = remaining[at];
                remaining.RemoveAt(at);
                result.Add((pick, point));
                taken.Add(SpotOf(point));
            }

            // 2. overflow over the outdoor points only, round-robin, each reuse at its next free scatter slot.
            var nextSlot = new Dictionary<DungeonSpawnPointDef, int>();
            foreach (var p in outdoor)
                nextSlot[p] = 0;
            var cursor = 0;

            foreach (var pick in remaining.ToList())
            {
                DungeonSpawnPointDef placed = null;

                for (var tries = 0; tries < outdoor.Count && placed == null; tries++)
                {
                    var point = outdoor[(cursor + tries) % outdoor.Count];

                    if (!Fits(wcids[pick], point, movementHeightOf))
                        continue;

                    while (placed == null)
                    {
                        var offset = ScatterOffset(nextSlot[point]);
                        if (offset == null)
                            break;

                        nextSlot[point]++;

                        var copy = ScatterCopy(point, offset.Value.Dx, offset.Value.Dy);
                        if (copy == null || (admitsScatter != null && !admitsScatter(point, copy)) || !ClearOfAll(copy, taken))
                            continue;

                        placed = copy;
                        cursor = (cursor + tries + 1) % outdoor.Count;
                    }
                }

                if (placed == null)
                {
                    dropped++;
                    continue;
                }

                result.Add((pick, placed));
                taken.Add(SpotOf(placed));
            }

            result.Sort((a, b) => a.Pick.CompareTo(b.Pick));
            return result;
        }

        /// <summary>
        /// The scatter admission rule for a site, pure: the copy must stay inside the include/exclude areas
        /// (OutdoorSiteGeometry.InIncludeExclude), no further from the circle centre than the curated point it came from
        /// (so it can never move into the warning band the store lint kept the point out of), and no closer to the drop
        /// than min(40 m, the curated point's own distance to the drop).
        /// </summary>
        public static bool AdmitsScatter(OutdoorSiteDef site, OutdoorPos drop, DungeonSpawnPointDef source, DungeonSpawnPointDef copy)
        {
            if (site?.Circle == null || source == null || copy == null)
                return false;

            var centre = OutdoorSiteGeometry.Centre(site.Circle);
            var src = new OutdoorPos((ushort)(source.Cell >> 16), source.X, source.Y);
            var cp = new OutdoorPos((ushort)(copy.Cell >> 16), copy.X, copy.Y);

            if (!OutdoorSiteGeometry.InIncludeExclude(site, cp))
                return false;

            if (OutdoorSiteGeometry.HorizontalDistance(cp, centre) > OutdoorSiteGeometry.HorizontalDistance(src, centre) + 1e-9)
                return false;

            var dropFloor = Math.Min(40.0, OutdoorSiteGeometry.HorizontalDistance(src, drop));
            return OutdoorSiteGeometry.HorizontalDistance(cp, drop) + 1e-9 >= dropFloor;
        }

        /// <summary>
        /// The per-landblock placement queues (R22), pure. The PRIMARY's group is always first (possibly empty, so the
        /// chain the door step starts on the primary has a group to begin with); the other landblocks follow in the order
        /// their first entry appears; within a group the entries keep their plan order. The boss entry is LAST, after
        /// every pack creature (invariant I6), in a group of its own unless its landblock is also the last pack group's.
        /// A dungeon plan (every entry on the primary) is therefore one group holding every entry in plan order, exactly
        /// the single queue it always was.
        /// </summary>
        public static List<(ushort Landblock, List<T> Entries)> PlacementGroups<T>(IReadOnlyList<T> entries, Func<T, ushort> landblockOf,
            Func<T, bool> isBoss, ushort primary)
        {
            var groups = new List<(ushort Landblock, List<T> Entries)> { (primary, new List<T>()) };
            var bosses = new List<T>();

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (isBoss(entry)) { bosses.Add(entry); continue; }

                    var landblock = landblockOf(entry);
                    var index = groups.FindIndex(g => g.Landblock == landblock);
                    if (index < 0)
                    {
                        groups.Add((landblock, new List<T>()));
                        index = groups.Count - 1;
                    }
                    groups[index].Entries.Add(entry);
                }
            }

            foreach (var boss in bosses)
            {
                var landblock = landblockOf(boss);
                var last = groups[groups.Count - 1];

                if (last.Landblock == landblock)
                    last.Entries.Add(boss);
                else
                    groups.Add((landblock, new List<T> { boss }));
            }

            return groups;
        }
    }
}
