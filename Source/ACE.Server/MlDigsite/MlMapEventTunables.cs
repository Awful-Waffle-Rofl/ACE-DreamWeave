using System;

using ACE.Server.Managers;

namespace ACE.Server.MlDigsite
{
    /// <summary>
    /// THE single place the RoZ round 19 level-spread scaling reads PropertyManager (the ml_mapevent_* rows).
    /// <see cref="MlMapEventScaling"/> takes the resolved <see cref="MlMapEventSettings"/> as a value and never
    /// reads a tunable itself, which keeps it unit-testable (PropertyManager reads throw under the test
    /// harness) and stops one key being read with two fallbacks in two places.
    ///
    /// Every read passes the registered default as its explicit fallback, for the reason MlDigsiteTunables
    /// documents: an unseeded key otherwise reads as the TYPE default, and a default-true switch would read
    /// false until the first sync. Both switches ship ON (this repo's standing rule for new settings).
    ///
    /// ml_mapevent_boss_magic_defense_scale (M1, PR #1369) is deliberately NOT read here: its two existing call
    /// sites read it themselves and are pinned by MlMapEventBossMagicDefenseWiringTests.
    /// </summary>
    public static class MlMapEventTunables
    {
        public static bool SpreadScalingEnabled => PropertyManager.GetBool("ml_mapevent_spread_scaling_enabled", true).Item;

        public static bool BossDamageScalingEnabled => PropertyManager.GetBool("ml_mapevent_boss_damage_scaling_enabled", true).Item;

        /// <summary>
        /// Resolves every level-spread tunable into one value, clamped into a range the pure maths can use. A
        /// chance is kept strictly inside (0, 1) so its logit is finite.
        /// </summary>
        public static MlMapEventSettings Read()
        {
            return new MlMapEventSettings(
                SpreadScalingEnabled,
                Chance(PropertyManager.GetDouble("ml_mapevent_min_hit_chance", 0.60).Item, 0.60),
                Chance(PropertyManager.GetDouble("ml_mapevent_min_spell_land_chance", 0.70).Item, 0.70),
                Chance(PropertyManager.GetDouble("ml_mapevent_trash_max_hit_chance", 0.75).Item, 0.75),
                Math.Max(1, PropertyManager.GetLong("ml_mapevent_trash_reference_max_health", 500).Item),
                Finite(PropertyManager.GetDouble("ml_mapevent_trash_damage_floor", 0.25).Item, 0.25, 0.01, 1.0),
                Finite(PropertyManager.GetDouble("ml_mapevent_trash_health_exponent", 1.0).Item, 1.0, 0.0, 10.0),
                Finite(PropertyManager.GetDouble("ml_mapevent_xp_level_exponent", 2.0).Item, 2.0, 0.0, 10.0),
                Finite(PropertyManager.GetDouble("ml_mapevent_boss_power_per_player", 0.15).Item, 0.15, 0.0, 10.0),
                Finite(PropertyManager.GetDouble("ml_mapevent_boss_health_cap", 3.0).Item, 3.0, 1.0, 100.0),
                Math.Max(0, PropertyManager.GetLong("ml_mapevent_boss_base_health", 0).Item),
                BossDamageScalingEnabled,
                Finite(PropertyManager.GetDouble("ml_mapevent_boss_hits_to_kill", 3.0).Item, 3.0, 0.1, 100.0));
        }

        private static double Chance(double value, double fallback) => Finite(value, fallback, 0.01, 0.99);

        private static double Finite(double value, double fallback, double min, double max)
        {
            if (!double.IsFinite(value))
                value = fallback;

            return Math.Clamp(value, min, max);
        }
    }
}
