using System;
using System.Collections.Generic;
using System.Globalization;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Realms
{
    /// <summary>
    /// realm_portal_recall_allowlist: the portal wcids that are exempt from the realm-routing recall lock
    /// (Portal.IsRecallLockedByRealmRouting). Every realm-routed portal is NoRecall / NoTie / NoSummon by
    /// default; a wcid listed here is exempt ONLY while it is routed by a persistent PortalRealm alone (9022,
    /// no PortalInstancing 9021, no PortalExitInstance 9025). Opt-in on purpose (owner decision 2026-09-26): the
    /// ruling was to keep the Marketplace portals recallable after the Marketplace moved into realm 1, not to
    /// unlock every realm portal - the D3 Held Door (1001109) is exactly the kind of portal the lock exists for.
    ///
    /// Test seam in the PvpSafeZoneTunables shape: PropertyManager.Get* throws in ACE.Server.Tests for an
    /// uncached key, so the read is wrapped and falls back to <see cref="DefaultAllowlist"/>, and the parsed set
    /// is cached per distinct raw value because NoRecall is read on every realm portal use.
    /// </summary>
    public static class RealmPortalRecallTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(RealmPortalRecallTunables));

        public const string AllowlistConfigKey = "realm_portal_recall_allowlist";

        /// <summary>
        /// 23032 (portalmarketplace, the retail Marketplace portal) and 1001007 (the Drift Network's "The
        /// Marketplace of Dereth" return portal).
        /// </summary>
        public const string DefaultAllowlist = "23032,1001007";

        public static Func<IReadOnlySet<uint>> AllowlistSource = ReadFromProperties;

        public static readonly IReadOnlySet<uint> Defaults = Parse(DefaultAllowlist);

        private static string cachedRaw;
        private static IReadOnlySet<uint> cachedSet;
        private static readonly object cacheLock = new object();

        /// <summary>
        /// Lenient comma-separated wcid list, decimal or 0x-prefixed hex: whitespace and empty entries ignored,
        /// a malformed or zero entry skipped with a logged warning, never thrown. Empty means nothing is exempt.
        /// </summary>
        public static IReadOnlySet<uint> Parse(string raw, ICollection<string> rejected = null)
        {
            var result = new HashSet<uint>();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();

                if (trimmed.Length == 0)
                    continue;

                uint wcid;
                var ok = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.TryParse(trimmed.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out wcid)
                    : uint.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out wcid);

                if (ok && wcid != 0)
                {
                    result.Add(wcid);
                }
                else
                {
                    rejected?.Add(token);
                    log.Warn($"RealmPortalRecallTunables: skipping malformed entry '{token}' in '{AllowlistConfigKey}'.");
                }
            }

            return result;
        }

        private static IReadOnlySet<uint> ReadFromProperties()
        {
            try
            {
                var raw = PropertyManager.GetString(AllowlistConfigKey, DefaultAllowlist).Item;

                lock (cacheLock)
                {
                    if (cachedSet != null && raw == cachedRaw)
                        return cachedSet;

                    cachedSet = Parse(raw);
                    cachedRaw = raw;

                    return cachedSet;
                }
            }
            catch (Exception ex)
            {
                log.Error($"could not read {AllowlistConfigKey}; falling back to the built-in default", ex);
                return Defaults;
            }
        }
    }
}
