using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Harbinger
{
    /// <summary>
    /// Per-copy tuning for /pinnacle enter: "key=value" tokens named after the <see cref="HarbingerSettings"/> fields (snake_case)
    /// laid over a base settings record for ONE copy (the MatronOverrides shape). Pure: no PropertyManager, no world. Unknown keys,
    /// unparsable values and malformed tokens are refused with a line naming the offender. <see cref="TryCreateScript"/> builds the boss script on the result.
    /// </summary>
    public static class HarbingerOverrides
    {
        private static readonly (string Key, double Min, double Max, Func<HarbingerSettings, double, HarbingerSettings> Set)[] Table =
        {
            ("acid_rate", 0.001, 2, (s, v) => s with { AcidRate = v }),
            ("acid_rate_cap", 0.001, 2, (s, v) => s with { AcidRateCap = v }),
            ("acid_escalate_seconds", 5, 600, (s, v) => s with { AcidEscalateSeconds = v }),
            ("acid_max_z", AcidMinZ, AcidCeilingZ, (s, v) => s with { AcidMaxZ = v }),
            ("acid_damage_scale", 0.01, 10, (s, v) => s with { AcidDamageScale = v }),
            ("wisp_pct", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { WispPct = v }),
            ("hail_pct", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { HailPct = v }),
            ("hail_max_falling", 1, 64, (s, v) => s with { HailMaxFalling = (int)v }),
            ("hail_run_slow", 0, 0.9, (s, v) => s with { HailRunSlow = v }),
            ("hail_attack_factor", 0.1, 1, (s, v) => s with { HailAttackFactor = v }),
            ("hail_slow_seconds", 0, 60, (s, v) => s with { HailSlowSeconds = v }),
            ("fire_pct", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { FirePct = v }),
            ("final_scale", 0, 1, (s, v) => s with { FinalScale = v }),
            ("summon_seconds", 1, 600, (s, v) => s with { SummonSeconds = v }),
            ("summon_cap", 0, 16, (s, v) => s with { SummonCap = (int)v }),
            ("final_summon_cap", 0, 16, (s, v) => s with { FinalSummonCap = (int)v }),
            ("cast_seconds", 1, 120, (s, v) => s with { CastSeconds = v }),
            ("melee_reach", 1, 40, (s, v) => s with { MeleeReach = v }),
            ("hazard_pct_min", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { HazardPctMin = v }),
            ("hazard_pct_max", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { HazardPctMax = v }),
            ("wall_fx", Matron.MatronLayout.WallFxPanels, Matron.MatronLayout.WallFxBoth, (s, v) => s with { WallFx = (int)v }),
            ("fog_acid", 0, Matron.MatronLayout.AcidFogs.Count - 1, (s, v) => s with { FogAcid = (int)v }),
            ("health_multiplier", PinnacleTunables.MinHarbingerHealthMultiplier, PinnacleTunables.MaxHarbingerHealthMultiplier, (s, v) => s with { HealthMultiplier = v }),
            ("head_health_multiplier", PinnacleTunables.MinHarbingerHeadHealthMultiplier, PinnacleTunables.MaxHarbingerHeadHealthMultiplier, (s, v) => s with { HeadHealthMultiplier = v }),
            ("crystal_cast_seconds", 1, 120, (s, v) => s with { CrystalCastSeconds = v }),
            ("attune_seconds", 0, 600, (s, v) => s with { AttuneSeconds = v }),
            ("pressure_seconds", 1, 600, (s, v) => s with { PressureSeconds = v }),
            ("alcove_grace_seconds", 0, 60, (s, v) => s with { AlcoveGraceSeconds = v }),
            ("pool_size", HarbingerLayout.MinPoolSize, 3, (s, v) => s with { PoolSize = v }),
            ("pool_cap", 0, PoolSlotCount, (s, v) => s with { PoolCap = (int)v }),
            ("drip_seconds", 1, 120, (s, v) => s with { DripSeconds = v }),
            ("drip_pressure_seconds", 1, 120, (s, v) => s with { DripPressureSeconds = v }),
            ("drip_pct", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { DripPct = v }),
            ("pool_step", 0.05, 0.5, (s, v) => s with { PoolStep = v }),
            // 0.65 = HarbingerSettings.PoolMaxZ (0.7, the pool fill's max_z) less 0.05, written as a literal (0.7 - 0.05 is not 0.65 in a double)
            ("pool_max_depth", HarbingerSettings.PoolSurfaceZ, 0.65, (s, v) => s with { PoolMaxDepth = v }),
            ("pool_damage", 0, 1000, (s, v) => s with { PoolDamage = v }),
            ("pool_damage_per_m", 0, 5000, (s, v) => s with { PoolDamagePerM = v }),
            ("film_damage", 0, 1000, (s, v) => s with { FilmDamage = v }),
            ("twin_revive_seconds", 4, 120, (s, v) => s with { TwinReviveSeconds = v }),
            ("conductor_wisp_pct", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { ConductorWispPct = v }),
            ("conductor_wisps", 1, 3, (s, v) => s with { ConductorWisps = (int)v }),
            ("conductor_pressure_wisps", 1, 3, (s, v) => s with { ConductorPressureWisps = (int)v }),
            ("whiteout_pct", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { WhiteoutPct = v }),
            ("whiteout_max_falling", 1, 16, (s, v) => s with { WhiteoutMaxFalling = (int)v }),
            ("whiteout_pressure_max_falling", 1, 16, (s, v) => s with { WhiteoutPressureMaxFalling = (int)v }),
            ("whiteout_speed", 2, 40, (s, v) => s with { WhiteoutSpeed = v }),
            ("whiteout_pressure_speed", 2, 40, (s, v) => s with { WhiteoutPressureSpeed = v }),
            ("whiteout_burst_radius", 1.1, 3, (s, v) => s with { WhiteoutBurstRadius = v }),
            ("ember_pct_min", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { EmberPctMin = v }),
            ("ember_pct_max", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { EmberPctMax = v }),
            ("ember_count", 1, 3, (s, v) => s with { EmberCount = (int)v }),
            ("ember_pressure_count", 1, 3, (s, v) => s with { EmberPressureCount = (int)v }),
            ("ember_every_seconds", 2, 120, (s, v) => s with { EmberEverySeconds = v }),
            ("ember_pressure_every_seconds", 2, 120, (s, v) => s with { EmberPressureEverySeconds = v }),
            ("ember_speed", 1, 12, (s, v) => s with { EmberSpeed = v }),
            ("ember_pressure_speed", 1, 12, (s, v) => s with { EmberPressureSpeed = v }),
            ("ember_ball_radius", 0.3, 0.5, (s, v) => s with { EmberBallRadius = v }),
        };

        /// <summary>Every key with its accepted range (test access).</summary>
        internal static IReadOnlyList<(string Key, double Min, double Max)> Bounds { get; } = Table.Select(t => (t.Key, t.Min, t.Max)).ToArray();

        /// <summary>True for a key that takes a whole number only (test access).</summary>
        internal static bool IsWholeNumberKey(string key) => WholeNumberKeys.Contains(key);

        /// <summary>The keys that take a whole number only.</summary>
        private static readonly HashSet<string> WholeNumberKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "hail_max_falling", "summon_cap", "final_summon_cap", "wall_fx", "fog_acid", "pool_cap", "whiteout_max_falling", "whiteout_pressure_max_falling", "conductor_wisps", "conductor_pressure_wisps", "ember_count", "ember_pressure_count" };

        /// <summary>The pool_cap ceiling: one pool per slot (HarbingerLayout.PoolSlotsUV).</summary>
        public const int PoolSlotCount = 18;

        /// <summary>The acid fill's ceiling must stay inside the chamber: above the fill floor (-35.95), at or under the -12.0 the region validates.</summary>
        public const double AcidMinZ = -35.0, AcidCeilingZ = -12.0;

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Every override key, in settings-field order.</summary>
        public static IReadOnlyList<string> Keys { get; } = Table.Select(t => t.Key).ToArray();

        /// <summary>
        /// <paramref name="overrides"/> laid over <paramref name="baseSettings"/>. False with an <paramref name="error"/>
        /// naming the first unknown key, unparsable or out-of-range value, or broken cross-field rule; <paramref name="result"/> is then the base, unchanged.
        /// </summary>
        public static bool TryApply(HarbingerSettings baseSettings, HazardArgs overrides, out HarbingerSettings result, out string error)
        {
            result = baseSettings;
            error = null;

            if (overrides == null || overrides.Count == 0)
                return true;

            var current = baseSettings;

            foreach (var key in overrides.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                var row = Table.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

                if (row.Key == null)
                {
                    error = $"unknown override key '{key}'";
                    return false;
                }

                var raw = overrides.GetString(key);

                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                {
                    error = $"{row.Key}='{raw}' is not a number";
                    return false;
                }

                if (WholeNumberKeys.Contains(row.Key) && value != Math.Floor(value))
                {
                    error = $"{row.Key}={Num(value)} is not a whole number";
                    return false;
                }

                if (value < row.Min || value > row.Max)
                {
                    error = $"{row.Key}={Num(value)} is out of range ({Num(row.Min)} to {Num(row.Max)})";
                    return false;
                }

                current = row.Set(current, value);
            }

            // cross-field rules, over the merged result
            if (current.AcidRateCap < current.AcidRate)
                error = $"acid_rate_cap ({Num(current.AcidRateCap)}) must be at least acid_rate ({Num(current.AcidRate)})";
            else if (current.HazardPctMin > current.HazardPctMax)
                error = $"hazard_pct_min ({Num(current.HazardPctMin)}) must not exceed hazard_pct_max ({Num(current.HazardPctMax)})";
            else if (current.EmberPctMin > current.EmberPctMax)
                error = $"ember_pct_min ({Num(current.EmberPctMin)}) must not exceed ember_pct_max ({Num(current.EmberPctMax)})";

            // the signature runs the merged settings would build, through the mechanics' own pre-flights (review F1 on
            // 19d358426): a per-key bound cannot see a combination, and a run refused at its start is retried, then
            // dropped from wanted, so the signature would silently vanish mid-fight
            if (error == null)
                error = SignatureProblem(current);

            if (error != null)
                return false;

            result = current;
            return true;
        }

        /// <summary>
        /// The first refusal any alcove signature run of <paramref name="settings"/> would meet at its start, or null: Ember
        /// roll (both pressure states) through BallRules.Validate on the fire alcove's preset, the conductor wisps (both
        /// states) through WispRules.Validate on the lightning alcove's preset over the wisps' defaults
        /// (<see cref="HazardDefaults.Wisps"/>, as the live mechanic lays them under the args), and every drip and pool area
        /// of the acid alcove through RainRules.Validate and FillRules.Validate (a pool fresh and at its deepest). The pcts
        /// are the authored ones (they change no pre-flight). Pure.
        /// </summary>
        public static string SignatureProblem(HarbingerSettings settings)
        {
            foreach (var pressured in new[] { false, true })
            {
                var state = pressured ? "pressed" : "unpressed";
                var fire = Preset(HarbingerElement.Fire);
                var ember = BallRules.Validate(fire, settings.EmberArgs(settings.EmberPctMax, pressured), BallPatterns.Random);

                if (ember != null)
                    return $"these overrides make Ember roll ({state}) unstartable: {ember}";

                var lightning = Preset(HarbingerElement.Lightning);
                var wispArgs = HazardArgs.Merge(settings.ConductorArgs(settings.ConductorWispPct, pressured), HazardArgs.Parse(HazardDefaults.Wisps));
                var wisps = WispRules.Validate(lightning, wispArgs, WispRules.Scatter);

                if (wisps != null)
                    return $"these overrides make the conductor wisps ({state}) unstartable: {wisps}";
            }

            var acid = HarbingerData.AlcoveOf(HarbingerElement.Acid);

            for (var slot = 0; slot < HarbingerLayout.PoolSlotsUV.Count; slot++)
            {
                var dripArea = Area(HarbingerLayout.PoolArea(acid, slot, HarbingerSettings.DripAreaSize));
                var drip = RainRules.Validate(dripArea, settings.DripArgs(settings.DripPct), null);

                if (drip != null)
                    return $"these overrides make the acid drip on slot {slot} unstartable: {drip}";

                var poolArea = Area(HarbingerLayout.PoolArea(acid, slot, settings.PoolSize));

                foreach (var surface in new[] { HarbingerSettings.PoolSurfaceZ, HazardRegionPresets.HarbingerAlcoveGroundZ + settings.PoolMaxDepth })
                {
                    var pool = FillRules.Validate(poolArea, settings.PoolArgs(1.0, surface));

                    if (pool != null)
                        return $"these overrides make the pool on slot {slot} unstartable: {pool}";
                }
            }

            return null;
        }

        private static HazardRegion Preset(HarbingerElement element)
        {
            if (!HazardRegionPresets.TryGet(HarbingerLayout.RegionPreset(HarbingerData.AlcoveOf(element)), 0, out var region))
                throw new InvalidOperationException($"no hazard region preset for the {element} alcove");

            return region;
        }

        private static HazardRegion Area(PinnacleHazardArea area)
            => PinnacleHazardRegions.Resolve(new PinnacleHazardOptions(Area: area), HazardRegionPresets.HarbingerName, 0, out var error)
               ?? throw new InvalidOperationException(error);

        /// <summary>A new <see cref="HarbingerScript"/> on the overridden settings, or false with the error.</summary>
        public static bool TryCreateScript(HarbingerSettings baseSettings, HazardArgs overrides, out HarbingerScript script, out string error)
        {
            script = null;

            if (!TryApply(baseSettings, overrides, out var applied, out error))
                return false;

            script = new HarbingerScript(applied);
            return true;
        }
    }
}
