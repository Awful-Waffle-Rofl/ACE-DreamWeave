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

            /// <summary>
            /// The character whose /weblogin code created this session (POST /v1/session/launch, only when it
            /// answered character_guid); 0 for a password session or a launch that named no character. The class
            /// ability panel writes only from a session bound to the character being changed.
            /// </summary>
            public uint BoundCharacterGuid;
        }

        private readonly ConcurrentDictionary<string, Entry> sessions = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

        private readonly TimeSpan lifetime;

        public MarketSessionStore(int lifetimeMinutes)
        {
            lifetime = TimeSpan.FromMinutes(Math.Max(1, lifetimeMinutes));
        }

        public int Count => sessions.Count;

        public string Issue(uint accountId) => Issue(accountId, 0);

        /// <summary>
        /// Issues a token bound to <paramref name="boundCharacterGuid"/> (0 = unbound). Only POST
        /// /v1/session/launch passes a nonzero value, and only the character it answers as character_guid.
        /// </summary>
        public string Issue(uint accountId, uint boundCharacterGuid)
        {
            var bytes = new byte[32];

            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var token = Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

            sessions[token] = new Entry { AccountId = accountId, ExpiresUtc = DateTime.UtcNow.Add(lifetime), BoundCharacterGuid = boundCharacterGuid };

            return token;
        }

        /// <summary>
        /// Resolves a token to its account. An expired token is REMOVED here rather than left for
        /// the sweep, so it stops working the moment it is first presented after expiry.
        /// </summary>
        public bool TryResolve(string token, out uint accountId) => TryResolve(token, out accountId, out _);

        /// <summary>As <see cref="TryResolve(string, out uint)"/>, plus the launch-bound character (0 when unbound).</summary>
        public bool TryResolve(string token, out uint accountId, out uint boundCharacterGuid)
        {
            accountId = 0;
            boundCharacterGuid = 0;

            if (string.IsNullOrEmpty(token) || !sessions.TryGetValue(token, out var entry))
                return false;

            if (entry.ExpiresUtc <= DateTime.UtcNow)
            {
                sessions.TryRemove(token, out _);
                return false;
            }

            accountId = entry.AccountId;
            boundCharacterGuid = entry.BoundCharacterGuid;
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
