using System;

using ACE.Common;

namespace ACE.Server.Entity
{
    public static class Timers
    {
        /// <summary>
        /// DateTime.UtcNow at the time the server started.
        /// </summary>
        public static DateTime WorldStartTime { get; } = DateTime.UtcNow;

        /// <summary>
        /// DateTime.UtcNow
        /// </summary>
        public static DateTime CurrentTime => DateTime.UtcNow;

        /// <summary>
        /// (CurrentTime - WorldStartTime).TotalSeconds<para />
        /// This value has 1ms precision.
        /// </summary>
        public static double RunningTime => (CurrentTime - WorldStartTime).TotalSeconds;


        /// <summary>
        /// DerethDateTime.UtcNowToEMUTime at the time the server started.
        /// </summary>
        public static DerethDateTime WorldStartLoreTime { get; } = DerethDateTime.UtcNowToEMUTime;

        /// <summary>
        /// Returns DerethDateTime.UtcNowToLoreTime
        /// </summary>
        public static DerethDateTime CurrentLoreTime => DerethDateTime.UtcNowToLoreTime;

        /// <summary>
        /// Returns current in game time, as seen on Map Panel, calculated from PortalYearTicks
        /// </summary>
        public static DerethDateTime CurrentInGameTime => new DerethDateTime(PortalYearTicks);

        /// <summary>
        /// This is the current Portal Year time, in seconds.<para />
        /// It is updated once per WorldManager.UpdateWorld() loop.<para />
        /// This can also be used as the "frame time" value. This value will be unchanged for any calculations done inside of a single Tick of WorldManager.UpdateWorld().<para />
        /// Measured on prod, WorldManager.UpdateWorld() runs about 863 times per second - so a per-assignment
        /// side effect here would run 863 times/second, not "once per tick" in any slow sense. See
        /// CurrentInGameTimeIsDay for why the day/night memoization deliberately lives in DerethDateTime
        /// instead, gated on a much coarser quantum.
        /// </summary>
        public static double PortalYearTicks { get; internal set; } = Timers.WorldStartLoreTime.Ticks;

        /// <summary>
        /// Whether current in-game time (as returned by CurrentInGameTime) falls within the day range.<para />
        /// This is a memoized value, recomputed only when the Derethian quarter-hour quantum underlying
        /// DerethDateTime's Hour calculation changes (see DerethDateTime.IsDayAtTicks) - about every 119
        /// seconds of portal-year time, not on every read and not once per WorldManager tick (which runs
        /// about 863 times/second on prod). DerethDateTime's constructor runs a counting loop proportional to
        /// elapsed real-world time since the retail end date - about 640,000 iterations and 2.782 ms per call
        /// as of 2026 - so callers that need this value every tick (such as per-generator Day/Night checks)
        /// must use this cached accessor rather than constructing a fresh DerethDateTime and reading IsDay.
        /// </summary>
        public static bool CurrentInGameTimeIsDay => DerethDateTime.IsDayAtTicks(PortalYearTicks);
    }
}
