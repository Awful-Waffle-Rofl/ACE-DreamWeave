using System;
using System.Threading;

using log4net;

using ACE.Common.Extensions;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Closes Wanted buy orders past expires_At and refunds their escrow (WANTED-DESIGN 6.4), on an
    /// hourly pass. A bare background thread, not a Timer, so a throw is caught here rather than
    /// swallowed; and it never reads PropertyManager inside the pass - MarketManager.ExpireOrders reads nothing.
    /// </summary>
    public static class MarketBuyOrderExpiry
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly TimeSpan PassInterval = TimeSpan.FromHours(1);
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
        private static volatile bool running;
        private static Thread worker;

        public static void Start()
        {
            if (running) return;
            running = true;
            worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MarketBuyOrderExpiry" };
            worker.Start();
            log.Info($"[MARKET] buy order expiry started; passes run every {PassInterval.TotalHours:N0} hour(s).");
        }

        public static void Stop()
        {
            running = false;
            var thread = worker;
            worker = null;
            if (thread == null) return;
            try { thread.Join(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { log.Warn($"[MARKET] the buy order expiry pass did not stop cleanly: {ex.GetFullMessage()}"); }
        }

        private static void WorkerLoop()
        {
            var lastPassUtc = DateTime.UtcNow;
            while (running)
            {
                Thread.Sleep(TickInterval);

                // Re-checked AFTER the sleep, not only at the loop head. Stop joins for 5 seconds and
                // the tick is 30, so without this a thread asleep when Stop was called wakes up and
                // runs a whole pass against a MarketManager that Shutdown has already torn down.
                if (!running)
                    break;

                try
                {
                    var now = DateTime.UtcNow;
                    if (now - lastPassUtc < PassInterval) continue;
                    lastPassUtc = now;
                    ACE.Server.Managers.Market.MarketManager.ExpireOrders(now);
                }
                catch (Exception ex)
                {
                    log.Error($"[MARKET] the buy order expiry tick failed: {ex.GetFullMessage()}");
                }
            }
        }
    }
}
