using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Realms
{
    /// <summary>
    /// The set of landblocks one ephemeral instance owns: its primary landblock plus any declared member
    /// landblocks. A plain value with no reference to a Landblock, so the registry can hand it out by instance id
    /// and every decision built on it can be unit tested.
    ///
    /// <para/>
    /// Every ephemeral instance has one. The default, <see cref="Single"/>, is the primary alone, which is exactly
    /// what an ephemeral instance was before footprints existed (a private copy of one dungeon landblock). An
    /// outdoor instance group declares more members; the footprint is fixed for the life of the instance.
    ///
    /// <para/>
    /// Landblock ids here are the 16-bit form (<c>LandblockId.Landblock</c>, 0xXXYY): X in the high byte, Y in
    /// the low byte.
    /// </summary>
    public sealed class EphemeralFootprint
    {
        private readonly HashSet<ushort> landblocks;

        /// <summary>The landblock whose load registered the instance, and whose unload alone may unregister it.</summary>
        public ushort Primary { get; }

        /// <summary>Every footprint landblock, the primary included. Snapshot copy.</summary>
        public IReadOnlyCollection<ushort> Landblocks => landblocks.ToArray();

        /// <summary>Number of footprint landblocks, the primary included.</summary>
        public int Count => landblocks.Count;

        /// <summary>True when the footprint is the primary alone (every ephemeral instance before outdoor groups).</summary>
        public bool IsSingle => landblocks.Count == 1;

        private EphemeralFootprint(ushort primary, IEnumerable<ushort> members)
        {
            Primary = primary;
            landblocks = new HashSet<ushort> { primary };

            if (members != null)
            {
                foreach (var member in members)
                    landblocks.Add(member);
            }
        }

        /// <summary>The default footprint: the primary landblock and nothing else.</summary>
        public static EphemeralFootprint Single(ushort primary) => new EphemeralFootprint(primary, null);

        /// <summary>A footprint of the primary plus <paramref name="members"/>. Duplicates and the primary itself are ignored.</summary>
        public static EphemeralFootprint Create(ushort primary, IEnumerable<ushort> members) => new EphemeralFootprint(primary, members);

        /// <summary>
        /// The square of landblocks within <paramref name="radius"/> (Chebyshev distance) of <paramref name="center"/>,
        /// clipped to the map (0..254 on each axis, as LandblockManager.GetAdjacentID clips), centered on and with the
        /// primary at <paramref name="center"/>. Radius 0 is <see cref="Single"/>.
        /// </summary>
        public static EphemeralFootprint Square(ushort center, int radius)
        {
            if (radius < 0)
                throw new ArgumentOutOfRangeException(nameof(radius), "radius must be zero or more");

            var cx = center >> 8;
            var cy = center & 0xFF;
            var members = new List<ushort>();

            for (var x = cx - radius; x <= cx + radius; x++)
            {
                for (var y = cy - radius; y <= cy + radius; y++)
                {
                    if (x < 0 || x > 254 || y < 0 || y > 254)
                        continue;

                    members.Add((ushort)((x << 8) | y));
                }
            }

            return new EphemeralFootprint(center, members);
        }

        /// <summary>True when <paramref name="landblock"/> is in the footprint (the primary or a member).</summary>
        public bool Contains(ushort landblock) => landblocks.Contains(landblock);

        /// <summary>
        /// True when <paramref name="landblock"/> is OUTSIDE the footprint but one of the 8 neighbours of a footprint
        /// landblock: the ring an outdoor footprint's adjacency loading reaches into (eagerly, or on demand under the
        /// lazy buffer ring; the classification is geometric and the same either way).
        /// </summary>
        public bool IsBuffer(ushort landblock)
        {
            if (landblocks.Contains(landblock))
                return false;

            var x = landblock >> 8;
            var y = landblock & 0xFF;

            foreach (var member in landblocks)
            {
                var dx = Math.Abs((member >> 8) - x);
                var dy = Math.Abs((member & 0xFF) - y);

                if (dx <= 1 && dy <= 1)
                    return true;
            }

            return false;
        }

        public override string ToString() => $"primary 0x{Primary:X4}, {landblocks.Count} landblock(s)";
    }

    /// <summary>
    /// What a landblock loaded under some instance id IS, relative to that instance's registered footprint.
    /// Decided once, when LandblockManager.GetLandblock constructs the landblock, and stored on it.
    /// </summary>
    public enum EphemeralLoadRole
    {
        /// <summary>A persistent (non-ephemeral) instance: the base world or a realm's default copy. Unchanged behaviour.</summary>
        NotEphemeral = 0,

        /// <summary>The landblock whose load registered the ephemeral instance (the creator passed the realm).</summary>
        Primary,

        /// <summary>A declared footprint member of a live instance, loaded later. Inherits the primary's realm and content rules.</summary>
        Member,

        /// <summary>Outside the footprint of a live instance but adjacent to it. Bare terrain.</summary>
        Buffer,

        /// <summary>Outside the footprint of a live instance and not adjacent to it (a physics seam reaching further). Bare terrain.</summary>
        Unrelated,

        /// <summary>An ephemeral instance id with no live registration (it ended, or was never registered). Bare terrain.</summary>
        Orphan,
    }

    /// <summary>The content sources a landblock copy loads (EphemeralInstanceGroup.ContentSourcesFor).</summary>
    [Flags]
    public enum EphemeralContentSources
    {
        None = 0,

        /// <summary>landblock_instance rows for the copy's realm, and therefore its generators.</summary>
        WorldRows = 1,

        /// <summary>Shard-persisted statics (GetStaticObjectsByLandblock).</summary>
        ShardStatics = 2,

        /// <summary>Shard-persisted dynamics (GetDynamicObjectsByLandblock).</summary>
        ShardDynamics = 4,

        /// <summary>Encounter spawns.</summary>
        Encounters = 8,

        All = WorldRows | ShardStatics | ShardDynamics | Encounters,
    }

    /// <summary>
    /// The pure decisions behind ephemeral instance groups (Open World Threads, phase P0). Every rule LandblockManager,
    /// Landblock, LandblockGroup and InstanceRouting apply to a landblock under an ephemeral instance id is here, so
    /// each can be unit tested without a Landblock (which cannot be constructed in a test: it needs the dat files).
    /// See EphemeralInstanceGroupTests.
    /// </summary>
    public static class EphemeralInstanceGroup
    {
        /// <summary>True when <paramref name="instance"/> has the ephemeral bit set.</summary>
        public static bool IsEphemeralInstance(uint instance)
        {
            ACE.Entity.Position.ParseInstanceID(instance, out var isEphemeral, out _, out _);
            return isEphemeral;
        }

        /// <summary>
        /// The role of <paramref name="landblock"/> loaded under <paramref name="instance"/>, given the footprint the
        /// registry holds for that instance right now (<paramref name="registeredFootprint"/>, null when the instance is
        /// not live). <paramref name="registersInstance"/> is true only on the creator's load, the one that passes an
        /// EphemeralRealm to GetLandblock and so registers the instance.
        ///
        /// <para/>
        /// A load of the footprint's primary that does NOT register is classified <see cref="EphemeralLoadRole.Orphan"/>:
        /// a registered instance's primary is by construction already loaded, so reaching here means the registry and
        /// the landblock table disagree, and the safe answer is a copy with no content and no claim on the instance.
        /// </summary>
        public static EphemeralLoadRole Classify(ushort landblock, uint instance, EphemeralFootprint registeredFootprint, bool registersInstance)
        {
            if (!IsEphemeralInstance(instance))
                return EphemeralLoadRole.NotEphemeral;

            if (registersInstance)
                return EphemeralLoadRole.Primary;

            if (registeredFootprint == null)
                return EphemeralLoadRole.Orphan;

            if (landblock == registeredFootprint.Primary)
                return EphemeralLoadRole.Orphan;

            if (registeredFootprint.Contains(landblock))
                return EphemeralLoadRole.Member;

            if (registeredFootprint.IsBuffer(landblock))
                return EphemeralLoadRole.Buffer;

            return EphemeralLoadRole.Unrelated;
        }

        /// <summary>
        /// True when a landblock in this role carries the instance's EphemeralRealm (Landblock.InnerRealmInfo): the
        /// primary, which registered it, and every member, which inherits the primary's. Nothing else does, so a
        /// buffer never reaches the Thread content filter, the run's unload grace, or the unregister path.
        /// </summary>
        public static bool InheritsRealm(EphemeralLoadRole role) => role == EphemeralLoadRole.Primary || role == EphemeralLoadRole.Member;

        /// <summary>
        /// True when a landblock in this role loads world content (landblock_instance rows and therefore generators,
        /// shard statics, shard dynamics, encounters) under its realm's normal rules. False is bare terrain: dat
        /// geometry, dat scenery and sky decor only. The hook for a later per-role content filter (P1) is this
        /// decision; Landblock reads it once per load.
        /// </summary>
        public static bool LoadsWorldContent(EphemeralLoadRole role) =>
            role == EphemeralLoadRole.NotEphemeral || role == EphemeralLoadRole.Primary || role == EphemeralLoadRole.Member;

        /// <summary>
        /// Which content sources a landblock in this role loads. The one chooser Landblock.CreateWorldObjects (world
        /// rows, shard statics), SpawnDynamicShardObjects (shard dynamics) and SpawnEncounters (encounters) consult;
        /// a later per-role content filter (P1) changes this, not those methods. Dat geometry, dat scenery and sky
        /// decor are not content sources here and always load. The Thread content filter and its no-shard-statics
        /// rule are separate and still apply on top, through the realm a Primary or Member carries.
        /// </summary>
        public static EphemeralContentSources ContentSourcesFor(EphemeralLoadRole role) =>
            LoadsWorldContent(role) ? EphemeralContentSources.All : EphemeralContentSources.None;

        /// <summary>
        /// True when this landblock's unload is timed into ace.threads.unload.duration: only the primary of an
        /// ephemeral instance (which is every single-landblock copy, so the metric means what it meant before). A
        /// group's member, buffer or orphan copies would otherwise each add a sample per run and skew it.
        /// </summary>
        public static bool TimesUnload(EphemeralLoadRole role) => role == EphemeralLoadRole.Primary;

        /// <summary>
        /// The input scan for <see cref="DecidePrimaryUnload"/>: of <paramref name="loaded"/>, the items under
        /// <paramref name="primary"/>'s instance other than the primary itself (by reference), and how many of
        /// those are occupied. Generic so it can be tested over fakes; LandblockManager passes its loaded
        /// landblocks, Landblock.Instance and Landblock.HasPlayerIncludingPending.
        /// </summary>
        public static List<T> SelectGroupOthers<T>(IEnumerable<T> loaded, T primary, Func<T, uint> instanceOf, Func<T, bool> isOccupied, out int occupiedCount) where T : class
        {
            var instance = instanceOf(primary);
            var others = new List<T>();
            occupiedCount = 0;

            foreach (var item in loaded)
            {
                if (ReferenceEquals(item, primary) || instanceOf(item) != instance)
                    continue;

                others.Add(item);

                if (isOccupied(item))
                    occupiedCount++;
            }

            return others;
        }

        /// <summary>
        /// True when a loadAdjacents request on a landblock in this role may load its 8 neighbours. Footprint
        /// landblocks may (which is what puts the buffer ring around an outdoor footprint); a buffer, unrelated or
        /// orphan landblock may not, so adjacency loading never walks outward past one ring from the footprint. A
        /// dungeon has no neighbours in any role (LandblockManager.GetAdjacentIDs), so this changes nothing indoors.
        /// </summary>
        public static bool LoadsAdjacents(EphemeralLoadRole role) => LoadsWorldContent(role);

        /// <summary>
        /// <see cref="LoadsAdjacents(EphemeralLoadRole)"/> with the lazy buffer ring applied. When
        /// <paramref name="lazyBufferRing"/> is false (every instance except a live outdoor Thread run that recorded the
        /// setting at start) this is exactly the one-argument form. When it is true, a footprint landblock (Primary or
        /// Member) no longer loads its 8 neighbours eagerly: a neighbour loads only when something needs it (a physics
        /// seam through LScape.get_landblock, a relocation, an object added there), and still under the run's instance id,
        /// so Classify still makes it a Buffer (bare terrain). NotEphemeral is never affected, whatever the flag says.
        /// </summary>
        public static bool LoadsAdjacents(EphemeralLoadRole role, bool lazyBufferRing)
        {
            if (lazyBufferRing && (role == EphemeralLoadRole.Primary || role == EphemeralLoadRole.Member))
                return false;

            return LoadsAdjacents(role);
        }

        /// <summary>
        /// True when an instance uses the lazy buffer ring: it declares a footprint (an outdoor group), it hosts a
        /// Thread run, and that run recorded dynamic_dungeons_outdoor_lazy_buffer_ring = true when it started
        /// (ThreadDungeonRun.LazyBufferRing). The /enter-instance-group seam (a footprint and no run), dungeon Threads (a
        /// run and no footprint) and every other ephemeral instance answer false and keep the eager ring.
        /// </summary>
        public static bool UsesLazyBufferRing(bool hasFootprint, bool hostsRun, bool runRecordedLazyRing) =>
            hasFootprint && hostsRun && runRecordedLazyRing;

        /// <summary>What a new load under the lazy buffer ring does with one neighbour's PHYSICS adjacency list.</summary>
        public enum NeighbourPhysicsSync
        {
            /// <summary>Leave it alone: not under the lazy ring (the old behaviour), or a dungeon-type neighbour, whose list is always empty.</summary>
            Skip,

            /// <summary>Resync it: it is in the tick group running on this thread, or nothing is ticking in parallel.</summary>
            Sync,

            /// <summary>Leave it alone and log: it belongs to another tick group whose thread may be reading the list right now.</summary>
            SkipCrossThread,
        }

        /// <summary>
        /// Which neighbours a new load resyncs the physics adjacency list of (LandblockManager.SetAdjacents). The list is a
        /// plain List read without a lock by PhysicsLandblock.GetServerObjects on the neighbour's own tick thread, so it is
        /// only rewritten when no other thread can be reading it: never outside the lazy ring; never for a dungeon-type
        /// neighbour (each dungeon ticks in a group of its own, LandblockManager.ProcessPendingLandblockGroupAdditions, and
        /// has no adjacents anyway); and, while groups tick in parallel, never for a neighbour already in a group other than
        /// the one this thread is ticking (the same test Landblock.AddWorldObjectInternal applies). A neighbour not yet in
        /// any group is not ticked by anyone, so it is safe. Generic over the group type so it is testable without a Landblock.
        /// </summary>
        public static NeighbourPhysicsSync DecideNeighbourPhysicsSync<TGroup>(bool lazyBufferRing, bool neighbourIsDungeon,
            bool tickingMultiThreaded, TGroup neighbourGroup, TGroup currentThreadGroup) where TGroup : class
        {
            if (!lazyBufferRing || neighbourIsDungeon)
                return NeighbourPhysicsSync.Skip;

            if (tickingMultiThreaded && neighbourGroup != null && !ReferenceEquals(neighbourGroup, currentThreadGroup))
                return NeighbourPhysicsSync.SkipCrossThread;

            return NeighbourPhysicsSync.Sync;
        }

        /// <summary><see cref="UsesLazyBufferRing(bool, bool, bool)"/> read off an instance's realm. Null (no realm) is false.</summary>
        public static bool UsesLazyBufferRing(EphemeralRealm realm) =>
            realm != null && UsesLazyBufferRing(realm.Footprint != null, realm.Run != null, realm.Run?.LazyBufferRing == true);

        /// <summary>
        /// True when the unload of <paramref name="landblock"/> may unregister the instance whose live footprint is
        /// <paramref name="registeredFootprint"/>: only the primary's. A member or buffer unloading on its own idle
        /// timer must leave the instance live, or the first one to go would end the whole group.
        /// </summary>
        public static bool MayUnregister(ushort landblock, EphemeralFootprint registeredFootprint) =>
            registeredFootprint != null && landblock == registeredFootprint.Primary;

        /// <summary>
        /// Open World Threads (R22): is this landblock's own idle unload suspended? Only a footprint MEMBER, and only
        /// while its instance's realm holds members (EphemeralRealm.HoldsFootprintMembers: a live Thread run). The
        /// primary is never held here (its idle window is the run's grace, and its unload is what drains the group);
        /// buffers and orphans carry no realm and are never held. Everything else keeps #1753's behaviour.
        /// </summary>
        public static bool MemberIdleUnloadHeld(EphemeralLoadRole role, bool realmHoldsMembers) =>
            role == EphemeralLoadRole.Member && realmHoldsMembers;

        /// <summary>What the unload drain does with a primary that has been claimed for destruction.</summary>
        public enum PrimaryUnloadDecision
        {
            /// <summary>Unload it and unregister the instance: it is the last landblock of the group.</summary>
            Unload,

            /// <summary>Refuse and keep it active: a player is on another landblock of the group.</summary>
            HoldOccupied,

            /// <summary>Refuse for now and queue the rest of the group for unload: they go first, the primary last.</summary>
            DrainGroupFirst,
        }

        /// <summary>
        /// The primary of a group unloads LAST. While a player stands on any other landblock loaded under the instance
        /// it is held; while other (empty) landblocks remain loaded they are queued and the primary waits for them, so
        /// the instance id stays registered - and so cannot be reallocated - until nothing is loaded under it. Only the
        /// primary consults this; any other role always unloads as before.
        /// </summary>
        public static PrimaryUnloadDecision DecidePrimaryUnload(int otherLoadedCount, int otherOccupiedCount)
        {
            if (otherOccupiedCount > 0)
                return PrimaryUnloadDecision.HoldOccupied;

            if (otherLoadedCount > 0)
                return PrimaryUnloadDecision.DrainGroupFirst;

            return PrimaryUnloadDecision.Unload;
        }

        /// <summary>
        /// True when a destination cell may be entered under a live instance with this footprint: any cell of any
        /// footprint landblock. A null footprint (the instance is not live) accepts nothing.
        /// </summary>
        public static bool RoutingAccepts(EphemeralFootprint registeredFootprint, uint cell) =>
            registeredFootprint != null && registeredFootprint.Contains((ushort)(cell >> 16));

        /// <summary>
        /// The key LandblockGroup clustering partitions on: an ephemeral instance's own id, and 0 for every persistent
        /// instance. Landblocks only cluster into one tick group when their keys match, so an outdoor ephemeral copy
        /// never merges into the tick group of the real world it duplicates. Persistent realms keep sharing key 0 and
        /// cluster exactly as before; that is deliberate (see the P0 PR), not an oversight.
        /// </summary>
        public static uint TickGroupKey(uint instance) => IsEphemeralInstance(instance) ? instance : 0u;

        /// <summary>True when landblocks under these two instances may share a tick group.</summary>
        public static bool MayShareTickGroup(uint instanceA, uint instanceB) => TickGroupKey(instanceA) == TickGroupKey(instanceB);
    }
}
