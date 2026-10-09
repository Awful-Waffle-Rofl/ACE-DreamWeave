using System;
using System.Threading;
using System.Threading.Tasks;

using ACE.Common.Extensions;
using ACE.Server.Entity.Actions;

using log4net;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// Generic form of PropertyAdminService.RunOnWorld, for admin routes whose work returns something other
    /// than a PropertyModifyResult (the World Event routes). RunOnWorld is left exactly as it is.
    ///
    /// Same contract: the work is enqueued through <c>enqueue</c> (the world thread in production) and
    /// waited on for at most <c>timeout</c>. A waiter that gives up sets an abandon flag, and the queued
    /// action skips the work only if it has NOT STARTED yet - so a timeout means the outcome is unknown,
    /// never that nothing happened. A throw inside the work is reported as Faulted, not rethrown.
    /// </summary>
    internal static class AdminWorldCall
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        internal enum Status
        {
            Completed,
            TimedOut,
            Faulted
        }

        internal static Status Run<T>(Action<IAction> enqueue, Func<T> work, TimeSpan timeout, out T result)
        {
            result = default;

            var tcs = new TaskCompletionSource<(Status status, T value)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandoned = 0;

            enqueue(new ActionEventDelegate(() =>
            {
                if (Volatile.Read(ref abandoned) != 0)
                {
                    tcs.TrySetResult((Status.TimedOut, default));
                    return;
                }

                try
                {
                    tcs.TrySetResult((Status.Completed, work()));
                }
                catch (Exception ex)
                {
                    log.Warn($"[MARKET][ADMIN] a queued admin action threw: {ex.GetFullMessage()}");
                    tcs.TrySetResult((Status.Faulted, default));
                }
            }));

            if (!tcs.Task.Wait(timeout))
            {
                Interlocked.Exchange(ref abandoned, 1);
                return Status.TimedOut;
            }

            var (status, value) = tcs.Task.Result;

            result = value;
            return status;
        }
    }
}
