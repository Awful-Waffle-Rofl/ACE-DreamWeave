using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Managers;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Live global overrides for the per-source (sources.json) and per-boss (bosses.json) wave/boss dials
    /// (TECH-DESIGN 2.15/C16). One server property per dial, `world_events_&lt;group&gt;_&lt;dial&gt;`; 0 OR
    /// NEGATIVE (the shipped default for every dial here is 0) means "use the JSON value", and any POSITIVE,
    /// finite value REPLACES the JSON value for every source/boss - there is no per-source or per-boss
    /// override, only a global one. Every dial this class knows about is a magnitude/count/duration, so a
    /// negative override can never be a meaningful value on its own terms - it is read as "unset" rather
    /// than as "an override the pure math would have to reject downstream". Non-finite (NaN/Infinity) is
    /// unset for the same reason.
    ///
    /// Reads are cheap (PropertyManager.GetDouble/GetLong are ConcurrentDictionary lookups) and go through
    /// <see cref="DoubleSource"/>/<see cref="LongSource"/> test seams, mirroring
    /// <see cref="WorldEventRosterSelector.DialSource"/>. A read that throws - a unit test has no shard
    /// config at all - is treated exactly like "no override": the JSON value stands.
    ///
    /// This class deliberately holds no math: the pure functions it feeds (CrowdHealthDef.Resolve,
    /// ScaledCount.Resolve, BossDef.ResolveHealthMult, WorldEventThroughput.ResolveBossMult) never call into
    /// PropertyManager themselves - each def's Effective* property resolves the override once, at the point
    /// the dial is consulted, and hands a plain value to the pure math. <see cref="WorldEventPaceController"/>
    /// is the one exception worth calling out: it reads its window/step dials through
    /// <see cref="Defs.PaceDef"/>'s Effective* accessors (which are fail-safe to the JSON value exactly like
    /// every other def here) rather than calling PropertyManager directly itself - see
    /// <see cref="Defs.PaceDef.EffectiveWindow"/> for the one place the min/max PAIR is additionally
    /// resolved together, since each Effective* accessor only ever validates its own dial in isolation.
    /// </summary>
    internal static class WorldEventOverrides
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(WorldEventOverrides));

        /// <summary>Test seam. Defaults to <see cref="PropertyManager.GetDouble(string, double, bool)"/> with a 0 fallback.</summary>
        internal static Func<string, double> DoubleSource = key => PropertyManager.GetDouble(key, 0).Item;

        /// <summary>Test seam. Defaults to <see cref="PropertyManager.GetLong(string, long, bool)"/> with a 0 fallback.</summary>
        internal static Func<string, long> LongSource = key => PropertyManager.GetLong(key, 0).Item;

        /// <summary>
        /// Every world_events dial override key this class knows about, and whether it is long- or
        /// double-typed. Used only to build the compose-time/status active-override summary - reading a
        /// SPECIFIC dial for SPECIFIC math never goes through this list, it calls <see cref="Double"/>/
        /// <see cref="Long"/> directly with the one key it needs.
        /// </summary>
        private static readonly (string key, bool isLong)[] AllKeys =
        {
            ("world_events_crowd_health_start_at", true),
            ("world_events_crowd_health_per_participant", false),
            ("world_events_crowd_health_cap", false),
            ("world_events_pace_health_step", false),
            ("world_events_pace_health_cap", false),
            ("world_events_pace_quantity_step", true),
            ("world_events_pace_min_wave_seconds", false),
            ("world_events_pace_max_wave_seconds", false),
            ("world_events_wave_count_base", true),
            ("world_events_wave_count_per_participant", false),
            ("world_events_wave_count_cap", true),
            ("world_events_max_alive", true),
            ("world_events_wave_interval_seconds", false),
            ("world_events_overflow_per_champion", true),
            ("world_events_synthetic_elite_health_mult", false),
            ("world_events_synthetic_champion_health_mult", false),
            ("world_events_boss_throughput_floor_health", true),
            ("world_events_boss_throughput_cap_health", true),
            ("world_events_boss_target_kill_seconds", false),
            ("world_events_boss_throughput_calibration", false),
            ("world_events_boss_min_sample_seconds", false),
            ("world_events_boss_per_player", false),
            ("world_events_boss_health_cap", false)
        };

        /// <summary>True for a value that counts as "set" for a live override: finite and strictly positive.</summary>
        private static bool IsSet(double value) => double.IsFinite(value) && value > 0;

        /// <summary>True for a value that counts as "set" for a live override. See the double overload.</summary>
        private static bool IsSet(long value) => value > 0;

        /// <summary>
        /// The effective value of a double dial: <paramref name="jsonValue"/> unless the world_events_&lt;...&gt;
        /// property at <paramref name="key"/> is set (finite and positive - see <see cref="IsSet(double)"/>),
        /// in which case the property wins.
        /// </summary>
        public static double Double(string key, double jsonValue)
        {
            double overrideValue;

            try
            {
                overrideValue = DoubleSource(key);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] could not read override property {key}; using the JSON value", ex);
                return jsonValue;
            }

            return IsSet(overrideValue) ? overrideValue : jsonValue;
        }

        /// <summary>The effective value of a long/integer dial. See <see cref="Double"/>.</summary>
        public static long Long(string key, long jsonValue)
        {
            long overrideValue;

            try
            {
                overrideValue = LongSource(key);
            }
            catch (Exception ex)
            {
                log.Error($"[WORLDEVENT] could not read override property {key}; using the JSON value", ex);
                return jsonValue;
            }

            return IsSet(overrideValue) ? overrideValue : jsonValue;
        }

        /// <summary>
        /// "key=value" for every override dial currently SET (see <see cref="IsSet(double)"/>/
        /// <see cref="IsSet(long)"/> - a zero or negative value is never listed), sorted by key. Empty when
        /// nothing is overridden. Used both for the compose-time [WORLDEVENT] log line and
        /// "/worldevent status".
        /// </summary>
        public static IReadOnlyList<string> ActiveOverrides()
        {
            var active = new List<string>();

            foreach (var (key, isLong) in AllKeys)
            {
                try
                {
                    if (isLong)
                    {
                        var value = LongSource(key);

                        if (IsSet(value))
                            active.Add($"{key}={value}");
                    }
                    else
                    {
                        var value = DoubleSource(key);

                        if (IsSet(value))
                            active.Add($"{key}={value}");
                    }
                }
                catch (Exception ex)
                {
                    log.Error($"[WORLDEVENT] could not read override property {key} while summarising active overrides", ex);
                }
            }

            return active.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        /// <summary>One line for the compose-time log / "/worldevent status": "none active" or the sorted list.</summary>
        public static string ActiveOverridesSummary()
        {
            var active = ActiveOverrides();

            return active.Count == 0 ? "world event dial overrides: none active" : $"world event dial overrides: {string.Join(", ", active)}";
        }
    }
}
