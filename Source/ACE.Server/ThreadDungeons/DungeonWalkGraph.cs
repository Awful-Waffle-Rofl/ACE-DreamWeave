using System;
using System.Collections.Generic;
using System.Numerics;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// A dungeon's walkable connectivity for "how far is it on foot": nodes are the landblock's cells, edges the
    /// portals between two of its cells (an exterior portal, OtherCellId 0xFFFF, is never an edge). PURE: built from
    /// a list of portals, so the algorithm is unit-tested on synthetic graphs; DungeonWalkGraphSource builds the real
    /// one from the cell dat.
    ///
    /// A walk is priced as the straight distance between consecutive waypoints: start point, then the centroid of
    /// each portal polygon passed through (landblock-local, the frame every dungeon position uses), then the target
    /// point. Inside one cell the walk is a straight line, so a room's interior walls are not priced; the doorways
    /// between rooms, and so stairs and corridors that turn back on themselves, are.
    /// </summary>
    public sealed class DungeonWalkGraph
    {
        /// <summary>One directed portal: stepping from <see cref="From"/> into <see cref="To"/> through <see cref="Centroid"/>.</summary>
        public readonly struct Portal
        {
            public Portal(uint from, uint to, Vector3 centroid)
            {
                From = from;
                To = to;
                Centroid = centroid;
            }

            public uint From { get; }

            public uint To { get; }

            public Vector3 Centroid { get; }
        }

        private readonly List<Portal> portals = new List<Portal>();
        private readonly Dictionary<uint, List<int>> outgoing = new Dictionary<uint, List<int>>();
        private readonly HashSet<uint> cells = new HashSet<uint>();

        /// <param name="cellIds">Every cell of the dungeon, including any with no internal portal.</param>
        /// <param name="links">Directed portals. A one-sided listing is honoured as one-way; the dat lists each portal from both sides.</param>
        public DungeonWalkGraph(IEnumerable<uint> cellIds, IEnumerable<Portal> links)
        {
            foreach (var cell in cellIds ?? Array.Empty<uint>())
                cells.Add(cell);

            foreach (var link in links ?? Array.Empty<Portal>())
            {
                if (link.From == link.To || !IsFinite(link.Centroid))
                    continue;

                cells.Add(link.From);
                cells.Add(link.To);

                if (!outgoing.TryGetValue(link.From, out var list))
                    outgoing[link.From] = list = new List<int>();

                list.Add(portals.Count);
                portals.Add(link);
            }
        }

        public int CellCount => cells.Count;

        public IReadOnlyList<Portal> Portals => portals;

        public bool HasCell(uint cell) => cells.Contains(cell);

        /// <summary>An unordered cell pair, the key a closed doorway is blocked by (both directions).</summary>
        public static (uint, uint) Pair(uint a, uint b) => a <= b ? (a, b) : (b, a);

        /// <summary>
        /// Shortest walks from <paramref name="start"/> (in <paramref name="startCell"/>) to everywhere, never passing a
        /// portal between a pair of cells in <paramref name="blocked"/>. Null when the start cell is not in the graph.
        /// </summary>
        public WalkField From(uint startCell, Vector3 start, ICollection<(uint, uint)> blocked = null)
        {
            if (!cells.Contains(startCell) || !IsFinite(start))
                return null;

            var dist = new float[portals.Count];
            Array.Fill(dist, float.PositiveInfinity);

            var queue = new PriorityQueue<int, float>();

            bool Open(int e) => blocked == null || blocked.Count == 0 || !blocked.Contains(Pair(portals[e].From, portals[e].To));

            if (outgoing.TryGetValue(startCell, out var first))
            {
                foreach (var e in first)
                {
                    if (!Open(e))
                        continue;

                    var d = Vector3.Distance(start, portals[e].Centroid);

                    if (d < dist[e])
                    {
                        dist[e] = d;
                        queue.Enqueue(e, d);
                    }
                }
            }

            while (queue.TryDequeue(out var e, out var d))
            {
                if (d > dist[e])
                    continue; // a stale entry: a shorter way here was already settled

                if (!outgoing.TryGetValue(portals[e].To, out var next))
                    continue;

                var at = portals[e].Centroid;

                foreach (var n in next)
                {
                    if (!Open(n))
                        continue;

                    var nd = d + Vector3.Distance(at, portals[n].Centroid);

                    if (nd < dist[n])
                    {
                        dist[n] = nd;
                        queue.Enqueue(n, nd);
                    }
                }
            }

            return new WalkField(this, startCell, start, dist);
        }

        /// <summary>
        /// The portal centroids of the shortest walk from <paramref name="start"/> in <paramref name="startCell"/> to
        /// <paramref name="goal"/> in <paramref name="goalCell"/>, in walking order, never through a <paramref name="blocked"/>
        /// pair. Empty when both are in one cell; null when the goal cannot be reached (or either cell is unknown).
        /// The same Dijkstra as <see cref="From"/>, keeping each portal's predecessor.
        /// </summary>
        public List<Vector3> Route(uint startCell, Vector3 start, uint goalCell, Vector3 goal, ICollection<(uint, uint)> blocked = null)
        {
            if (!cells.Contains(startCell) || !cells.Contains(goalCell) || !IsFinite(start) || !IsFinite(goal))
                return null;

            if (startCell == goalCell)
                return new List<Vector3>();

            var dist = new float[portals.Count];
            var prev = new int[portals.Count];
            Array.Fill(dist, float.PositiveInfinity);
            Array.Fill(prev, -1);

            var queue = new PriorityQueue<int, float>();

            bool Open(int e) => blocked == null || blocked.Count == 0 || !blocked.Contains(Pair(portals[e].From, portals[e].To));

            if (outgoing.TryGetValue(startCell, out var first))
            {
                foreach (var e in first)
                {
                    if (!Open(e))
                        continue;

                    var d = Vector3.Distance(start, portals[e].Centroid);

                    if (d < dist[e])
                    {
                        dist[e] = d;
                        queue.Enqueue(e, d);
                    }
                }
            }

            while (queue.TryDequeue(out var e, out var d))
            {
                if (d > dist[e] || !outgoing.TryGetValue(portals[e].To, out var next))
                    continue;

                foreach (var n in next)
                {
                    if (!Open(n))
                        continue;

                    var nd = d + Vector3.Distance(portals[e].Centroid, portals[n].Centroid);

                    if (nd < dist[n])
                    {
                        dist[n] = nd;
                        prev[n] = e;
                        queue.Enqueue(n, nd);
                    }
                }
            }

            var best = -1;
            var bestTotal = float.PositiveInfinity;

            for (var e = 0; e < portals.Count; e++)
            {
                if (portals[e].To != goalCell || float.IsPositiveInfinity(dist[e]))
                    continue;

                var total = dist[e] + Vector3.Distance(portals[e].Centroid, goal);

                if (total < bestTotal)
                {
                    bestTotal = total;
                    best = e;
                }
            }

            if (best < 0)
                return null;

            var route = new List<Vector3>();

            for (var e = best; e >= 0; e = prev[e])
                route.Add(portals[e].Centroid);

            route.Reverse();
            return route;
        }

        /// <summary>
        /// The doorway a gate standing at <paramref name="gatePoint"/> closes: the portal whose centroid is nearest in
        /// the horizontal plane, among portals whose centroid sits between <see cref="DoorwayBelow"/> under and
        /// <see cref="DoorwayAbove"/> over the gate's floor height, and no further than <see cref="DoorwayReach"/>.
        /// Null when no portal qualifies (the gate then blocks nothing in the walk). Ties keep the first portal listed.
        /// </summary>
        public (uint, uint)? NearestDoorway(Vector3 gatePoint)
        {
            var best = -1;
            var bestDistance = float.MaxValue;

            for (var i = 0; i < portals.Count; i++)
            {
                var c = portals[i].Centroid;
                var dz = c.Z - gatePoint.Z;

                if (dz < -DoorwayBelow || dz > DoorwayAbove)
                    continue;

                var h = new Vector2(c.X - gatePoint.X, c.Y - gatePoint.Y).Length();

                if (h <= DoorwayReach && h < bestDistance)
                {
                    best = i;
                    bestDistance = h;
                }
            }

            return best < 0 ? null : Pair(portals[best].From, portals[best].To);
        }

        /// <summary>A gate's doorway portal is at most this far from the gate point, horizontally (metres).</summary>
        public const float DoorwayReach = 3.0f;

        /// <summary>A doorway portal's centroid sits at most this far below the gate's floor height (metres).</summary>
        public const float DoorwayBelow = 1.0f;

        /// <summary>A doorway portal's centroid sits at most this far above the gate's floor height (metres): half a tall doorway.</summary>
        public const float DoorwayAbove = 6.0f;

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        /// <summary>The settled result of one <see cref="From"/> search.</summary>
        public sealed class WalkField
        {
            private readonly DungeonWalkGraph graph;
            private readonly float[] dist;

            internal WalkField(DungeonWalkGraph graph, uint startCell, Vector3 start, float[] dist)
            {
                this.graph = graph;
                this.dist = dist;
                StartCell = startCell;
                Start = start;
            }

            public uint StartCell { get; }

            public Vector3 Start { get; }

            /// <summary>Can any point of <paramref name="cell"/> be walked to (the start cell, or a cell some open portal enters)?</summary>
            public bool Reaches(uint cell)
            {
                if (cell == StartCell)
                    return true;

                for (var e = 0; e < dist.Length; e++)
                {
                    if (graph.portals[e].To == cell && !float.IsPositiveInfinity(dist[e]))
                        return true;
                }

                return false;
            }

            /// <summary>
            /// The walk to <paramref name="point"/> in <paramref name="cell"/>, metres; null when that cell cannot be
            /// reached (not in the graph, or cut off by a blocked doorway). The start cell is a straight line.
            /// </summary>
            public float? DistanceTo(uint cell, Vector3 point)
            {
                if (!IsFinite(point) || !graph.cells.Contains(cell))
                    return null;

                if (cell == StartCell)
                    return Vector3.Distance(Start, point);

                var best = float.PositiveInfinity;

                for (var e = 0; e < dist.Length; e++)
                {
                    if (graph.portals[e].To != cell || float.IsPositiveInfinity(dist[e]))
                        continue;

                    var d = dist[e] + Vector3.Distance(graph.portals[e].Centroid, point);

                    if (d < best)
                        best = d;
                }

                return float.IsPositiveInfinity(best) ? null : best;
            }
        }
    }
}
