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

        /// <summary>True: a pre-spawn drift check wants the template rebuilt. Sets the session's RebuildRequired and refuses the rep.</summary>
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

        /// <summary>The rep's state digest Core (TrialDigest.Core), recorded as state_digest. Null when no pre-spawn setup ran.</summary>
        public string StateDigest { get; init; }

        /// <summary>True: the pre-spawn drift check flagged this rep. It still runs, flagged, and the session requires a rebuild before the next one.</summary>
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
    }
}
