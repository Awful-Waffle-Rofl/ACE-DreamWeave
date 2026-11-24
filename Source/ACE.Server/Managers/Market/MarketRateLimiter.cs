using System;
using System.Collections.Concurrent;
using System.Threading;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The server-side limits from DESIGN section 7, independent of anything the web app does. The
    /// IN-FLIGHT GATE refuses IMMEDIATELY rather than queuing (a queue in front of the world is the
    /// failure it prevents); the WRITE BUCKET rations one account; the LOGIN BUCKETS blunt guessing per account AND source address, counting failures too, refilling continuously so no burst boundary exists.
    /// </summary>
    public sealed class MarketRateLimiter
    {
        private sealed class Bucket
        {
            public double Tokens;
            public DateTime LastRefillUtc;
        }

        private readonly int maxInFlight;
        private readonly double writePerMinute;
        private readonly double writeBurst;
        private readonly double loginPerMinutePerAccount;
        private readonly double loginPerMinutePerIp;

        private int inFlight;

        private readonly ConcurrentDictionary<uint, Bucket> writeBuckets = new ConcurrentDictionary<uint, Bucket>();
        private readonly ConcurrentDictionary<string, Bucket> loginAccountBuckets = new ConcurrentDictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Bucket> loginIpBuckets = new ConcurrentDictionary<string, Bucket>(StringComparer.Ordinal);

        /// <param name="writeBurst">
        /// The write bucket's capacity, when it should differ from its per-minute refill; null (every caller but the
        /// class ability panel) keeps capacity equal to the refill, as before.
        /// </param>
        public MarketRateLimiter(int maxInFlight, int writeRatePerMinute, int loginRatePerMinutePerAccount, int loginRatePerMinutePerIp,
            int? writeBurst = null)
        {
            this.maxInFlight = Math.Max(1, maxInFlight);
            writePerMinute = Math.Max(1, writeRatePerMinute);
            this.writeBurst = Math.Max(1, writeBurst ?? writeRatePerMinute);
            loginPerMinutePerAccount = Math.Max(1, loginRatePerMinutePerAccount);
            loginPerMinutePerIp = Math.Max(1, loginRatePerMinutePerIp);
        }

        public int InFlight => Volatile.Read(ref inFlight);

        /// <summary>
        /// Reserves one in-flight slot, or refuses. Every caller that gets true MUST call
        /// <see cref="ExitInFlight"/> in a finally, or the gate leaks slots and refuses everything.
        /// </summary>
        public bool TryEnterInFlight()
        {
            while (true)
            {
                var current = Volatile.Read(ref inFlight);

                if (current >= maxInFlight)
                    return false;

                if (Interlocked.CompareExchange(ref inFlight, current + 1, current) == current)
                    return true;
            }
        }

        public void ExitInFlight()
        {
            while (true)
            {
                var current = Volatile.Read(ref inFlight);

                if (current <= 0)
                    return;

                if (Interlocked.CompareExchange(ref inFlight, current - 1, current) == current)
                    return;
            }
        }

        public bool TryWrite(uint accountId) => TryTake(writeBuckets, accountId, writePerMinute, writeBurst);

        public bool TryLoginAccount(string accountName)
            => TryTake(loginAccountBuckets, accountName ?? string.Empty, loginPerMinutePerAccount, loginPerMinutePerAccount);

        public bool TryLoginIp(string ip) => TryTake(loginIpBuckets, ip ?? string.Empty, loginPerMinutePerIp, loginPerMinutePerIp);

        public void Reset()
        {
            writeBuckets.Clear();
            loginAccountBuckets.Clear();
            loginIpBuckets.Clear();
            Volatile.Write(ref inFlight, 0);
        }

        private static bool TryTake<TKey>(ConcurrentDictionary<TKey, Bucket> buckets, TKey key, double perMinute, double capacity)
        {
            var bucket = buckets.GetOrAdd(key, _ => new Bucket { Tokens = capacity, LastRefillUtc = DateTime.UtcNow });

            lock (bucket)
            {
                var now = DateTime.UtcNow;
                var elapsedMinutes = (now - bucket.LastRefillUtc).TotalMinutes;

                if (elapsedMinutes > 0)
                {
                    bucket.Tokens = Math.Min(capacity, bucket.Tokens + elapsedMinutes * perMinute);
                    bucket.LastRefillUtc = now;
                }

                if (bucket.Tokens < 1.0)
                    return false;

                bucket.Tokens -= 1.0;
                return true;
            }
        }
    }
}
