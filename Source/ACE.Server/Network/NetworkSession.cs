using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using ACE.Common.Cryptography;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Network.Enum;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Handlers;
using ACE.Server.Network.Managers;
using ACE.Server.Network.Packets;

using log4net;

namespace ACE.Server.Network
{
    public class NetworkSession
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly ILog packetLog = LogManager.GetLogger(System.Reflection.Assembly.GetEntryAssembly(), "Packets");

        private const int minimumTimeBetweenBundles = 5; // 5ms
        private const int timeBetweenTimeSync = 20000; // 20s
        private const int timeBetweenAck = 2000; // 2s

        private readonly Session session;
        private readonly ConnectionListener connectionC2S; // This is the connection the client transmits on. In retail this would be port 9000 for GLS; For world servers, examples would be port 9002 / 9004 / 9006 / 9008.
        private readonly ConnectionListener connectionS2C; // This is the connection the server transmits on. In retail this would be port 9001 for GLS; For world servers, examples would be port 9003 / 9005 / 9007 / 9009.

        private readonly Object[] currentBundleLocks = new Object[(int)GameMessageGroup.QueueMax];
        private readonly NetworkBundle[] currentBundles = new NetworkBundle[(int)GameMessageGroup.QueueMax];

        private ConcurrentDictionary<uint, ClientPacket> outOfOrderPackets = new ConcurrentDictionary<uint, ClientPacket>();
        private ConcurrentDictionary<uint, MessageBuffer> partialFragments = new ConcurrentDictionary<uint, MessageBuffer>();
        private ConcurrentDictionary<uint, ClientMessage> outOfOrderFragments = new ConcurrentDictionary<uint, ClientMessage>();

        private DateTime nextSend = DateTime.UtcNow;

        // Resync will be started after ConnectResponse, and should immediately be sent then, so no delay here.
        // Fun fact: even though we send the server time in the ConnectRequest, client doesn't seem to use it?  Therefore we must TimeSync early so client doesn't see a skew when we send it later.
        public bool sendResync;
        private DateTime nextResync = DateTime.UtcNow;

        // Ack should be sent after a 2 second delay, so start enabled with the delay.
        // Sending this too early seems to cause issues with clients disconnecting.
        private bool sendAck = true;
        private DateTime nextAck = DateTime.UtcNow.AddMilliseconds(timeBetweenAck);

        private uint lastReceivedPacketSequence = 1;
        private uint lastReceivedFragmentSequence;

        /// <summary>
        /// This is referenced from many threads:<para />
        /// ConnectionListener.OnDataReceieve()->Session.HandlePacket()->This.HandlePacket(packet), This path can come from any client or other thinkable object.<para />
        /// WorldManager.UpdateWorld()->Session.Update(lastTick)->This.Update(lastTick)
        /// </summary>
        private readonly ConcurrentDictionary<uint /*seq*/, ServerPacket> cachedPackets = new ConcurrentDictionary<uint /*seq*/, ServerPacket>();

        private static readonly TimeSpan cachedPacketPruneInterval = TimeSpan.FromSeconds(5);
        private DateTime lastCachedPacketPruneTime;
        /// <summary>
        /// Number of seconds to retain cachedPackets
        /// </summary>
        private const int cachedPacketRetentionTime = 120;

        /// <summary>
        /// This is referenced by multiple thread:<para />
        /// [ConnectionListener Thread + 0] WorldManager.ProcessPacket()->SendLoginRequestReject()<para />
        /// [ConnectionListener Thread + 0] WorldManager.ProcessPacket()->Session.ProcessPacket()->NetworkSession.ProcessPacket()->DoRequestForRetransmission()<para />
        /// [ConnectionListener Thread + 1] WorldManager.ProcessPacket()->Session.ProcessPacket()->NetworkSession.ProcessPacket()-> ... AuthenticationHandler<para />
        /// [World Manager Thread] WorldManager.UpdateWorld()->Session.Update(lastTick)->This.Update(lastTick)<para />
        /// </summary>
        private readonly ConcurrentQueue<ServerPacket> packetQueue = new ConcurrentQueue<ServerPacket>();

        // Per-session network throughput. Mutated with Interlocked because outbound flush runs
        // under a Parallel.ForEach across sessions in NetworkManager.DoSessionWork.
        private long totalBytesSent;
        private long totalPacketsSent;
        private long totalBytesReceived;
        private long totalPacketsReceived;

        /// <summary>Cumulative UDP payload bytes sent to this session's client.</summary>
        public long TotalBytesSent => totalBytesSent;
        /// <summary>Cumulative UDP datagrams sent to this session's client.</summary>
        public long TotalPacketsSent => totalPacketsSent;
        /// <summary>Cumulative UDP payload bytes received from this session's client.</summary>
        public long TotalBytesReceived => totalBytesReceived;
        /// <summary>Cumulative UDP datagrams received from this session's client.</summary>
        public long TotalPacketsReceived => totalPacketsReceived;

        // Pending-teleport diagnostics (Player TELEPORT_PENDING / TELEPORT_ABANDONED). Written on the connection
        // listener thread (ProcessPacket and what it calls) and read from the player's landblock thread only when a
        // diagnostic line is written, so Interlocked/Volatile like the totals above. Nothing reads them to decide
        // anything.
        private long lastPacketReceivedTicks;
        private long crcFailures;
        private long retransmitsServed;
        private long retransmitsUncached;
        private long clientRetransmitRequests;
        private long serverRetransmitRequests;
        private long lastClientAckSequence;
        private long lastClientAckTicks;

        // Arrival-cost diagnostics ([TELEPORT_ABANDONED] creates/bytesOut/peakInflight, [TELEPORT_ARRIVAL_COST]).
        // createObjectsEnqueued is bumped by EnqueueSend on any thread; peakInflightSinceReset is raised after every
        // flush and zeroed by ResetArrivalPeaks when a teleport window opens. Both Interlocked. Logging only.
        private long createObjectsEnqueued;
        private long peakInflightSinceReset;

        // Dropped-session diagnostics (Session.DropSession): distinct guids ever created on this session, each mapped to
        // its wcid (the outbound ring resolves w= from it), capped so a long session cannot grow it without bound.
        // Bumped by EnqueueSend on any thread. Logging only.
        private const int DistinctCreatedCap = 4096;
        private readonly ConcurrentDictionary<uint, uint> distinctCreatedGuids = new ConcurrentDictionary<uint, uint>();
        private int distinctCreatedCount;
        private volatile bool distinctCreatedCapped;

        // Client object book hook (ClientObjectBook.EnqueueFollowed): delegates built once per session so the hook
        // allocates nothing per message, and a one-shot latch so a bookkeeping failure is logged once, not per message.
        private readonly Action<GameMessage> enqueueIntoBundle;
        private readonly Action<Exception> noteBookFailure;
        private int bookFailureLogged;

        // Outbound ring (OutboundRing, outbound_ring_* settings): every message dequeued by SendBundle, timed against the
        // last inbound packet, dumped when the client drops. Diagnostics only. The entry array is allocated on the
        // first record; ringFailureLogged latches the one failure log per session.
        private readonly OutboundRing outboundRing = new OutboundRing();
        private int ringFailureLogged;

        internal OutboundRing OutboundRing => outboundRing;

        /// <summary>CreateObject messages enqueued on this session so far. Logging only.</summary>
        public long CreateObjectsEnqueued => Interlocked.Read(ref createObjectsEnqueued);

        /// <summary>Distinct guids this session was sent a CreateObject for; a floor when <see cref="DistinctCreatedCapped"/>.</summary>
        public int DistinctCreated => Volatile.Read(ref distinctCreatedCount);

        /// <summary>True once a new guid arrived after the distinct-created set reached its cap.</summary>
        public bool DistinctCreatedCapped => distinctCreatedCapped;

        /// <summary>UTC ticks of the last packet received from the client, 0 before the first. Logging only.</summary>
        public long LastPacketReceivedTicks => Interlocked.Read(ref lastPacketReceivedTicks);

        // Outbound send pacing (network_pacing_enabled, default off). Created the first time a flush runs with pacing
        // on and never discarded after, so a session that was paced once keeps draining its held packets through the
        // paced path even after pacing is switched off. A session that was never paced never creates one and runs the
        // original flush loop unchanged. Every touch of the pacer is under pacerLock, because Update (and so the flush)
        // is reachable from two threads: Session.TickOutbound on the NetworkManager Parallel.ForEach, and
        // NetworkManager.SendLoginRequestReject / AuthenticationHandler on the connection listener thread.
        private readonly object pacerLock = new object();
        private volatile SendPacer<ServerPacket> pacer;
        private volatile bool pacingEnabledLastFlush;
        private static readonly System.Diagnostics.Stopwatch pacerClock = System.Diagnostics.Stopwatch.StartNew();

