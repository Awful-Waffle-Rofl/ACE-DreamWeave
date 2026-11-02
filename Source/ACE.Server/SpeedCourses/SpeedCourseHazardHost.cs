using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

using ACE.Server.Entity;
using ACE.Server.HazardMechanics;

namespace ACE.Server.SpeedCourses
{
    /// <summary>One hazard start, as the course host asks the hazard framework for it.</summary>
    public sealed class SpeedHazardStart
    {
        public SpeedHazardDef Hazard { get; init; }

        /// <summary>The hazard's index in the course's hazard list (the seed slot).</summary>
        public int SlotIndex { get; init; }

        /// <summary>0 for the first start of this hazard, then one more for every restart.</summary>
        public int Attempt { get; init; }

        public HazardRegion Region { get; init; }

        public string Kind { get; init; }

        public string Pattern { get; init; }

        /// <summary>What the host lays over the settings layer: the scaled damage pct and, for a boss-centred hazard, cx/cy.</summary>
        public HazardArgs CallerArgs { get; init; }

        /// <summary>The hazard's own authored args (speed-courses.json), used instead of the hazard_mech_&lt;kind&gt; row.</summary>
        public HazardArgs SettingsLayer { get; init; }

        public string SettingsLayerName { get; init; }

        public int Seed { get; init; }
    }

    /// <summary>
    /// Course hazard host -> hazard framework. Live: <see cref="LiveSpeedHazardPort"/> over HazardManager. Tests: a fake.
    /// </summary>
    public interface ISpeedHazardPort
    {
        /// <summary>The course landblock id (0x0089 for the Black Book); every region is built on it.</summary>
        ushort LandblockId { get; }

        /// <summary>The hazard_mech_enabled master switch. Off: every start is refused.</summary>
        bool Enabled { get; }

        /// <summary>Starts one hazard run under <paramref name="host"/>. The run id (&gt; 0), or 0 with <paramref name="error"/>.</summary>
        int Start(SpeedHazardStart request, IHazardHost host, out string error);

        /// <summary>Stops run <paramref name="runId"/> (outcome Stopped). Idempotent; an unknown or ended id is a no-op.</summary>
        void Stop(int runId, string reason);

        /// <summary>Drops the port's handle on a run that has ended.</summary>
        void Forget(int runId);
    }

    /// <summary>The live port: HazardManager on the course instance's landblock.</summary>
    public sealed class LiveSpeedHazardPort : ISpeedHazardPort
    {
        private readonly Landblock landblock;
        private readonly ConcurrentDictionary<int, HazardRun> runs = new ConcurrentDictionary<int, HazardRun>();

        public LiveSpeedHazardPort(Landblock landblock)
        {
            this.landblock = landblock ?? throw new ArgumentNullException(nameof(landblock));
        }

        public ushort LandblockId => landblock.Id.Landblock;

        public bool Enabled => HazardTunables.Enabled;

        public int Start(SpeedHazardStart request, IHazardHost host, out string error)
        {
            var run = HazardManager.Start(request.Region, request.Kind, request.CallerArgs, host, request.Seed, request.Pattern, landblock,
                request.SettingsLayer, request.SettingsLayerName, out error);

            if (run == null)
                return 0;

            runs[run.Id] = run;
            return run.Id;
        }

        public void Stop(int runId, string reason)
        {
            if (runs.TryRemove(runId, out var run))
                HazardManager.Stop(run, HazardOutcome.Stopped, reason);
        }

        public void Forget(int runId) => runs.TryRemove(runId, out _);
    }

