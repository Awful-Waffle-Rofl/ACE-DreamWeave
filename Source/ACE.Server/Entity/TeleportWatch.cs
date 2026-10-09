using System;
using System.Text;

namespace ACE.Server.Entity
{
    /// <summary>What started a pending teleport window (Player.Teleporting set, waiting on GameActionLoginComplete).</summary>
    public enum TeleportKind
    {
        Teleport,
        Portal,
        Login,
    }

    /// <summary>Watchdog thresholds a pending teleport has newly crossed on one check.</summary>
    [Flags]
    public enum TeleportPendingThreshold
    {
        None = 0,
        First = 1,
        Second = 2,
    }

    /// <summary>Why a teleport that never completed was given up on.</summary>
    public enum TeleportAbandonReason
    {
        Logout,
        Disconnect,
        StuckLogoff,
    }

    /// <summary>
    /// Point-in-time read of one session's network counters, taken at teleport start (for the deltas) and again when
    /// a diagnostic line is written. Filled by NetworkSession.FillTeleportDiagnostics and Session's game action
    /// counters; plain values so the formatting and decision logic below can be unit tested without a session.
    /// </summary>
    public struct TeleportNetSnapshot
    {
        /// <summary>False when the player had no session/network to read (the line then says so).</summary>
        public bool Available;

        /// <summary>UDP datagrams received from the client (NetworkSession.TotalPacketsReceived).</summary>
        public long PacketsReceived;

        /// <summary>UTC ticks of the last datagram received from the client, 0 when none.</summary>
        public long LastPacketReceivedTicks;

        /// <summary>
        /// Datagrams from the client dropped for a bad CRC. They count in PacketsReceived and LastPacketReceivedTicks
        /// (both are recorded before the check), so this is what tells real liveness apart from arriving garbage.
        /// </summary>
        public long CrcFailures;

        /// <summary>GameActions dispatched for this session (Session.GameActionsReceived).</summary>
        public long GameActions;

        /// <summary>Opcode of the last GameAction dispatched, -1 when none.</summary>
        public int LastGameActionOpcode;

        /// <summary>UTC ticks of the last GameAction dispatched, 0 when none.</summary>
        public long LastGameActionTicks;

        /// <summary>Cached packets re-sent because the client asked for them (client is missing our packets).</summary>
        public long RetransmitsServed;

        /// <summary>Client retransmit requests for packets no longer cached (answered with RejectRetransmit).</summary>
        public long RetransmitsUncached;

        /// <summary>Retransmit-request packets received from the client.</summary>
        public long ClientRetransmitRequests;

        /// <summary>Retransmit requests the server sent the client (we are missing the client's packets).</summary>
        public long ServerRetransmitRequests;

        /// <summary>The server's last assigned outbound packet sequence.</summary>
        public uint ServerPacketSequence;

        /// <summary>Highest outbound packet sequence the client has acknowledged, 0 when none.</summary>
        public uint ClientAckSequence;

        /// <summary>UTC ticks of the last ack from the client, 0 when none.</summary>
        public long ClientAckTicks;

        /// <summary>Outbound packets queued and not yet put on the wire. Only read when a line is written.</summary>
        public int OutboundQueueDepth;

        /// <summary>Sent packets still held for retransmit (pruned on client ack). Only read when a line is written.</summary>
        public int RetainedPackets;
    }

    /// <summary>
    /// Pure decision logic and line formatting for the pending-teleport diagnostics ([TELEPORT_PENDING],
    /// [TELEPORT_ABANDONED], ace.teleport.* counters). Diagnostics only: nothing here changes a teleport.
    /// </summary>
    public static class TeleportWatchRules
    {
        public const double FirstThresholdSeconds = 10.0;
        public const double SecondThresholdSeconds = 30.0;

        /// <summary>
        /// Classifies the pending window from the stamps the teleport and login paths already write. Portal: Teleport()
        /// copies LastTeleportStartTimestamp into LastPortalTeleportTimestamp when fromPortal. Login: PlayerEnterWorld
        /// stamps LoginTimestamp and LastTeleportStartTimestamp with one value. Only meaningful while the window is
        /// still pending, because OnTeleportComplete re-stamps LastPortalTeleportTimestamp on arrival.
        /// </summary>
        public static TeleportKind ClassifyKind(double? teleportStart, double? portalStamp, double? loginStamp)
        {
            if (teleportStart == null)
                return TeleportKind.Teleport;

            if (portalStamp == teleportStart)
                return TeleportKind.Portal;

            if (loginStamp == teleportStart)
                return TeleportKind.Login;

            return TeleportKind.Teleport;
        }

