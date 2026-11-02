using System;

using ACE.Common;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.Sequence;
using ACE.Server.Network.Structure;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Server-side arrival fallback. A teleport, portal or login sets Teleporting and, before this, only the client's
    /// GameActionLoginComplete cleared it (via OnTeleportComplete). A client that stays connected and keeps acking but
    /// never sends it (prod 2026-10-10: ackAgo 1.9s, actions +198, no LoginComplete) was held as pink bubbles until the
    /// 5 minute MaximumTeleportTime logoff in Player_Tick. Two server sources now complete it too:
    /// <list type="bullet">
    /// <item>movement: the first MoveToState with movement commands from the window's recorded destination landblock, at
    /// least TeleportArrivalRules.MovementGraceSeconds after the start (GameActionMoveToState calls CheckArrivalOnMovementInput);</item>
    /// <item>timer: a per-window ActionChain scheduled teleport_forced_complete_seconds after the window opens.</item>
    /// </list>
    /// Every source goes through <see cref="CompleteArrival"/>, which calls the same OnTeleportComplete the client path
    /// always used. OnTeleportComplete's materialize latch (TeleportArrivalLatch) makes the arrival side effects run once
    /// per window however many sources drive it. The decision logic is pure, in Entity/TeleportArrival.cs.
    /// </summary>
    partial class Player
    {
        private readonly TeleportArrivalLatch arrivalLatch = new TeleportArrivalLatch();

        /// <summary>
        /// The single entry point for completing an arrival: GameActionLoginComplete (Client) and the two forced sources.
        /// <para/>
        /// Re-enters OnTeleportComplete whenever this window has not materialized yet, whoever asked first: a request is
        /// never a reason to refuse another source, so an earlier request that went nowhere cannot strand the window.
        /// While a landblock wait is running this starts another retry chain; the chains share the wait's clock (keyed on
        /// the stamp and landblock) and the materialize latch lets exactly one of them materialize the player.
        /// <para/>
        /// Duplicate policy: a LoginComplete for a window that already materialized (the client acking twice, or acking
        /// late after a forced completion) re-runs none of OnTeleportComplete - not the stance resync, CheckMonsters,
        /// CheckHouse, the challenge exit checks, the LastPortalTeleportTimestamp hijack or the arrival overlap capture.
        /// Before this, every LoginComplete re-ran all of it. Prod 2026-10-10 (109 logins, 944 teleports) showed no window
        /// with a second client teleport-complete, so in practice nothing that used to happen stops happening.
        /// <para/>
        /// The first-login block runs for Client, for Movement on a non-login window, and never for Timer (TeleportArrivalRules.RunsFirstEnterWorld):
        /// a forced login leaves FirstEnterWorldDone unset until a real client signal arrives, which then reaches the block
        /// here even though the window is already materialized.
        /// </summary>
        public void CompleteArrival(TeleportCompletionSource source)
        {
            var stamp = LastTeleportStartTimestamp;

            if (source == TeleportCompletionSource.Client)
                NoteClientAfterForced(stamp);

            arrivalLatch.NoteRequest(stamp, source);

            if (!arrivalLatch.IsMaterialized(stamp))
                OnTeleportComplete();
            else if (log.IsDebugEnabled)
                log.Debug($"[TELEPORT_DUP_COMPLETE] {Name} (0x{Guid.Full:X8}) source={TeleportArrivalRules.SourceTag(source)} owner={TeleportArrivalRules.SourceTag(arrivalLatch.SourceFor(stamp))} elapsed={Time.GetUnixTime() - (stamp ?? 0):0.0}s");

            // Moved here from GameActionLoginComplete. Client always; Movement only for a non-login window; Timer never
            if (TeleportArrivalRules.RunsFirstEnterWorld(source, ArrivalKind(stamp)) && !FirstEnterWorldDone)
            {
                FirstEnterWorldDone = true;
                SendPropertyUpdatesAndOverrides();
            }
        }

        /// <summary>The client's LoginComplete for a window a forced source already owns: one line and a counter per window.</summary>
        private void NoteClientAfterForced(double? stamp)
        {
            try
            {
                if (!arrivalLatch.TryNoteLateClient(stamp))
                    return;

                ServerMetrics.TeleportLateClientComplete.Add(1);
                log.Info($"[TELEPORT_LATE_CLIENT_COMPLETE] {Name} (0x{Guid.Full:X8}) kind={ArrivalKindTag(stamp)} forcedBy={TeleportArrivalRules.SourceTag(arrivalLatch.SourceFor(stamp))} " +
                         $"elapsed={Time.GetUnixTime() - (stamp ?? 0):0.0}s materialized={(arrivalLatch.IsMaterialized(stamp) ? "true" : "false")}");
            }
            catch (Exception ex)
            {
                log.Warn($"NoteClientAfterForced failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        /// <summary>
        /// OnTeleportComplete, at the point it materializes the player: the once-only latch for this window. False means
        /// this window already materialized and OnTeleportComplete must stop before any side effect.
        /// </summary>
        private bool TryMaterializeArrival(out TeleportCompletionSource source)
        {
            return arrivalLatch.TryMaterialize(LastTeleportStartTimestamp, out source);
        }

        private bool ArrivalAlreadyMaterialized() => arrivalLatch.IsMaterialized(LastTeleportStartTimestamp);

        /// <summary>
        /// Duration telemetry for every completed arrival, from the window's start stamp to materializing (so a landblock
        /// wait is included, matching how prod arrival times were measured). The client path is a Debug line only, so it
        /// stays quiet; the Info line is [TELEPORT_FORCED_COMPLETE], for forced sources only.
        /// </summary>
        private void RecordArrivalCompleted(TeleportCompletionSource source)
        {
            try
            {
                var stamp = LastTeleportStartTimestamp;
                if (stamp == null)
                    return;

                var elapsed = Time.GetUnixTime() - stamp.Value;

                ServerMetrics.RecordTeleportArrival(source, elapsed);

                if (log.IsDebugEnabled)
                    log.Debug($"[TELEPORT_COMPLETE] {Name} (0x{Guid.Full:X8}) kind={ArrivalKindTag(stamp)} source={TeleportArrivalRules.SourceTag(source)} elapsed={elapsed:0.0}s");
            }
            catch (Exception ex)
            {
                log.Warn($"RecordArrivalCompleted failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        private TeleportKind? ArrivalKind(double? stamp) => teleportWatch.IsCurrent(stamp) ? teleportWatch.Kind : (TeleportKind?)null;

        private string ArrivalKindTag(double? stamp) => teleportWatch.IsCurrent(stamp) ? TeleportWatchRules.KindTag(teleportWatch.Kind) : "unknown";

        private bool ArrivalSessionConnected => Session != null && Session.Player == this && Session.PendingTermination == null;

        // ---- movement trigger ----

        /// <summary>
        /// GameActionMoveToState, before the handler does anything else with the report: the movement trigger. MoveToState
        /// reaches its handler while Teleporting (InboundMessageManager.HandleGameAction drops nothing on Teleporting; the
        /// handler itself only skips SetRequestedLocation), so this is the earliest point that sees it. Completing first
        /// lets the rest of the handler accept the report as ordinary movement. One bool test when not teleporting.
        /// </summary>
        public void CheckArrivalOnMovementInput(MoveToState moveToState)
        {
            if (!Teleporting)
                return;

            try
            {
                var stamp = LastTeleportStartTimestamp;
                if (stamp == null || moveToState == null)
                    return;

                var args = TeleportArrivalRules.DeriveMovementArgs(moveToState, stamp.Value, Time.GetUnixTime(),
                    teleportWatch.IsCurrent(stamp), teleportWatch.DestCell);

                // cheap tests before the property read: the grace window and movement commands reject most reports
                if (!args.HasMovement || args.ElapsedSeconds < TeleportArrivalRules.MovementGraceSeconds)
                    return;

                var enabled = PropertyManager.GetBool("teleport_forced_complete_on_movement").Item;

                if (!TeleportArrivalRules.ShouldCompleteOnMovement(enabled, Teleporting, IsLoggingOut, arrivalLatch.IsMaterialized(stamp),
                        args.ElapsedSeconds, ArrivalInput.MoveToState, args.HasMovement, args.ReportedCell, args.DestinationCell))
                    return;

                ForceArrival(TeleportCompletionSource.Movement, args.ElapsedSeconds, MovementSequenceFields(args.ReportedTeleportSequence));
            }
            catch (Exception ex)
            {
                log.Error($"CheckArrivalOnMovementInput failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        /// <summary>teleSeq/selfSeq/liveSeq for the movement line, plus the mismatch counter. Observation only, never enforced.</summary>
        private string MovementSequenceFields(ushort reported)
        {
            var self = ReadSequence(SelfTeleportSequence);
            var live = ReadSequence(Sequences.GetCurrentSequence(SequenceType.ObjectTeleport));

            var fields = TeleportArrivalRules.FormatSequenceFields(reported, self, live, out var mismatch);

            if (mismatch)
                ServerMetrics.TeleportForcedMovementSequenceMismatch.Add(1);

            return fields;
        }

        private static ushort? ReadSequence(byte[] sequence) => sequence != null && sequence.Length >= 2 ? BitConverter.ToUInt16(sequence, 0) : (ushort?)null;

        // ---- timer backstop ----

        /// <summary>
        /// Teleport() and PlayerEnterWorld, right after the window opens: schedules this window's backstop. A scheduled
        /// ActionChain per window rather than a Heartbeat check, because Heartbeat runs every ~5 s, which would fire the
        /// backstop anywhere from N to N+5 s; the chain fires at N. The limit is read again when it fires, so a live change
        /// applies to pending windows, including 0 to switch it off.
        /// <para/>
        /// Survival while detached: the delayed action is handed to this Player as the chain's actor (DelayManager calls
        /// actor.EnqueueAction), and Player overrides EnqueueAction to push onto its OWN actionQueue unconditionally
        /// (Player_Tick.cs), unlike WorldObject.EnqueueAction, which drops work when CurrentLandblock is null. The queue is
        /// run by Player_Tick, which the player's landblock calls, so an action that lands while the player is between
        /// landblocks waits and runs on the player's next tick instead of being lost. It deliberately stays on the player's
        /// own queue rather than WorldManager's: OnTeleportComplete touches landblock and physics state that belongs to
        /// the player's landblock thread, and WorldManager's queue runs on the world thread alongside parallel landblock ticks.
        /// </summary>
        private void ScheduleForcedArrivalBackstop()
        {
            try
            {
                var limit = PropertyManager.GetLong("teleport_forced_complete_seconds").Item;
                if (limit <= 0)
                    return;

                ScheduleForcedArrivalCheck(LastTeleportStartTimestamp, limit);
            }
            catch (Exception ex)
            {
                log.Warn($"ScheduleForcedArrivalBackstop failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        private void ScheduleForcedArrivalCheck(double? scheduledStamp, double delaySeconds)
        {
            if (scheduledStamp == null)
                return;

            var chain = new ActionChain();
            chain.AddDelaySeconds(delaySeconds);
            chain.AddAction(this, () => OnForcedArrivalTimer(scheduledStamp));
            chain.EnqueueChain();
        }

        private void OnForcedArrivalTimer(double? scheduledStamp)
        {
            try
            {
                var limit = PropertyManager.GetLong("teleport_forced_complete_seconds").Item;
                var stamp = LastTeleportStartTimestamp;
                var elapsed = Time.GetUnixTime() - (scheduledStamp ?? 0);

                var decision = TeleportArrivalRules.EvaluateTimer(limit, Teleporting, arrivalLatch.IsMaterialized(stamp), ArrivalSessionConnected,
                    IsLoggingOut, scheduledStamp, stamp, elapsed, out var remaining);

                switch (decision)
                {
                    case ForcedCompleteTimerDecision.Fire:
                        ForceArrival(TeleportCompletionSource.Timer, elapsed, null);
                        break;

                    case ForcedCompleteTimerDecision.Reschedule:
                        ScheduleForcedArrivalCheck(scheduledStamp, remaining);
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Error($"OnForcedArrivalTimer failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        // ---- shared forced completion ----

        private void ForceArrival(TeleportCompletionSource source, double elapsed, string extraFields)
        {
            try
            {
                var stamp = LastTeleportStartTimestamp;

                // once per window per source: a source that drives the window again during a landblock wait is not a new decision
                if (arrivalLatch.TryNoteForced(stamp, source))
                {
                    ServerMetrics.RecordTeleportForcedComplete(source);

                    var requestedBy = arrivalLatch.IsRequested(stamp) ? TeleportArrivalRules.SourceTag(arrivalLatch.SourceFor(stamp)) : "none";

                    // the TeleportWatch field set, written while Teleporting is still set, so it describes the client we gave up on
                    log.Info($"[TELEPORT_FORCED_COMPLETE] reason={TeleportArrivalRules.SourceTag(source)} elapsed={elapsed:0.0}s requestedBy={requestedBy} " +
                             (extraFields != null ? extraFields + " " : "") + BuildTeleportWatchFields(elapsed));
                }
            }
            catch (Exception ex)
            {
                log.Warn($"ForceArrival telemetry failed for {Name} (0x{Guid.Full:X8})", ex);
            }

            CompleteArrival(source);
        }
    }
}
