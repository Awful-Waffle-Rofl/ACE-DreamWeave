using System;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One resolved snapshot of every pvp_conduct_* setting (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): the anti-idle, contribution
    /// floor, feeding and deserter dials. The coordinator reads the engage radius (sampler), the idle dials (idle warning and removal),
    /// the feeding dials (feeding removal), the floor dials (the payout) and the deserter dials (the deserter lockout, PvpDeserterLadder).
    /// </summary>
    public sealed record PvpConductDials(
        // pvp_conduct_idle_enabled - idle enforcement on/off (read by the coordinator's idle enforcement).
        bool IdleEnabled,
        // pvp_conduct_idle_warn_seconds - idle seconds before the one warning.
        long IdleWarnSeconds,
        // pvp_conduct_idle_remove_seconds - idle seconds before removal (ForfeitInactive); always above the warning.
        long IdleRemoveSeconds,
        // pvp_conduct_engage_radius - metres to a living enemy that count a sampled second as engaged (read now, by the sampler).
        double EngageRadius,
        // pvp_conduct_floor_enabled - contribution floor on pay on/off (read now, by the payout).
        bool FloorEnabled,
        // pvp_conduct_floor_share - the contribution share below which pay is withheld (read now).
        double FloorShare,
        // pvp_conduct_floor_min_engaged_seconds - engaged seconds under which, with no damage and no healing, a seat is below the floor (read now).
        long FloorMinEngagedSeconds,
        // pvp_conduct_feed_enabled - feeding removal on/off (battlegrounds; read at each death).
        bool FeedEnabled,
        // pvp_conduct_feed_min_deaths - deaths before feeding is considered.
        long FeedMinDeaths,
        // pvp_conduct_feed_max_share - the contribution share BELOW which a seat at the death count is removed (ForfeitFeeding).
        double FeedMaxShare,
        // pvp_conduct_deserter_enabled - deserter lockouts on/off (battlegrounds and Tugak Brawl).
        bool DeserterEnabled,
        // pvp_conduct_deserter_lockout_1_minutes .. _3_minutes - the lockout length by strike count 1 / 2 / 3.
        long DeserterLockout1Minutes,
        long DeserterLockout2Minutes,
        long DeserterLockout3Minutes,
        // pvp_conduct_deserter_decay_days - one strike decays per full period of this many days since the last strike.
        long DeserterDecayDays);

    /// <summary>
    /// Test seam for the pvp_conduct_* settings, mirroring <see cref="Battlegrounds.BattlegroundTunables"/>: PropertyManager.Get* throws in
    /// ACE.Server.Tests for any uncached key, so every read is wrapped and falls back to <see cref="Defaults"/>, the SAME defaults the
    /// PropertyManager entries ship with. The coordinator takes its own seam (an optional constructor argument) that defaults to
    /// <see cref="DialSource"/>.
    /// </summary>
    public static class PvpConductTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpConductTunables));

        public static Func<PvpConductDials> DialSource = ReadFromProperties;

        public static readonly PvpConductDials Defaults = new PvpConductDials(
            IdleEnabled: true,
            IdleWarnSeconds: 60,
            IdleRemoveSeconds: 90,
            EngageRadius: 30.0,
            FloorEnabled: true,
            FloorShare: 0.25,
            FloorMinEngagedSeconds: 30,
            FeedEnabled: true,
            FeedMinDeaths: 4,
            FeedMaxShare: 0.10,
            DeserterEnabled: true,
            DeserterLockout1Minutes: 15,
            DeserterLockout2Minutes: 60,
            DeserterLockout3Minutes: 1440,
            DeserterDecayDays: 7);

        private static PvpConductDials ReadFromProperties()
        {
            try
            {
                var d = Defaults;

                return Clamp(new PvpConductDials(
                    IdleEnabled: PropertyManager.GetBool("pvp_conduct_idle_enabled", d.IdleEnabled).Item,
                    IdleWarnSeconds: PropertyManager.GetLong("pvp_conduct_idle_warn_seconds", d.IdleWarnSeconds).Item,
                    IdleRemoveSeconds: PropertyManager.GetLong("pvp_conduct_idle_remove_seconds", d.IdleRemoveSeconds).Item,
                    EngageRadius: PropertyManager.GetDouble("pvp_conduct_engage_radius", d.EngageRadius).Item,
                    FloorEnabled: PropertyManager.GetBool("pvp_conduct_floor_enabled", d.FloorEnabled).Item,
                    FloorShare: PropertyManager.GetDouble("pvp_conduct_floor_share", d.FloorShare).Item,
                    FloorMinEngagedSeconds: PropertyManager.GetLong("pvp_conduct_floor_min_engaged_seconds", d.FloorMinEngagedSeconds).Item,
                    FeedEnabled: PropertyManager.GetBool("pvp_conduct_feed_enabled", d.FeedEnabled).Item,
                    FeedMinDeaths: PropertyManager.GetLong("pvp_conduct_feed_min_deaths", d.FeedMinDeaths).Item,
                    FeedMaxShare: PropertyManager.GetDouble("pvp_conduct_feed_max_share", d.FeedMaxShare).Item,
                    DeserterEnabled: PropertyManager.GetBool("pvp_conduct_deserter_enabled", d.DeserterEnabled).Item,
                    DeserterLockout1Minutes: PropertyManager.GetLong("pvp_conduct_deserter_lockout_1_minutes", d.DeserterLockout1Minutes).Item,
                    DeserterLockout2Minutes: PropertyManager.GetLong("pvp_conduct_deserter_lockout_2_minutes", d.DeserterLockout2Minutes).Item,
                    DeserterLockout3Minutes: PropertyManager.GetLong("pvp_conduct_deserter_lockout_3_minutes", d.DeserterLockout3Minutes).Item,
                    DeserterDecayDays: PropertyManager.GetLong("pvp_conduct_deserter_decay_days", d.DeserterDecayDays).Item));
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_conduct_* tunables; falling back to built-in defaults", ex);
                return Defaults;
            }
        }

        /// <summary>
        /// Input clamps, so a misconfigured value cannot invert a rule: the idle warning at least 1 second and the removal always at least one
        /// second after it; the engage radius 0 (nobody counts by proximity) when NaN, infinite or negative; both shares in 0..1 (NaN = 0); every
        /// count, length and decay at least 0, a deserter lockout at most MaxDeserterLockoutMinutes and the decay at most MaxDeserterDecayDays. The switches are returned as given. Pure.
        /// </summary>
        public static PvpConductDials Clamp(PvpConductDials d)
        {
            if (d == null)
                return null;

            var warn = Math.Max(1L, d.IdleWarnSeconds);

            return d with
            {
                IdleWarnSeconds = warn,
                IdleRemoveSeconds = Math.Max(warn + 1, d.IdleRemoveSeconds),
                EngageRadius = Finite(d.EngageRadius) && d.EngageRadius > 0.0 ? d.EngageRadius : 0.0,
                FloorShare = Share(d.FloorShare),
                FloorMinEngagedSeconds = Math.Max(0L, d.FloorMinEngagedSeconds),
                FeedMinDeaths = Math.Max(0L, d.FeedMinDeaths),
                FeedMaxShare = Share(d.FeedMaxShare),
                DeserterLockout1Minutes = Math.Clamp(d.DeserterLockout1Minutes, 0L, MaxDeserterLockoutMinutes),
                DeserterLockout2Minutes = Math.Clamp(d.DeserterLockout2Minutes, 0L, MaxDeserterLockoutMinutes),
                DeserterLockout3Minutes = Math.Clamp(d.DeserterLockout3Minutes, 0L, MaxDeserterLockoutMinutes),
                DeserterDecayDays = Math.Clamp(d.DeserterDecayDays, 0L, MaxDeserterDecayDays),
            };
        }

        /// <summary>The longest deserter lockout a dial may ask for: one year, in minutes.</summary>
        public const long MaxDeserterLockoutMinutes = 525600;

        /// <summary>The longest deserter decay period: 100 years, in days (keeps decay_days x 86400 far from overflow).</summary>
        public const long MaxDeserterDecayDays = 36500;

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private static double Share(double v) => Finite(v) ? Math.Clamp(v, 0.0, 1.0) : 0.0;
    }
}
