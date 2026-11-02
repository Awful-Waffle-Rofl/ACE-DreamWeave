using System;

using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// WaffleACE: the bound on a FastTick cast waiting to face its target.
    ///
    /// A FastTick targeted cast does not release (before the windup, and again after the launch gesture) until the
    /// player faces the target within spellcast_max_angle. Before this bound, that wait could last forever:
    /// - the server turn-to (TurnTo_Magic -> CreateTurnToChain2) only reports back through
    ///   MoveToManager -> Player.OnMoveComplete -> OnMoveComplete_Magic, and any client MoveToState cancels it
    ///   (GameActionMoveToState -> StopExistingMoveToChains2 -> cancel_moveto -> CancelMoveTo(ActionCancelled)), after
    ///   which the cast re-checks the angle and re-issues the turn - so a client that keeps moving keeps cancelling it;
    /// - a turn waiting behind IsAnimating never starts, and the motion-stall watchdog deliberately ignores a casting
    ///   player (MotionStallWatchdog.IsStalled);
    /// - a held client turn key parks the cast in PendingTurnRelease, released only by a MoveToState with no turn
    ///   command or by Player.CheckTurn, which runs only while the player is moving or animating.
    /// While waiting the player stays IsBusy, so every other action answers YoureTooBusy.
    ///
    /// The clock (MagicState.TurnWaitStart) starts when a phase first waits and is not restarted by a re-issue; this
    /// class holds the pure decision, Player.CheckCastTurnTimeout the per-tick call site. Pure so ACE.Server.Tests
    /// can reach it: Player cannot be constructed there (its constructor needs a live database).
    /// </summary>
    public static class CastTurnTimeout
    {
        public const string TimeoutKey = "spellcast_turn_timeout_seconds";

        /// <summary>
        /// Default bound, seconds. A normal server turn of 180 degrees at the player TurnToSpeed takes well under 2 s,
        /// so 5 s only fails a cast that is not going to face its target on its own.
        /// </summary>
        public const double DefaultTimeoutSeconds = 5.0;

        /// <summary>Floor for an enabled bound, so a typo cannot fail ordinary casts mid-turn.</summary>
        public const double MinTimeoutSeconds = 1.0;

        /// <summary>The live tunable, through a seam: PropertyManager reads throw in the test harness.</summary>
        public static Func<double> TimeoutSeconds = () =>
        {
            try
            {
                return ClampTimeout(PropertyManager.GetDouble(TimeoutKey, DefaultTimeoutSeconds).Item);
            }
            catch (Exception)
            {
                return DefaultTimeoutSeconds;
            }
        };

        /// <summary>NaN/infinity -> default; 0 or less -> 0 (bound disabled); otherwise at least MinTimeoutSeconds.</summary>
        public static double ClampTimeout(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                return DefaultTimeoutSeconds;

            if (seconds <= 0)
                return 0;

            return Math.Max(MinTimeoutSeconds, seconds);
        }

        /// <summary>
        /// TRUE when a FastTick cast has been waiting on its facing for at least timeoutSeconds. FALSE for a
        /// non-FastTick player (whose cast never waits on facing), a player not casting, a cast not waiting
        /// (waitStart == DateTime.MinValue), or a disabled bound (timeoutSeconds 0 or less).
        /// </summary>
        public static bool IsExpired(bool fastTick, bool isCasting, DateTime waitStart, DateTime now, double timeoutSeconds)
        {
            if (!fastTick || !isCasting || waitStart == DateTime.MinValue || timeoutSeconds <= 0)
                return false;

            return (now - waitStart).TotalSeconds >= timeoutSeconds;
        }

        /// <summary>
        /// TRUE when the turn is waiting behind an animation queue that is still healthy: blocked on IsAnimating
        /// (MotionStallWatchdog.IsTurnBlockedOnAnimation) behind a queue that has not yet been continuously non-empty
        /// for longer than the motion-stall watchdog's staleness threshold (motion_stall_threshold_seconds). A long
        /// legitimate animation (AFKState 5.7 s, UseMagicStaff 6.0 s, EnterPKLite 10.3 s) must not count against the
        /// cast clock; a dead queue past the threshold does.
        /// </summary>
        public static bool IsHeldByHealthyAnimation(bool turnBlockedOnAnimation, double queueStaleSeconds, double staleThresholdSeconds)
        {
            return turnBlockedOnAnimation && queueStaleSeconds <= staleThresholdSeconds;
        }

        /// <summary>
        /// The clock pause: while the turn is held by a healthy animation, the wait start moves forward by the time
        /// since the last check, so that time is not counted. Otherwise unchanged.
        /// </summary>
        public static DateTime AdvanceWaitStart(DateTime waitStart, DateTime lastCheck, DateTime now, bool heldByHealthyAnimation)
        {
            if (!heldByHealthyAnimation || waitStart == DateTime.MinValue || lastCheck == DateTime.MinValue || now <= lastCheck)
                return waitStart;

            return waitStart + (now - lastCheck);
        }

        /// <summary>
        /// Most seconds one phase's clock may be paused behind healthy animations in total:
        /// max(2 x timeout, stale threshold + timeout). Long enough for any single healthy animation in the human motion
        /// table to finish and the turn to complete; past it the hold is ignored (MagicState.TickTurnWait, Decide).
        /// </summary>
        public static double MaxPauseSeconds(double timeoutSeconds, double staleThresholdSeconds)
        {
            return Math.Max(2.0 * timeoutSeconds, staleThresholdSeconds + timeoutSeconds);
        }

        public enum Outcome
        {
            /// <summary>Keep waiting.</summary>
            Wait,

            /// <summary>Pre-windup phase expired: FailCast (no mana or components spent yet).</summary>
            Fail,

            /// <summary>Post-gesture phase expired: release the spell without the angle check, as non-FastTick does.</summary>
            Release,
        }

        public struct Decision
        {
            public Outcome Outcome;

            /// <summary>Clear the dead animation queue (Player.ClearMotionStall) before resolving the cast.</summary>
            public bool ClearQueue;
        }

        /// <summary>
        /// The whole per-tick decision Player.CheckCastTurnTimeout acts on. Expired and not held by a healthy animation:
        /// Fail before the windup, Release after the launch gesture (castMotionDone). ClearQueue when the turn is blocked
        /// behind a stale queue and the motion-stall kill switch (motion_stall_autoclear) is on: the cast clock has
        /// already served as the watchdog's blocked-turn dwell, so the clear does not wait on that second gate.
        /// </summary>
        public static Decision Decide(bool fastTick, bool isCasting, DateTime waitStart, DateTime now, double timeoutSeconds,
            bool castMotionDone, bool turnBlockedOnAnimation, double queueStaleSeconds, double staleThresholdSeconds, bool autoClearEnabled,
            bool pauseExhausted = false)
        {
            if (!IsExpired(fastTick, isCasting, waitStart, now, timeoutSeconds))
                return new Decision { Outcome = Outcome.Wait };

            // a healthy animation holds the cast only until the phase's pause budget is spent
            if (!pauseExhausted && IsHeldByHealthyAnimation(turnBlockedOnAnimation, queueStaleSeconds, staleThresholdSeconds))
                return new Decision { Outcome = Outcome.Wait };

            return new Decision
            {
                Outcome = castMotionDone ? Outcome.Release : Outcome.Fail,

                // only a queue the watchdog itself would call stale is cleared
                ClearQueue = autoClearEnabled && turnBlockedOnAnimation && queueStaleSeconds > staleThresholdSeconds,
            };
        }
    }
}
