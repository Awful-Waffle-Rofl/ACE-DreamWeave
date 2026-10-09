using System;

using ACE.Common;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network.GameAction;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Pending-teleport diagnostics for the "stuck in portal space" report: a teleport or login sets Teleporting and
    /// only the client's GameActionLoginComplete (OnTeleportComplete) clears it, so a client that never sends it is
    /// invisible until the 5 minute MaximumTeleportTime logoff - and players kill the client long before that.
    ///
    /// Writes [TELEPORT_PENDING] (Warn, once at 10s and once at 30s per teleport, from Heartbeat), [TELEPORT_ABANDONED]
    /// (Info, once per teleport, from LogOut_Inner) and [TELEPORT_LATE_COMPLETE] (Info, only after a pending line
    /// fired), and feeds the ace.teleport.* counters. The fields are chosen to separate three hypotheses: H1 lost
    /// packets (client alive, not acking past the teleport, asking for resends), H2 client stall (client sending and
    /// acking but no LoginComplete), H3 server ordering (self-position sends after Teleport() returned, or another
    /// teleport replacing a pending one).
    ///
    /// A window is opened in two steps. OpenTeleportWatch runs straight after the start stamp is written, before the
    /// first send, so the start is counted and the window exists even if the rest of Teleport() throws.
    /// BaselineTeleportWatch runs as Teleport()'s last statement (after SendSelf for a login) and takes the origin and
    /// the counters every delta is measured from. A line written for a window whose baseline never ran says
    /// baseline=missing.
    ///
    /// Diagnostics only: nothing here changes Teleporting, a timing, or what is sent. Every hook is wrapped so a fault
    /// here can never interrupt the teleport, completion or logout path it sits in.
    /// </summary>
    partial class Player
    {
        private readonly TeleportWatch teleportWatch = new TeleportWatch();

        // Self-audience UpdatePosition sends to this player's own client. A plain increment: it sits on the per-move
        // UpdatePlayerPosition path, and a lost increment from an off-thread caller only blurs a diagnostic count.
        private long selfPositionSends;

        public void NoteSelfPositionSend() => selfPositionSends++;

        /// <summary>
        /// Opens the pending window for the stamp just written to LastTeleportStartTimestamp: supersede check, started
        /// counter, kind, destination, and a fallback network read so the H1/H2 deltas survive a Teleport() that throws
        /// before its baseline step.
        /// </summary>
        private void OpenTeleportWatch(TeleportKind kind, bool teleportingBefore, uint destCell, uint destInstance)
        {
            try
            {
                var stamp = LastTeleportStartTimestamp;
                if (stamp == null)
                    return;

                if (teleportWatch.Begin(stamp.Value, kind, teleportingBefore))
                    ServerMetrics.TeleportSuperseded.Add(1);

                ServerMetrics.RecordTeleportStarted(kind);

                teleportWatch.DestCell = destCell;
                teleportWatch.DestInstance = destInstance;

                var snapshot = default(TeleportNetSnapshot);
                Session?.FillTeleportDiagnostics(ref snapshot, false);
                teleportWatch.NetAtStart = snapshot;
                teleportWatch.SelfPositionSendsAtStart = selfPositionSends;
            }
            catch (Exception ex)
            {
                log.Warn($"OpenTeleportWatch failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        /// <summary>
        /// The baseline every delta is measured from, taken after the opener's own sends so they are not counted as
        /// extra. Acts only when <paramref name="expectedStamp"/> is still both the player's stamp and the open window,
        /// so a teleport started in between (one that opened its own window) is never re-baselined by its caller.
        /// </summary>
        private void BaselineTeleportWatch(double? expectedStamp, bool hasOrigin, uint originCell, uint originInstance)
        {
            try
            {
                if (expectedStamp == null || LastTeleportStartTimestamp != expectedStamp || !teleportWatch.IsCurrent(expectedStamp))
                    return;

                teleportWatch.HasOrigin = hasOrigin;
                teleportWatch.OriginCell = originCell;
                teleportWatch.OriginInstance = originInstance;

                var snapshot = default(TeleportNetSnapshot);
                Session?.FillTeleportDiagnostics(ref snapshot, false);
                teleportWatch.NetAtStart = snapshot;
                teleportWatch.SelfPositionSendsAtStart = selfPositionSends;

                teleportWatch.MarkBaseline();
            }
            catch (Exception ex)
            {
                log.Warn($"BaselineTeleportWatch failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        /// <summary>
        /// Heartbeat: one [TELEPORT_PENDING] line per threshold per teleport. The player heartbeat runs every ~5s
        /// (HeartbeatInterval, default 5.0 in WorldObject_Tick), so the lines land at 10-15s and 30-35s, not exactly on
        /// the threshold. A player with no session/network still gets the line, with net=unavailable.
        /// </summary>
        private void TeleportWatchHeartbeat()
        {
            if (!Teleporting)
                return;

            try
            {
                var stamp = LastTeleportStartTimestamp;
                if (stamp == null)
                    return;

                var pendingSeconds = Time.GetUnixTime() - stamp.Value;

                // CheckThresholds is a no-op unless this stamp's window was opened
                var crossed = teleportWatch.CheckThresholds(true, stamp, pendingSeconds);
                if (crossed == TeleportPendingThreshold.None)
                    return;

                if ((crossed & TeleportPendingThreshold.First) != 0)
                    ServerMetrics.TeleportPending10s.Add(1);

                if ((crossed & TeleportPendingThreshold.Second) != 0)
                    ServerMetrics.TeleportPending30s.Add(1);

                var threshold = (crossed & TeleportPendingThreshold.Second) != 0 ? TeleportWatchRules.SecondThresholdSeconds : TeleportWatchRules.FirstThresholdSeconds;

                log.Warn($"[TELEPORT_PENDING] threshold={threshold:0}s {BuildTeleportWatchFields(pendingSeconds)}");
            }
            catch (Exception ex)
            {
                log.Warn($"TeleportWatchHeartbeat failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        /// <summary>Called by the TELEPORT_STUCK block before it forces the logoff, so the abandon it causes says so.</summary>
        private void TeleportWatchNoteStuckLogoff()
        {
            try
            {
                teleportWatch.NoteStuckLogoff(LastTeleportStartTimestamp);
            }
            catch (Exception ex)
            {
                log.Warn($"TeleportWatchNoteStuckLogoff failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        /// <summary>
        /// Top of LogOut_Inner, which every logout and disconnect path reaches (via LogOut, or PlayerManager's PK logoff
        /// queue): a teleport still pending here is abandoned. Latched per teleport, because the stuck-logoff retries
        /// every heartbeat; a no-op for a stamp whose window was never opened.
        /// </summary>
        private void TeleportWatchOnLogOut()
        {
            try
            {
                var stamp = LastTeleportStartTimestamp;

                if (!teleportWatch.TryAbandon(Teleporting, stamp))
                    return;

                var session = Session;
                var reason = TeleportWatchRules.ClassifyAbandon(teleportWatch.StuckLogoffFired(stamp), session == null || session.PendingTermination != null);

                ServerMetrics.TeleportAbandoned.Add(1);

                var pendingSeconds = Time.GetUnixTime() - (stamp ?? 0);

                log.Info($"[TELEPORT_ABANDONED] reason={TeleportWatchRules.ReasonTag(reason)} {BuildTeleportWatchFields(pendingSeconds)}");
            }
            catch (Exception ex)
            {
                log.Warn($"TeleportWatchOnLogOut failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        /// <summary>OnTeleportComplete, just before it clears Teleporting. A no-op for a stamp whose window was never opened.</summary>
        private void TeleportWatchOnComplete()
        {
            try
            {
                var stamp = LastTeleportStartTimestamp;

                if (!teleportWatch.TryComplete(Teleporting, stamp))
                    return;

                ServerMetrics.TeleportCompleted.Add(1);

                // only worth a line when a pending line already went out for this teleport: it tells a slow arrival
                // apart from one that never came
                if (teleportWatch.AnyThresholdFired(stamp))
                    log.Info($"[TELEPORT_LATE_COMPLETE] {Name} (0x{Guid.Full:X8}) kind={TeleportWatchRules.KindTag(teleportWatch.Kind)} after={Time.GetUnixTime() - (stamp ?? 0):0}s");
            }
            catch (Exception ex)
            {
                log.Warn($"TeleportWatchOnComplete failed for {Name} (0x{Guid.Full:X8})", ex);
            }
        }

        private string BuildTeleportWatchFields(double pendingSeconds)
        {
            var snapshot = default(TeleportNetSnapshot);
            Session?.FillTeleportDiagnostics(ref snapshot, true);

            var lastAction = snapshot.LastGameActionOpcode >= 0 ? ((GameActionType)snapshot.LastGameActionOpcode).ToString() : null;

            var landblock = CurrentLandblock;

            return TeleportWatchRules.FormatFields(Name, Guid.Full, teleportWatch, pendingSeconds,
                landblock?.Id.Landblock, landblock?.Instance, landblock?.CreateWorldObjectsCompleted,
                snapshot, lastAction, selfPositionSends, DateTime.UtcNow.Ticks);
        }
    }
}
