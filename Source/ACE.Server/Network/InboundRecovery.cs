using System;

using ACE.Common.Cryptography;
using ACE.Server.Managers;

namespace ACE.Server.Network
{
    /// <summary>
    /// Inbound loss-recovery settings, read from PropertyManager once per NetworkManager.DoSessionWork
    /// (<see cref="Refresh"/>) and held in volatile fields, so the per-packet path never touches PropertyManager.
    /// Values are clamped here.
    /// </summary>
    public static class InboundRecoverySettings
    {
        public const string CryptoLookaheadKey = "network_crypto_lookahead";
        public const string StallSecondsKey = "network_inbound_stall_seconds";

        public const long DefaultCryptoLookahead = CryptoSystem.StandardEffortLevel;
        public const long DefaultStallSeconds = 30;

        public const int MaxStallSeconds = 600;

        private static volatile int stallSeconds = ClampStallSeconds(DefaultStallSeconds);

        /// <summary>
        /// Seconds an inbound gap may go without the in-order sequence advancing (and a lost checksum key stream may
        /// keep failing) before the session is ended. 0 is off.
        /// </summary>
        public static int StallSeconds => stallSeconds;

        /// <summary>0 or less is off (0); otherwise at most 600.</summary>
        public static int ClampStallSeconds(long value) => value <= 0 ? 0 : (int)Math.Min(value, MaxStallSeconds);

        /// <summary>
        /// Re-reads both settings. The look-ahead goes to <see cref="CryptoSystem.DefaultEffortLevel"/>, which clamps
        /// it to 256-16384 and applies it to sessions created from then on. Never throws: a failed read keeps the
        /// compiled default.
        /// </summary>
        public static void Refresh()
        {
            CryptoSystem.DefaultEffortLevel = CryptoSystem.ClampEffortLevel(Read(() => PropertyManager.GetLong(CryptoLookaheadKey, DefaultCryptoLookahead).Item, DefaultCryptoLookahead));
            stallSeconds = ClampStallSeconds(Read(() => PropertyManager.GetLong(StallSecondsKey, DefaultStallSeconds).Item, DefaultStallSeconds));
        }

        private static T Read<T>(Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }

    internal enum InboundGapVerdict
    {
        None,

        /// <summary>Packets are queued behind a missing sequence and the in-order sequence has not moved for the stall time.</summary>
        Stalled,

        /// <summary>More packets are queued out of order than any recoverable gap can produce.</summary>
        QueueOverflow,
    }

    /// <summary>
    /// One recovered gap, returned by <see cref="InboundGapTracker.OnAdvanced"/> when the out-of-order queue drains.
    /// </summary>
    internal readonly struct InboundGapRecovery
    {
        public InboundGapRecovery(uint span, TimeSpan elapsed, int naks, bool log, int suppressed)
        {
            Span = span;
            Elapsed = elapsed;
            Naks = naks;
            Log = log;
            Suppressed = suppressed;
        }

        /// <summary>Highest sequence seen during the gap minus the last in-order sequence when it opened.</summary>
        public uint Span { get; }
        public TimeSpan Elapsed { get; }
        public int Naks { get; }

        /// <summary>True when this recovery should be logged (span above the legacy limit, not rate-limited).</summary>
        public bool Log { get; }

        /// <summary>Loggable recoveries skipped by the rate limit since the last logged one.</summary>
        public int Suppressed { get; }
    }

    /// <summary>
    /// The inbound gap and key-stream bookkeeping for one session, pure so it can be tested without a session. Times
    /// are passed in. Used only from the session's connection-listener receive path (single threaded per session).
    /// </summary>
    internal sealed class InboundGapTracker
    {
        /// <summary>Least time between two logged recoveries on one session.</summary>
        public static readonly TimeSpan RecoveryLogInterval = TimeSpan.FromSeconds(10);

        /// <summary>Encrypted-checksum failures in a row, with no success between, before a lost key stream can end the session.</summary>
        public const int KeyStreamMinFailures = 3;

        public bool InGap { get; private set; }
        public DateTime GapOpenedUtc { get; private set; }
        public uint GapStartSequence { get; private set; }
        public uint GapHighSequence { get; private set; }
        public int GapNaks { get; private set; }
        public DateTime StallSinceUtc { get; private set; }

        public long KeyFailuresInRow { get; private set; }
        public DateTime FirstKeyFailureUtc { get; private set; }

        private DateTime lastRecoveryLogUtc = DateTime.MinValue;
        private int suppressedRecoveries;

        /// <summary>Highest sequence seen during the gap minus the last in-order sequence when it opened.</summary>
        public uint GapSpan => InGap ? GapHighSequence - GapStartSequence : 0;

        /// <summary>
        /// Original reorder-layer rule, with the bound now the session's crypto window: a packet more than
        /// <paramref name="window"/> sequences beyond the one after the next expected is abnormal.
        /// </summary>
        public static bool ExceedsReorderBound(uint receivedSequence, uint lastReceivedSequence, int window)
        {
            var bottom = lastReceivedSequence + 2;
            return receivedSequence < bottom || receivedSequence - bottom > (uint)window;
        }

