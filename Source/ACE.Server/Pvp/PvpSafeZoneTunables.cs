using System;

using log4net;

using ACE.Server.Managers;
using ACE.Server.Realms;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One resolved snapshot of the pvp_safe_landblocks tunable: the parsed, realm-aware list of landblocks
    /// where PvP harm is refused (Docs/Runbook.md "pvp_safe_landblocks").
    /// </summary>
    public sealed record PvpSafeZoneDials(LandblockRealmList SafeLandblocks);

    /// <summary>
    /// Test seam for pvp_safe_landblocks, following ACE.Server.Pvp.PvpTunables exactly: PropertyManager.Get*
    /// throws in ACE.Server.Tests for any uncached key, so the read here is wrapped and falls back to
    /// <see cref="Defaults"/> - the SAME default the tunable ships with, so a test-environment read and a
    /// fresh, unconfigured shard read agree.
    ///
    /// The list is parsed and cached once per distinct raw value rather than re-parsed on every PvP hit -
    /// CheckPKStatusVsTarget and the DoT tick hook both consult this on every harmful action, so re-splitting
    /// the string per call would be wasted work on the hot path.
    /// </summary>
    public static class PvpSafeZoneTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpSafeZoneTunables));

        public const string SafeLandblocksConfigKey = "pvp_safe_landblocks";

        /// <summary>
        /// The Marketplace, which lives in realm 1's copy of Aerfalle Keep (landblock 0x01F5). Realm-scoped on
        /// purpose: the base-world Aerfalle Keep in realm 0 is an ordinary dungeon, not a safe zone.
        /// </summary>
        public const string DefaultSafeLandblocks = "01F5@1";

        /// <summary>
        /// pvp_safe_landblocks has always excluded ephemeral instances (an arena match instanced onto a listed
        /// landblock is not the Marketplace), for bare entries too.
        /// </summary>
        internal const bool BareMatchesEphemeral = false;

        public static Func<PvpSafeZoneDials> DialSource = ReadFromProperties;

        public static readonly PvpSafeZoneDials Defaults = new PvpSafeZoneDials(
            LandblockRealmList.Parse(DefaultSafeLandblocks, SafeLandblocksConfigKey, BareMatchesEphemeral));

        // The parsed list is cached and keyed off the last-seen raw string, so a hot path (every harmful PvP
        // action runs this) pays only a string comparison and not a Split/hex-parse on every hit. The cache
        // is invalidated automatically the moment the config value actually changes (admin edit, reload).
        private static string cachedRaw;
        private static PvpSafeZoneDials cachedDials;
        private static readonly object cacheLock = new object();

        private static PvpSafeZoneDials ReadFromProperties()
        {
            try
            {
                var raw = PropertyManager.GetString(SafeLandblocksConfigKey, DefaultSafeLandblocks).Item;

                lock (cacheLock)
                {
                    if (cachedDials != null && raw == cachedRaw)
                        return cachedDials;

                    cachedDials = new PvpSafeZoneDials(LandblockRealmList.Parse(raw, SafeLandblocksConfigKey, BareMatchesEphemeral));
                    cachedRaw = raw;

                    return cachedDials;
                }
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_safe_landblocks tunable; falling back to the built-in default", ex);
                return Defaults;
            }
        }
    }
}
