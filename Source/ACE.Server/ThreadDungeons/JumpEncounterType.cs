using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

using log4net;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    public enum JumpPhase { Idle, Running, Done }

    /// <summary>What a use of the Trialmaster did.</summary>
    public enum JumpStartOutcome { RunOver, NotInside, AlreadyRunning, Spent, Claimed }

    /// <summary>What a use of the goal did, in the order the checks run.</summary>
    public enum JumpGoalOutcome { RunOver, NotInside, NotRunning, TimeUp, NotOnTop, AlreadyFinished, Finished }

    /// <summary>
    /// One Jump encounter's state (Docs/Threads/ENCOUNTERS.md "Jump"). The phase, the attempt generation, the deadline, the
    /// start roster and the finishers change only under <see cref="gate"/>; the world objects are touched only on the
    /// NPC's landblock queue. Player-free, so the unit tests drive it with guids and positions.
    /// </summary>
    public sealed class JumpState
    {
        private readonly object gate = new object();

        private JumpPhase phase = JumpPhase.Idle;
        private int generation;
        private bool formed;
        private DateTime deadline = DateTime.MinValue;
        private readonly HashSet<uint> startMembers = new HashSet<uint>();
        private readonly HashSet<uint> finishers = new HashSet<uint>();

        public JumpState(ThreadEncounterPlaceRequest request, JumpConfig config)
        {
            Request = request;
            Config = config;
            LayoutSeed = request?.Seed ?? 0;
        }

        public JumpConfig Config { get; }

        /// <summary>
        /// The seed of the layout Place accepted (the request seed, or a derived one when that layout was obstructed).
        /// Form regenerates from this, never from the request seed. Set by Place before the state is published, never changed after.
        /// </summary>
        public int LayoutSeed { get; internal set; }

        /// <summary>The Place request (run, the point's landblock, context, point, seed). Null once the run ended.</summary>
        public volatile ThreadEncounterPlaceRequest Request;

        public uint NpcGuid;
        public volatile uint GoalGuid;

        /// <summary>The Trialmaster. Its landblock queue is where every course object is created and destroyed.</summary>
        public WorldObject Npc;

        /// <summary>The top pad, in global outdoor metres, once the course has formed.</summary>
        public double TopGx, TopGy;
        public float TopZ;

        // World objects of the current attempt: NPC landblock queue only.
        internal readonly List<WorldObject> Pads = new List<WorldObject>();
        internal WorldObject Goal;

        private JumpFallGuard.Zone zone;

        /// <summary>The fall exemption zone of the running course (in-memory; closed by the end or by OnRunEnded).</summary>
        public JumpFallGuard.Zone Zone { get { lock (gate) return zone; } }

        /// <summary>
        /// Records the formed course's fall zone. When the run already ended (<see cref="Request"/> is null, set by
        /// <see cref="Release"/> under the same lock), the zone is closed here, so a zone opened after OnRunEnded read
        /// "no zone" can never stay open forever.
        /// </summary>
        public void AttachZone(JumpFallGuard.Zone opened, DateTime utcNow)
        {
            bool close;

            lock (gate)
            {
                zone = opened;
                close = Request == null;
            }

            if (close)
                JumpFallGuard.Close(opened, utcNow);
        }

        /// <summary>The run ended (OnRunEnded): releases the request and closes any attached zone, under the zone lock.</summary>
        public void Release(DateTime utcNow)
        {
            JumpFallGuard.Zone attached;

            lock (gate)
            {
                Request = null;
                attached = zone;
            }

            JumpFallGuard.Close(attached, utcNow);
        }

        public JumpPhase Phase { get { lock (gate) return phase; } }

        public int Generation { get { lock (gate) return generation; } }

        public DateTime Deadline { get { lock (gate) return deadline; } }

        public IReadOnlyCollection<uint> Finishers { get { lock (gate) return finishers.ToList(); } }

        public IReadOnlyCollection<uint> StartMembers { get { lock (gate) return startMembers.ToList(); } }

        /// <summary>
        /// A use of the NPC. The checks run in this order: the run is over (silent), the user is not a member inside,
        /// the climb is under way, the encounter is spent. Only from Idle is the phase claimed (Idle to Running, a new
        /// generation), so two uses in one tick start one course.
        /// </summary>
        public JumpStartOutcome TryClaim(bool runOver, bool userInside, out int claimedGeneration)
        {
            claimedGeneration = 0;

            if (runOver)
                return JumpStartOutcome.RunOver;

            if (!userInside)
                return JumpStartOutcome.NotInside;

            lock (gate)
            {
                switch (phase)
                {
                    case JumpPhase.Running:
                        return JumpStartOutcome.AlreadyRunning;
                    case JumpPhase.Done:
                        return JumpStartOutcome.Spent;
                }

                phase = JumpPhase.Running;
                formed = false;
                deadline = DateTime.MinValue;
                startMembers.Clear();
                claimedGeneration = ++generation;
                return JumpStartOutcome.Claimed;
            }
        }

        /// <summary>The course could not form: back to Idle (the encounter is not spent, no timer ran). False when stale.</summary>
        public bool FormFailed(int claimedGeneration)
        {
            lock (gate)
            {
                if (phase != JumpPhase.Running || generation != claimedGeneration || formed)
                    return false;

                phase = JumpPhase.Idle;
                return true;
            }
        }

        /// <summary>The course formed: the timer starts now, for the members inside at this moment. False when stale.</summary>
        public bool MarkFormed(int claimedGeneration, DateTime deadlineUtc, IEnumerable<uint> membersInside)
        {
            lock (gate)
            {
                if (phase != JumpPhase.Running || generation != claimedGeneration || formed)
                    return false;

                formed = true;
                deadline = deadlineUtc;
                startMembers.Clear();

                foreach (var guid in membersInside ?? Enumerable.Empty<uint>())
                    startMembers.Add(guid);

                return true;
            }
        }

        /// <summary>Is <paramref name="claimedGeneration"/> the current attempt, still Running?</summary>
        public bool IsCurrent(int claimedGeneration)
        {
            lock (gate)
                return phase == JumpPhase.Running && generation == claimedGeneration;
        }

        /// <summary>
        /// A use of the goal. The checks run in this order: the run is over, the user is a member inside, the course is
        /// Running (formed) and <paramref name="utcNow"/> is at or before the deadline, the user stands on the top pad
        /// (<see cref="JumpEncounterType.IsOnTopPad"/>), the user has not finished already. Finished adds the user to the
        /// finishers, which is the latch: one Finished per player per course. <paramref name="everyoneFinished"/> is true
        /// when this finish completes every member who was inside at the start.
        /// </summary>
        public JumpGoalOutcome TryFinish(uint guid, bool runOver, bool userInside, DateTime utcNow, double gx, double gy, float z,
            out bool everyoneFinished, out int currentGeneration)
        {
            everyoneFinished = false;
            currentGeneration = 0;

            if (runOver)
                return JumpGoalOutcome.RunOver;

            if (!userInside)
                return JumpGoalOutcome.NotInside;

            lock (gate)
            {
                currentGeneration = generation;

                if (phase != JumpPhase.Running || !formed)
                    return JumpGoalOutcome.NotRunning;

                if (utcNow > deadline)
                    return JumpGoalOutcome.TimeUp;

                if (!JumpEncounterType.IsOnTopPad(TopGx, TopGy, TopZ, gx, gy, z))
                    return JumpGoalOutcome.NotOnTop;

                if (!finishers.Add(guid))
                    return JumpGoalOutcome.AlreadyFinished;

                everyoneFinished = startMembers.Count > 0 && startMembers.All(finishers.Contains);
                return JumpGoalOutcome.Finished;
            }
        }

        /// <summary>The course ends (timeout or everyone finished): Running to Done, once. False when stale or already ended.</summary>
        public bool TryEnd(int claimedGeneration)
        {
            lock (gate)
            {
                if (phase != JumpPhase.Running || generation != claimedGeneration || !formed)
                    return false;

                phase = JumpPhase.Done;
                return true;
            }
        }
    }

    /// <summary>
    /// The Jump encounter (Docs/Threads/ENCOUNTERS.md "Jump"), outdoor runs only. A Skyward Trialmaster waits at the
    /// placed point; a member inside the run uses it, and a random course of free-floating platforms rises around it with
    /// ONE timer for the whole group (dynamic_dungeons_jump_time_seconds). Every member who reaches the top platform and
    /// rings the Summit Bell before the time is up is a finisher. One shot: when the timer ends the course fades and the
    /// encounter is spent. A fall from the course never hurts (JumpFallGuard).
    ///
    /// Threading. Place runs on the point's landblock queue and enters the Trialmaster there. OnUse runs on the user's
    /// thread: the phase changes are one locked call on <see cref="JumpState"/>, and every world-object change (the pads,
    /// the goal, their destroy, the NPC's destroy) is queued onto the NPC's own landblock through an ActionChain on the
    /// NPC. Every queued action does nothing once the run or its encounters ended, or once its attempt is no longer the
    /// Running one. OnRunEnded releases in-memory state only.
    /// </summary>
    public sealed class JumpEncounterType : IThreadEncounterType, IThreadEncounterUsable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindName = "jump";

        /// <summary>Seconds between the course's end and the Trialmaster's destroy.</summary>
        public const float SpentDestroyDelaySeconds = 3.0f;

        /// <summary>The landblock-local margin RejectPoint keeps around the course disc: x and y in [R + 2, 190 - R].</summary>
        public const float LandblockMarginLow = 2f;
        public const float LandblockEdgeHigh = 190f;

        /// <summary>How far inside the run's outdoor perimeter every pad stays.</summary>
        public const float PerimeterInset = 5f;

        /// <summary>A pad that entered the world further than this from its intended spot was moved: the course does not form.</summary>
        public const float PlacementTolerance = 0.02f;

        /// <summary>The top-pad test: half the side plus this, either axis.</summary>
        public const float TopPadXyMargin = 0.6f;
        public const float TopPadBelow = 0.3f;
        public const float TopPadAbove = 2.5f;

        /// <summary>The countdown marks, seconds left.</summary>
        public static readonly int[] CountdownMarks = { 30, 10, 5 };

        public const string RefusedLine = "Only members of this thread can begin the climb.";
        public const string RunningLine = "The climb is already under way.";
        public const string SpentLine = "The platforms have already been raised here.";
        public const string CouldNotFormLine = "The platforms cannot rise here. The Trialmaster's course is blocked.";
        public const string StartLine = "The platforms rise! Reach the Summit Bell at the top before time runs out.";
        public const string NotOnTopLine = "You must stand atop the highest platform to ring the bell.";
        public const string FinishedLine = "You reached the summit!";
        public const string TimeoutLine = "Time is up. The platforms fade away.";
        public const string EarlyEndLine = "Everyone reached the summit. The platforms fade away.";

        public static string CountdownLine(int seconds) => $"{seconds.ToString(CultureInfo.InvariantCulture)} seconds remain.";

        public static string OtherFinishedLine(string name) => $"{name} reached the summit!";

        public string Kind => KindName;

        /// <summary>Test seam: the object factory. Production is WorldObjectFactory.</summary>
        internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

        /// <summary>Test seam: the terrain function the generator gets for a request. Production probes the point's landblock.</summary>
        internal static Func<ThreadEncounterPlaceRequest, Func<float, float, float?>> GroundFor = ProductionGround;

        /// <summary>Test seam: what a finisher's completion hook observed (guid), before the hook itself runs.</summary>
        internal static Action<ThreadEncounterInstance, uint> CompletionObserved;

        /// <summary>Test seam: EnterWorld for the Trialmaster and the course objects (pads, goal). Production is WorldObject.EnterWorld.</summary>
        internal static Func<WorldObject, bool> EnterWorld = wo => wo.EnterWorld();

        /// <summary>Test seam: Destroy for the course objects and the spent NPC. Production is WorldObject.Destroy.</summary>
        internal static Action<WorldObject> DestroyObject = wo => wo.Destroy();

        /// <summary>Test seam: the online roster members inside the run. Production is ThreadRunPresence.OnlineMembersInside.</summary>
        internal static Func<ThreadDungeonRun, List<Player>> MembersInside = run => ThreadRunPresence.OnlineMembersInside(run);

        /// <summary>Test seam: runs right after a course is marked formed (a test throws here to drive the late-failure path).</summary>
        internal static Action AfterFormed;

        /// <summary>
        /// Test seam: would a course object of this wcid enter at this position (pads: and land within PlacementTolerance,
        /// the third argument)? Null when clear, otherwise why not. Enters nothing. Production is JumpPlacementProbe.
        /// </summary>
        internal static Func<uint, Position, bool, string> ProbePlacement = (wcid, at, mustLandExactly) => JumpPlacementProbe.Probe(CreateObject, wcid, at, mustLandExactly);

        internal delegate JumpLayout GenerateFn(JumpConfig config, Random rng, Vector3 npcLocal, Func<float, float, float?> ground, Func<float, float, bool> allowed, out string why);

        /// <summary>Test seam: the course generator Place and Form both call. Production is JumpCourse.Generate.</summary>
        internal static GenerateFn GenerateLayout = JumpCourse.Generate;

        /// <summary>How many layouts Place tries: the request seed, then derived seeds (<see cref="LayoutSeedFor"/>).</summary>
        public const int LayoutAttempts = 16;

        /// <summary>The odd stride between derived layout seeds (the 32-bit golden-ratio constant).</summary>
        public const int LayoutSeedStride = unchecked((int)0x9E3779B1);

        /// <summary>The seed of layout attempt <paramref name="attempt"/> (0 is the request seed). Pure.</summary>
        public static int LayoutSeedFor(int requestSeed, int attempt) => unchecked(requestSeed + attempt * LayoutSeedStride);

        /// <summary>
        /// Place stops early after this many layouts IN A ROW had no room (an obstruction resets the run). No room is usually
        /// the point's verdict rather than the seed's, so the search stops early; the generator's walks are seed-driven, so a
        /// marginal point can still be refused that a later seed would have fitted.
        /// </summary>
        public const int NoRoomStopAfter = 6;

        /// <summary>
        /// The refusal prefix: "the course is obstructed" when every refused layout was obstructed, "no room for the course"
        /// when every one had no room, otherwise "no clear layout". Pure.
        /// </summary>
        internal static string RefusalKind(bool sawObstruction, bool sawNoRoom)
            => sawObstruction && !sawNoRoom ? "the course is obstructed"
               : sawNoRoom && !sawObstruction ? "no room for the course"
               : "no clear layout";

        // ---- config ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Parses a jump row's config. Absent, null or {} yields the shipped defaults. A config that is not an object, a bad
        /// wcid, a platform count outside 7 to 9, a bad rise or gap range, a step the design jump skill cannot make, or a
        /// bad radius is an error (null with <paramref name="error"/>). Pure.
        /// </summary>
        public static JumpConfig ParseConfig(JsonElement config, out string error)
        {
            error = null;

            if (config.ValueKind == JsonValueKind.Undefined || config.ValueKind == JsonValueKind.Null)
                return Validate(new JumpConfig(), out error);

            if (config.ValueKind != JsonValueKind.Object)
            {
                error = $"config must be an object, not {config.ValueKind}";
                return null;
            }

            var d = new JumpConfig();

            if (!TryWcid(config, "npcWcid", d.NpcWcid, out var npc, out error)
                || !TryWcid(config, "platformWcid", d.PlatformWcid, out var platform, out error)
                || !TryWcid(config, "goalWcid", d.GoalWcid, out var goal, out error))
                return null;

            if (!TryInt(config, "platforms", d.Platforms, out var platforms)
                || !TryRange(config, "rise", d.RiseMin, d.RiseMax, out var riseMin, out var riseMax)
                || !TryRange(config, "gap", d.GapMin, d.GapMax, out var gapMin, out var gapMax)
                || !TryFloat(config, "radius", d.Radius, out var radius)
                || !TryFloat(config, "firstDistance", d.FirstDistance, out var first)
                || !TryInt(config, "designJumpSkill", (int)d.DesignJumpSkill, out var skill)
                || !TryFloat(config, "designRunSpeed", d.DesignRunSpeed, out var speed))
            {
                error = "config.platforms, rise, gap, radius, firstDistance, designJumpSkill and designRunSpeed must be numbers (rise and gap as { min, max })";
                return null;
            }

            if (skill < 1)
            {
                error = "config.designJumpSkill must be at least 1";
                return null;
            }

            return Validate(new JumpConfig
            {
                NpcWcid = npc,
                PlatformWcid = platform,
                GoalWcid = goal,
                Platforms = platforms,
                RiseMin = riseMin,
                RiseMax = riseMax,
                GapMin = gapMin,
                GapMax = gapMax,
                Radius = radius,
                FirstDistance = first,
                DesignJumpSkill = (uint)skill,
                DesignRunSpeed = speed,
            }, out error);
        }

        /// <summary>The range checks every parsed config passes, including that the hardest step is reachable. Pure.</summary>
        internal static JumpConfig Validate(JumpConfig c, out string error)
        {
            error = null;

            if (c.Platforms < JumpConfig.MinPlatforms || c.Platforms > JumpConfig.MaxPlatforms)
                error = $"config.platforms must be {JumpConfig.MinPlatforms} to {JumpConfig.MaxPlatforms}";
            else if (!(c.RiseMin >= 0.5f) || !(c.RiseMax >= c.RiseMin))
                error = "config.rise must be { min, max } with 0.5 <= min <= max";
            else if (!(c.GapMin >= 0.5f) || !(c.GapMax >= c.GapMin))
                error = "config.gap must be { min, max } with 0.5 <= min <= max";
            else if (!(c.Radius >= 6f && c.Radius <= 30f))
                error = "config.radius must be 6 to 30";
            else if (!(c.FirstDistance >= 0f && c.FirstDistance <= 10f))
                error = "config.firstDistance must be 0 to 10";
            else if (!(c.DesignRunSpeed >= 1f && c.DesignRunSpeed <= 20f))
                error = "config.designRunSpeed must be 1 to 20";
            else if (!JumpCourse.IsStepReachable(c.RiseMax, c.GapMax, c.DesignJumpSkill, c.DesignRunSpeed, out var why))
                error = $"the hardest step is not reachable at Jump {c.DesignJumpSkill.ToString(CultureInfo.InvariantCulture)}: {why}";

            return error == null ? c : null;
        }

        private static bool TryWcid(JsonElement obj, string name, uint fallback, out uint value, out string error)
        {
            value = fallback;
            error = null;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt32(out value) && value != 0)
                return true;

            error = $"config.{name} must be a positive whole number";
            return false;
        }

        private static bool TryInt(JsonElement obj, string name, int fallback, out int value)
        {
            value = fallback;
            return !obj.TryGetProperty(name, out var e) || (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value));
        }

        private static bool TryFloat(JsonElement obj, string name, float fallback, out float value)
        {
            value = fallback;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var d) || !double.IsFinite(d))
                return false;

            value = (float)d;
            return true;
        }

        private static bool TryRange(JsonElement obj, string name, float minFallback, float maxFallback, out float min, out float max)
        {
            min = minFallback;
            max = maxFallback;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            return e.ValueKind == JsonValueKind.Object && TryFloat(e, "min", minFallback, out min) && TryFloat(e, "max", maxFallback, out max);
        }

        // ---- pure checks ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Is a player whose feet are at (<paramref name="gx"/>, <paramref name="gy"/>, <paramref name="z"/>) on the top pad?
        /// Within the half side plus <see cref="TopPadXyMargin"/> on both axes, feet from <see cref="TopPadBelow"/> under
        /// to <see cref="TopPadAbove"/> over its top face. Global outdoor metres. Pure.
        /// </summary>
        public static bool IsOnTopPad(double padGx, double padGy, float padTop, double gx, double gy, float z)
            => Math.Abs(gx - padGx) <= JumpCourse.PadHalf + TopPadXyMargin
               && Math.Abs(gy - padGy) <= JumpCourse.PadHalf + TopPadXyMargin
               && z >= padTop - TopPadBelow
               && z <= padTop + TopPadAbove;

        /// <summary>Is the run (or its encounters) over, or the request released? Then every use and queued action is silent.</summary>
        internal static bool RunOver(ThreadDungeonRun run, JumpState state)
            => run == null || state?.Request == null || run.State == ThreadDungeonRunState.Ended || run.EncountersEnded;

        /// <summary>
        /// The guard every queued action runs behind: the run and its encounters are live, and <paramref name="generation"/>
        /// is the current Running attempt. Runs <paramref name="action"/> and returns true only then.
        /// </summary>
        internal static bool RunIfLive(ThreadDungeonRun run, JumpState state, int generation, Action action)
        {
            if (RunOver(run, state) || !state.IsCurrent(generation))
                return false;

            action?.Invoke();
            return true;
        }

        /// <summary>The countdown marks that fit inside a timer of <paramref name="timeSeconds"/>, as (mark, delay) pairs. Pure.</summary>
        internal static IEnumerable<(int Mark, double Delay)> CountdownSchedule(long timeSeconds)
            => CountdownMarks.Where(m => m < timeSeconds).Select(m => (m, (double)(timeSeconds - m)));

        // ---- IThreadEncounterType -------------------------------------------------------------------------------

        /// <summary>Outdoor only, and the whole course disc must fit on the point's landblock: local x and y in [R + 2, 190 - R]. Pure.</summary>
        public string RejectPoint(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, bool isOutdoor)
        {
            if (!isOutdoor)
                return "jump: outdoor runs only";

            var config = ParseConfig(def?.Config ?? default, out var error);

            if (config == null)
                return "jump: " + error;

            return FitsLandblock(candidate.Point.X, candidate.Point.Y, config.Radius) ? null : "jump: the course would leave the point's landblock";
        }

        /// <summary>The RejectPoint bounds as a pure check.</summary>
        internal static bool FitsLandblock(float x, float y, float radius)
        {
            var lo = radius + LandblockMarginLow;
            var hi = LandblockEdgeHigh - radius;
            return x >= lo && x <= hi && y >= lo && y <= hi;
        }

        public bool Place(ThreadEncounterPlaceRequest request, out string failure)
        {
            failure = null;

            if (request.Run.Dungeon?.IsOutdoor != true)
            {
                failure = "outdoor runs only";
                return false;
            }

            var config = ParseConfig(request.Def?.Config ?? default, out var error);

            if (config == null)
            {
                failure = error;
                return false;
            }

            // The dry run: the same generator and ground the Use will see, and every pad and the goal probed against the
            // world (ProbePlacement, which enters nothing), so a point whose course cannot form is never placed. An
            // obstructed layout is retried under derived seeds; the accepted seed is what Form regenerates from.
            var point = request.Point.Point;
            var npcLocal = new Vector3(point.X, point.Y, point.Z);
            var ground = GroundFor(request);
            var allowed = AllowedFor(request);

            string lastRefusal = null;
            var sawObstruction = false;
            var sawNoRoom = false;
            var noRoomRun = 0;
            var attempts = 0;
            var acceptedSeed = 0;
            var acceptedAttempt = -1;

            for (var attempt = 0; attempt < LayoutAttempts && acceptedAttempt < 0; attempt++)
            {
                attempts++;
                var seed = LayoutSeedFor(request.Seed, attempt);
                var layout = GenerateLayout(config, new Random(seed), npcLocal, ground, allowed, out var why);

                if (layout == null)
                {
                    lastRefusal = why;
                    sawNoRoom = true;

                    // No room is usually the point's verdict, not the seed's: stop after NoRoomStopAfter in a row (a marginal
                    // point can still be refused).
                    if (++noRoomRun >= NoRoomStopAfter)
                        break;

                    continue;
                }

                noRoomRun = 0;
                var blocked = Obstruction(request, config, layout);

                if (blocked != null)
                {
                    lastRefusal = blocked;
                    sawObstruction = true;
                    continue;
                }

                acceptedSeed = seed;
                acceptedAttempt = attempt;
            }

            if (acceptedAttempt < 0)
            {
                failure = string.Format(CultureInfo.InvariantCulture, "{0} after {1} layouts: {2}", RefusalKind(sawObstruction, sawNoRoom), attempts, lastRefusal);
                return false;
            }

            if (acceptedAttempt > 0)
                request.Run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "{0}: course layout {1} of {2} accepted (seed {3}); the last refused ({4}): {5}",
                    request.Instance, acceptedAttempt + 1, LayoutAttempts, acceptedSeed, RefusalKind(sawObstruction, sawNoRoom), lastRefusal));

            var npc = CreateObject(config.NpcWcid);

            if (npc == null)
            {
                failure = $"wcid {config.NpcWcid} did not create - is its weenie in the world database?";
                return false;
            }

            npc.Location = request.PositionOf(point);
            npc.TimeToRot = -1;
            npc.IsTransientSpawn = true;

            var state = new JumpState(request, config) { NpcGuid = npc.Guid.Full, Npc = npc, LayoutSeed = acceptedSeed };
            request.Instance.State = state;

            // Before EnterWorld: a use can arrive the moment it is visible.
            request.Instance.Track(npc);

            bool entered;

            try
            {
                entered = EnterWorld(npc);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {request.Run} jump Trialmaster EnterWorld threw at {npc.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                npc.P_ThreadEncounter = null;
                try { npc.Destroy(); } catch { }
                request.Instance.State = null;
                failure = $"wcid {config.NpcWcid} failed to enter the world at {npc.Location?.ToLOCString()}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The first pad (in climb order), or else the goal, that would not form where <paramref name="layout"/> puts it,
        /// as "pad N at LOC (why)" / "the goal at LOC (why)"; null when every object is clear. The positions and the
        /// criteria are Form's: a pad at LocalPosition(pad.X, pad.Y, pad.Bottom) must enter and land within
        /// PlacementTolerance, the goal must enter. Enters nothing.
        /// </summary>
        internal static string Obstruction(ThreadEncounterPlaceRequest request, JumpConfig config, JumpLayout layout)
        {
            for (var i = 0; i < layout.Pads.Count; i++)
            {
                var pad = layout.Pads[i];
                var at = LocalPosition(request, pad.X, pad.Y, pad.Bottom);
                var why = ProbePlacement(config.PlatformWcid, at, true);

                if (why != null)
                    return $"pad {(i + 1).ToString(CultureInfo.InvariantCulture)} at {at.ToLOCString()} ({why})";
            }

            var goal = LocalPosition(request, layout.Goal.X, layout.Goal.Y, layout.Goal.Z);
            var goalWhy = ProbePlacement(config.GoalWcid, goal, false);

            return goalWhy == null ? null : $"the goal at {goal.ToLOCString()} ({goalWhy})";
        }

        public void OnRunEnded(ThreadEncounterInstance instance, string why)
        {
            if (!(instance?.State is JumpState state))
                return;

            // In-memory only: the request is released and the zone lapses after its grace, under one lock with
            // AttachZone so a course forming concurrently cannot leave its zone open. The copy and its objects go with EndRun.
            state.Release(DateTime.UtcNow);
        }

        /// <summary>
        /// The finisher hook, called once per player per course (the finisher set is the latch): the finisher gets one
        /// shared encounter prize (ThreadEncounterPrize.Grant, owner ruling 2026-11-09), delivered on their own landblock
        /// thread. A null player (tests) grants nothing.
        /// </summary>
        public static void OnCourseCompleted(ThreadEncounterInstance instance, Player player)
        {
            if (instance?.Run == null || player == null)
                return;

            ThreadEncounterPrize.Grant(instance.Run, player, KindName);
        }

        // ---- IThreadEncounterUsable ------------------------------------------------------------------------------

        public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)
        {
            if (!(instance?.State is JumpState state) || used == null || player == null)
                return;

            if (state.NpcGuid != 0 && used.Guid.Full == state.NpcGuid)
                UseNpc(instance, state, used, player);
            else if (state.GoalGuid != 0 && used.Guid.Full == state.GoalGuid)
                UseGoal(instance, state, player);
        }

        private static void UseNpc(ThreadEncounterInstance instance, JumpState state, WorldObject npc, Player player)
        {
            var run = instance.Run;
            var outcome = state.TryClaim(RunOver(run, state), ThreadRunPresence.IsMemberInside(run, player), out var generation);

            switch (outcome)
            {
                case JumpStartOutcome.NotInside:
                    Send(player, RefusedLine);
                    return;
                case JumpStartOutcome.AlreadyRunning:
                    Send(player, RunningLine);
                    return;
                case JumpStartOutcome.Spent:
                    Send(player, SpentLine);
                    return;
                case JumpStartOutcome.RunOver:
                    return;
            }

            if (npc.CurrentLandblock == null)
            {
                state.FormFailed(generation);
                Send(player, CouldNotFormLine);
                return;
            }

            var chain = new ActionChain();
            chain.AddAction(npc, () => Form(instance, state, generation, player));
            chain.EnqueueChain();
        }

        /// <summary>NPC landblock queue: generate, spawn and verify the pads, then the goal, then start the timer. Never throws.</summary>
        internal static void Form(ThreadEncounterInstance instance, JumpState state, int generation, Player user)
        {
            var run = instance.Run;
            var request = state.Request;

            if (RunOver(run, state) || !state.IsCurrent(generation))
                return;

            try
            {
                var point = request.Point.Point;
                var layout = GenerateLayout(state.Config, new Random(state.LayoutSeed), new Vector3(point.X, point.Y, point.Z), GroundFor(request), AllowedFor(request), out var why);

                if (layout == null)
                {
                    CouldNotForm(instance, state, generation, user, "no course fits: " + why);
                    return;
                }

                foreach (var pad in layout.Pads)
                {
                    var wo = CreateObject(state.Config.PlatformWcid);

                    if (wo == null)
                    {
                        CouldNotForm(instance, state, generation, user, $"platform wcid {state.Config.PlatformWcid} did not create");
                        return;
                    }

                    var intended = LocalPosition(request, pad.X, pad.Y, pad.Bottom);
                    wo.Location = new Position(intended);
                    wo.TimeToRot = -1;
                    wo.IsTransientSpawn = true;
                    state.Pads.Add(wo);

                    if (!TryEnter(wo, run) || !LandedAt(wo, intended))
                    {
                        CouldNotForm(instance, state, generation, user,
                            $"a platform did not land where intended ({intended.ToLOCString()} -> {(wo.PhysicsObj == null ? "not entered" : wo.Location?.ToLOCString())})");
                        return;
                    }
                }

                var goal = CreateObject(state.Config.GoalWcid);

                if (goal == null)
                {
                    CouldNotForm(instance, state, generation, user, $"goal wcid {state.Config.GoalWcid} did not create");
                    return;
                }

                goal.Location = LocalPosition(request, layout.Goal.X, layout.Goal.Y, layout.Goal.Z);
                goal.TimeToRot = -1;
                goal.IsTransientSpawn = true;
                state.Goal = goal;
                state.GoalGuid = goal.Guid.Full;
                instance.Track(goal);

                if (!TryEnter(goal, run))
                {
                    CouldNotForm(instance, state, generation, user, "the goal failed to enter the world");
                    return;
                }

                var lb = (ushort)(point.Cell >> 16);
                var top = layout.TopPad;
                state.TopGx = OutdoorSiteGeometry.ToGlobalX(lb, top.X);
                state.TopGy = OutdoorSiteGeometry.ToGlobalY(lb, top.Y);
                state.TopZ = top.Top;

                var now = DateTime.UtcNow;
                var seconds = ThreadDungeonRun.ClampJumpTimeSeconds(run.JumpTimeSeconds);
                var inside = MembersInside(run);

                if (!state.MarkFormed(generation, now.AddSeconds(seconds), inside.Select(p => p.Guid.Full)))
                {
                    DestroyCourse(state);
                    return;
                }

                AfterFormed?.Invoke();

                state.AttachZone(JumpFallGuard.Open(run.Instance, OutdoorSiteGeometry.ToGlobalX(lb, point.X), OutdoorSiteGeometry.ToGlobalY(lb, point.Y), state.Config.Radius, now), now);

                foreach (var member in inside)
                    Send(member, StartLine);

                foreach (var (mark, delay) in CountdownSchedule(seconds))
                    Schedule(state.Npc, delay, () => RunIfLive(run, state, generation, () => SendInside(run, CountdownLine(mark))));

                Schedule(state.Npc, seconds, () => EndCourse(instance, state, generation, TimeoutLine));

                run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "{0}: course raised by {1}; {2} pads, top {3:0.0} m up, {4} s, {5} member(s) inside",
                    instance, user?.Name, layout.Pads.Count, top.Top - point.Z, seconds, inside.Count));
                log.Info($"[DYNDUNGEON] {run} {instance} course raised by {user?.Name}: {layout.Pads.Count} pads, {seconds} s");
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} {instance} course formation threw", ex);
                CouldNotForm(instance, state, generation, user, "threw " + ex.GetType().Name);

                // A throw AFTER MarkFormed: FormFailed refuses (the course had formed), so the phase is still Running with
                // its timer and zone live while the course is gone. End it now instead, which spends the encounter.
                if (state.IsCurrent(generation))
                    EndCourse(instance, state, generation, TimeoutLine);
            }
        }

        private static void CouldNotForm(ThreadEncounterInstance instance, JumpState state, int generation, Player user, string why)
        {
            DestroyCourse(state);

            if (!state.FormFailed(generation))
                return;

            Send(user, CouldNotFormLine);
            instance.Run.RecordEncounterOutcome($"{instance}: could not form ({why}); back to idle");

            if (log.IsDebugEnabled)
                log.Debug($"[DYNDUNGEON] {instance.Run} {instance} could not form: {why}");
        }

        /// <summary>NPC landblock queue: the timeout, or the early end. Destroys the course, spends the encounter, queues the NPC's destroy.</summary>
        internal static void EndCourse(ThreadEncounterInstance instance, JumpState state, int generation, string line)
        {
            var run = instance.Run;

            if (RunOver(run, state) || !state.TryEnd(generation))
                return;

            DestroyCourse(state);
            JumpFallGuard.Close(state.Zone, DateTime.UtcNow);
            SendInside(run, line);

            run.RecordEncounterOutcome($"{instance}: course ended ({(line == EarlyEndLine ? "everyone finished" : "time up")}); {state.Finishers.Count} finisher(s)");

            var npc = state.Npc;

            Schedule(npc, SpentDestroyDelaySeconds, () =>
            {
                if (!RunOver(run, state) && npc != null && !npc.IsDestroyed)
                    DestroyObject(npc);
            });
        }

        private static void UseGoal(ThreadEncounterInstance instance, JumpState state, Player player)
        {
            var run = instance.Run;
            var location = player.Location;
            double gx = 0, gy = 0;
            var z = float.NaN;

            if (location != null && !location.Indoors)
            {
                var lb = location.LandblockId.Landblock;
                gx = OutdoorSiteGeometry.ToGlobalX(lb, location.PositionX);
                gy = OutdoorSiteGeometry.ToGlobalY(lb, location.PositionY);
                z = location.PositionZ;
            }

            RingGoal(instance, state, player.Guid.Full, player.Name, ThreadRunPresence.IsMemberInside(run, player), DateTime.UtcNow, gx, gy, z, player);
        }

        /// <summary>
        /// The Player-free core of a goal use: the locked finish (JumpState.TryFinish, which is the once-per-player latch),
        /// then for a finisher the lines, the completion hook and, when everyone has finished, the early end queued on the
        /// NPC's landblock. <paramref name="player"/> only receives lines and the hook; it may be null in a test.
        /// </summary>
        internal static JumpGoalOutcome RingGoal(ThreadEncounterInstance instance, JumpState state, uint guid, string name, bool userInside, DateTime utcNow,
            double gx, double gy, float z, Player player)
        {
            var run = instance.Run;
            var outcome = state.TryFinish(guid, RunOver(run, state), userInside, utcNow, gx, gy, z, out var everyone, out var generation);

            switch (outcome)
            {
                case JumpGoalOutcome.NotOnTop:
                    Send(player, NotOnTopLine);
                    break;

                case JumpGoalOutcome.Finished:
                    Send(player, FinishedLine);

                    foreach (var other in MembersInside(run))
                    {
                        if (other.Guid.Full != guid)
                            Send(other, OtherFinishedLine(name));
                    }

                    CompletionObserved?.Invoke(instance, guid);
                    OnCourseCompleted(instance, player);
                    run.RecordEncounterOutcome($"{instance}: {name} reached the summit");

                    if (everyone && state.Npc?.CurrentLandblock != null)
                    {
                        var chain = new ActionChain();
                        chain.AddAction(state.Npc, () => EndCourse(instance, state, generation, EarlyEndLine));
                        chain.EnqueueChain();
                    }
                    break;

                default:
                    // Not a member inside, not running, past the deadline, already finished: silent.
                    if (log.IsDebugEnabled)
                        log.Debug($"[DYNDUNGEON] {run} {instance} goal use by {name}: {outcome}");
                    break;
            }

            return outcome;
        }

        // ---- world helpers ---------------------------------------------------------------------------------------

        /// <summary>A position on the point's landblock, in the run copy, at local x, y, z, its cell resolved from x and y.</summary>
        private static Position LocalPosition(ThreadEncounterPlaceRequest request, float x, float y, float z)
        {
            var p = request.Point.Point;
            var pos = new Position(p.Cell, x, y, z, 0f, 0f, 0f, 1f, request.Run.Instance);
            pos.SetLandCell();
            return pos;
        }

        private static bool TryEnter(WorldObject wo, ThreadDungeonRun run)
        {
            try
            {
                return EnterWorld(wo);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} jump object wcid {wo.WeenieClassId} EnterWorld threw at {wo.Location?.ToLOCString()}", ex);
                return false;
            }
        }

        /// <summary>Did the object enter where it was put: same landblock and instance, within <see cref="PlacementTolerance"/>?</summary>
        private static bool LandedAt(WorldObject wo, Position intended)
        {
            var at = wo.Location;
            return at != null && at.Instance == intended.Instance && at.LandblockId.Landblock == intended.LandblockId.Landblock
                   && Vector3.Distance(at.Pos, intended.Pos) <= PlacementTolerance;
        }

        /// <summary>NPC landblock queue: destroys the current attempt's pads and goal. Idempotent.</summary>
        private static void DestroyCourse(JumpState state)
        {
            foreach (var pad in state.Pads)
            {
                try { if (!pad.IsDestroyed) DestroyObject(pad); } catch { }
            }

            state.Pads.Clear();

            var goal = state.Goal;
            state.Goal = null;

            if (goal != null)
            {
                goal.P_ThreadEncounter = null;
                try { if (!goal.IsDestroyed) DestroyObject(goal); } catch { }
            }
        }

        private static void Schedule(WorldObject on, double delaySeconds, Action action)
        {
            if (on == null)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(delaySeconds);
            chain.AddAction(on, action);
            chain.EnqueueChain();
        }

        private static void SendInside(ThreadDungeonRun run, string line)
        {
            foreach (var member in MembersInside(run))
                Send(member, line);
        }

        private static void Send(Player player, string line)
        {
            if (!string.IsNullOrEmpty(line))
                player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        /// <summary>
        /// The generator's ground on the point's landblock: the overflow scatter's checks (ThreadDungeonSpawner.ProbeOutdoorGround:
        /// no building in the cell, walkable) with the scatter's own height tolerance against the point. Only ever probes the
        /// point's own landblock (AllowedFor keeps every sample on it), which this queue holds loaded.
        /// </summary>
        private static Func<float, float, float?> ProductionGround(ThreadEncounterPlaceRequest request)
        {
            var p = request.Point.Point;
            var landblock = (ushort)(p.Cell >> 16);

            return (x, y) =>
            {
                var pos = new Position(p.Cell, x, y, p.Z, 0f, 0f, 0f, 1f, request.Run.Instance);

                if (pos.Indoors)
                    return null;

                pos.SetLandCell();

                if (pos.LandblockId.Landblock != landblock)
                    return null;

                return ThreadDungeonSpawner.ProbeOutdoorGround(pos, p.Z, ThreadDungeonSpawner.ScatterMaxDropZ, out var terrainZ) ? terrainZ : (float?)null;
            };
        }

        /// <summary>
        /// The generator's bounds: inside the point's landblock (a 0.5 m inset), and when the run has an outdoor perimeter,
        /// at least <see cref="PerimeterInset"/> inside it.
        /// </summary>
        internal static Func<float, float, bool> AllowedFor(ThreadEncounterPlaceRequest request)
        {
            var landblock = (ushort)(request.Point.Point.Cell >> 16);
            var perimeter = request.Context?.OutdoorPerimeter;
            return (x, y) => Allowed(landblock, x, y, perimeter == null ? (double?)null : perimeter.CentreGx, perimeter?.CentreGy ?? 0, perimeter?.Radius ?? 0);
        }

        /// <summary>The bounds as a pure check: landblock-local [0.5, 191.5], and within the perimeter radius less the inset. Pure.</summary>
        internal static bool Allowed(ushort landblock, float x, float y, double? centreGx, double centreGy, double perimeterRadius)
        {
            if (x < 0.5f || x > 191.5f || y < 0.5f || y > 191.5f)
                return false;

            if (centreGx == null)
                return true;

            var dx = OutdoorSiteGeometry.ToGlobalX(landblock, x) - centreGx.Value;
            var dy = OutdoorSiteGeometry.ToGlobalY(landblock, y) - centreGy;
            return Math.Sqrt(dx * dx + dy * dy) <= perimeterRadius - PerimeterInset;
        }
    }
}