        // Lock-free mirrors of the pacer's depths for the diagnostic readers (FillTeleportDiagnostics,
        // ResetArrivalPeaks run on the world/landblock thread), which must never wait on pacerLock: the flush holds it
        // across socket.SendTo. Written with Interlocked at the end of every paced flush, under the lock.
        private int pacerHeldMirror;
        private long pacerHeldBytesMirror;
        private int pacerPeakHeldSinceReset;

        /// <summary>
        /// Fills the network half of a TeleportNetSnapshot. With <paramref name="includeDepths"/> false the two
        /// point-in-time depths are skipped: ConcurrentDictionary.Count takes every bucket lock, so they are read only
        /// when a diagnostic line is actually being written, never at teleport start.
        /// </summary>
        public void FillTeleportDiagnostics(ref ACE.Server.Entity.TeleportNetSnapshot snapshot, bool includeDepths)
        {
            snapshot.PacketsReceived = Interlocked.Read(ref totalPacketsReceived);
            snapshot.LastPacketReceivedTicks = Interlocked.Read(ref lastPacketReceivedTicks);
            snapshot.CrcFailures = Interlocked.Read(ref crcFailures);
            snapshot.RetransmitsServed = Interlocked.Read(ref retransmitsServed);
            snapshot.RetransmitsUncached = Interlocked.Read(ref retransmitsUncached);
            snapshot.ClientRetransmitRequests = Interlocked.Read(ref clientRetransmitRequests);
            snapshot.ServerRetransmitRequests = Interlocked.Read(ref serverRetransmitRequests);
            snapshot.ServerPacketSequence = ConnectionData.PacketSequence?.CurrentValue ?? 0;
            snapshot.ClientAckSequence = (uint)Interlocked.Read(ref lastClientAckSequence);
            snapshot.ClientAckTicks = Interlocked.Read(ref lastClientAckTicks);
            snapshot.CreateObjectsEnqueued = Interlocked.Read(ref createObjectsEnqueued);
            snapshot.BytesSent = Interlocked.Read(ref totalBytesSent);
            snapshot.PeakInflight = Interlocked.Read(ref peakInflightSinceReset);
            snapshot.PacingEnabled = pacingEnabledLastFlush;

            if (includeDepths)
            {
                snapshot.OutboundQueueDepth = packetQueue.Count;
                snapshot.RetainedPackets = cachedPackets.Count;

                // mirrors, never the pacer itself: no lock on the reader's thread
                snapshot.PacerHeld = Volatile.Read(ref pacerHeldMirror);
                snapshot.PacerHeldBytes = Interlocked.Read(ref pacerHeldBytesMirror);
                snapshot.PacerPeakHeld = Volatile.Read(ref pacerPeakHeldSinceReset);
            }
        }

        /// <summary>
        /// Zeroes the "since teleport start" peaks (in-flight packets, pacer held depth). Called when a teleport or
        /// login window opens, so the peaks on its lines belong to that window. Lock-free. A paced flush running at
        /// the same instant can still fold in the peak it sampled just before the reset, so the held peak may include
        /// up to one flush interval from before the window opened.
        /// </summary>
        public void ResetArrivalPeaks()
        {
            Interlocked.Exchange(ref peakInflightSinceReset, 0);
            Interlocked.Exchange(ref pacerPeakHeldSinceReset, 0);
        }

        /// <summary>
        /// Sent-but-unacked packets: the last assigned outbound sequence minus the client's last acked sequence. Before
        /// the first ack this is every sequenced packet sent so far. A <c>UIntSequence</c> built with
        /// clientPrimed false starts at uint.MaxValue (SessionConnectionData does exactly that), and that sentinel means
        /// nothing has been sent, so it is 0, not 4294967295.
        /// </summary>
        internal static long Inflight(uint seq, uint ack)
        {
            if (seq == uint.MaxValue)
                return 0;

            return seq > ack ? seq - ack : 0;
        }

        private long CurrentInflight()
        {
            return Inflight(ConnectionData.PacketSequence?.CurrentValue ?? 0u, (uint)Interlocked.Read(ref lastClientAckSequence));
        }

        private void SampleInflightPeak()
        {
            var inflight = CurrentInflight();

            long current;
            while (inflight > (current = Interlocked.Read(ref peakInflightSinceReset)))
            {
                if (Interlocked.CompareExchange(ref peakInflightSinceReset, inflight, current) == current)
                    break;
            }
        }

        // 1-second rolling window used to derive the *PerSecond properties below. Rolled forward
        // inside Update(), which already runs once per tick per session - no separate timer/thread.
        private DateTime throughputWindowStart = DateTime.UtcNow;
        private long throughputWindowBaseBytesSent;
        private long throughputWindowBasePacketsSent;
        private long throughputWindowBaseBytesReceived;
        private long throughputWindowBasePacketsReceived;

        /// <summary>UDP payload bytes/s sent to this session's client, over the last completed 1-second window.</summary>
        public double BytesSentPerSecond { get; private set; }
        /// <summary>UDP datagrams/s sent to this session's client, over the last completed 1-second window.</summary>
        public double PacketsSentPerSecond { get; private set; }
        /// <summary>UDP payload bytes/s received from this session's client, over the last completed 1-second window.</summary>
        public double BytesReceivedPerSecond { get; private set; }
        /// <summary>UDP datagrams/s received from this session's client, over the last completed 1-second window.</summary>
        public double PacketsReceivedPerSecond { get; private set; }

        public readonly SessionConnectionData ConnectionData = new SessionConnectionData();

        /// <summary>
        /// Stores the tick value for the when an active session will timeout. If this value is in the past, the session is dead/inactive.
        /// </summary>
        public long TimeoutTick { get; set; }

        public ushort ClientId { get; }
        public ushort ServerId { get; }

        public NetworkSession(Session session, ConnectionListener connectionListener, ushort clientId, ushort serverId)
        {
            this.session = session;
            connectionC2S = connectionListener;
            connectionS2C = SocketManager.GetMatchedConnectionListener(connectionC2S);

            ClientId = clientId;
            ServerId = serverId;

            // New network auth session timeouts will always be low.
            TimeoutTick = DateTime.UtcNow.AddSeconds(AuthenticationHandler.DefaultAuthTimeout).Ticks;

            for (int i = 0; i < currentBundles.Length; i++)
            {
                currentBundleLocks[i] = new object();
                currentBundles[i] = new NetworkBundle();
            }

            enqueueIntoBundle = EnqueueIntoBundle;
            noteBookFailure = NoteBookFailure;
        }

        /// <summary>
        /// A ClientObjectBook bookkeeping failure: the message was still enqueued. Logged once per session.
        /// </summary>
        private void NoteBookFailure(Exception ex)
        {
            if (Interlocked.Exchange(ref bookFailureLogged, 1) == 0)
                log.Error($"[{session.LoggingIdentifier}] ClientObjectBook bookkeeping failed; the message was still sent. Further failures on this session are not logged.", ex);
        }

        // /netcapture diagnostic. Only touched when NetCapture.AnyFlagged.
        private readonly NetCaptureBinder captureBinder = new NetCaptureBinder();

        internal NetCaptureRing GetCaptureRing()
        {
            return captureBinder.Resolve(session.Player?.Guid.Full ?? 0u, this, () => session.EndPointC2S?.ToString());
        }

