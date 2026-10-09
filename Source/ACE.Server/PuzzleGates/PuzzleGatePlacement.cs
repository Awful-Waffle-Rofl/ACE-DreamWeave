using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using ACE.Server.WorldObjects;

using Position = ACE.Entity.Position;

namespace ACE.Server.PuzzleGates
{
    public enum PuzzlePlacementState
    {
        /// <summary>Registered; the initial spawn is queued on the landblock and has not run yet.</summary>
        Spawning,

        /// <summary>Gate and round objects are in the world and accepting activations.</summary>
        Live,

        /// <summary>The gate has opened. Levers stay and answer with the "spent" line.</summary>
        Solved,

        /// <summary>A spawn failed; whatever entered the world is being destroyed.</summary>
        Failed,

        /// <summary>Removed by clear, reroll or reap. Every queued action on it is a no-op.</summary>
        Cleared,
    }

    /// <summary>What a lever activation means for its placement, decided before anything is touched.</summary>
    public enum PuzzleActivationRoute
    {
        /// <summary>The placement is not accepting activations (still spawning, failed or cleared).</summary>
        NotLive,

        /// <summary>
        /// The object is not a lever of the CURRENT round: a lever from a round a reshuffle already retired,
        /// whose activation was queued before that reshuffle ran. Ignored, never scored.
        /// </summary>
        StaleRound,

        /// <summary>A lever of the current round; the slot says which.</summary>
        Current,
    }

    /// <summary>
    /// One placed puzzle gate. The ANSWER lives only here (fan-out invariant 1): no world object carries it.
    /// <para/>
    /// THREADING. World objects and round state are only mutated on the placement's own landblock action
    /// queue (PuzzleGateManager enqueues every mutation there). The command thread reads state for
    /// list / reveal / clear and flips <see cref="State"/> to Cleared; both sides take <see cref="Sync"/>.
    /// The landblock side holds it for a whole activation, the command side only for short reads, and
    /// nothing else is locked while it is held, so there is no lock ordering to get wrong.
    /// <para/>
    /// The routing and reap decisions are pure (guids, enums and times only) so they are unit-tested
    /// without a world; see PuzzleGatePlacementTests.
    /// </summary>
    public sealed class PuzzleGatePlacement
    {
        public readonly object Sync = new object();

        public int Id { get; }

        public PuzzleGateOptions Options { get; }

        public int Seed { get; }

        public PuzzleGateType Type => Options.Type;

        /// <summary>Copy of the admin's position at place time (cell, instance, rotation).</summary>
        public Position Anchor { get; }

        public float AnchorYawDeg { get; }

        /// <summary>Recorded shuffle spots (landblock-local) captured at place time; reused by reroll.</summary>
        public IReadOnlyList<Vector3> RecordedSpots { get; }

        /// <summary>Guid of the admin who placed it, for the deferred spawn report.</summary>
        public uint PlacedBy { get; }

        /// <summary>The live landblock object the placement was queued on. Typed loosely so tests need no world.</summary>
        public object Landblock { get; }

        public PuzzlePlacementState State { get; private set; } = PuzzlePlacementState.Spawning;

        public PuzzleGateGenerator Generator { get; set; }

        public PuzzleGateRules Rules { get; }

        public PuzzlePlan Plan { get; private set; }

        /// <summary>Bumped by every <see cref="BeginRound"/>; a diagnostic only, routing keys on guids.</summary>
        public int RoundGeneration { get; private set; }

        /// <summary>Who owns the placement (admin or a Thread run). Never null.</summary>
        public IPuzzleGateHost Host { get; }

        /// <summary>The gate model: which wcid(s) the gate role spawns and how a solve removes them. Never null.</summary>
        public PuzzleGateModel GateModel { get; }

        /// <summary>Per-site layout; null means the admin defaults (PuzzleLayoutParams.Default).</summary>
        public PuzzleLayoutParams Layout { get; }

        // ---- world objects (landblock thread only) ----

        /// <summary>The primary gate object (the first barrier panel, for a barrier). Also in <see cref="GateParts"/>.</summary>
        public WorldObject Gate;

