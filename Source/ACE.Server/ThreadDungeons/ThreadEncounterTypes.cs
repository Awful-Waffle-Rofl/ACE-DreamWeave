using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Landblock = ACE.Server.Entity.Landblock;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One kind of optional run encounter (Docs/Threads/ENCOUNTERS.md). Registered once per <see cref="Kind"/> in
    /// <see cref="ThreadEncounterTypes"/>; every encounters.json row whose kind matches is placed through it.
    ///
    /// Threading. <see cref="RejectPoint"/> may run on any thread and must be pure. <see cref="Place"/> runs on the
    /// action queue of the landblock its point is on (<see cref="ThreadEncounterPlaceRequest.Landblock"/>), once per
    /// pick; every object it creates must be created and entered there, and must be on that landblock.
    /// <see cref="OnRunEnded"/> runs on the thread of whoever called EndRun (usually the WORLD thread; a landblock thread
    /// for an end raised from one, or the Place thread for a run that ended mid-Place), exactly once per instance whose
    /// Place returned true. It must only release in-memory state: no EnterWorld, no Destroy, no landblock-queue work
    /// there. The copy is torn down with its objects by EndRun's own destruction path.
    ///
    /// Never subclass WorldObject for an encounter: track the objects you create with
    /// <see cref="ThreadEncounterInstance.Track"/>, which sets their in-memory back-reference.
    ///
    /// The one exception (owner ruling 2026-11-16): the Wandering Peddler (<see cref="PeddlerEncounterType"/>), whose NPC is
    /// a <see cref="ThreadPeddlerVendor"/>, a Vendor subclass, because only a Vendor opens the client's native vendor window.
    /// Clear only (2026-11-19): its Place records a plan and creates nothing; the NPC is created at the clear by
    /// ThreadPeddlerShop.Present and tracked under the same instance, so it is still torn down with the run's copy. Do not
    /// read the exception as a precedent: a new type that wants its own WorldObject class needs its own ruling.
    /// </summary>
    public interface IThreadEncounterType
    {
        string Kind { get; }

        /// <summary>A type-specific point filter on top of the row's placement block. Null accepts; a short reason rejects. Pure.</summary>
        string RejectPoint(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, bool isOutdoor) => null;

        /// <summary>
        /// The same filter, also given every curated point of the run (the picker's candidates), for a type whose fit
        /// depends on the point's neighbours. The default is <see cref="RejectPoint"/>. This is what the encounter pass and
        /// /dd encounter place call. Pure.
        /// </summary>
        string RejectPointAmong(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, bool isOutdoor)
            => RejectPoint(def, candidate, isOutdoor);

        /// <summary>
        /// The same filter, also given the run, for a type whose Place refuses on something only the run knows (its
        /// dungeon's walk graph: Defense and Gamble's portal walk-ratio rule). This is what the encounter pass and /dd
        /// encounter place call, so the picker never spends a slot on a point Place is certain to refuse. The default is
        /// <see cref="RejectPointAmong"/>. Pure over the pass's walk graph seam.
        /// </summary>
        string RejectPointInRun(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, ThreadDungeonRun run)
            => RejectPointAmong(def, candidate, runPoints, run?.Dungeon?.IsOutdoor == true);

        /// <summary>
        /// A per-RUN filter on the type row, consulted before any point is looked at (ThreadEncounterPicker.TypeUnavailable,
        /// through ThreadEncounterPass.BuildRequest): null allows the type in this run, a short reason drops it from the
        /// run's draw and is reported with the other reasons. Default: every run is allowed. Pure.
        /// </summary>
        string RejectRun(ThreadEncounterTypeDef def, ThreadDungeonRun run) => null;

        /// <summary>
        /// Places the encounter. True when it is now in the world (the framework then records the instance and owes it
        /// <see cref="OnRunEnded"/>); false with <paramref name="failure"/> set when nothing was left behind. Must not throw
        /// into the caller on purpose; a throw is caught, logged, and treated as false (the type must then have left
        /// nothing in the world, or have tracked what it did leave so the framework can clear the back-references).
        /// </summary>
        bool Place(ThreadEncounterPlaceRequest request, out string failure);

        /// <summary>
        /// The run is over: release whatever in-memory state the instance holds. Runs on the EndRun caller's thread (usually
        /// the world thread): no EnterWorld, no Destroy, no landblock-queue work. The copy is destroyed with its objects.
        /// </summary>
        void OnRunEnded(ThreadEncounterInstance instance, string why);
    }

    /// <summary>
    /// Optional: an encounter type whose tracked objects react to a player's Use. WorldObject.OnActivate routes a use of
    /// any object carrying a P_ThreadEncounter back-reference here (after CheckUseRequirements, the puzzle-gate rule),
    /// through <see cref="ThreadEncounterPass.OnActivated"/>. Runs on the thread OnActivate runs on (the user's
    /// landblock); world-object work belongs on the used object's own landblock queue.
    /// </summary>
    public interface IThreadEncounterUsable
    {
        void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player);
    }

    /// <summary>
    /// Optional: an encounter type that hears the death of a creature it tracks. ThreadDungeonManager.OnRunCreatureDied
    /// routes the death of any run creature carrying a P_ThreadEncounter back-reference here (through
    /// <see cref="ThreadEncounterPass.OnTrackedCreatureDied"/>) before the bonus-kill route. Runs on the dying creature's
    /// landblock thread.
    /// </summary>
    public interface IThreadEncounterDeathAware
    {
        void OnTrackedCreatureDied(ThreadEncounterInstance instance, Creature creature);
    }

    /// <summary>Thread-safe registry of encounter implementations by kind (the IPinnacleEncounter pattern, Knots/IPinnacleEncounter.cs).</summary>
    public static class ThreadEncounterTypes
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, IThreadEncounterType> ByKind = new Dictionary<string, IThreadEncounterType>(StringComparer.Ordinal);

        /// <summary>Registers (or replaces) the implementation for <c>impl.Kind</c>. A null impl or blank kind is ignored.</summary>
        public static void Register(IThreadEncounterType impl)
        {
            if (impl == null || string.IsNullOrWhiteSpace(impl.Kind))
                return;

            lock (Gate)
                ByKind[impl.Kind] = impl;
        }

        public static bool TryGet(string kind, out IThreadEncounterType impl)
        {
            impl = null;

            if (kind == null)
                return false;

            lock (Gate)
                return ByKind.TryGetValue(kind, out impl);
        }

        public static bool IsRegistered(string kind) => TryGet(kind, out _);

        /// <summary>The registered kinds, sorted.</summary>
        public static IReadOnlyList<string> Kinds
        {
            get
            {
                lock (Gate)
                    return ByKind.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            }
        }

        /// <summary>Test seam: removes one registration.</summary>
        internal static void Unregister(string kind)
        {
            if (kind == null)
                return;

            lock (Gate)
                ByKind.Remove(kind);
        }

        /// <summary>Test seam: removes every registration.</summary>
        internal static void Clear()
        {
            lock (Gate)
                ByKind.Clear();
        }
    }

    /// <summary>
    /// What the run keeps for its encounters once its populate chain has finished (stamped by ThreadDungeonSpawner
    /// immediately before MarkPopulated, write-once). Everything here was a local of the populate chain; an
    /// encounter that adds creatures shapes them exactly as the run's own were shaped.
    /// </summary>
    public sealed class ThreadEncounterRunContext
    {
        public ThreadEncounterRunContext(DungeonSpawnPlan plan, uint observedMaxNonBoss, OutdoorRunPerimeter outdoorPerimeter,
            IReadOnlyDictionary<ushort, Landblock> landblocks, Landblock primary)
        {
            Plan = plan;
            ObservedMaxNonBoss = observedMaxNonBoss;
            OutdoorPerimeter = outdoorPerimeter;
            Landblocks = landblocks ?? new Dictionary<ushort, Landblock>();
            Primary = primary;
        }

        /// <summary>The run's population plan (ThreadDungeonSpawner.ShapeCreature's input). Null only in tests.</summary>
        public DungeonSpawnPlan Plan { get; }

        /// <summary>The largest Health.MaxValue any placed non-boss creature entered the world with (invariant I2's input).</summary>
        public uint ObservedMaxNonBoss { get; }

        /// <summary>The outdoor perimeter every outdoor run creature is bound to; null for a dungeon run.</summary>
        public OutdoorRunPerimeter OutdoorPerimeter { get; }

        /// <summary>The run copy's loaded landblocks by landblock id (ThreadDungeonManager.LoadedRunLandblocks at populate).</summary>
        public IReadOnlyDictionary<ushort, Landblock> Landblocks { get; }

        /// <summary>The run copy's primary landblock.</summary>
        public Landblock Primary { get; }
    }

    /// <summary>
    /// One placed (or being-placed) encounter. Holds an in-memory back-reference to its run, never persisted. The
    /// objects it tracks and its placed/ended state live under the run's stateLock (ThreadDungeonRun_Encounters.cs).
    /// </summary>
    public sealed class ThreadEncounterInstance
    {
        internal ThreadEncounterInstance(int id, ThreadDungeonRun run, ThreadEncounterPick pick, IThreadEncounterType type)
        {
            Id = id;
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Pick = pick;
            Type = type;
        }

        /// <summary>Per-run id, 1-based, in the order the instances were created.</summary>
        public int Id { get; }

        public ThreadDungeonRun Run { get; }

        public ThreadEncounterPick Pick { get; }

        public IThreadEncounterType Type { get; }

        public string Kind => Pick.Def?.Kind;

        /// <summary>Type-owned state. The framework never reads it.</summary>
        public object State { get; set; }

        /// <summary>Set (under the run's stateLock) once the instance has been ended or its Place failed: it tracks nothing after that.</summary>
        internal bool Ended;

        /// <summary>Tracked objects; read and written only through the run's locked methods.</summary>
        internal readonly List<WorldObject> TrackedObjects = new List<WorldObject>();

        /// <summary>Sets <paramref name="wo"/>'s back-reference to this instance and records it. Call before EnterWorld.</summary>
        public void Track(WorldObject wo)
        {
            // Every encounter object (monsters excluded inside) is made selectable and radar-visible here, before EnterWorld.
            ThreadEncounterObjectVisibility.Apply(wo);
            Run.TrackEncounterObject(this, wo);
        }

        /// <summary>Like <see cref="Track"/> for pure scenery (the portal rings): tracked and released with the instance, but not made selectable or put on the radar.</summary>
        public void TrackDecor(WorldObject wo) => Run.TrackEncounterObject(this, wo);

        /// <summary>A snapshot of the tracked objects.</summary>
        public IReadOnlyList<WorldObject> Objects => Run.EncounterObjects(this);

        public override string ToString() => $"encounter #{Id} {Pick.Def?.Id}/{Kind} at {Pick.Point}";
    }

    /// <summary>What <see cref="IThreadEncounterType.Place"/> is handed.</summary>
    public sealed class ThreadEncounterPlaceRequest
    {
        /// <summary>Default radius for <see cref="NearbyPoints"/>, metres.</summary>
        public const float DefaultNearbyRadius = 40f;

        internal ThreadEncounterPlaceRequest(ThreadDungeonRun run, Landblock landblock, ThreadEncounterInstance instance, ThreadEncounterRunContext context)
        {
            Run = run;
            Landblock = landblock;
            Instance = instance;
            Context = context;
        }

        public ThreadDungeonRun Run { get; }

        /// <summary>The landblock whose action queue this call runs on: the point's own. Null only in tests.</summary>
        public Landblock Landblock { get; }

        public ThreadEncounterInstance Instance { get; }

        public ThreadEncounterRunContext Context { get; }

        public ThreadEncounterTypeDef Def => Instance.Pick.Def;

        public ThreadEncounterCandidate Point => Instance.Pick.Point;

        public int Seed => Instance.Pick.Seed;

        /// <summary>A position in the run copy for a curated point.</summary>
        public Position PositionOf(DungeonSpawnPointDef p)
            => new Position(p.Cell, p.X, p.Y, p.Z, p.QX, p.QY, p.QZ, p.QW, Run.Instance);

        /// <summary>
        /// The run's other curated points on the SAME landblock as the selected point, within <paramref name="radius"/>,
        /// nearest first (the selected point excluded). Outdoor runs offer only open-ground points. Anything created at
        /// one of these is created on this call's landblock, which is the threading rule.
        /// </summary>
        public IReadOnlyList<DungeonSpawnPointDef> NearbyPoints(float radius = DefaultNearbyRadius)
            => ThreadEncounterPass.NearbyPoints(Run, Point, radius);

        /// <summary>
        /// The outdoor perimeter binding every outdoor run creature gets before EnterWorld (perimeter, HomeRadius,
        /// awareness clamp). A no-op for a dungeon run. Call before EnterWorld.
        /// </summary>
        public void BindToRunPerimeter(Creature creature, DungeonSpawnPointDef point)
            => ThreadDungeonSpawner.BindToRunPerimeter(creature, Context?.OutdoorPerimeter, point);

        /// <summary>
        /// The run's creature shaping (ThreadDungeonSpawner.ShapeCreature) with this run's plan and observed max health.
        /// Nothing here touches Location, the landblock or EnterWorld; the caller's EnterWorld must follow it.
        /// </summary>
        internal void ShapeCreature(Creature creature, DungeonSpawnPlanEntry entry, CreatureShapeOptions options)
            => ThreadDungeonSpawner.ShapeCreature(creature, Context?.Plan, entry, Context?.ObservedMaxNonBoss ?? 0u, options);
    }
}
