using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.BalanceTrials
{
    public enum TrialPhase { Preparing, Ready, Running, Done, Aborted }

    /// <summary>
    /// The pure state machine of one trial. It reads nothing but its constructor arguments and the ITrialWorld seam. Every
    /// method runs on the room landblock's thread (TrialManager.Enqueue and the room's tick), never on the world thread: the
    /// room is a private ephemeral landblock, so its own thread owns the session, its creatures and the rep standing in it.
    /// </summary>
    public sealed class TrialSession
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly TrialScenario _scenario;
        private readonly ITrialWorld _world;
        private readonly Func<int, TrialRep> _prepareRep;
        private List<uint> _liveWave = new List<uint>();
        private int _waveIndex;

        public TrialSession(string trialUuid, TrialScenario scenario, string scenarioSha, TrialArchetype archetype, string archetypeSha,
            TrialRoom room, uint playerGuid, ITrialWorld world, Func<int, TrialRep> prepareRep)
        {
            TrialUuid = trialUuid;
            _scenario = scenario;
            ScenarioSha = scenarioSha;
            Archetype = archetype;
            ArchetypeSha = archetypeSha;
            Room = room;
            PlayerGuid = playerGuid;
            _world = world;
            _prepareRep = prepareRep;
            Phase = TrialPhase.Preparing;
        }

        public string TrialUuid { get; }
        public string ScenarioSha { get; }
        public TrialArchetype Archetype { get; }
        public string ArchetypeSha { get; }
        public TrialRoom Room { get; }
        public uint PlayerGuid { get; }

        /// <summary>The session's rep count: the scenario's, unless a campaign lifted the cap (<see cref="RepCap"/>).</summary>
        public int Reps => RepCap ?? _scenario.Reps;

        /// <summary>A campaign member session's reps cap (TrialScenarioLoader.MaxReps), in place of the scenario's rep count. Null on a plain session. Set before the session is registered.</summary>
        internal int? RepCap { get; set; }

        /// <summary>A campaign member session's deadline on the session clock: TryNext starts no rep at or after it. Null on a plain session. Set before the session is registered.</summary>
        internal double? NoNewRepAfter { get; set; }

        /// <summary>
        /// A fresh read of the session clock for the deadline check (a campaign sets it). TryNext's <c>now</c> can be stale: the room
        /// tick's time is taken on the world thread when the tick is queued. Null: <c>now</c> alone.
        /// </summary>
        internal Func<double> DeadlineClock { get; set; }

        /// <summary>trial_run.driver for this session's reps (the campaign start's driver label); null writes "manual".</summary>
        internal string Driver { get; set; }

        /// <summary>The campaign member this session runs for, or null on a plain session (TrialCampaigns.RoomTick reads it).</summary>
        internal TrialCampaignMember CampaignMember { get; set; }

        /// <summary>The run-row tee: TrialRunLog calls it once per rep with the row it wrote (null when the write failed), on the writing thread.</summary>
        internal Action<TrialRep, ACE.Server.Managers.Analytics.AnalyticsDatabase.TrialRunRow> RunRowWritten { get; set; }

        /// <summary>The scenario this session runs (the run row's scenario_id).</summary>
        public TrialScenario Scenario => _scenario;

        /// <summary>The world seam this session drives (TrialManager keeps it beside the session for its player-gone sweep).</summary>
        internal ITrialWorld World => _world;

        public TrialPhase Phase { get; private set; }
        public int RepsDone { get; private set; }
        public TrialRep CurrentRep { get; private set; }

        /// <summary>Set when a pre-spawn step wants a rebuild (the hardCopyEvery schedule, or a failed purge; through TrialRep.RebuildRequired, or directly); blocks TryNext until cleared. Drift never sets it. Written only on the room's thread.</summary>
        public bool RebuildRequired { get; set; }

        /// <summary>
        /// The rep's own consumable counts by wcid, captured ONCE per session at its first soft reset (TrialStarter.BaselineConsumables).
        /// Every rep's soft reset restocks and trims to it, so a rep keeps what it was started with and only loot picked up in a rep
        /// is trimmed. Null until captured. Room thread only.
        /// </summary>
        public IReadOnlyDictionary<uint, long> ConsumableBaseline { get; set; }

        /// <summary>True once the player has been told this session that a rep differs from its master (one note per session). Room thread only.</summary>
        public bool DriftNoted { get; set; }

        public string LastEndState { get; private set; }
        public string LastRefusal { get; private set; }

        /// <summary>
        /// Fires once, when the session reaches Done or Aborted (the run finished, an abort, a forfeit, a stop, the player leaving),
        /// after the last RepEnded. TrialStarter's closing consumable reset hangs off it. Same thread as the call that ended it.
        /// </summary>
        public event Action<TrialSession> Ended;

        private bool _endedFired;

        private void FireEnded()
        {
            if (_endedFired)
                return;

            _endedFired = true;
            Ended?.Invoke(this);
        }

        /// <summary>Fires once per rep, after DestroyAll. Arguments: the rep and its end state.</summary>
        public event Action<TrialRep, string> RepEnded;

        /// <summary>
        /// Fires once per rep that starts running: after CurrentRep, StartedAt and the Running phase are set, before the first
        /// wave spawns (TrialRunLog creates the rep's recorder here). Every rep that fires it later fires RepEnded.
        /// </summary>
        public event Action<TrialRep> RepStarted;

        public bool TryNext(double now, out string refusal)
        {
            RefreshPreparing();

            if (Phase != TrialPhase.Ready)
                return Refuse($"not ready ({Phase})", out refusal);

            // A campaign starts no rep after its deadline (the rep in flight at the deadline finishes on its own).
            if (NoNewRepAfter is double deadline && Math.Max(now, DeadlineClock?.Invoke() ?? now) >= deadline)
                return Refuse("the campaign deadline has passed", out refusal);

            if (RebuildRequired)
                return Refuse("template rebuild required", out refusal);

            if (RepsDone >= Reps)
                return Refuse("all reps done", out refusal);

            // The room was empty at Preparing -> Ready, but a generator can spawn its children after the landblock loads
            // (WorldObject_Generators), so every rep re-checks before anything is prepared or reset. The session stays Ready.
            if (_world.LiveForeignCreatures() > 0)
                return Refuse("room not empty", out refusal);

            var rep = _prepareRep(RepsDone + 1);

            if (rep == null)
                return Refuse("prepareRep returned no rep", out refusal);

            rep.Number = RepsDone + 1;

            if (rep.RebuildRequired)
            {
                RebuildRequired = true;
                return Refuse(rep.Refusal ?? "template rebuild required", out refusal);
            }

            if (rep.Shortfalls != null && rep.Shortfalls.Count > 0)
            {
                // Nothing spawned and no rep ran: no RepEnded, RepsDone and LastEndState untouched, session stays Ready.
                return Refuse("plan shortfall: " + string.Join("; ", rep.Shortfalls), out refusal);
            }

            if (rep.Refusal != null)
                return Refuse(rep.Refusal, out refusal);

            CurrentRep = rep;
            rep.StartedAt = now;

            // The start-of-rep aggro grace: LiveTrialWorld.SpawnWave holds wave 0 for this long (rep.StartGraceSecsFor).
            rep.StartGraceSecs = _scenario.StartGraceSecs;

            // A drifted rep runs, flagged on its row; drift is information only and owes no rebuild (owner ruling 2026-11-24).
            _waveIndex = 0;
            rep.WaveIndex = 0;
            Phase = TrialPhase.Running;
            LastRefusal = null;

            RepStarted?.Invoke(rep);

            if (!SpawnCurrentWave())
            {
                EndRep(TrialEventKinds.EndForfeit, now, returnPlayer: false);
                refusal = LastRefusal;
                return false;
            }

            refusal = null;
            return true;
        }

        public void Tick(double now)
        {
            if (Phase == TrialPhase.Preparing)
            {
                RefreshPreparing();
                return;
            }

            if (Phase != TrialPhase.Running)
                return;

            if (!_world.PlayerOnline || !_world.PlayerInRoom)
            {
                LastRefusal = _world.PlayerOnline ? "player left the room" : "player went offline";
                EndRep(TrialEventKinds.EndForfeit, now, returnPlayer: false);
                return;
            }

            if (now - CurrentRep.StartedAt > _scenario.TimeoutSecs)
            {
                EndRep(TrialEventKinds.EndTimeout, now, returnPlayer: true);
                return;
            }

            LogGraceRelease(now);

            AdvanceWaves(now);
        }

        /// <summary>
        /// The once-per-rep release line for the start grace. Each held creature releases itself (Creature.Monster_Tick); this only
        /// reports, at the first tick at or past the rep's GraceUntil. A rep that ends first logs its release from EndRep instead
        /// (<see cref="LogGraceReleaseAtRepEnd"/>), so every "applied" line has exactly one "released" line.
        /// </summary>
        private void LogGraceRelease(double now)
        {
            var rep = CurrentRep;

            if (rep == null || rep.GraceReleaseLogged || rep.GraceUntil <= 0 || now < rep.GraceUntil)
                return;

            rep.GraceReleaseLogged = true;

            log.Info(GraceReleaseLine(TrialUuid, rep, now - rep.StartedAt, null));
        }

        /// <summary>EndRep's half of the release line: the rep ended while the grace was still unreported (it had not run out yet).</summary>
        private void LogGraceReleaseAtRepEnd(TrialRep rep, string endState, double now)
        {
            if (rep == null || rep.GraceReleaseLogged || rep.GraceUntil <= 0)
                return;

            rep.GraceReleaseLogged = true;

            log.Info(GraceReleaseLine(TrialUuid, rep, now - rep.StartedAt, endState));
        }

        /// <summary>
        /// The release line text. <paramref name="elapsedSecs"/> is the real time since the rep started; <paramref name="endState"/>
        /// non-null means the rep ended before the grace ran out.
        /// </summary>
        internal static string GraceReleaseLine(string trialUuid, TrialRep rep, double elapsedSecs, string endState)
        {
            var why = endState == null ? "" : $" (rep ended {endState} before the {rep.StartGraceSecs}s grace ran out)";

            return $"[TRIAL] trial {trialUuid} rep {rep.Number} ({rep.RunKey}) start grace released after {Math.Max(0.0, elapsedSecs):0.0}s{why}: "
                + $"{rep.GraceHeld} held, {rep.GraceEarlyWakes} woken early by an attack";
        }

        public void OnCreatureDied(uint guid, double now)
        {
            if (Phase == TrialPhase.Running)
                AdvanceWaves(now);
        }

        public void OnPlayerDied(double now)
        {
            if (Phase == TrialPhase.Running)
            {
                // The death event, before the rep ends (Finish counts it).
                CurrentRep.Recorder?.OnDeath(now);
                EndRep(TrialEventKinds.EndDied, now, returnPlayer: false);
            }
        }

        public void Abort(string reason, double now)
        {
            if (Phase == TrialPhase.Done || Phase == TrialPhase.Aborted)
                return;

            LastRefusal = reason;

            if (Phase == TrialPhase.Running)
                EndRep(TrialEventKinds.EndForfeit, now, returnPlayer: true);
            else
            {
                Phase = TrialPhase.Aborted;
                FireEnded();
            }
        }

        private void RefreshPreparing()
        {
            if (Phase != TrialPhase.Preparing || !_world.RoomLoaded)
                return;

            if (_world.LiveForeignCreatures() > 0)
            {
                LastRefusal = "room not empty";
                Phase = TrialPhase.Aborted;
                FireEnded();
            }
            else
                Phase = TrialPhase.Ready;
        }

        private bool Refuse(string reason, out string refusal)
        {
            LastRefusal = reason;
            refusal = reason;
            return false;
        }

        private bool SpawnCurrentWave()
        {
            var rep = CurrentRep;
            var guids = _waveIndex < rep.Waves.Count ? _world.SpawnWave(rep.Waves[_waveIndex], rep) : null;

            if (guids == null || guids.Count == 0)
            {
                LastRefusal = $"wave {_waveIndex} spawned no creatures";
                _liveWave = new List<uint>();
                return false;
            }

            _liveWave = guids.ToList();
            return true;
        }

        private void AdvanceWaves(double now)
        {
            while (Phase == TrialPhase.Running && _liveWave.All(g => !_world.IsAlive(g)))
            {
                if (_waveIndex + 1 >= CurrentRep.Waves.Count)
                {
                    EndRep(TrialEventKinds.EndCleared, now, returnPlayer: true);
                    return;
                }

                _waveIndex++;
                CurrentRep.WaveIndex = _waveIndex;

                if (!SpawnCurrentWave())
                {
                    EndRep(TrialEventKinds.EndForfeit, now, returnPlayer: false);
                    return;
                }
            }
        }

        private void EndRep(string endState, double now, bool returnPlayer)
        {
            var rep = CurrentRep;
            rep.EndState = endState;
            rep.EndedAt = now;
            LastEndState = endState;
            RepsDone++;
            _liveWave = new List<uint>();

            LogGraceReleaseAtRepEnd(rep, endState, now);

            _world.DestroyAll();

            if (returnPlayer)
                _world.ReturnPlayerToStart();

            if (endState == TrialEventKinds.EndForfeit)
                Phase = TrialPhase.Aborted;
            else
                Phase = RepsDone >= Reps ? TrialPhase.Done : TrialPhase.Ready;

            RepEnded?.Invoke(rep, endState);

            if (Phase == TrialPhase.Done || Phase == TrialPhase.Aborted)
                FireEnded();
        }
    }
}
