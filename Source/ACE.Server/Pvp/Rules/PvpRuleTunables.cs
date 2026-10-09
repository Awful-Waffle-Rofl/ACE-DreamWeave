using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Server.Managers;

namespace ACE.Server.Pvp.Rules
{
    /// <summary>
    /// Test seam for the PvP rules levers, mirroring <see cref="PvpTunables"/> (and through it
    /// Player_Facets.FacetTunables) exactly: PropertyManager.Get* throws in ACE.Server.Tests for any uncached key,
    /// so the whole read is wrapped and falls back to <see cref="Defaults"/> - the SAME defaults the keys are
    /// registered with in DefaultPropertyManager, so a test-environment read and a fresh, unconfigured shard read
    /// agree.
    ///
    /// NEVER call <see cref="DialSource"/> before the interaction is classified. The isolation invariant
    /// (Docs/Pvp/DESIGN.md "PvP rules (levers)") is that a PvE path reads no lever at all; the choke-point entry
    /// points in <see cref="PvpRules"/> classify first and read second, and a new lever must do the same.
    /// </summary>
    public static class PvpRuleTunables
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(PvpRuleTunables));

        public const string KeyEnabled = "pvp_rules_enabled";
        public const string KeyDamageCap = "pvp_damage_cap";
        public const string KeyDamageCapMaxHealthFraction = "pvp_damage_cap_max_health_fraction";
        public const string KeyDamageRatingScale = "pvp_damage_rating_scale";
        public const string KeyCritDamageRatingScale = "pvp_crit_damage_rating_scale";
        public const string KeyBlockAirborneConsumables = "pvp_block_airborne_consumables";
        public const string KeyConsumableMinIntervalMs = "pvp_consumable_min_interval_ms";
        public const string KeyDispelVulnLockSeconds = "pvp_dispel_vuln_lock_seconds";
        public const string KeyCloakDamageReduction = "pvp_cloak_damage_reduction";
        public const string KeyCleaveEnabled = "pvp_cleave_enabled";
        public const string KeyStripRareBuffs = "pvp_strip_rare_buffs";
        public const string KeyWarMagicDamageMod = "pvp_war_magic_damage_mod";
        public const string KeyMeleeDamageMod = "pvp_melee_damage_mod";
        public const string KeyMissileDamageMod = "pvp_missile_damage_mod";
        public const string KeyCritDamageMod = "pvp_crit_damage_mod";
        public const string KeyMagicAbsorbMod = "pvp_magic_absorb_mod";
        public const string KeyHealingMod = "pvp_healing_mod";
        public const string KeyCsCritMod = "pvp_cs_crit_mod";
        public const string KeyCbCritMod = "pvp_cb_crit_mod";
        public const string KeyHealthFloor = "pvp_health_floor";
        public const string KeyHealthCeiling = "pvp_health_ceiling";
        public const string KeyMeleeDefenseMod = "pvp_melee_defense_mod";
        public const string KeyMissileDefenseMod = "pvp_missile_defense_mod";
        public const string KeyMagicDefenseMod = "pvp_magic_defense_mod";

        /// <summary>Every lever key, in the order /pvprules and the [PVP] rules log line print them.</summary>
        public static readonly IReadOnlyList<string> Keys = new[]
        {
            KeyEnabled,
            KeyDamageCap,
            KeyDamageCapMaxHealthFraction,
            KeyDamageRatingScale,
            KeyCritDamageRatingScale,
            KeyBlockAirborneConsumables,
            KeyConsumableMinIntervalMs,
            KeyDispelVulnLockSeconds,
            KeyCloakDamageReduction,
            KeyCleaveEnabled,
            KeyStripRareBuffs,
            KeyWarMagicDamageMod,
            KeyMeleeDamageMod,
            KeyMissileDamageMod,
            KeyCritDamageMod,
            KeyMagicAbsorbMod,
            KeyHealingMod,
            KeyCsCritMod,
            KeyCbCritMod,
            KeyHealthFloor,
            KeyHealthCeiling,
            KeyMeleeDefenseMod,
            KeyMissileDefenseMod,
            KeyMagicDefenseMod,
        };

        public static Func<PvpRuleDials> DialSource = ReadFromProperties;

        public static readonly PvpRuleDials Defaults = new PvpRuleDials(
            Enabled: true,
            DamageCap: 0,
            DamageCapMaxHealthFraction: 0.5,
            DamageRatingScale: 1.0,
            CritDamageRatingScale: 1.0,
            BlockAirborneConsumables: true,
            ConsumableMinIntervalMs: 0,
            DispelVulnLockSeconds: 300,
            CloakDamageReduction: 100.0,
            CleaveEnabled: true,
            StripRareBuffs: true,
            WarMagicDamageMod: 1.0,
            MeleeDamageMod: 1.0,
            MissileDamageMod: 1.0,
            CritDamageMod: 1.0,
            MagicAbsorbMod: 1.0,
            HealingMod: 1.0,
            CsCritMod: 1.0,
            CbCritMod: 1.0,
            HealthFloor: 0,
            HealthCeiling: 0,
            MeleeDefenseMod: 1.0,
            MissileDefenseMod: 1.0,
            MagicDefenseMod: 1.0);

        private static PvpRuleDials ReadFromProperties()
        {
            try
            {
                var d = Defaults;

                return new PvpRuleDials(
                    Enabled: PropertyManager.GetBool(KeyEnabled, d.Enabled).Item,
                    DamageCap: PropertyManager.GetLong(KeyDamageCap, d.DamageCap).Item,
                    DamageCapMaxHealthFraction: PropertyManager.GetDouble(KeyDamageCapMaxHealthFraction, d.DamageCapMaxHealthFraction).Item,
                    DamageRatingScale: PropertyManager.GetDouble(KeyDamageRatingScale, d.DamageRatingScale).Item,
                    CritDamageRatingScale: PropertyManager.GetDouble(KeyCritDamageRatingScale, d.CritDamageRatingScale).Item,
                    BlockAirborneConsumables: PropertyManager.GetBool(KeyBlockAirborneConsumables, d.BlockAirborneConsumables).Item,
                    ConsumableMinIntervalMs: PropertyManager.GetLong(KeyConsumableMinIntervalMs, d.ConsumableMinIntervalMs).Item,
                    DispelVulnLockSeconds: PropertyManager.GetLong(KeyDispelVulnLockSeconds, d.DispelVulnLockSeconds).Item,
                    CloakDamageReduction: PropertyManager.GetDouble(KeyCloakDamageReduction, d.CloakDamageReduction).Item,
                    CleaveEnabled: PropertyManager.GetBool(KeyCleaveEnabled, d.CleaveEnabled).Item,
                    StripRareBuffs: PropertyManager.GetBool(KeyStripRareBuffs, d.StripRareBuffs).Item,
                    WarMagicDamageMod: PropertyManager.GetDouble(KeyWarMagicDamageMod, d.WarMagicDamageMod).Item,
                    MeleeDamageMod: PropertyManager.GetDouble(KeyMeleeDamageMod, d.MeleeDamageMod).Item,
                    MissileDamageMod: PropertyManager.GetDouble(KeyMissileDamageMod, d.MissileDamageMod).Item,
                    CritDamageMod: PropertyManager.GetDouble(KeyCritDamageMod, d.CritDamageMod).Item,
                    MagicAbsorbMod: PropertyManager.GetDouble(KeyMagicAbsorbMod, d.MagicAbsorbMod).Item,
                    HealingMod: PropertyManager.GetDouble(KeyHealingMod, d.HealingMod).Item,
                    CsCritMod: PropertyManager.GetDouble(KeyCsCritMod, d.CsCritMod).Item,
                    CbCritMod: PropertyManager.GetDouble(KeyCbCritMod, d.CbCritMod).Item,
                    HealthFloor: PropertyManager.GetLong(KeyHealthFloor, d.HealthFloor).Item,
                    HealthCeiling: PropertyManager.GetLong(KeyHealthCeiling, d.HealthCeiling).Item,
                    MeleeDefenseMod: PropertyManager.GetDouble(KeyMeleeDefenseMod, d.MeleeDefenseMod).Item,
                    MissileDefenseMod: PropertyManager.GetDouble(KeyMissileDefenseMod, d.MissileDefenseMod).Item,
                    MagicDefenseMod: PropertyManager.GetDouble(KeyMagicDefenseMod, d.MagicDefenseMod).Item);
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not read the pvp_ rules levers; falling back to built-in defaults", ex);
                return Defaults;
            }
        }

        /// <summary>
        /// One (key, value) row per lever for a snapshot, in <see cref="Keys"/> order. Values are formatted with
        /// PropertyManager's own invariant formatters, so they compare equal to the default text /pvprules
        /// renders from DefaultPropertyManager.
        /// </summary>
        public static IReadOnlyList<(string Key, string Value)> Describe(PvpRuleDials dials)
        {
            dials ??= Defaults;

            return new List<(string, string)>
            {
                (KeyEnabled, PropertyManager.FormatBool(dials.Enabled)),
                (KeyDamageCap, PropertyManager.FormatLong(dials.DamageCap)),
                (KeyDamageCapMaxHealthFraction, PropertyManager.FormatDouble(dials.DamageCapMaxHealthFraction)),
                (KeyDamageRatingScale, PropertyManager.FormatDouble(dials.DamageRatingScale)),
                (KeyCritDamageRatingScale, PropertyManager.FormatDouble(dials.CritDamageRatingScale)),
                (KeyBlockAirborneConsumables, PropertyManager.FormatBool(dials.BlockAirborneConsumables)),
                (KeyConsumableMinIntervalMs, PropertyManager.FormatLong(dials.ConsumableMinIntervalMs)),
                (KeyDispelVulnLockSeconds, PropertyManager.FormatLong(dials.DispelVulnLockSeconds)),
                (KeyCloakDamageReduction, PropertyManager.FormatDouble(dials.CloakDamageReduction)),
                (KeyCleaveEnabled, PropertyManager.FormatBool(dials.CleaveEnabled)),
                (KeyStripRareBuffs, PropertyManager.FormatBool(dials.StripRareBuffs)),
                (KeyWarMagicDamageMod, PropertyManager.FormatDouble(dials.WarMagicDamageMod)),
                (KeyMeleeDamageMod, PropertyManager.FormatDouble(dials.MeleeDamageMod)),
                (KeyMissileDamageMod, PropertyManager.FormatDouble(dials.MissileDamageMod)),
                (KeyCritDamageMod, PropertyManager.FormatDouble(dials.CritDamageMod)),
                (KeyMagicAbsorbMod, PropertyManager.FormatDouble(dials.MagicAbsorbMod)),
                (KeyHealingMod, PropertyManager.FormatDouble(dials.HealingMod)),
                (KeyCsCritMod, PropertyManager.FormatDouble(dials.CsCritMod)),
                (KeyCbCritMod, PropertyManager.FormatDouble(dials.CbCritMod)),
                (KeyHealthFloor, PropertyManager.FormatLong(dials.HealthFloor)),
                (KeyHealthCeiling, PropertyManager.FormatLong(dials.HealthCeiling)),
                (KeyMeleeDefenseMod, PropertyManager.FormatDouble(dials.MeleeDefenseMod)),
                (KeyMissileDefenseMod, PropertyManager.FormatDouble(dials.MissileDefenseMod)),
                (KeyMagicDefenseMod, PropertyManager.FormatDouble(dials.MagicDefenseMod)),
            };
        }

        /// <summary>The registered default of one lever key, as text, or null for a key that is not registered.</summary>
        public static string RegisteredDefaultText(string key)
        {
            if (DefaultPropertyManager.DefaultBooleanProperties.TryGetValue(key, out var b))
                return PropertyManager.FormatBool(b.Item);

            if (DefaultPropertyManager.DefaultLongProperties.TryGetValue(key, out var l))
                return PropertyManager.FormatLong(l.Item);

            if (DefaultPropertyManager.DefaultDoubleProperties.TryGetValue(key, out var d))
                return PropertyManager.FormatDouble(d.Item);

            return null;
        }

        /// <summary>`[PVP] rules key=value ... reason=&lt;reason&gt;` - the boot / after-modify log line.</summary>
        public static string FormatLogLine(PvpRuleDials dials, string reason)
        {
            var pairs = string.Join(" ", Describe(dials).Select(r => $"{r.Key}={r.Value}"));

            return string.IsNullOrEmpty(reason) ? $"[PVP] rules {pairs}" : $"[PVP] rules {pairs} reason={reason}";
        }

        /// <summary>Logs the resolved levers at Info. Never throws: a logging failure must not fail a boot or a /modify* command.</summary>
        public static void LogResolved(string reason)
        {
            try
            {
                log.Info(FormatLogLine(PvpRules.ReadDials(), reason));
            }
            catch (Exception ex)
            {
                log.Error("[PVP] could not log the resolved pvp_ rules levers", ex);
            }
        }

        /// <summary>Called by PropertyAdminService after a successful /modify* (in-game, console or web) of any key.</summary>
        public static void OnPropertyModified(string key)
        {
            if (key != null && Keys.Contains(key))
                LogResolved("modify:" + key);

            // the generated pvp_{arena|bg}_{category}_{stat} context tuning keys log their own non-neutral line
            PvpContextTunables.OnPropertyModified(key);
        }
    }
}
