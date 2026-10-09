using System;
using System.Threading;

using log4net;

using ACE.Common.Extensions;
using ACE.Server.Managers.Market;

namespace ACE.Server.Managers
{
    /// <summary>
    /// The wall-clock background thread for MarketAdvertiser (Docs/Market). Structured exactly like
    /// MarketBuyOrderExpiry: a bare background Thread, not a Timer, so a throw is caught here rather
    /// than swallowed.
    ///
    /// market_ad_interval_hours is re-read every tick, so flipping it on or off takes effect within
    /// TickInterval without a restart. The slot bookkeeping (lastPostedSlot/seeded) lives on this
    /// class rather than inside Evaluate, so Evaluate itself stays a pure function a test can drive
    /// directly instead of sleeping through real ticks.
    /// </summary>
    public static class MarketAdvertiserJob
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
        private static volatile bool running;
        private static Thread worker;

        private static DateTime? lastPostedSlot;
        private static bool seeded;

        public static void Start()
        {
            if (running) return;
            running = true;
            lastPostedSlot = null;
            seeded = false;
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MarketAdvertiserJob" };
            worker.Start();
            log.Info("[MARKET] trade advertiser job started; checks the schedule every 30 seconds.");
        }

        public static void Stop()
        {
            running = false;
            var thread = worker;
            worker = null;
            if (thread == null) return;
            try { thread.Join(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { log.Warn($"[MARKET] the trade advertiser job did not stop cleanly: {ex.GetFullMessage()}"); }
        }

        private static void WorkerLoop()
        {
            while (running)
            {
                Thread.Sleep(TickInterval);

                // Re-checked AFTER the sleep, not only at the loop head - see MarketBuyOrderExpiry for
                // why: Stop joins for 5 seconds against a 30 second tick.
                if (!running)
                    break;

                try
                {
                    Tick(DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] the trade advertiser tick failed: {ex.GetFullMessage()}");
                }
            }
        }

        private static void Tick(DateTime nowUtc)
        {
            var intervalHours = (int)PropertyManager.GetLong("market_ad_interval_hours").Item;

            var decision = Evaluate(nowUtc, intervalHours, lastPostedSlot, seeded);

            lastPostedSlot = decision.NewLastPostedSlot;
            seeded = decision.NewSeeded;

            if (decision.SlotToPost != null)
                MarketAdvertiser.PostNow();
        }

        /// <summary>
        /// The pure slot decision, driven directly by tests rather than through a sleeping thread.
        ///
        /// intervalHours &lt;= 0 always returns "disabled" (no post, unseeded) so turning the feature
        /// back on later reseeds cleanly instead of firing immediately on old state. Otherwise: the
        /// FIRST call after Start (seeded == false) always seeds to the current slot WITHOUT posting -
        /// a restart must never immediately fire an advert. After that, a post happens only when the
        /// current slot differs from the last one posted, which caps it at one post per slot even
        /// after a long stall (a missed slot is simply skipped, never queued up and burst-fired).
        /// </summary>
        public static SlotDecision Evaluate(DateTime nowUtc, int intervalHours, DateTime? lastPostedSlot, bool seeded)
        {
            if (intervalHours <= 0)
                return new SlotDecision { SlotToPost = null, NewLastPostedSlot = null, NewSeeded = false };

            if (intervalHours > 24)
                intervalHours = 24;

            var currentSlot = CurrentSlotUtc(nowUtc, intervalHours);

            if (!seeded)
                return new SlotDecision { SlotToPost = null, NewLastPostedSlot = currentSlot, NewSeeded = true };

            if (lastPostedSlot != currentSlot)
                return new SlotDecision { SlotToPost = currentSlot, NewLastPostedSlot = currentSlot, NewSeeded = true };

            return new SlotDecision { SlotToPost = null, NewLastPostedSlot = lastPostedSlot, NewSeeded = true };
        }

        /// <summary>The most recent aligned slot at or before nowUtc - the mirror of MarketAdvertiser.NextRunUtc's "strictly after" search.</summary>
        private static DateTime CurrentSlotUtc(DateTime nowUtc, int intervalHours)
        {
            var midnight = nowUtc.Date;
            var hour = (nowUtc.Hour / intervalHours) * intervalHours;
            return midnight.AddHours(hour);
        }
    }

    /// <summary>The outcome of one MarketAdvertiserJob.Evaluate call: what to post, if anything, and the state to carry into the next tick.</summary>
    public struct SlotDecision
    {
        public DateTime? SlotToPost { get; set; }
        public DateTime? NewLastPostedSlot { get; set; }
        public bool NewSeeded { get; set; }
    }
}
