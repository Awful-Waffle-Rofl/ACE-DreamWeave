using System.Collections.Generic;

namespace ACE.Server.BalanceTrials
{
    /// <summary>
    /// The closed vocabularies of the Balance Trials recorder (trial_event.kind, trial_event.dir and
    /// trial_run.end_state in ace_analytics.sql). This file is the ONLY place these strings are spelled
    /// (design 12 invariant): every writer and every reader names the constant, never the literal.
    /// Each value must fit its column (kind VARCHAR(12), dir VARCHAR(3), end_state VARCHAR(8));
    /// AnalyticsTrialSchemaTests pins that.
    /// </summary>
    public static class TrialEventKinds
    {
        public const string Hit = "hit";
        public const string Miss = "miss";
        public const string Evade = "evade";
        public const string Resist = "resist";
        public const string Kill = "kill";
        public const string Death = "death";
        public const string Consume = "consume";
        public const string Proc = "proc";
        public const string Spawn = "spawn";
        public const string VitalLow = "vital_low";

        /// <summary>Every trial_event.kind value.</summary>
        public static readonly IReadOnlyList<string> All = new[]
        {
            Hit, Miss, Evade, Resist, Kill, Death, Consume, Proc, Spawn, VitalLow,
        };

        /// <summary>trial_event.dir: the trial character dealt the event.</summary>
        public const string DirOut = "out";

        /// <summary>trial_event.dir: the trial character received the event.</summary>
        public const string DirIn = "in";

        /// <summary>Every trial_event.dir value.</summary>
        public static readonly IReadOnlyList<string> AllDirs = new[] { DirOut, DirIn };

        public const string EndCleared = "cleared";
        public const string EndDied = "died";
        public const string EndTimeout = "timeout";
        public const string EndForfeit = "forfeit";

        /// <summary>Every trial_run.end_state value.</summary>
        public static readonly IReadOnlyList<string> AllEndStates = new[]
        {
            EndCleared, EndDied, EndTimeout, EndForfeit,
        };
    }
}