        /// <summary>Most packets that may sit out of order before the session is ended: twice the crypto window.</summary>
        public static int QueueCap(int window) => window * 2;

        /// <summary>
        /// A packet was queued out of order. <paramref name="queueWasEmpty"/> true (nothing was queued before it) opens
        /// a fresh gap even if the tracker still thinks one is open, so a stale gap can never carry an old stall clock.
        /// </summary>
        public void OnQueued(uint lastReceivedSequence, uint sequence, DateTime now, bool queueWasEmpty)
        {
            if (!InGap || queueWasEmpty)
            {
                InGap = true;
                GapOpenedUtc = now;
                GapStartSequence = lastReceivedSequence;
                GapHighSequence = sequence;
                GapNaks = 0;
                StallSinceUtc = now;
            }
            else if (sequence > GapHighSequence)
                GapHighSequence = sequence;
        }

        /// <summary>A retransmit request was sent for the open gap.</summary>
        public void OnNak()
        {
            if (InGap)
                GapNaks++;
        }

        /// <summary>
        /// The in-order sequence moved. Restarts the stall clock; when the out-of-order queue is now empty the gap is
        /// closed and returned (null when no gap was open or one is still open).
        /// </summary>
        public InboundGapRecovery? OnAdvanced(bool queueEmpty, DateTime now)
        {
            StallSinceUtc = now;

            if (!InGap || !queueEmpty)
                return null;

            var span = GapSpan;
            var elapsed = now - GapOpenedUtc;
            var naks = GapNaks;
            InGap = false;

            if (span <= CryptoSystem.LegacyEffortLevel)
                return new InboundGapRecovery(span, elapsed, naks, false, 0);

            if (now - lastRecoveryLogUtc < RecoveryLogInterval)
            {
                suppressedRecoveries++;
                return new InboundGapRecovery(span, elapsed, naks, false, 0);
            }

            var suppressed = suppressedRecoveries;
            suppressedRecoveries = 0;
            lastRecoveryLogUtc = now;
            return new InboundGapRecovery(span, elapsed, naks, true, suppressed);
        }

        /// <summary>
        /// The out-of-order queue emptied without the in-order sequence moving (a queued ack-only packet was drained;
        /// HandleOrderedPacket does not advance on those). Closes the gap silently, so its stall clock cannot go stale.
        /// </summary>
        public void OnDrainedWithoutAdvance()
        {
            InGap = false;
        }

        /// <summary>
        /// Whether the open gap should end the session. <paramref name="stallLimit"/> of zero or less turns the stall
        /// rule off; the queue cap always applies.
        /// </summary>
        public InboundGapVerdict Check(int queuedCount, int queueCap, DateTime now, TimeSpan stallLimit)
        {
            if (queuedCount > queueCap)
                return InboundGapVerdict.QueueOverflow;

            if (InGap && queuedCount > 0 && stallLimit > TimeSpan.Zero && now - StallSinceUtc >= stallLimit)
                return InboundGapVerdict.Stalled;

            return InboundGapVerdict.None;
        }

        /// <summary>
        /// A failure more than this long after the previous one starts a new run: the rule is meant for a client that
        /// keeps sending and keeps failing, not for a few stray failures separated by silence.
        /// </summary>
        public static readonly TimeSpan KeyFailureRunGap = TimeSpan.FromSeconds(5);

        public DateTime LastKeyFailureUtc { get; private set; }

        /// <summary>An encrypted-checksum packet failed verification.</summary>
        public void OnKeyFailure(DateTime now)
        {
            if (KeyFailuresInRow > 0 && now - LastKeyFailureUtc > KeyFailureRunGap)
                KeyFailuresInRow = 0;

            if (KeyFailuresInRow == 0)
                FirstKeyFailureUtc = now;

            KeyFailuresInRow++;
            LastKeyFailureUtc = now;
        }

        /// <summary>An encrypted-checksum packet verified.</summary>
        public void OnKeyVerified()
        {
            KeyFailuresInRow = 0;
        }

        /// <summary>
        /// True when encrypted packets have failed at least <see cref="KeyStreamMinFailures"/> times in a row over at
        /// least <paramref name="limit"/>, with none verifying between and no silence longer than
        /// <see cref="KeyFailureRunGap"/> between two failures: the client's keys are past the window and
        /// nothing it sends will decode again. A zero or negative limit is off.
        /// </summary>
        public bool KeyStreamLost(DateTime now, TimeSpan limit)
        {
            return limit > TimeSpan.Zero && KeyFailuresInRow >= KeyStreamMinFailures
                && now - LastKeyFailureUtc <= KeyFailureRunGap
                && now - FirstKeyFailureUtc >= limit;
        }
    }
}
