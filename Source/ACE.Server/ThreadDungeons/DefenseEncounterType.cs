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
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Factories;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Physics;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;
using ACE.Server.WorldEvents.Hold;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// A defense row's parsed config (encounters.json "config"). Defaults when a key is absent: the five elemental pillar
    /// variants, 1 to 3 portals, 3 to 5 enemies per portal 2 to 4 s apart, portal points 8 to 25 m from the pillar, and
    /// the portal scale per run kind. The retired showHealthBar key is ignored with a note (<see cref="DefenseEncounterType.RetiredShowHealthBarNote"/>):
    /// the pillar always presents as a friendly NPC.
    /// </summary>
    public sealed class DefenseConfig : IPortalWaveConfig
    {
        public IReadOnlyList<uint> Wcids { get; init; } = DefenseEncounterType.DefaultWcids;
        public int PortalsMin { get; init; } = 1;
        public int PortalsMax { get; init; } = 3;
        public int PerPortalMin { get; init; } = 3;
        public int PerPortalMax { get; init; } = 5;
        public double StaggerMin { get; init; } = 2.0;
        public double StaggerMax { get; init; } = 4.0;
        public float PortalMinDistance { get; init; } = 8f;
        public float PortalMaxDistance { get; init; } = 25f;

        /// <summary>The Sky Rift portal recipe's scale inside a dungeon (low ceilings): the discs and their height scale together.</summary>
        public double PortalScaleDungeon { get; init; } = 0.3;

        /// <summary>The Sky Rift portal recipe's scale outdoors.</summary>
        public double PortalScaleOutdoor { get; init; } = 0.6;

        public const int MaxPortals = 6;
        public const int MaxPerPortal = 12;
        public const double MaxStaggerSeconds = 30.0;
        public const float MaxPortalDistance = 60f;
        public const double MaxPortalScale = 2.0;

        /// <summary>The portal scale for a run of this kind.</summary>
        public double PortalScale(bool isOutdoor) => isOutdoor ? PortalScaleOutdoor : PortalScaleDungeon;
    }

    /// <summary>How a defense ended.</summary>
    public enum DefenseOutcome
    {
        /// <summary>Still going.</summary>
        None,

        /// <summary>Every enemy that came through is dead or removed and the pillar stands.</summary>
        Success,

        /// <summary>The pillar fell.</summary>
        Failure,

        /// <summary>The pillar still stood at max_seconds: a success.</summary>
        Timeout,
    }

    /// <summary>
    /// Defense (Docs/Threads/ENCOUNTERS.md "Defense"): an elemental pillar a roster member wakes with a Use. 1 to 3 portals
    /// open around it and each streams a few of the run's trash creatures. Every enemy first fights the players; one still
    /// alive dynamic_dungeons_defense_switch_seconds after it came through turns on the pillar. The defense succeeds when
    /// every enemy is dead or removed with the pillar standing (or the pillar still stands at dynamic_dungeons_defense_max_seconds) and
    /// fails when the pillar falls. A win grants the shared encounter prize (<see cref="OnDefenseSucceeded"/>), no penalty: leftover
    /// enemies stay as ordinary bonus mobs. The enemies' kills never count toward the clear (ExcludedFromClear).
    ///
    /// It reuses the World Events Hold engine through its interface only (ThreadDefenseHoldView, ThreadDefenseHold): the
    /// pillar is a Hold knight of a view this encounter owns, so only the view's tagged enemies can hurt it, every such hit
    /// lands and is paced by undefended_seconds and min_death_seconds, and players and pets cannot hurt it. It presents as a friendly NPC: no client health bar and no
    /// assess health line, only the once-each milestones. An enemy is tagged (once, as a seeker) only when it switches;
    /// until then it is an ordinary run creature, engaged at the nearest member the moment it comes through.
    ///
    /// Threading. Place runs on the point's landblock queue and enters the pillar there. OnUse runs on the user's thread:
    /// it latches under the state lock and queues everything else (portals, the stream, the 1 s monitor) onto the PILLAR's
    /// landblock through ActionChains. An enemy that wandered onto another landblock is switched or woken on its own
    /// landblock's queue. OnRunEnded releases in-memory state only.
    /// </summary>
    public sealed class DefenseEncounterType : IThreadEncounterType, IThreadEncounterUsable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindName = "defense";

        /// <summary>The five elemental pillar variants (Content/sql/weenies/1007951..1007955; wcid-registry.d/wcid-1007951.tsv).</summary>
        public static readonly IReadOnlyList<uint> DefaultWcids = new uint[] { 1007951, 1007952, 1007953, 1007954, 1007955 };

        /// <summary>A dungeon portal point whose walked distance from the pillar is more than this times the straight line is refused (a wall between them).</summary>
        public const float WalkRatio = ThreadEncounterPortalWaves.WalkRatio;

        /// <summary>Seconds between the use and the first enemy of each portal (the portals open first).</summary>
        public const double FirstSpawnDelaySeconds = ThreadEncounterPortalWaves.FirstSpawnDelaySeconds;

        /// <summary>The monitor's period.</summary>
        public const double MonitorSeconds = 1.0;

        /// <summary>Seconds between an outcome and the pillar's destroy.</summary>
        public const double PillarDestroyDelaySeconds = 3.0;

        /// <summary>The health milestones announced, highest first (percent).</summary>
        public static readonly IReadOnlyList<int> MilestonePercents = new[] { 75, 50, 25 };

        public const string RefusedLine = "Only members of this thread can wake the pillar.";
        public const string StartLine = "The pillar hums awake and portals tear open around it. Defend the pillar!";
        public const string SuccessLine = "The pillar holds, and the portals collapse.";
        public const string FailureLine = "The pillar shatters, and the portals collapse.";

        /// <summary>The milestone line for <paramref name="percent"/> (75, 50 or 25).</summary>
        public static string MilestoneLine(int percent) => $"The pillar is at {percent.ToString(CultureInfo.InvariantCulture)}% strength.";

        /// <summary>The note a config carrying the retired showHealthBar key gets (the key is otherwise ignored).</summary>
        public const string RetiredShowHealthBarNote = "showHealthBar is retired and ignored: the pillar always presents as a friendly NPC with no health bar";

        /// <summary>How an enemy is engaged when it comes through, or when it is found idle.</summary>
        public enum EngageChoice
        {
            /// <summary>Nothing to do: it already turned on the pillar, or the defense is no longer running.</summary>
            None,

            /// <summary>Woken at the nearest living member inside the run.</summary>
            Member,

            /// <summary>No member to fight: it turns on the pillar now rather than stand at its portal.</summary>
            Pillar,
        }

        /// <summary>
        /// The engage rule (owner playtest ruling 2026-11-11: a spawned enemy engages at once and never idles): a woken
        /// enemy fights its member. Only with NO member inside the run does an enemy that has not switched yet turn on a
        /// standing pillar of an active defense; a member who is there but not targetable this tick leaves it idle for the
        /// monitor to retry, so it never loses its players-first window to a momentary miss. Pure.
        /// </summary>
        public static EngageChoice Engage(ThreadEncounterPortalWaves.WakeResult wake, bool alreadySwitched, bool viewActive, bool pillarStanding)
        {
            if (wake == ThreadEncounterPortalWaves.WakeResult.Woke)
                return EngageChoice.Member;

            if (wake != ThreadEncounterPortalWaves.WakeResult.NoMember)
                return EngageChoice.None;

            return !alreadySwitched && viewActive && pillarStanding ? EngageChoice.Pillar : EngageChoice.None;
        }

        public string Kind => KindName;

        /// <summary>Test seam: the object factory. Production is WorldObjectFactory.</summary>
        internal static Func<uint, WorldObject> CreateObject = wcid => WorldObjectFactory.CreateNewWorldObject(wcid);

        /// <summary>The shared portal-wave machinery, logging as this type under the "defense" label and creating through <see cref="CreateObject"/> (read at each call).</summary>
        private static readonly ThreadEncounterPortalWaves Waves = new ThreadEncounterPortalWaves(log, KindName, wcid => CreateObject(wcid));

        /// <summary>Test seam: the clock (unix seconds).</summary>
        internal static Func<double> Now = () => ACE.Common.Time.GetUnixTime();

        /// <summary>One enemy that came through a portal. Touched only on the pillar's landblock queue (under the state lock).</summary>
        internal sealed class DefenseEnemy
        {
            public DefenseEnemy(Creature creature, double spawnedAt)
            {
                Creature = creature;
                SpawnedAt = spawnedAt;
            }

            public Creature Creature { get; }
            public double SpawnedAt { get; }

            /// <summary>
            /// The switch was asked for (it may complete on the enemy's own landblock a moment later). Volatile: the engage
            /// fallback may set it from the enemy's own landblock thread; a duplicate switch is harmless (the tag is write-once).
            /// </summary>
            public volatile bool SwitchRequested;
        }

        /// <summary>The per-instance state. Released by OnRunEnded.</summary>
        internal sealed class DefenseState
        {
            public DefenseState(ThreadEncounterPlaceRequest request, DefenseConfig config, ThreadDefenseHoldView view, IReadOnlyList<DungeonSpawnPointDef> portalPoints)
            {
                Request = request;
                Config = config;
                View = view;
                PortalPoints = portalPoints ?? Array.Empty<DungeonSpawnPointDef>();
            }

            public readonly object Gate = new object();

            public DefenseConfig Config { get; }

            public ThreadDefenseHoldView View { get; }

            /// <summary>Where the portals open, chosen by Place (same landblock as the pillar).</summary>
            public IReadOnlyList<DungeonSpawnPointDef> PortalPoints { get; }

            /// <summary>The pillar's guid. The instance also tracks portals and enemies, and using one of THEM is not a pillar use.</summary>
            public uint PillarGuid;

            public Creature Pillar;

            /// <summary>The Place request (run, the pillar's landblock, context, point). Null once the run ended.</summary>
            public volatile ThreadEncounterPlaceRequest Request;

            // Under Gate.
            public bool Started;
            public bool Finished;
            public double StartedAt;
            public int Pending;
            public int LastMilestone = 100;
            public readonly List<DefenseEnemy> Enemies = new List<DefenseEnemy>();
            public readonly List<WorldObject> PortalObjects = new List<WorldObject>();
        }

        // ---- config ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Parses a defense row's config. An absent or empty config yields the defaults. A config that is not an object, or
        /// any bad key, is an error (null with <paramref name="error"/>): a defense with a broken portal or stream block is
        /// not placed at all. A bad wcid entry is skipped and noted in <paramref name="problems"/>, so long as one is left;
        /// the retired showHealthBar key is ignored and noted there too. Pure.
        /// </summary>
        public static DefenseConfig ParseConfig(JsonElement config, List<string> problems, out string error)
        {
            error = null;
            problems ??= new List<string>();

            if (config.ValueKind == JsonValueKind.Undefined || config.ValueKind == JsonValueKind.Null)
                return new DefenseConfig();

            if (config.ValueKind != JsonValueKind.Object)
            {
                error = $"config must be an object, not {config.ValueKind}";
                return null;
            }

            IReadOnlyList<uint> wcids = DefaultWcids;

            if (config.TryGetProperty("wcids", out var w))
            {
                if (w.ValueKind != JsonValueKind.Array)
                {
                    error = "config.wcids must be an array of positive whole numbers";
                    return null;
                }

                var parsed = new List<uint>();
                var i = 0;

                foreach (var e in w.EnumerateArray())
                {
                    var label = $"wcids[{i++}]";

                    if (e.ValueKind != JsonValueKind.Number || !e.TryGetUInt32(out var wcid) || wcid == 0)
                    {
                        problems.Add($"{label}: not a positive whole number; skipped");
                        continue;
                    }

                    if (parsed.Contains(wcid))
                    {
                        problems.Add($"{label}: {wcid} repeated; skipped");
                        continue;
                    }

                    parsed.Add(wcid);
                }

                if (parsed.Count == 0)
                {
                    error = "config.wcids has no usable wcid";
                    return null;
                }

                wcids = parsed;
            }

            if (!TryReadIntRange(config, "portals", 1, 3, 1, DefenseConfig.MaxPortals, out var portalsMin, out var portalsMax, out error))
                return null;

            if (!TryReadIntRange(config, "perPortal", 3, 5, 1, DefenseConfig.MaxPerPortal, out var perMin, out var perMax, out error))
                return null;

            if (!TryReadDoubleRange(config, "staggerSeconds", 2.0, 4.0, 0.0, DefenseConfig.MaxStaggerSeconds, out var staggerMin, out var staggerMax, out error))
                return null;

            if (!TryReadNumber(config, "portalMinDistance", 8.0, out var minDist) || !TryReadNumber(config, "portalMaxDistance", 25.0, out var maxDist)
                || minDist < 0 || maxDist <= minDist || maxDist > DefenseConfig.MaxPortalDistance)
            {
                error = $"config.portalMinDistance and portalMaxDistance must be numbers with 0 <= min < max <= {DefenseConfig.MaxPortalDistance.ToString(CultureInfo.InvariantCulture)}";
                return null;
            }

            // Retired (owner playtest ruling 2026-11-11): a shown health bar sent the pillar to the client as attackable.
            if (config.TryGetProperty("showHealthBar", out _))
                problems.Add(RetiredShowHealthBarNote);

            double scaleDungeon = 0.3, scaleOutdoor = 0.6;

            if (config.TryGetProperty("portalScale", out var scale))
            {
                if (scale.ValueKind != JsonValueKind.Object
                    || !TryReadNumber(scale, "dungeon", 0.3, out scaleDungeon) || !TryReadNumber(scale, "outdoor", 0.6, out scaleOutdoor)
                    || !(scaleDungeon > 0) || !(scaleOutdoor > 0) || scaleDungeon > DefenseConfig.MaxPortalScale || scaleOutdoor > DefenseConfig.MaxPortalScale)
                {
                    error = $"config.portalScale must be {{ dungeon, outdoor }} with each above 0 and at most {DefenseConfig.MaxPortalScale.ToString(CultureInfo.InvariantCulture)}";
                    return null;
                }
            }

            return new DefenseConfig
            {
                Wcids = wcids,
                PortalsMin = portalsMin,
                PortalsMax = portalsMax,
                PerPortalMin = perMin,
                PerPortalMax = perMax,
                StaggerMin = staggerMin,
                StaggerMax = staggerMax,
                PortalMinDistance = (float)minDist,
                PortalMaxDistance = (float)maxDist,
                PortalScaleDungeon = scaleDungeon,
                PortalScaleOutdoor = scaleOutdoor,
            };
        }

        private static bool TryReadIntRange(JsonElement config, string name, int defMin, int defMax, int floor, int ceiling, out int min, out int max, out string error)
        {
            min = defMin;
            max = defMax;
            error = null;

            if (!config.TryGetProperty(name, out var block))
                return true;

            if (block.ValueKind != JsonValueKind.Object || !TryReadInt(block, "min", defMin, out min) || !TryReadInt(block, "max", defMax, out max)
                || min < floor || max < min || max > ceiling)
            {
                error = $"config.{name} must be {{ min, max }} with {floor} <= min <= max <= {ceiling}";
                return false;
            }

            return true;
        }

        private static bool TryReadDoubleRange(JsonElement config, string name, double defMin, double defMax, double floor, double ceiling, out double min, out double max, out string error)
        {
            min = defMin;
            max = defMax;
            error = null;

            if (!config.TryGetProperty(name, out var block))
                return true;

            if (block.ValueKind != JsonValueKind.Object || !TryReadNumber(block, "min", defMin, out min) || !TryReadNumber(block, "max", defMax, out max)
                || !double.IsFinite(min) || !double.IsFinite(max) || min < floor || max < min || max > ceiling)
            {
                error = $"config.{name} must be {{ min, max }} with {floor.ToString(CultureInfo.InvariantCulture)} <= min <= max <= {ceiling.ToString(CultureInfo.InvariantCulture)}";
                return false;
            }

            return true;
        }

        private static bool TryReadInt(JsonElement obj, string name, int fallback, out int value)
        {
            value = fallback;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value);
        }

        private static bool TryReadNumber(JsonElement obj, string name, double fallback, out double value)
        {
            value = fallback;

            if (!obj.TryGetProperty(name, out var e))
                return true;

            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value) && double.IsFinite(value);
        }

        // ---- pure planning ---------------------------------------------------------------------------------------

        /// <summary>One draw of the pillar's look from <paramref name="wcids"/>. 0 for an empty list. One rng draw.</summary>
        public static uint PickVariant(IReadOnlyList<uint> wcids, Random rng)
            => wcids == null || wcids.Count == 0 || rng == null ? 0u : wcids[rng.Next(wcids.Count)];

        /// <summary>
        /// Where the portals open around the pillar (<see cref="ThreadEncounterPortalWaves.PickPortals"/>, the refusal
        /// naming the pillar). Pure over <paramref name="rng"/>.
        /// </summary>
        public static List<DungeonSpawnPointDef> PickPortals(Vector3 pillar, ushort landblock, IReadOnlyList<PortalWaveCandidate> candidates,
            bool isOutdoor, DefenseConfig config, Random rng, out string refusal)
            => ThreadEncounterPortalWaves.PickPortals(pillar, landblock, candidates, isOutdoor, config, rng, "pillar", out refusal);

        /// <summary>
        /// The picker-time prefilter (review R1, 2026-11-11): a point where Place's <see cref="PickPortals"/> must refuse is
        /// rejected before the slot is spent, so the slot goes to another type or point. Counts the run's curated points
        /// on the candidate's landblock (open-ground ones outdoors, as NearbyPoints offers), the candidate itself excluded,
        /// whose straight-line distance is within [portalMinDistance, portalMaxDistance], and rejects below portals.min.
        /// The dungeon walk-ratio test is not applied here (no walk graph per candidate at pick time), so this count is an
        /// upper bound on PickPortals': it never rejects a point Place would accept. Pure.
        /// </summary>
        public string RejectPointAmong(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, bool isOutdoor)
        {
            var config = ParseConfig(def?.Config ?? default, null, out var error);

            if (config == null)
                return "defense: " + error;

            return Waves.RejectPointAmong(candidate, runPoints, isOutdoor, config);
        }

        /// <summary>
        /// The prefilter with Place's whole portal rule: <see cref="RejectPointAmong"/>, then Place's own candidates
        /// (walked distances included) through the walk-ratio test Place's <see cref="PickPortals"/> applies
        /// (ThreadEncounterPortalWaves.RejectPointInRun). Pure over the walk graph seam.
        /// </summary>
        public string RejectPointInRun(ThreadEncounterTypeDef def, ThreadEncounterCandidate candidate, IReadOnlyList<ThreadEncounterCandidate> runPoints, ThreadDungeonRun run)
        {
            var isOutdoor = run?.Dungeon?.IsOutdoor == true;
            var config = ParseConfig(def?.Config ?? default, null, out var error);

            if (config == null)
                return "defense: " + error;

            return Waves.RejectPointInRun(run, candidate, runPoints, isOutdoor, config, "pillar");
        }

        /// <summary>The <see cref="RejectPointAmong"/> count: run points that could hold a portal for a pillar at <paramref name="pillar"/>, by straight line (<see cref="ThreadEncounterPortalWaves.PortalPointsInReach"/>). Pure.</summary>
        public static int PortalPointsInReach(ThreadEncounterCandidate pillar, IReadOnlyList<ThreadEncounterCandidate> runPoints, bool isOutdoor, DefenseConfig config)
            => ThreadEncounterPortalWaves.PortalPointsInReach(pillar, runPoints, isOutdoor, config);

        /// <summary>The stream through the portals (<see cref="ThreadEncounterPortalWaves.PlanStream"/>). Pure over <paramref name="rng"/>.</summary>
        public static List<PortalWaveSpawn> PlanStream(IReadOnlyList<DungeonSpawnPointDef> portals, IReadOnlyList<DungeonSpawnPlanEntry> planEntries,
            DefenseConfig config, Random rng)
            => ThreadEncounterPortalWaves.PlanStream(portals, planEntries, config, rng);

        /// <summary>
        /// The switch rule: an enemy turns on the pillar once it has lived <paramref name="switchSeconds"/> since it came
        /// through, only while the defense is active, only once, and never when dead. Pure.
        /// </summary>
        public static bool ShouldSwitch(bool active, bool alreadyRequested, bool dead, double spawnedAt, double now, double switchSeconds)
            => active && !alreadyRequested && !dead && double.IsFinite(now) && double.IsFinite(spawnedAt) && now - spawnedAt >= switchSeconds;

        /// <summary>
        /// The outcome table: the pillar down is a failure (first); with the pillar standing, the stream finished and no
        /// enemy alive is a success; still standing at <paramref name="maxSeconds"/> is a timeout (a success); else none. Pure.
        /// </summary>
        public static DefenseOutcome Decide(bool pillarStanding, bool streamDone, int enemiesAlive, double elapsed, double maxSeconds)
            => ThreadEncounterPortalWaves.Decide(pillarStanding, streamDone, enemiesAlive, elapsed, maxSeconds) switch
            {
                PortalWaveOutcome.AnchorLost => DefenseOutcome.Failure,
                PortalWaveOutcome.Cleared => DefenseOutcome.Success,
                PortalWaveOutcome.TimedOut => DefenseOutcome.Timeout,
                _ => DefenseOutcome.None,
            };

        /// <summary>
        /// The milestone to announce now: the LOWEST of 75/50/25 that is below <paramref name="lastSent"/> (100 before any)
        /// and at or above the pillar's health percent; 0 for none. A drop past two milestones in one tick says only the
        /// lower one. Pure.
        /// </summary>
        public static int NextMilestone(int lastSent, double healthFraction)
        {
            if (!double.IsFinite(healthFraction))
                return 0;

            var percent = healthFraction * 100.0;
            var result = 0;

            foreach (var m in MilestonePercents)
                if (m < lastSent && percent <= m)
                    result = m;

            return result;
        }

        // ---- IThreadEncounterType -------------------------------------------------------------------------------

        public bool Place(ThreadEncounterPlaceRequest request, out string failure)
        {
            failure = null;

            var run = request.Run;
            var problems = new List<string>();
            var config = ParseConfig(request.Def?.Config ?? default, problems, out var error);

            foreach (var problem in problems)
                run.RecordEncounterOutcome($"{request.Def?.Id}: {problem}");

            if (config == null)
            {
                failure = error;
                return false;
            }

            if (ThreadEncounterTrash.Pool(request.Context?.Plan?.Entries).Count == 0)
            {
                failure = "the run's plan has no trash to send through the portals";
                return false;
            }

            var isOutdoor = run.Dungeon?.IsOutdoor == true;
            var rng = new Random(unchecked(request.Seed * 31 + 0x0DEF));
            var portals = PickPortals(request.Point.Global, request.Point.Landblock, Waves.PortalCandidates(request, config, isOutdoor), isOutdoor, config, rng, out var refusal);

            if (portals == null)
            {
                failure = refusal;
                return false;
            }

            var wcid = PickVariant(config.Wcids, rng);
            var wo = CreateObject(wcid);

            if (!(wo is Creature pillar))
            {
                try { wo?.Destroy(); } catch { }
                failure = wo == null ? $"wcid {wcid} did not create - is its weenie in the world database?" : $"wcid {wcid} is not a creature";
                return false;
            }

            pillar.Location = request.PositionOf(request.Point.Point);
            pillar.TimeToRot = -1;
            pillar.IsTransientSpawn = true;

            var view = new ThreadDefenseHoldView(ThreadDefenseHoldView.BuildTuning(run.DefenseUndefendedSeconds, run.DefenseMinDeathSeconds));

            // The tag before EnterWorld: the physics layer caches the knight run id while the pillar enters.
            if (!ThreadDefenseHold.PreparePillar(pillar, view, ThreadDefenseHold.PillarMaxHealth))
            {
                try { pillar.Destroy(); } catch { }
                failure = $"wcid {wcid} could not be prepared as the pillar";
                return false;
            }

            request.Instance.State = new DefenseState(request, config, view, portals) { PillarGuid = pillar.Guid.Full, Pillar = pillar };

            // Before EnterWorld: a use can arrive the moment it is visible.
            request.Instance.Track(pillar);

            bool entered;

            try
            {
                entered = pillar.EnterWorld();
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {run} defense pillar EnterWorld threw at {pillar.Location?.ToLOCString()}", ex);
                entered = false;
            }

            if (!entered)
            {
                pillar.P_ThreadEncounter = null;
                try { pillar.Destroy(); } catch { }
                request.Instance.State = null;
                failure = $"wcid {wcid} failed to enter the world at {pillar.Location?.ToLOCString()}";
                return false;
            }

            run.RecordEncounterOutcome(string.Format(CultureInfo.InvariantCulture, "{0}: pillar {1} ({2}), {3} portal point(s), view run 0x{4:X8}",
                request.Def?.Id, wcid, pillar.Name, portals.Count, view.RunId));

            return true;
        }

        public void OnRunEnded(ThreadEncounterInstance instance, string why)
        {
            if (instance?.State is DefenseState state)
            {
                state.View.Deactivate();
                state.Request = null;
            }
        }

        // ---- IThreadEncounterUsable ------------------------------------------------------------------------------

        /// <summary>Is <paramref name="used"/> the pillar this state belongs to (not a portal layer or an enemy)? Pure.</summary>
        internal static bool IsThePillar(DefenseState state, WorldObject used)
            => state != null && used != null && state.PillarGuid != 0 && used.Guid.Full == state.PillarGuid;

        /// <summary>The use latch: true exactly once per defense (the use that starts it). Under the state lock.</summary>
        internal static bool TryStart(DefenseState state, double now)
        {
            lock (state.Gate)
            {
                if (state.Started || state.Finished || state.Request == null)
                    return false;

                state.Started = true;
                state.StartedAt = now;
                state.View.Activate();
                return true;
            }
        }

        public void OnUse(ThreadEncounterInstance instance, WorldObject used, Player player)
        {
            if (!(instance?.State is DefenseState state) || player == null)
                return;

            // The instance tracks the portals and the enemies too: only a use of the pillar itself starts anything.
            if (!IsThePillar(state, used))
                return;

            var run = instance.Run;
            var request = state.Request;

            if (request == null || run.State == ThreadDungeonRunState.Ended || run.EncountersEnded)
                return;

            if (!ThreadRunPresence.IsMemberInside(run, player))
            {
                // Refused without spending the pillar: a visitor or a member the puzzle policy removed.
                Send(player, RefusedLine);
                return;
            }

            if (!TryStart(state, Now()))
                return;

            foreach (var recipient in ThreadRunPresence.MessageRecipients(run))
                Send(recipient, StartLine);

            run.RecordEncounterOutcome($"{instance}: woken by {player.Name}");
            log.Info($"[DYNDUNGEON] {run} {instance} defense woken by {player.Name}");

            Schedule(instance, state, request);
        }

        private static void Send(Player player, string line)
        {
            if (!string.IsNullOrEmpty(line))
                player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(line, ChatMessageType.Broadcast));
        }

        private static void SendAll(ThreadDungeonRun run, string line)
        {
            foreach (var recipient in ThreadRunPresence.MessageRecipients(run))
                Send(recipient, line);
        }

        // ---- the defense, on the pillar's landblock queue ---------------------------------------------------------

        /// <summary>Queues the portals, every planned enemy at its delay, and the first monitor tick, all onto the pillar's landblock.</summary>
        private static void Schedule(ThreadEncounterInstance instance, DefenseState state, ThreadEncounterPlaceRequest request)
        {
            var landblock = request.Landblock;

            if (landblock == null)
                return;

            var rng = new Random(unchecked(request.Seed * 17 + 0x57E4));
            var stream = PlanStream(state.PortalPoints, request.Context?.Plan?.Entries, state.Config, rng);

            lock (state.Gate)
                state.Pending = stream.Count;
            var open = new ActionChain();
            open.AddAction(landblock, () => OpenPortals(instance, state));
            open.EnqueueChain();

            foreach (var spawn in stream)
            {
                var chain = new ActionChain();
                chain.AddDelaySeconds(spawn.Delay);
                chain.AddAction(landblock, () => SpawnEnemy(instance, state, spawn));
                chain.EnqueueChain();
            }

            ScheduleTick(instance, state, landblock);
        }

        private static void ScheduleTick(ThreadEncounterInstance instance, DefenseState state, ACE.Server.Entity.Landblock landblock)
        {
            var chain = new ActionChain();
            chain.AddDelaySeconds(MonitorSeconds);
            chain.AddAction(landblock, () => Tick(instance, state, landblock));
            chain.EnqueueChain();
        }

        private static bool RunOver(ThreadEncounterInstance instance, DefenseState state)
        {
            var run = instance.Run;
            return state.Request == null || run.State == ThreadDungeonRunState.Ended || run.EncountersEnded;
        }

        /// <summary>Pillar landblock queue: the Sky Rift layers at every portal point, facing the pillar. Never throws.</summary>
        private static void OpenPortals(ThreadEncounterInstance instance, DefenseState state)
        {
            try
            {
                var request = state.Request;

                if (request == null || RunOver(instance, state))
                    return;

                var isOutdoor = request.Run.Dungeon?.IsOutdoor == true;
                var pillarAt = request.Point.Global;
                var scale = state.Config.PortalScale(isOutdoor);

                // Laid out as Hold ring portals around the pillar; each layer that entered is kept for the collapse.
                var opened = Waves.OpenPortalLayers(instance, request, pillarAt, state.PortalPoints, scale, isOutdoor, wo =>
                {
                    lock (state.Gate)
                        state.PortalObjects.Add(wo);
                });

                instance.Run.RecordEncounterOutcome($"{instance}: {state.PortalPoints.Count} portal(s) opened ({opened} layer(s), scale {scale.ToString("0.##", CultureInfo.InvariantCulture)})");
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {instance} portals threw", ex);
            }
        }

        /// <summary>Pillar landblock queue: one planned enemy comes through its portal. Never throws.</summary>
        private static void SpawnEnemy(ThreadEncounterInstance instance, DefenseState state, PortalWaveSpawn spawn)
        {
            Creature placed = null;
            DefenseEnemy added = null;

            try
            {
                var request = state.Request;

                // The finally below counts this planned enemy as done on every path, placed or not.
                lock (state.Gate)
                {
                    if (state.Finished)
                        return;
                }

                if (request == null || RunOver(instance, state) || spawn.Entry == null)
                    return;

                // Every encounter enemy is ExcludedFromClear: its kills never count toward the clear.
                placed = Waves.TrySpawnEnemy(request, spawn.Entry, excludedFromClear: true);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {instance} defense enemy threw", ex);
            }
            finally
            {
                lock (state.Gate)
                {
                    state.Pending = Math.Max(0, state.Pending - 1);

                    if (placed != null)
                    {
                        added = new DefenseEnemy(placed, Now());
                        state.Enemies.Add(added);
                    }

                    state.View.PublishMonsters(state.Enemies.Where(e => IsStanding(e.Creature)).Select(e => e.Creature));
                }
            }

            // Engaged the moment it is through (owner playtest ruling 2026-11-11), whatever the portal's distance: this queue
            // is its landblock (TrySpawnEnemy refuses any other), so it runs here and now. Outside the lock, as the tick's.
            if (added != null)
            {
                try
                {
                    EngageEnemy(added, state, instance.Run);
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {instance.Run} {instance} defense enemy engage threw", ex);
                }
            }
        }

        /// <summary>
        /// Still in the fight: not dead and not destroyed. An enemy removed without dying (an admin delete, a landblock
        /// reap) is no longer standing, so the success outcome stays reachable (owner ruling 2026-11-09). Also the pillar's
        /// test. Any thread (two field reads).
        /// </summary>
        internal static bool IsStanding(Creature c) => ThreadEncounterPortalWaves.IsStanding(c);

        /// <summary>Pillar landblock queue, once a second: wake, switch, milestones, the outcome. Reschedules itself until an outcome or the run's end. Never throws.</summary>
        private static void Tick(ThreadEncounterInstance instance, DefenseState state, ACE.Server.Entity.Landblock landblock)
        {
            try
            {
                var request = state.Request;

                if (request == null || RunOver(instance, state))
                {
                    state.View.Deactivate();
                    return;
                }

                var run = instance.Run;
                var now = Now();
                var pillar = state.Pillar;
                var pillarStanding = IsStanding(pillar);
                List<DefenseEnemy> alive;
                bool streamDone;
                double elapsed;

                lock (state.Gate)
                {
                    if (state.Finished)
                        return;

                    alive = state.Enemies.Where(e => IsStanding(e.Creature)).ToList();
                    streamDone = state.Pending <= 0;
                    elapsed = now - state.StartedAt;
                }

                state.View.PublishMonsters(alive.Select(e => e.Creature));

                foreach (var enemy in alive)
                {
                    var creature = enemy.Creature;

                    if (pillarStanding && ShouldSwitch(state.View.IsActive, enemy.SwitchRequested, creature.IsDead, enemy.SpawnedAt, now, run.DefenseSwitchSeconds))
                    {
                        // Latched only once the switch has run or been queued: an enemy with no landblock this tick is
                        // asked again on the next one instead of never switching.
                        if (OnEnemyThread(creature, landblock, () =>
                        {
                            if (ThreadDefenseHold.SwitchToSeeker(creature, state.View) && log.IsDebugEnabled)
                                log.Debug($"[DYNDUNGEON] {run} defense enemy {creature.Name} 0x{creature.Guid.Full:X8} turns on the pillar");
                        }))
                            enemy.SwitchRequested = true;
                    }
                    else if (creature.MonsterState == Creature.State.Idle)
                    {
                        var idle = enemy;
                        OnEnemyThread(creature, landblock, () => EngageEnemy(idle, state, run));
                    }
                }

                if (pillarStanding && pillar.Health.MaxValue > 0)
                {
                    int milestone;

                    lock (state.Gate)
                    {
                        milestone = NextMilestone(state.LastMilestone, pillar.Health.Current / (double)pillar.Health.MaxValue);

                        if (milestone > 0)
                            state.LastMilestone = milestone;
                    }

                    if (milestone > 0)
                        SendAll(run, MilestoneLine(milestone));
                }

                var outcome = Decide(pillarStanding, streamDone, alive.Count, elapsed, run.DefenseMaxSeconds);

                if (outcome != DefenseOutcome.None)
                {
                    Finish(instance, state, landblock, outcome, alive.Count);
                    return;
                }

                ScheduleTick(instance, state, landblock);
            }
            catch (Exception ex)
            {
                log.Error($"[DYNDUNGEON] {instance.Run} {instance} defense tick threw; the defense stops here", ex);
                state.View.Deactivate();
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the enemy's own landblock: here when it is the pillar's, else queued onto its
        /// own. False, running and queueing nothing, when the enemy is on no landblock right now.
        /// </summary>
        private static bool OnEnemyThread(Creature creature, ACE.Server.Entity.Landblock pillarLandblock, Action action)
        {
            var current = creature.CurrentLandblock;

            if (current == null)
                return false;

            if (ReferenceEquals(current, pillarLandblock))
            {
                action();
                return true;
            }

            new ActionChain(creature, action).EnqueueChain();
            return true;
        }

        /// <summary>
        /// The enemy's landblock thread, as it comes through and whenever the monitor finds it idle: it is woken at a
        /// member (<see cref="ThreadEncounterPortalWaves.EngageEnemy"/>), or, with no member inside the run at all, turned on the pillar now
        /// (<see cref="Engage"/>), so it never stands at its portal. A member who is inside but not targetable this moment
        /// leaves it idle for the next monitor tick, so the 20 s player-first window is kept whenever a member is there.
        /// </summary>
        private static void EngageEnemy(DefenseEnemy enemy, DefenseState state, ThreadDungeonRun run)
        {
            var creature = enemy?.Creature;

            // Not standing, no physics object or not idle: nothing to wake.
            if (!ThreadEncounterPortalWaves.EngageEnemy(creature, run, out var wake))
                return;

            var choice = Engage(wake, enemy.SwitchRequested || ThreadDefenseHold.IsTagged(creature), state.View.IsActive, IsStanding(state.Pillar));

            if (choice != EngageChoice.Pillar)
                return;

            enemy.SwitchRequested = true;

            if (ThreadDefenseHold.SwitchToSeeker(creature, state.View) && log.IsDebugEnabled)
                log.Debug($"[DYNDUNGEON] {run} defense enemy {creature.Name} 0x{creature.Guid.Full:X8} has no member to fight and turns on the pillar");
        }

        /// <summary>Pillar landblock queue: the outcome. Ends the view, collapses the portals, says the line, calls the reward hook, and destroys the pillar a moment later.</summary>
        private static void Finish(ThreadEncounterInstance instance, DefenseState state, ACE.Server.Entity.Landblock landblock, DefenseOutcome outcome, int enemiesLeft)
        {
            List<WorldObject> portals;

            lock (state.Gate)
            {
                if (state.Finished)
                    return;

                state.Finished = true;
                portals = state.PortalObjects.ToList();
                state.PortalObjects.Clear();
            }

            // Every leftover enemy stays and fights as an ordinary bonus mob: a seeker of an inactive view retargets as normal.
            state.View.Deactivate();

            foreach (var portal in portals)
            {
                try
                {
                    if (!portal.IsDestroyed)
                        portal.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {instance.Run} {instance} portal destroy threw", ex);
                }
            }

            var run = instance.Run;

            SendAll(run, outcome == DefenseOutcome.Failure ? FailureLine : SuccessLine);

            run.RecordEncounterOutcome($"{instance}: {outcome} ({enemiesLeft} enemy(ies) still standing)");
            log.Info($"[DYNDUNGEON] {run} {instance} defense {outcome}, {enemiesLeft} enemy(ies) still standing");

            if (outcome != DefenseOutcome.Failure)
                OnDefenseSucceeded(instance, outcome);

            var pillar = state.Pillar;

            if (pillar == null)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(PillarDestroyDelaySeconds);
            chain.AddAction(landblock, () =>
            {
                try
                {
                    if (!pillar.IsDestroyed)
                        pillar.Destroy();
                }
                catch (Exception ex)
                {
                    log.Error($"[DYNDUNGEON] {run} {instance} pillar destroy threw", ex);
                }
            });
            chain.EnqueueChain();
        }

        /// <summary>
        /// THE reward hook. Called once, on the pillar's landblock queue, when a defense ends with the pillar standing
        /// (<paramref name="outcome"/> Success or Timeout). Every online member inside the run at this moment
        /// (ThreadRunPresence.OnlineMembersInside) gets one shared encounter prize (ThreadEncounterPrize.Grant, owner
        /// ruling 2026-11-09), which delivers on each winner's own landblock thread.
        /// </summary>
        internal static void OnDefenseSucceeded(ThreadEncounterInstance instance, DefenseOutcome outcome)
        {
            var run = instance?.Run;

            if (run == null)
                return;

            var granted = 0;

            foreach (var winner in ThreadRunPresence.OnlineMembersInside(run))
            {
                if (ThreadEncounterPrize.Grant(run, winner, KindName))
                    granted++;
            }

            run.RecordEncounterOutcome($"{instance}: reward hook ({outcome}); {granted} prize(s) granted");
            log.Info($"[DYNDUNGEON] {run} {instance} defense succeeded ({outcome}); {granted} prize(s) granted");
        }
    }
}
