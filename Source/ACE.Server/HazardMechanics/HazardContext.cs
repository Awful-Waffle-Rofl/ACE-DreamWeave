using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.PuzzleGates;
using ACE.Server.WorldObjects;

using log4net;

using Position = ACE.Entity.Position;

namespace ACE.Server.HazardMechanics
{
    /// <summary>What a moving hazard's height follows. Sub-legs only on a TERRAIN region; the Ground leg END applies in every mode (the hover rule).</summary>
    public enum HazardFollow
    {
        /// <summary>A straight line between the two ends; never split.</summary>
        Straight,

        /// <summary>The region's hazard height (hazard plane, or ground + offset): a flying wisp.</summary>
        HazardHeight,

        /// <summary>The ground plus <see cref="HazardMoveOptions.GroundOffset"/>: a rolling ball. THE HOVER RULE (HazardGround.FollowZ): the leg end is forced to GroundZ + GroundOffset on plane AND terrain regions.</summary>
        Ground,
    }

    /// <summary>How one owned object is destroyed (HazardContext.RouteDestroy).</summary>
    public enum HazardDestroyRoute
    {
        /// <summary>Nothing to destroy (no object, or already destroyed).</summary>
        Skip,

        /// <summary>Destroy it now, on the calling thread.</summary>
        Now,

        /// <summary>Queue the destroy on the object's own landblock (its tick thread).</summary>
        EnqueueOnItsLandblock,
    }

    /// <summary>Where a leg-end snap goes (HazardContext.RouteSnap).</summary>
    public enum HazardSnapRoute
    {
        /// <summary>Single-landblock region: Snap on the run's landblock at the given point.</summary>
        RunLandblock,

        /// <summary>Spanning region: Snap on the owning landblock (the object's own) at the local point.</summary>
        OwnerLocal,

        /// <summary>Spanning region, target off the object's landblock or on none usable: zero velocity, move nothing.</summary>
        StopInPlace,
    }

    /// <summary>Optional knobs for <see cref="HazardContext.MoveLeg"/>. Null means all defaults.</summary>
    public sealed class HazardMoveOptions
    {
        public static readonly HazardMoveOptions Default = new HazardMoveOptions();

        /// <summary>
        /// What the leg's heights follow. Default HazardHeight. A plane-region leg is one straight piece; a Ground
        /// leg's END is GroundZ + GroundOffset in every ground mode (HazardGround.FollowZ).
        /// </summary>
        public HazardFollow Follow { get; set; } = HazardFollow.HazardHeight;

        /// <summary>Height above the ground for <see cref="HazardFollow.Ground"/> (a ball's hover; HazardDefaults.BallHover). Honoured on plane and terrain regions.</summary>
        public float GroundOffset { get; set; }

        /// <summary>A fixed spin (radians/s) for the whole leg. Overrides <see cref="RollRadius"/>.</summary>
        public Vector3? Omega { get; set; }

        /// <summary>&gt; 0: spin like a ball of this radius rolling along the ground (omega = cross(up, v) / r).</summary>
        public float RollRadius { get; set; }

        /// <summary>Force this many sub-legs (unison groups set it; see <see cref="HazardContext.MoveLegs"/>). Null: computed.</summary>
        public int? SubLegs { get; set; }

        /// <summary>
        /// True (default): the framework calls StopLeg at the leg end (snap + zero velocity). False: the hazard is
        /// left flying - the mechanic must Destroy or MoveLeg it at the end (a ball leaving the region).
        /// </summary>
        public bool AutoStop { get; set; } = true;

        /// <summary>
        /// Called by the driver on the tick the leg is found finished, BEFORE the leg-end snap (or before the path is
        /// cleared when AutoStop is off), with the handle still carrying its finished Path. For measurement (the
        /// movement gate logs here) or chaining: a MoveLeg (or Destroy) of the same handle from here replaces the
        /// finished path, and the framework then skips its own snap/clear. Must not throw (a throw ends the run as
        /// Aborted).
        /// </summary>
        public Action<HazardHandle, HazardPath> OnLegEnd { get; set; }
    }

    /// <summary>
    /// Everything a mechanic may see and do, and its ONLY route to the world (shared invariants 1-3, 9). One
    /// context per run, created with it; every method is LANDBLOCK THREAD ONLY (the driver, Start/Tick/OnStop and
    /// Schedule callbacks all run there).
    ///
    /// Owned objects: every <see cref="Spawn"/> is registered here BEFORE EnterWorld, offered to the host's
    /// Adopt, and destroyed by the framework when the run ends - mechanics never clean up after themselves.
    /// </summary>
    public sealed class HazardContext
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly List<HazardHandle> owned = new List<HazardHandle>();
        private readonly List<(double Due, long Seq, Action Step)> schedule = new List<(double, long, Action)>();
        private readonly Dictionary<(int Hazard, uint Player), double> lastHits = new Dictionary<(int, uint), double>();

        private long scheduleSeq;
        private long tickNumber;
        private int nextHandleId;

        /// <summary>The turned-host lift (Spawn) has been logged for this run.</summary>
        private bool turnedLiftLogged;

        /// <summary>
        /// How Teardown destroys an owned object; returns true only when it actually destroyed something (the
        /// teardown count). The default is <see cref="DestroyOwned"/> (exactly <see cref="DestroyHandle"/> for a
        /// single-landblock region); tests replace it (they have no world objects).
        /// </summary>
        internal Func<HazardHandle, bool> DestroyForTeardown { get; set; }

        /// <summary>Destroys the handle's live WorldObject. False when there was nothing to destroy (null or already destroyed).</summary>
        internal static bool DestroyHandle(HazardHandle h)
        {
            if (h?.Object == null || h.Object.IsDestroyed)
                return false;

            h.Object.Destroy();
            return true;
        }

        /// <summary>
        /// The default teardown destroy. A single-landblock region: <see cref="DestroyHandle"/>. A spanning region:
        /// an object whose landblock is not usable from this thread right now (see HazardSpan) is destroyed by an
        /// action queued on ITS landblock instead, so it never leaks and is never touched from another tick thread.
        /// </summary>
        private bool DestroyOwned(HazardHandle h)
        {
            if (h?.Object == null || h.Object.IsDestroyed)
                return false;

            if (!Spanning)
                return DestroyHandle(h);

            return DestroyOnItsThread(h.Object, onAnchorThread: true) != HazardDestroyRoute.Skip;
        }

