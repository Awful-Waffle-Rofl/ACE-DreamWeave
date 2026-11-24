using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Physics;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>The portal-wave knobs a wave encounter reads (Defense's <see cref="DefenseConfig"/> is one).</summary>
    public interface IPortalWaveConfig
    {
        int PortalsMin { get; }
        int PortalsMax { get; }
        int PerPortalMin { get; }
        int PerPortalMax { get; }
        double StaggerMin { get; }
        double StaggerMax { get; }
        float PortalMinDistance { get; }
        float PortalMaxDistance { get; }

        /// <summary>The portal scale for a run of this kind.</summary>
        double PortalScale(bool isOutdoor);
    }

    /// <summary>One curated point a portal might open at, with its frame position and (dungeons) its walked distance from the anchor.</summary>
    public readonly record struct PortalWaveCandidate(DungeonSpawnPointDef Point, Vector3 Global, float? WalkDistance);

    /// <summary>One planned enemy: which portal it comes through, how many seconds after the start, and what it is.</summary>
    public readonly record struct PortalWaveSpawn(int Portal, double Delay, DungeonSpawnPlanEntry Entry);

    /// <summary>How a portal wave stands (<see cref="ThreadEncounterPortalWaves.Decide"/>).</summary>
    public enum PortalWaveOutcome
    {
        /// <summary>Still going.</summary>
        None,

        /// <summary>The stream finished and every enemy that came through is dead or removed, with the anchor standing.</summary>
        Cleared,

        /// <summary>The anchor fell (a caller with no anchor passes it as always standing).</summary>
        AnchorLost,

        /// <summary>The anchor still stood at max seconds.</summary>
        TimedOut,
    }

    /// <summary>
    /// The portal-wave machinery a Thread encounter reuses (extracted from Defense, no behaviour change): pick portal
    /// points around an anchor point, open the Sky Rift layers there, plan a stream of the run's trash through them, spawn
    /// each enemy, and engage it at the nearest member inside the run. The anchor is the encounter's own object (Defense's
    /// pillar); nothing here knows what it is, and a caller with no Hold engine just never tags its enemies.
    ///
    /// Threading. Every object is created only onto the Place request's own landblock (the caller runs this on that
    /// landblock's queue), and every object is Tracked on the instance BEFORE EnterWorld. Engage and wake run on the
    /// enemy's own landblock thread. Nothing here holds state: the caller owns its lists and its lock.
    ///
    /// The instance carries the caller's logger, its kind label (used in log lines and refusals) and its object factory,
    /// so a caller's log output is exactly what it was before the extraction.
    /// </summary>
    public sealed class ThreadEncounterPortalWaves
    {
        /// <summary>A dungeon portal point whose walked distance from the anchor is more than this times the straight line is refused (a wall between them).</summary>
        public const float WalkRatio = 1.5f;

        /// <summary>Seconds between the start and the first enemy of each portal (the portals open first).</summary>
        public const double FirstSpawnDelaySeconds = 2.0;

        /// <summary>
        /// The Sky Rift portal layers (Content/events/axes/goals.json "portals.parts", wcids 1002644/1002645) at scale 1:
        /// upright discs 5 m up, spaced along the portal's facing. Scaled whole by the row's portalScale.
        /// </summary>
        internal static readonly IReadOnlyList<DecorDef> PortalParts = new[]
        {
            new DecorDef { Wcid = 1002645, Scale = 6.9f, Speed = 0.8f, Dz = 5.0f, Dy = 3.14f, Pitch = 90f },
            new DecorDef { Wcid = 1002644, Scale = 5.5f, Speed = 0.941f, Dz = 5.0f, Dy = 2.30f, Pitch = 90f },
            new DecorDef { Wcid = 1002645, Scale = 4.1f, Speed = 1.143f, Dz = 5.0f, Dy = 1.47f, Pitch = 90f },
            new DecorDef { Wcid = 1002644, Scale = 2.8f, Speed = 1.455f, Dz = 5.0f, Dy = 0.67f, Pitch = 90f },
            new DecorDef { Wcid = 1002645, Scale = 1.7f, Speed = 2.0f, Dz = 5.0f, Dy = -0.03f, Pitch = 90f },
        };

        /// <summary>What one wake attempt at a member found.</summary>
        public enum WakeResult
        {
            /// <summary>Woken with a member (or, switched, the pillar) as its target.</summary>
            Woke,

            /// <summary>A member is inside the run but none could be its target this moment (past the chase range or the
            /// outdoor perimeter, not visible yet). It stays idle and the next monitor tick tries again.</summary>
            MemberUntargetable,

            /// <summary>No online member is inside the run at all.</summary>
            NoMember,
        }

        private readonly ILog log;
        private readonly string label;
        private readonly Func<uint, WorldObject> createObject;

        /// <param name="log">The caller's logger (its log lines keep their logger name).</param>
        /// <param name="label">The caller's kind label, as it appears in its log lines and refusals (Defense: "defense").</param>
        /// <param name="createObject">The caller's object factory, read at each call (so a test seam reassigned later still applies).</param>
        public ThreadEncounterPortalWaves(ILog log, string label, Func<uint, WorldObject> createObject)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.label = label ?? throw new ArgumentNullException(nameof(label));
            this.createObject = createObject ?? throw new ArgumentNullException(nameof(createObject));
        }

        // ---- pure planning ---------------------------------------------------------------------------------------

        /// <summary>Compass bearing (radians, 0 = north, clockwise) from <paramref name="from"/> to <paramref name="to"/> in the run's frame. Pure.</summary>
        public static double Bearing(Vector3 from, Vector3 to) => Math.Atan2(to.X - from.X, to.Y - from.Y);

        /// <summary>The smallest angle between two bearings, in [0, pi]. Pure.</summary>
        public static double AngleBetween(double a, double b)
        {
            var d = Math.Abs(a - b) % (2 * Math.PI);
            return d > Math.PI ? 2 * Math.PI - d : d;
        }

        /// <summary>
        /// Where the portals open. A candidate qualifies when it is on the anchor's landblock, between portalMinDistance
        /// and portalMaxDistance from the anchor (straight line in the run's frame), and, in a dungeon, its walked
        /// distance (when the walk graph knows it) is at most <see cref="WalkRatio"/> times that line. Null with
        /// <paramref name="refusal"/> when fewer than portals.min qualify (the refusal names the anchor as
        /// <paramref name="anchorName"/>). Otherwise between portals.min and portals.max of them (never more than qualify):
        /// the first drawn at random, each next the one whose bearing from the anchor is furthest from every bearing
        /// already picked (ties to the earlier candidate). Pure over <paramref name="rng"/>.
        /// A caller may tighten the dungeon walk rule: <paramref name="walkRatio"/> replaces <see cref="WalkRatio"/>, and
        /// <paramref name="requireWalkDistance"/> also refuses a dungeon candidate whose walked distance is unknown (the
        /// Gambler's Cache, whose timer is a loss). Both default to Defense's rule, unchanged.
        /// </summary>
        public static List<DungeonSpawnPointDef> PickPortals(Vector3 anchor, ushort landblock, IReadOnlyList<PortalWaveCandidate> candidates,
            bool isOutdoor, IPortalWaveConfig config, Random rng, string anchorName, out string refusal,
            float walkRatio = WalkRatio, bool requireWalkDistance = false)
        {
            refusal = null;

            if (config == null || rng == null)
            {
                refusal = "no config";
                return null;
            }

            var qualifying = Qualifying(anchor, landblock, candidates, isOutdoor, config, walkRatio, requireWalkDistance);

            if (qualifying.Count < config.PortalsMin)
            {
                refusal = TooFewRefusal(qualifying.Count, config, anchorName);
                return null;
            }

            var count = Math.Min(rng.Next(config.PortalsMin, config.PortalsMax + 1), qualifying.Count);
            var picked = new List<(DungeonSpawnPointDef Point, double Bearing)>();

            if (count > 0)
            {
                var first = rng.Next(qualifying.Count);
                picked.Add(qualifying[first]);
                qualifying.RemoveAt(first);
            }

            while (picked.Count < count && qualifying.Count > 0)
            {
                var best = 0;
                var bestScore = double.MinValue;

                for (var i = 0; i < qualifying.Count; i++)
                {
                    var score = picked.Min(p => AngleBetween(p.Bearing, qualifying[i].Bearing));

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }

                picked.Add(qualifying[best]);
                qualifying.RemoveAt(best);
            }

            return picked.Select(p => p.Point).ToList();
        }

        /// <summary>
        /// THE portal qualifying rule, shared by <see cref="PickPortals"/> (Place) and <see cref="RejectPointInRun"/> (the
        /// picker): on the anchor's landblock, between portalMinDistance and portalMaxDistance by straight line, and in a
        /// dungeon a walked distance of at most <paramref name="walkRatio"/> times that line (an unknown walked distance
        /// fails only with <paramref name="requireWalkDistance"/>). In candidate order, with each one's bearing. Pure.
        /// </summary>
        internal static List<(DungeonSpawnPointDef Point, double Bearing)> Qualifying(Vector3 anchor, ushort landblock, IReadOnlyList<PortalWaveCandidate> candidates,
            bool isOutdoor, IPortalWaveConfig config, float walkRatio, bool requireWalkDistance)
        {
            var qualifying = new List<(DungeonSpawnPointDef Point, double Bearing)>();

            if (config == null)
                return qualifying;

            foreach (var c in candidates ?? Array.Empty<PortalWaveCandidate>())
            {
                if (c.Point == null || (ushort)(c.Point.Cell >> 16) != landblock)
                    continue;

                var d = ThreadEncounterGeometry.Distance(anchor, c.Global, isOutdoor);

                if (d < config.PortalMinDistance || d > config.PortalMaxDistance)
                    continue;

                if (!isOutdoor && (c.WalkDistance is float walk ? walk > walkRatio * d : requireWalkDistance))
                    continue;

                qualifying.Add((c.Point, Bearing(anchor, c.Global)));
            }

            return qualifying;
        }

        /// <summary>The refusal <see cref="PickPortals"/> gives when fewer than portals.min qualify (the prod log's text). Pure.</summary>
        internal static string TooFewRefusal(int qualifying, IPortalWaveConfig config, string anchorName)
            => string.Format(CultureInfo.InvariantCulture, "{0} portal point(s) between {1} and {2} m on the {4}'s landblock; {3} needed",
                qualifying, config.PortalMinDistance, config.PortalMaxDistance, config.PortalsMin, anchorName);

        /// <summary>
        /// The picker-time prefilter with Place's full rule (prod 2026-11-21: 25 dungeon defense and gamble picks passed the
        /// straight-line <see cref="RejectPointAmong"/> and were then refused by Place's walk-ratio test, the slot lost).
        /// First the straight-line count (cheap, and its reason unchanged); then, for a run, the exact candidates Place will
        /// build (<see cref="PortalCandidates(ThreadDungeonRun, ThreadEncounterCandidate, IPortalWaveConfig, bool)"/>, walk
        /// graph included) through <see cref="Qualifying"/> with the caller's walk rule, rejected below portals.min with
        /// Place's own refusal, prefixed by the label. Null accepts. Pure over the walk graph seam.
        /// </summary>
        public string RejectPointInRun(ThreadDungeonRun run, ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, bool isOutdoor,
            IPortalWaveConfig config, string anchorName, float walkRatio = WalkRatio, bool requireWalkDistance = false)
        {
            var straight = RejectPointAmong(candidate, runPoints, isOutdoor, config);

            if (straight != null || run == null || candidate == null || config == null)
                return straight;

            var qualifying = Qualifying(candidate.Global, candidate.Landblock, PortalCandidates(run, candidate, config, isOutdoor), isOutdoor, config, walkRatio, requireWalkDistance).Count;

            return qualifying < config.PortalsMin ? $"{label}: {TooFewRefusal(qualifying, config, anchorName)}" : null;
        }

        /// <summary>
        /// The picker-time prefilter (review R1, 2026-11-11): a point where Place's <see cref="PickPortals"/> must refuse is
        /// rejected before the slot is spent, so the slot goes to another type or point. Rejects (with a reason prefixed by
        /// the caller's label) when <see cref="PortalPointsInReach"/> is below portals.min; null otherwise, or with no
        /// candidate or no config (a caller rejects an unparseable config itself, with its own reason). The dungeon walk-ratio test is not applied here (no walk graph per candidate at pick time), so this
        /// count is an upper bound on PickPortals': it never rejects a point Place would accept. Pure.
        /// </summary>
        public string RejectPointAmong(ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, bool isOutdoor, IPortalWaveConfig config)
        {
            if (candidate == null || config == null)
                return null;

            var reach = PortalPointsInReach(candidate, runPoints, isOutdoor, config);

            return reach < config.PortalsMin
                ? string.Format(CultureInfo.InvariantCulture, "{0}: fewer than {1} portal point(s) {2} to {3} m away", label, config.PortalsMin, config.PortalMinDistance, config.PortalMaxDistance)
                : null;
        }

        /// <summary>
        /// The <see cref="RejectPointAmong"/> count: the run's curated points on the anchor's landblock (open-ground ones
        /// outdoors, as NearbyPoints offers), the anchor itself excluded, whose straight-line distance is within
        /// [portalMinDistance, portalMaxDistance]. Pure.
        /// </summary>
        public static int PortalPointsInReach(ThreadEncounterCandidate anchor, IReadOnlyList<ThreadEncounterCandidate> runPoints, bool isOutdoor, IPortalWaveConfig config)
        {
            if (anchor?.Point == null || runPoints == null || config == null)
                return 0;

            var landblock = anchor.Landblock;
            var count = 0;

            foreach (var c in runPoints)
            {
                var p = c?.Point;

                if (p == null || !p.Curated || ReferenceEquals(p, anchor.Point) || c.Landblock != landblock)
                    continue;

                if (isOutdoor && !string.Equals(p.Kind, "outdoor", StringComparison.Ordinal))
                    continue;

                var d = ThreadEncounterGeometry.Distance(anchor.Global, c.Global, isOutdoor);

                if (d >= config.PortalMinDistance && d <= config.PortalMaxDistance)
                    count++;
            }

            return count;
        }

        /// <summary>
        /// The stream: for each portal, between perPortal.min and perPortal.max trash creatures, the first
        /// <see cref="FirstSpawnDelaySeconds"/> after the start and each next one staggerSeconds (uniform in [min, max])
        /// after the one before, standing on its portal's point. All portals stream at once; the list is in delay order
        /// (stable, so one portal's own order is kept). Empty when the plan has no trash. Pure over <paramref name="rng"/>.
        /// </summary>
        public static List<PortalWaveSpawn> PlanStream(IReadOnlyList<DungeonSpawnPointDef> portals, IReadOnlyList<DungeonSpawnPlanEntry> planEntries,
            IPortalWaveConfig config, Random rng)
        {
            var result = new List<PortalWaveSpawn>();

            if (portals == null || config == null || rng == null)
                return result;

            var pool = ThreadEncounterTrash.Pool(planEntries);

            if (pool.Count == 0)
                return result;

            for (var p = 0; p < portals.Count; p++)
            {
                var n = rng.Next(config.PerPortalMin, config.PerPortalMax + 1);
                var t = FirstSpawnDelaySeconds;

                for (var k = 0; k < n; k++)
                {
                    if (k > 0)
                        t += config.StaggerMin + rng.NextDouble() * (config.StaggerMax - config.StaggerMin);

                    result.Add(new PortalWaveSpawn(p, t, ThreadEncounterTrash.Draw(pool, portals[p], rng)));
                }
            }

            return result.OrderBy(s => s.Delay).ToList();
        }

        /// <summary>
        /// The outcome table: the anchor down is lost (first); with the anchor standing, the stream finished and no enemy
        /// alive is cleared; still standing at <paramref name="maxSeconds"/> is timed out; else none. A caller with no
        /// anchor passes <paramref name="anchorStanding"/> true. Pure.
        /// </summary>
        public static PortalWaveOutcome Decide(bool anchorStanding, bool streamDone, int enemiesAlive, double elapsed, double maxSeconds)
        {
            if (!anchorStanding)
                return PortalWaveOutcome.AnchorLost;

            if (streamDone && enemiesAlive <= 0)
                return PortalWaveOutcome.Cleared;

            if (elapsed >= maxSeconds)
                return PortalWaveOutcome.TimedOut;

            return PortalWaveOutcome.None;
        }

        // ---- portal points (Place) -------------------------------------------------------------------------------

        /// <summary>The run's curated points near the anchor (same landblock, NearbyPoints), with the walked distance in a dungeon whose walk graph knows the anchor's cell.</summary>
        public List<PortalWaveCandidate> PortalCandidates(ThreadEncounterPlaceRequest request, IPortalWaveConfig config, bool isOutdoor)
            => PortalCandidates(request.Run, request.Point, config, isOutdoor);

        /// <summary>
        /// The same candidates for a run and an anchor candidate, without a place request: what Place builds, so the picker
        /// (<see cref="RejectPointInRun"/>) sees exactly the points and walked distances Place will.
        /// </summary>
        public List<PortalWaveCandidate> PortalCandidates(ThreadDungeonRun run, ThreadEncounterCandidate anchorCandidate, IPortalWaveConfig config, bool isOutdoor)
        {
            var anchor = anchorCandidate.Point;
            DungeonWalkGraph.WalkField walk = null;

            // The encounter pass's guard shape: no graph for an outdoor site or an unset landblock.
            var noGraph = isOutdoor || run?.Dungeon == null || run.Dungeon.Landblock == 0;

            if (!noGraph)
            {
                try
                {
                    var graph = ThreadEncounterPass.WalkGraphSource(run.Dungeon.Landblock);

                    if (graph != null && graph.HasCell(anchor.Cell))
                        walk = graph.From(anchor.Cell, new Vector3(anchor.X, anchor.Y, anchor.Z));
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} {label} walk graph threw; straight-line portal distances only", ex);
                }
            }

            // ThreadEncounterPlaceRequest.NearbyPoints, for this run and anchor.
            return ThreadEncounterPass.NearbyPoints(run, anchorCandidate, config.PortalMaxDistance)
                .Select(p => new PortalWaveCandidate(p, ThreadEncounterGeometry.Global(p, isOutdoor), walk?.DistanceTo(p.Cell, new Vector3(p.X, p.Y, p.Z))))
                .ToList();
        }

        // ---- on the Place landblock's queue ----------------------------------------------------------------------

        /// <summary>
        /// The Place landblock's queue: the Sky Rift layers at every portal point, each laid out as a Hold ring portal (by
        /// its bearing from the anchor, facing away from it) at <paramref name="scale"/>. <paramref name="onPlaced"/> gets
        /// each layer that entered the world, right after it did. Returns the number of layers placed. The caller catches.
        /// </summary>
        public int OpenPortalLayers(ThreadEncounterInstance instance, ThreadEncounterPlaceRequest request, Vector3 anchorGlobal,
            IReadOnlyList<DungeonSpawnPointDef> portalPoints, double scale, bool isOutdoor, Action<WorldObject> onPlaced)
        {
            var opened = 0;

            foreach (var point in portalPoints)
            {
                // Laid out as a Hold ring portal: by its bearing from the anchor, facing away from it.
                var bearing = Bearing(anchorGlobal, ThreadEncounterGeometry.Global(point, isOutdoor)) * 180.0 / Math.PI;
                var placed = ThreadDefenseHold.PortalLayers(PortalParts, scale, bearing);
                var centre = request.PositionOf(point);

                foreach (var part in placed)
                    if (TryPlacePortalPart(instance, request, centre, part, isOutdoor, onPlaced))
                        opened++;
            }

            return opened;
        }

        /// <summary>The Place landblock's queue: one portal layer, Tracked before EnterWorld, refused off this queue's landblock.</summary>
        public bool TryPlacePortalPart(ThreadEncounterInstance instance, ThreadEncounterPlaceRequest request, Position centre,
            DecorDef part, bool isOutdoor, Action<WorldObject> onPlaced)
        {
            var position = WorldEventSpawner.DecorPosition(centre, part.Dx, part.Dy, part.Dz, part.Inverted, part.Yaw, part.Pitch);

            if (position == null)
                return false;

            if (isOutdoor && !position.Indoors)
            {
                // Mirrors the World Events decor snap: the ground under the layer, then its height above it.
                position.AdjustMapCoords();
                position.PositionZ += part.Dz;
            }

            // The threading rule: only ever onto the landblock this queue belongs to.
            if (request.Landblock != null && position.LandblockId.Landblock != request.Landblock.Id.Landblock)
                return false;

            var wo = createObject(part.Wcid);

            if (wo == null)
                return false;

            wo.Location = position;
            wo.SetProperty(PropertyFloat.DefaultScale, part.Scale);
            wo.SetProperty(PropertyFloat.MotionSpeed, part.Speed);

            if (wo.CurrentMotionState?.MotionState != null)
                wo.CurrentMotionState.MotionState.ForwardSpeed = part.Speed;

            wo.TimeToRot = -1;
            wo.Stuck = true;
            wo.IsTransientSpawn = true;

            instance.TrackDecor(wo);

            bool entered;

            try
            {
                entered = wo.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {label} portal layer {part.Wcid} EnterWorld threw", ex);
                entered = false;
            }

            if (!entered)
            {
                wo.P_ThreadEncounter = null;
                try { wo.Destroy(); } catch { }
                return false;
            }

            onPlaced?.Invoke(wo);

            return true;
        }

        /// <summary>
        /// The Place landblock's queue: one planned enemy, shaped as a rewarding run creature (ExcludedFromClear as asked),
        /// bound to the run perimeter, Tracked before EnterWorld, and refused off this queue's landblock. Null when it was
        /// not placed. No Hold tag: it fights players as an ordinary run creature.
        /// </summary>
        public Creature TrySpawnEnemy(ThreadEncounterPlaceRequest request, DungeonSpawnPlanEntry entry, bool excludedFromClear)
        {
            var wo = createObject(entry.Wcid);

            if (!(wo is Creature creature))
            {
                wo?.Destroy();
                return null;
            }

            if (!DungeonPopulationBuilder.IsKillable(creature.Attackable, creature.PlayerKillerStatus))
            {
                creature.Destroy();
                return null;
            }

            creature.Location = request.PositionOf(entry.Point);

            // The threading rule: only ever onto the landblock this queue belongs to.
            if (request.Landblock != null && creature.Location.LandblockId.Landblock != request.Landblock.Id.Landblock)
            {
                creature.Destroy();
                return null;
            }

            request.ShapeCreature(creature, entry, new CreatureShapeOptions
            {
                Run = request.Run,
                Rewards = true,
                GroupEffort = request.Run.Group.Effort,
                ExcludedFromClear = excludedFromClear,
            });

            request.BindToRunPerimeter(creature, entry.Point);

            // No Hold tag here: a caller with a Hold engine tags it later (Defense: ThreadDefenseHold.TagAsSeeker on its switch).
            request.Instance.Track(creature);

            bool entered;

            try
            {
                entered = creature.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {request.Run} {label} enemy EnterWorld threw for wcid {entry.Wcid}", ex);
                entered = false;
            }

            if (!entered)
            {
                creature.P_ThreadEncounter = null;
                try { creature.Destroy(); } catch { }
                return null;
            }

            return creature;
        }

        // ---- engage, on the enemy's own landblock thread ---------------------------------------------------------

        /// <summary>Still in the fight: not dead and not destroyed. Any thread (two field reads).</summary>
        public static bool IsStanding(Creature c) => c != null && !c.IsDead && !c.IsDestroyed;

        /// <summary>
        /// May an unswitched enemy be woken at this member? Alive, not teleporting, attackable, with a physics object, and,
        /// for an enemy bound to an outdoor perimeter, standing where the perimeter lets it hold a player
        /// (OutdoorCreaturePerimeter.IsValidTarget). Without that last clause an enemy woken at a member past the radius
        /// would chase, be sent home by the perimeter, go idle, and be woken at the same member again. Pure.
        /// </summary>
        internal static bool IsWakeCandidate(OutdoorCreaturePerimeter perimeter, bool dead, bool teleporting, bool attackable, bool hasPhysics, Position location)
            => !dead && !teleporting && attackable && hasPhysics && (perimeter == null || perimeter.IsValidTarget(true, location));

        /// <summary>
        /// One wake attempt's result (pure): Woke when it woke; otherwise NoMember only when no online member was inside the
        /// run at all, and MemberUntargetable for any other miss (every member dead, teleporting, past the chase range or
        /// the outdoor perimeter, or not visible after the forced add).
        /// </summary>
        internal static WakeResult ClassifyWake(int membersInside, bool woke)
            => woke ? WakeResult.Woke : membersInside <= 0 ? WakeResult.NoMember : WakeResult.MemberUntargetable;

        /// <summary>
        /// The enemy's landblock thread, as it comes through and whenever a monitor finds it idle: an idle, standing enemy
        /// is woken at the nearest member (<see cref="Wake"/>). False, waking nothing, when it is not standing, has no
        /// physics object or is not idle. True with <paramref name="wake"/> the attempt's result otherwise; a caller with
        /// an anchor decides its own fallback from it (Defense turns it on the pillar only on NoMember), and a caller
        /// without one can ignore it: a missed enemy stays idle for its next monitor tick.
        /// </summary>
        internal static bool EngageEnemy(Creature creature, ThreadDungeonRun run, out WakeResult wake)
        {
            wake = WakeResult.MemberUntargetable;

            if (!IsStanding(creature) || creature.PhysicsObj == null || creature.MonsterState != Creature.State.Idle)
                return false;

            wake = Wake(creature, run);
            return true;
        }

        /// <summary>
        /// The enemy's landblock thread: an idle enemy is given a target and woken rather than standing at its portal. A
        /// switched (tagged) one goes through the engine's TryHoldWake; an untagged one takes the nearest living member
        /// inside the run within chase range (TryHoldWake's own player branch, which needs a tag). Says whether it woke,
        /// found members it could not target this moment, or found no member inside at all (<see cref="ClassifyWake"/>).
        /// </summary>
        private static WakeResult Wake(Creature creature, ThreadDungeonRun run)
        {
            // Never NoMember on a guard: only an empty member set may send an enemy to the pillar early.
            if (!IsStanding(creature) || creature.PhysicsObj == null || creature.MonsterState != Creature.State.Idle)
                return WakeResult.MemberUntargetable;

            if (ThreadDefenseHold.IsTagged(creature))
                return creature.TryHoldWake() ? WakeResult.Woke : WakeResult.MemberUntargetable;

            Player best = null;
            var bestDistSq = double.MaxValue;
            var members = 0;

            foreach (var p in ThreadRunPresence.OnlineMembersInside(run))
            {
                members++;

                if (!IsWakeCandidate(creature.P_OutdoorPerimeter, p.IsDead, p.Teleporting, p.Attackable, p.PhysicsObj != null, p.Location))
                    continue;

                var distSq = creature.PhysicsObj.get_distance_sq_to_object(p.PhysicsObj, true);

                if (distSq <= Creature.MaxChaseRangeSq && distSq < bestDistSq)
                {
                    best = p;
                    bestDistSq = distSq;
                }
            }

            if (best == null)
                return ClassifyWake(members, false);

            if (!creature.IsVisibleTarget(best))
                creature.PhysicsObj.ObjMaint.AddVisibleTargets(new List<PhysicsObj> { best.PhysicsObj });

            if (!creature.IsVisibleTarget(best))
                return ClassifyWake(members, false);

            creature.AttackTarget = best;
            creature.WakeUp(alertNearby: false);
            return ClassifyWake(members, true);
        }
    }
}
