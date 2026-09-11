using System;
using System.Collections.Generic;

using log4net;

namespace ACE.Server.Entity.Actions
{
    public class DelayManager : IActor
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly SortedSet<DelayAction> delayHeap = new SortedSet<DelayAction>();

        public void RunActions()
        {
            // While the minimum time of our delayHeap is > our current time, kick off actions
            bool checkNeeded = true;

            while (checkNeeded)
            {
                checkNeeded = false;
                List<DelayAction> toAct = new List<DelayAction>();

                // Actions may be added to delayHeap from network queue -- therefore this is needed
                lock (delayHeap)
                {
                    while (delayHeap.Count > 0)
                    {
                        // Find the next (O(1))
                        var min = delayHeap.Min;

                        // If they wanted to run before or at now
                        if (min.EndTime <= Timers.PortalYearTicks)
                        {
                            toAct.Add(min);

                            // O(log(n))
                            delayHeap.Remove(min);
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                foreach (var action in toAct)
                {
                    // Deferred work runs on the world-simulation thread, far from the handler that scheduled it
                    // (every AddDelaySeconds continuation transits here). An unhandled exception escapes to
                    // WorldManager's fatal handler, which does NOT crash the process - it STOPS the world. The
                    // batch in toAct was already removed from delayHeap above, so an escaping throw would also
                    // drop every remaining action in this batch. One bad action must fail alone and be logged.
                    Tuple<IActor, IAction> next;

                    try
                    {
                        next = action.Act();
                    }
                    catch (Exception ex)
                    {
                        log.Error($"DelayManager.RunActions(): Act() threw and was contained. Action type: {action?.GetType().FullName}, WaitTime: {action?.WaitTime}, EndTime: {action?.EndTime}", ex);
                        continue;
                    }

                    if (next != null)
                        next.Item1.EnqueueAction(next.Item2);
                }
            }
        }

        public void EnqueueAction(IAction action)
        {
            var delayAction = action as DelayAction;

            if (delayAction == null)
            {
                log.Error("Non DelayAction IAction added to DelayManager");
                return;
            }

            delayAction.Start();

            lock (delayHeap)
                delayHeap.Add(delayAction);
        }
    }
}
