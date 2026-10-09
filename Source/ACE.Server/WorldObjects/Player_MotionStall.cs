using System;
using System.Text;

using ACE.Server.Physics;
using ACE.Server.Physics.Common;

namespace ACE.Server.WorldObjects
{
    partial class Player
    {
        private readonly MotionStallWatchdog.LogGate motionStallLogGate = new MotionStallWatchdog.LogGate();

        private readonly MotionStallWatchdog.LogGate motionStallErrorGate = new MotionStallWatchdog.LogGate();

        /// <summary>
        /// PhysicsTimer.CurrentTime at which a MoveToManager turn last became blocked behind IsAnimating, or
        /// MotionStallWatchdog.NotBlocked. Reset on every check where the turn is not blocked.
        /// </summary>
        private double motionStallTurnBlockedSince = MotionStallWatchdog.NotBlocked;

        /// <summary>
        /// Motion-stall watchdog hook (see MotionStallWatchdog). Runs every physics tick (trigger "tick") and when a
        /// new FastTick move-to / turn-to supersedes a pending one (trigger "supersede"). Ordered cheapest first:
        /// on the hot path a player with no blocked turn costs one bool read (plus one field write on the tick the
        /// block ends), and the tunables are read only while a turn is actually waiting behind IsAnimating.
        /// Never throws.
        /// </summary>
        public void CheckMotionStall(string trigger)
        {
            try
            {
                var physicsObj = PhysicsObj;
                var movementManager = physicsObj?.MovementManager;

                var blocked = physicsObj != null && physicsObj.IsAnimating && movementManager != null
                    && MotionStallWatchdog.IsTurnBlockedOnAnimation(movementManager.MoveToManager);

                if (!blocked)
                {
                    if (motionStallTurnBlockedSince != MotionStallWatchdog.NotBlocked)
                        motionStallTurnBlockedSince = MotionStallWatchdog.NotBlocked;
                    return;
                }

                var now = PhysicsTimer.CurrentTime;
                motionStallTurnBlockedSince = MotionStallWatchdog.NextTurnBlockedSince(true, motionStallTurnBlockedSince, now);

                var minterp = movementManager.MotionInterpreter;
                if (minterp?.PendingMotions == null)
                    return;

                if (!MotionStallWatchdog.Enabled())
                    return;

                var snapshot = MotionStallWatchdog.BuildSnapshot(
                    fastTick: FastTick,
                    isAnimating: physicsObj.IsAnimating,
                    turnBlocked: true,
                    turnBlockedSince: motionStallTurnBlockedSince,
                    transientState: physicsObj.TransientState,
                    teleporting: Teleporting,
                    casting: MagicState != null && MagicState.IsCasting,
                    animatingSince: minterp.PendingMotionsNonEmptySince,
                    now: now);

                if (!MotionStallWatchdog.IsStalled(snapshot, now, MotionStallWatchdog.ThresholdSeconds(), MotionStallWatchdog.TurnBlockedSeconds()))
                    return;

                ClearMotionStall(trigger);
            }
            catch (Exception ex)
            {
                if (motionStallErrorGate.TryPass(DateTime.UtcNow, out var suppressed))
                    log.Error($"[MOTION_STALL] {Name} (0x{Guid.Full:X8}) watchdog check failed (trigger={trigger}, suppressedSinceLast={suppressed})", ex);
            }
        }