        /// <summary>Every gate object: one for a door or a focal object, N panels for a barrier.</summary>
        public readonly List<WorldObject> GateParts = new List<WorldObject>();

        /// <summary>
        /// True once a solve DESTROYED the gate (PuzzleGateSolveAction.Destroy). From then on a missing gate is the
        /// expected state, not a sign of an outside removal, so <see cref="GateMissing"/> looks at the levers instead.
        /// </summary>
        public bool GateRemovedBySolve;

        private int removalNotified;

        /// <summary>
        /// The curated site id this placement was built from (a run placement, or an admin /puzzlegate site one);
        /// null for a plain /puzzlegate place. Diagnostic only: it reaches the [PUZZLE_GATE] and [PUZZLE_POLICY] log lines.
        /// </summary>
        public string SiteId { get; set; }

        /// <summary>
        /// 0-based slot of the lever pulled by the activation being handled, set under <see cref="Sync"/> by the
        /// manager before the host callbacks run; -1 before any pull. A diagnostic read by <see cref="LogTag"/>
        /// (the host callbacks run on the same landblock thread right after, so no other pull can interleave).
        /// This relies on the host and the fail sink running SYNCHRONOUSLY on the placement's landblock thread: a sink
        /// that deferred its work to another thread or a later tick could read a later pull's slot.
        /// </summary>
        public int LastPullSlot { get; set; } = -1;

        /// <summary>
        /// The fields that tie a log line to one puzzle: "placement=3 site=filos_doom type=sigil slot=2" (slot is
        /// the 1-based lever just pulled, "-" when none). Site is "-" for a plain admin placement.
        /// </summary>
        public string LogTag()
        {
            return string.Format(CultureInfo.InvariantCulture, "placement={0} site={1} type={2} slot={3}",
                Id, SiteId ?? "-", Type.ToString().ToLowerInvariant(), LastPullSlot >= 0 ? (LastPullSlot + 1).ToString(CultureInfo.InvariantCulture) : "-");
        }

        /// <summary>
        /// The world-object half of the reap rule, read under <see cref="Sync"/>: is the placement's gate gone from
        /// the world by something other than its own solve? A placement whose solve destroyed its gate counts as
        /// missing only once its levers are gone too (a landblock reload), so a solved barrier is not reaped the
        /// moment its panels fade.
        /// </summary>
        public bool GateMissing()
        {
            if (GateRemovedBySolve)
            {
                foreach (var wo in RoundObjects)
                    if (wo != null && !wo.IsDestroyed)
                        return false;

                return true;
            }

            return Gate == null || Gate.IsDestroyed;
        }

        private int solveFinished;

        /// <summary>Set by PuzzleGateManager.FinishSolve once the host has heard the solve.</summary>
        public bool SolveFinished => System.Threading.Volatile.Read(ref solveFinished) != 0;

        public void MarkSolveFinished() => System.Threading.Interlocked.Exchange(ref solveFinished, 1);

        /// <summary>
        /// A solved scene that tears down on its solve (<see cref="PuzzleGateModel.TearsDownOnSolve"/>) whose host has
        /// not yet heard the solve. Its objects are already fading, so <see cref="GateMissing"/> is true, but no reap may
        /// take it: the solve's own follow-up (OnSolved, then FinishSolve) owns its removal. Read under <see cref="Sync"/>.
        /// </summary>
        public bool AwaitingSolveFinish => State == PuzzlePlacementState.Solved && GateModel.TearsDownOnSolve && !SolveFinished;

        /// <summary>True for the ONE caller that may report this placement's removal to its host.</summary>
        public bool TryClaimRemovalNotice() => System.Threading.Interlocked.Exchange(ref removalNotified, 1) == 0;

        /// <summary>Every answer-bearing object of the current round (levers, lights, indicator).</summary>
        public readonly List<WorldObject> RoundObjects = new List<WorldObject>();

        public readonly List<WorldObject> Ambush = new List<WorldObject>();

        private readonly Dictionary<uint, int> roundCandidates = new Dictionary<uint, int>();

