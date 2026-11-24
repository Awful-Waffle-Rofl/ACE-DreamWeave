using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// One-time launch codes that let a player sign in to the website from the game. IN MEMORY, per
    /// process: a restart voids every outstanding code, which is correct for a 60 second credential.
    /// A code is single use (any redeem attempt that finds it removes it), expires at 60 seconds,
    /// and is never logged. Per account at most <see cref="MaxOutstandingPerAccount"/> are live (a
    /// further issue revokes the oldest) and at most <see cref="MaxIssuesPerMinutePerAccount"/> may be
    /// issued per rolling minute, which caps redemptions per account by issuance.
    /// </summary>
    public sealed class WebLaunchCodeStore
    {
        public const int CodeBytes = 32;
        public const int LifetimeSeconds = 60;
        public const int MaxOutstandingPerAccount = 3;
        public const int MaxIssuesPerMinutePerAccount = 10;

        /// <summary>Set by Program.cs at startup; tests construct their own instance and never set this.</summary>
        public static WebLaunchCodeStore Live { get; set; }

        private sealed class Entry
        {
            public uint AccountId;
            public uint CharacterGuid;
            public DateTime IssuedAt;
        }

        private sealed class AccountState
        {
            // Oldest first.
            public readonly List<string> Outstanding = new List<string>();
            public readonly List<DateTime> IssueTimes = new List<DateTime>();
        }

        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(LifetimeSeconds);

        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> codes = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<uint, AccountState> accounts = new Dictionary<uint, AccountState>();
        private readonly Func<DateTime> utcNow;

        public WebLaunchCodeStore(Func<DateTime> utcNow = null)
        {
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public int Count
        {
            get { lock (gate) return codes.Count; }
        }

        /// <summary>False = refused (account 0, or rate limited); code is then null.</summary>
        public bool TryIssue(uint accountId, uint characterGuid, out string code)
        {
            code = null;

            if (accountId == 0)
                return false;

            lock (gate)
            {
                var now = utcNow();
                Sweep(now);

                if (!accounts.TryGetValue(accountId, out var state))
                    accounts[accountId] = state = new AccountState();

                if (state.IssueTimes.Count >= MaxIssuesPerMinutePerAccount)
                    return false;

                while (state.Outstanding.Count >= MaxOutstandingPerAccount)
                {
                    codes.Remove(state.Outstanding[0]);
                    state.Outstanding.RemoveAt(0);
                }

                var bytes = new byte[CodeBytes];
                using (var rng = RandomNumberGenerator.Create())
                    rng.GetBytes(bytes);

                var token = Convert.ToBase64String(bytes)
                    .Replace('+', '-')
                    .Replace('/', '_')
                    .TrimEnd('=');

                codes[token] = new Entry { AccountId = accountId, CharacterGuid = characterGuid, IssuedAt = now };
                state.Outstanding.Add(token);
                state.IssueTimes.Add(now);

                code = token;
                return true;
            }
        }

        /// <summary>Removes the code on any hit; an expired code is consumed and fails.</summary>
        public bool TryRedeem(string code, out uint accountId, out uint characterGuid)
        {
            accountId = 0;
            characterGuid = 0;

            if (string.IsNullOrEmpty(code))
                return false;

            lock (gate)
            {
                if (!codes.TryGetValue(code, out var entry))
                    return false;

                codes.Remove(code);

                if (accounts.TryGetValue(entry.AccountId, out var state))
                    state.Outstanding.Remove(code);

                if (utcNow() >= entry.IssuedAt + Lifetime)
                    return false;

                accountId = entry.AccountId;
                characterGuid = entry.CharacterGuid;
                return true;
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                codes.Clear();
                accounts.Clear();
            }
        }

        // Caller holds the lock. Bounds memory without a background thread.
        private void Sweep(DateTime now)
        {
            List<string> dead = null;

            foreach (var kvp in codes)
            {
                if (now >= kvp.Value.IssuedAt + Lifetime)
                    (dead ??= new List<string>()).Add(kvp.Key);
            }

            if (dead != null)
            {
                foreach (var key in dead)
                {
                    var entry = codes[key];
                    codes.Remove(key);

                    if (accounts.TryGetValue(entry.AccountId, out var state))
                        state.Outstanding.Remove(key);
                }
            }

            List<uint> idle = null;

            foreach (var kvp in accounts)
            {
                kvp.Value.IssueTimes.RemoveAll(t => now >= t + Lifetime);

                if (kvp.Value.IssueTimes.Count == 0 && kvp.Value.Outstanding.Count == 0)
                    (idle ??= new List<uint>()).Add(kvp.Key);
            }

            if (idle != null)
                foreach (var id in idle)
                    accounts.Remove(id);
        }
    }
}
