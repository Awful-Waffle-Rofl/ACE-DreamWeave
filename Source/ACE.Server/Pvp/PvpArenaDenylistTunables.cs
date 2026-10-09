using System;
using System.Collections.Generic;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One resolved snapshot of the pvp_arena_denylist tunable: the parsed, normalized set of character ids
    /// refused at queue-join time (Docs/Pvp/DESIGN.md "Joining the queue"). Ported from Doctide
    /// `arenas_blacklist` (Docs/Pvp/DESIGN.md "Tunables" table).
    /// </summary>
    public sealed record PvpArenaDenylistDials(IReadOnlySet<uint> DenylistedCharacterIds);

    /// <summary>
    /// Test seam for pvp_arena_denylist, following <see cref="PvpSafeZoneTunables"/> exactly: PropertyManager.Get*
    /// throws in ACE.Server.Tests for any uncached key, so the read here is wrapped and falls back to
    /// <see cref="Defaults"/> - the SAME default the tunable ships with (empty), so a test-environment read and a
    /// fresh, unconfigured shard read agree.
    ///
    /// The CSV is parsed and cached into a HashSet once per <see cref="DialSource"/> call rather than re-parsed
    /// on every queue-join attempt, the same cache-on-raw-text pattern <see cref="PvpSafeZoneTunables"/> uses.
    /// </summary>
    public static class PvpArenaDenylistTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpArenaDenylistTunables));

        public const string DenylistConfigKey = "pvp_arena_denylist";

        public const string DefaultDenylist = "";

        public static Func<PvpArenaDenylistDials> DialSource = ReadFromProperties;

        public static readonly PvpArenaDenylistDials Defaults = new PvpArenaDenylistDials(ParseCsv(DefaultDenylist));

        private static string cachedRaw;
        private static PvpArenaDenylistDials cachedDials;
        private static readonly object cacheLock = new object();

        private static PvpArenaDenylistDials ReadFromProperties()
        {
            try
            {
                var raw = PropertyManager.GetString(DenylistConfigKey, DefaultDenylist).Item;

                lock (cacheLock)
                {
                    if (cachedDials != null && raw == cachedRaw)
                        return cachedDials;

                    cachedDials = new PvpArenaDenylistDials(ParseCsv(raw));
                    cachedRaw = raw;

                    return cachedDials;
                }
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_arena_denylist tunable; falling back to the built-in default (empty)", ex);
                return Defaults;
            }
        }

        /// <summary>PURE. A CSV of character ids (decimal, whitespace-tolerant, blank entries ignored) into a set.</summary>
        public static IReadOnlySet<uint> ParseCsv(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new HashSet<uint>();

            var ids = new HashSet<uint>();

            foreach (var token in raw.Split(','))
            {
                var trimmed = token.Trim();

                if (trimmed.Length == 0)
                    continue;

                if (uint.TryParse(trimmed, out var id))
                    ids.Add(id);
                else
                    log.Warn($"[PVP] pvp_arena_denylist: could not parse '{trimmed}' as a character id; skipping it");
            }

            return ids;
        }

        /// <summary>TRUE if <paramref name="characterId"/> is on the resolved denylist.</summary>
        public static bool IsDenylisted(uint characterId)
        {
            var dials = DialSource?.Invoke() ?? Defaults;

            return dials.DenylistedCharacterIds.Contains(characterId);
        }
    }
}