        // ---- approach prompt (landblock thread only) ----

        /// <summary>Who has had the approach hint. A reroll builds a new placement, which re-arms everyone.</summary>
        public readonly PuzzlePromptTracker Prompts = new PuzzlePromptTracker();

        /// <summary>Landblock-local centre the prompt radius is measured from: the lever row's centre.</summary>
        public Vector3 PromptCentre { get; }

        // ---- telemetry ----

        public DateTime QueuedUtc { get; }

        public DateTime? LiveUtc { get; private set; }

        public DateTime? FirstActivationUtc { get; private set; }

        public DateTime? SolvedUtc { get; private set; }

        public DateTime LastReshuffleUtc { get; private set; }

        public string SolverName { get; private set; }

        public int Activations { get; private set; }

        public PuzzleGatePlacement(int id, PuzzleGateOptions options, int seed, Position anchor, float anchorYawDeg,
            IReadOnlyList<Vector3> recordedSpots, uint placedBy, object landblock, DateTime now,
            IPuzzleGateHost host = null, PuzzleGateModel gateModel = null, PuzzleLayoutParams layout = null)
        {
            Host = host ?? AdminPuzzleGateHost.Instance;
            GateModel = gateModel ?? PuzzleGateModel.Default;
            Layout = layout;
            Id = id;
            Options = options ?? throw new ArgumentNullException(nameof(options));
            Seed = seed;
            Anchor = anchor;
            AnchorYawDeg = anchorYawDeg;
            RecordedSpots = recordedSpots ?? Array.Empty<Vector3>();
            PlacedBy = placedBy;
            Landblock = landblock;
            QueuedUtc = now;
            LastReshuffleUtc = now;
            Rules = new PuzzleGateRules(options.Rounds, options.LockoutSeconds);

            if (anchor != null)
                PromptCentre = PuzzleGateGenerator.ToWorld(anchor.Pos, anchorYawDeg, new Vector3(0, PuzzleGateTunables.LeverRowDistance, 0));

            if (layout != null && !layout.TryValidate(out _))
                Layout = null;
        }

        // =============================================================================================
        // Pure state transitions
        // =============================================================================================

        /// <summary>
        /// Starts a round: from now on only <paramref name="candidates"/> route as <see cref="PuzzleActivationRoute.Current"/>.
        /// Every lever of the previous round becomes stale in the same step, which is what makes a reshuffle
        /// safe against an activation that was queued before it.
        /// </summary>
        public void BeginRound(PuzzlePlan plan, IEnumerable<(uint Guid, int Slot)> candidates, DateTime now)
        {
            Plan = plan;
            roundCandidates.Clear();

            foreach (var (guid, slot) in candidates)
                roundCandidates[guid] = slot;

            RoundGeneration++;
            LastReshuffleUtc = now;
        }

        /// <summary>Retires the current round's levers without starting a new one (the reshuffle's first half).</summary>
        public void RetireRound()
        {
            roundCandidates.Clear();
        }

        public void MarkLive(DateTime now)
        {
            if (State == PuzzlePlacementState.Spawning)
            {
                State = PuzzlePlacementState.Live;
                LiveUtc = now;
            }
        }

        public void MarkFailed()
        {
            if (State != PuzzlePlacementState.Cleared)
                State = PuzzlePlacementState.Failed;
        }

        public void MarkCleared() => State = PuzzlePlacementState.Cleared;

        public void MarkSolved(string solver, DateTime now)
        {
            State = PuzzlePlacementState.Solved;
            SolverName = solver;
            SolvedUtc = now;
        }

        public void RecordActivation(DateTime now)
        {
            Activations++;

            if (!FirstActivationUtc.HasValue)
                FirstActivationUtc = now;
        }

