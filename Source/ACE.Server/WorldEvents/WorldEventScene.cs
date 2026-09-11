using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// What kind of placement one <see cref="ScenePlanEntry"/> is (WP-22, D6).
    /// </summary>
    public enum ScenePlanKind
    {
        Decor,
        Npc,
        Objective
    }

    /// <summary>
    /// One placement <see cref="WorldEventScenePreview.BuildPlan"/> computed for a scene preview - a pure
    /// data record, no world object involved (WP-22, D6). Offsets are metres from the scene centre (x east,
    /// y north, z up), already rotated by the scene's sceneYaw; Yaw is the entry's own heading with sceneYaw
    /// added on top.
    /// </summary>
    public sealed class ScenePlanEntry
    {
        public ScenePlanKind Kind { get; set; }
        public uint Wcid { get; set; }
        public float Dx { get; set; }
        public float Dy { get; set; }
        public float Dz { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float Scale { get; set; }
        public float Speed { get; set; }
        public bool Inverted { get; set; }

        /// <summary>DecorDef.Snap, carried through for Decor entries only (WP-23). See DecorDef for the rule.</summary>
        public bool Snap { get; set; }

        /// <summary>Resolved objective health, or null for a theme with no objectiveHealth. Objective entries only.</summary>
        public int? Health { get; set; }
    }

    /// <summary>
    /// WP-22 "/worldevent scene": stand up or clear one source theme's decor/npcs/objectives at an admin's
    /// feet WITHOUT running a world event - no WorldEventManager state, no waves, timers, ledger or reward
    /// caches. The look-iteration tool the repo owner asked for so a scene can be reviewed without paying the
    /// announce/stage/wave/reward cost of a real run.
    ///
    /// Placement mirrors WorldEventSpawner's PlaceDecor/PlaceNpcs/TryPlace(Source) recipes - same property
    /// writes (WorldEventId stamped before EnterWorld, TimeToRot = -1, DefaultScale/MotionSpeed for decor,
    /// the WP-15 npc/WP-21 objective terrain-snap-then-dz rule), but is not bound to a live WorldEvent:
    ///
    ///   * every scene object carries the sentinel <see cref="SceneRunId"/> as PropertyInt.WorldEventId
    ///     instead of a real RunId. WorldEvent.RunId is a monotonic uint counted up from 1
    ///     (WorldEventManager.NextRunId), so a negative sentinel can never collide with one - a real run's
    ///     OnEventCreatureDied (keyed on the P_WorldEvent back-reference, which scene objects never get -
    ///     Creature_Death.cs:130 requires both that reference AND the WorldEventId stamp) and the reward/cache
    ///     paths can therefore never confuse a scene object with a live run's, while WorldEventOrphanFilter's
    ///     crash-sweep (which only checks "does PropertyInt.WorldEventId exist at all", not its value) still
    ///     catches an orphaned scene object exactly like an orphaned run object;
    ///   * there is no held-landblock guard (WorldEvent.IsHeldLandblock) - a scene holds nothing. An object
    ///     that lands in an adjacent landblock is simply placed there; EnterWorld routes to the right
    ///     landblock via LandblockManager.
    /// </summary>
    public static class WorldEventScenePreview
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Sentinel PropertyInt.WorldEventId every scene object carries. Never a valid RunId - those are a
        /// monotonic uint counted up from 1 (WorldEventManager.NextRunId) - so a negative value can never
        /// collide, however many runs the process has started.
        /// </summary>
        public const int SceneRunId = -1;

        // A fixed OUTDOOR cell, the same fixture WorldEventGeometryTests uses, purely so
        // WorldEventGeometry's Position-shaped API has something to offset from when the legacy
        // ObjectiveWcid/geometry-anchor path needs anchor positions before any live centre exists. Only the
        // OFFSET (Position.GetOffset) is ever read back from it - never its absolute coordinates.
        private const uint DummyCell = 0x016C0025;
        private const float DummyCentreX = 96f;
        private const float DummyCentreY = 96f;

        private static readonly object sync = new object();
        private static readonly List<Scene> scenes = new List<Scene>();
        private static int nextSceneNumber = 1;

        private sealed class Scene
        {
            public int Number;
            public string ThemeId;
            public string Loc;
            public int DecorCount;
            public int NpcCount;
            public int ObjectiveCount;
            public int? Health;
            public readonly List<SceneObject> Objects = new List<SceneObject>();
        }

        /// <summary>
        /// One object a scene placed, paired with the Landblock its PlaceOne action was enqueued on
        /// (<see cref="SpawnLandblock"/>) - needed so Clear can always destroy it through a landblock action
        /// queue rather than ever on the calling thread (see Clear's remarks for why that matters).
        /// </summary>
        private sealed class SceneObject
        {
            public SceneObject(WorldObject wo, Landblock spawnLandblock)
            {
                Object = wo;
                SpawnLandblock = spawnLandblock;
            }

            public WorldObject Object { get; }

            public Landblock SpawnLandblock { get; }
        }

        // ---- plan (pure, D6) ---------------------------------------------------------------------------

        /// <summary>
        /// Rotates offset (dx, dy) by yawDegrees about the origin (D6, pure). Positive yaw is
        /// counter-clockwise seen from above, the same sense as WorldEventSpawner.NpcRotation - e.g.
        /// (6.5, 0) at 90 degrees becomes (0, 6.5): east rotated ccw becomes north.
        /// </summary>
        public static (float Dx, float Dy) RotateOffset(float dx, float dy, float yawDegrees)
        {
            var rad = yawDegrees * Math.PI / 180.0;
            var cos = (float)Math.Cos(rad);
            var sin = (float)Math.Sin(rad);

            return (dx * cos - dy * sin, dx * sin + dy * cos);
        }

        /// <summary>
        /// Builds the flat placement plan for one theme, WITHOUT creating any world object (D6, pure) - this
        /// is what the tests assert against directly. Every decor/npc/fixed-offset-objective offset is
        /// rotated by sceneYawDegrees about the geometry centre via <see cref="RotateOffset"/>, and every
        /// entry's own yaw gets sceneYawDegrees added on top.
        ///
        /// Objectives: a theme with SourceThemeDef.Objectives entries places exactly those (rotated,
        /// exactly like decor/npcs). A theme with none of those but a non-zero ObjectiveWcid places one at
        /// each geometry anchor instead - the legacy anchor-following form - sampled from
        /// WorldEventGeometry with no jitter; those anchors are NOT rotated a second time by sceneYaw, since
        /// Ring/Edges/Disc already randomise their own placement. ObjectiveHealth, when the theme sets it,
        /// is resolved from participants via ScaledCount.Resolve and carried on every objective record
        /// either way.
        ///
        /// Genuinely pure, not just "no world object": the Ring/Edges/Disc geometry anchors are drawn from a
        /// System.Random seeded deterministically from theme.Id (<see cref="GeometryAnchors"/>) rather than
        /// from WorldEventGeometry's ThreadSafeRandom-backed overloads, so calling BuildPlan twice with the
        /// same (theme, participants, sceneYawDegrees) always returns the same plan - two "scene rift"
        /// invocations place the rifts at the same compass points rather than a new random ring each time,
        /// which is what a look-iteration preview needs.
        /// </summary>
        public static List<ScenePlanEntry> BuildPlan(SourceThemeDef theme, int participants, float sceneYawDegrees)
        {
            var plan = new List<ScenePlanEntry>();

            if (theme == null)
                return plan;

            foreach (var decor in theme.Decor ?? new List<DecorDef>())
            {
                if (decor == null || decor.Wcid == 0)
                    continue;

                var (dx, dy) = RotateOffset(decor.Dx, decor.Dy, sceneYawDegrees);

                plan.Add(new ScenePlanEntry
                {
                    Kind = ScenePlanKind.Decor,
                    Wcid = decor.Wcid,
                    Dx = dx,
                    Dy = dy,
                    Dz = decor.Dz,
                    Yaw = decor.Yaw + sceneYawDegrees,
                    Pitch = decor.Pitch,
                    Scale = decor.Scale,
                    Speed = decor.Speed,
                    Inverted = decor.Inverted,
                    Snap = decor.Snap
                });
            }

            foreach (var npc in theme.Npcs ?? new List<NpcDef>())
            {
                if (npc == null || npc.Wcid == 0)
                    continue;

                var (dx, dy) = RotateOffset(npc.Dx, npc.Dy, sceneYawDegrees);

                plan.Add(new ScenePlanEntry
                {
                    Kind = ScenePlanKind.Npc,
                    Wcid = npc.Wcid,
                    Dx = dx,
                    Dy = dy,
                    Dz = npc.Dz,
                    Yaw = npc.Yaw + sceneYawDegrees
                });
            }

            var participantsClamped = Math.Max(0, participants);
            int? health = theme.ObjectiveHealth?.Resolve(participantsClamped);

            if (theme.Objectives != null && theme.Objectives.Count > 0)
            {
                foreach (var objective in theme.Objectives)
                {
                    if (objective == null || objective.Wcid == 0)
                        continue;

                    var (dx, dy) = RotateOffset(objective.Dx, objective.Dy, sceneYawDegrees);

                    plan.Add(new ScenePlanEntry
                    {
                        Kind = ScenePlanKind.Objective,
                        Wcid = objective.Wcid,
                        Dx = dx,
                        Dy = dy,
                        Dz = objective.Dz,
                        Yaw = objective.Yaw + sceneYawDegrees,
                        Health = health
                    });
                }
            }
            else if (theme.ObjectiveWcid != 0)
            {
                foreach (var anchor in GeometryAnchors(theme))
                {
                    var centre = DummyCentre();
                    var offset = centre.GetOffset(anchor);

                    plan.Add(new ScenePlanEntry
                    {
                        Kind = ScenePlanKind.Objective,
                        Wcid = theme.ObjectiveWcid,
                        Dx = offset.X,
                        Dy = offset.Y,
                        Dz = 0f,
                        Yaw = AnchorYawDegrees(anchor),
                        Health = health
                    });
                }
            }

            return plan;
        }

        private static Position DummyCentre()
        {
            return new Position(DummyCell, DummyCentreX, DummyCentreY, 0f, 0f, 0f, 0f, 1f, 0);
        }

        private static IReadOnlyList<Position> GeometryAnchors(SourceThemeDef theme)
        {
            var centre = DummyCentre();
            var points = Math.Max(1, theme.GeometryPoints);

            // Deterministic, NOT WorldEventGeometry's default ThreadSafeRandom-backed overloads - see
            // BuildPlan's remarks. Seeded from theme.Id alone (not participants/sceneYaw) so every scene of
            // the same theme lands on the same compass points, which is the point of a repeatable preview.
            var rng = new Random(DeterministicSeed(theme.Id));

            switch (theme.GeometryKind)
            {
                case SourceGeometry.Ring:
                    return WorldEventGeometry.Ring(centre, theme.GeometryRadius, points, 0f, rng);

                case SourceGeometry.Edges:
                    return WorldEventGeometry.Edges(centre, theme.GeometryRadius, points, 0f, rng);

                case SourceGeometry.Disc:
                    return WorldEventGeometry.Disc(centre, theme.GeometryRadius, points, rng);

                case SourceGeometry.Single:
                default:
                    return WorldEventGeometry.Single(centre);
            }
        }

        /// <summary>
        /// A stable (process- and run-independent) hash of <paramref name="s"/>, used ONLY to seed
        /// GeometryAnchors' Random - NOT string.GetHashCode(), which .NET randomizes per process for
        /// security and would make two BuildPlan calls in the SAME run agree but two different server runs
        /// (or two test runs) disagree, defeating the point of a fixed seed (D6, pure).
        /// </summary>
        private static int DeterministicSeed(string s)
        {
            unchecked
            {
                var hash = 17;

                foreach (var c in s ?? string.Empty)
                    hash = hash * 31 + c;

                return hash;
            }
        }

        /// <summary>
        /// Recovers the facing yaw (degrees) WorldEventGeometry's PlaceFacingCentre baked into an anchor's
        /// Rotation, as the exact inverse of WorldEventSpawner.NpcRotation (D6, pure). Position.Rotate builds
        /// a pure Z rotation via Quaternion.CreateFromYawPitchRoll(0, 0, roll), which for a pure Z rotation is
        /// the same quaternion NpcRotation's CreateFromAxisAngle(UnitZ, yawRad) produces - so yaw = 2 *
        /// atan2(z, w) recovers it.
        /// </summary>
        private static float AnchorYawDegrees(Position anchor)
        {
            var yawRad = 2.0 * Math.Atan2(anchor.RotationZ, anchor.RotationW);

            return (float)(yawRad * 180.0 / Math.PI);
        }

        // ---- placement -----------------------------------------------------------------------------------

        /// <summary>
        /// Builds theme's plan and places every entry at <paramref name="centre"/>, rotated by
        /// <paramref name="sceneYawDegrees"/>, on <paramref name="landblock"/>'s action queue. Construction
        /// happens on the caller's thread (safe, exactly as WorldEventSpawner's paths do it); every position
        /// write and EnterWorld happens on the landblock queue. Returns a one-line summary; the scene is
        /// recorded (guids and all) before this returns, so Clear/List see it immediately even though the
        /// landblock queue may not have drained yet.
        /// </summary>
        public static string Spawn(SourceThemeDef theme, Position centre, int participants, float sceneYawDegrees, Landblock landblock)
        {
            if (theme == null)
                return "worldevent scene: unknown theme";

            if (centre == null)
                return "worldevent scene: no live position";

            if (landblock == null)
                return "worldevent scene: no live landblock";

            var plan = BuildPlan(theme, participants, sceneYawDegrees);

            if (plan.Count == 0)
                return $"worldevent scene: theme {theme.Id} has no decor, npcs or objectives to place";

            var scene = new Scene
            {
                ThemeId = theme.Id,
                Loc = centre.ToLOCString()
            };

            var centreSnapshot = new Position(centre);

            foreach (var entry in plan)
            {
                WorldObject wo;

                try
                {
                    wo = WorldObjectFactory.CreateNewWorldObject(entry.Wcid);
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] scene could not create wcid {entry.Wcid}", ex);
                    continue;
                }

                if (wo == null)
                {
                    log.Error($"[WORLDEVENT] scene wcid {entry.Wcid} does not exist; skipped");
                    continue;
                }

                if (entry.Kind != ScenePlanKind.Decor && !(wo is Creature))
                {
                    // The same refusal WorldEventSpawner.Spawn/SpawnNpcs make - a non-Creature npc/objective
                    // can never die and would be a permanent fixture rather than a scoreable objective.
                    log.Error($"[WORLDEVENT] scene wcid {entry.Wcid} is not a Creature; it can never die, so it is not spawned as {entry.Kind}");
                    wo.Destroy();
                    continue;
                }

                switch (entry.Kind)
                {
                    case ScenePlanKind.Decor:
                        scene.DecorCount++;
                        break;
                    case ScenePlanKind.Npc:
                        scene.NpcCount++;
                        break;
                    case ScenePlanKind.Objective:
                        scene.ObjectiveCount++;
                        if (entry.Health.HasValue)
                            scene.Health = entry.Health;
                        break;
                }

                scene.Objects.Add(new SceneObject(wo, landblock));

                landblock.EnqueueAction(new ActionEventDelegate(() => PlaceOne(wo, entry, centreSnapshot)));
            }

            if (scene.Objects.Count == 0)
                return $"worldevent scene: theme {theme.Id}: nothing could be constructed";

            int number;

            lock (sync)
            {
                number = nextSceneNumber++;
                scene.Number = number;
                scenes.Add(scene);
            }

            var healthText = scene.Health.HasValue ? $" (health {scene.Health})" : "";

            return $"scene #{number}: {theme.Id} at {scene.Loc}: decor {scene.DecorCount}, npcs {scene.NpcCount}, " +
                   $"objectives {scene.ObjectiveCount}{healthText}";
        }

        /// <summary>
        /// The per-object placement recipe, run on the owning landblock's action queue. Mirrors
        /// WorldEventSpawner's PlaceDecor (decor) and TryPlace/PlaceNpcs (npc/objective) step for step: no
        /// terrain snap for decor unless DecorDef.Snap is set (WP-23), terrain snap + re-applied dz for
        /// npc/objective (the WP-15/WP-21 rule) and for snap-enabled decor alike, WorldEventId stamped and
        /// TimeToRot = -1 before EnterWorld, DefaultScale/MotionSpeed for decor, ApplyHealth for an
        /// objective with a resolved health. No held-landblock guard - see the class remarks.
        /// </summary>
        private static void PlaceOne(WorldObject wo, ScenePlanEntry entry, Position centre)
        {
            if (wo.IsDestroyed)
                return;

            Position position = null;

            try
            {
                if (entry.Kind == ScenePlanKind.Decor)
                {
                    position = WorldEventSpawner.DecorPosition(centre, entry.Dx, entry.Dy, entry.Dz, entry.Inverted, entry.Yaw, entry.Pitch);

                    // WP-23 DecorDef.Snap: mirrors WorldEventSpawner.PlaceDecor exactly. AdjustMapCoords
                    // never touches Rotation, which DecorPosition already set above, so no re-set is needed.
                    if (entry.Snap && !position.Indoors)
                    {
                        position.AdjustMapCoords();
                        position.PositionZ += entry.Dz;
                    }
                }
                else
                {
                    position = WorldEventSpawner.NpcPosition(centre, entry.Dx, entry.Dy, entry.Dz, entry.Yaw);

                    if (!position.Indoors)
                    {
                        position.AdjustMapCoords();

                        if (entry.Kind == ScenePlanKind.Npc)
                            position.PositionZ += entry.Dz;
                        else
                            position.PositionZ = WorldEventSpawner.ObjectiveSnapZ(position.PositionZ, entry.Dz);
                    }
                }

                wo.Location = position;

                // Stamped BEFORE the object enters the world - no window in which a landblock save could see
                // a live scene object as an ordinary persistable dynamic (TECH-DESIGN 2.9, same rule every
                // WorldEventSpawner placement path follows).
                wo.SetProperty(PropertyInt.WorldEventId, SceneRunId);
                wo.TimeToRot = -1;

                if (entry.Kind == ScenePlanKind.Decor)
                {
                    wo.SetProperty(PropertyFloat.DefaultScale, entry.Scale);
                    wo.SetProperty(PropertyFloat.MotionSpeed, entry.Speed);

                    if (wo.CurrentMotionState?.MotionState != null)
                        wo.CurrentMotionState.MotionState.ForwardSpeed = entry.Speed;
                }
                else if (entry.Kind == ScenePlanKind.Objective && entry.Health.HasValue && wo is Creature creature)
                {
                    // Deliberately NOT made inert the way WorldEventSpawner's InertObjective kind is
                    // (2026-08-19): a scene is a LOOK check with no goal composed at all, so the objective
                    // should stand exactly as the weenie ships it - attackable, labelled and on the radar -
                    // rather than in the scenery dress one particular goal would give it.
                    WorldEventSpawner.ApplyHealth(creature, entry.Health.Value);
                }

                var entered = false;

                try
                {
                    entered = wo.EnterWorld();
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] scene wcid={entry.Wcid} failed to enter the world at {position.ToLOCString()}", ex);
                }

                if (!entered)
                {
                    log.Error($"[WORLDEVENT] scene wcid={entry.Wcid} failed to enter the world at {position.ToLOCString()}");

                    if (!wo.IsDestroyed)
                        wo.Destroy();

                    return;
                }

                log.Info($"[WORLDEVENT] scene spawn wcid={wo.WeenieClassId} guid=0x{wo.Guid.Full:X8} loc={wo.Location?.ToLOCString()}");
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] scene placement threw for wcid {entry.Wcid} at {position?.ToLOCString()}", ex);

                if (!wo.IsDestroyed)
                    wo.Destroy();
            }
        }

        /// <summary>
        /// Tears down one scene's objects (or every scene's, when <paramref name="sceneNumber"/> is null).
        /// Already-destroyed objects are skipped rather than double-destroyed.
        ///
        /// Every destroy is enqueued on a landblock action queue - NEVER run on the calling thread. That
        /// matters because Spawn returns (and this can be called) before a placed object's PlaceOne action
        /// has necessarily run: calling Destroy() directly on the calling thread would recycle the object's
        /// guid (WorldObject.Destroy -> GuidManager.RecycleDynamicGuid) while the still-pending PlaceOne
        /// could go on to call EnterWorld on that same "destroyed" object - either resurrecting it or
        /// colliding the guid with whatever gets allocated next. Each SceneObject remembers the Landblock its
        /// PlaceOne was enqueued on (<see cref="SceneObject.SpawnLandblock"/>); enqueuing the destroy there
        /// too exploits the queue's FIFO order (it is a ConcurrentQueue drained in enqueue order) to guarantee
        /// PlaceOne always runs first. If the object has since entered the world on a DIFFERENT landblock
        /// (wo.CurrentLandblock is set and differs - possible when an offset crosses a landblock boundary),
        /// that is the one used instead, since it is now the object's actual owner.
        /// </summary>
        public static string Clear(int? sceneNumber)
        {
            List<Scene> targets;

            lock (sync)
            {
                targets = sceneNumber.HasValue
                    ? scenes.Where(s => s.Number == sceneNumber.Value).ToList()
                    : new List<Scene>(scenes);

                foreach (var scene in targets)
                    scenes.Remove(scene);
            }

            if (targets.Count == 0)
                return sceneNumber.HasValue ? $"no scene #{sceneNumber}" : "no scenes to clear";

            var destroyed = 0;

            foreach (var scene in targets)
            {
                foreach (var record in scene.Objects)
                {
                    var wo = record?.Object;

                    if (wo == null || wo.IsDestroyed)
                        continue;

                    var target = wo;
                    var currentLandblock = wo.CurrentLandblock;

                    var queueLandblock = currentLandblock != null && currentLandblock != record.SpawnLandblock
                        ? currentLandblock
                        : record.SpawnLandblock;

                    if (queueLandblock == null)
                    {
                        // Unreachable in practice - Spawn's early "landblock == null" guard means
                        // SpawnLandblock is always set - but never destroy on the calling thread as a
                        // fallback; log instead so a defect here is visible rather than silently unsafe.
                        log.Error($"[WORLDEVENT] scene clear: wcid={wo.WeenieClassId} guid=0x{wo.Guid.Full:X8} has no landblock to destroy through; leaked");
                        continue;
                    }

                    queueLandblock.EnqueueAction(new ActionEventDelegate(() =>
                    {
                        if (!target.IsDestroyed)
                            target.Destroy();
                    }));

                    destroyed++;
                }
            }

            return $"cleared {destroyed} object(s) from {targets.Count} scene(s)";
        }

        /// <summary>One line per live scene: number, theme id, loc, counts, and how many objects are still not destroyed.</summary>
        public static string List()
        {
            List<Scene> snapshot;

            lock (sync)
                snapshot = new List<Scene>(scenes);

            if (snapshot.Count == 0)
                return "no live scenes";

            var lines = new List<string>();

            foreach (var scene in snapshot)
            {
                var live = scene.Objects.Count(record => record?.Object != null && !record.Object.IsDestroyed);
                var healthText = scene.Health.HasValue ? $" health {scene.Health}" : "";

                lines.Add($"scene #{scene.Number}: {scene.ThemeId} at {scene.Loc}: decor {scene.DecorCount}, " +
                          $"npcs {scene.NpcCount}, objectives {scene.ObjectiveCount}{healthText}, live {live}");
            }

            return string.Join("\n", lines);
        }
    }
}
