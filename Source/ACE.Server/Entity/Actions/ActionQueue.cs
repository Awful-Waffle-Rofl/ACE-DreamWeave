// Uncomment this if you want to measure the time actions take to execute and report slow ones
//#define WRAP_AND_MEASURE_ACT_WITH_STOPWATCH

using System;
using System.Collections.Concurrent;

namespace ACE.Server.Entity.Actions
{
    public class ActionQueue : IActor
    {
        protected ConcurrentQueue<IAction> Queue { get; } = new ConcurrentQueue<IAction>();

        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        #if WRAP_AND_MEASURE_ACT_WITH_STOPWATCH
        private readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
        #endif

        public void RunActions()
        {
            if (Queue.IsEmpty)
                return;

            var count = Queue.Count;

            for (int i = 0; i < count; i++)
            {
                if (Queue.TryDequeue(out var result))
                {
                    #if WRAP_AND_MEASURE_ACT_WITH_STOPWATCH
                    sw.Restart();
                    #endif

                    // Deferred/queued work runs on the world-simulation thread, far from the handler that
                    // scheduled it. An unhandled exception here escapes to WorldManager's fatal handler, which
                    // does NOT crash the process - it STOPS the world (process up, sessions connected, nothing
                    // ticking). This one loop drains all four action queues (inbound messages, WorldManager's,
                    // every landblock's, every player's), so one bad action must fail alone and be logged with
                    // the actor's identity, never take the world down. See the reference pattern in
                    // Landblock.CreateWorldObjects.
                    Tuple<IActor, IAction> enqueue = null;

                    try
                    {
                        enqueue = result.Act();
                    }
                    catch (Exception ex)
                    {
                        if (result is ActionEventDelegate failedActionEventDelegate)
                        {
                            if (failedActionEventDelegate.Action.Target is WorldObjects.WorldObject failedWorldObject)
                                log.Error($"ActionQueue.RunActions(): Act() threw. Method.Name: {failedActionEventDelegate.Action.Method.Name}, Target: {failedActionEventDelegate.Action.Target} 0x{failedWorldObject.Guid}:{failedWorldObject.Name} [{failedWorldObject.WeenieClassId} - {failedWorldObject.WeenieType}]", ex);
                            else
                                log.Error($"ActionQueue.RunActions(): Act() threw. Method.Name: {failedActionEventDelegate.Action.Method.Name}, Target: {failedActionEventDelegate.Action.Target}", ex);
                        }
                        else
                            log.Error($"ActionQueue.RunActions(): Act() threw. Action type: {result?.GetType().FullName}", ex);

                        // no enqueue result to propagate on failure; the remaining queued actions must still drain this tick
                        continue;
                    }

                    #if WRAP_AND_MEASURE_ACT_WITH_STOPWATCH
                    sw.Stop();

                    if (sw.Elapsed.TotalSeconds > 1)
                    {
                        if (result is ActionEventDelegate actionEventDelegate)
                        {
                            if (actionEventDelegate.Action.Target is WorldObjects.WorldObject worldObject)
                                log.Warn($"ActionQueue Act() took {sw.Elapsed.TotalSeconds:N0}s. Method.Name: {actionEventDelegate.Action.Method.Name}, Target: {actionEventDelegate.Action.Target} 0x{worldObject.Guid}:{worldObject.Name}");
                            else
                                log.Warn($"ActionQueue Act() took {sw.Elapsed.TotalSeconds:N0}s. Method.Name: {actionEventDelegate.Action.Method.Name}, Target: {actionEventDelegate.Action.Target}");
                        }
                        else
                            log.Warn($"ActionQueue Act() took {sw.Elapsed.TotalSeconds:N0}s.");
                    }
                    #endif

                    if (enqueue != null)
                        enqueue.Item1.EnqueueAction(enqueue.Item2);
                }
            }
        }

        public void EnqueueAction(IAction action)
        {
            Queue.Enqueue(action);
        }

        public void Clear()
        {
            Queue.Clear();
        }
    }
}
