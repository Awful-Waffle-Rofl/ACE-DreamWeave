using System;

using ACE.Server.Managers;

namespace ACE.Server.Network
{
    /// <summary>
    /// The outbound ring's settings, read from PropertyManager once per NetworkManager.DoSessionWork
    /// (<see cref="Refresh"/>) and held in volatile fields, so the per-message record path never touches
    /// PropertyManager. Values are clamped here.
    /// </summary>
    public static class OutboundRingSettings
    {
        public const string EnabledKey = "outbound_ring_enabled";
        public const string EntriesKey = "outbound_ring_entries";
        public const string DumpEntriesKey = "outbound_ring_dump_entries";
        public const string ControlSecondsKey = "outbound_ring_control_seconds";

        public const bool DefaultEnabled = true;
        public const long DefaultEntries = 512;
        public const long DefaultDumpEntries = 128;
        public const long DefaultControlSeconds = 300;

        public const int MinEntries = 64;
        public const int MaxEntries = 4096;
        public const int MinDumpEntries = 16;
        public const int MaxDumpEntries = 512;
        public const int MaxControlSeconds = 86400;

        private static volatile bool enabled = DefaultEnabled;
        private static volatile int entries = ClampEntries(DefaultEntries);
        private static volatile int dumpEntries = ClampDumpEntries(DefaultDumpEntries);
        private static volatile int controlSeconds = ClampControlSeconds(DefaultControlSeconds);

        /// <summary>Kill switch: false records nothing and dumps nothing.</summary>
        public static bool Enabled => enabled;

        /// <summary>Ring capacity for a ring allocated now: 64-4096, a power of two.</summary>
        public static int Entries => entries;

        /// <summary>Most entry lines per dump: 16-512.</summary>
        public static int DumpEntries => dumpEntries;

        /// <summary>Seconds between control samples; 0 is off.</summary>
        public static int ControlSeconds => controlSeconds;

        /// <summary>Clamped to 64-4096, then rounded up to a power of two.</summary>
        public static int ClampEntries(long value)
        {
            var clamped = (int)Math.Clamp(value, MinEntries, MaxEntries);

            var pow = MinEntries;
            while (pow < clamped)
                pow <<= 1;

            return pow;
        }

        public static int ClampDumpEntries(long value) => (int)Math.Clamp(value, MinDumpEntries, MaxDumpEntries);

        /// <summary>0 or less is off (0); otherwise at most a day.</summary>
        public static int ClampControlSeconds(long value) => value <= 0 ? 0 : (int)Math.Min(value, MaxControlSeconds);

        /// <summary>
        /// Re-reads all four settings. Never throws: a failed read keeps the compiled default for that setting.
        /// </summary>
        public static void Refresh()
        {
            enabled = Read(() => PropertyManager.GetBool(EnabledKey, DefaultEnabled).Item, DefaultEnabled);
            entries = ClampEntries(Read(() => PropertyManager.GetLong(EntriesKey, DefaultEntries).Item, DefaultEntries));
            dumpEntries = ClampDumpEntries(Read(() => PropertyManager.GetLong(DumpEntriesKey, DefaultDumpEntries).Item, DefaultDumpEntries));
            controlSeconds = ClampControlSeconds(Read(() => PropertyManager.GetLong(ControlSecondsKey, DefaultControlSeconds).Item, DefaultControlSeconds));
        }

        private static T Read<T>(Func<T> read, T fallback)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }
}
