using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Pinnacles.Matron
{
    /// <summary>
    /// Per-copy tuning for /pinnacle enter (plan T9): "key=value" tokens named after the <see cref="MatronSettings"/>
    /// fields (snake_case of each field name) laid over a base settings record for ONE copy. Pure: no PropertyManager,
    /// no world. Unknown keys, unparsable values and malformed tokens are refused with a line naming the offender.
    /// </summary>
    public static class MatronOverrides
    {
        private static readonly (string Key, double Min, double Max, Func<MatronSettings, double, MatronSettings> Set)[] Table =
        {
            ("flow_rate", 0.001, 2, (s, v) => s with { FlowRate = v }),
            ("finale_rate", 0.001, 2, (s, v) => s with { FinaleRate = v }),
            ("drain_rate", -2, -0.01, (s, v) => s with { DrainRate = v }),
            ("lava_clear_rate", -2, -0.01, (s, v) => s with { LavaClearRate = v }),
            ("flow_escalate_seconds", 5, 600, (s, v) => s with { FlowEscalateSeconds = v }),
            ("flow_rate_cap", 0.001, 2, (s, v) => s with { FlowRateCap = v }),
            ("damage", 0, 1000, (s, v) => s with { Damage = v }),
            ("damage_per_m", 0, 1000, (s, v) => s with { DamagePerM = v }),
            ("damage_max", 0, 100000, (s, v) => s with { DamageMax = v }),
            ("submerged_damage", 0, 10000, (s, v) => s with { SubmergedDamage = v }),
            ("submerged_per_m", 0, 1000, (s, v) => s with { SubmergedPerM = v }),
            ("submerged_max", 0, 100000, (s, v) => s with { SubmergedMax = v }),
            ("head_height", 0.5, 10, (s, v) => s with { HeadHeight = v }),
            ("finale_damage_scale", 0.01, 10, (s, v) => s with { FinaleDamageScale = v }),
            ("max_z", RoomFloorZ, RoomCeilingZ, (s, v) => s with { MaxZ = v }),
            ("fill_floor_z", RoomFloorZ, RoomCeilingZ, (s, v) => s with { FillFloorZ = v }),
            ("hazard_pct_min", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { HazardPctMin = v }),
            ("hazard_pct_max", 0, PinnacleTunables.MaxHazardPct, (s, v) => s with { HazardPctMax = v }),
            ("wall_fx", MatronLayout.WallFxPanels, MatronLayout.WallFxBoth, (s, v) => s with { WallFx = (int)v }),
            ("fog_acid", 0, MatronLayout.AcidFogs.Count - 1, (s, v) => s with { FogAcid = (int)v }),
            ("fog_lava", 0, MatronLayout.LavaFogs.Count - 1, (s, v) => s with { FogLava = (int)v }),
            ("flow_delay", 0, 30, (s, v) => s with { FlowDelay = v }),
            ("beacon_depth", MatronScript.DrainMinDepth, 20, (s, v) => s with { BeaconDepth = v }),
            ("beacon_fx", 0, MatronLayout.DrainBeacons.Count - 1, (s, v) => s with { BeaconFx = (int)v }),
            ("return_max_depth", 0, 20, (s, v) => s with { ReturnMaxDepth = v }),
        };

        /// <summary>The keys that take a whole number only.</summary>
        private static readonly HashSet<string> WholeNumberKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "wall_fx", "fog_acid", "fog_lava", "beacon_fx" };

        /// <summary>The matron preset's ground plane and ceiling: the fill must stay inside the room.</summary>
        public const double RoomFloorZ = -60.0, RoomCeilingZ = -38.0;

        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        /// <summary>Every override key, in settings-field order.</summary>
        public static IReadOnlyList<string> Keys { get; } = Table.Select(t => t.Key).ToArray();

        /// <summary>
        /// <paramref name="overrides"/> laid over <paramref name="baseSettings"/>. False with an <paramref name="error"/>
        /// naming the first unknown key, unparsable or out-of-range value, or broken cross-field rule; <paramref name="result"/> is then the base, unchanged.
        /// </summary>
        public static bool TryApply(MatronSettings baseSettings, HazardArgs overrides, out MatronSettings result, out string error)
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
            if (current.FlowRateCap < current.FlowRate)
                error = $"flow_rate_cap ({Num(current.FlowRateCap)}) must be at least flow_rate ({Num(current.FlowRate)})";
            else if (current.FillFloorZ >= current.MaxZ)
                error = $"fill_floor_z ({Num(current.FillFloorZ)}) must be below max_z ({Num(current.MaxZ)})";
            else if (current.HazardPctMin > current.HazardPctMax)
                error = $"hazard_pct_min ({Num(current.HazardPctMin)}) must not exceed hazard_pct_max ({Num(current.HazardPctMax)})";

            if (error != null)
                return false;

            result = current;
            return true;
        }

        /// <summary>A new <see cref="MatronScript"/> on the overridden settings, or false with the error.</summary>
        public static bool TryCreateScript(MatronSettings baseSettings, HazardArgs overrides, out MatronScript script, out string error)
        {
            script = null;

            if (!TryApply(baseSettings, overrides, out var applied, out error))
                return false;

            script = new MatronScript(applied);
            return true;
        }
    }
}