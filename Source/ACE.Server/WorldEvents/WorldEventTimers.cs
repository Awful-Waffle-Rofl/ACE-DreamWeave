namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The verdict a single timer evaluation produces. Exactly one verdict per evaluation; the caller acts
    /// on it and evaluates again on the next tick.
    /// </summary>
    public enum TimerVerdict
    {
        None,
        Warn60,
        Warn30,
        FailedTimeout,
        FailedNoParticipants,
        FailedWipe
    }

    /// <summary>
    /// Wall-clock timer arithmetic for a running event (TECH-DESIGN 2.2). Entirely pure so the whole matrix
    /// is testable against an injected clock (D6): the event feeds a snapshot in, gets one verdict back, and
    /// owns all of the side effects.
    /// </summary>
    public static class WorldEventTimers
    {
        /// <summary>
        /// A snapshot of everything the timers read. Plain mutable struct rather than a readonly one so the
        /// call sites can use an object initializer (TECH-DESIGN 5.6 sketches it as a readonly struct with
        /// mutable fields, which does not compile); it is always passed by "in".
        /// </summary>
        public struct TimerInputs
        {
            /// <summary>Current wall clock, unix seconds.</summary>
            public double Now;

            /// <summary>When the event entered Active. 0 while it has not.</summary>
            public double ActiveAt;

            /// <summary>When a participant was last credited (a creature died to a player). 0 for never.</summary>
            public double LastCreditAt;

            public int MaxDurationSeconds;
            public int AbandonAfterSeconds;
            public int WipeGraceSeconds;

            /// <summary>True once any player has ever been credited on this run.</summary>
            public bool AnyoneEverCredited;

            /// <summary>
            /// When a live participant was last seen inside the event area. 0 disables the wipe rule
            /// entirely, which is what WP-02 ships - WP-04 feeds this from the participation ledger.
            /// </summary>
            public double LastAliveParticipantAt;

            /// <summary>
            /// True while any non-staff, living player is inside the event's reward radius (presence, not
            /// kill credit). Defaults to false, which preserves every pre-change verdict for a caller that
            /// does not set it. Guards both FailedNoParticipants branches below - the field being answered
            /// is what keeps a run alive, not whether it has produced a kill yet.
            /// </summary>
            public bool PlayersPresent;
        }

        /// <summary>
        /// Evaluates the terminal and warning timers for one tick.
        ///
        /// Precedence is fixed: FailedTimeout > FailedWipe > FailedNoParticipants > warnings, so a run that
        /// hits its hard duration cap always reports a timeout even if it also looks abandoned.
        ///
        /// FailedNoParticipants now means "the field is empty", not "nothing has died recently" - both of
        /// its branches are guarded by <see cref="TimerInputs.PlayersPresent"/>, so a run with players in it
        /// that never lands a kill runs on to FailedTimeout instead of being abandoned out from under them.
        ///
        /// Warnings fire once each. Warn30 is checked before Warn60 so a tick that skips past both marks
        /// reports the tighter one; the caller must then treat the 60 s mark as spent too (WorldEvent does),
        /// or it would announce "60 seconds remaining" after "30 seconds remaining".
        /// </summary>
        public static TimerVerdict Evaluate(in TimerInputs t, bool warned60, bool warned30)
        {
            // Nothing is timed before the event goes Active.
            if (t.ActiveAt <= 0)
                return TimerVerdict.None;

            var elapsed = t.Now - t.ActiveAt;

            if (t.MaxDurationSeconds > 0 && elapsed >= t.MaxDurationSeconds)
                return TimerVerdict.FailedTimeout;

            if (t.WipeGraceSeconds > 0 && t.AnyoneEverCredited && t.LastAliveParticipantAt > 0 &&
                t.Now - t.LastAliveParticipantAt >= t.WipeGraceSeconds)
                return TimerVerdict.FailedWipe;

            if (t.AbandonAfterSeconds > 0 && !t.PlayersPresent)
            {
                if (!t.AnyoneEverCredited && elapsed >= t.AbandonAfterSeconds)
                    return TimerVerdict.FailedNoParticipants;

                if (t.AnyoneEverCredited && t.Now - t.LastCreditAt >= t.AbandonAfterSeconds)
                    return TimerVerdict.FailedNoParticipants;
            }

            if (t.MaxDurationSeconds > 0)
            {
                var remaining = t.MaxDurationSeconds - elapsed;

                if (remaining <= 30 && !warned30)
                    return TimerVerdict.Warn30;

                if (remaining <= 60 && !warned60)
                    return TimerVerdict.Warn60;
            }

            return TimerVerdict.None;
        }
    }
}