    /// <summary>
    /// The hazard host of one speed-course run: <see cref="ISpeedHazardDriver"/> towards the run, <see cref="IHazardHost"/>
    /// towards the hazard framework (the Pinnacle host's shape, LivePinnacleWorld). Each run tick (landblock thread) it
    /// asks <see cref="SpeedCourseRules.DecideHazard"/> about every course hazard and starts or stops it:
    /// <list type="bullet">
    /// <item>Start: the hazard's args as the settings layer (no hazard_mech_&lt;kind&gt; read), its region with exclusions,
    /// its pattern, seed <see cref="SpeedCourseRules.HazardSeed"/>(course seed, slot, attempt) with attempt one more on
    /// every restart, and <see cref="SpeedCourseRules.ScaledPct"/> as args "pct" for a damaging hazard (pctAuthored &gt; 0,
    /// not memory). A boss-centred hazard (<see cref="SpeedHazardDef.OriginAtBoss"/>) gets the boss's position as cx/cy.</item>
    /// <item><see cref="OnEnded"/> may arrive on any thread; it is posted onto the instance landblock and handled there.
    /// Completed with a token contributes it to the Seal once (south memory = south_rite); Failed waits retrySeconds
    /// and restarts with the next seed.</item>
    /// <item><see cref="Stop"/> (the run's once-only teardown: finish, forfeit, death, landblock gone) stops every live run
    /// exactly once.</item>
    /// <item>With hazard_mech_enabled off no start is attempted; the run continues without hazards and says so once.</item>
    /// </list>
    /// </summary>
    public sealed class SpeedCourseHazardHost : ISpeedHazardDriver, IHazardHost
    {
        /// <summary>How long a hazard whose start was refused (other than by the master switch) waits before trying again.</summary>
        public const double RefusalBackoffSeconds = 5.0;

        /// <summary>A token hazard whose starts keep being refused this long is given up and its token granted.</summary>
        public const double RefusalGrantSeconds = 30.0;

        /// <summary>
        /// An on_named_dead_and_in_region hazard (the south memory) stops only after the runner has been outside its
        /// trigger area this long without a break, so stepping off the pads does not reset the sequence.
        /// </summary>
        public const double OutsideGraceSeconds = 3.0;

        private sealed class Slot
        {
            public SpeedHazardDef Def;
            public int Index;
            public HazardRegion Region;

            /// <summary>When the runner left the trigger area while this hazard ran (the grace clock), or null.</summary>
            public double? OutsideSince;

            /// <summary>When the current streak of refused starts began, or null.</summary>
            public double? FirstRefusedAt;

            /// <summary>The live run's id; 0 when none is running (a stop-requested run counts as not running).</summary>
            public int RunId;

            public int Attempt;
            public double? LastStart;
            public double? LastEndTime;
            public SpeedHazardEnd LastEnd;
            public bool Contributed;
            public double RefusedUntil;
            public bool RefusalLogged;

            /// <summary>
            /// A boss_phase step's hazard already ended Failed/Aborted once since the phase machine started this step, and
            /// was restarted. Reset by every StartStep; a second such end advances the phase.
            /// </summary>
            public bool StepRestarted;
        }

        private readonly SpeedCourseRun run;
        private readonly ISpeedHazardPort port;
        private readonly List<Slot> slots = new List<Slot>();

        /// <summary>Every run this host started whose OnEnded has not been handled yet (stop-requested ones included).</summary>
        private readonly Dictionary<int, Slot> pending = new Dictionary<int, Slot>();

        private readonly object gate = new object();

        private int stopped;
        private bool masterOffLogged;
        private double lastNow;

        /// <summary>The course's boss phases, or null when it declares none.</summary>
        private readonly SpeedBossPhaseMachine bossPhases;

        /// <summary>The boss phases, for tests and logs; null when the course declares none.</summary>
        public SpeedBossPhaseMachine BossPhases => bossPhases;

        public SpeedCourseHazardHost(SpeedCourseRun run, ISpeedHazardPort port)
        {
            this.run = run ?? throw new ArgumentNullException(nameof(run));
            this.port = port ?? throw new ArgumentNullException(nameof(port));

            var hazards = run.Course?.Hazards;

            if (hazards == null)
                return;

            for (var i = 0; i < hazards.Count; i++)
            {
                var def = hazards[i];

                if (def?.Region == null || def.Trigger == null)
                    continue;

                slots.Add(new Slot
                {
                    Def = def,
                    Index = i,
                    Region = def.Region.Build($"speed:{def.Id}", port.LandblockId, run.Instance),
                });
            }

            if (run.Course.BossPhases != null && run.Course.BossPhases.Count > 0)
                bossPhases = new SpeedBossPhaseMachine(run.Course.BossPhases, new PhasePort(this));
        }

