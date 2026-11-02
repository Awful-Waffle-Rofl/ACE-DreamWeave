using System;
using System.Collections.Generic;

namespace ACE.Server.Network
{
    /// <summary>
    /// Tunables for one <see cref="SendPacer{T}"/> pass. Plain values handed in by the caller (NetworkSession reads
    /// them from PropertyManager once per Update), so the pacer itself never touches PropertyManager and can be unit
    /// tested.
    /// </summary>
    public struct SendPacerConfig
    {
        /// <summary>Steady token-bucket refill rate in bytes per second. 0 or less means no rate limit.</summary>
        public long RateBytesPerSecond;

        /// <summary>Bucket capacity in bytes: how much may go out back to back after an idle spell.</summary>
        public long BurstBytes;

        /// <summary>Most sent-but-unacked packets allowed before the queue holds. 0 or less means no cap.</summary>
        public long MaxInflightPackets;

        /// <summary>
        /// Trickle floor: when the in-flight cap has blocked the head of the queue for this many ms with nothing
        /// released, one packet is released anyway, so a client that waits for data before acking can never deadlock.
        /// 0 or less is treated as 1.
        /// </summary>
        public long TrickleMs;
    }

    /// <summary>
    /// Per-session outbound pacer: a strict FIFO of held items released by a token-bucket byte rate and a cap on
    /// in-flight (sent, not yet acked) packets, with a trickle floor so a stalled ack stream still makes progress.
    ///
    /// Pure: no PropertyManager, no wall clock (the clock is injected), no knowledge of ServerPacket. The caller
    /// decides which items bypass the queue (header-only ack/NAK packets), supplies the live in-flight count, and does
    /// the actual send inside the release callback.
    ///
    /// Not thread-safe on its own. NetworkSession serializes every call under one lock.
    /// </summary>
    public sealed class SendPacer<T>
    {
        private readonly Func<long> clockMs;
        private readonly Queue<(T item, int size)> held = new Queue<(T item, int size)>();

        private double tokens;
        private long lastRefillMs;
        private bool refillStarted;

        // Last time the queue made progress: a release, or the first item entering an empty queue. The trickle floor
        // measures from here, so an idle session does not trickle the instant new data arrives.
        private long lastProgressMs;

        public SendPacer(Func<long> clockMs)
        {
            this.clockMs = clockMs ?? throw new ArgumentNullException(nameof(clockMs));
        }

        /// <summary>Items currently held.</summary>
        public int HeldCount => held.Count;

        /// <summary>Bytes currently held.</summary>
        public long HeldBytes { get; private set; }

        /// <summary>Largest HeldCount seen since construction or the last <see cref="TakePeak"/>.</summary>
        public int PeakHeld { get; private set; }

        /// <summary>Packets released by the trickle floor (cap overridden) since construction.</summary>
        public long TrickleReleases { get; private set; }

        /// <summary>Current bucket level in bytes (may be negative after an oversized packet). Exposed for tests.</summary>
        public double Tokens => tokens;

        /// <summary>
        /// Returns <see cref="PeakHeld"/> and restarts it from what is held now. The owner folds each taken value into
        /// its own lock-free peak, so a diagnostic reader never needs the pacer (or its lock) to read or reset one.
        /// </summary>
        public int TakePeak()
        {
            var peak = PeakHeld;
            PeakHeld = held.Count;
            return peak;
        }

        /// <summary>
        /// Offers one item. Returns true when the caller must send it NOW, unqueued: a bypass item (header-only ack or
        /// NAK, which takes no new sequence and must never wait behind data). Returns false when the item was queued.
        /// A bypass item is never held, even when the queue is non-empty.
        /// </summary>
        public bool Offer(T item, int sizeBytes, bool bypass)
        {
            if (bypass)
                return true;

            if (held.Count == 0)
                lastProgressMs = clockMs();

            held.Enqueue((item, Math.Max(0, sizeBytes)));
            HeldBytes += Math.Max(0, sizeBytes);

            if (held.Count > PeakHeld)
                PeakHeld = held.Count;

            return false;
        }

        /// <summary>
        /// Releases held items in FIFO order while the rate and in-flight rules allow, calling <paramref name="send"/>
        /// for each. <paramref name="inflight"/> is re-read before every release, because each release changes it.
        /// Returns the number released.
        /// </summary>
        public int Drain(SendPacerConfig config, Func<long> inflight, Action<T> send)
        {
            var now = clockMs();
            Refill(config, now);

            var released = 0;
            var trickleMs = Math.Max(1, config.TrickleMs);

            while (held.Count > 0)
            {
                var head = held.Peek();

                var trickle = false;
                if (config.MaxInflightPackets > 0 && inflight() >= config.MaxInflightPackets)
                {
                    if (now - lastProgressMs < trickleMs)
                        break;

                    trickle = true;
                }

                if (config.RateBytesPerSecond > 0)
                {
                    // an item larger than the whole bucket would never fit, so it only needs the bucket full (or the
                    // equivalent): it then drives the level negative and the rate is still honoured on average
                    var need = Math.Min(head.size, Math.Max(1, config.BurstBytes));
                    if (tokens < need)
                        break;

                    tokens -= head.size;
                }

                held.Dequeue();
                HeldBytes -= head.size;
                lastProgressMs = now;
                released++;
                if (trickle)
                    TrickleReleases++;

                send(head.item);

                // the floor releases one item per trickle interval, not a run
                if (trickle)
                    break;
            }

            return released;
        }

        /// <summary>Releases everything held, in order, ignoring rate and cap (teardown, or pacing switched off).</summary>
        public int DrainAll(Action<T> send)
        {
            var released = 0;
            while (held.Count > 0)
            {
                var head = held.Dequeue();
                HeldBytes -= head.size;
                released++;
                send(head.item);
            }

            lastProgressMs = clockMs();
            return released;
        }

        /// <summary>Discards everything held without sending it (session teardown).</summary>
        public void Clear()
        {
            held.Clear();
            HeldBytes = 0;
        }

        private void Refill(SendPacerConfig config, long now)
        {
            var burst = Math.Max(1, config.BurstBytes);

            if (!refillStarted)
            {
                refillStarted = true;
                lastRefillMs = now;
                tokens = burst;
                return;
            }

            var elapsed = now - lastRefillMs;
            lastRefillMs = now;

            if (elapsed > 0 && config.RateBytesPerSecond > 0)
                tokens += config.RateBytesPerSecond * (elapsed / 1000.0);

            if (tokens > burst)
                tokens = burst;
        }
    }
}
