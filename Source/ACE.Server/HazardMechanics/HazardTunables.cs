using System;

using ACE.Server.Managers;

namespace ACE.Server.HazardMechanics
{
    /// <summary>
    /// THE ONLY PropertyManager reader in the hazard mechanics library. Mechanics never read settings: they get
    /// their numbers from the merged <see cref="HazardArgs"/> handed to them in the context, which is what keeps
    /// them testable (PropertyManager reads throw under the unit-test harness).
    ///
    /// Settings:
    ///   hazard_mech_enabled  (bool, default ON)   kill switch: false refuses new runs and stops live ones.
    ///   hazard_mech_tick_ms  (long, default 100)  driver cadence, clamped 50..1000.
    ///   hazard_mech_wisps / _balls / _beams / _memory (string) one key=value string per mechanic, laid UNDER
    ///   the caller's args.
    /// </summary>
    public static class HazardTunables
    {
        public const string EnabledKey = "hazard_mech_enabled";
        public const string TickMsKey = "hazard_mech_tick_ms";
        public const string KeyPrefix = "hazard_mech_";

        public static bool Enabled => PropertyManager.GetBool(EnabledKey, HazardDefaults.Enabled).Item;

        public static long TickMs => Math.Clamp(PropertyManager.GetLong(TickMsKey, HazardDefaults.TickMs).Item, HazardDefaults.MinTickMs, HazardDefaults.MaxTickMs);

        /// <summary>The settings key carrying <paramref name="kind"/>'s key=value string, or null for a kind with none (the tools).</summary>
        public static string SettingsKeyFor(string kind)
        {
            switch (kind?.ToLowerInvariant())
            {
                case HazardMechanicRegistry.Wisps: return KeyPrefix + HazardMechanicRegistry.Wisps;
                case HazardMechanicRegistry.Balls: return KeyPrefix + HazardMechanicRegistry.Balls;
                case HazardMechanicRegistry.Beams: return KeyPrefix + HazardMechanicRegistry.Beams;
                case HazardMechanicRegistry.Memory: return KeyPrefix + HazardMechanicRegistry.Memory;
                default: return null;
            }
        }

        /// <summary>The shipped default for <paramref name="kind"/>'s settings string ("" for a kind with none).</summary>
        public static string ShippedDefaultFor(string kind)
        {
            switch (kind?.ToLowerInvariant())
            {
                case HazardMechanicRegistry.Wisps: return HazardDefaults.Wisps;
                case HazardMechanicRegistry.Balls: return HazardDefaults.Balls;
                case HazardMechanicRegistry.Beams: return HazardDefaults.Beams;
                case HazardMechanicRegistry.Memory: return HazardDefaults.Memory;
                default: return "";
            }
        }

        /// <summary>The live settings string for <paramref name="kind"/>, parsed. Empty for a kind with none.</summary>
        public static HazardArgs SettingsFor(string kind)
        {
            var key = SettingsKeyFor(kind);

            if (key == null)
                return HazardArgs.Empty;

            return HazardArgs.Parse(PropertyManager.GetString(key, ShippedDefaultFor(kind)).Item);
        }
    }
}