        public bool PlayerFacing => true;

        public bool IsStopped => Volatile.Read(ref stopped) != 0;

        private ISpeedCourseWorld World => run.World;

        // =====================================================================================================
        // Tick (landblock thread)
        // =====================================================================================================

        public void Tick(SpeedCourseRun r, in SpeedCourseRunSnapshot s)
        {
            lock (gate)
            {
                if (IsStopped)
                    return;

                lastNow = s.Now;

                foreach (var slot in slots)
                {
                    var runner = s.Runner;
                    var inRegion = InTriggerArea(slot, runner, s.Now);
                    var view = s.ForHazard(inRegion, slot.RunId != 0, slot.LastStart, slot.LastEndTime, slot.LastEnd);

                    var decision = SpeedCourseRules.DecideHazard(slot.Def.Trigger, slot.Def.RetrySeconds, view);

                    // The refusal grant needs an UNBROKEN streak of wanted-but-refused starts: any tick that does not want
                    // a start (the runner left, the trigger lapsed) ends the streak.
                    if (decision != SpeedHazardDecision.Start)
                        slot.FirstRefusedAt = null;

                    switch (decision)
                    {
                        case SpeedHazardDecision.Start:
                            TryStart(slot, s.Now);
                            break;

                        case SpeedHazardDecision.Stop:
                            StopSlot(slot, s.Now, $"trigger {slot.Def.Trigger.Type} no longer holds");
                            break;
                    }
                }

                if (bossPhases != null)
                {
                    var runnerView = s.Runner;
                    var bossAlive = s.BossSpawned && !s.BossDead && !run.Boss.IsNone;

                    bossPhases.Tick(s.Now, bossAlive,
                        bossAlive ? World.CurrentHealth(run.Boss) : 0u,
                        bossAlive ? World.MaxHealth(run.Boss) : 0u,
                        area => runnerView.HasPosition && area.Contains(runnerView.X, runnerView.Y, runnerView.Z));
                }
            }
        }

        private Slot FindSlot(string hazardId)
        {
            foreach (var slot in slots)
                if (string.Equals(slot.Def.Id, hazardId, StringComparison.Ordinal))
                    return slot;

            return null;
        }

        /// <summary>The boss phase machine's view of this host (called under <see cref="gate"/>, landblock thread).</summary>
        private sealed class PhasePort : ISpeedBossPhasePort
        {
            private readonly SpeedCourseHazardHost host;

            public PhasePort(SpeedCourseHazardHost host) => this.host = host;

            public void SetGuard(bool immune, uint floorHp)
            {
                if (!host.run.Boss.IsNone)
                    host.World.SetBossGuard(host.run.Boss, immune, floorHp);
            }

            public SpeedBossStepStart StartStep(string hazardId, double now)
            {
                var slot = host.FindSlot(hazardId);

                if (slot == null)
                    return SpeedBossStepStart.Disabled;

                if (slot.RunId != 0)
                    return SpeedBossStepStart.Started;

                if (!host.port.Enabled)
                {
                    host.TryStart(slot, now); // logs the master switch once
                    return SpeedBossStepStart.Disabled;
                }

                // a fresh step: its hazard gets one restart again if it fails or aborts on its own
                slot.StepRestarted = false;

                host.TryStart(slot, now);
                return slot.RunId != 0 ? SpeedBossStepStart.Started : SpeedBossStepStart.Refused;
            }

            public void StopStep(string hazardId, string reason)
            {
                var slot = host.FindSlot(hazardId);

                if (slot != null && slot.RunId != 0)
                    host.StopSlot(slot, host.lastNow, reason);
            }

