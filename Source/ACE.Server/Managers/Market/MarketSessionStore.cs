using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Bearer session tokens for the market API (DESIGN section 6). IN MEMORY, per process, on
    /// purpose: a restart logs everyone out, correct for a session that authorizes spending. The token carries NO account id - one that did would let a holder enumerate.
    /// </summary>
    public sealed class MarketSessionStore
    {
        private sealed class Entry
        {
            public uint AccountId;
            public DateTime ExpiresUtc;
        }

        private readonly ConcurrentDictionary<string, Entry> sessions = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private readonly TimeSpan lifetime;

        public MarketSessionStore(int lifetimeMinutes)
        {
            lifetime = TimeSpan.FromMinutes(Math.Max(1, lifetimeMinutes));
        }

        public int Count => sessions.Count;

        public string Issue(uint accountId)
        {
            var bytes = new byte[32];

            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var token = Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            sessions[token] = new Entry { AccountId = accountId, ExpiresUtc = DateTime.UtcNow.Add(lifetime) };

            return token;
        }

        /// <summary>
        /// Resolves a token to its account. An expired token is REMOVED here rather than left for
        /// the sweep, so it stops working the moment it is first presented after expiry.
        /// </summary>
        public bool TryResolve(string token, out uint accountId)
        {
            accountId = 0;

            if (string.IsNullOrEmpty(token) || !sessions.TryGetValue(token, out var entry))
                return false;

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                sessions.TryRemove(token, out _);
                return false;
            }

            accountId = entry.AccountId;
            return true;
        }

        /// <summary>Drops expired entries. Bounds memory on a long-running process; not a security control.</summary>
        public void Sweep()
        {
            var now = DateTime.UtcNow;

            foreach (var kvp in sessions)
            {
                if (kvp.Value.ExpiresUtc <= now)
                    sessions.TryRemove(kvp.Key, out _);
            }
        }

        public void Clear() => sessions.Clear();
    }
}