        public static string KindTag(TeleportKind kind)
        {
            switch (kind)
            {
                case TeleportKind.Portal: return "portal";
                case TeleportKind.Login: return "login";
                default: return "teleport";
            }
        }

        /// <summary>
        /// A stuck-logoff (the 5 minute MaximumTeleportTime) is the cause even though it then runs the logout path (and
        /// a session drop may follow), so
        /// it wins; a session that is gone or terminating is a disconnect; anything else reached the logout path while
        /// still pending.
        /// </summary>
        public static TeleportAbandonReason ClassifyAbandon(bool stuckLogoffFired, bool sessionGoneOrTerminating)
        {
            if (stuckLogoffFired)
                return TeleportAbandonReason.StuckLogoff;

            if (sessionGoneOrTerminating)
                return TeleportAbandonReason.Disconnect;

            return TeleportAbandonReason.Logout;
        }

        public static string ReasonTag(TeleportAbandonReason reason)
        {
            switch (reason)
            {
                case TeleportAbandonReason.StuckLogoff: return "stuck-logoff";
                case TeleportAbandonReason.Disconnect: return "disconnect";
                default: return "logout";
            }
        }

        private static string Ago(long nowTicks, long thenTicks)
        {
            if (thenTicks <= 0)
                return "never";

            return ((nowTicks - thenTicks) / (double)TimeSpan.TicksPerSecond).ToString("0.0") + "s";
        }

        private static string Delta(long now, long start)
        {
            var d = now - start;
            return d >= 0 ? "+" + d : d.ToString();
        }

