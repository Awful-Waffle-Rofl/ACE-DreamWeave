using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Server.Entity;
using ACE.Server.Network.Enum;

namespace ACE.Server.Network
{
    /// <summary>
    /// Fixed-window limiter for outbound ring dumps: at most <c>max</c> per window. Not thread-safe on its own;
    /// <see cref="OutboundRingMonitor"/> calls it under a lock.
    /// </summary>
    public sealed class OutboundRingDumpLimiter
    {
        private readonly int max;
        private readonly long windowTicks;
        private long windowStart;
        private int taken;
        private bool started;

        public OutboundRingDumpLimiter(int max, long windowTicks)
        {
            this.max = max;
            this.windowTicks = windowTicks;
        }

        public bool TryTake(long now)
        {
            if (!started || now - windowStart >= windowTicks)
            {
                started = true;
                windowStart = now;
                taken = 0;
            }

            if (taken >= max)
                return false;

            taken++;
            return true;
        }
    }

    /// <summary>
    /// When and how the outbound ring is dumped (see <see cref="OutboundRing"/>): on a dropped session
    /// (k=t network timeout, k=s a connection-fault reason after 10 s of inbound silence) and as a periodic control sample from
    /// a healthy session (k=c). Each dump is snapshotted synchronously by the caller's thread; formatting and logging
    /// run on the thread pool. Logs through the Session logger, at INFO. Never logs an address: the account name only.
    /// </summary>
    public static class OutboundRingMonitor
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Session));

        public const int MaxDumpsPerMinute = 30;

        /// <summary>k=s fires only once the client has been silent at least this long.</summary>
        public const long SilentDropMs = 10_000;

        /// <summary>A control candidate must have sent something within this long.</summary>
        public const long ControlMaxSinceInboundMs = 2_000;

        /// <summary>A control dump is taken this long after its anchor is recorded.</summary>
        public const int ControlDelaySeconds = 5;

        private static readonly object limiterLock = new object();
        private static readonly OutboundRingDumpLimiter limiter = new OutboundRingDumpLimiter(MaxDumpsPerMinute, 60 * Stopwatch.Frequency);

        private static long ridCounter = new Random().Next();

        // control sample state: touched only from NetworkManager.DoSessionWork's serial phase
        private sealed class PendingControl
        {
            public Session Session;
            public NetworkSession Network;
            public OutboundRingAnchor Anchor;
            public long Due;
        }

        private static readonly List<PendingControl> pendingControls = new List<PendingControl>();
        private static readonly Random controlRandom = new Random();
        private static long nextControlStamp;
        private static int sessionWorkFailureLogged;

        /// <summary>A fresh dump id: 8 lowercase hex digits from a counter seeded randomly at boot.</summary>
        public static string NextRid() => ((uint)Interlocked.Increment(ref ridCounter)).ToString("x8");

        /// <summary>
        /// The dump kind for a dropped session, or '\0' for none. k=t for every Network Timeout; k=s only for a reason
        /// that implies a connection fault (<see cref="IsConnectionFault"/>) after at least 10 s of client silence.
        /// Administrative and account drops (shutdown, world closed, boots, bans, logins elsewhere) never dump.
        /// </summary>
        public static char DropKind(SessionTerminationReason reason, long sinceInboundMs)
        {
            if (reason == SessionTerminationReason.NetworkTimeout)
                return 't';

            return IsConnectionFault(reason) && sinceInboundMs >= SilentDropMs ? 's' : '\0';
        }

        /// <summary>The k=s whitelist: drop reasons that imply the connection itself failed.</summary>
        public static bool IsConnectionFault(SessionTerminationReason reason)
        {
            switch (reason)
            {
                case SessionTerminationReason.ClientSentNetworkErrorDisconnect:
                case SessionTerminationReason.ClientConnectionFailure:
                case SessionTerminationReason.SendToSocketException:
                case SessionTerminationReason.AbnormalSequenceReceived:
                case SessionTerminationReason.BadHandshake:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The control-sample clock. <paramref name="next"/> 0 means unscheduled: it is set one interval out and
        /// nothing is due. Otherwise the deadline is pulled in to at most one interval from now (so a lowered
        /// interval takes effect promptly), and when it has passed the sample is due and the next one is scheduled
        /// one interval out. <paramref name="intervalTicks"/> 0 or less unschedules (control off). Returns true when
        /// a sample is due.
        /// </summary>
        public static bool AdvanceControlClock(ref long next, long now, long intervalTicks)
        {
            if (intervalTicks <= 0)
            {
                next = 0;
                return false;
            }

            if (next == 0)
            {
                next = now + intervalTicks;
                return false;
            }

            next = Math.Min(next, now + intervalTicks);

            if (now < next)
                return false;

            next = now + intervalTicks;
            return true;
        }

        private static long SinceInboundMs(NetworkSession network, long nowUtcTicks)
        {
            var last = network.LastPacketReceivedTicks;
            return last == 0 ? -1 : (nowUtcTicks - last) / TimeSpan.TicksPerMillisecond;
        }

        /// <summary>
        /// Called from Session.DropSession before the dropped line is logged. Returns the dump's rid, or null when no
        /// dump was taken. Never throws.
        /// </summary>
        public static string OnDrop(Session session, SessionTerminationReason reason)
        {
            try
            {
                if (!OutboundRingSettings.Enabled)
                    return null;

                var network = session?.Network;
                if (network == null)
                    return null;

                var kind = DropKind(reason, SinceInboundMs(network, DateTime.UtcNow.Ticks));
                if (kind == '\0')
                    return null;

                return Dump(session, kind, null);
            }
            catch (Exception ex)
            {
                log.Warn("[OUTBOUND_RING] drop dump failed", ex);
                return null;
            }
        }

        /// <summary>
        /// The control sample, from NetworkManager.DoSessionWork's serial phase (under its session lock): takes the
        /// pending control dumps that are due, then every outbound_ring_control_seconds picks one in-world session
        /// uniformly at random from those that sent something in the last 2 s, records its anchor, and dumps it 5 s
        /// later relative to that anchor. Never throws.
        /// </summary>
        public static void OnSessionWork(Session[] sessionMap)
        {
            try
            {
                if (!OutboundRingSettings.Enabled)
                {
                    pendingControls.Clear();
                    return;
                }

                var now = Stopwatch.GetTimestamp();

                for (var i = pendingControls.Count - 1; i >= 0; i--)
                {
                    var pending = pendingControls[i];
                    if (now < pending.Due)
                        continue;

                    pendingControls.RemoveAt(i);

                    if (StillLive(pending.Session, pending.Network, sessionMap))
                        Dump(pending.Session, 'c', pending.Anchor);
                }

                if (!AdvanceControlClock(ref nextControlStamp, now, OutboundRingSettings.ControlSeconds * Stopwatch.Frequency))
                    return;

                var chosen = PickControl(sessionMap, DateTime.UtcNow.Ticks);
                if (chosen != null)
                {
                    pendingControls.Add(new PendingControl
                    {
                        Session = chosen,
                        Network = chosen.Network,
                        Anchor = chosen.Network.OutboundRing.ReadAnchor(),
                        Due = now + ControlDelaySeconds * Stopwatch.Frequency,
                    });
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref sessionWorkFailureLogged, 1) == 0)
                    log.Warn("[OUTBOUND_RING] control sample failed; further failures are not logged", ex);
            }
        }

        private static Session PickControl(Session[] sessionMap, long nowUtcTicks)
        {
            Session chosen = null;
            var seen = 0;

            foreach (var session in sessionMap)
            {
                if (session == null || session.State != SessionState.WorldConnected || session.Player == null || session.PendingTermination != null)
                    continue;

                var network = session.Network;
                if (network == null || !network.OutboundRing.Allocated)
                    continue;

                var since = SinceInboundMs(network, nowUtcTicks);
                if (since < 0 || since >= ControlMaxSinceInboundMs)
                    continue;

                // reservoir sample of one: uniform over the candidates, no list
                seen++;
                if (controlRandom.Next(seen) == 0)
                    chosen = session;
            }

            return chosen;
        }

        private static bool StillLive(Session session, NetworkSession network, Session[] sessionMap)
        {
            if (session.PendingTermination != null || session.Network != network)
                return false;

            var id = network.ClientId;
            return id < sessionMap.Length && sessionMap[id] == session;
        }

        /// <summary>
        /// Takes one dump: snapshots the ring, selects and resolves the entries, captures the header, then hands the
        /// formatting and logging to the thread pool. Returns the rid, or null when the ring never recorded anything.
        /// Over the rate cap only the header is logged, with shown=0 skipped=rate.
        /// </summary>
        internal static string Dump(Session session, char kind, OutboundRingAnchor? anchorOverride)
        {
            var network = session.Network;
            var ring = network?.OutboundRing;
            if (ring == null || !ring.Allocated)
                return null;

            var snapshot = ring.Snapshot();
            var anchor = anchorOverride ?? snapshot.Anchor;

            var net = default(TeleportNetSnapshot);
            network.FillTeleportDiagnostics(ref net, false);

            bool allowed;
            lock (limiterLock)
                allowed = limiter.TryTake(Stopwatch.GetTimestamp());

            var selected = allowed
                ? OutboundRing.Select(snapshot.Entries, anchor, OutboundRingSettings.DumpEntries, OutboundRing.DefaultPreWindowTicks)
                : new List<OutboundRingEntry>();

            var wcids = new uint[selected.Count];
            for (var i = 0; i < selected.Count; i++)
                wcids[i] = selected[i].Guid == 0 ? 0 : network.CreatedWcid(selected[i].Guid);

            var nowUtc = DateTime.UtcNow;
            var player = session.Player;
            var rid = NextRid();

            var header = new OutboundRingDumpHeader
            {
                Rid = rid,
                Kind = kind,
                Player = player?.Name,
                Account = session.Account,
                Cell = player?.Location?.Cell ?? 0,
                Teleporting = player?.Teleporting ?? false,
                SinceInboundMs = net.LastPacketReceivedTicks == 0 ? -1 : (nowUtc.Ticks - net.LastPacketReceivedTicks) / TimeSpan.TicksPerMillisecond,
                ConnectionSeconds = (long)(nowUtc - session.CreatedUtc).TotalSeconds,
                Capacity = snapshot.Capacity,
                Written = snapshot.Written,
                Suppressed = snapshot.Suppressed,
                Caps = snapshot.Caps,
                ServerSequence = net.ServerPacketSequence,
                ClientAck = net.ClientAckSequence,
                AckAgoMs = net.ClientAckTicks == 0 ? -1 : (nowUtc.Ticks - net.ClientAckTicks) / TimeSpan.TicksPerMillisecond,
                ResendMisses = net.RetransmitsUncached,
                CrcFailures = net.CrcFailures,
                Shown = selected.Count,
                RateSkipped = !allowed,
                Evicted = OutboundRing.Evicted(snapshot.Written, snapshot.Capacity, anchor, OutboundRingSettings.DumpEntries),
            };

            Task.Run(() =>
            {
                try
                {
                    log.Info(OutboundRingFormat.Header(header));

                    for (var i = 0; i < selected.Count; i++)
                        log.Info(OutboundRingFormat.Entry(rid, kind, selected[i], anchor.Stamp, header.ClientAck, wcids[i]));
                }
                catch (Exception ex)
                {
                    log.Warn("[OUTBOUND_RING] dump logging failed for rid=" + rid, ex);
                }
            });

            return rid;
        }
    }
}
