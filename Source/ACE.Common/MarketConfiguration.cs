namespace ACE.Common
{
    /// <summary>
    /// The Market's server-side configuration (Docs/Market/DESIGN.md section 12).
    ///
    /// A top-level section beside MySql, Offline and DDD: an optional module with its own
    /// lifecycle, not a game rule. Enabled is FALSE and SharedKey is EMPTY by default.
    /// </summary>
    public class MarketConfiguration
    {
        /// <summary>Off by default. With this false the module registers nothing and starts no listener.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Where Kestrel binds. Loopback by default so a misconfigured deployment fails closed; the
        /// container overrides it to the compose network, never to the host.
        /// </summary>
        public string ListenUrl { get; set; } = "http://127.0.0.1:5090";

        /// <summary>
        /// The X-Market-Key every request must carry. An empty key makes the host refuse to start
        /// rather than serve unauthenticated. Supply it from the deployment .env, never in git.
        /// </summary>
        public string SharedKey { get; set; } = string.Empty;

        /// <summary>Global concurrent request ceiling. Excess returns 429 immediately and never queues into the world.</summary>
        public int MaxInFlight { get; set; } = 8;

        /// <summary>
        /// Per-account write budget. A bulk vault action costs one server write per row, so the
        /// server-wide in-flight gate of 8 concurrent requests (<see cref="MaxInFlight"/>) is the
        /// protection for the server itself; this bucket only rations a single account.
        /// </summary>
        public int WriteRatePerMinute { get; set; } = 120;

        /// <summary>
        /// Hard per-request budget. On expiry the client gets 503 and the enqueued work still
        /// completes or rolls back on its own; it is not cancelled.
        /// </summary>
        public int RequestTimeoutMs { get; set; } = 3000;

        /// <summary>Login bucket per account name. Counts FAILED attempts too, or it does not blunt a guess.</summary>
        public int LoginRatePerMinutePerAccount { get; set; } = 5;

        /// <summary>Login bucket per source address. Counts FAILED attempts too.</summary>
        public int LoginRatePerMinutePerIp { get; set; } = 30;

        /// <summary>
        /// The ONLY address whose X-Forwarded-For is honoured. Empty ignores the header entirely,
        /// which is the safe default: trusting it from anywhere lets a caller forge the per-IP key.
        /// </summary>
        public string TrustedForwardedForSource { get; set; } = string.Empty;

        /// <summary>How long a web session token stays valid. A restart invalidates every token regardless.</summary>
        public int SessionLifetimeMinutes { get; set; } = 720;
    }
}