        /// <summary>
        /// Destroys <paramref name="wo"/> the way <see cref="RouteDestroy"/> says: now, or by an action queued on its
        /// own landblock. Only reached for a spanning region. Returns the route taken.
        /// </summary>
        private HazardDestroyRoute DestroyOnItsThread(WorldObject wo, bool onAnchorThread)
        {
            var lb = wo?.CurrentLandblock;
            var s = Span;
            var route = RouteDestroy(Spanning, wo != null && !wo.IsDestroyed, lb != null, lb != null && s != null && s.Usable(lb), onAnchorThread);

            switch (route)
            {
                case HazardDestroyRoute.Now:
                    wo.Destroy();
                    break;

                case HazardDestroyRoute.EnqueueOnItsLandblock:
                    lb.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
                    {
                        if (!wo.IsDestroyed)
                            wo.Destroy();
                    }));
                    break;
            }

            return route;
        }

        /// <summary>
        /// PURE: how one owned object is destroyed.
        ///   - Not alive: <see cref="HazardDestroyRoute.Skip"/>.
        ///   - A single-landblock region: <see cref="HazardDestroyRoute.Now"/> (today's behaviour; the caller is on the
        ///     run's landblock thread).
        ///   - A spanning region, on the anchor's thread (a tick or the queued Teardown): Now when the object is in no
        ///     landblock or its landblock is usable from here (HazardSpan.Usable), else queued on ITS landblock.
        ///   - A spanning region, NOT on the anchor's thread (the anchor is dead, so its queued Teardown can never run:
        ///     <see cref="RouteOrphanedTeardown"/>): queued on its landblock - the only thread that may touch it - and
        ///     Now only when it is in no landblock at all. An object still on the dead anchor was already destroyed by
        ///     the anchor's unload (it is not alive), and a queue that never runs again belongs to a landblock whose own
        ///     unload destroyed it too.
        /// </summary>
        internal static HazardDestroyRoute RouteDestroy(bool spanning, bool alive, bool hasLandblock, bool landblockUsable, bool onAnchorThread)
        {
            if (!alive)
                return HazardDestroyRoute.Skip;

            if (!spanning)
                return HazardDestroyRoute.Now;

            if (!hasLandblock)
                return HazardDestroyRoute.Now;

            if (onAnchorThread && landblockUsable)
                return HazardDestroyRoute.Now;

            return HazardDestroyRoute.EnqueueOnItsLandblock;
        }

        private int teardownClaimed;

        /// <summary>True once the queued <see cref="Teardown"/> or <see cref="RouteOrphanedTeardown"/> has run (whichever came first).</summary>
        internal bool TeardownClaimed => Volatile.Read(ref teardownClaimed) != 0;

        /// <summary>
        /// The teardown of a SPANNING run whose anchor landblock is dead, so the Teardown queued on it will never run
        /// (Landblock.Unload clears the queue and destroys only its own objects; a hazard on a still-loaded neighbour
        /// would otherwise live forever, TimeToRot -1). Called OFF the anchor's thread (HazardManager: from Stop, or
        /// the reaper's pending-teardown sweep); safe because a dead anchor never ticks this run again, and the claim
        /// makes it and Teardown mutually exclusive. Routes a destroy for every owned object onto its own landblock
        /// (<see cref="RouteDestroy"/>, onAnchorThread false), drops the schedule, and does NOT call the mechanic's
        /// OnStop (that is landblock-thread code). Returns how many destroys were routed (now or queued); 0 when the
        /// teardown was already claimed.
        /// </summary>
        internal int RouteOrphanedTeardown()
        {
            if (Interlocked.Exchange(ref teardownClaimed, 1) != 0)
                return 0;

            // radar is not restored here: fog refuses spanning regions (HazardPanels.RegionRefusal), so adding spanning support must add it
            var routed = 0;

            foreach (var h in owned.ToArray())
            {
                h.Path = null;

                try
                {
                    if (DestroyOnItsThread(h.Object, onAnchorThread: false) != HazardDestroyRoute.Skip)
                        routed++;
                }
                catch (Exception ex)
                {
                    log.Error($"{Run.LogPrefix} routing the orphaned destroy of hazard {h.Id} ({h.Tag}) threw", ex);
                }
            }

            owned.Clear();
            schedule.Clear();
            lastHits.Clear();

            LastTeardownDestroyed = routed;
            Log($"teardown (anchor landblock gone): {routed} object(s) routed to their own landblocks");
            return routed;
        }

        /// <summary>
        /// Motion trace: when set (non-null), every network message the framework sends for a moving hazard - each
        /// VectorUpdate and each leg-end snap - is logged at Info with this tag appended to the run prefix. The
        /// movement gate sets it per case ("move-case=2 leg=1"); args "trace=1" sets "trace" for any run.
        /// </summary>
        public string TraceTag { get; set; }
        private Random rng;
        private Func<float, float, float> terrain;
        private bool terrainResolved;
        private HazardSpan span;

        internal HazardContext(HazardRun run)
        {
            Run = run;
            DestroyForTeardown = DestroyOwned;

            if (run.Args.GetBool("trace", false))
                TraceTag = "trace";
        }

        public HazardRun Run { get; }

        public HazardRegion Region => Run.Region;

        /// <summary>The merged numbers (caller args over the settings string). Read every number through its typed accessor with a default and a clamp.</summary>
        public HazardArgs Args => Run.Args;

        /// <summary>The resolved pattern, or null.</summary>
        public string Pattern => Run.Pattern;

        /// <summary>The run's seeded RNG (seed = Run.Seed): pass it to every pure *Rules/*Patterns decision. Reproducible per seed.</summary>
        public Random Rng => rng ??= new Random(Run.Seed);

        /// <summary>Run-relative seconds since Start, fixed for the whole tick. The only clock a mechanic uses.</summary>
        public double Now { get; private set; }

        /// <summary>
        /// The damage element: args "damage_type" (fire|acid|cold|electric|bludgeon, case-insensitive), default Fire.
        /// An unrecognised word reads as Fire here; the mechanics' Validate refuses it before a run starts.
        /// </summary>
        public DamageType DamageType => ParseDamageType(Args.GetString(HazardDefaults.DamageTypeKey));

        /// <summary>The verb in the default hit line: args "hit_verb", default "sears".</summary>
        public string HitVerb
        {
            get
            {
                var verb = Args.GetString(HazardDefaults.HitVerbKey);
                return string.IsNullOrWhiteSpace(verb) ? HazardDefaults.HitVerb : verb.Trim();
            }
        }

        /// <summary>The run's live count (HazardRun.LiveCount); null = the mechanic uses its args.</summary>
        public int? LiveCount => Run.LiveCount;

        /// <summary>
        /// Drains the run's control inbox (HazardRun.Control) in arrival order. Landblock thread; call it first in Tick.
        /// Empty when nothing is waiting.
        /// </summary>
        public IReadOnlyList<HazardControl> TakeControls() => Run.DrainControls();

        /// <summary>The five accepted words of args "damage_type" -> the element; null for anything else. Pure.</summary>
        internal static DamageType? TryParseDamageType(string word)
        {
            switch (word?.Trim().ToLowerInvariant())
            {
                case "fire": return DamageType.Fire;
                case "acid": return DamageType.Acid;
                case "cold": return DamageType.Cold;
                case "electric": return DamageType.Electric;
                case "bludgeon": return DamageType.Bludgeon;
                default: return null;
            }
        }

        private static DamageType ParseDamageType(string word) => TryParseDamageType(word) ?? DamageType.Fire;

        /// <summary>
        /// The refusal line for a given args "damage_type" that is not one of fire|acid|cold|electric|bludgeon, else null.
        /// THE one copy: every hazard mechanic's Validate (wisps, balls, rain) returns it, so the line is identical everywhere
        /// (HAZARDS-PLAN fan-out invariant).
        /// </summary>
        internal static string BadDamageType(HazardArgs args)
        {
            var given = args?.GetString(HazardDefaults.DamageTypeKey);

            if (given == null || TryParseDamageType(given) != null)
                return null;

            return $"damage_type='{given}' is not one of fire|acid|cold|electric|bludgeon";
        }

        /// <summary>Damage per hit as a fraction of MaxHealth: args "pct", default 0.60, clamped 0..5.</summary>
        public double Pct => Args.GetDouble(HazardDefaults.PctKey, HazardDefaults.DamagePct, HazardDefaults.MinPct, HazardDefaults.MaxPct);

        /// <summary>Player radius for the 2D hit test: args "player_radius", default 0.5, clamped 0..3.</summary>
        public double PlayerRadius => Args.GetDouble(HazardDefaults.PlayerRadiusKey, HazardDefaults.PlayerRadius, HazardDefaults.MinPlayerRadius, HazardDefaults.MaxPlayerRadius);

        /// <summary>
        /// How far effect hosts (pillars, beams, markers) are kept inside the region edge so their emitters are not
        /// cut off by walls: args "edge_inset", default 2.0 m, clamped 0..10. Applied by
        /// <see cref="SpawnTimedEffect"/>; mechanics pass it to the pure layout helpers
        /// (HazardRegion.ClampInside / TrySamplePoint).
        /// </summary>
        public float EdgeInset => (float)Args.GetDouble(HazardDefaults.EdgeInsetKey, HazardDefaults.EdgeInset, HazardDefaults.MinEdgeInset, HazardDefaults.MaxEdgeInset);

        /// <summary>Every live handle this run owns (destroyed ones are pruned each tick).</summary>
        public IReadOnlyList<HazardHandle> Handles => owned;

        internal int OwnedCount => owned.Count;

        private Landblock Landblock => Run.Landblock;

        /// <summary>The run's region reaches into neighbouring landblocks (HazardRegion.SpansLandblocks).</summary>
        private bool Spanning => Region != null && Region.SpansLandblocks;

        /// <summary>
        /// Where the run's anchor-frame points live (HazardSpan): the identity over the run's own landblock for a
        /// single-landblock region, the 1..4 covered landblocks for a spanning one. Null only for a run with no
        /// landblock (registry tests) unless a test sets it.
        /// </summary>
        internal HazardSpan Span
        {
            get
            {
                if (span == null && Landblock != null)
                    span = HazardSpan.For(Region, Landblock);

                return span;
            }
            set => span = value;
        }

        private Func<float, float, float> Terrain
        {
            get
            {
                if (!terrainResolved)
                {
                    terrain = Region.GroundMode != HazardGroundMode.Terrain ? null
                        : Spanning ? HazardGround.TerrainSampler(Span)
                        : HazardGround.TerrainSampler(Landblock);
                    terrainResolved = true;
                }

                return terrain;
            }
        }

        // ---- heights ----------------------------------------------------------------------------------------------

        /// <summary>Ground z at (x, y) for this region (plane, or live terrain).</summary>
        public float GroundZ(float x, float y) => Region.GroundZ(x, y, Terrain);

        /// <summary>Where a moving hazard flies at (x, y) for this region.</summary>
        public float HazardZ(float x, float y) => Region.HazardZ(x, y, Terrain);

        /// <summary>
        /// Would <paramref name="point"/> resolve to a cell of this landblock (for a spanning region: of the usable
        /// landblock that owns it)? raise false is the test a MOVING body's spawn must pass (formations use it to skip
        /// points inside solids); true is the test a pillar/beam origin passes (raised up to 1.5 m onto whatever is
        /// solid below).
        /// </summary>
        public bool HasCell(Vector3 point, bool raise = false)
            => Landblock != null && HazardGround.TryResolveCell(Span, point, raise, out _, out _, out _, out _);

        // ---- spawning ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Creates one object (shared invariant 1): instance-stamped position on a cell of THIS landblock - for a
        /// spanning region, of the usable covered landblock that owns the anchor-frame point (raised up
        /// to 1.5 m out of a solid), scale, VisualEffectScript, TimeToRot -1 and IsTransientSpawn, all before
        /// EnterWorld; registered with the run and offered to the host's Adopt BEFORE EnterWorld; destroyed again on a
        /// refused entry. Returns null (logged) on any failure, and always once the run has ended.
        /// </summary>
        public HazardHandle Spawn(HazardSpawnSpec spec)
        {
            if (spec == null || Run.IsEnded || Landblock == null)
                return null;

            var instance = Region.Instance;

            var raise = spec.AllowRaise ?? !spec.Moving;

            // resolvedLocal goes into the object's Position (with cell, on the owning landblock); resolved is the same
            // point in the anchor frame, what the handle keeps. Identical for a single-landblock region.
            if (!HazardGround.TryResolveCell(Span, spec.Position, raise, out var owner, out var cell, out var resolvedLocal, out var resolved))
            {
                if (Spanning && owner == null)
                    Log($"spawn wcid {spec.Wcid} at ({spec.Position.X:0.00}, {spec.Position.Y:0.00}, {spec.Position.Z:0.00}) refused: its landblock is outside the region's cover or not usable this tick");
                else
                    Log($"spawn wcid {spec.Wcid} at ({spec.Position.X:0.00}, {spec.Position.Y:0.00}, {spec.Position.Z:0.00}) refused: no cell of this landblock holds it"
                        + (raise ? " (even raised 1.5 m)" : " (a moving body is never raised; pick a point ctx.HasCell accepts)"));
                return null;
            }

            var wo = WorldObjectFactory.CreateNewWorldObject(spec.Wcid);

            if (wo == null)
            {
                Log($"spawn wcid {spec.Wcid} refused: no weenie in the world database");
                return null;
            }

            var rotation = spec.Rotation ?? PuzzleGateGenerator.YawQuaternion(spec.YawDeg);

            // THE TURNED-HOST LIFT (HazardGround.TurnedHostLift): a grounded host turned so its spheres fall below its
            // origin is raised until they sit where they would unturned. HazardGround.LiftedOrigin returns the point
            // unchanged, without the Setup lookup, for an unturned, ceiling (not grounded) or moving spawn.
            {
                var lifted = HazardGround.LiftedOrigin(spec.Grounded, spec.Moving,
                    () => HazardGround.SphereCentres(wo.SetupTableId, (wo.ObjScale ?? 1.0f) * (float.IsFinite(spec.Scale) && spec.Scale > 0f ? spec.Scale : 1f)),
                    rotation, resolved);
                var lift = lifted.Z - resolved.Z;

                if (lifted != resolved)
                {
                    if (HazardGround.TryResolveCell(Span, lifted, false, out var liftedOwner, out var liftedCell, out var liftedLocal, out var liftedAnchor))
                    {
                        owner = liftedOwner;
                        cell = liftedCell;
                        resolvedLocal = liftedLocal;
                        resolved = liftedAnchor;

                        if (!turnedLiftLogged)
                        {
                            turnedLiftLogged = true;
                            Log(FormattableString.Invariant($"spawn wcid {spec.Wcid} ({spec.Tag}) is a turned floor host: raised {lift:0.###} m (setup 0x{wo.SetupTableId:X8}) so its collision spheres stay above the floor; noted once per run"));
                        }
                    }
                    else
                    {
                        Log(FormattableString.Invariant($"spawn wcid {spec.Wcid} ({spec.Tag}): the turned-host lift of {lift:0.###} m found no cell; spawning unlifted"));
                    }
                }
            }

            wo.Location = new Position(cell, resolvedLocal, rotation) { Instance = instance };

            if (float.IsFinite(spec.Scale) && spec.Scale > 0f && Math.Abs(spec.Scale - 1.0f) > 1e-6f)
                wo.ObjScale = (wo.ObjScale ?? 1.0f) * spec.Scale;

            if (spec.Script != 0)
                wo.VisualEffectScript = spec.Script;

            // Unset, not an explicit 0, below the wire threshold (precedent Landblock.cs cloud translucency).
            if (spec.Translucency.HasValue && float.IsFinite(spec.Translucency.Value) && spec.Translucency.Value >= 0.001f)
                wo.Translucency = Math.Min(1f, spec.Translucency.Value);

            wo.TimeToRot = -1;
            wo.IsTransientSpawn = true;

            if (spec.Moving)
            {
                // PrepareMoving builds the PhysicsObj early (HazardMotion.PrepareMovingBody), so a throw here is a
                // refused spawn that must release that body as well as the object.
                try
                {
                    PrepareMoving(wo);
                }
                catch (Exception ex)
                {
                    log.Error($"{Run.LogPrefix} preparing a moving body threw for wcid {spec.Wcid}", ex);
                    Log($"spawn wcid {spec.Wcid} refused: its moving body could not be prepared");
                    ReleaseRefused(wo);
                    return null;
                }
            }

            var handle = new HazardHandle(++nextHandleId, wo, spec, resolved) { SpawnTick = tickNumber };

            owned.Add(handle); // registered BEFORE EnterWorld

            try
            {
                Run.Host.Adopt(Run, wo);
            }
            catch (Exception ex)
            {
                log.Error($"{Run.LogPrefix} host Adopt threw for wcid {spec.Wcid}", ex);
            }

            bool entered;

            try
            {
                entered = wo.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"{Run.LogPrefix} EnterWorld threw for wcid {spec.Wcid} at {wo.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                owned.Remove(handle);
                Log($"spawn wcid {spec.Wcid} refused: failed to enter the world at {wo.Location?.ToLOCString()}{ProbeReason(wo, rotation, resolved)}");

                ReleaseRefused(wo);
                return null;
            }

            // A moving hazard is a Missile (HazardMotion.PrepareMovingBody), and the server simulates an ACTIVE
            // missile every tick and destroys it after WorldObject.ProjectileTimeout. Parked inactive from birth.
            if (spec.Moving)
                HazardMotion.Park(wo);

            return handle;
        }

        /// <summary>
        /// Why a refused entry was refused, when it is the usual cause: the engine's placement probe (the first
        /// collision sphere's centre, turned with the body; for a Setup with no spheres the 0.1 m DummySphere on its
        /// front face, HazardGround.SphereCentres) is in no cell, so PhysicsObj.AdjustPosition finds none - the body
        /// faces into a floor or wall (HazardPanels.FaceClearance). Empty otherwise. Never throws.
        /// </summary>
        private string ProbeReason(WorldObject wo, System.Numerics.Quaternion rotation, Vector3 origin)
        {
            try
            {
                var centres = HazardGround.SphereCentres(wo.SetupTableId, wo.ObjScale ?? 1.0f);

                if (centres.Count == 0)
                    return "";

                var probe = origin + Vector3.Transform(centres[0], rotation);

                return HasCell(probe) ? "" : FormattableString.Invariant(
                    $" (its placement probe, the first collision sphere's centre, falls at ({probe.X:0.###}, {probe.Y:0.###}, {probe.Z:0.###}) in no cell: it faces into a floor or wall)");
            }
            catch (Exception)
            {
                return "";
            }
        }

        /// <summary>
        /// Cleans up an object whose spawn was refused: its PhysicsObj (which a moving body has BEFORE EnterWorld,
        /// HazardMotion.PrepareMovingBody, and which nothing else would release on a refused entry), then the object.
        /// </summary>
        private static void ReleaseRefused(WorldObject wo)
        {
            if (wo == null)
                return;

            // DestroyObject leaves its cells and the world and unregisters it (ObjectMaint.DestroyObject ->
            // ServerObjectManager.RemoveServerObject). WorldObject.PhysicsObj has a protected setter, so the reference
            // itself stays on the discarded object; nothing reads it again (WorldObject.Destroy does not).
            try
            {
                wo.PhysicsObj?.DestroyObject();
            }
            catch (Exception ex)
            {
                log.Error($"releasing the physics body of a refused spawn threw for wcid {wo.WeenieClassId}", ex);
            }

            if (!wo.IsDestroyed)
                wo.Destroy();
        }

        /// <summary>
        /// A short-lived effect: an Effect Host (1006952) carrying <paramref name="scriptId"/> at
        /// <paramref name="point"/>, destroyed after <paramref name="seconds"/>. Endless emitters cannot be stopped
        /// (VisualEffectManager), so destroying the host IS how a burst ends. The handle can be passed to
        /// HitPlayers as the damage source. Null on failure.
        ///
        /// WALL CLEARANCE: (x, y) is pulled at least <see cref="EdgeInset"/> inside the region edge
        /// (HazardRegion.ClampInside) so the emitter is not cut off by a wall; z is kept. The handle's Rest is the
        /// point actually used - a mechanic that hits players at the effect must use handle.Rest, not its own point.
        ///
        /// RAISE: a non-moving host is by default raised up to 1.5 m onto a cell when its point holds none
        /// (<see cref="HazardSpawnSpec.AllowRaise"/> null). <paramref name="allowRaise"/> false spawns it exactly where
        /// given or not at all, for a host that hangs from a ceiling: a raise would walk it up into whatever is above.
        ///
        /// GROUNDED: <paramref name="grounded"/> true for a host standing on the floor (every floor marker and floor beam).
        /// A grounded host turned by <paramref name="orientation"/> so its collision spheres fall below its origin is
        /// raised by HazardGround.TurnedHostLift (see <see cref="HazardSpawnSpec.Grounded"/>); unturned, nothing changes.
        /// </summary>
        public HazardHandle SpawnTimedEffect(Vector3 point, uint scriptId, Quaternion orientation, double seconds, float scale = 1f, string tag = null, bool? allowRaise = null, bool grounded = false)
        {
            var xy = Region.ClampInside(new Vector2(point.X, point.Y), EdgeInset);

            var handle = Spawn(new HazardSpawnSpec
            {
                Wcid = HazardDefaults.EffectHostWcid,
                Position = new Vector3(xy, point.Z),
                Rotation = orientation,
                Scale = scale,
                AllowRaise = allowRaise,
                Grounded = grounded,
                Script = scriptId,
                Tag = tag ?? "effect",
            });

            if (handle != null)
                Schedule(seconds, () => Destroy(handle));

            return handle;
        }

        /// <summary>Destroys one owned object now. Safe on an already-destroyed or foreign handle.</summary>
        public void Destroy(HazardHandle handle)
        {
            if (handle == null)
                return;

            owned.Remove(handle);
            handle.Path = null;

            var wo = handle.Object;

            if (wo != null && !wo.IsDestroyed)
            {
                if (Spanning)
                    DestroyOnItsThread(wo, onAnchorThread: true);
                else
                    wo.Destroy();
            }
        }

        private void PrepareMoving(WorldObject wo)
        {
            if (HazardMotion.PrepareMovingBody(wo))
                Log($"wcid {wo.WeenieClassId} carries a Static PhysicsState; stripped for a moving hazard (fix the weenie)");
        }

        // ---- moving -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Starts one leg: from where the hazard is NOW (its computed position) to <paramref name="end"/> over
        /// <paramref name="duration"/> seconds (shared invariant 2). Sets the server velocity/omega and broadcasts a
        /// VectorUpdate; on a terrain region with a following height the leg is split into ~4 m sub-legs and the
        /// driver sends each new velocity at its boundary (no snap); at the end the driver snaps and stops it
        /// (unless AutoStop is off). Returns false for a dead handle. A non-positive duration snaps straight to the end.
        /// </summary>
        /// <remarks>
        /// NEVER IN THE SPAWN TICK. The server simulates a non-creature only while it is animating or before its
        /// second physics update (WorldObject_Tick.cs UpdateObjectPhysics: IsAnimating || InitialUpdates &lt;= 1), and
        /// InitialUpdates only counts updates on which the object did NOT move (PhysicsObj.UpdateObjectInternal) - so
        /// an object given a velocity before it has settled could be flown by the server too, drifting its Location
        /// and even carrying it off the landblock. Two guards: a MoveLeg on a handle spawned in the current driver
        /// tick is DEFERRED to the next tick (same arguments, returns true), and every leg clears the server
        /// object's Active flag (HazardMotion.SendVelocity), which UpdateObjectPhysics checks first.
        ///
        /// A DEFERRED MoveLeg returns true while the handle's Path is still null until the next tick. Key follow-on
        /// timing off <see cref="HazardHandle.LegEndTime"/> once it is set, or schedule it at duration + one tick
        /// (hazard_mech_tick_ms) - never at exactly Now + duration.
        /// </remarks>
        public bool MoveLeg(HazardHandle handle, Vector3 end, double duration, HazardMoveOptions options = null)
        {
            if (handle == null || !handle.Alive || Run.IsEnded)
                return false;

            if (handle.SpawnTick == tickNumber)
            {
                Schedule(0.0, () => MoveLeg(handle, end, duration, options));
                return true;
            }

            options ??= HazardMoveOptions.Default;

            if (!double.IsFinite(duration) || duration <= 0.0)
            {
                end = LegEnd(Region, Terrain, end, options);
                handle.Path = null;
                handle.Rest = end;
                SnapObject(handle.Object, end, out _);
                return true;
            }

            var legs = PlanLeg(Region, Terrain, handle.PositionAt(Now), end, Now, duration, options);

            var path = new HazardPath(legs, options.Omega, options.RollRadius, options.AutoStop) { OnEnd = options.OnLegEnd };

            handle.Path = path;
            HazardMotion.SendVelocity(handle.Object, path.SentVelocity, path.SentOmega);
            TraceSent(handle, "leg-start", path.SentVelocity, path.SentOmega, path.SubLegCount);
            return true;
        }

        private void TraceSent(HazardHandle h, string what, Vector3 v, Vector3 omega, int subLegs)
        {
            if (TraceTag == null)
                return;

            log.Info(FormattableString.Invariant(
                $"{Run.LogPrefix} {TraceTag} phase=sent msg=VectorUpdate why={what} hazard={h.Id}({h.Tag}) t={Now:0.000} v=({v.X:0.00},{v.Y:0.00},{v.Z:0.00}) |v|={v.Length():0.00} w=({omega.X:0.00},{omega.Y:0.00},{omega.Z:0.00}) sublegs={subLegs}"));
        }

        /// <summary>
        /// UNISON: moves a group with one shared schedule - the same start time, the same duration and the SAME
        /// sub-leg count for every member (the largest any member needs), so all of them change velocity at the
        /// same instants and arrive together whatever terrain each crosses. Returns how many legs started.
        /// </summary>
        public int MoveLegs(IReadOnlyList<(HazardHandle Handle, Vector3 End)> group, double duration, HazardMoveOptions options = null)
        {
            if (group == null || group.Count == 0)
                return 0;

            options ??= HazardMoveOptions.Default;

            // One member spawned this tick defers the WHOLE group, so the group still starts on one tick (unison).
            foreach (var (h, _) in group)
            {
                if (h != null && h.SpawnTick == tickNumber)
                {
                    var deferredOptions = options;
                    Schedule(0.0, () => MoveLegs(group, duration, deferredOptions));
                    return group.Count;
                }
            }

            var n = options.SubLegs ?? 1;

            if (options.SubLegs == null && HazardGround.SplitsLegs(Region, options.Follow))
            {
                var members = new List<(Vector3, Vector3)>(group.Count);

                foreach (var (h, e) in group)
                    if (h != null && h.Alive)
                        members.Add((h.PositionAt(Now), LegEnd(Region, Terrain, e, options)));

                n = HazardMotion.UnisonSubLegCount(members);
            }

            var shared = new HazardMoveOptions
            {
                Follow = options.Follow,
                GroundOffset = options.GroundOffset,
                Omega = options.Omega,
                RollRadius = options.RollRadius,
                SubLegs = n,
                AutoStop = options.AutoStop,
                OnLegEnd = options.OnLegEnd,
            };

            var started = 0;

            foreach (var (h, e) in group)
                if (MoveLeg(h, e, duration, shared))
                    started++;

            return started;
        }

        /// <summary>
        /// Stops a hazard where it is now (its computed position) or at <paramref name="at"/>: zero velocity, a
        /// teleport-style reposition clients snap to, and a zero VectorUpdate.
        /// </summary>
        public void StopLeg(HazardHandle handle, Vector3? at = null)
        {
            if (handle == null || !handle.Alive)
                return;

            var where = at ?? handle.PositionAt(Now);

            handle.Path = null;
            handle.Rest = where;

            // A leg ending inside a solid (no cell; moving bodies are never raised) keeps the server object at its
            // last valid point while clients, who flew the leg, hold it hidden there. Expected over solids: Debug.
            var snapped = SnapObject(handle.Object, where, out var error);

            if (!snapped && log.IsDebugEnabled)
                log.Debug($"{Run.LogPrefix} stop of hazard {handle.Id} ({handle.Tag}) kept its last server point: {error}");

            if (TraceTag != null)
            {
                var loc = handle.Object.Location;

                log.Info(FormattableString.Invariant(
                    $"{Run.LogPrefix} {TraceTag} phase=sent msg={(snapped ? "UpdatePosition(teleport)+VectorUpdate(0)" : "VectorUpdate(0)-only")} hazard={handle.Id}({handle.Tag}) t={Now:0.000} target=({where.X:0.00},{where.Y:0.00},{where.Z:0.00}) server=({loc?.PositionX:0.00},{loc?.PositionY:0.00},{loc?.PositionZ:0.00}) cell=0x{loc?.Cell:X8}{(snapped ? "" : " nosnap=" + error)}"));
            }
        }

        /// <summary>
        /// The leg-end snap of one object to anchor-frame <paramref name="at"/> (HazardMotion.Snap). A
        /// single-landblock region: exactly Snap on the run's landblock. A spanning region: the point is routed to the
        /// landblock that owns it; Snap itself then refuses a target that is not on the object's OWN landblock, so a
        /// moving body is never server-relocated across a landblock edge - it keeps its last server point and the
        /// client, which flew the leg, holds it there. A point on no usable landblock takes the same path (velocity
        /// zeroed and broadcast, no reposition).
        /// </summary>
        private bool SnapObject(WorldObject wo, Vector3 at, out string error)
        {
            Landblock owner = null;
            var local = at;
            var located = Spanning && Span != null && Span.TryLocate(at, out owner, out local);
            var objectLandblock = wo?.CurrentLandblock;

            // the object's own landblock: the copy it is in, or (not yet added) the one its Location names
            var ownsIt = located && owner != null && (objectLandblock != null
                ? ReferenceEquals(owner, objectLandblock)
                : wo?.Location != null && wo.Location.LandblockId.Landblock == owner.Id.Landblock);

            switch (RouteSnap(Spanning, located, ownsIt))
            {
                case HazardSnapRoute.RunLandblock:
                    return HazardMotion.Snap(wo, Landblock, Region.Instance, at, out error);

                case HazardSnapRoute.OwnerLocal:
                    return HazardMotion.Snap(wo, owner, Region.Instance, local, out error);

                default:
                    // null landblock: Snap zeroes the velocity, parks and broadcasts, and moves nothing
                    HazardMotion.Snap(wo, null, Region.Instance, at, out _);
                    error = located
                        ? $"snap target ({at.X:0.00}, {at.Y:0.00}) is on another landblock than the hazard's own; a moving body is never relocated across a landblock edge"
                        : $"snap target ({at.X:0.00}, {at.Y:0.00}) is on no landblock this run can use this tick";
                    return false;
            }
        }

        /// <summary>
        /// PURE: where a leg-end snap goes. A single-landblock region: <see cref="HazardSnapRoute.RunLandblock"/>
        /// (exactly today's Snap). A spanning region: <see cref="HazardSnapRoute.OwnerLocal"/> only when the
        /// anchor-frame target is on a usable covered landblock (<paramref name="located"/>) AND that landblock is the
        /// object's own (<paramref name="ownerIsObjectsLandblock"/>); otherwise
        /// <see cref="HazardSnapRoute.StopInPlace"/> - velocity zeroed, nothing relocated - so a moving body never
        /// crosses a landblock edge server-side.
        /// </summary>
        internal static HazardSnapRoute RouteSnap(bool spanning, bool located, bool ownerIsObjectsLandblock)
        {
            if (!spanning)
                return HazardSnapRoute.RunLandblock;

            return located && ownerIsObjectsLandblock ? HazardSnapRoute.OwnerLocal : HazardSnapRoute.StopInPlace;
        }

        /// <summary>
        /// PURE: a leg's end as the framework will fly it. A <see cref="HazardFollow.Ground"/> leg ends at
        /// GroundZ(x, y) + GroundOffset in every ground mode (the hover rule, HazardGround.FollowZ) whatever z the
        /// caller passed; every other Follow keeps the caller's end.
        /// </summary>
        internal static Vector3 LegEnd(HazardRegion region, Func<float, float, float> terrain, Vector3 end, HazardMoveOptions options)
            => options != null && options.Follow == HazardFollow.Ground
                ? HazardGround.FollowPoint(region, HazardFollow.Ground, options.GroundOffset, end, terrain)
                : end;

        /// <summary>
        /// PURE: the sub-legs MoveLeg flies from <paramref name="start"/> towards <paramref name="end"/>. Split into
        /// ~4 m pieces only where the height can change (HazardGround.SplitsLegs - terrain); intermediate z from
        /// HazardGround.HeightAt; the end from <see cref="LegEnd"/>.
        /// </summary>
        internal static List<HazardLeg> PlanLeg(HazardRegion region, Func<float, float, float> terrain, Vector3 start, Vector3 end,
            double t0, double duration, HazardMoveOptions options)
        {
            options ??= HazardMoveOptions.Default;

            end = LegEnd(region, terrain, end, options);

            var heightAt = HazardGround.HeightAt(region, options.Follow, options.GroundOffset, terrain);
            var n = options.SubLegs ?? (HazardGround.SplitsLegs(region, options.Follow) ? HazardMotion.SubLegCount(start, end) : 1);

            return HazardMotion.Split(start, end, t0, duration, n, heightAt);
        }

        /// <summary>
        /// The point a body following <paramref name="follow"/> occupies at (x, y): spawn it here, and put its
        /// markers here, so spawn, leg ends and markers agree (the hover rule, HazardGround.FollowZ).
        /// </summary>
        public Vector3 PointFor(float x, float y, HazardFollow follow, float groundOffset = 0f)
            => HazardGround.FollowPoint(Region, follow, groundOffset, new Vector3(x, y, GroundZ(x, y)), Terrain);

        // ---- damage -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Damages every player in reach (shared invariant 3): ceil(pct x MaxHealth) through Player.TakeDamage, no
        /// resistance, players only, when the 2D distance from <paramref name="centre"/> is within
        /// <paramref name="radius"/> + player_radius AND the player's z is inside the region band above
        /// GroundZ(x, y), and at most once per hazard per player per <paramref name="windowSeconds"/>
        /// (+infinity: once per hazard lifetime - one hit per ball; a new pillar host per burst gives one hit per
        /// burst). <paramref name="pct"/> null = args "pct" (default 0.60); 0 = visual only. Use the hazard's
        /// COMPUTED position for <paramref name="centre"/> (handle.PositionAt(ctx.Now)). Returns how many were hit.
        /// A spanning region tests the players of every usable covered landblock, in the anchor frame; a region with
        /// HitsRequireInside also needs the player inside its 2D shape.
        /// </summary>
        public int HitPlayers(HazardHandle source, Vector3 centre, float radius, double windowSeconds = double.PositiveInfinity,
            double? pct = null, DamageType damageType = DamageType.Fire, string text = null)
        {
            var span = Span;

            if (source == null || source.Object == null || source.Object.IsDestroyed || span == null || Run.IsEnded)
                return 0;

            var fraction = pct ?? Pct;

            if (!(fraction > 0.0))
                return 0;

            var players = PlayerSource;
            var slots = span.Slots;
            var playerRadius = PlayerRadius;
            var hits = 0;

            // Every usable covered landblock (a single-landblock region: just the run's own), each player once: a
            // player is only taken from the list of the landblock their Location names.
            for (var si = 0; si < slots.Count; si++)
            {
                var slot = slots[si];
                var lb = slot.Current;

                if (!slot.Usable || lb == null)
                    continue;

                for (var i = 0; i < players.Count(lb); i++)
                {
                    if (!players.TryGet(lb, i, out var seen))
                        continue;

                    var player = seen.Player;

                    if (seen.IsDead || seen.IsDestroyed)
                        continue;

                    var loc = seen.Location;

                    if (loc == null || loc.Instance != Region.Instance || loc.LandblockId.Landblock != slot.Id)
                        continue;

                    var p = slot.IsAnchor ? loc.Pos : HazardSpan.ToAnchorFrame(span.AnchorId, slot.Id, loc.Pos);

                    if (!HazardHitRules.InReach(centre, p, radius, playerRadius))
                        continue;

                    if (!Region.InBand(p, Terrain))
                        continue;

                    // an arena region: "hit by the hazard" == "inside the arena" (the band is already checked)
                    if (Region.HitsRequireInside && !Region.Contains(p.X, p.Y))
                        continue;

                    var key = (source.Id, seen.Guid);
                    double? last = lastHits.TryGetValue(key, out var t) ? t : (double?)null;

                    if (!HazardHitRules.WindowOpen(last, Now, windowSeconds))
                        continue;

                    lastHits[key] = Now;

                    var taken = DamageForTest != null
                        ? DamageForTest(source, seen, fraction, damageType, text)
                        : DamagePlayer(source, player, fraction, damageType, text);

                    if (taken > 0)
                        hits++;
                }
            }

            return hits;
        }

        // ---- who is here (the players seam) ------------------------------------------------------------------------

        /// <summary>One player as the context sees them: the object (null only in tests), guid, Location and the two liveness flags.</summary>
        internal readonly struct PlayerSighting
        {
            public PlayerSighting(Player player, uint guid, Position location, bool isDead, bool isDestroyed)
            {
                Player = player;
                Guid = guid;
                Location = location;
                IsDead = isDead;
                IsDestroyed = isDestroyed;
            }

            public Player Player { get; }
            public uint Guid { get; }
            public Position Location { get; }
            public bool IsDead { get; }
            public bool IsDestroyed { get; }
        }

        /// <summary>
        /// The players on one landblock, by index (no enumerator, no copy). Live: Landblock.PlayersOnLandblockThread,
        /// LANDBLOCK THREAD ONLY - only ever asked for a usable landblock (HazardSpan), which ticks on this thread.
        /// </summary>
        internal interface IPlayerSource
        {
            int Count(Landblock landblock);

            /// <summary>False for an empty (null) entry.</summary>
            bool TryGet(Landblock landblock, int index, out PlayerSighting sighting);
        }

        private sealed class LivePlayerSource : IPlayerSource
        {
            public static readonly LivePlayerSource Instance = new LivePlayerSource();

            public int Count(Landblock landblock) => landblock.PlayersOnLandblockThread.Count;

            public bool TryGet(Landblock landblock, int index, out PlayerSighting sighting)
            {
                var player = landblock.PlayersOnLandblockThread[index];

                if (player == null)
                {
                    sighting = default;
                    return false;
                }

                sighting = new PlayerSighting(player, player.Guid.Full, player.Location, player.IsDead, player.IsDestroyed);
                return true;
            }
        }

        /// <summary>Test seam: where the players come from (fake landblocks). Live: the landblocks' own player lists.</summary>
        internal IPlayerSource PlayerSource { get; set; } = LivePlayerSource.Instance;

        /// <summary>Test seam: replaces the damage tail of <see cref="HitPlayers"/> (returns the damage "taken"). Always null live.</summary>
        internal Func<HazardHandle, PlayerSighting, double, DamageType, string, int> DamageForTest { get; set; }

        /// <summary>
        /// <paramref name="location"/> in the anchor frame, when it is on this run's instance and on a usable covered
        /// landblock (a single-landblock region: the run's own landblock, position unchanged). False otherwise.
        /// </summary>
        private bool TryAnchorPosition(Position location, out Vector3 position)
        {
            position = default;

            var span = Span;

            if (location == null || span == null || location.Instance != Region.Instance)
                return false;

            var slot = span.SlotOf(location.LandblockId.Landblock);

            if (slot == null || !slot.Usable || slot.Current == null)
                return false;

            position = slot.IsAnchor ? location.Pos : HazardSpan.ToAnchorFrame(span.AnchorId, slot.Id, location.Pos);
            return true;
        }

        /// <summary>
        /// Damages ONE player now (shared invariant 3, the per-player half of <see cref="HitPlayers"/>): ceil(pct x
        /// MaxHealth) through Player.TakeDamage, no resistance, with the source's name in the line and the host told. No
        /// reach test, no band test and no once-per-window ledger: the CALLER decides who is hit and when (the zones
        /// mechanic keeps its own dwell clock). Returns the damage actually taken (0 when pct is 0, the player is dead or
        /// protected, or the run has ended).
        /// </summary>
        public int HitPlayer(HazardHandle source, Player player, double pct, DamageType damageType = DamageType.Fire, string text = null)
        {
            if (source == null || source.Object == null || source.Object.IsDestroyed || Landblock == null || Run.IsEnded)
                return 0;

            if (player == null || player.IsDead || player.IsDestroyed)
                return 0;

            // A spanning region only touches a player on a usable landblock (this tick thread); an arena region only
            // hits a player inside it. Neither applies to a single-landblock region without HitsRequireInside.
            if (Spanning || Region.HitsRequireInside)
            {
                if (!TryAnchorPosition(player.Location, out var p))
                    return 0;

                if (Region.HitsRequireInside && !Region.ContainsPlayer(p, Terrain))
                    return 0;
            }

            return DamagePlayer(source, player, pct, damageType, text);
        }

        /// <summary>
        /// Damages ONE player now by a FLAT number mitigated by the player's resistance to <paramref name="damageType"/>
        /// (the fill hit), same guards as <see cref="HitPlayer"/>, same shared tail. Returns the damage actually taken.
        /// </summary>
        public int HitPlayerFlat(HazardHandle source, Player player, double flat, DamageType damageType, string text = null)
        {
            if (source == null || source.Object == null || source.Object.IsDestroyed || Landblock == null || Run.IsEnded)
                return 0;

            if (player == null || player.IsDead || player.IsDestroyed)
                return 0;

            if (Spanning || Region.HitsRequireInside)
            {
                if (!TryAnchorPosition(player.Location, out var p))
                    return 0;

                if (Region.HitsRequireInside && !Region.ContainsPlayer(p, Terrain))
                    return 0;
            }

            var amount = HazardHitRules.FlatAmount(flat, player.GetResistanceMod(damageType, source.Object, null));

            return ApplyHit(source, player, amount, damageType, text);
        }

        /// <summary>The percent-of-max-health tail of <see cref="HitPlayers"/> and <see cref="HitPlayer"/>. Returns the damage taken (0 = none).</summary>
        private int DamagePlayer(HazardHandle source, Player player, double fraction, DamageType damageType, string text)
        {
            var amount = HazardHitRules.DamageFor(player.Health.MaxValue, fraction);

            return ApplyHit(source, player, amount, damageType, text);
        }

        /// <summary>The one place a hazard damages a player: TakeDamage, the tell, the debug line and the host callback. Returns the damage taken (0 = none).</summary>
        private int ApplyHit(HazardHandle source, Player player, uint amount, DamageType damageType, string text)
        {
            if (amount == 0)
                return 0;

            int taken;

            try
            {
                // isHazard: no ward soaks a hazard hit (owner ruling R3; PreWriteDamageContext.IsHazard lists them)
                taken = player.TakeDamage(source.Object, damageType, amount, BodyPart.Foot, isHazard: true);
            }
            catch (Exception ex)
            {
                log.Error($"{Run.LogPrefix} damage threw for {player.Name}", ex);
                return 0;
            }

            // Invincible, dead, lifestone-protected or fully mitigated: no damage, so no line, no count and no
            // host notification. The ledger write in HitPlayers stands - the hit was still "spent" for this window.
            if (taken <= 0)
                return 0;

            Tell(player, text ?? $"The {source.Object.Name} {HitVerb} you for {taken} damage!");

            if (log.IsDebugEnabled)
                log.Debug($"{Run.LogPrefix} hit {player.Name} by hazard {source.Id} ({source.Tag}) for {taken} (asked {amount}) at t={Now:0.00}");

            try
            {
                Run.Host.OnPlayerHit(Run, player, taken);
            }
            catch (Exception ex)
            {
                log.Error($"{Run.LogPrefix} host OnPlayerHit threw", ex);
            }

            return taken;
        }

        /// <summary>
        /// Every live player currently in the region (2D shape AND z band), with their position in the region's frame:
        /// landblock-local for a single-landblock region, the anchor frame for a spanning one (players on every usable
        /// covered landblock).
        /// </summary>
        public List<(Player Player, Vector3 Position)> PlayersInRegion()
        {
            var result = new List<(Player, Vector3)>();
            var span = Span;

            // span is null exactly when the run has no landblock (registry tests), unless a test supplies one
            if (span == null)
                return result;

            var players = PlayerSource;
            var slots = span.Slots;

            for (var si = 0; si < slots.Count; si++)
            {
                var slot = slots[si];
                var lb = slot.Current;

                if (!slot.Usable || lb == null)
                    continue;

                for (var i = 0; i < players.Count(lb); i++)
                {
                    if (!players.TryGet(lb, i, out var seen))
                        continue;

                    var loc = seen.Location;

                    if (loc == null || seen.IsDead || loc.Instance != Region.Instance || loc.LandblockId.Landblock != slot.Id)
                        continue;

                    var p = slot.IsAnchor ? loc.Pos : HazardSpan.ToAnchorFrame(span.AnchorId, slot.Id, loc.Pos);

                    if (Region.ContainsPlayer(p, Terrain))
                        result.Add((seen.Player, p));
                }
            }

            return result;
        }

        // ---- radar ------------------------------------------------------------------------------------------------

        private readonly HazardRadarLedger<Creature> radarLedger = new HazardRadarLedger<Creature>();

        /// <summary>
        /// Test seam: replaces the live radar write (the property set and the broadcast) with a recorder. Receives the
        /// creature and the value being written (null = the property is removed); returns whether it wrote.
        /// </summary>
        internal Func<Creature, RadarBehavior?, bool> RadarWriteForTest { get; set; }

        /// <summary>The guids of the creatures this run currently hides from the radar.</summary>
        public IReadOnlyCollection<uint> RadarHidden => radarLedger.Hidden;

        /// <summary>
        /// Every live non-player, non-pet creature in the region (2D shape AND z band) with its position in the
        /// region's frame, on every usable covered landblock and in the run's instance. The run's own spawns are left
        /// out. Landblock thread.
        /// </summary>
        public List<(Creature Creature, Vector3 Position)> CreaturesInRegion()
        {
            var result = new List<(Creature, Vector3)>();
            var span = Span;

            if (span == null)
                return result;

            var own = new HashSet<WorldObject>();

            foreach (var h in owned)
                if (h.Object != null)
                    own.Add(h.Object);

            var slots = span.Slots;

            for (var si = 0; si < slots.Count; si++)
            {
                var slot = slots[si];
                var lb = slot.Current;

                if (!slot.Usable || lb == null)
                    continue;

                foreach (var wo in lb.GetAllWorldObjectsForDiagnostics())
                {
                    if (!(wo is Creature c) || c is Player || c is CombatPet || c.IsDead || c.IsDestroyed || own.Contains(c))
                        continue;

                    var loc = c.Location;

                    if (loc == null || loc.Instance != Region.Instance || loc.LandblockId.Landblock != slot.Id)
                        continue;

                    var p = slot.IsAnchor ? loc.Pos : HazardSpan.ToAnchorFrame(span.AnchorId, slot.Id, loc.Pos);

                    if (Region.ContainsPlayer(p, Terrain))
                        result.Add((c, p));
                }
            }

            return result;
        }

        /// <summary>
        /// Hides a creature from the radar (ShowNever), recording its AUTHORED value (the biota's, never the ephemeral
        /// overlay another run may have written) so the framework can give it back (<see cref="RestoreRadar"/>, and
        /// every remaining one when the run is torn down). False when it is already hidden by this run or gone.
        ///
        /// OVERLAP: when run A ends while run B still covers the creature, A restores the authored value and B hides it
        /// again on its next radar scan (within radar_scan seconds). That brief blip is accepted.
        /// </summary>
        public bool HideFromRadar(Creature c)
        {
            if (c == null || c.IsDestroyed)
                return false;

            if (!radarLedger.Record(c.Guid.Full, c, AuthoredRadar(c)))
                return false;

            WriteRadarOnItsThread(c, RadarBehavior.ShowNever);
            return true;
        }

        /// <summary>The creature's authored radar behaviour: the biota value, which the ephemeral overlay (c.RadarBehavior) never touches. Null = never authored.</summary>
        internal static RadarBehavior? AuthoredRadar(Creature c)
            => (RadarBehavior?)ACE.Entity.Models.BiotaExtensions.GetProperty(c.Biota, PropertyInt.ShowableOnRadar, c.BiotaDatabaseLock);

        /// <summary>Gives one hidden creature its authored radar behaviour back. False when it was not hidden or is gone (the entry is dropped either way).</summary>
        public bool RestoreRadar(uint guid)
        {
            if (!radarLedger.TryTake(guid, out var c, out var authored))
                return false;

            if (c == null || c.IsDestroyed)
                return false;

            WriteRadarOnItsThread(c, authored);
            return true;
        }

        /// <summary>Restores every creature still hidden; called by Teardown. Returns how many were written or queued.</summary>
        internal int RestoreAllRadar()
        {
            var restored = 0;

            foreach (var (c, authored) in radarLedger.TakeAll())
            {
                if (c == null || c.IsDestroyed)
                    continue;

                if (WriteRadarOnItsThread(c, authored))
                    restored++;
            }

            return restored;
        }

        /// <summary>Where a radar write must run: now (the creature is on the run's landblock), queued on its own landblock, or nowhere (in no landblock).</summary>
        internal static HazardDestroyRoute RouteRadarWrite(bool onRunLandblock, bool hasLandblock)
            => onRunLandblock ? HazardDestroyRoute.Now : hasLandblock ? HazardDestroyRoute.EnqueueOnItsLandblock : HazardDestroyRoute.Skip;

        /// <summary>
        /// A creature belongs to the landblock thread it stands on, which after it wandered off is not this run's: write
        /// inline on the run's landblock, else queue the write on the creature's own landblock, else skip it.
        /// </summary>
        private bool WriteRadarOnItsThread(Creature c, RadarBehavior? value)
        {
            var lb = c.CurrentLandblock;

            switch (RouteRadarWrite(ReferenceEquals(lb, Landblock), lb != null))
            {
                case HazardDestroyRoute.Now:
                    return WriteRadar(c, value);

                case HazardDestroyRoute.EnqueueOnItsLandblock:
                    lb.EnqueueAction(new ACE.Server.Entity.Actions.ActionEventDelegate(() =>
                    {
                        if (!c.IsDestroyed)
                            WriteRadar(c, value);
                    }));
                    return true;

                default:
                    return false;
            }
        }
        private bool WriteRadar(Creature c, RadarBehavior? value)
        {
            try
            {
                if (RadarWriteForTest != null)
                    return RadarWriteForTest(c, value);

                c.RadarBehavior = value;
                c.EnqueueBroadcast(new GameMessageUpdateObject(c));

                if (value.HasValue)
                    c.EnqueueBroadcast(true, new GameMessagePublicUpdatePropertyInt(c, PropertyInt.ShowableOnRadar, (int)value.Value));
                else
                    // HEDGE pending the owner probe: UpdateObject omits a null radar field, so a client may keep ShowNever.
                    // 0 (Undefined) as the client's reading of a missing field is an assumption, not verified.
                    c.EnqueueBroadcast(true, new GameMessagePublicUpdatePropertyInt(c, PropertyInt.ShowableOnRadar, 0));

                return true;
            }
            catch (Exception ex)
            {
                log.Error($"{Run.LogPrefix} radar write threw for {c?.Name}", ex);
                return false;
            }
        }

        // ---- talking ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The run's host asked for player-facing text (<see cref="IHazardHost.PlayerFacing"/>): <see cref="Say"/> is then
        /// silent and only <see cref="SayPlayer"/> speaks. Read live from the host; a host that throws counts as the admin
        /// default (false).
        /// </summary>
        public bool PlayerFacing
        {
            get
            {
                try
                {
                    return Run?.Host?.PlayerFacing == true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The DEVELOPER channel: one "[hazard #N] ..." line to every player on this landblock copy (shared invariant 9).
        /// Silent for a <see cref="PlayerFacing"/> host, whose players are not shown run numbers, counts or coordinates.
        /// </summary>
        public void Say(string line, ChatMessageType type = ChatMessageType.WorldBroadcast)
        {
            if (PlayerFacing)
                return;

            Broadcast(line, type);
        }

        /// <summary>The PLAYER channel: a short, plain line, said only for a <see cref="PlayerFacing"/> host (silent for /hazard).</summary>
        public void SayPlayer(string line, ChatMessageType type = ChatMessageType.Broadcast)
        {
            if (!PlayerFacing)
                return;

            Broadcast(line, type);
        }

        private void Broadcast(string line, ChatMessageType type)
        {
            var span = Span;

            if (string.IsNullOrEmpty(line) || span == null)
                return;

            // every usable covered landblock (a single-landblock region: just the run's own)
            var players = PlayerSource;
            var slots = span.Slots;

            for (var si = 0; si < slots.Count; si++)
            {
                var slot = slots[si];
                var lb = slot.Current;

                if (!slot.Usable || lb == null)
                    continue;

                for (var i = 0; i < players.Count(lb); i++)
                {
                    if (!players.TryGet(lb, i, out var seen))
                        continue;

                    if (seen.Location == null || seen.Location.Instance != Region.Instance)
                        continue;

                    Tell(seen.Player, line, type);
                }
            }
        }

        /// <summary>One line to one player. Best effort.</summary>
        public void Tell(Player player, string line, ChatMessageType type = ChatMessageType.Broadcast)
        {
            if (player == null || string.IsNullOrEmpty(line))
                return;

            try
            {
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, type));
            }
            catch (Exception ex)
            {
                log.Error($"{Run.LogPrefix} tell threw for {player.Name}", ex);
            }
        }

        /// <summary>Info line with the run's "[HAZARD] lb=.. inst=.. run=#n kind=.." prefix.</summary>
        public void Log(string message)
        {
            log.Info($"{Run.LogPrefix} {message}");
            LogForTest?.Invoke(message);
        }

        /// <summary>WARN line with the same prefix as <see cref="Log"/>: a run that goes on but is degraded.</summary>
        public void LogWarn(string message)
        {
            log.Warn($"{Run.LogPrefix} {message}");
            LogForTest?.Invoke(message);
        }

        /// <summary>Test seam: sees every <see cref="Log"/> and <see cref="LogWarn"/> line (without the prefix). Always null live.</summary>
        internal Action<string> LogForTest { get; set; }

        // ---- time -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Runs <paramref name="step"/> on the landblock thread at the first driver tick at or after Now +
        /// <paramref name="seconds"/> (tick_ms resolution, default 100 ms). Same-time steps run in the order
        /// scheduled. Dropped when the run ends.
        /// </summary>
        public void Schedule(double seconds, Action step)
        {
            if (step == null || Run.IsEnded)
                return;

            if (!double.IsFinite(seconds) || seconds < 0.0)
                seconds = 0.0;

            schedule.Add((Now + seconds, ++scheduleSeq, step));
        }

        /// <summary>Ends the run with <paramref name="outcome"/>. Idempotent. The teardown runs after this tick.</summary>
        public void End(HazardOutcome outcome, string reason = null)
            => HazardManager.Stop(Run, outcome, reason ?? outcome.ToString().ToLowerInvariant());

        // ---- driver side (internal) -------------------------------------------------------------------------------

        internal void SetNow(double now) => Now = now;

        /// <summary>
        /// Called by the driver at the top of every tick of this run. A spanning region re-checks which covered
        /// landblocks it may touch this tick (HazardSpan.Refresh); a no-op for a single-landblock region.
        /// </summary>
        internal void BeginTick()
        {
            tickNumber++;
            RefreshSpan();
        }

        /// <summary>HazardSpan.Refresh for a spanning region, logging each covered landblock the first time it is skipped.</summary>
        private void RefreshSpan()
        {
            if (!Spanning)
                return;

            Span?.Refresh((slot, why) => LogWarn($"covered landblock 0x{slot.Id:X4} skipped: {why} (logged once per run per landblock)"));
        }

        /// <summary>Test seam: own a handle without a world (the handle's Object may be null).</summary>
        internal void AddOwnedForTest(HazardHandle handle) => owned.Add(handle);

        internal int ScheduledCountForTest => schedule.Count;

        /// <summary>How many objects the last Teardown actually destroyed (already-dead handles are not counted).</summary>
        internal int LastTeardownDestroyed { get; private set; }

        /// <summary>Advances every moving hazard: a new sub-leg velocity at a boundary, a snap at the leg end.</summary>
        internal void AdvanceMotion()
        {
            for (var i = owned.Count - 1; i >= 0; i--)
            {
                var h = owned[i];

                if (!h.Alive)
                {
                    owned.RemoveAt(i);
                    continue;
                }

                // Belt and braces for the Missile flag: whatever re-armed the server physics since the last tick,
                // a moving hazard is never left active (HazardMotion.Park).
                if (h.Moving)
                    HazardMotion.Park(h.Object);

                var path = h.Path;

                if (path == null)
                    continue;

                switch (path.Advance(Now))
                {
                    case HazardMotionStep.NewVelocity:
                        HazardMotion.SendVelocity(h.Object, path.SentVelocity, path.SentOmega);
                        TraceSent(h, $"sub-leg {path.SentIndex + 1}", path.SentVelocity, path.SentOmega, path.SubLegCount);
                        break;

                    case HazardMotionStep.Finished:
                        path.OnEnd?.Invoke(h, path);

                        // OnEnd may chain the next leg (MoveLeg sets a NEW Path) or destroy the handle: then this
                        // finished path must not snap or clear what replaced it.
                        if (!h.Alive || !ReferenceEquals(h.Path, path))
                            break;

                        if (path.AutoStop)
                        {
                            StopLeg(h, path.End);
                        }
                        else
                        {
                            h.Path = null;
                            h.Rest = path.End;
                        }
                        break;
                }
            }
        }

        /// <summary>Runs every due Schedule step in (due, order) order. A step scheduled by a step runs on a later tick at the earliest if it is not yet due.</summary>
        internal void RunDue()
        {
            if (schedule.Count == 0)
                return;

            var due = new List<(double Due, long Seq, Action Step)>();

            for (var i = schedule.Count - 1; i >= 0; i--)
            {
                if (schedule[i].Due <= Now)
                {
                    due.Add(schedule[i]);
                    schedule.RemoveAt(i);
                }
            }

            if (due.Count == 0)
                return;

            due.Sort((a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Seq.CompareTo(b.Seq));

            foreach (var d in due)
            {
                if (Run.IsEnded)
                    return;

                d.Step();
            }
        }

        /// <summary>
        /// The end of a run on the landblock thread: the mechanic's OnStop (only if it was started), then every owned
        /// object destroyed and the schedule dropped. Runs once (queued by HazardManager.Stop).
        /// </summary>
        internal void Teardown(HazardOutcome outcome, bool wasStarted)
        {
            // the queued teardown ran: an orphaned-teardown route (anchor dead) must not also run. Teardown itself
            // still always runs as before. The orphan route can only overlap it if this queued action stalled past
            // ReapStaleSeconds on a live anchor. The claim flag stops a route after this point, and a route that ran
            // first leaves nothing owned; a truly simultaneous overlap is not guarded.
            Interlocked.Exchange(ref teardownClaimed, 1);

            // queued on the anchor outside a driver tick: which neighbours this thread may touch is re-read now
            RefreshSpan();

            if (wasStarted)
            {
                try
                {
                    Run.Mechanic?.OnStop(this, outcome);
                }
                catch (Exception ex)
                {
                    log.Error($"{Run.LogPrefix} OnStop threw", ex);
                }
            }

            schedule.Clear();

            // radar blips hidden by this run come back whatever the outcome, after OnStop and before anything is destroyed
            var radarRestored = RestoreAllRadar();

            if (radarRestored > 0)
                Log($"teardown: {radarRestored} radar blip(s) restored");

            var destroyed = 0;

            foreach (var h in owned.ToArray())
            {
                h.Path = null;

                try
                {
                    if (DestroyForTeardown(h))
                        destroyed++;
                }
                catch (Exception ex)
                {
                    log.Error($"{Run.LogPrefix} destroy threw for hazard {h.Id} ({h.Tag})", ex);
                }
            }

            owned.Clear();
            lastHits.Clear();

            LastTeardownDestroyed = destroyed;
            Log($"teardown: {destroyed} object(s) destroyed");
        }
    }
}
