using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using log4net;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>
    /// The live side of the Double Marks windows: turns the pvp_bg_bonus_* dials into a parsed schedule and a resolved zone,
    /// caching each by its raw text so the per-tick Crier and the payout do not re-parse, and logging a malformed entry or an
    /// unresolvable zone once per distinct string. The arithmetic itself is the pure <see cref="BgBonusWindow"/>.
    /// </summary>
    public static class BgBonusConfig
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(BgBonusConfig));

        /// <summary>The fixed offset used when the host cannot resolve pvp_bg_bonus_timezone (Pacific standard time, no DST).</summary>
        private static readonly TimeZoneInfo FallbackZone = TimeZoneInfo.CreateCustomTimeZone("BgBonusFallbackUtcMinus8", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

        private static readonly ConcurrentDictionary<string, IReadOnlyList<BgBonusSpan>> SpanCache = new();
        private static readonly ConcurrentDictionary<string, TimeZoneInfo> ZoneCache = new();
        private static readonly ConcurrentDictionary<string, bool> Warned = new();

        /// <summary>The schedule and zone for <paramref name="bg"/>'s bonus dials. Never null, never throws.</summary>
        public static (IReadOnlyList<BgBonusSpan> Spans, TimeZoneInfo Zone) Resolve(BattlegroundDials bg)
        {
            return (ParseCached(bg?.BonusWindows), ResolveZone(bg?.BonusTimezone));
        }

        /// <summary>Whether a battleground match that went Live at <paramref name="liveSinceUtc"/> is inside a Double Marks window under <paramref name="bg"/>.</summary>
        public static bool IsBonusMatch(BattlegroundDials bg, DateTime liveSinceUtc)
        {
            if (bg == null || !bg.BonusEnabled)
                return false;

            try
            {
                var (spans, zone) = Resolve(bg);
                return BgBonusWindow.IsActive(liveSinceUtc, spans, zone);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] evaluating the Double Marks window threw; the match pays the ordinary Marks", ex);
                return false;
            }
        }

        private static IReadOnlyList<BgBonusSpan> ParseCached(string spec)
        {
            spec ??= string.Empty;

            return SpanCache.GetOrAdd(spec, text =>
            {
                var malformed = new List<string>();
                var spans = BgBonusWindow.Parse(text, malformed);

                foreach (var entry in malformed)
                    if (Warned.TryAdd("window:" + entry, true))
                        log.Warn($"[PVP] pvp_bg_bonus_windows entry '{entry}' is not HH:mm-HH:mm (24 hour), and the start and end must differ; skipped");

                return spans;
            });
        }

        private static TimeZoneInfo ResolveZone(string zoneId)
        {
            return ZoneCache.GetOrAdd(zoneId ?? string.Empty, id =>
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
                {
                    if (Warned.TryAdd("zone:" + id, true))
                        log.Warn($"[PVP] pvp_bg_bonus_timezone '{id}' could not be resolved (ids are case-sensitive IANA ids, e.g. America/Los_Angeles); the Double Marks windows use a fixed UTC-8 offset, which is an hour off during daylight saving", ex);

                    return FallbackZone;
                }
            });
        }
    }
}