        private void CaptureMessage(GameMessage message)
        {
            try
            {
                var ring = GetCaptureRing();
                if (ring == null)
                    return;
                NetCapture.RecordMessage(ring, message, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                log.Warn("NetCapture: message capture failed", ex);
            }
        }

        private void CaptureFragment(MessageFragment owner, ServerPacketFragment spf)
        {
            try
            {
                var ring = GetCaptureRing();
                if (ring == null)
                    return;
                ring.AddFragment(DateTime.UtcNow, (uint)owner.Message.Opcode, spf.Header.Sequence, spf.Header.Index, spf.Header.Count, spf.Header.Queue, spf.Data);
            }
            catch (Exception ex)
            {
                log.Warn("NetCapture: fragment capture failed", ex);
            }
        }

        /// <summary>
        /// Enequeues a GameMessage for sending to this client.
        /// This may be called from many threads.
        /// </summary>
        /// <param name="messages">One or more GameMessages to send</param>
        public void EnqueueSend(params GameMessage[] messages)
        {
            if (isReleased) // Session has been removed
                return;

            foreach (var message in messages)
            {
                if (NetCapture.AnyFlagged)
                    CaptureMessage(message);

                // Web command console capture (PLAN-P4.md 3.3): one int read when no web command runs.
                if (ACE.Server.Command.Web.WebCommandContext.IsCapturing && message is GameMessageSystemChat chat)
                    ACE.Server.Command.Web.WebCommandContext.TryCapture(session, ACE.Server.Command.Web.WebCommandContext.LevelChat, chat.Text);

                if (message.Opcode == GameMessageOpcode.ObjectCreate)
                {
                    Interlocked.Increment(ref createObjectsEnqueued);
                    NoteDistinctCreate(message);
                }

                // Client object book (teleport_flush_scope): bookkeeping only, never a hold or a delay. See ClientObjectBook.
                var book = ClientObjectBook.Follows(message) ? session.Player?.ClientObjectBook : null;
                if (book != null)
                    ClientObjectBook.EnqueueFollowed(book, message, enqueueIntoBundle, noteBookFailure);
                else
                    EnqueueIntoBundle(message);
            }
        }

        /// <summary>
        /// Enequeues a GameMessage for sending to this client.
        /// This may be called from many threads.
        /// </summary>
        /// <param name="messages">One or more GameMessages to send</param>
        public void EnqueueSend(IEnumerable<GameMessage> messages)
        {
            if (isReleased) // Session has been removed
                return;

            foreach (var message in messages)
            {
                if (NetCapture.AnyFlagged)
                    CaptureMessage(message);

                // Inside the existing loop, never a separate pass: a lazy sequence must be enumerated once.
                if (ACE.Server.Command.Web.WebCommandContext.IsCapturing && message is GameMessageSystemChat chat)
                    ACE.Server.Command.Web.WebCommandContext.TryCapture(session, ACE.Server.Command.Web.WebCommandContext.LevelChat, chat.Text);

                if (message.Opcode == GameMessageOpcode.ObjectCreate)
                {
                    Interlocked.Increment(ref createObjectsEnqueued);
                    NoteDistinctCreate(message);
                }

                // Client object book (teleport_flush_scope): bookkeeping only, never a hold or a delay. See ClientObjectBook.
                var book = ClientObjectBook.Follows(message) ? session.Player?.ClientObjectBook : null;
                if (book != null)
                    ClientObjectBook.EnqueueFollowed(book, message, enqueueIntoBundle, noteBookFailure);
                else
                    EnqueueIntoBundle(message);
            }
        }

        /// <summary>
        /// The bundle enqueue both EnqueueSend(GameMessage) overloads end in, unchanged from when it was inline in each.
        /// </summary>
        private void EnqueueIntoBundle(GameMessage message)
        {
            var grp = message.Group;
            var currentBundleLock = currentBundleLocks[(int)grp];
            lock (currentBundleLock)
            {
                var currentBundle = currentBundles[(int)grp];
                currentBundle.EncryptedChecksum = true;
                packetLog.DebugFormat("[{0}] Enqueuing Message {1}", session.LoggingIdentifier, message.Opcode);
                currentBundle.Enqueue(message);
            }
        }

        /// <summary>
        /// Counts each guid this session was ever sent a CreateObject for, once, for the dropped-session line's
        /// distinctCreated. Stops adding at <see cref="DistinctCreatedCap"/> and marks the count as a floor instead.
        /// Logging only.
        /// </summary>
        private void NoteDistinctCreate(GameMessage message)
        {
            if (!(message is GameMessageCreateObject create))
                return;

            if (Volatile.Read(ref distinctCreatedCount) >= DistinctCreatedCap)
            {
                if (!distinctCreatedGuids.ContainsKey(create.ObjectId))
                    distinctCreatedCapped = true;
                return;
            }

            if (distinctCreatedGuids.TryAdd(create.ObjectId, create.WeenieClassId))
                Interlocked.Increment(ref distinctCreatedCount);
        }

        /// <summary>The wcid of a guid this session was sent a CreateObject for, 0 when unknown. Logging only.</summary>
        internal uint CreatedWcid(uint guid)
        {
            return distinctCreatedGuids.TryGetValue(guid, out var wcid) ? wcid : 0;
        }

        /// <summary>
        /// Records one dequeued message in the outbound ring (SendBundle only, never EnqueueSend). Returns its ring
        /// index, 0 when suppressed or on failure. A failure never reaches the send path; the first one per session is
        /// logged.
        /// </summary>
        private long RecordOutbound(GameMessage message, long stamp)
        {
            try
            {
                OutboundMessageDecode.Decode(message, out var guid, out var aux, out var eventType);
                return outboundRing.Record(stamp, (ushort)message.Opcode, eventType, guid, aux, (int)message.Data.Length, 0, OutboundRingSettings.Entries);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref ringFailureLogged, 1) == 0)
                    log.Error($"[{session.LoggingIdentifier}] outbound ring record failed; the message was still sent. Further failures on this session are not logged.", ex);
                return 0;
            }
        }

        /// <summary>
        /// A message's last piece just went into a packet: tags the piece so the packet's sequence can be written back to
        /// the ring entry when the packet is stamped.
        /// </summary>
        internal static void TagFinalPiece(MessageFragment fragment, ServerPacketFragment piece)
        {
            if (fragment.RingIndex != 0 && fragment.DataRemaining <= 0)
                piece.RingIndex = fragment.RingIndex;
        }

        /// <summary>Writes a just-stamped packet's sequence back to the ring entries whose last piece it carries.</summary>
        private void NoteStampedRingEntries(ServerPacket packet)
        {
            try
            {
                foreach (var fragment in packet.Fragments)
                {
                    if (fragment is ServerPacketFragment piece && piece.RingIndex != 0)
                        outboundRing.NotePacketSeq(piece.RingIndex, packet.Header.Sequence);
                }
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref ringFailureLogged, 1) == 0)
                    log.Error($"[{session.LoggingIdentifier}] outbound ring sequence note failed; the packet was still sent. Further failures on this session are not logged.", ex);
            }
        }

        /// <summary>
        /// Enqueues a ServerPacket for sending to this client.
        /// Currently this is only used publicly once during login.  If that changes it's thread safety should be re
        /// </summary>
        /// <param name="packets"></param>
        public void EnqueueSend(params ServerPacket[] packets)
        {
            if (isReleased) // Session has been removed
                return;

            foreach (var packet in packets)
            {
                if (packetLog.IsDebugEnabled)
                    packetLog.DebugFormat("[{0}] Enqueuing Packet {1}", session.LoggingIdentifier, packet.GetHashCode());
                packetQueue.Enqueue(packet);
            }
        }

        /// <summary>
        /// Prunes the cachedPackets dictionary
        /// Checks if we should send the current bundle and then flushes all pending packets.
        /// </summary>
        public void Update()
        {
            if (isReleased) // Session has been removed
                return;

            RollThroughputWindow();

            if (DateTime.UtcNow - lastCachedPacketPruneTime > cachedPacketPruneInterval)
                PruneCachedPackets();

            for (int i = 0; i < currentBundles.Length; i++)
            {
                NetworkBundle bundleToSend = null;

                var group = (GameMessageGroup)i;

                var currentBundleLock = currentBundleLocks[i];
                lock (currentBundleLock)
                {
                    var currentBundle = currentBundles[i];

                    if (group == GameMessageGroup.InvalidQueue)
                    {
                        if (sendResync && !currentBundle.TimeSync && DateTime.UtcNow > nextResync)
                        {
                            packetLog.DebugFormat("[{0}] Setting to send TimeSync packet", session.LoggingIdentifier);
                            currentBundle.TimeSync = true;
                            currentBundle.EncryptedChecksum = true;
                            nextResync = DateTime.UtcNow.AddMilliseconds(timeBetweenTimeSync);
                        }

                        if (sendAck && !currentBundle.SendAck && DateTime.UtcNow > nextAck)
                        {
                            packetLog.DebugFormat("[{0}] Setting to send ACK packet", session.LoggingIdentifier);
                            currentBundle.SendAck = true;
                            nextAck = DateTime.UtcNow.AddMilliseconds(timeBetweenAck);
                        }

                        if (currentBundle.NeedsSending && DateTime.UtcNow >= nextSend)
                        {
                            packetLog.DebugFormat("[{0}] Swapping bundle", session.LoggingIdentifier);
                            // Swap out bundle so we can process it
                            bundleToSend = currentBundle;
                            currentBundles[i] = new NetworkBundle();
                        }
                    }
                    else
                    {
                        if (currentBundle.NeedsSending && DateTime.UtcNow >= nextSend)
                        {
                            packetLog.DebugFormat("[{0}] Swapping bundle", session.LoggingIdentifier);
                            // Swap out bundle so we can process it
                            bundleToSend = currentBundle;
                            currentBundles[i] = new NetworkBundle();
                        }
                    }
                }

                // Send our bundle if we have one
                // We should be able to execute this outside the lock as Sending is single threaded
                // and all future writes from other threads will go to the new bundle
                if (bundleToSend != null)
                {
                    SendBundle(bundleToSend, group);
                    nextSend = DateTime.UtcNow.AddMilliseconds(minimumTimeBetweenBundles);
                }
            }

            // Pacing tunables are read once per Update, never per packet. The numeric ones only when pacing is on.
            var pacingEnabled = PropertyManager.GetBool("network_pacing_enabled").Item;
            pacingEnabledLastFlush = pacingEnabled;

            if (!pacingEnabled && pacer == null)
                FlushPackets(); // never paced: the original path, unchanged
            else
            {
                var config = default(SendPacerConfig);
                if (pacingEnabled)
                {
                    config.RateBytesPerSecond = PropertyManager.GetLong("network_send_rate_bytes_per_sec").Item;
                    config.BurstBytes = PropertyManager.GetLong("network_send_burst_bytes").Item;
                    config.MaxInflightPackets = PropertyManager.GetLong("network_max_unacked_packets").Item;
                    config.TrickleMs = PropertyManager.GetLong("network_pacing_trickle_ms").Item;
                }

                // a session being torn down releases everything it holds, so boot/logoff messages are not stuck
                // behind the pacer during the termination window
                FlushPacketsPaced(pacingEnabled && session.PendingTermination == null, config);
            }

            SampleInflightPeak();
        }

        /// <summary>
        /// Rolls the 1-second throughput window forward when it has elapsed, deriving the
        /// *PerSecond properties from the delta against the totals recorded at the last roll.
        /// </summary>
        private void RollThroughputWindow()
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - throughputWindowStart).TotalSeconds;

            if (elapsed < 1.0)
                return;

            var currentBytesSent = totalBytesSent;
            var currentPacketsSent = totalPacketsSent;
            var currentBytesReceived = totalBytesReceived;
            var currentPacketsReceived = totalPacketsReceived;

            BytesSentPerSecond = (currentBytesSent - throughputWindowBaseBytesSent) / elapsed;
            PacketsSentPerSecond = (currentPacketsSent - throughputWindowBasePacketsSent) / elapsed;
            BytesReceivedPerSecond = (currentBytesReceived - throughputWindowBaseBytesReceived) / elapsed;
            PacketsReceivedPerSecond = (currentPacketsReceived - throughputWindowBasePacketsReceived) / elapsed;

            throughputWindowBaseBytesSent = currentBytesSent;
            throughputWindowBasePacketsSent = currentPacketsSent;
            throughputWindowBaseBytesReceived = currentBytesReceived;
            throughputWindowBasePacketsReceived = currentPacketsReceived;
            throughputWindowStart = now;
        }

        private void PruneCachedPackets()
        {
            lastCachedPacketPruneTime = DateTime.UtcNow;

            var currentTime = (ushort)Timers.PortalYearTicks;

            // Make sure our comparison still works when ushort wraps every 18.2 hours.
            var removalList = cachedPackets.Values.Where(x => (currentTime >= x.Header.Time ? currentTime : currentTime + ushort.MaxValue) - x.Header.Time > cachedPacketRetentionTime);

            foreach (var packet in removalList)
                cachedPackets.TryRemove(packet.Header.Sequence, out _);
        }

        // This is called from ConnectionListener.OnDataReceieve()->Session.ProcessPacket()->This
        /// <summary>
        /// Processes and incoming packet from a client.
        /// </summary>
        /// <param name="packet">The ClientPacket to process.</param>
        /// <param name="dataSize">
        /// The exact received UDP datagram length, from Socket.EndReceiveFrom() in
        /// ConnectionListener.OnDataReceive(). ClientPacket has no property that reconstructs this:
        /// Header.Size is only the post-header data length, and the receive buffer can be larger
        /// than Header.Size + PacketHeader.HeaderSize, so it can't be derived after the fact.
        /// </param>
        public void ProcessPacket(ClientPacket packet, int dataSize)
        {
            if (isReleased) // Session has been removed
                return;

            packetLog.DebugFormat("[{0}] Processing packet {1}", session.LoggingIdentifier, packet.Header.Sequence);
            NetworkStatistics.C2S_Packets_Aggregate_Increment();

            // Server-wide ace.network.bytes.received / ace.network.packets.received are recorded
            // once, in ConnectionListener.OnDataReceive, before the packet is routed to a session.
            // Only the per-session totals are recorded here.
            Interlocked.Add(ref totalBytesReceived, dataSize);
            Interlocked.Increment(ref totalPacketsReceived);
            Interlocked.Exchange(ref lastPacketReceivedTicks, DateTime.UtcNow.Ticks);
            if (OutboundRingSettings.Enabled)
                outboundRing.NoteInbound(System.Diagnostics.Stopwatch.GetTimestamp());

            if (!packet.VerifyCRC(ConnectionData.CryptoClient))
            {
                Interlocked.Increment(ref crcFailures);

                if (packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
                    NoteKeyFailure();

                return;
            }

            if (packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
                gapTracker.OnKeyVerified();

            // If the client sent a NAK with a cleartext CRC then process it
            if ((packet.Header.Flags & PacketHeaderFlags.RequestRetransmit) == PacketHeaderFlags.RequestRetransmit
                && !((packet.Header.Flags & PacketHeaderFlags.EncryptedChecksum) == PacketHeaderFlags.EncryptedChecksum))
            {
                List<uint> uncached = null;

                Interlocked.Increment(ref clientRetransmitRequests);

                foreach (uint sequence in packet.HeaderOptional.RetransmitData)
                {
                    if (!Retransmit(sequence))
                    {
                        if (uncached == null)
                            uncached = new List<uint>();

                        uncached.Add(sequence);
                        Interlocked.Increment(ref retransmitsUncached);
                    }
                    else
                        Interlocked.Increment(ref retransmitsServed);
                }

                if (uncached != null)
                {
                    // Sends a response packet w/ PacketHeader.RejectRetransmit
                    var packetRejectRetransmit = new PacketRejectRetransmit(uncached);
                    EnqueueSend(packetRejectRetransmit);
                }

                NetworkStatistics.C2S_RequestsForRetransmit_Aggregate_Increment();
                return; //cleartext crc NAK is never accompanied by additional data needed by the rest of the pipeline
            }

            #region order-insensitive "half-processing"

            if (packet.Header.HasFlag(PacketHeaderFlags.Disconnect))
            {
                session.Terminate(SessionTerminationReason.PacketHeaderDisconnect);
                return;
            }

            if (packet.Header.HasFlag(PacketHeaderFlags.NetErrorDisconnect))
            {
                session.Terminate(SessionTerminationReason.ClientSentNetworkErrorDisconnect);
                return;
            }

            // depending on the current session state:
            // Set the next timeout tick value, to compare against in the WorldManager
            // Sessions that have gone past the AuthLoginRequest step will stay active for a longer period of time (exposed via configuration) 
            // Sessions that in the AuthLoginRequest will have a short timeout, as set in the AuthenticationHandler.DefaultAuthTimeout.
            // Example: Applications that check uptime will stay in the AuthLoginRequest state.
            session.Network.TimeoutTick = (session.State == SessionState.AuthLoginRequest) ?
                DateTime.UtcNow.AddSeconds(AuthenticationHandler.DefaultAuthTimeout).Ticks : // Default is 15s
                DateTime.UtcNow.AddSeconds(NetworkManager.DefaultSessionTimeout).Ticks; // Default is 60s

            #endregion

            #region Reordering stage

            // Reordering stage
            // Check if this packet's sequence is a sequence which we have already processed.
            // There are some exceptions:
            // Sequence 0 as we have several Seq 0 packets during connect.  This also cathes a case where it seems CICMDCommand arrives at any point with 0 sequence value too.
            // If the only header on the packet is AckSequence. It seems AckSequence can come in with the same sequence value sometimes.
            if (packet.Header.Sequence <= lastReceivedPacketSequence && packet.Header.Sequence != 0 &&
                !(packet.Header.Flags == PacketHeaderFlags.AckSequence && packet.Header.Sequence == lastReceivedPacketSequence))
            {
                packetLog.WarnFormat("[{0}] Packet {1} received again", session.LoggingIdentifier, packet.Header.Sequence);
                return;
            }

            // Check if this packet's sequence is greater then the next one we should be getting.
            // If true we must store it to replay once we have caught up.
            var desiredSeq = lastReceivedPacketSequence + 1;
            if (packet.Header.Sequence > desiredSeq)
            {
                packetLog.DebugFormat("[{0}] Packet {1} received out of order", session.LoggingIdentifier, packet.Header.Sequence);

                var queueWasEmpty = outOfOrderPackets.IsEmpty;

                if (!outOfOrderPackets.ContainsKey(packet.Header.Sequence))
                    outOfOrderPackets.TryAdd(packet.Header.Sequence, packet);

                var now = DateTime.UtcNow;
                gapTracker.OnQueued(lastReceivedPacketSequence, packet.Header.Sequence, now, queueWasEmpty);

                if (desiredSeq + 2 <= packet.Header.Sequence && now - LastRequestForRetransmitTime > new TimeSpan(0, 0, 1))
                    DoRequestForRetransmission(packet.Header.Sequence);

                CheckInboundGap(now);

                return;
            }

            #endregion

            #region Final processing stage

            var sequenceBefore = lastReceivedPacketSequence;

            // Processing stage
            // If we reach here, this is a packet we should proceed with processing.
            HandleOrderedPacket(packet);

            // Process data now in sequence
            // Finally check if we have any out of order packets or fragments we need to process;
            CheckOutOfOrderPackets();
            CheckOutOfOrderFragments();

            if (lastReceivedPacketSequence != sequenceBefore)
                NoteSequenceAdvanced();
            else if (gapTracker.InGap && outOfOrderPackets.IsEmpty)
                gapTracker.OnDrainedWithoutAdvance(); // only a queued ack-only packet was drained: close, never leave it stale

            #endregion
        }

        // Inbound loss recovery (InboundGapTracker, network_inbound_stall_seconds). Touched only on this session's
        // connection-listener receive path, like the reorder state above.
        private readonly InboundGapTracker gapTracker = new InboundGapTracker();

        /// <summary>
        /// The in-order sequence moved. Closes the gap once nothing is left queued, and logs the recovery of one wider
        /// than the legacy 256 limit (rate-limited per session), so the larger window can be confirmed on a live server.
        /// </summary>
        private void NoteSequenceAdvanced()
        {
            // only consult the queue while a gap is open: Count/IsEmpty are never paid on a session with no loss
            if (!gapTracker.InGap)
            {
                gapTracker.OnAdvanced(true, DateTime.UtcNow);
                return;
            }

            var startSeq = gapTracker.GapStartSequence;
            var highSeq = gapTracker.GapHighSequence;

            var recovery = gapTracker.OnAdvanced(outOfOrderPackets.IsEmpty, DateTime.UtcNow);

            if (recovery is InboundGapRecovery r && r.Log)
                log.Info($"[INBOUND_GAP_RECOVERED] [{session.LoggingIdentifier}] span={r.Span} seq={startSeq}->{highSeq} ms={(long)r.Elapsed.TotalMilliseconds} naks={r.Naks} window={ConnectionData.CryptoClient.EffortLevel} suppressed={r.Suppressed}");
        }

        /// <summary>
        /// Ends the session when an open gap can no longer recover: the in-order sequence has not moved for
        /// network_inbound_stall_seconds while packets wait behind it (the missing ones are never coming), or more
        /// packets are queued than any gap inside the crypto window can produce. One INFO line says which.
        /// </summary>
        private void CheckInboundGap(DateTime now)
        {
            if (session.PendingTermination != null)
                return;

            var window = ConnectionData.CryptoClient.EffortLevel;
            var queued = outOfOrderPackets.Count;
            var stallSeconds = InboundRecoverySettings.StallSeconds;

            var verdict = gapTracker.Check(queued, InboundGapTracker.QueueCap(window), now, TimeSpan.FromSeconds(stallSeconds));
            if (verdict == InboundGapVerdict.None)
                return;

            var what = verdict == InboundGapVerdict.Stalled ? "stalled" : "queue_overflow";

            log.Info($"[INBOUND_GAP_ENDED] [{session.LoggingIdentifier}] cause={what} span={gapTracker.GapSpan} seq={gapTracker.GapStartSequence}->{gapTracker.GapHighSequence} last_seq={lastReceivedPacketSequence} queued={queued} naks={gapTracker.GapNaks} stalled_s={(long)(now - gapTracker.StallSinceUtc).TotalSeconds} open_s={(long)(now - gapTracker.GapOpenedUtc).TotalSeconds} window={window} stall_limit_s={stallSeconds}");

            session.Terminate(SessionTerminationReason.AbnormalSequenceReceived, null, null, verdict == InboundGapVerdict.Stalled
                ? "inbound sequence stalled: missing packets were never retransmitted"
                : "inbound out-of-order queue over its cap");
        }

        /// <summary>
        /// An encrypted-checksum packet failed to verify. When they have all failed for network_inbound_stall_seconds,
        /// the client's key stream is beyond the window and nothing it sends will verify again; cleartext acks would
        /// otherwise keep the session alive indefinitely, so it is ended with one INFO line.
        /// </summary>
        private void NoteKeyFailure()
        {
            var now = DateTime.UtcNow;
            gapTracker.OnKeyFailure(now);

            if (session.PendingTermination != null)
                return;

            var stallSeconds = InboundRecoverySettings.StallSeconds;
            if (!gapTracker.KeyStreamLost(now, TimeSpan.FromSeconds(stallSeconds)))
                return;

            var crypto = ConnectionData.CryptoClient;

            log.Info($"[INBOUND_KEYSTREAM_LOST] [{session.LoggingIdentifier}] failures_in_row={gapTracker.KeyFailuresInRow} since_s={(long)(now - gapTracker.FirstKeyFailureUtc).TotalSeconds} last_seq={lastReceivedPacketSequence} window={crypto.EffortLevel} outstanding_keys={crypto.OutstandingKeys} stall_limit_s={stallSeconds}");

            session.Terminate(SessionTerminationReason.ClientConnectionFailure, null, null, "checksum key stream lost: no encrypted packet verified");
        }

        const uint MaxNumNakSeqIds = 115; //464 + header = 484;  (464 - 4) / 4

        /// <summary>
        /// request retransmission of lost sequences
        /// </summary>
        /// <param name="rcvdSeq">the sequence of the packet that was just received.</param>
        private void DoRequestForRetransmission(uint rcvdSeq)
        {
            var desiredSeq = lastReceivedPacketSequence + 1;
            List<uint> needSeq = new List<uint>();
            needSeq.Add(desiredSeq);
            uint bottom = desiredSeq + 1;
            // the bound is this session's crypto window: a gap the checksum layer can accept is not ended here
            if (InboundGapTracker.ExceedsReorderBound(rcvdSeq, lastReceivedPacketSequence, ConnectionData.CryptoClient.EffortLevel))
            {
                session.Terminate(SessionTerminationReason.AbnormalSequenceReceived);
                return;
            }
            uint seqIdCount = 1;
            for (uint a = bottom; a < rcvdSeq; a++)
            {
                if (!outOfOrderPackets.ContainsKey(a))
                {
                    needSeq.Add(a);
                    seqIdCount++;
                    if (seqIdCount >= MaxNumNakSeqIds)
                    {
                        break;
                    }
                }
            }

            ServerPacket reqPacket = new ServerPacket();
            byte[] reqData = new byte[4 + (needSeq.Count * 4)];
            MemoryStream msReqData = new MemoryStream(reqData, 0, reqData.Length, true, true);
            msReqData.Write(BitConverter.GetBytes((uint)needSeq.Count), 0, 4);
            needSeq.ForEach(k => msReqData.Write(BitConverter.GetBytes(k), 0, 4));
            reqPacket.Data = msReqData;
            reqPacket.Header.Flags = PacketHeaderFlags.RequestRetransmit;

            EnqueueSend(reqPacket);

            LastRequestForRetransmitTime = DateTime.UtcNow;
            if (packetLog.IsDebugEnabled)
                packetLog.DebugFormat("[{0}] Requested retransmit of {1}", session.LoggingIdentifier, needSeq.Select(k => k.ToString()).Aggregate((a, b) => a + ", " + b));
            NetworkStatistics.S2C_RequestsForRetransmit_Aggregate_Increment();
            Interlocked.Increment(ref serverRetransmitRequests);
            gapTracker.OnNak();
        }

        private DateTime LastRequestForRetransmitTime = DateTime.MinValue;

        /// <summary>
        /// Handles a packet<para />
        /// Packets at this stage are already verified, "half processed", and reordered
        /// </summary>
        /// <param name="packet">ClientPacket to handle</param>
        private void HandleOrderedPacket(ClientPacket packet)
        {
            packetLog.DebugFormat("[{0}] Handling packet {1}", session.LoggingIdentifier, packet.Header.Sequence);

            // If we have an EchoRequest flag, we should flag to respond with an echo response on next send.
            if (packet.Header.HasFlag(PacketHeaderFlags.EchoRequest))
            {
                FlagEcho(packet.HeaderOptional.EchoRequestClientTime);
                VerifyEcho(packet.HeaderOptional.EchoRequestClientTime);
            }

            // If we have an AcknowledgeSequence flag, we can clear our cached packet buffer up to that sequence.
            if (packet.Header.HasFlag(PacketHeaderFlags.AckSequence))
                AcknowledgeSequence(packet.HeaderOptional.AckSequence);

            if (packet.Header.HasFlag(PacketHeaderFlags.TimeSync))
            {
                packetLog.DebugFormat("[{0}] Incoming TimeSync TS: {1}", session.LoggingIdentifier, packet.HeaderOptional.TimeSynch);
                // Do something with this...
                // Based on network traces these are not 1:1.  Server seems to send them every 20 seconds per port.
                // Client seems to send them alternatingly every 2 or 4 seconds per port.
                // We will send this at a 20 second time interval.  I don't know what to do with these when we receive them at this point.
            }

            // This should be set on the first packet to the server indicating the client is logging in.
            // This is the start of a three-way handshake between the client and server (LoginRequest, ConnectRequest, ConnectResponse)
            // Note this would be sent to each server a client would connect too (Login and each world).
            // In our current implimenation we handle all roles in this one server.
            if (packet.Header.HasFlag(PacketHeaderFlags.LoginRequest))
            {
                packetLog.DebugFormat("[{0}] LoginRequest", session.LoggingIdentifier);
                AuthenticationHandler.HandleLoginRequest(packet, session);
                return;
            }

            // Process all fragments out of the packet
            foreach (ClientPacketFragment fragment in packet.Fragments)
                ProcessFragment(fragment);

            // Update the last received sequence.
            if (packet.Header.Sequence != 0 && packet.Header.Flags != PacketHeaderFlags.AckSequence)
                lastReceivedPacketSequence = packet.Header.Sequence;
        }

        /// <summary>
        /// Processes a fragment, combining split fragments as needed, then handling them
        /// </summary>
        /// <param name="fragment">ClientPacketFragment to process</param>
        private void ProcessFragment(ClientPacketFragment fragment)
        {
            packetLog.DebugFormat("[{0}] Processing fragment {1}", session.LoggingIdentifier, fragment.Header.Sequence);

            ClientMessage message = null;

            // Check if this fragment is split
            if (fragment.Header.Count != 1)
            {
                // Packet is split
                packetLog.DebugFormat("[{0}] Fragment {1} is split, this index {2} of {3} fragments", session.LoggingIdentifier, fragment.Header.Sequence, fragment.Header.Index, fragment.Header.Count);

                if (partialFragments.TryGetValue(fragment.Header.Sequence, out var buffer))
                {
                    // Existing buffer, add this to it and check if we are finally complete.
                    buffer.AddFragment(fragment);
                    packetLog.DebugFormat("[{0}] Added fragment {1} to existing buffer. Buffer at {2} of {3}", session.LoggingIdentifier, fragment.Header.Sequence, buffer.Count, buffer.TotalFragments);
                    if (buffer.Complete)
                    {
                        // The buffer is complete, so we can go ahead and handle
                        packetLog.DebugFormat("[{0}] Buffer {1} is complete", session.LoggingIdentifier, buffer.Sequence);
                        message = buffer.TryGetMessage();
                        MessageBuffer removed = null;
                        partialFragments.TryRemove(fragment.Header.Sequence, out removed);
                    }
                }
                else
                {
                    // No existing buffer, so add a new one for this fragment sequence.
                    packetLog.DebugFormat("[{0}] Creating new buffer {1} for this split fragment", session.LoggingIdentifier, fragment.Header.Sequence);
                    var newBuffer = new MessageBuffer(fragment.Header.Sequence, fragment.Header.Count);
                    newBuffer.AddFragment(fragment);

                    packetLog.DebugFormat("[{0}] Added fragment {1} to the new buffer. Buffer at {2} of {3}", session.LoggingIdentifier, fragment.Header.Sequence, newBuffer.Count, newBuffer.TotalFragments);
                    partialFragments.TryAdd(fragment.Header.Sequence, newBuffer);
                }
            }
            else
            {
                // Packet is not split, proceed with handling it.
                packetLog.DebugFormat("[{0}] Fragment {1} is not split", session.LoggingIdentifier, fragment.Header.Sequence);

                if (fragment.Data.Length >= 4) // ClientMessage must be a minimum of 4 bytes in length
                    message = new ClientMessage(fragment.Data);
            }

            // If message is not null, we have a complete message to handle
            if (message != null)
            {
                // First check if this message is the next sequence, if it is not, add it to our outOfOrderFragments
                if (fragment.Header.Sequence == lastReceivedFragmentSequence + 1)
                {
                    packetLog.DebugFormat("[{0}] Handling fragment {1}", session.LoggingIdentifier, fragment.Header.Sequence);
                    HandleFragment(message);
                }
                else
                {
                    packetLog.DebugFormat("[{0}] Fragment {1} is early, lastReceivedFragmentSequence = {2}", session.LoggingIdentifier, fragment.Header.Sequence, lastReceivedFragmentSequence);
                    outOfOrderFragments.TryAdd(fragment.Header.Sequence, message);
                }
            }
        }

        /// <summary>
        /// Handles a ClientMessage by calling using InboundMessageManager
        /// </summary>
        /// <param name="message">ClientMessage to process</param>
        private void HandleFragment(ClientMessage message)
        {
            InboundMessageManager.HandleClientMessage(message, session);
            lastReceivedFragmentSequence++;
        }

        /// <summary>
        /// Checks if we now have packets queued out of order which should be processed as the next sequence.
        /// </summary>
        private void CheckOutOfOrderPackets()
        {
            while (outOfOrderPackets.TryRemove(lastReceivedPacketSequence + 1, out var packet))
            {
                packetLog.DebugFormat("[{0}] Ready to handle out-of-order packet {1}", session.LoggingIdentifier, packet.Header.Sequence);
                HandleOrderedPacket(packet);
            }
        }

        /// <summary>
        /// Checks if we now have fragments queued out of order which should be handled as the next sequence.
        /// </summary>
        private void CheckOutOfOrderFragments()
        {
            while (outOfOrderFragments.TryRemove(lastReceivedFragmentSequence + 1, out var message))
            {
                packetLog.DebugFormat("[{0}] Ready to handle out of order fragment {1}", session.LoggingIdentifier, lastReceivedFragmentSequence + 1);
                HandleFragment(message);
            }
        }

        //private List<EchoStamp> EchoStamps = new List<EchoStamp>();

        private static int EchoLogInterval = 5;
        private static int EchoInterval = 10;
        private static float EchoThreshold = 2.0f;
        private static float DiffThreshold = 0.01f;

        private float lastClientTime;
        private DateTime lastServerTime;

        private double lastDiff;
        private int echoDiff;

        private void VerifyEcho(float clientTime)
        {
            if (session.Player == null || session.logOffRequestTime != DateTime.MinValue)
                return;

            var serverTime = DateTime.UtcNow;

            if (lastClientTime == 0)
            {
                lastClientTime = clientTime;
                lastServerTime = serverTime;
                return;
            }

            var serverTimeDiff = serverTime - lastServerTime;
            var clientTimeDiff = clientTime - lastClientTime;

            var diff = Math.Abs(serverTimeDiff.TotalSeconds - clientTimeDiff);

            if (diff > EchoThreshold && diff - lastDiff > DiffThreshold)
            {
                lastDiff = diff;
                echoDiff++;

                if (echoDiff >= EchoLogInterval)
                    log.Warn($"{session.Player.Name} - TimeSync error: {echoDiff} (diff: {diff})");

                if (echoDiff >= EchoInterval)
                {
                    log.Error($"{session.Player.Name} - disconnected for speedhacking");

                    var actionChain = new ActionChain();
                    actionChain.AddAction(session.Player, () =>
                    {
                        //session.Network.EnqueueSend(new GameMessageBootAccount(session));
                        session.Network.EnqueueSend(new GameMessageSystemChat($"TimeSync: client speed error", ChatMessageType.Broadcast));
                        session.LogOffPlayer();

                        echoDiff = 0;
                        lastDiff = 0;
                        lastClientTime = 0;

                    });
                    actionChain.EnqueueChain();
                }
            }
            else if (echoDiff > 0)
            {
                if (echoDiff > EchoLogInterval)
                    log.Warn($"{session.Player.Name} - Diff: {diff}");

                lastDiff = 0;
                echoDiff = 0;
            }
        }

        //is this special channel
        private void FlagEcho(float clientTime)
        {
            var currentBundleLock = currentBundleLocks[(int)GameMessageGroup.InvalidQueue];
            lock (currentBundleLock)
            {
                var currentBundle = currentBundles[(int)GameMessageGroup.InvalidQueue];

                // Debug.Assert(clientTime == -1f, "Multiple EchoRequests before Flush, potential issue with network logic!");
                currentBundle.ClientTime = clientTime;
                currentBundle.EncryptedChecksum = true;
            }
        }

        private void AcknowledgeSequence(uint sequence)
        {
            // TODO Sending Acks seems to cause some issues.  Needs further research.
            // if (!sendAck)
            //    sendAck = true;

            Interlocked.Exchange(ref lastClientAckSequence, sequence);
            Interlocked.Exchange(ref lastClientAckTicks, DateTime.UtcNow.Ticks);

            var removalList = cachedPackets.Keys.Where(x => x < sequence);

            foreach (var key in removalList)
                cachedPackets.TryRemove(key, out _);
        }

        private bool Retransmit(uint sequence)
        {
            if (cachedPackets.TryGetValue(sequence, out var cachedPacket))
            {
                packetLog.DebugFormat("[{0}] Retransmit {1}", session.LoggingIdentifier, sequence);

                if (!cachedPacket.Header.HasFlag(PacketHeaderFlags.Retransmission))
                    cachedPacket.Header.Flags |= PacketHeaderFlags.Retransmission;

                SendPacketRaw(cachedPacket);

                return true;
            }

            if (packetLog.IsDebugEnabled)
            {
                if (cachedPackets.Count > 0)
                {
                    // This is to catch a race condition between .Count and .Min() and .Max()
                    try
                    {
                        packetLog.DebugFormat("Session {0}\\{1} ({2}:{3}) retransmit requested packet {4} not in cache. Cache range {5} - {6}.", session.Network?.ClientId, session.EndPointC2S, session.Account, session.Player?.Name, sequence, cachedPackets.Keys.Min(), cachedPackets.Keys.Max());
                    }
                    catch
                    {
                        packetLog.DebugFormat("Session {0}\\{1} ({2}:{3}) retransmit requested packet {4} not in cache. Cache is empty. Race condition threw exception.", session.Network?.ClientId, session.EndPointC2S, session.Account, session.Player?.Name, sequence);
                    }
                }
                else
                    packetLog.DebugFormat("Session {0}\\{1} ({2}:{3}) retransmit requested packet {4} not in cache. Cache is empty.", session.Network?.ClientId, session.EndPointC2S, session.Account, session.Player?.Name, sequence);
            }

            return false;
        }

        private void FlushPackets()
        {
            while (packetQueue.TryDequeue(out var packet))
            {
                packetLog.DebugFormat("[{0}] Flushing packets, count {1}", session.LoggingIdentifier, packetQueue.Count);

                StampAndSend(packet);
            }
        }

        /// <summary>
        /// Gives a packet its protocol state (sequence, id, time, retransmit cache entry) and sends it, which applies the
        /// checksum key. The one place that happens, for both flush paths: a packet held by the pacer has none of this
        /// until it is released here, so holding it never leaves a gap in the sequence or the ISAAC stream.
        /// </summary>
        private void StampAndSend(ServerPacket packet)
        {
            if (packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum) && ConnectionData.PacketSequence.CurrentValue == 0)
                ConnectionData.PacketSequence = new Sequence.UIntSequence(1);

            bool isNak = packet.Header.Flags.HasFlag(PacketHeaderFlags.RequestRetransmit);

            // If we are only ACKing, then we don't seem to have to increment the sequence
            if (packet.Header.Flags == PacketHeaderFlags.AckSequence || isNak)
                packet.Header.Sequence = ConnectionData.PacketSequence.CurrentValue;
            else
                packet.Header.Sequence = ConnectionData.PacketSequence.NextValue;
            packet.Header.Id = ServerId;
            packet.Header.Iteration = 0x01;
            packet.Header.Time = (ushort)Timers.PortalYearTicks;

            if (packet.Header.Sequence >= 2u && !isNak)
                cachedPackets.TryAdd(packet.Header.Sequence, packet);

            if (packet.Fragments.Count > 0)
                NoteStampedRingEntries(packet);

            SendPacket(packet);
        }

        /// <summary>
        /// Packets that never wait behind the pacer:
        /// <para>- everything without an encrypted checksum: a bare ack, a NAK, a RejectRetransmit, the cleartext login
        /// handshake;</para>
        /// <para>- every packet with no fragments: optional-header-only packets such as a standalone TimeSync or
        /// EchoResponse, which carry Timers.PortalYearTicks in their payload and would report a stale time if
        /// held.</para>
        /// Overtaking held data is safe for all of them because a held packet has no protocol state: the sequence
        /// (NextValue or CurrentValue) and the ISAAC key (IssacServer.Next in SendPacket) are both taken inside
        /// StampAndSend, in call order, under pacerLock, so a bypass packet simply takes the next value and the held
        /// packets take the ones after it. No gap and no reuse. An echo or time sync that rides in the first packet of
        /// a bundle that also carries message fragments is still paced with that packet.
        /// </summary>
        internal static bool BypassesPacer(ServerPacket packet) =>
            !packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum) || packet.Fragments.Count == 0;

        /// <summary>Bytes the packet puts on the wire: header + optional-header data + every fragment.</summary>
        internal static int WireSize(ServerPacket packet)
        {
            var size = PacketHeader.HeaderSize + (int)(packet.Data?.Length ?? 0);

            foreach (var fragment in packet.Fragments)
                size += fragment.Length;

            return size;
        }

        /// <summary>
        /// The paced flush. New packets enter the pacer's FIFO with no protocol state; the pacer releases them (into
        /// StampAndSend) as the byte bucket and the in-flight cap allow. With <paramref name="paced"/> false (pacing
        /// switched off on a session that was paced earlier, or the session terminating) everything held is released
        /// at once, in order, before anything newer.
        /// </summary>
        private void FlushPacketsPaced(bool paced, SendPacerConfig config)
        {
            lock (pacerLock)
            {
                if (isReleased)
                    return;

                var p = pacer;
                if (p == null)
                    pacer = p = new SendPacer<ServerPacket>(() => pacerClock.ElapsedMilliseconds);

                FlushThroughPacer(packetQueue, p, paced, config, CurrentInflight, StampAndSend);

                // publish the depths for the lock-free diagnostic readers
                Volatile.Write(ref pacerHeldMirror, p.HeldCount);
                Interlocked.Exchange(ref pacerHeldBytesMirror, p.HeldBytes);

                var peak = p.TakePeak();
                int current;
                while (peak > (current = Volatile.Read(ref pacerPeakHeldSinceReset)))
                {
                    if (Interlocked.CompareExchange(ref pacerPeakHeldSinceReset, peak, current) == current)
                        break;
                }
            }
        }

        /// <summary>
        /// The paced flush's core, static so it can be tested without a session: every queued packet is offered to the
        /// pacer (bypass packets are sent at once), then the pacer releases what the rules allow, or, with
        /// <paramref name="paced"/> false, everything it holds, oldest first.
        /// </summary>
        internal static void FlushThroughPacer(ConcurrentQueue<ServerPacket> queue, SendPacer<ServerPacket> pacer, bool paced, SendPacerConfig config,
            Func<long> inflight, Action<ServerPacket> stampAndSend)
        {
            while (queue.TryDequeue(out var packet))
            {
                if (pacer.Offer(packet, WireSize(packet), BypassesPacer(packet)))
                    stampAndSend(packet);
            }

            if (paced)
                pacer.Drain(config, inflight, stampAndSend);
            else
                pacer.DrainAll(stampAndSend);
        }

        private void SendPacket(ServerPacket packet)
        {
            if (packetLog.IsDebugEnabled)
                packetLog.DebugFormat("[{0}] Sending packet {1}", session.LoggingIdentifier, packet.GetHashCode());
            NetworkStatistics.S2C_Packets_Aggregate_Increment();

            if (packet.Header.HasFlag(PacketHeaderFlags.EncryptedChecksum))
            {
                uint issacXor = ConnectionData.IssacServer.Next();
                if (packetLog.IsDebugEnabled)
                    packetLog.DebugFormat("[{0}] Setting Issac for packet {1} to {2}", session.LoggingIdentifier, packet.GetHashCode(), issacXor);
                packet.IssacXor = issacXor;
            }

            SendPacketRaw(packet);
        }

        private void SendPacketRaw(ServerPacket packet)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent((int)(PacketHeader.HeaderSize + (packet.Data?.Length ?? 0) + (packet.Fragments.Count * PacketFragment.MaxFragementSize)));

            try
            {
                // On connection to server, client expects response on the connection it initiated, once that occurs, the client connects to the +1 port and then the server transmits on that connection, while the client continues to transmit on the initial port.
                var socket = (session.EndPointS2C is null) ? connectionC2S.Socket : connectionS2C.Socket;
                var endPoint = (session.EndPointS2C is null) ? session.EndPointC2S : session.EndPointS2C;

                packet.CreateReadyToSendPacket(buffer, out var size);

                packetLog.DebugFormat("{0}", packet);

                if (packetLog.IsDebugEnabled)
                {
                    var listenerEndpoint = (System.Net.IPEndPoint)socket.LocalEndPoint;
                    var sb = new StringBuilder();
                    sb.AppendLine(String.Format("[{5}] Sending Packet (Len: {0}) [{1}:{2}=>{3}:{4}]", size, listenerEndpoint.Address, listenerEndpoint.Port, endPoint.Address, endPoint.Port, session.Network.ClientId));
                    sb.AppendLine(buffer.BuildPacketString(0, size));
                    packetLog.DebugFormat("{0}", sb);
                }

                try
                {
                    socket.SendTo(buffer, size, SocketFlags.None, endPoint);

                    ServerMetrics.BytesSent.Add(size);
                    ServerMetrics.PacketsSent.Add(1);
                    NetworkStatistics.S2C_Bytes_Aggregate_Add(size);
                    Interlocked.Add(ref totalBytesSent, size);
                    Interlocked.Increment(ref totalPacketsSent);
                }
                catch (SocketException ex)
                {
                    // Unhandled Exception: System.Net.Sockets.SocketException: A message sent on a datagram socket was larger than the internal message buffer or some other network limit, or the buffer used to receive a datagram into was smaller than the datagram itself
                    // at System.Net.Sockets.Socket.UpdateStatusAfterSocketErrorAndThrowException(SocketError error, String callerName)
                    // at System.Net.Sockets.Socket.SendTo(Byte[] buffer, Int32 offset, Int32 size, SocketFlags socketFlags, EndPoint remoteEP)

                    var listenerEndpoint = (System.Net.IPEndPoint)socket.LocalEndPoint;
                    var sb = new StringBuilder();
                    sb.AppendLine(ex.ToString());
                    sb.AppendLine(String.Format("[{5}] Sending Packet (Len: {0}) [{1}:{2}=>{3}:{4}]", buffer.Length, listenerEndpoint.Address, listenerEndpoint.Port, endPoint.Address, endPoint.Port, session.Network.ClientId));
                    log.Error(sb.ToString());

                    session.Terminate(SessionTerminationReason.SendToSocketException, null, null, ex.Message);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, true);
            }
        }

        /// <summary>
        /// This function handles turning a bundle of messages (representing all messages accrued in a timeslice),
        /// into 1 or more packets, combining multiple messages into one packet or spliting large message across
        /// several packets as needed.
        /// </summary>
        /// <param name="bundle"></param>
        private void SendBundle(NetworkBundle bundle, GameMessageGroup group)
        {
            packetLog.DebugFormat("[{0}] Sending Bundle", session.LoggingIdentifier);

            bool writeOptionalHeaders = true;

            List<MessageFragment> fragments = new List<MessageFragment>();

            // Outbound ring: one timestamp per bundle, never per message
            var ringOn = OutboundRingSettings.Enabled;
            var ringStamp = ringOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            // Pull all messages out and create MessageFragment objects
            while (bundle.HasMoreMessages)
            {
                var message = bundle.Dequeue();

                var fragment = new MessageFragment(message, ConnectionData.FragmentSequence++);
                if (ringOn)
                    fragment.RingIndex = RecordOutbound(message, ringStamp);
                fragments.Add(fragment);
            }

            packetLog.DebugFormat("[{0}] Bundle Fragment Count: {1}", session.LoggingIdentifier, fragments.Count);

            // Loop through while we have fragements
            while (fragments.Count > 0 || writeOptionalHeaders)
            {
                ServerPacket packet = new ServerPacket();
                PacketHeader packetHeader = packet.Header;

                if (fragments.Count > 0)
                    packetHeader.Flags |= PacketHeaderFlags.BlobFragments;

                if (bundle.EncryptedChecksum)
                    packetHeader.Flags |= PacketHeaderFlags.EncryptedChecksum;

                int availableSpace = ServerPacket.MaxPacketSize;

                // Pull first message and see if it is a large one
                var firstMessage = fragments.FirstOrDefault();
                if (firstMessage != null)
                {
                    // If a large message send only this one, filling the whole packet
                    if (firstMessage.DataRemaining >= availableSpace)
                    {
                        packetLog.DebugFormat("[{0}] Sending large fragment", session.LoggingIdentifier);
                        ServerPacketFragment spf = firstMessage.GetNextFragment();
                        if (NetCapture.AnyFlagged)
                            CaptureFragment(firstMessage, spf);
                        packet.Fragments.Add(spf);
                        TagFinalPiece(firstMessage, spf);
                        availableSpace -= spf.Length;
                        if (firstMessage.DataRemaining <= 0)
                            fragments.Remove(firstMessage);
                    }
                    // Otherwise we'll write any optional headers and process any small messages that will fit
                    else
                    {
                        if (writeOptionalHeaders)
                        {
                            writeOptionalHeaders = false;
                            WriteOptionalHeaders(bundle, packet);
                            if (packet.Data != null)
                                availableSpace -= (int)packet.Data.Length;
                        }

                        // Create a list to remove completed messages after iterator
                        List<MessageFragment> removeList = new List<MessageFragment>();

                        foreach (MessageFragment fragment in fragments)
                        {
                            bool fragmentSkipped = false;

                            // Is this a large fragment and does it have a tail that needs sending?
                            if (!fragment.TailSent && availableSpace >= fragment.TailSize)
                            {
                                packetLog.DebugFormat("[{0}] Sending tail fragment", session.LoggingIdentifier);
                                ServerPacketFragment spf = fragment.GetTailFragment();
                                if (NetCapture.AnyFlagged)
                                    CaptureFragment(fragment, spf);
                                packet.Fragments.Add(spf);
                                TagFinalPiece(fragment, spf);
                                availableSpace -= spf.Length;
                            }
                            // Otherwise will this message fit in the remaining space?
                            else if (availableSpace >= fragment.NextSize)
                            {
                                packetLog.DebugFormat("[{0}] Sending small message", session.LoggingIdentifier);
                                ServerPacketFragment spf = fragment.GetNextFragment();
                                if (NetCapture.AnyFlagged)
                                    CaptureFragment(fragment, spf);
                                packet.Fragments.Add(spf);
                                TagFinalPiece(fragment, spf);
                                availableSpace -= spf.Length;
                            }
                            else
                                fragmentSkipped = true;

                            // If message is out of data, set to remove it
                            if (fragment.DataRemaining <= 0)
                                removeList.Add(fragment);

                            // UIQueue messages must go out in order. Otherwise, you might see an NPC's tells in an order that doesn't match their defined emotes.
                            if (fragmentSkipped && group == GameMessageGroup.UIQueue)
                                break;
                        }

                        // Remove all completed messages
                        fragments.RemoveAll(x => removeList.Contains(x));
                    }
                }
                // If no messages, write optional headers
                else
                {
                    packetLog.DebugFormat("[{0}] No messages, just sending optional headers", session.LoggingIdentifier);
                    if (writeOptionalHeaders)
                    {
                        writeOptionalHeaders = false;
                        WriteOptionalHeaders(bundle, packet);
                        if (packet.Data != null)
                            availableSpace -= (int)packet.Data.Length;
                    }
                }
                EnqueueSend(packet);
            }
        }

        private void WriteOptionalHeaders(NetworkBundle bundle, ServerPacket packet)
        {
            PacketHeader packetHeader = packet.Header;

            if (bundle.SendAck) // 0x4000
            {
                packetHeader.Flags |= PacketHeaderFlags.AckSequence;
                packetLog.DebugFormat("[{0}] Outgoing AckSeq: {1}", session.LoggingIdentifier, lastReceivedPacketSequence);
                packet.InitializeDataWriter();
                packet.DataWriter.Write(lastReceivedPacketSequence);
            }

            if (bundle.TimeSync) // 0x1000000
            {
                packetHeader.Flags |= PacketHeaderFlags.TimeSync;
                packetLog.DebugFormat("[{0}] Outgoing TimeSync TS: {1}", session.LoggingIdentifier, Timers.PortalYearTicks);
                packet.InitializeDataWriter();
                packet.DataWriter.Write(Timers.PortalYearTicks);
            }

            if (bundle.ClientTime != -1f) // 0x4000000
            {
                packetHeader.Flags |= PacketHeaderFlags.EchoResponse;
                packetLog.DebugFormat("[{0}] Outgoing EchoResponse: {1}", session.LoggingIdentifier, bundle.ClientTime);
                packet.InitializeDataWriter();
                packet.DataWriter.Write(bundle.ClientTime);
                packet.DataWriter.Write((float)Timers.PortalYearTicks - bundle.ClientTime);
            }
        }


        private bool isReleased;

        /// <summary>
        /// This will empty out arrays, collections and dictionaries, and mark the object as released.
        /// Any further work assigned to this object will be ignored.
        /// </summary>
        public void ReleaseResources()
        {
            isReleased = true;

            for (int i = 0; i < currentBundles.Length; i++)
                currentBundles[i] = null;

            outOfOrderPackets.Clear();
            partialFragments.Clear();
            outOfOrderFragments.Clear();

            cachedPackets.Clear();

            packetQueue.Clear();

            // held packets have no sequence or cache entry, so discarding them leaves no protocol state behind
            lock (pacerLock)
            {
                pacer?.Clear();
                Volatile.Write(ref pacerHeldMirror, 0);
                Interlocked.Exchange(ref pacerHeldBytesMirror, 0);
            }

            ConnectionData.CryptoClient.ReleaseResources();
        }
    }
}
