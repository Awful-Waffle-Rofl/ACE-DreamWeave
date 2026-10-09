using System;
using System.Collections.Generic;
using System.Text;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Physics;
using ACE.Server.Physics.Animation;

using log4net;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// WaffleACE motion-stall watchdog (mitigation, not a root-cause fix).
    ///
    /// The stall: MoveToManager.BeginTurnToHeading does nothing while PhysicsObj.IsAnimating, with no timeout,
    /// and IsAnimating only drops when MotionInterp.PendingMotions drains through MotionDone. If one MotionDone
    /// is ever lost, the queue never drains and every later turn-to (corpse open, any use) waits forever until the
    /// next use cancels it with a silent UseDone. Jumping or portaling clears the queue, which is why both "fix" it.
    /// Why MotionDone goes missing is still unknown; the WARN line this writes is the evidence for that hunt.
    ///
    /// The pure decision lives here, outside Player, because Player's static constructor touches the world
    /// database and so Player statics are unreachable from ACE.Server.Tests. The PropertyManager reads go
    /// through swappable seams because those reads throw in the test harness and this runs on the tick path.
    /// </summary>
    public static class MotionStallWatchdog
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string EnabledKey = "motion_stall_autoclear";
        public const string ThresholdKey = "motion_stall_threshold_seconds";
        public const string TurnBlockedKey = "motion_stall_turn_blocked_seconds";

        public const bool DefaultEnabled = true;

        /// <summary>
        /// Gate 1, the queue clock: seconds PendingMotions must have been continuously non-empty. Owner-ruled default
        /// 10 s. For reference, player animation lengths in the human motion table 0x09000001 (ACE.Content.Tools
        /// motionlength over every MotionCommand in the NonCombat, Magic and HandCombat stances): MarketplaceRecall /
        /// PKArenaRecall 18.37 s, the other recalls 15.06 s, EnterPKLite 10.3 s, UseMagicStaff 6.0 s, AFKState 5.7 s.
        /// 10 s is below the recalls and EnterPKLite, so a single one of those can keep this gate open on its own.
        /// </summary>
        public const double DefaultThresholdSeconds = 10.0;

        /// <summary>
        /// Gate 2, the dwell: seconds the turn itself must have been continuously blocked behind IsAnimating. A
        /// healthy queue releases a waiting turn as soon as what is ahead of it finishes; a stuck queue blocks it
        /// indefinitely. Owner-ruled default 5 s, which is below the recalls (15.06 / 18.37 s), EnterPKLite (10.3 s),
        /// UseMagicStaff (6.0 s) and AFKState (5.7 s), so a turn waiting behind one of those can be blocked longer
        /// than this. This is also the delay a stuck player sees on the click that gets cleared.
        /// </summary>
        public const double DefaultTurnBlockedSeconds = 5.0;

        /// <summary>Floor for the queue clock: a near-zero value would clear the normal per-click Ready node.</summary>
        public const double MinThresholdSeconds = 1.0;

        /// <summary>Floor for the dwell: a normal click blocks its turn for a few ticks while its Ready node drains.</summary>
        public const double MinTurnBlockedSeconds = 0.5;

        /// <summary>Sentinel for "the turn is not currently blocked".</summary>
        public const double NotBlocked = -1.0;

        /// <summary>Most entries the log line / admin output lists before eliding the rest.</summary>
        public const int MaxListedMotions = 12;

        public static Func<bool> Enabled = () =>
        {
            try
            {
                return PropertyManager.GetBool(EnabledKey, DefaultEnabled).Item;
            }
            catch (Exception)
            {
                return DefaultEnabled;
            }
        };

        public static Func<double> ThresholdSeconds = () =>
        {
            try
            {
                return ClampThreshold(PropertyManager.GetDouble(ThresholdKey, DefaultThresholdSeconds).Item);
            }
            catch (Exception)
            {
                return DefaultThresholdSeconds;
            }
        };

        public static Func<double> TurnBlockedSeconds = () =>
        {
            try
            {
                return ClampTurnBlocked(PropertyManager.GetDouble(TurnBlockedKey, DefaultTurnBlockedSeconds).Item);
            }
            catch (Exception)
            {
                return DefaultTurnBlockedSeconds;
            }
        };

        public static double ClampThreshold(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                return DefaultThresholdSeconds;

            return Math.Max(MinThresholdSeconds, seconds);
        }

        public static double ClampTurnBlocked(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                return DefaultTurnBlockedSeconds;

            return Math.Max(MinTurnBlockedSeconds, seconds);
        }

        /// <summary>
        /// The per-player dwell clock: when the turn first became blocked, kept while it stays blocked, and reset to
        /// NotBlocked on any check where it is not.
        /// </summary>
        public static double NextTurnBlockedSince(bool blocked, double since, double now)
        {
            if (!blocked)
                return NotBlocked;

            return since < 0 ? now : since;
        }

        /// <summary>
        /// TRUE when the MoveToManager holds a turn that BeginTurnToHeading is refusing to start because the object
        /// is animating: an active move-to/turn-to whose head node is a TurnToHeading that has not begun turning.
        /// This is the one wait that IsAnimating gates, on both the FastTick (CreateTurnToChain2 /
        /// CreateMoveToChain2) and the melee-charge paths, since both go through MoveToManager.
        /// </summary>
        public static bool IsTurnBlockedOnAnimation(MoveToManager moveToManager)
        {
            if (moveToManager == null || moveToManager.MovementType == MovementType.Invalid)
                return false;

            // AlwaysTurn bypasses the IsAnimating gate in BeginTurnToHeading.
            if (moveToManager.AlwaysTurn)
                return false;

            var pending = moveToManager.PendingActions;
            if (pending == null || pending.Count == 0 || pending[0].Type != MovementType.TurnToHeading)
                return false;

            // a turn already under way is progressing, not waiting
            var command = moveToManager.CurrentCommand;
            return command != (uint)MotionCommand.TurnRight && command != (uint)MotionCommand.TurnLeft;
        }

        /// <summary>Everything the stall decision reads, so it can be tested without a live Player.</summary>
        public struct Snapshot
        {
            public bool FastTick;
            public bool IsAnimating;
            public bool TurnBlocked;
            public bool Contact;
            public bool Teleporting;
            public bool Casting;

            /// <summary>MotionInterp.PendingMotionsNonEmptySince, PhysicsTimer seconds.</summary>
            public double AnimatingSince;

            /// <summary>How long the turn has been continuously blocked, seconds (0 when not blocked).</summary>
            public double TurnBlockedSeconds;
        }

        /// <summary>
        /// Maps the live values Player reads onto a Snapshot. Primitives only, so the mapping is testable without a
        /// Player; Player.CheckMotionStall passes its fields straight through.
        /// </summary>
        public static Snapshot BuildSnapshot(bool fastTick, bool isAnimating, bool turnBlocked, double turnBlockedSince,
            TransientStateFlags transientState, bool teleporting, bool casting, double animatingSince, double now)
        {
            return new Snapshot
            {
                FastTick = fastTick,
                IsAnimating = isAnimating,
                TurnBlocked = turnBlocked,
                Contact = (transientState & TransientStateFlags.Contact) != 0,
                Teleporting = teleporting,
                Casting = casting,
                AnimatingSince = animatingSince,
                TurnBlockedSeconds = turnBlocked && turnBlockedSince >= 0 ? now - turnBlockedSince : 0.0,
            };
        }

        /// <summary>
        /// The stall predicate: both clocks past their gates (see DefaultThresholdSeconds and
        /// DefaultTurnBlockedSeconds). FastTick only: a non-FastTick player's server physics is not simulated between
        /// client reports (EnqueueBroadcastMotion skips ApplyPhysicsMotion for them, Player_Tick only updates their
        /// physics while velocity is non-zero), so a long-lived queue is normal there, and their use path
        /// (CreateMoveToChain) turns by a timed chain that never waits on IsAnimating.
        /// </summary>
        public static bool IsStalled(Snapshot s, double now, double thresholdSeconds, double turnBlockedSeconds)
        {
            if (!s.FastTick || !s.IsAnimating || !s.TurnBlocked)
                return false;

            if (!s.Contact || s.Teleporting || s.Casting)
                return false;

            if (s.TurnBlockedSeconds < turnBlockedSeconds)
                return false;

            return now - s.AnimatingSince > thresholdSeconds;
        }

        /// <summary>The auto-clear decision: the kill switch, then the stall predicate.</summary>
        public static bool ShouldAutoClear(bool enabled, Snapshot s, double now, double thresholdSeconds, double turnBlockedSeconds)
        {
            return enabled && IsStalled(s, now, thresholdSeconds, turnBlockedSeconds);
        }

        /// <summary>
        /// TRUE when a clear would change nothing (empty queue, not animating): /motionstate clear then reports
        /// "nothing to clear" and writes no [MOTION_STALL] line.
        /// </summary>
        public static bool NothingToClear(int pendingCount, bool isAnimating)
        {
            return pendingCount == 0 && !isAnimating;
        }

        /// <summary>
        /// The "stale for" figure of a clear: how long the queue has been continuously non-empty, or 0 for an empty
        /// queue (whose non-empty timestamp is from its last run and would print a meaningless large number).
        /// </summary>
        public static double StaleSeconds(int pendingCount, double now, double nonEmptySince)
        {
            return pendingCount > 0 ? now - nonEmptySince : 0.0;
        }

        /// <summary>"Ready/0, TurnRight/12, ..." with at most MaxListedMotions entries.</summary>
        public static string DescribeMotions(IEnumerable<MotionNode> motions)
        {
            if (motions == null)
                return "(none)";

            var sb = new StringBuilder();
            var listed = 0;
            var extra = 0;

            foreach (var node in motions)
            {
                if (listed >= MaxListedMotions)
                {
                    extra++;
                    continue;
                }

                if (listed > 0)
                    sb.Append(", ");

                sb.Append((MotionCommand)node.Motion).Append('/').Append(node.ContextID);
                listed++;
            }

            if (listed == 0)
                return "(none)";

            if (extra > 0)
                sb.Append($", +{extra} more");

            return sb.ToString();
        }

        /// <summary>
        /// The [MOTION_STALL] line. Kept in one place so the admin path and the auto path cannot drift.
        /// </summary>
        public static string FormatClearLine(string name, uint guid, int cleared, double staleSeconds, string motions, bool contact, string trigger, int suppressed)
        {
            var line = $"[MOTION_STALL] {name} (0x{guid:X8}) cleared {cleared} pending motion(s) stale for {staleSeconds:0.0}s: {motions} contact={contact} trigger={trigger}";

            if (suppressed > 0)
                line += $" suppressedSinceLast={suppressed}";

            return line;
        }

        /// <summary>
        /// Per-player log rate limit. Each auto-clear restarts the stale clock, so clears are already bounded to one
        /// per threshold; this further caps the WARN line at one per Interval and counts what it held back.
        /// </summary>
        public class LogGate
        {
            public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

            private DateTime lastLogged = DateTime.MinValue;

            public int Suppressed { get; private set; }

            /// <summary>
            /// TRUE when a line may be written now, with suppressedBefore = the clears held back since the last
            /// written line (and the counter reset); FALSE counts this clear as suppressed.
            /// </summary>
            public bool TryPass(DateTime now, out int suppressedBefore)
            {
                if (now - lastLogged >= Interval)
                {
                    suppressedBefore = Suppressed;
                    Suppressed = 0;
                    lastLogged = now;
                    return true;
                }

                Suppressed++;
                suppressedBefore = 0;
                return false;
            }
        }

        public static void Warn(string line)
        {
            log.Warn(line);
        }
    }
}
