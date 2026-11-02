using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Matron
{
    /// <summary>
    /// Where the Matron fight stands inside its framework phase (phase 1: Fighting..Sealed, phase 2: Finale*). Guarded is a
    /// threshold reached while her wave adds live: she is immune and still fights until they are dead.
    /// </summary>
    public enum MatronStep { Fighting, Guarded, Running, Opening, Flowing, Sealed, FinaleWalking, FinaleOpening, FinaleRace, FinaleLava }

    /// <summary>
    /// The Olthoi Matron boss script (Docs/Knots/MATRON-ENCOUNTER-DESIGN.md, plan T7). Pure logic behind
    /// <see cref="IPinnacleFightPort"/>: no WorldObject, PropertyManager, DateTime or new Random.
    ///
    /// TWO FRAMEWORK PHASES. Phase 1 is the whole four-flow loop as internal steps (Fighting -> Running -> Opening ->
    /// Flowing -> Sealed -> Fighting, four times), because the core destroys adds at every phase change and wave
    /// survivors carry over (design section 9). Phase 2 is the finale. A 1 s poll timer drives everything the port
    /// cannot raise as an event: the health threshold (the floor), her arrival, the owner's landing, the rate escalation.
    /// While she walks (Running, FinaleWalking) it runs every <see cref="WalkPollSeconds"/>.
    ///
    /// THE BROOD GATES EACH THRESHOLD (playtest round 6). The script tracks the handles of the wave adds it spawned (never
    /// the head or the rock). A threshold reached while any of them lives makes her immune there and then, but she does not
    /// run: she stays engaged in Guarded (<see cref="MatronLines.LineBroodGuards"/>, said again every
    /// <see cref="BroodReminderSeconds"/>; the hold has no timeout) and every poll checks the adds; once
    /// none lives she runs to the next pit, or after the fourth seal asks for the finale, exactly as an ungated threshold.
    /// The core destroys adds at a phase change, so the finale is never asked for while a sentinel stands. The tracking is
    /// cleared on every phase entry (the core cleared the adds) and kept by RestartPhase (the hold kept them).
    ///
    /// THE FLOW DELAY. A vent's acid starts <see cref="MatronSettings.FlowDelay"/> after its door opens (the flow-start
    /// timer); until then the flow's rate is 0, so a fill at the floor is not wanted and nothing is started, and the
    /// escalation counts from the delayed start.
    ///
    /// ARRIVAL. The live walk (Creature.MoveTo(Position)) stops when her collision sphere is within 0.6 m of the point, so
    /// her centre ends about 0.6 + her radius short of it; she has arrived inside <see cref="ArrivalRadius"/>, when her
    /// distance has stopped shrinking (a stall), or at <see cref="MoveTimeout"/>. She then turns in place to the point's
    /// heading (<see cref="IPinnacleFightPort.TurnTo"/>) and plays the opening motion when the turn is done.
    ///
    /// ONE FILL RUN. Every fill start goes through <see cref="StartFill"/>, which stops the tag first, so at most one run
    /// (30 tiles, 60 panels: the 64-panel cap) is ever live. The only place a rate is SENT is <see cref="ApplyRate"/>.
    /// The poll is the single RECONCILER (<see cref="ReconcileFill"/>): the core's one-shot retry relaunches a run with its
    /// original args (start_z, rate, damage_scale), so every poll re-sends the current rate and scale, snaps an unexplained
    /// fall back, and restarts a wanted fill that is not running (cooldown and per-phase cap), so an unsealed flow can
    /// never be left without acid.
    ///
    /// DEATH AND RETURN (owner Q1, section 10). The core's hold stops every hazard and keeps every prop, timer, add and
    /// health; RestartPhase restarts the fill at the checkpoint (the level when the current cycle's threshold fired,
    /// or the floor after a drain), never more than <see cref="MatronSettings.ReturnMaxDepth"/> over the floor, and
    /// re-asserts her behaviour for the step. A spent drain stays spent, a drain still
    /// running is treated as done, sealed pits stay sealed, an open flow stays open and its escalation starts again (a flow
    /// still in its delay waits a full delay afresh), and a Guarded hold stays held on the same adds.
    /// RestartPhase never moves the step and never spawns or destroys a prop, so calling it twice leaves the same state.
    ///
    /// THE FINALE. She walks to <see cref="MatronData.FinalePoint"/>, turns to the arena centre and plays the same opening
    /// motion as a pit flow, then stands immune and peaceful. The rumble reopens the four pits and puts the breakable rock in
    /// the north opening; the rock's death lets the lava out (it clears the acid), <see cref="PillarSeconds"/> later the fire
    /// pillar rises on her, and <see cref="SlaySeconds"/> after that she is slain inside it and leaves a lootless corpse. The
    /// pillar outlives her: only End destroys props. Her death wins the fight.
    ///
    /// STALE EVENTS. Every timer is honoured only in the step that armed it (face and open: Opening, flow-start: Flowing
    /// while the flow waits, face and rumble: FinaleOpening, pillar and slay: FinaleLava, drain-done: while draining); a
    /// door use counts only for the door the current step is waiting on; a HazardEnded for the fill is ignored while the
    /// port still reads a live run under the tag.
    /// </summary>
    public sealed class MatronScript : IPinnacleBossScript
    {
        public const string TagFill = "fill";

        /// <summary>The drain iris, the one use target; the grate and the black panel are unselectable and have their own tags.</summary>
        public const string TagDrain = "drain";

        public const string TagDrainGrate = "drain-grate";

        public const string TagDrainFloor = "drain-floor";

        /// <summary>The drain beacon: a beam over the drain while the acid is deep and the drain unused (reconciled by the poll only).</summary>
        public const string TagDrainBeacon = "drain-beacon";

        /// <summary>Seconds between spawn attempts of a beacon that failed to spawn (the poll runs every quarter second).</summary>
        public const double BeaconRetrySeconds = 5.0;

        public const string TimerPoll = "poll";

        public const string TimerOpen = "open";

        public const string TimerRumble = "rumble";

        public const string TimerSlay = "slay";

        public const string TimerDrainDone = "drain-done";

        /// <summary>The flow delay is over: the open vent's acid starts to rise.</summary>
        public const string TimerFlowStart = "flow-start";

        /// <summary>Her in-place turn is over: the opening motion plays.</summary>
        public const string TimerFace = "face";

        /// <summary>The fire pillar rises on her; the slay follows.</summary>
        public const string TimerPillar = "pillar";

        /// <summary>The finale's breakable rock (an add): its death lets the lava out.</summary>
        public const string TagRock = "rock";

        public const double PollSeconds = 1.0;

        /// <summary>The poll's interval while she walks (Running, FinaleWalking), so arrival is seen within a quarter second.</summary>
        public const double WalkPollSeconds = 0.25;

        /// <summary>The open motion (SpecialAttack1 under HandCombat) runs about 1.25 s before the flow appears.</summary>
        public const double OpenSeconds = 1.25;

        public const double RumbleSeconds = 2.0;

        /// <summary>The fire pillar rises on her this long after the rock breaks and the lava comes out (fixed: the acid may not be cleared yet).</summary>
        public const double PillarSeconds = 5.0;

        /// <summary>She is slain this long after the pillar rises on her: the fire hits her first, then she dies in it.</summary>
        public const double SlaySeconds = 1.0;

        /// <summary>The opening motion plays this long after her turn's estimated end (the turn's last frame lands first).</summary>
        public const double TurnSettleSeconds = 0.1;

        /// <summary>A poll restarts a fill that is wanted and not running at most this often...</summary>
        public const double FillReconcileCooldown = 5.0;

        /// <summary>...and at most this many times per phase entry, the count reset whenever a poll reads a live surface.</summary>
        public const int MaxPollFillRestarts = 10;

        /// <summary>A live surface this far below the last level, while nothing lowers it, is a core retry run relaunched at its original start_z.</summary>
        public const double LevelRegressionTolerance = 0.05;

        /// <summary>A walk that has not arrived after this many seconds counts as arrived (the live MoveTo gives up on a stall).</summary>
        public const double MoveTimeout = 4.0;

        /// <summary>
        /// Her reach radius: the head weenie's Setup.Radius 1.41 (setup of 1007550, unscaled, `cells --modelbounds`) times her
        /// scale 1.5. The live walk measures her distance from the point from this sphere (MoveToManager.GetCurrentDistance
        /// with UseSpheres, which MoveToParameters sets by default).
        /// </summary>
        public const double ReachRadius = 1.41 * 1.5;

        /// <summary>Creature.GetMoveToPosition's DistanceToObject: the walk ends when her sphere is this close to the point.</summary>
        public const double WalkStopDistance = 0.6;

        /// <summary>Slack over where the walk stops, so a stop a little long still counts.</summary>
        public const double ArrivalMargin = 0.3;

        /// <summary>Her CENTRE's 2D distance from the point at which she has arrived: where the live walk ends, plus the margin.</summary>
        public const double ArrivalRadius = WalkStopDistance + ReachRadius + ArrivalMargin;

        /// <summary>A walk check that closes less than this on the best distance so far makes no progress...</summary>
        public const double StallEpsilon = 0.05;

        /// <summary>...and this many such checks in a row are a stall: she has arrived where she stands.</summary>
        public const int StallChecks = 3;

        /// <summary>No stall is called this soon after a walk begins (the motion may still be starting).</summary>
        public const double StallGraceSeconds = 1.0;

        /// <summary>Her health floors: the four flow thresholds, then 0.0 (1 HP: she never dies of damage).</summary>
        public static readonly IReadOnlyList<double> Thresholds = new[] { 0.8, 0.6, 0.4, 0.2, 0.0 };

        /// <summary>The owner has landed when their feet are at or below this z inside the landing rectangle.</summary>
        public const float LandingMaxZ = -59.4f;

        public const float LandingMinX = 195f, LandingMaxX = 245f, LandingMinY = -165f, LandingMaxY = -105f;

        /// <summary>The drain is refused while the acid is shallower than this.</summary>
        public const double DrainMinDepth = 0.25;

        /// <summary>While Guarded, the brood line is said again this often (the hold itself has no timeout).</summary>
        public const double BroodReminderSeconds = 20.0;

        /// <summary>A wave add never spawns within this distance (2D) of the owner.</summary>
        public const double WaveClearance = 5.0;

        /// <summary>Restarts of the fill after a non-Stopped end, per phase entry or restart.</summary>
        public const int MaxFillRetries = 5;

        /// <summary>The finale's lava floor in the north niche.</summary>
        public const double LavaFloorOpacity = 0.75;

        /// <summary>The fill is wanted while its surface is more than this above the floor (below it, a run would be a floor film).</summary>
        private const double FloorEpsilon = 0.02;

        public static string DoorTag(int opening) => $"door-{opening}";

        private readonly MatronSettings settings;
        private readonly PinnacleScriptData data = MatronData.Script();

        private int phase;
        private readonly List<int> pitOrder = new List<int>();

        // ---- props (every handle the script spawned and has not destroyed) ----
        private readonly Dictionary<int, PinnacleHandle> doors = new Dictionary<int, PinnacleHandle>();
        private readonly HashSet<int> openDoors = new HashSet<int>();
        private readonly Dictionary<int, List<PinnacleHandle>> flowProps = new Dictionary<int, List<PinnacleHandle>>();
        private readonly Dictionary<int, List<PinnacleHandle>> platforms = new Dictionary<int, List<PinnacleHandle>>();
        private readonly List<PinnacleHandle> drainProps = new List<PinnacleHandle>();
        private readonly List<PinnacleHandle> lavaProps = new List<PinnacleHandle>();
        private readonly List<PinnacleHandle> pillars = new List<PinnacleHandle>();
        private PinnacleHandle drainDoor;
        private PinnacleHandle beacon = PinnacleHandle.None;
        private double nextBeaconTry;

        // ---- the loop ----
        private int flowsDone;
        private double currentFloor;
        private double stepStart;   // Running / FinaleWalking: when the walk began
        private double walkBest;    // the closest the walk has come
        private int walkStalls;     // checks in a row without progress
        private double flowStart;   // Flowing: the escalation's origin (the delayed start)
        private bool flowWaiting;   // Flowing: the door is open, the flow delay not yet over (rate 0)
        private bool finaleRequested;

        // ---- the brood: every wave add the script spawned this phase (never the head or the rock) ----
        private readonly List<PinnacleHandle> waveAdds = new List<PinnacleHandle>();
        private double broodSaidAt;   // Guarded: when the brood line was last said

        // ---- the fill ----
        private bool fillRunning;
        private double level;
        private double checkpoint;
        private double finaleStartZ;
        private double? lastRate;
        private int fillRetries;
        private bool draining;
        private int pollFillRestarts;
        private double nextFillReconcile;

        // ---- the finale ----
        private PinnacleHandle rock;
        private bool slayRequested;
        private bool pillarsSpawned;
        private PinnaclePoint pillarAt;

        public MatronScript(): this(MatronSettings.Defaults)
        {
        }

        public MatronScript(MatronSettings settings)
        {
            this.settings = settings ?? MatronSettings.Defaults;
            level = checkpoint = finaleStartZ = this.settings.FillFloorZ;
        }

        public string Kind => MatronData.Kind;

        public PinnacleScriptData Data => data;

        /// <summary>The tunables this fight runs on (the live defaults, or a /pinnacle enter per-copy override of them).</summary>
        public MatronSettings Settings => settings;

        public int PhaseCount => 2;

        // ---- read by tests and Describe ----
        public MatronStep Step { get; private set; }

        public IReadOnlyList<int> PitOrder => pitOrder;

        /// <summary>The pit the loop is on: the one flowing (or about to), or the one just sealed in Sealed.</summary>
        public int CurrentPit => pitOrder.Count == 0 ? 0 : pitOrder[Math.Min(Step == MatronStep.Sealed ? flowsDone - 1 : flowsDone, pitOrder.Count - 1)];

        public int FlowsDone => flowsDone;

        /// <summary>The wave adds the script spawned this phase and still tracks (alive or not).</summary>
        public IReadOnlyList<PinnacleHandle> WaveAdds => waveAdds;

        /// <summary>Flowing with the door open and the flow delay not yet over.</summary>
        public bool FlowWaiting => flowWaiting;

        public bool DrainSpent { get; private set; }

        public bool Draining => draining;

        public double Checkpoint => checkpoint;

        /// <summary>The finale rock while it stands (None before the rumble, after it breaks, or when it failed to spawn).</summary>
        public PinnacleHandle Rock => rock;

        /// <summary>The last known fill surface z.</summary>
        public double Level => level;

        // ================================================================ lifecycle

        public void OnStart(IPinnacleFightPort port)
        {
            port.Say(MatronLines.LineStart);
            port.Say(MatronLines.LineTauntIntrude);
            port.Say(MatronLines.LineTauntFeed);
            port.SetAttackable(port.Head, true);
            port.Engage(port.Head);
        }

        public void EnterPhase(int n, IPinnacleFightPort port)
        {
            phase = n;

            // the core has cleared the previous phase: no run, no timer
            fillRunning = false;
            lastRate = null;
            fillRetries = 0;
            draining = false;
            pollFillRestarts = 0;
            nextFillReconcile = 0;
            slayRequested = false;
            pillarsSpawned = false;
            rock = PinnacleHandle.None;
            flowWaiting = false;

            // the core destroyed every add at the phase change: no wave add is left to guard her
            waveAdds.Clear();

            if (n == 1)
                EnterLoop(port);
            else
                EnterFinale(port);

            port.SetTimer(TimerPoll, PollInterval);
        }

        private void EnterLoop(IPinnacleFightPort port)
        {
            // destroy-tracked-then-spawn: a second entry (an admin jump) never duplicates a prop
            DestroyAllProps(port);

            pitOrder.Clear();
            pitOrder.AddRange(MatronData.PitIds);

            for (var i = pitOrder.Count - 1; i > 0; i--)
            {
                var j = port.Rng.Next(i + 1);
                (pitOrder[i], pitOrder[j]) = (pitOrder[j], pitOrder[i]);
            }

            flowsDone = 0;
            finaleRequested = false;
            Step = MatronStep.Fighting;
            checkpoint = level = finaleStartZ = settings.FillFloorZ;

            SpawnFixtures(port);

            port.SetImmune(port.Head, false);
            SetFloor(port, Thresholds[0]);
            port.Engage(port.Head);
        }

        private void EnterFinale(IPinnacleFightPort port)
        {
            // the survivor adds are gone already (the core's ClearPhase); the loop's furniture goes now
            foreach (var id in flowProps.Keys.ToList())
                DestroyFlow(port, id);

            foreach (var id in platforms.Keys.ToList())
                DestroyPlatforms(port, id);

            DestroyAll(port, lavaProps);
            DestroyAll(port, pillars);

            foreach (var id in openDoors.ToList())
                CloseDoor(port, id);

            // an admin jump straight into phase 2 never ran phase 1
            if (doors.Count == 0)
                SpawnFixtures(port);

            Step = MatronStep.FinaleWalking;
            checkpoint = level = finaleStartZ;

            port.SetImmune(port.Head, true);
            SetFloor(port, 0.0);
            port.SetPeaceful(port.Head, true);
            BeginWalk(port, MatronData.FinalePoint);

            EnsureFill(port);
        }

        public void RestartPhase(IPinnacleFightPort port)
        {
            // the hold stopped the run; whatever an event handler started at this resume is stopped too
            port.StopHazard(TagFill);
            fillRunning = false;
            lastRate = null;
            fillRetries = 0;

            // a drain still running at the death already re-checkpointed to the floor: it is done, and stays spent
            if (draining)
            {
                draining = false;
                port.CancelTimer(TimerDrainDone);
            }

            // never over the returning player's head: they land on the floor. The checkpoint takes the capped level too, so
            // Describe reports the acid that is really there
            level = checkpoint = Math.Min(checkpoint, settings.FillFloorZ + settings.ReturnMaxDepth);

            switch (Step)
            {
                case MatronStep.Fighting:
                    port.SetImmune(port.Head, false);
                    port.Engage(port.Head);
                    break;

                case MatronStep.Guarded:
                    // the hold kept her brood: still immune, still fighting, until the next poll finds them all dead
                    port.SetImmune(port.Head, true);
                    port.Engage(port.Head);
                    break;

                case MatronStep.Running:
                    port.SetImmune(port.Head, true);
                    port.SetPeaceful(port.Head, true);
                    BeginWalk(port, MatronData.Opening(CurrentPit).Stand);
                    break;

                case MatronStep.Opening:
                    // the turn and the motion again from the start: a pending open would cut the motion short
                    port.SetImmune(port.Head, true);
                    port.SetPeaceful(port.Head, true);
                    port.CancelTimer(TimerOpen);
                    FaceThenOpen(port, MatronData.Opening(CurrentPit).Stand);
                    break;

                case MatronStep.Flowing:
                    // the same flow reopens (its props never left) and its escalation starts again; a flow still in its
                    // delay waits a full delay from now (re-setting the tag replaces the pending start)
                    port.SetImmune(port.Head, true);
                    port.Engage(port.Head);

                    if (flowWaiting)
                        port.SetTimer(TimerFlowStart, settings.FlowDelay);
                    else
                        flowStart = port.Now;
                    break;

                case MatronStep.Sealed:
                    // immune until the owner lands (owner Q2)
                    port.SetImmune(port.Head, true);
                    port.Engage(port.Head);
                    break;

                case MatronStep.FinaleWalking:
                    port.SetImmune(port.Head, true);
                    port.SetPeaceful(port.Head, true);
                    BeginWalk(port, MatronData.FinalePoint);
                    break;

                case MatronStep.FinaleOpening:
                    port.SetImmune(port.Head, true);
                    port.SetPeaceful(port.Head, true);
                    port.CancelTimer(TimerRumble);
                    FaceThenOpen(port, MatronData.FinalePoint);
                    break;

                case MatronStep.FinaleRace:
                    // the rock is an add: the hold kept it, alive and where it stood
                    port.SetImmune(port.Head, true);
                    port.SetPeaceful(port.Head, true);
                    break;

                case MatronStep.FinaleLava:
                    // the lava props stayed; the acid restarts at the checkpoint and the lava clears it again. No pillar yet:
                    // it rises a full delay afresh. The pillar up and her not slain yet: the slay runs a full delay from now.
                    port.SetImmune(port.Head, true);
                    port.SetPeaceful(port.Head, true);

                    if (!pillarsSpawned)
                        port.SetTimer(TimerPillar, PillarSeconds);
                    else if (!slayRequested)
                        port.SetTimer(TimerSlay, SlaySeconds);
                    break;
            }

            SetFloor(port, currentFloor);
            EnsureFill(port);
            port.SetTimer(TimerPoll, PollInterval);
        }

        public string Describe()
            => FormattableString.Invariant(
                $"matron p{phase} step={Step} flows={flowsDone}/{MatronData.PitIds.Count} order={string.Join(",", pitOrder)} floor={currentFloor:0.00} fill={(fillRunning ? "on" : "off")} level={level:0.00} checkpoint={checkpoint:0.00} rate={(lastRate.HasValue ? lastRate.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) : "-")} drain={(draining ? "draining" : DrainSpent ? "spent" : "ready")} retries={fillRetries}/{MaxFillRetries} brood={waveAdds.Count}{(flowWaiting ? " flow=waiting" : "")} beacon={(beacon.IsNone ? "off" : "on")}");

        // ================================================================ events

        public void OnEvent(PinnacleEvent e, IPinnacleFightPort port)
        {
            switch (e.Kind)
            {
                case PinnacleEventKind.AddDied:
                    // wave adds carry over and their deaths change nothing here (a Guarded hold reads them on the poll); the
                    // rock lets the lava out; her death ends the fight
                    if (e.Tag == PinnacleEvent.HeadTag)
                        WinNow(port);
                    else if (e.Tag == TagRock && Step == MatronStep.FinaleRace)
                    {
                        // a rock that failed to spawn reports here too (SpawnFailed): the finale never strands on it
                        rock = PinnacleHandle.None;
                        LavaOut(port);
                    }
                    break;

                case PinnacleEventKind.HazardEnded:
                    if (e.Tag == TagFill)
                        OnFillEnded(e.Outcome, port);
                    break;

                case PinnacleEventKind.ObjectUsed:
                    OnObjectUsed(e, port);
                    break;

                case PinnacleEventKind.TimerFired:
                    OnTimer(e.Tag, port);
                    break;
            }
        }

        private void OnTimer(string tag, IPinnacleFightPort port)
        {
            switch (tag)
            {
                case TimerPoll:
                    Poll(port);
                    port.SetTimer(TimerPoll, PollInterval);
                    break;

                case TimerFace:
                    if (Step == MatronStep.Opening)
                    {
                        PlayOpening(port);
                        port.SetTimer(TimerOpen, OpenSeconds);
                    }
                    else if (Step == MatronStep.FinaleOpening)
                    {
                        PlayOpening(port);
                        port.SetTimer(TimerRumble, RumbleSeconds);
                    }
                    break;

                case TimerOpen:
                    if (Step == MatronStep.Opening)
                        OpenFlow(port);
                    break;

                case TimerFlowStart:
                    if (Step == MatronStep.Flowing && flowWaiting)
                        StartFlow(port);
                    break;

                case TimerDrainDone:
                    if (draining)
                    {
                        draining = false;
                        ReadLevel(port);
                        ApplyRate(port);
                        EnsureFill(port);
                    }
                    break;

                case TimerRumble:
                    if (Step == MatronStep.FinaleOpening)
                        Rumble(port);
                    break;

                case TimerPillar:
                    if (Step == MatronStep.FinaleLava && !pillarsSpawned)
                        RaisePillar(port);
                    break;

                case TimerSlay:
                    if (Step == MatronStep.FinaleLava && pillarsSpawned && !slayRequested)
                        SlayHer(port);
                    break;
            }
        }

        private void Poll(IPinnacleFightPort port)
        {
            ReconcileFill(port);
            ReconcileBeacon(port);

            switch (Step)
            {
                case MatronStep.Fighting:
                    if (!finaleRequested && port.IsAtHealthFloor(port.Head))
                        OnThreshold(port);
                    break;

                case MatronStep.Guarded:
                    if (!finaleRequested && !BroodAlive(port))
                        PassThreshold(port);
                    else if (port.Now - broodSaidAt >= BroodReminderSeconds)
                    {
                        // the hold has no timeout: the reminder tells the player why she is still immune
                        broodSaidAt = port.Now;
                        port.Say(MatronLines.LineBroodGuards);
                    }
                    break;

                case MatronStep.Running:
                    if (Arrived(port, MatronData.Opening(CurrentPit).Stand))
                    {
                        Step = MatronStep.Opening;
                        FaceThenOpen(port, MatronData.Opening(CurrentPit).Stand);
                    }
                    break;

                case MatronStep.Sealed:
                    if (OwnerLanded(port))
                        OnLanded(port);
                    break;

                case MatronStep.FinaleWalking:
                    if (Arrived(port, MatronData.FinalePoint))
                    {
                        Step = MatronStep.FinaleOpening;
                        FaceThenOpen(port, MatronData.FinalePoint);
                    }
                    break;
            }
        }

        // ================================================================ the loop

        /// <summary>
        /// Her health floor is reached in Fighting. She is immune from here on; while any wave add lives she holds in
        /// Guarded, still fighting, and the poll passes the threshold once they are dead.
        /// </summary>
        private void OnThreshold(IPinnacleFightPort port)
        {
            port.SetImmune(port.Head, true);

            if (BroodAlive(port))
            {
                Step = MatronStep.Guarded;
                broodSaidAt = port.Now;
                port.Say(MatronLines.LineBroodGuards);
                return;
            }

            PassThreshold(port);
        }

        /// <summary>True while any wave add the script spawned this phase is alive.</summary>
        private bool BroodAlive(IPinnacleFightPort port)
        {
            foreach (var h in waveAdds)
                if (port.IsAlive(h))
                    return true;

            return false;
        }

        /// <summary>The threshold with no brood left: the run to the next pit, or after the fourth seal the finale.</summary>
        private void PassThreshold(IPinnacleFightPort port)
        {
            // a drain in progress already promised the floor: drained acid never comes back after a death
            var cycleStart = draining ? settings.FillFloorZ : level;

            if (flowsDone >= MatronData.PitIds.Count)
            {
                finaleRequested = true;
                finaleStartZ = cycleStart;
                port.AdvancePhase();
                return;
            }

            checkpoint = cycleStart;
            Step = MatronStep.Running;

            port.Say(MatronLines.LineFlowOpens);
            port.SetImmune(port.Head, true);
            port.SetPeaceful(port.Head, true);
            BeginWalk(port, MatronData.Opening(CurrentPit).Stand);
        }

        private void OpenFlow(IPinnacleFightPort port)
        {
            var pit = CurrentPit;
            var o = MatronData.Opening(pit);

            Step = MatronStep.Flowing;
            OpenDoor(port, pit);
            SpawnFlow(port, pit, MatronLayout.BackPanel(o), MatronLayout.WallStrip(o, lava: false, settings.WallFx, settings.FogAcid));
            SpawnPlatforms(port, pit);

            // the walk is over and the motion has played: she chases the owner, still immune
            port.Engage(port.Head);

            // the acid follows the open door after the flow delay (rate 0 until then: no fill is wanted at the floor)
            if (settings.FlowDelay > 0.0)
            {
                flowWaiting = true;
                port.SetTimer(TimerFlowStart, settings.FlowDelay);
            }
            else
                StartFlow(port);
        }

        /// <summary>The flow delay is over: the escalation counts from now and the acid rises.</summary>
        private void StartFlow(IPinnacleFightPort port)
        {
            flowWaiting = false;
            flowStart = port.Now;
            EnsureFill(port);
            ApplyRate(port);
        }

        private void Seal(IPinnacleFightPort port, int pit)
        {
            // a seal inside the delay: the pending start is spent
            if (flowWaiting)
            {
                flowWaiting = false;
                port.CancelTimer(TimerFlowStart);
            }

            CloseDoor(port, pit);
            DestroyFlow(port, pit);
            flowsDone++;
            Step = MatronStep.Sealed;

            port.Say(MatronLines.LineSeal);
            ApplyRate(port);   // 0, unless a drain is running: a seal never cancels a drain
        }

        private void OnLanded(IPinnacleFightPort port)
        {
            var pit = CurrentPit;

            DestroyPlatforms(port, pit);
            SpawnWave(port, flowsDone);

            port.SetImmune(port.Head, false);
            SetFloor(port, Thresholds[flowsDone]);
            Step = MatronStep.Fighting;
        }

        private void SpawnWave(IPinnacleFightPort port, int number)
        {
            var points = MatronData.WaveSpawnPoints.ToList();

            for (var i = points.Count - 1; i > 0; i--)
            {
                var j = port.Rng.Next(i + 1);
                (points[i], points[j]) = (points[j], points[i]);
            }

            if (port.TryGetOwnerPosition(out var owner))
            {
                var clear = points.Where(pt => Distance2D(pt, owner) >= WaveClearance).ToList();

                if (clear.Count > 0)
                    points = clear;
            }

            var n = 0;
            var spawned = 0;
            var tier = MatronData.WaveTier(number);

            foreach (var (wcid, count) in MatronData.Wave(number))
            {
                for (var k = 0; k < count; k++, n++)
                {
                    // more adds than clear points: a second lap stands 1 m east of the first (every point keeps its cell)
                    var at = points[n % points.Count];
                    var lap = n / points.Count;

                    if (lap > 0)
                        at = at with { X = at.X + lap };

                    // a spawn that failed twice (None) guards nothing
                    var h = port.SpawnAdd($"wave{number}-{n}", wcid, at, tier);

                    if (!h.IsNone)
                    {
                        waveAdds.Add(h);
                        spawned++;
                    }
                }
            }

            // the sentinel line only when a sentinel actually stands
            if (number == MatronData.Waves.Count && spawned > 0)
            {
                port.Say(MatronLines.LineSentinels);
                port.Say(MatronLines.LineTauntSentinels);
            }
        }

        // ================================================================ door use

        private void OnObjectUsed(PinnacleEvent e, IPinnacleFightPort port)
        {
            if (e.Tag == TagDrain)
            {
                if (e.Handle == drainDoor)
                    UseDrain(port);
                return;
            }

            foreach (var kv in doors)
            {
                if (kv.Value != e.Handle || e.Tag != DoorTag(kv.Key))
                    continue;

                // the north door has no use: the finale's way out is the rock
                if (Step == MatronStep.Flowing && kv.Key == CurrentPit && kv.Key != MatronData.NorthId)
                    Seal(port, kv.Key);

                return;
            }
        }

        private void UseDrain(IPinnacleFightPort port)
        {
            if (DrainSpent)
            {
                port.Say(MatronLines.LineDrainSpent);
                return;
            }

            ReadLevel(port);
            var depth = level - settings.FillFloorZ;

            // the lava clears the acid itself: nothing for the drain to do then
            if (depth < DrainMinDepth || Step == MatronStep.FinaleLava)
            {
                port.Say(MatronLines.LineDrainEmpty);
                return;
            }

            DrainSpent = true;
            draining = true;
            DestroyBeacon(port);
            nextBeaconTry = 0;
            checkpoint = settings.FillFloorZ;

            port.SetDoorOpen(drainDoor, true);
            port.Say(MatronLines.LineDrainUsed);
            ApplyRate(port);
            port.SetTimer(TimerDrainDone, depth / Math.Abs(settings.DrainRate) + 1.0);
        }

        // ================================================================ the finale

        private void Rumble(IPinnacleFightPort port)
        {
            Step = MatronStep.FinaleRace;
            port.Say(MatronLines.LineRumble);
            port.Say(MatronLines.LineTauntBurn);

            foreach (var pit in MatronData.PitIds)
            {
                var o = MatronData.Opening(pit);
                OpenDoor(port, pit);
                SpawnFlow(port, pit, MatronLayout.BackPanel(o), MatronLayout.WallStrip(o, lava: false, settings.WallFx, settings.FogAcid));
            }

            SpawnPlatforms(port, MatronData.NorthId);
            SpawnRock(port);

            // the reopened pits are lethal: the finale scale, on a fresh run or the one already there
            EnsureFill(port);
            port.ControlHazard(TagFill, "damage_scale", DamageScaleNow(port));
            ApplyRate(port);
        }

        private void LavaOut(IPinnacleFightPort port)
        {
            Step = MatronStep.FinaleLava;
            port.Say(MatronLines.LineLavaDoor);

            var north = MatronData.Opening(MatronData.NorthId);

            Add(port, lavaProps, "lava-panel", MatronLayout.BackPanel(north, MatronData.LavaPanelWcid));

            var strip = MatronLayout.WallStrip(north, lava: true, settings.WallFx, settings.FogLava);
            for (var i = 0; i < strip.Count; i++)
                Add(port, lavaProps, $"lava-strip-{i}", strip[i]);

            for (var i = 0; i < MatronData.LavaPoolPoints.Count; i++)
                Add(port, lavaProps, $"lava-pool-{i}", new PinnacleProp(MatronData.LavaPanelWcid, MatronData.LavaPoolPoints[i]));

            // the north niche's lava floor, laid like a pit's acid floor
            var floor = MatronLayout.NicheFloor(north, MatronData.LavaPanelWcid, HazardPanels.TranslucencyFor(LavaFloorOpacity).Value);
            for (var i = 0; i < floor.Count; i++)
                Add(port, lavaProps, $"lava-floor-{i}", floor[i]);

            // the lava blocks the four acid flows
            foreach (var pit in MatronData.PitIds)
            {
                DestroyFlow(port, pit);
                CloseDoor(port, pit);
            }

            ApplyRate(port);
            port.SetTimer(TimerPillar, PillarSeconds);
        }

        /// <summary>The north door gives way to the rock: an add on the north sill, at the fixed rock tier.</summary>
        private void SpawnRock(IPinnacleFightPort port)
        {
            if (doors.Remove(MatronData.NorthId, out var door))
            {
                openDoors.Remove(MatronData.NorthId);
                port.DestroyProp(door);
            }

            if (!rock.IsNone && port.IsAlive(rock))
                return;

            rock = port.SpawnAdd(TagRock, MatronData.RockWcid, MatronLayout.Rock(MatronData.Opening(MatronData.NorthId)), MatronData.RockTier);
        }

        /// <summary>The fixed-delay fire pillar, on her where she stands; the slay follows it. Once.</summary>
        private void RaisePillar(IPinnacleFightPort port)
        {
            pillarAt = port.TryGetPosition(port.Head, out var at) ? at : MatronData.FinalePoint;
            pillarsSpawned = true;

            Add(port, pillars, "pillar", new PinnacleProp(MatronData.EffectHostWcid, MatronData.PillarHostAt(pillarAt.X, pillarAt.Y, pillarAt.Cell), Script: MatronData.PillarScript));
            port.SetTimer(TimerSlay, SlaySeconds);
        }

        /// <summary>She dies inside the pillar (it survives her: only End destroys props). Her death event wins the fight. Once.</summary>
        private void SlayHer(IPinnacleFightPort port)
        {
            slayRequested = true;
            port.Slay(port.Head);
        }

        private static void WinNow(IPinnacleFightPort port)
        {
            port.Say(MatronLines.LineWin);
            port.Win();
        }

        // ================================================================ the fill

        /// <summary>The rate the fill should have now. The lava's clear beats a drain; a drain beats every step rate.</summary>
        private double RateNow(IPinnacleFightPort port)
        {
            if (Step == MatronStep.FinaleLava)
                return settings.LavaClearRate;

            if (draining)
                return settings.DrainRate;

            switch (Step)
            {
                case MatronStep.Flowing:
                    if (flowWaiting)
                        return 0.0;

                    var doublings = Math.Floor(Math.Max(0.0, port.Now - flowStart) / settings.FlowEscalateSeconds);
                    return Math.Min(settings.FlowRate * Math.Pow(2.0, Math.Min(doublings, 30.0)), settings.FlowRateCap);

                case MatronStep.FinaleRace:
                    return settings.FinaleRate;

                default:
                    return 0.0;
            }
        }

        private double DamageScaleNow(IPinnacleFightPort port)
        {
            var scale = MatronSettings.LevelScale(port.Level, settings.HazardPctMin, settings.HazardPctMax);

            return Step == MatronStep.FinaleRace ? scale * settings.FinaleDamageScale : scale;
        }

        /// <summary>
        /// THE only place a rate is sent. An event sends it when it differs from the last one the run accepted; every poll
        /// sends it regardless (<paramref name="always"/>), because the core's one-shot retry relaunches a run with its
        /// ORIGINAL args and the dedupe cannot see that.
        /// </summary>
        private void ApplyRate(IPinnacleFightPort port, bool always = false)
        {
            if (!fillRunning)
                return;

            var rate = RateNow(port);

            if (!always && lastRate.HasValue && Math.Abs(lastRate.Value - rate) < 1e-12)
                return;

            // refused (the run is between the core's retries): lastRate stays, so the next poll sends it again
            if (port.ControlHazard(TagFill, "rate", rate))
                lastRate = rate;
        }

        private bool FillWanted(IPinnacleFightPort port) => RateNow(port) > 0.0 || level > settings.FillFloorZ + FloorEpsilon;

        /// <summary>
        /// THE poll's fill reconciler, run first on every poll. A live surface is read (and a fall that nothing explains, a
        /// core retry relaunched at its original start_z, is snapped back with set_z); a fill that is wanted and not running
        /// (its restarts after an end ran out, or nothing started it) is started again, no more often than the cooldown
        /// and no more than the cap per phase; and the run gets the current rate and damage scale whatever it last
        /// accepted. Every start still goes through StartFill, so one run at most.
        /// </summary>
        private void ReconcileFill(IPinnacleFightPort port)
        {
            if (port.TryGetHazardLevel(TagFill, out var z))
            {
                pollFillRestarts = 0;

                if (fillRunning && RateNow(port) >= 0.0 && z < level - LevelRegressionTolerance)
                    port.ControlHazard(TagFill, "set_z", level);
                else
                    level = z;
            }
            else if (!fillRunning && FillWanted(port) && port.Now >= nextFillReconcile && pollFillRestarts < MaxPollFillRestarts)
            {
                pollFillRestarts++;
                nextFillReconcile = port.Now + FillReconcileCooldown;
                StartFill(port, level);
                return;
            }

            if (!fillRunning)
                return;

            ApplyRate(port, always: true);
            port.ControlHazard(TagFill, "damage_scale", DamageScaleNow(port));
        }

        /// <summary>Starts the fill when it is wanted and not running, stops it when it is running and not wanted.</summary>
        private void EnsureFill(IPinnacleFightPort port)
        {
            var wanted = FillWanted(port);

            if (!fillRunning && wanted)
            {
                StartFill(port, level);
            }
            else if (fillRunning && !wanted)
            {
                port.StopHazard(TagFill);
                fillRunning = false;
                lastRate = null;
                level = settings.FillFloorZ;
            }
        }

        /// <summary>Every fill start: the tag is stopped first, so there is never a second run.</summary>
        private void StartFill(IPinnacleFightPort port, double startZ)
        {
            var rate = RateNow(port);

            port.StopHazard(TagFill);
            port.StartHazard(TagFill, HazardMechanicRegistry.Fill, null, settings.FillArgs(startZ, rate, DamageScaleNow(port)));
            fillRunning = true;
            lastRate = rate;
        }

        private void OnFillEnded(HazardOutcome outcome, IPinnacleFightPort port)
        {
            // an end queued for a run since replaced: the port still reads a live run under the tag
            if (port.TryGetHazardLevel(TagFill, out _))
                return;

            fillRunning = false;
            lastRate = null;

            if (outcome == HazardOutcome.Stopped || fillRetries >= MaxFillRetries)
                return;

            fillRetries++;
            EnsureFill(port);
        }

        /// <summary>The surface of the live run, when the port reads one; otherwise the last known level stands.</summary>
        private void ReadLevel(IPinnacleFightPort port)
        {
            if (port.TryGetHazardLevel(TagFill, out var z))
                level = z;
        }

        // ================================================================ props

        /// <summary>
        /// The drain beacon, reconciled from the poll only (a restart never spawns or destroys a prop). Wanted while the drain is
        /// unused and not draining, the lava is not out, and the acid is at least <see cref="MatronSettings.BeaconDepth"/> over the floor.
        /// A spawn that fails is retried after <see cref="BeaconRetrySeconds"/>, not every poll: only UseDrain and DestroyAllProps reset
        /// that timer, so the acid flapping around the depth cannot defeat it.
        /// </summary>
        private void ReconcileBeacon(IPinnacleFightPort port)
        {
            var wanted = !DrainSpent && !draining && Step != MatronStep.FinaleLava && (level - settings.FillFloorZ) >= settings.BeaconDepth;

            if (wanted && beacon.IsNone && port.Now >= nextBeaconTry)
            {
                beacon = port.SpawnProp(TagDrainBeacon, MatronLayout.DrainBeacon(MatronLayout.DrainBeacons[settings.BeaconFx]));

                if (beacon.IsNone)
                    nextBeaconTry = port.Now + BeaconRetrySeconds;
            }
            else if (!wanted && !beacon.IsNone)
            {
                DestroyBeacon(port);
            }
        }

        private void DestroyBeacon(IPinnacleFightPort port)
        {
            if (!beacon.IsNone)
                port.DestroyProp(beacon);

            beacon = PinnacleHandle.None;
        }

        private void SpawnFixtures(IPinnacleFightPort port)
        {
            foreach (var o in MatronData.Openings)
                doors[o.Id] = port.SpawnProp(DoorTag(o.Id), MatronLayout.DoorProp(o));

            // the drain, bottom up: the black panel, the grate tiles over it (tagged drain-grate-0 to -3), the iris over that (the use target)
            drainProps.Add(port.SpawnProp(TagDrainFloor, MatronLayout.DrainFloor()));
            var grate = MatronLayout.DrainGrate();
            for (var i = 0; i < grate.Count; i++)
                drainProps.Add(port.SpawnProp($"{TagDrainGrate}-{i}", grate[i]));
            drainDoor = port.SpawnProp(TagDrain, MatronLayout.DrainIris(DrainSpent));
            drainProps.Add(drainDoor);
        }

        private void DestroyAllProps(IPinnacleFightPort port)
        {
            foreach (var h in doors.Values)
                port.DestroyProp(h);

            doors.Clear();
            openDoors.Clear();

            foreach (var id in flowProps.Keys.ToList())
                DestroyFlow(port, id);

            foreach (var id in platforms.Keys.ToList())
                DestroyPlatforms(port, id);

            DestroyAll(port, drainProps);
            DestroyAll(port, lavaProps);
            DestroyAll(port, pillars);
            drainDoor = PinnacleHandle.None;
            DestroyBeacon(port);
            nextBeaconTry = 0;
        }

        private void OpenDoor(IPinnacleFightPort port, int id)
        {
            if (doors.TryGetValue(id, out var h) && openDoors.Add(id))
                port.SetDoorOpen(h, true);
        }

        private void CloseDoor(IPinnacleFightPort port, int id)
        {
            if (doors.TryGetValue(id, out var h) && openDoors.Remove(id))
                port.SetDoorOpen(h, false);
        }

        /// <summary>A flow's back panel, wall strip and niche acid floor (spawned once per open: an open flow is never spawned twice).</summary>
        private void SpawnFlow(IPinnacleFightPort port, int id, PinnacleProp backPanel, IReadOnlyList<PinnacleProp> strip)
        {
            if (flowProps.ContainsKey(id))
                return;

            var list = new List<PinnacleHandle>();
            flowProps[id] = list;

            Add(port, list, $"panel-{id}", backPanel);

            for (var i = 0; i < strip.Count; i++)
                Add(port, list, $"strip-{id}-{i}", strip[i]);


            var floor = MatronLayout.NicheFloor(MatronData.Opening(id));
            for (var i = 0; i < floor.Count; i++)
                Add(port, list, $"niche-floor-{id}-{i}", floor[i]);
        }

        private void SpawnPlatforms(IPinnacleFightPort port, int id)
        {
            if (platforms.ContainsKey(id))
                return;

            var list = new List<PinnacleHandle>();
            platforms[id] = list;

            var points = MatronLayout.Platforms(MatronData.Opening(id));
            for (var k = 0; k < points.Count; k++)
                Add(port, list, $"platform-{id}-{k}", new PinnacleProp(MatronData.PlatformWcid, points[k]));
        }

        private void DestroyFlow(IPinnacleFightPort port, int id)
        {
            if (flowProps.Remove(id, out var list))
                DestroyAll(port, list);
        }

        private void DestroyPlatforms(IPinnacleFightPort port, int id)
        {
            if (platforms.Remove(id, out var list))
                DestroyAll(port, list);
        }

        private static void Add(IPinnacleFightPort port, List<PinnacleHandle> into, string tag, PinnacleProp prop)
        {
            var h = port.SpawnProp(tag, prop);

            if (!h.IsNone)
                into.Add(h);
        }

        private static void DestroyAll(IPinnacleFightPort port, List<PinnacleHandle> list)
        {
            foreach (var h in list)
                port.DestroyProp(h);

            list.Clear();
        }

        // ================================================================ helpers

        private void SetFloor(IPinnacleFightPort port, double fraction)
        {
            currentFloor = fraction;
            port.SetHealthFloor(port.Head, fraction);
        }

        private double PollInterval => Step == MatronStep.Running || Step == MatronStep.FinaleWalking ? WalkPollSeconds : PollSeconds;

        private void BeginWalk(IPinnacleFightPort port, PinnaclePoint to)
        {
            stepStart = port.Now;
            walkBest = double.MaxValue;
            walkStalls = 0;
            port.MoveTo(port.Head, to);
        }

        /// <summary>Within <see cref="ArrivalRadius"/>, stalled (no progress for <see cref="StallChecks"/> checks after the grace), or timed out.</summary>
        private bool Arrived(IPinnacleFightPort port, PinnaclePoint to)
        {
            var since = port.Now - stepStart;

            if (since >= MoveTimeout)
                return true;

            if (!port.TryGetPosition(port.Head, out var at))
                return false;

            var d = Distance2D(at, to);

            if (d <= ArrivalRadius)
                return true;

            if (d < walkBest - StallEpsilon)
            {
                walkBest = d;
                walkStalls = 0;
                return false;
            }

            if (since < StallGraceSeconds)
                return false;

            return ++walkStalls >= StallChecks;
        }

        /// <summary>She turns in place to <paramref name="facing"/>'s heading; the opening motion plays when the turn is done.</summary>
        private static void FaceThenOpen(IPinnacleFightPort port, PinnaclePoint facing)
        {
            var turn = port.TurnTo(port.Head, facing);

            port.SetTimer(TimerFace, Math.Max(0.0, turn) + TurnSettleSeconds);
        }

        /// <summary>The opening motion (the pit flow's and the finale's): the acid attack and the breath.</summary>
        private static void PlayOpening(IPinnacleFightPort port)
        {
            port.PlayMotion(port.Head, MotionStance.HandCombat, MotionCommand.SpecialAttack1, false);
            port.PlayEffect(port.Head, PlayScript.BreatheAcid);
        }

        private static bool OwnerLanded(IPinnacleFightPort port)
            => port.TryGetOwnerPosition(out var at)
            && at.Z <= LandingMaxZ
            && at.X >= LandingMinX && at.X <= LandingMaxX
            && at.Y >= LandingMinY && at.Y <= LandingMaxY;

        private static double Distance2D(PinnaclePoint a, PinnaclePoint b)
        {
            var dx = (double)a.X - b.X;
            var dy = (double)a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
