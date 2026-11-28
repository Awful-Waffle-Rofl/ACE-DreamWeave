using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// One rep of a trial. prepareRep builds it before any spawn; the session fills the run-time fields. Shortfalls, Refusal and
    /// RebuildRequired are how prepareRep (Tasks 3, 5 and 7) tells the session to stop before the first spawn.
    /// </summary>
    public sealed class TrialRep
    {
        /// <summary>Guid.NewGuid().ToString("N"), one per rep.</summary>
        public string RunKey { get; init; } = Guid.NewGuid().ToString("N");

        /// <summary>1-based rep number.</summary>
        public int Number { get; internal set; }

        public TrialGemRoll Gem { get; init; }

        public DungeonSpawnPlan Plan { get; init; }

        public IReadOnlyList<IReadOnlyList<DungeonSpawnPlanEntry>> Waves { get; init; } = Array.Empty<IReadOnlyList<DungeonSpawnPlanEntry>>();

        /// <summary>Plan shortfall lines from TrialWavePlan.Select. Any line aborts the rep and the session before any spawn.</summary>
        public IReadOnlyList<string> Shortfalls { get; init; } = Array.Empty<string>();

        /// <summary>Non-null: the rep is refused before any spawn (a pre-spawn step failed). The session stays Ready.</summary>
        public string Refusal { get; init; }

        /// <summary>True: a pre-spawn step (the hardCopyEvery schedule, or a failed purge) wants the template rebuilt. Sets the session's RebuildRequired and refuses the rep.</summary>
        public bool RebuildRequired { get; init; }

        /// <summary>Index of the wave currently live.</summary>
        public int WaveIndex { get; internal set; }

        public double StartedAt { get; internal set; }

        /// <summary>The session clock when the rep ended (EndRep), before RepEnded fires. The run row's duration ends here.</summary>
        public double EndedAt { get; internal set; }

        /// <summary>The rep character's level when the rep started (TrialRunLog), recorded as char_level.</summary>
        internal int CharLevelAtStart { get; set; }

        public string EndState { get; internal set; }

        public uint ObservedMaxNonBoss { get; set; }

        /// <summary>The rep's state digest Core (TrialDigest.Core), recorded as state_digest. Null when no pre-spawn setup ran, and when the master digest was not in hand (drift unknown; DigestDrift is then false).</summary>
        public string StateDigest { get; init; }

        /// <summary>True: the pre-spawn drift check flagged this rep. It runs, flagged, and nothing is owed for it.</summary>
        public bool DigestDrift { get; init; }

        /// <summary>
        /// The rep's recorder: created by TrialRunLog when the rep starts (TrialSession.RepStarted) and set back to null once its run
        /// row is written, after which every hook drops its event. Read by the hooks in TrialHooks.
        /// </summary>
        internal TrialRecorder Recorder { get; set; }

        /// <summary>
        /// The player half of the spawn predictions: set by LiveTrialWorld.SpawnWave from the session's TrialPlayerSideSource just
        /// before each spawn (null until it has arrived, or when it failed: spawns then carry no prediction). Read on the room thread.
        /// </summary>
        internal TrialPlayerSide PlayerSide { get; set; }

        /// <summary>The boss's spawn prediction (TrialSpawner.Spawn), which fills the run row's three pred_* columns. Null when none was made.</summary>
        internal TrialPrediction BossPrediction { get; set; }

        /// <summary>
        /// The start-of-rep aggro grace in seconds (the scenario's startGraceSecs), set by the session when the rep starts. Only
        /// wave 0 is held (<see cref="StartGraceSecsFor"/>). 0: no grace.
        /// </summary>
        internal int StartGraceSecs { get; set; }

        /// <summary>The hold seconds a wave spawned now gets: the grace for wave 0, 0 for every later wave.</summary>
        internal int StartGraceSecsFor(int waveIndex) => waveIndex == 0 && StartGraceSecs > 0 ? StartGraceSecs : 0;

        /// <summary>How many wave-0 creatures entered the world held (LiveTrialWorld.SpawnWave). Room thread.</summary>
        internal int GraceHeld { get; set; }

        /// <summary>TrialManager.Now() at which the wave-0 hold runs out (LiveTrialWorld.SpawnWave); 0 when nothing was held. Room thread.</summary>
        internal double GraceUntil { get; set; }

        private int graceEarlyWakes;

        /// <summary>How many held creatures were woken before the grace ran out by an attack (Creature.BreakTrialHold).</summary>
        internal int GraceEarlyWakes => System.Threading.Volatile.Read(ref graceEarlyWakes);

        internal void NoteGraceEarlyWake() => System.Threading.Interlocked.Increment(ref graceEarlyWakes);

        /// <summary>True once the session has logged the grace release for this rep. Room thread.</summary>
        internal bool GraceReleaseLogged { get; set; }
    }
}
