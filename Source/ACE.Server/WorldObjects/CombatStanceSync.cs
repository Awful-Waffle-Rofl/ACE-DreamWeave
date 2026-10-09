using System;

using ACE.Entity.Enum;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Pure decisions behind the "locked into peace mode" mitigation and its diagnostics: a player's client shows
    /// the combat bar while its own character stays in the peace stance, and only a relog clears it (prod reports
    /// 2026-09-23 and 2026-09-30).
    ///
    /// Kept free of Player and WorldObject so ACE.Server.Tests can reach it: Player's static constructor touches the
    /// world database (see FastTickPolicy). Creature.SetCombatMode, Player.HandleActionChangeCombatMode and
    /// Player.OnMoveToState act on these results.
    /// </summary>
    public static class CombatStanceSync
    {
        /// <summary>
        /// What Creature.SetCombatMode does with a request, before any stance animation work.
        /// </summary>
        public enum StanceRequestAction
        {
            /// <summary>The stance actually changes: run the normal switch.</summary>
            Switch,

            /// <summary>Already in the requested stance and nothing to tell anyone: return at once (monsters).</summary>
            Silent,

            /// <summary>
            /// Already in the requested stance on the server, but the request came from a player: re-send that stance
            /// to the player's own client. A client that asks for a stance the server believes it already holds is,
            /// by definition, not showing that stance - returning silently left it stuck with the combat bar up and
            /// the character in peace, and for missile mode the client cannot leave that state by itself
            /// (see Player.HandleActionChangeCombatMode_Inner).
            /// </summary>
            ResendToSelf,
        }

        /// <summary>
        /// The SetCombatMode short-circuit. A NonCombat request always switches (it always broadcasts the peace
        /// motion), exactly as before; so does any request whose target stance differs from the current one.
        /// </summary>
        public static StanceRequestAction ClassifyStanceRequest(bool isPlayer, CombatMode requested, MotionStance currentStance, MotionStance targetStance)
        {
            if (requested == CombatMode.NonCombat || currentStance != targetStance)
                return StanceRequestAction.Switch;

            return isPlayer ? StanceRequestAction.ResendToSelf : StanceRequestAction.Silent;
        }

        /// <summary>
        /// The stance a client is reporting in a MoveToState raw motion state: the packed CurrentStyle when the flag
        /// is present, otherwise the default NonCombat (the client omits the field when it holds its default, which is
        /// also how Physics.Animation.RawMotionState.SetState reads it).
        /// </summary>
        public static MotionStance ClientReportedStance(bool hasCurrentStyle, MotionStance currentStyle)
        {
            if (!hasCurrentStyle || currentStyle == 0)
                return MotionStance.NonCombat;

            return currentStyle;
        }
    }

    /// <summary>
    /// Per-player, rate-capped gate for the [COMBATMODE] Info lines, plus the "same mode requested again within
    /// <see cref="RepeatWindow"/>" detector that marks a client repeatedly asking for a stance it is not getting.
    ///
    /// Thread-safe. A combat-mode request is handled on the world thread (inbound game actions), but a deferred one
    /// runs later from the player's own action queue, which Landblock.TickSingleThreadedWork drains during the
    /// landblock tick - so both members take a private lock. Uncontended in practice: a handful of calls per request.
    /// </summary>
    public sealed class CombatModeRequestLog
    {
        public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

        public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

        public const int MaxInfoLinesPerWindow = 10;

        private readonly object sync = new object();

        private CombatMode lastRequested = CombatMode.Undef;
        private DateTime lastRequestTime = DateTime.MinValue;

        private DateTime windowStart = DateTime.MinValue;
        private int linesInWindow;
        private int suppressedInWindow;

        /// <summary>
        /// Records a client request and returns TRUE when the same mode was already requested within RepeatWindow.
        /// Only client-originated requests are recorded (see Player.HandleActionChangeCombatMode's clientRequest),
        /// so a server-side re-wield never reads as a client asking twice.
        /// </summary>
        public bool RecordRequest(DateTime now, CombatMode requested)
        {
            lock (sync)
            {
                var repeat = requested == lastRequested && now - lastRequestTime < RepeatWindow;

                lastRequested = requested;
                lastRequestTime = now;

                return repeat;
            }
        }

        /// <summary>
        /// TRUE when one more Info line may be written now. At most MaxInfoLinesPerWindow per RateWindow;
        /// <paramref name="suppressedBefore"/> reports how many were dropped in the previous window. It is reported by
        /// the call that rolls the window over, which always returns TRUE, so the count rides on the next emitted line.
        /// </summary>
        public bool TryTakeInfoLine(DateTime now, out int suppressedBefore)
        {
            lock (sync)
            {
                suppressedBefore = 0;

                if (now - windowStart >= RateWindow)
                {
                    suppressedBefore = suppressedInWindow;
                    windowStart = now;
                    linesInWindow = 0;
                    suppressedInWindow = 0;
                }

                if (linesInWindow >= MaxInfoLinesPerWindow)
                {
                    suppressedInWindow++;
                    return false;
                }

                linesInWindow++;
                return true;
            }
        }
    }

    /// <summary>
    /// Watches for a client whose own reported stance (MoveToState raw style) disagrees with the stance the server
    /// believes it broadcast (CurrentMotionState.Stance). A disagreement held for <see cref="Threshold"/> opens a
    /// streak; a reported streak is announced once when it starts and once when it ends - never a line per packet.
    /// Starts are further capped to one per <see cref="MinReportInterval"/>: a streak that opens sooner is tracked
    /// silently (no start line, no recovery line) and counted, and the count rides on the next reported start
    /// (<see cref="SuppressedStartsBeforeLast"/>).
    ///
    /// Runs per movement packet, so the in-sync path only compares two enums and touches no allocation. Not
    /// thread-safe: fed only from the world thread (inbound MoveToState via Player.OnMoveToState).
    /// </summary>
    public sealed class StanceDesyncWatchdog
    {
        public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

        public static readonly TimeSpan MinReportInterval = TimeSpan.FromSeconds(60);

        public enum Transition
        {
            None,
            StreakStarted,
            Recovered,
        }

        private DateTime mismatchSince = DateTime.MinValue;
        private bool streakOpen;
        private bool streakReported;

        private DateTime lastReportedStart = DateTime.MinValue;
        private int suppressedStarts;

        /// <summary>TRUE while a streak (reported or not) is open.</summary>
        public bool InStreak => streakOpen;

        /// <summary>When the current (or just-ended) mismatch was first observed.</summary>
        public DateTime MismatchSince => mismatchSince;

        /// <summary>For the most recent StreakStarted: how many streak starts were suppressed since the one before it.</summary>
        public int SuppressedStartsBeforeLast { get; private set; }

        /// <param name="suppressed">TRUE while a mismatch is expected: a stance animation is still running
        /// (now &lt; NextUseTime) or the player is teleporting. A suppressed observation never starts a streak and
        /// restarts the pending clock, but it does not close a streak that is already open.</param>
        /// <param name="duration">for Recovered, how long the reported streak lasted</param>
        public Transition Observe(DateTime now, MotionStance clientStance, MotionStance serverStance, bool suppressed, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            // an unknown server stance is not a disagreement
            var inSync = clientStance == serverStance || serverStance == MotionStance.Invalid;

            if (inSync)
            {
                var wasReported = streakOpen && streakReported;

                if (wasReported)
                    duration = now - mismatchSince;

                streakOpen = false;
                streakReported = false;
                mismatchSince = DateTime.MinValue;

                return wasReported ? Transition.Recovered : Transition.None;
            }

            if (streakOpen)
                return Transition.None;

            if (suppressed)
            {
                mismatchSince = DateTime.MinValue;
                return Transition.None;
            }

            if (mismatchSince == DateTime.MinValue)
            {
                mismatchSince = now;
                return Transition.None;
            }

            if (now - mismatchSince < Threshold)
                return Transition.None;

            streakOpen = true;

            if (lastReportedStart != DateTime.MinValue && now - lastReportedStart < MinReportInterval)
            {
                suppressedStarts++;
                return Transition.None;
            }

            streakReported = true;
            lastReportedStart = now;
            SuppressedStartsBeforeLast = suppressedStarts;
            suppressedStarts = 0;

            return Transition.StreakStarted;
        }
    }
}