            public void Log(string line) => host.World.Log($"[SPEEDCOURSE] {host.run} {line}");
        }

        /// <summary>
        /// The runner is in the hazard's trigger area (<see cref="SpeedHazardDef.TriggerArea"/>). For a running
        /// on_named_dead_and_in_region hazard, leaving counts only after <see cref="OutsideGraceSeconds"/> outside
        /// without a break.
        /// </summary>
        private static bool InTriggerArea(Slot slot, SpeedRunnerView runner, double now)
        {
            var area = slot.Def.TriggerArea;
            var inside = runner.HasPosition && area.Contains(runner.X, runner.Y, runner.Z);

            var graced = slot.RunId != 0
                && string.Equals(slot.Def.Trigger.Type, SpeedHazardTriggers.OnNamedDeadAndInRegion, StringComparison.Ordinal);

            if (inside || !graced)
            {
                slot.OutsideSince = null;
                return inside;
            }

            slot.OutsideSince ??= now;
            return now - slot.OutsideSince.Value < OutsideGraceSeconds;
        }

        private void TryStart(Slot slot, double now)
        {
            if (!port.Enabled)
            {
                if (!masterOffLogged)
                {
                    masterOffLogged = true;
                    World.Log($"[SPEEDCOURSE] {run} hazard mechanics are switched off (hazard_mech_enabled is false); this run continues without hazards");
                }

                // A hazard the runner needs for a Seal token can never be played: the course grants the token instead.
                if (!string.IsNullOrEmpty(slot.Def.Token))
                    run.GrantToken(slot.Def.Token, $"hazard '{slot.Def.Id}' cannot start: hazard mechanics are switched off");

                return;
            }

            if (now < slot.RefusedUntil)
                return;

            var def = slot.Def;
            var seed = SpeedCourseRules.HazardSeed(run.Course.Seed, slot.Index, slot.Attempt);
            var callerArgs = CallerArgs(def, out var note);

            var request = new SpeedHazardStart
            {
                Hazard = def,
                SlotIndex = slot.Index,
                Attempt = slot.Attempt,
                Region = slot.Region,
                Kind = def.Kind,
                Pattern = string.IsNullOrWhiteSpace(def.Pattern) ? null : def.Pattern,
                CallerArgs = callerArgs,
                SettingsLayer = def.ToHazardArgs(),
                SettingsLayerName = $"speed-courses.json hazard '{def.Id}' args",
                Seed = seed,
            };

            int id;
            string error;

            try
            {
                id = port.Start(request, this, out error);
            }
            catch (Exception ex)
            {
                id = 0;
                error = $"threw: {ex.Message}";
            }

            if (id <= 0)
            {
                slot.RefusedUntil = now + RefusalBackoffSeconds;
                slot.FirstRefusedAt ??= now;

                if (!slot.RefusalLogged)
                {
                    slot.RefusalLogged = true;
                    World.LogWarn($"[SPEEDCOURSE] {run} hazard '{def.Id}' start refused: {error} (retrying every {RefusalBackoffSeconds:0}s, logged once)");
                }

                if (!string.IsNullOrEmpty(def.Token) && now - slot.FirstRefusedAt.Value >= RefusalGrantSeconds)
                    run.GrantToken(def.Token, $"hazard '{def.Id}' starts refused for {now - slot.FirstRefusedAt.Value:0}s: {error}");

                return;
            }

            slot.FirstRefusedAt = null;
            slot.RunId = id;
            slot.LastStart = now;
            pending[id] = slot;

            World.Log($"[SPEEDCOURSE] {run} hazard '{def.Id}' started as run #{id} ({def.Kind}/{request.Pattern ?? "default"}, seed {seed}, attempt {slot.Attempt}{note})");

            slot.Attempt++;
        }