        /// <summary>
        /// Routes an activation. Pure: decided from the guid and the placement state only. A stale lever
        /// (one a reshuffle has already retired) is never scored, even when its queued activation runs after
        /// the reshuffle that replaced it.
        /// </summary>
        public PuzzleActivationRoute Route(uint guid, bool objectDestroyed, out int slot)
        {
            slot = -1;

            if (State != PuzzlePlacementState.Live && State != PuzzlePlacementState.Solved)
                return PuzzleActivationRoute.NotLive;

            if (objectDestroyed || !roundCandidates.TryGetValue(guid, out slot))
            {
                slot = -1;
                return PuzzleActivationRoute.StaleRound;
            }

            return PuzzleActivationRoute.Current;
        }

        public bool IsCorrect(int slot) => Plan != null && slot == Plan.AnswerSlot;

        /// <summary>
        /// True when the placement should be dropped from the registry. Pure.
        /// <list type="bullet">
        /// <item>Failed or Cleared: nothing left to keep.</item>
        /// <item>Live or Solved with the gate destroyed (or never set): something outside the manager
        /// (/reload-landblock, landblock unload, an admin @delete) removed it.</item>
        /// <item>Still Spawning after <see cref="PuzzleGateTunables.SpawnTimeoutSeconds"/>: the queued spawn
        /// never ran - an unload clears the landblock's action queue - so it never will.</item>
        /// </list>
        /// </summary>
        public static bool ShouldReap(PuzzlePlacementState state, bool gateMissingOrDestroyed, TimeSpan sinceQueued)
            => ShouldReap(state, gateMissingOrDestroyed, sinceQueued, false);

        /// <param name="awaitingSolveFinish"><see cref="AwaitingSolveFinish"/>: never reaped while true.</param>
        public static bool ShouldReap(PuzzlePlacementState state, bool gateMissingOrDestroyed, TimeSpan sinceQueued, bool awaitingSolveFinish)
        {
            if (awaitingSolveFinish)
                return false;

            switch (state)
            {
                case PuzzlePlacementState.Failed:
                case PuzzlePlacementState.Cleared:
                    return true;
                case PuzzlePlacementState.Spawning:
                    return sinceQueued > TimeSpan.FromSeconds(PuzzleGateTunables.SpawnTimeoutSeconds);
                default:
                    return gateMissingOrDestroyed;
            }
        }

        // =============================================================================================
        // Text (admin-facing and log only - never sent to a non-admin player)
        // =============================================================================================

        private static long Ms(DateTime? from, DateTime? to)
        {
            if (!from.HasValue || !to.HasValue)
                return -1;

            return (long)(to.Value - from.Value).TotalMilliseconds;
        }

        /// <summary>
        /// The INFO line for a terminal event. ms_first and ms_solve are measured from the moment the
        /// placement went live; -1 when the event never happened.
        /// </summary>
        public string TelemetryLine(string outcome, DateTime now)
        {
            var start = LiveUtc ?? QueuedUtc;

            return string.Format(CultureInfo.InvariantCulture,
                "[PUZZLE_GATE] id={0} type={1} seed={2} outcome={3} solver={4} ms_first={5} ms_solve={6} wrong={7} rounds={8}/{9}",
                Id, Type.ToString().ToLowerInvariant(), Seed, outcome, SolverName ?? "-",
                Ms(start, FirstActivationUtc), Ms(start, SolvedUtc), Rules.WrongCount, Rules.RoundsCompleted(now), Rules.Rounds) + SweepTag;
        }

        /// <summary>A puzzlegate-sweep fixture (its host is an IPuzzleSweepHost): tagged so log tooling can filter it out of telemetry.</summary>
        public bool IsSweep => Host is IPuzzleSweepHost;

        /// <summary>" sweep=1" on every [PUZZLE_GATE] line of a sweep fixture, "" otherwise.</summary>
        public string SweepTag => IsSweep ? " sweep=1" : "";

        public string Summary(DateTime now)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "#{0} {1} seed={2} {3} round {4}/{5} wrong={6} pulls={7} lb=0x{8:X8} inst=0x{9:X8}",
                Id, Type.ToString().ToLowerInvariant(), Seed, State.ToString().ToLowerInvariant(),
                Rules.RoundsCompleted(now), Rules.Rounds, Rules.WrongCount, Activations, Anchor?.LandblockId.Raw ?? 0, Anchor?.Instance ?? 0);
        }
    }
}
