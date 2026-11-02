using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;

using Position = ACE.Entity.Position;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// Where a run's anchor-frame point lives in the world. A run's region is in its ANCHOR landblock's frame
    /// (HazardRegion.Landblock, the landblock the driver ticks on). A single-landblock region is the identity: one
    /// slot, the run's own landblock, every point resolves to it unchanged. A spanning region
    /// (<see cref="HazardRegion.SpansLandblocks"/>) covers 1..4 landblocks (at most 2 per axis, since a spanning
    /// region is under 192 m on each side) and maps a point to its owning landblock plus that landblock's local
    /// position.
    ///
    /// THREADING. A neighbour is touched (its players read, an object spawned on it, its terrain sampled) only while
    /// it is <see cref="Usable"/>: loaded, and in the very same LandblockGroup object as the anchor. Landblocks of one
    /// group tick on one thread, one after another (LandblockManager.TickMultiThreadedWork: Parallel.ForEach over
    /// groups, a plain foreach over each group's landblocks), and the driver's tick is an action on the anchor's own
    /// queue, run inside that foreach (Landblock.TickMultiThreadedWork -> actionQueue.RunActions). Group membership
    /// changes only between the parallel phases (ProcessPendingLandblockGroupAdditions at the top of each phase and
    /// UnloadLandblocks after them, both on the world thread), so a usability computed at the top of a driver tick
    /// holds for the whole tick. Adjacent same-instance outdoor landblocks always share a group once grouped
    /// (LandblockGroupClustering.MayCluster: Chebyshev distance 1 is under both spacings), but a just-loaded one has
    /// no group until the next phase starts, and a dungeon landblock is always alone: those are skipped for the tick.
    /// Once a neighbour has been usable it stops being usable only by unloading (groups merge whole, and a split
    /// never separates landblocks MayCluster joins), and an unload destroys every transient object on it - so a
    /// handle spawned on a neighbour is either on this thread or no longer Alive.
    ///
    /// <see cref="Refresh"/> runs once per driver tick (HazardContext.BeginTick) and once before a teardown.
    /// </summary>
    internal sealed class HazardSpan
    {
        /// <summary>The world as the span sees it. Live: LandblockManager and Landblock.CurrentLandblockGroup. Tests: fakes.</summary>
        internal interface IWorld
        {
            /// <summary>The loaded landblock copy at (id, instance), or null. Must never load one.</summary>
            Landblock Find(ushort landblockId, uint instance);

            /// <summary>The tick group <paramref name="landblock"/> is in now, or null when it has none.</summary>
            object GroupOf(Landblock landblock);
        }

        /// <summary>One covered landblock: its id, its offset from the anchor in landblocks, and this tick's copy and usability.</summary>
        internal sealed class Slot
        {
            internal Slot(ushort id, int dx, int dy)
            {
                Id = id;
                Dx = dx;
                Dy = dy;
            }

            public ushort Id { get; }

            /// <summary>Landblocks east (+) / west (-) of the anchor.</summary>
            public int Dx { get; }

            /// <summary>Landblocks north (+) / south (-) of the anchor.</summary>
            public int Dy { get; }

            /// <summary>The anchor-frame offset of this landblock's local origin.</summary>
            public Vector2 Offset => new Vector2(Dx * Length, Dy * Length);

            public bool IsAnchor => Dx == 0 && Dy == 0;

            /// <summary>This tick's loaded copy (the anchor slot: always the run's landblock). Null when not loaded.</summary>
            public Landblock Current { get; internal set; }

            /// <summary>This tick: may the driver touch it?</summary>
            public bool Usable { get; internal set; }
        }

        private sealed class LiveWorld : IWorld
        {
            public static readonly LiveWorld Instance = new LiveWorld();

            public Landblock Find(ushort landblockId, uint instance)
                => LandblockManager.GetLoadedLandblock(new LandblockId(((uint)landblockId << 16) | 0xFFFF), instance);

            public object GroupOf(Landblock landblock) => landblock?.CurrentLandblockGroup;
        }

        /// <summary>Outdoor landblock edge length in metres.</summary>
        public const float Length = HazardRegion.LandblockLength;

        /// <summary>The largest landblock x or y index (0xFF is not a landblock column/row; 0xFFFF is the cell mask).</summary>
        public const int MaxIndex = 0xFE;

        private readonly Slot[] slots;
        private readonly IWorld world;
        private readonly HashSet<ushort> loggedUnusable = new HashSet<ushort>();

        private HazardSpan(ushort anchorId, uint instance, bool spans, Landblock anchor, Slot[] slots, IWorld world)
        {
            AnchorId = anchorId;
            Instance = instance;
            Spans = spans;
            Anchor = anchor;
            this.slots = slots;
            this.world = world;
        }

        /// <summary>The anchor landblock id (HazardRegion.Landblock).</summary>
        public ushort AnchorId { get; }

        public uint Instance { get; }

        /// <summary>The run's own landblock copy (the driver's). Null only in registry tests.</summary>
        public Landblock Anchor { get; }

        /// <summary>False: the single-landblock identity. True: a spanning region's 1..4 covered landblocks.</summary>
        public bool Spans { get; }

        /// <summary>The covered landblocks, the anchor first when covered.</summary>
        public IReadOnlyList<Slot> Slots => slots;

        // ---- pure ------------------------------------------------------------------------------------------------

        /// <summary>
        /// The landblocks a spanning region's bounding box covers, as (id, dx, dy), anchor first. A point at exactly
        /// x = 192 belongs to the landblock east of the anchor (its local x is 0), so a box reaching 192 covers it;
        /// x = 0 is the anchor's. False with the reason when the box leaves the (-192, 384) frame, does not cover the
        /// anchor itself, or needs a landblock past the world edge (column/row 0x00 going west/south, 0xFE going
        /// east/north). Pure.
        /// </summary>
        internal static bool TryCover(HazardRegion region, out List<(ushort Id, int Dx, int Dy)> cover, out string error)
        {
            cover = new List<(ushort, int, int)>();
            error = null;

            if (region == null)
            {
                error = "no region.";
                return false;
            }

            if (!region.SpansLandblocks)
            {
                cover.Add((region.Landblock, 0, 0));
                return true;
            }

            var x0 = BlockIndex(region.MinX);
            var x1 = BlockIndex(region.MaxX);
            var y0 = BlockIndex(region.MinY);
            var y1 = BlockIndex(region.MaxY);

            if (x0 < -1 || y0 < -1 || x1 > 1 || y1 > 1 || x1 - x0 > 1 || y1 - y0 > 1)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "spanning region x {0:0.##}..{1:0.##} y {2:0.##}..{3:0.##} needs more than 2 x 2 landblocks around anchor 0x{4:X4}",
                    region.MinX, region.MaxX, region.MinY, region.MaxY, region.Landblock);
                return false;
            }

            if (x0 > 0 || x1 < 0 || y0 > 0 || y1 < 0)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "spanning region x {0:0.##}..{1:0.##} y {2:0.##}..{3:0.##} does not cover its anchor landblock 0x{4:X4}; anchor it on the landblock under its centre",
                    region.MinX, region.MaxX, region.MinY, region.MaxY, region.Landblock);
                return false;
            }

            var ax = region.Landblock >> 8;
            var ay = region.Landblock & 0xFF;

            cover.Add((region.Landblock, 0, 0));

            for (var dx = x0; dx <= x1; dx++)
            {
                for (var dy = y0; dy <= y1; dy++)
                {
                    if (dx == 0 && dy == 0)
                        continue;

                    var nx = ax + dx;
                    var ny = ay + dy;

                    if (nx < 0 || ny < 0 || nx > MaxIndex || ny > MaxIndex)
                    {
                        error = string.Format(CultureInfo.InvariantCulture,
                            "spanning region around anchor 0x{0:X4} reaches past the world edge (landblock {1},{2}); move it inside the map",
                            region.Landblock, nx, ny);
                        cover.Clear();
                        return false;
                    }

                    cover.Add(((ushort)((nx << 8) | ny), dx, dy));
                }
            }

            return true;
        }

        /// <summary>Which landblock (relative to the anchor) an anchor-frame coordinate lies in: floor(v / 192). Pure.</summary>
        internal static int BlockIndex(float v) => (int)Math.Floor(v / Length);

        /// <summary>The landblock offset and local position of an anchor-frame point (z unchanged). Pure.</summary>
        internal static (int Dx, int Dy, Vector3 Local) Split(Vector3 anchorPoint)
        {
            var dx = BlockIndex(anchorPoint.X);
            var dy = BlockIndex(anchorPoint.Y);

            return (dx, dy, new Vector3(anchorPoint.X - dx * Length, anchorPoint.Y - dy * Length, anchorPoint.Z));
        }

        /// <summary>The anchor-frame position of a landblock-local point on landblock <paramref name="landblockId"/>. Pure.</summary>
        internal static Vector3 ToAnchorFrame(ushort anchorId, ushort landblockId, Vector3 local)
        {
            var dx = (landblockId >> 8) - (anchorId >> 8);
            var dy = (landblockId & 0xFF) - (anchorId & 0xFF);

            return new Vector3(local.X + dx * Length, local.Y + dy * Length, local.Z);
        }

        // ---- construction ----------------------------------------------------------------------------------------

        /// <summary>
        /// The span of <paramref name="region"/> hosted on <paramref name="anchor"/>. A single-landblock region (or one
        /// whose cover cannot be computed - Start refuses those before a run exists) is the identity span.
        /// <paramref name="world"/> null = live. No slot is usable until the first <see cref="Refresh"/>, except the
        /// anchor.
        /// </summary>
        internal static HazardSpan For(HazardRegion region, Landblock anchor, IWorld world = null)
        {
            world ??= LiveWorld.Instance;

            var anchorId = region?.Landblock ?? 0;
            var instance = region?.Instance ?? 0;

            if (region == null || !region.SpansLandblocks || !TryCover(region, out var cover, out _))
            {
                var only = new Slot(anchorId, 0, 0) { Current = anchor, Usable = anchor != null };
                return new HazardSpan(anchorId, instance, false, anchor, new[] { only }, world);
            }

            var built = new Slot[cover.Count];

            for (var i = 0; i < cover.Count; i++)
            {
                var (id, dx, dy) = cover[i];
                built[i] = new Slot(id, dx, dy);

                if (dx == 0 && dy == 0)
                {
                    built[i].Current = anchor;
                    built[i].Usable = anchor != null;
                }
            }

            return new HazardSpan(anchorId, instance, true, anchor, built, world);
        }

        // ---- per tick --------------------------------------------------------------------------------------------

        /// <summary>
        /// Re-resolves every neighbour slot (a neighbour may have loaded, unloaded or been reloaded since the last
        /// tick) and recomputes <see cref="Slot.Usable"/>: loaded, and in the anchor's own tick group (same object;
        /// a null group never matches). The anchor slot is always the run's landblock and always usable. Calls
        /// <paramref name="onFirstUnusable"/> once per run per landblock, the first time that landblock is skipped.
        /// A no-op for the identity span.
        /// </summary>
        public void Refresh(Action<Slot, string> onFirstUnusable = null)
        {
            if (!Spans)
                return;

            var anchorGroup = Anchor != null ? world.GroupOf(Anchor) : null;

            foreach (var slot in slots)
            {
                if (slot.IsAnchor)
                    continue;

                var lb = world.Find(slot.Id, Instance);
                slot.Current = lb;

                string why = null;

                if (lb == null)
                    why = "not loaded";
                else if (anchorGroup == null)
                    why = "the anchor has no tick group yet";
                else if (!ReferenceEquals(world.GroupOf(lb), anchorGroup))
                    why = "not on the anchor's tick thread (different or no landblock group)";

                slot.Usable = why == null;

                if (why != null && loggedUnusable.Add(slot.Id))
                    onFirstUnusable?.Invoke(slot, why);
            }
        }

        /// <summary>The slot of <paramref name="landblock"/>, by object (this tick's copy), or null.</summary>
        public Slot SlotOf(Landblock landblock)
        {
            if (landblock == null)
                return null;

            foreach (var slot in slots)
                if (ReferenceEquals(slot.Current, landblock))
                    return slot;

            return null;
        }

        /// <summary>The slot whose landblock id is <paramref name="landblockId"/>, or null.</summary>
        public Slot SlotOf(ushort landblockId)
        {
            foreach (var slot in slots)
                if (slot.Id == landblockId)
                    return slot;

            return null;
        }

        /// <summary>
        /// May the driver touch <paramref name="landblock"/> this tick? The anchor: always. A covered neighbour: as of
        /// the last <see cref="Refresh"/>. Anything else: no.
        /// </summary>
        public bool Usable(Landblock landblock)
        {
            if (landblock == null)
                return false;

            if (ReferenceEquals(landblock, Anchor))
                return true;

            var slot = SlotOf(landblock);
            return slot != null && slot.Usable;
        }

        /// <summary>
        /// The landblock that owns anchor-frame point <paramref name="anchorPoint"/> and the point's local position on
        /// it. Identity span: the run's landblock and the point unchanged, whatever its coordinates (exactly today's
        /// behaviour). Spanning: false when the point is outside the covered landblocks or its landblock is not
        /// usable this tick (<paramref name="owner"/> is then that slot's copy or null).
        /// </summary>
        public bool TryLocate(Vector3 anchorPoint, out Landblock owner, out Vector3 local)
        {
            if (!Spans)
            {
                owner = Anchor;
                local = anchorPoint;
                return owner != null;
            }

            var (dx, dy, l) = Split(anchorPoint);
            local = l;

            foreach (var slot in slots)
            {
                if (slot.Dx != dx || slot.Dy != dy)
                    continue;

                owner = slot.Current;
                return slot.Usable && owner != null;
            }

            owner = null;
            return false;
        }

        /// <summary>
        /// The anchor-frame position of <paramref name="position"/> (a player's or an object's Location), from its
        /// landblock id: local + (dx, dy) x 192. Identity span: the local position unchanged.
        /// </summary>
        public Vector3 ToAnchorFrame(Position position)
        {
            if (position == null)
                return Vector3.Zero;

            return Spans ? ToAnchorFrame(AnchorId, position.LandblockId.Landblock, position.Pos) : position.Pos;
        }
    }
}
