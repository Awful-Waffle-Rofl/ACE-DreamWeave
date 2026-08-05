using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

using ACE.Common;
using ACE.Common.Performance;
using ACE.Server.Entity;

namespace ACE.Server.Managers
{
    /// <summary>
    /// WaffleACE: an in-memory registry of every active <see cref="Fellowship"/> on the server.
    ///
    /// Fellowships are not persisted (they are lost on restart) and each <see cref="ACE.Server.WorldObjects.Player"/>
    /// only holds a reference to its own fellowship, so there is no built-in way to enumerate all of them. This
    /// registry backs the server-managed /fship commands (notably /fship list) and drives the leech-management
    /// sweep. Registration happens in the Fellowship constructor; empty fellowships are pruned lazily on Tick().
    /// </summary>
    public static class FellowshipManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly HashSet<Fellowship> fellowships = new HashSet<Fellowship>();
        private static readonly object fellowshipLock = new object();

        /// <summary>
        /// The rate at which FellowshipManager.Tick() executes. Leech detection is a coarse timeout, so a
        /// low-frequency sweep is plenty; the per-tick cost is a couple of comparisons per member over a
        /// bounded set of fellowships.
        /// </summary>
        private static readonly RateLimiter tickRateLimiter = new RateLimiter(1, TimeSpan.FromSeconds(30));

        public static void Register(Fellowship fellowship)
        {
            lock (fellowshipLock)
                fellowships.Add(fellowship);
        }

        public static void Unregister(Fellowship fellowship)
        {
            lock (fellowshipLock)
                fellowships.Remove(fellowship);
        }

        /// <summary>
        /// Returns a snapshot list of the currently registered fellowships (safe to enumerate without the lock).
        /// </summary>
        public static List<Fellowship> GetAllFellowships()
        {
            lock (fellowshipLock)
                return fellowships.ToList();
        }

        /// <summary>
        /// Timestamp of the last fellowship-panel vitals flush. Vitals run on their own, much faster cadence
        /// than the leech sweep, so the two are throttled independently.
        /// </summary>
        private static double lastVitalFlush;

        /// <summary>
        /// Runs from WorldManager.UpdateGameWorld() on every world tick (60Hz), and internally throttles two
        /// independent jobs:
        ///
        ///   - the vitals flush, on 'fellowship_vital_update_interval', which pushes coalesced fellowship-panel
        ///     vital updates (see <see cref="Fellowship.FlushVitalUpdates"/>);
        ///   - the leech sweep (every 30s), which ejects members who have gone past the timeout without
        ///     contributing XP, and prunes fellowships with no live members left.
        ///
        /// Both are cheap no-ops when not due, so the 60Hz call costs a couple of comparisons.
        /// </summary>
        public static void Tick()
        {
            var vitalInterval = PropertyManager.GetDouble("fellowship_vital_update_interval").Item;

            // clamp: a zero/negative interval would flush every world tick, which is the O(n^2)-at-60Hz
            // behaviour this batching exists to prevent
            if (vitalInterval < 0.1)
                vitalInterval = 0.1;

            var now = Time.GetUnixTime();

            var vitalDue = now - lastVitalFlush >= vitalInterval;
            var leechDue = tickRateLimiter.GetSecondsToWaitBeforeNextEvent() <= 0;

            if (!vitalDue && !leechDue)
                return;

            if (vitalDue)
                lastVitalFlush = now;

            if (leechDue)
                tickRateLimiter.RegisterEvent();

            List<Fellowship> snapshot;
            lock (fellowshipLock)
                snapshot = fellowships.ToList();

            foreach (var fellowship in snapshot)
            {
                try
                {
                    if (vitalDue)
                        fellowship.FlushVitalUpdates();

                    if (leechDue && fellowship.OnTick() == 0)
                        Unregister(fellowship);
                }
                catch (Exception ex)
                {
                    log.Error($"FellowshipManager.Tick() failed for fellowship '{fellowship.FellowshipName}'", ex);
                }
            }
        }
    }
}