        /// <summary>
        /// Drops the pending motion queue (MotionInterp.ClearPendingMotions, the HandleExitWorld way) and writes the
        /// [MOTION_STALL] line. Server state only: nothing is sent to the client. A waiting turn-to then starts on the
        /// next MoveToManager.UseTime. trigger "admin" always logs; the automatic triggers are rate-limited per player.
        /// Returns the log line, or a "nothing to clear" message (no WARN) when the queue is already empty and the
        /// object is not animating, or null when there is no motion interpreter.
        /// </summary>
        public string ClearMotionStall(string trigger)
        {
            var physicsObj = PhysicsObj;
            var minterp = physicsObj?.MovementManager?.MotionInterpreter;
            if (minterp?.PendingMotions == null)
                return null;

            var count = minterp.PendingMotions.Count;

            if (MotionStallWatchdog.NothingToClear(count, physicsObj.IsAnimating))
                return $"motionstate: {Name} has no pending motions; nothing to clear.";

            var staleSeconds = MotionStallWatchdog.StaleSeconds(count, PhysicsTimer.CurrentTime, minterp.PendingMotionsNonEmptySince);
            var motions = MotionStallWatchdog.DescribeMotions(minterp.PendingMotions);
            var contact = physicsObj.TransientState.HasFlag(TransientStateFlags.Contact);

            var cleared = minterp.ClearPendingMotions();
            motionStallTurnBlockedSince = MotionStallWatchdog.NotBlocked;

            var isAdmin = trigger == "admin";
            var pass = motionStallLogGate.TryPass(DateTime.UtcNow, out var suppressed) || isAdmin;

            var line = MotionStallWatchdog.FormatClearLine(Name, Guid.Full, cleared, staleSeconds, motions, contact, trigger, suppressed);

            if (pass)
                MotionStallWatchdog.Warn(line);

            return line;
        }

        /// <summary>The /motionstate report. Must run on the world thread.</summary>
        public string DescribeMotionState()
        {
            var physicsObj = PhysicsObj;
            if (physicsObj == null)
                return $"{Name} (0x{Guid.Full:X8}): no physics object.";

            var sb = new StringBuilder();
            var minterp = physicsObj.MovementManager?.MotionInterpreter;
            var moveToManager = physicsObj.MovementManager?.MoveToManager;
            var now = PhysicsTimer.CurrentTime;

            sb.AppendLine($"{Name} (0x{Guid.Full:X8}) motion state:");
            sb.AppendLine($"  IsAnimating={physicsObj.IsAnimating} FastTick={FastTick} Teleporting={Teleporting} Casting={MagicState != null && MagicState.IsCasting}");
            sb.AppendLine($"  Contact={physicsObj.TransientState.HasFlag(TransientStateFlags.Contact)} OnWalkable={physicsObj.TransientState.HasFlag(TransientStateFlags.OnWalkable)}");

            if (minterp?.PendingMotions == null)
                sb.AppendLine("  PendingMotions: (no motion interpreter)");
            else
            {
                sb.AppendLine($"  PendingMotions ({minterp.PendingMotions.Count}): {MotionStallWatchdog.DescribeMotions(minterp.PendingMotions)}");
                sb.AppendLine($"  seconds since last queue change={now - minterp.PendingMotionsChangedTime:0.0} continuously non-empty for={(minterp.PendingMotions.Count > 0 ? (now - minterp.PendingMotionsNonEmptySince).ToString("0.0") : "0.0")}");
            }

            if (moveToManager == null)
                sb.AppendLine("  MoveToManager: (none)");
            else
                sb.AppendLine($"  MoveTo: type={moveToManager.MovementType} pendingActions={moveToManager.PendingActions?.Count ?? 0} head={(moveToManager.PendingActions != null && moveToManager.PendingActions.Count > 0 ? moveToManager.PendingActions[0].Type.ToString() : "-")} currentCommand={(ACE.Entity.Enum.MotionCommand)moveToManager.CurrentCommand} turnBlockedOnAnimation={MotionStallWatchdog.IsTurnBlockedOnAnimation(moveToManager)} turnBlockedFor={(motionStallTurnBlockedSince >= 0 ? (now - motionStallTurnBlockedSince).ToString("0.0") : "0.0")}");

            sb.AppendLine($"  Use pending: IsPlayerMovingTo2={IsPlayerMovingTo2} MoveToParams={(MoveToParams != null ? "set" : "null")} legacyChainPending={IsPlayerMovingTo}");
            sb.Append($"  Watchdog: enabled={MotionStallWatchdog.Enabled()} nonEmptySeconds={MotionStallWatchdog.ThresholdSeconds():0.0} turnBlockedSeconds={MotionStallWatchdog.TurnBlockedSeconds():0.0}");

            return sb.ToString();
        }
    }
}
