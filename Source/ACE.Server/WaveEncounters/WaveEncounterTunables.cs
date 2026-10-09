using System;

using ACE.Server.Managers;

namespace ACE.Server.WaveEncounters
{
    /// <summary>
    /// The four live tunables of the object-anchored wave runner. Every read falls back to the compiled
    /// default on any throw: PropertyManager reads throw for an uncached key in the unit-test process (no
    /// shard config), and a broken read must never take an encounter down with it.
    /// </summary>
    public static class WaveEncounterTunables
    {
        public const long DefaultTtlSeconds = 2700;
        public const long DefaultWipeGraceSeconds = 20;
        public const long DefaultCooldownSeconds = 300;
        public const double DefaultPresenceRadius = 80.0;

        public static TimeSpan Ttl => Seconds(ReadLong("wave_encounter_ttl_seconds", DefaultTtlSeconds));

        public static TimeSpan WipeGrace => Seconds(ReadLong("wave_encounter_wipe_grace_seconds", DefaultWipeGraceSeconds));

        public static TimeSpan Cooldown => Seconds(ReadLong("wave_encounter_cooldown_seconds", DefaultCooldownSeconds));

        public static float PresenceRadius
        {
            get
            {
                double value;

                try
                {
                    value = PropertyManager.GetDouble("wave_encounter_presence_radius", DefaultPresenceRadius).Item;
                }
                catch
                {
                    value = DefaultPresenceRadius;
                }

                if (double.IsNaN(value) || value <= 0)
                    return 0f;

                return (float)Math.Min(value, 100000.0);
            }
        }

        private static long ReadLong(string key, long fallback)
        {
            try
            {
                return PropertyManager.GetLong(key, fallback).Item;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Clamped to [0, one day]: a negative reads as "off", a huge value cannot overflow a DateTime add.</summary>
        private static TimeSpan Seconds(long seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86400));
    }
}