        /// <summary>The caller args for one start: the scaled pct for a damaging hazard and, for a boss-centred one, cx/cy.</summary>
        private HazardArgs CallerArgs(SpeedHazardDef def, out string note)
        {
            var tokens = new List<string>();
            note = "";

            if (def.PctAuthored > 0 && !string.Equals(def.Kind, HazardMechanicRegistry.Memory, StringComparison.OrdinalIgnoreCase))
            {
                var pct = SpeedCourseRules.ScaledPct(def, run.RunLevel);
                tokens.Add(HazardDefaults.PctKey + "=" + Num(pct));
                note += $", pct {Num(pct)}";
            }

            if (def.OriginAtBoss)
            {
                if (!run.Boss.IsNone && World.TryGetPosition(run.Boss, out var x, out var y))
                {
                    tokens.Add("cx=" + Num(x));
                    tokens.Add("cy=" + Num(y));
                    note += $", origin boss ({Num(x)}, {Num(y)})";
                }
                else
                {
                    note += ", origin region (boss position unavailable)";
                }
            }

            return HazardArgs.FromTokens(tokens);
        }

        private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        private void StopSlot(Slot slot, double now, string reason)
        {
            var id = slot.RunId;

            slot.RunId = 0;
            slot.LastEnd = SpeedHazardEnd.Stopped;
            slot.LastEndTime = now;

            try
            {
                port.Stop(id, reason);
            }
            catch (Exception ex)
            {
                World.LogError($"[SPEEDCOURSE] {run} hazard '{slot.Def.Id}' stop of run #{id} threw: {ex}");
            }
        }

        // =====================================================================================================
        // IHazardHost (any thread) -> landblock thread
        // =====================================================================================================

        /// <summary>Any thread. Never handled here: posted onto the instance landblock.</summary>
        public void OnEnded(HazardRun hazardRun, HazardOutcome outcome)
        {
            if (hazardRun != null)
                OnRunEnded(hazardRun.Id, outcome, hazardRun.EndReason);
        }

        /// <summary>
        /// The id form of <see cref="OnEnded"/> (any thread): posts <see cref="HandleEnded"/> onto the landblock.
        /// <paramref name="reason"/> is the run's own end reason (HazardRun.EndReason), quoted when a boss step fails.
        /// </summary>
        public void OnRunEnded(int runId, HazardOutcome outcome, string reason = null)
        {
            try
            {
                World.Post(() => HandleEnded(runId, outcome, reason));
            }
            catch (Exception ex)
            {
                World.LogError($"[SPEEDCOURSE] {run} could not post the end of hazard run #{runId}: {ex}");
            }
        }

        private static SpeedHazardEnd MapOutcome(HazardOutcome outcome)
        {
            switch (outcome)
            {
                case HazardOutcome.Completed: return SpeedHazardEnd.Completed;
                case HazardOutcome.Failed:
                case HazardOutcome.Aborted: return SpeedHazardEnd.Failed;
                default: return SpeedHazardEnd.Stopped;
            }
        }

