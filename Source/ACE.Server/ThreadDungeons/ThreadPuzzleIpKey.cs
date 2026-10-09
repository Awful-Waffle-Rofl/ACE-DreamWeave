using System.Globalization;
using System.Net;
using System.Net.Sockets;

using log4net;

using ACE.Server.Entity.RewardClaims;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The ONLY source of a Thread puzzle fail-policy key (ThreadPuzzleFailPolicy). Every fail, lockout and
    /// lockout check is keyed through here, so the recording side and the enforcing side cannot disagree about
    /// which characters share a key.
    ///
    /// A key is the connection, not the account: every account behind one address shares it. Two exceptions,
    /// both from the EXISTING household allowlists (IpLimitManager.HouseholdLists - an account named in
    /// ip_limit_exempt_accounts, or an address in Config.js AllowUnlimitedSessionsFromIPAddresses): a household
    /// connection is keyed per ACCOUNT ("acct:&lt;id&gt;"), so one family member's lockout does not bar the rest.
    /// Access level never changes a key, and loopback is an ordinary address here (unlike IpLimitManager.IsExempt,
    /// which exempts both from the IP active-player limit).
    ///
    /// IPv6 is keyed by its /64 prefix ("2001:db8:1:2::/64"): one subscriber line is normally handed a whole /64,
    /// so a per-address key would be trivially rotated.
    /// </summary>
    public static class ThreadPuzzleIpKey
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>The prefix of a per-account household key.</summary>
        public const string AccountPrefix = "acct:";

        /// <summary>The suffix every IPv6 key carries.</summary>
        public const string Ipv6PrefixSuffix = "/64";

        /// <summary>
        /// Pure. NULL when there is no address (nothing is counted, and every check passes), or for a household
        /// connection with no account id. A household connection is "acct:&lt;id&gt;"; anything else is the address
        /// normalised exactly as reward claims normalise it (RewardClaimRules.NormalizeIpKey: IPv4-mapped IPv6
        /// collapses to IPv4, the scope id is dropped), with IPv6 then cut to its /64.
        /// </summary>
        public static string For(IPAddress address, bool householdExempt, uint accountId)
        {
            if (address == null)
                return null;

            if (householdExempt)
                return accountId == 0 ? null : AccountPrefix + accountId.ToString(CultureInfo.InvariantCulture);

            var normalized = RewardClaimRules.NormalizeIpKey(address, false);

            if (normalized == null || !IPAddress.TryParse(normalized, out var parsed))
                return normalized;

            if (parsed.AddressFamily != AddressFamily.InterNetworkV6)
                return normalized;

            var bytes = parsed.GetAddressBytes();

            for (var i = 8; i < bytes.Length; i++)
                bytes[i] = 0;

            return new IPAddress(bytes).ToString() + Ipv6PrefixSuffix;
        }

        /// <summary>
        /// The session adapter. <paramref name="lists"/> are the household allowlists, passed in rather than read
        /// here because this runs on landblock threads (a scored lever pull), where PropertyManager is not read:
        /// callers pass ThreadPuzzlePolicyConfig.Current.Household, refreshed on the world thread.
        /// </summary>
        public static string For(Session session, IpLimitManager.HouseholdLists lists)
        {
            var address = session?.EndPointC2S?.Address;

            if (address == null)
            {
                if (log.IsDebugEnabled)
                    log.Debug($"[PUZZLE_POLICY] no key: {(session == null ? "no session" : "no remote address")} (account {session?.AccountId.ToString(CultureInfo.InvariantCulture) ?? "-"}); not counted, checks pass");

                return null;
            }

            return For(address, IpLimitManager.IsHouseholdExempt(session, lists), session.AccountId);
        }
    }
}