        /// <summary>
        /// The shared field set of [TELEPORT_PENDING] and [TELEPORT_ABANDONED], everything after the tag. One line.
        ///
        /// seqAtStart is the server's outbound packet sequence read when the baseline was taken, AFTER the teleport
        /// message was enqueued but before it is necessarily packetized (bundles flush on the next session update). So
        /// ackPastStart above zero means the client acknowledged something sent after the teleport was enqueued, which
        /// makes delivery of the teleport packet likely but does not prove it was that packet.
        ///
        /// baseline=missing means the window opened but its baseline step never ran (Teleport() threw between the two):
        /// origin is then unknown and every delta is measured from the fallback read taken at open, which still covers
        /// the H1/H2 evidence but counts Teleport()'s own position sends as extra.
        /// </summary>
        public static string FormatFields(string name, uint guid, TeleportWatch watch, double pendingSeconds, uint? landblockId, uint? landblockInstance,
            bool? landblockLoaded, TeleportNetSnapshot now, string lastGameActionName, long selfPositionSendsNow, long nowTicks)
        {
            var start = watch.NetAtStart;
            var sb = new StringBuilder(512);

            sb.Append(name).Append(" (0x").Append(guid.ToString("X8")).Append(')');
            sb.Append(" kind=").Append(KindTag(watch.Kind));
            sb.Append(" pending=").Append(pendingSeconds.ToString("0")).Append('s');

            if (!watch.BaselineTaken)
                sb.Append(" baseline=missing");

            sb.Append(" from=");
            if (!watch.BaselineTaken)
                sb.Append("unknown");
            else if (watch.HasOrigin)
                sb.Append("0x").Append(watch.OriginCell.ToString("X8")).Append("/0x").Append(watch.OriginInstance.ToString("X8"));
            else
                sb.Append("none");

            sb.Append(" to=0x").Append(watch.DestCell.ToString("X8")).Append("/0x").Append(watch.DestInstance.ToString("X8"));

            sb.Append(" lb=");
            if (landblockId.HasValue)
                sb.Append("0x").Append(landblockId.Value.ToString("X4")).Append("/0x").Append((landblockInstance ?? 0).ToString("X8"))
                  .Append(" lbLoaded=").Append(landblockLoaded == true ? "true" : "false");
            else
                sb.Append("none lbLoaded=n/a");

            if (now.Available && start.Available)
            {
                // client liveness (H1 vs H2): is the client still sending at all, and what was the last thing it asked for
                sb.Append(" inPkts=").Append(Delta(now.PacketsReceived, start.PacketsReceived));
                sb.Append(" inPktAgo=").Append(Ago(nowTicks, now.LastPacketReceivedTicks));
                sb.Append(" crcFail=").Append(Delta(now.CrcFailures, start.CrcFailures)).Append('/').Append(now.CrcFailures);
                sb.Append(" actions=").Append(Delta(now.GameActions, start.GameActions));
                sb.Append(" lastAction=").Append(lastGameActionName ?? "none");
                sb.Append(" lastActionAgo=").Append(Ago(nowTicks, now.LastGameActionTicks));

                // outbound reliability (H1): did the client ack past the sequence in use when the teleport was enqueued
                // (see the summary for what that does and does not prove), and is it asking for resends
                sb.Append(" seqAtStart=").Append(start.ServerPacketSequence);
                sb.Append(" seqNow=").Append(now.ServerPacketSequence);
                sb.Append(" clientAck=").Append(now.ClientAckSequence);
                sb.Append(" ackPastStart=").Append(Delta(now.ClientAckSequence, start.ServerPacketSequence));
                sb.Append(" ackAgo=").Append(Ago(nowTicks, now.ClientAckTicks));
                sb.Append(" resends=").Append(Delta(now.RetransmitsServed, start.RetransmitsServed)).Append('/').Append(now.RetransmitsServed);
                sb.Append(" resendMiss=").Append(Delta(now.RetransmitsUncached, start.RetransmitsUncached)).Append('/').Append(now.RetransmitsUncached);
                sb.Append(" clientNaks=").Append(Delta(now.ClientRetransmitRequests, start.ClientRetransmitRequests)).Append('/').Append(now.ClientRetransmitRequests);
                sb.Append(" serverNaks=").Append(Delta(now.ServerRetransmitRequests, start.ServerRetransmitRequests)).Append('/').Append(now.ServerRetransmitRequests);
                sb.Append(" outQueue=").Append(now.OutboundQueueDepth);
                sb.Append(" retained=").Append(now.RetainedPackets);
            }
            else
                sb.Append(" net=unavailable");

            // server ordering (H3): self-position sends after Teleport() itself returned, and how many teleports in a row
            // replaced a still-pending one (GameMessagePlayerTeleport has one construction site, in Teleport(), so a
            // second teleport message during the window always shows up as superseded)
            sb.Append(" extraSelfPositions=").Append(Delta(selfPositionSendsNow, watch.SelfPositionSendsAtStart));
            sb.Append(" superseded=").Append(watch.SupersededCount);

            return sb.ToString();
        }
    }

    /// <summary>
    /// Per-player pending-teleport bookkeeping. Every once-only decision is keyed on the LastTeleportStartTimestamp
    /// value it was made for, so a new teleport (a new stamp) re-arms all of them with no explicit reset. Plain
    /// fields: written on the player's own landblock/world thread like the rest of Player's teleport state, and a lost
    /// race costs at most one duplicate or missing diagnostic line, never gameplay.
    /// </summary>
    public sealed class TeleportWatch
    {
        private double? currentStamp;
        private double? firstFiredStamp;
        private double? secondFiredStamp;
        private double? abandonedStamp;
        private double? completedStamp;
        private double? stuckLogoffStamp;

        public TeleportKind Kind;
        public bool HasOrigin;
        public uint OriginCell;
        public uint OriginInstance;
        public uint DestCell;
        public uint DestInstance;

        /// <summary>Teleports in a row that started while the previous one was still pending (0 for a clean start).</summary>
        public int SupersededCount;

        /// <summary>
        /// Network counters at the start of the window. Begin's caller stores a fallback read taken when the window
        /// opens; the baseline step replaces it with one taken after the opener's own sends.
        /// </summary>
        public TeleportNetSnapshot NetAtStart;
        public long SelfPositionSendsAtStart;