        /// <summary>Landblock thread.</summary>
        private void HandleEnded(int runId, HazardOutcome outcome, string reason)
        {
            string contributeToken = null;
            string hazardId;

            lock (gate)
            {
                try
                {
                    port.Forget(runId);
                }
                catch (Exception ex)
                {
                    World.LogError($"[SPEEDCOURSE] {run} forgetting hazard run #{runId} threw: {ex}");
                }

                if (!pending.Remove(runId, out var slot))
                    return;

                hazardId = slot.Def.Id;

                if (IsStopped || run.IsEnded)
                    return;

                var completed = outcome == HazardOutcome.Completed;

                if (completed && !string.IsNullOrEmpty(slot.Def.Token) && !slot.Contributed)
                {
                    slot.Contributed = true;
                    contributeToken = slot.Def.Token;
                }

                // A run the host already stopped keeps its Stopped end unless it had in fact Completed first.
                var ownEnd = slot.RunId == runId;

                if (ownEnd || (completed && slot.RunId == 0))
                {
                    slot.RunId = 0;
                    slot.LastEnd = MapOutcome(outcome);
                    slot.LastEndTime = lastNow;
                }

                // A boss phase step is done when its run ends without this host stopping it: Completed, Failed, Aborted,
                // or an outside stop (/hazard stop, the kill switch) or reap, which would otherwise hold the boss immune
                // until the phase timeout. A run this host stopped (phase timeout, boss dead) has RunId 0 already and
                // was accounted for by the machine; teardown returned above.
                //
                // THE ABORT GUARD: a step's hazard that FAILS or ABORTS on its own is a broken step, not a played one
                // (2026-10-25: every rain marker was refused, rain aborted in 8 s and the 75% immunity phase was skipped
                // with nothing in the log above INFO). The first such end is logged as an error and the hazard restarted
                // with the next seed, without advancing; a second one, or a refused restart, advances as before.
                if (ownEnd && bossPhases != null
                    && string.Equals(slot.Def.Trigger?.Type, SpeedHazardTriggers.BossPhase, StringComparison.Ordinal))
                {
                    var broken = outcome == HazardOutcome.Failed || outcome == HazardOutcome.Aborted;

                    if (broken && !slot.StepRestarted)
                    {
                        slot.StepRestarted = true;
                        World.LogError($"[SPEEDCOURSE] {run} boss step '{hazardId}' run #{runId} ended {outcome} ({reason ?? "no reason given"}); restarting it once with the next seed (attempt {slot.Attempt})");

                        TryStart(slot, lastNow);

                        if (slot.RunId == 0)
                        {
                            World.LogError($"[SPEEDCOURSE] {run} boss step '{hazardId}' restart was refused; the phase advances");
                            bossPhases.OnStepEnded(hazardId, lastNow);
                        }
                    }
                    else
                    {
                        if (broken)
                            World.LogError($"[SPEEDCOURSE] {run} boss step '{hazardId}' run #{runId} ended {outcome} again after its restart ({reason ?? "no reason given"}); the phase advances");

                        bossPhases.OnStepEnded(hazardId, lastNow);
                    }
                }
            }

            World.Log($"[SPEEDCOURSE] {run} hazard '{hazardId}' run #{runId} ended {outcome}");

            // Through the run, which contributes with nobody notified and tells the runner the course's own line.
            if (contributeToken != null)
                run.EarnToken(contributeToken, $"hazard '{hazardId}' completed");
        }

        // =====================================================================================================
        // Teardown (any thread)
        // =====================================================================================================

        public void Stop(SpeedCourseRun r, string reason)
        {
            if (Interlocked.Exchange(ref stopped, 1) != 0)
                return;

            var ids = new List<(int Id, string Hazard)>();

            lock (gate)
            {
                foreach (var slot in slots)
                {
                    if (slot.RunId == 0)
                        continue;

                    ids.Add((slot.RunId, slot.Def.Id));
                    slot.RunId = 0;
                }
            }

            foreach (var (id, hazard) in ids)
            {
                try
                {
                    port.Stop(id, $"speed course ended: {reason}");
                }
                catch (Exception ex)
                {
                    World.LogError($"[SPEEDCOURSE] {run} hazard '{hazard}' stop of run #{id} threw at teardown: {ex}");
                }
            }

            // A run that ends mid-phase (runner offline, re-register) must not leave its boss immune and floored for as
            // long as the instance lives: release the guard. Posted, because Stop runs on whichever thread ended the run
            // and SetBossGuard touches the creature; a gone landblock simply drops it with the boss.
            if (bossPhases != null && !run.Boss.IsNone)
            {
                var boss = run.Boss;

                try
                {
                    World.Post(() => World.SetBossGuard(boss, false, 0));
                }
                catch (Exception ex)
                {
                    World.LogError($"[SPEEDCOURSE] {run} could not post the boss guard release at teardown: {ex}");
                }
            }

            World.Log($"[SPEEDCOURSE] {run} hazards stopped ({reason}): {ids.Count} live run(s)");
        }
    }
}
