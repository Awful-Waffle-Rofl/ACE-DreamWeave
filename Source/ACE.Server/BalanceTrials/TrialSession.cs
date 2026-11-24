using System;
using System.Collections.Generic;
using System.Linq;

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

        /// <summary>The scenario's rep count.</summary>
        public int Reps => _scenario.Reps;

        /// <summary>The scenario this session runs (the run row's scenario_id).</summary>
        public TrialScenario Scenario => _scenario;

        /// <summary>The world seam this session drives (TrialManager keeps it beside the session for its player-gone sweep).</summary>
        internal ITrialWorld World => _world;

        public TrialPhase Phase { get; private set; }
        public int RepsDone { get; private set; }
        public TrialRep CurrentRep { get; private set; }

        /// <summary>Set by Task 7's pre-spawn drift check (through TrialRep.RebuildRequired, or directly); blocks TryNext until cleared. Written only on the room's thread.</summary>
        public bool RebuildRequired { get; set; }

        public string LastEndState { get; private set; }
        public string LastRefusal { get; private set; }

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

            if (RebuildRequired)
                return Refuse("template rebuild required", out refusal);

            if (RepsDone >= _scenario.Reps)
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

            // A drifted rep runs, flagged; the template must be rebuilt before the next one (Task 7's drift rule).
            if (rep.DigestDrift)
                RebuildRequired = true;
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

            AdvanceWaves(now);
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
                Phase = TrialPhase.Aborted;
        }

        private void RefreshPreparing()
        {
            if (Phase != TrialPhase.Preparing || !_world.RoomLoaded)
                return;

            if (_world.LiveForeignCreatures() > 0)
            {
                LastRefusal = "room not empty";
                Phase = TrialPhase.Aborted;
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

            _world.DestroyAll();

            if (returnPlayer)
                _world.ReturnPlayerToStart();

            if (endState == TrialEventKinds.EndForfeit)
                Phase = TrialPhase.Aborted;
            else
                Phase = RepsDone >= _scenario.Reps ? TrialPhase.Done : TrialPhase.Ready;

            RepEnded?.Invoke(rep, endState);
        }
    }
}