        /// <summary>
        /// False until <see cref="MarkBaseline"/> runs for the current window. Teleport() opens the window right after
        /// stamping and takes the baseline as its LAST statement, so false while pending means Teleport() threw in
        /// between: origin is unknown and the deltas are from the fallback read at open.
        /// </summary>
        public bool BaselineTaken { get; private set; }

        /// <summary>The stamp the last Begin was for, null before the first.</summary>
        public double? CurrentStamp => currentStamp;

        /// <summary>
        /// True when <paramref name="stamp"/> is the window the last Begin opened. Every per-teleport decision below
        /// requires it, so a stamp that never had a Begin (an end without a start) is a no-op rather than a decision
        /// made against another teleport's data.
        /// </summary>
        public bool IsCurrent(double? stamp) => stamp.HasValue && currentStamp == stamp;

        /// <summary>
        /// Opens the window for <paramref name="stamp"/>. Returns true when it supersedes a previous window that was
        /// still pending (Teleporting was already set and that window neither completed nor was abandoned).
        /// </summary>
        public bool Begin(double stamp, TeleportKind kind, bool teleportingBefore)
        {
            var superseded = teleportingBefore && currentStamp.HasValue && currentStamp != stamp
                && abandonedStamp != currentStamp && completedStamp != currentStamp;

            SupersededCount = superseded ? SupersededCount + 1 : 0;

            currentStamp = stamp;
            Kind = kind;
            HasOrigin = false;
            OriginCell = OriginInstance = DestCell = DestInstance = 0;
            NetAtStart = default;
            SelfPositionSendsAtStart = 0;
            BaselineTaken = false;

            return superseded;
        }

        /// <summary>Records that the baseline step ran for the current window.</summary>
        public void MarkBaseline() => BaselineTaken = true;

        /// <summary>
        /// Thresholds newly crossed for this stamp, each at most once per stamp. A first check already past the second
        /// threshold returns both, so the first-threshold count never falls below the second.
        /// </summary>
        public TeleportPendingThreshold CheckThresholds(bool teleporting, double? stamp, double pendingSeconds)
        {
            if (!teleporting || !IsCurrent(stamp))
                return TeleportPendingThreshold.None;

            var crossed = TeleportPendingThreshold.None;

            if (pendingSeconds >= TeleportWatchRules.FirstThresholdSeconds && firstFiredStamp != stamp)
            {
                firstFiredStamp = stamp;
                crossed |= TeleportPendingThreshold.First;
            }

            if (pendingSeconds >= TeleportWatchRules.SecondThresholdSeconds && secondFiredStamp != stamp)
            {
                secondFiredStamp = stamp;
                crossed |= TeleportPendingThreshold.Second;
            }

            return crossed;
        }

        /// <summary>Records that the stuck-logoff fired for this stamp, so the abandon it causes is attributed to it.</summary>
        public void NoteStuckLogoff(double? stamp) => stuckLogoffStamp = stamp;

        public bool StuckLogoffFired(double? stamp) => stamp.HasValue && stuckLogoffStamp == stamp;

        /// <summary>
        /// True exactly once per stamp, while still teleporting and not already completed. Every logout path reaches
        /// Player.LogOut_Inner, which can run more than once for one teleport (the stuck-logoff calls LogOut again on
        /// every heartbeat when the player has no session), so this latch is what keeps ace.teleport.abandoned to one
        /// per teleport.
        /// </summary>
        public bool TryAbandon(bool teleporting, double? stamp)
        {
            if (!teleporting || !IsCurrent(stamp) || abandonedStamp == stamp || completedStamp == stamp)
                return false;

            abandonedStamp = stamp;
            return true;
        }

        /// <summary>
        /// True exactly once per stamp, when the window was pending and had not already been abandoned (a LoginComplete
        /// landing during the logout animation after an abandon is not also a completion).
        /// </summary>
        public bool TryComplete(bool teleporting, double? stamp)
        {
            if (!teleporting || !IsCurrent(stamp) || completedStamp == stamp || abandonedStamp == stamp)
                return false;

            completedStamp = stamp;
            return true;
        }

        /// <summary>True when a watchdog threshold fired for this stamp (a late completion worth a line).</summary>
        public bool AnyThresholdFired(double? stamp) => stamp.HasValue && firstFiredStamp == stamp;
    }
}
