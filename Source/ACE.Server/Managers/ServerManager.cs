using System;
using System.Threading;

using log4net;

using ACE.Common;
using ACE.Common.Extensions;
using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Entity.Actions;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.Network.Managers;
using ACE.Server.Mods;

namespace ACE.Server.Managers
{
    /// <summary>
    /// ServerManager handles unloading the server application properly.
    /// </summary>
    /// <remarks>
    ///   Possibly useful for:
    ///     1. Monitor for errors and performance issues in LandblockManager, GuidManager, WorldManager,
    ///         DatabaseManager, or AssetManager
    ///   Known issue:
    ///     1. No method to verify that everything unloaded properly.
    /// </remarks>
    public static class ServerManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Indicates advanced warning if the applcation will unload.
        /// </summary>
        public static bool ShutdownInitiated { get; private set; }

        /// <summary>
        /// Indicates server shutting down.
        /// </summary>
        public static bool ShutdownInProgress { get; private set; }

        /// <summary>
        /// The amount of seconds that the server will wait before unloading the application.
        /// </summary>
        public static uint ShutdownInterval { get; private set; }

        public static DateTime ShutdownTime { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// Guards the transition into final shutdown so only one thread ever runs it. Two callers can
        /// reach ShutdownServer at nearly the same time: a repeat container stop signal (SIGTERM/SIGINT)
        /// arriving during an already-pending countdown routes through DoShutdownNow, which runs
        /// ShutdownServer synchronously on the signal thread while the original countdown thread started
        /// by BeginShutdown is still waiting on its own captured shutdownTime; or /shutdown racing a
        /// SIGTERM the same way. Without this guard, the loser's wait loop would eventually expire and
        /// re-run the entire final shutdown body (logoff, landblock unload, DatabaseManager.Stop,
        /// Environment.Exit) concurrently with the winner.
        /// </summary>
        private static int finalShutdownStarted;

        /// <summary>
        /// Generation counter for the hard shutdown deadline (see ArmShutdownDeadline). Each arming
        /// captures the current value; a deadline thread that wakes to find the counter has moved on
        /// belongs to a superseded shutdown and returns without doing anything. CancelShutdown bumps it,
        /// which is what stops a cancelled shutdown's deadline from force-exiting a server that is
        /// happily still running minutes later.
        /// </summary>
        private static int shutdownDeadlineGeneration;

        /// <summary>
        /// Sets the Shutdown Interval in Seconds
        /// </summary>
        /// <param name="interval">postive value representing seconds</param>
        public static void SetShutdownInterval(uint interval)
        {
            log.Info($"Server shutdown interval reset: {interval}");
            ShutdownInterval = interval;
        }

        public static void Initialize()
        {
            // Loads the configuration for ShutdownInterval from the settings file.
            ShutdownInterval = ConfigManager.Config.Server.ShutdownInterval;
        }

        /// <summary>
        /// Starts the shutdown wait thread.
        /// </summary>
        public static void BeginShutdown()
        {
            ShutdownInitiated = true;

            ArmShutdownDeadline();

            var shutdownThread = new Thread(ShutdownServer);
            shutdownThread.Name = "Shutdown Server";
            shutdownThread.Start();
        }

        /// <summary>
        /// Calling this function will always cancel an in-progress shutdown (application unload). This will also
        /// stop the shutdown wait thread and alert users that the server will stay in operation.
        /// </summary>
        /// <remarks>
        /// If this countdown was started by a container stop signal (SIGTERM/SIGINT), cancelling it here does
        /// NOT stop the container: Docker will still send SIGKILL once stop_grace_period elapses (600s in both
        /// deploy/stage and deploy/prod docker-compose.yml). Cancelling only buys time within that window - it
        /// does not cancel the container stop itself.
        /// </remarks>
        public static void CancelShutdown()
        {
            ShutdownInitiated = false;
            ShutdownTime = DateTime.MinValue;

            // Retire any armed hard deadline along with the shutdown it belonged to. Without this, a
            // shutdown that was started and then cancelled would still force-exit the process once its
            // deadline elapsed - turning a cancel into a delayed, unexplained outage.
            Interlocked.Increment(ref shutdownDeadlineGeneration);
        }

        public static void DoShutdownNow()
        {
            SetShutdownInterval(0);
            ShutdownInitiated = true;

            // Armed after SetShutdownInterval(0) on purpose, so the force-now path gets the short
            // deadline (0 + ShutdownHardDeadlineSeconds) rather than inheriting a long countdown's.
            ArmShutdownDeadline();

            PlayerManager.BroadcastToAll(new GameMessageSystemChat("Broadcast from System> ATTENTION - This Asheron's Call Server is shutting down NOW!!!!", ChatMessageType.WorldBroadcast));
            ShutdownServer();
        }

        /// <summary>
        /// Fast, world-independent shutdown. This is the path taken when the world thread is dead or
        /// hung, where the normal ShutdownServer body cannot complete: three of its waits (players to
        /// log off, landblocks to unload, WorldActive to clear) are unbounded and every one of them is
        /// serviced by the world thread. On 2026-09-01 a container stop hit exactly that and had to be
        /// force-killed after the 300s warned countdown ran on a thread that was already dead.
        ///
        /// It deliberately does very little. The ONE thing worth doing with a dead world is draining the
        /// shard database queue: SerializedShardDatabase runs on its own thread and is still alive and
        /// still processing when the world is not, and its Stop() abandons whatever is left queued
        /// rather than flushing it. Everything else here is teardown.
        ///
        /// NOTHING in this path may touch the world thread: no PlayerManager.GetAllOnline, no
        /// LandblockManager, no WorldManager.EnqueueAction, no wait on WorldManager.WorldActive, no
        /// ForceLogoff. If a future change adds one, this method stops working in precisely the
        /// situation it exists for, and it will look fine in every test where the world is healthy.
        /// </summary>
        /// <param name="reason">Logged verbatim. Say which caller decided this and why.</param>
        /// <param name="exitCode">70 for a watchdog self-exit, 0 for a requested stop on a dead world.</param>
        public static void DoFastShutdown(string reason, int exitCode)
        {
            // ORDER IS LOAD-BEARING: these two flags go first, before anything that could exit.
            // Environment.Exit at the bottom re-enters Program.OnProcessExit, which routes container
            // exits back into InitiateContainerShutdown("Process exit"), whose repeat-signal branch
            // calls the slow, world-dependent DoShutdownNow() unless ShutdownInProgress is already
            // true. Setting these later - or not at all - reintroduces the exact hang this method
            // exists to remove.
            ShutdownInitiated = true;
            ShutdownInProgress = true;

            // Same reasoning one level down: claim the final-shutdown latch so a countdown thread that
            // is mid-wait can never enter the normal final shutdown body behind us.
            Interlocked.Exchange(ref finalShutdownStarted, 1);

            log.Warn($"FAST SHUTDOWN ({reason}) - skipping player logoff, landblock unload and the world stop, " +
                     $"because those all depend on the world thread. Draining the shard database queue, then exiting with code {exitCode}.");

            // Every teardown step is best-effort, and Environment.Exit below must be reached whatever
            // any of them does. This is not defensive habit, it is the difference between this method
            // working and this method making things worse: the only caller that can reach here without
            // an operator behind it is WorldWatchdog, whose exit is a ONE-SHOT latch and whose poll loop
            // swallows exceptions. A throw escaping here would therefore be logged once, never retried,
            // and would leave the process wedged with ShutdownInProgress already true - at which point a
            // later "docker stop" hits InitiateContainerShutdown's repeat-signal branch, sees that flag,
            // logs "shutdown already in progress, ignoring repeat signal" and does nothing at all. Only
            // SIGKILL would recover it, and SIGKILL loses the shard drain. Being stuck is the exact
            // thing this path exists to end, so it may never be the thing this path causes.
            try
            {
                PropertyManager.StopUpdating();

                // Note: no ResyncVariables() here, unlike the normal path. That writes property state
                // back through the world, and this path exists precisely for when the world cannot
                // service it.

                DrainShardQueue(TimeSpan.FromSeconds(GetShutdownDrainSeconds()));

                DatabaseManager.Stop();
            }
            catch (Exception ex)
            {
                log.Error("Fast shutdown teardown failed. Exiting anyway - some shard state may be unsaved.", ex);
            }

            log.Warn($"Fast shutdown complete. Exiting at {DateTime.UtcNow.ToCommonString()} with code {exitCode}.");

            Environment.Exit(exitCode);
        }

        /// <summary>
        /// Arms a one-shot background deadline that force-exits the process with code 71 if a shutdown
        /// that has already begun never finishes.
        ///
        /// This covers the case the container-stop triage cannot: a world thread that is healthy when
        /// the shutdown starts and hangs partway through it. By then the fast path has not been chosen,
        /// and ShutdownServer is sitting in one of its three unbounded waits with nothing to time it
        /// out. Docker's stop_grace_period would eventually SIGKILL, but a SIGKILL loses the shard queue
        /// drain, so exiting ourselves - after one more drain attempt - is strictly better.
        ///
        /// The deadline is derived from ShutdownInterval rather than hardcoded, so an admin
        /// "/shutdown 600" gets 600 + ShutdownHardDeadlineSeconds and is not cut off mid-countdown.
        ///
        /// It is a no-op on a normal shutdown for free: the thread is a background thread, so a normal
        /// Environment.Exit at the end of ShutdownServer tears it down before it can ever wake.
        /// </summary>
        private static void ArmShutdownDeadline()
        {
            var hardDeadlineSeconds = ConfigManager.Config.Server.ShutdownHardDeadlineSeconds;

            if (hardDeadlineSeconds <= 0)
            {
                log.Warn("ShutdownHardDeadlineSeconds is 0 - no hard shutdown deadline is armed. A world thread that hangs during shutdown will block the stop indefinitely.");
                return;
            }

            var generation = Interlocked.Increment(ref shutdownDeadlineGeneration);
            var waitSeconds = ShutdownInterval + (uint)hardDeadlineSeconds;

            var deadlineThread = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));

                // Superseded by a later arming, or by a cancel. Either way this deadline is not ours to
                // fire - see shutdownDeadlineGeneration.
                if (Volatile.Read(ref shutdownDeadlineGeneration) != generation)
                    return;

                // Cancelled outright and never re-armed.
                if (!ShutdownInitiated && !ShutdownInProgress)
                    return;

                log.Fatal($"HARD SHUTDOWN DEADLINE reached: {waitSeconds}s elapsed since shutdown began " +
                          $"(ShutdownInterval {ShutdownInterval}s + ShutdownHardDeadlineSeconds {hardDeadlineSeconds}s) and the shutdown never completed. " +
                          $"Stuck phase: {DescribeStuckShutdownPhase()}. Draining the shard database queue and force-exiting with code 71.");

                try
                {
                    DrainShardQueue(TimeSpan.FromSeconds(GetShutdownDrainSeconds()));
                    DatabaseManager.Stop();
                }
                catch (Exception ex)
                {
                    // Never let teardown trouble stop the force-exit: being stuck is the thing we are
                    // here to end.
                    log.Error("Hard shutdown deadline: shard drain / database stop failed, exiting anyway.", ex);
                }

                Environment.Exit(71);
            });

            deadlineThread.Name = "Shutdown Deadline";
            deadlineThread.IsBackground = true;
            deadlineThread.Start();
        }

        /// <summary>
        /// Best guess at which shutdown phase never completed, for the FATAL line above. Reads only
        /// counts, all of which are safe to read from another thread (the metrics scrape thread already
        /// reads two of them once a scrape).
        /// </summary>
        private static string DescribeStuckShutdownPhase()
        {
            try
            {
                if (!ShutdownInProgress)
                    return "the warned countdown never expired (ShutdownServer had not reached final shutdown)";

                var playerCount = PlayerManager.GetOnlineCount();
                if (playerCount > 0)
                    return $"waiting for {playerCount} player(s) to log off";

                var sessionCount = NetworkManager.GetAuthenticatedSessionCount();
                if (sessionCount > 0)
                    return $"waiting for {sessionCount} authenticated session(s) to disconnect";

                var landblockCount = LandblockManager.GetLoadedLandblocks().Count;
                if (landblockCount > 0)
                    return $"waiting for {landblockCount} landblock(s) to unload";

                if (WorldManager.WorldActive)
                    return "waiting for the world thread to stop";

                return $"draining the shard database queue ({DatabaseManager.Shard?.QueueCount ?? 0} pending)";
            }
            catch (Exception ex)
            {
                return $"undetermined ({ex.GetType().Name})";
            }
        }

        /// <summary>
        /// Waits for the shard database queue to drain, up to <paramref name="cap"/>.
        ///
        /// The cap is the point of this method. SerializedShardDatabase.Stop() calls CompleteAdding()
        /// and joins the worker, and the worker loop runs "while (!_queue.IsAddingCompleted)" - so Stop
        /// ABANDONS everything still queued rather than flushing it. This wait is therefore the only
        /// thing that actually saves outstanding shard work, on both the normal and the fast path, and
        /// it has to be bounded so that a queue that is not draining (a database that is down, say)
        /// cannot hold a container stop open until SIGKILL.
        /// </summary>
        private static void DrainShardQueue(TimeSpan cap)
        {
            // /netcapture dump writes run on the thread pool; give in-flight ones up to 5s so a stop does not lose them.
            // Non-throwing by its own contract.
            ACE.Server.Network.NetCapture.WaitForPendingDumps(TimeSpan.FromSeconds(5));

            // The vault audit writer is flushed HERE, and not beside some other manager's teardown,
            // because this method is the one piece of shutdown all three paths share - normal, fast and
            // the hard deadline - and an audit row that is never written is evidence an operator needed.
            // It does NOT go through SerializedShardDatabase (the account_vault* DAO deliberately
            // bypasses that queue), so it has to be drained separately, and it must run BEFORE the wait
            // below rather than after DatabaseManager.Stop. Bounded and non-throwing by its own
            // contract; guarded anyway, because nothing may stop a shutdown that is already in progress.
            try
            {
                AccountVaultAuditWriter.Shutdown();
            }
            catch (Exception ex)
            {
                log.Error("Flushing the account vault audit writer failed. Continuing the shutdown.", ex);
            }

            // Same reasoning: the Thread puzzle IP ledger persists fails and lockouts on its own background
            // chain, outside SerializedShardDatabase. Bounded and non-throwing by its own contract.
            ACE.Server.ThreadDungeons.ThreadPuzzleIpLedger.Shutdown(TimeSpan.FromSeconds(5));

            // Same reasoning: the analytics writer holds queued session logouts that would otherwise die with
            // the process. Bounded (a short join) and non-throwing; a no-op when analytics is off.
            try
            {
                ACE.Server.Managers.Analytics.AnalyticsManager.Shutdown();
            }
            catch (Exception ex)
            {
                log.Error("Stopping the analytics writer failed. Continuing the shutdown.", ex);
            }

            var shard = DatabaseManager.Shard;

            if (shard == null)
                return;

            var deadline = DateTime.UtcNow + cap;
            var logUpdateTS = DateTime.MinValue;
            int shardQueueCount;

            while ((shardQueueCount = shard.QueueCount) > 0)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    log.Error($"Shard database queue did not drain within {cap.TotalSeconds:0}s - abandoning {shardQueueCount} pending operation(s). " +
                              $"Stopping the shard database does not flush them, so this is unsaved shard state.");
                    return;
                }

                logUpdateTS = LogStatusUpdate(logUpdateTS, $"Waiting for database queue ({shardQueueCount}) to empty...");
                Thread.Sleep(10);
            }
        }

        /// <summary>
        /// ShutdownDrainSeconds, floored at 1. A configured 0 would make the drain a no-op and silently
        /// throw away queued shard writes on every shutdown, which is never what an operator means.
        /// </summary>
        private static int GetShutdownDrainSeconds()
        {
            return Math.Max(1, ConfigManager.Config.Server.ShutdownDrainSeconds);
        }

        /// <summary>
        /// Threaded task created when performing a server shutdown
        /// </summary>
        private static void ShutdownServer()
        {
            var shutdownTime = DateTime.UtcNow.AddSeconds(ShutdownInterval);

            ShutdownTime = shutdownTime;

            var lastNoticeTime = DateTime.UtcNow;

            // wait for shutdown interval to expire
            while (shutdownTime != DateTime.MinValue && shutdownTime >= DateTime.UtcNow)
            {
                // this allows the server shutdown to be canceled
                if (!ShutdownInitiated)
                {
                    // reset shutdown details
                    string shutdownText = $"The server shut down has been cancelled @ {DateTime.Now.ToCommonString()} ({DateTime.UtcNow.ToCommonString()} UTC)";
                    log.Info(shutdownText);

                    // special text
                    foreach (var player in PlayerManager.GetAllOnline())
                        player.Session.WorldBroadcast($"Broadcast from System> ATTENTION - This Asheron's Call Server shut down has been cancelled.");

                    // break function
                    return;
                }

                // Another runner (a repeat container stop signal that called DoShutdownNow, or /shutdown
                // racing a SIGTERM) may already be inside the final shutdown body below. If so, this
                // thread is stale: stop broadcasting notices and exit quietly instead of continuing to
                // wait on our own captured shutdownTime, which would otherwise eventually re-enter and
                // re-run the entire final shutdown a second time.
                if (ShutdownInProgress)
                    return;

                lastNoticeTime = NotifyPlayersOfPendingShutdown(lastNoticeTime, shutdownTime.AddSeconds(1));

                Thread.Sleep(10);
            }

            // Only one thread may ever proceed past this point into final shutdown. Two callers can
            // arrive here at nearly the same time (see finalShutdownStarted's doc comment above), so
            // guard the transition with a CAS: the loser returns immediately instead of duplicating
            // logoff / landblock unload / DatabaseManager.Stop / Environment.Exit.
            if (Interlocked.CompareExchange(ref finalShutdownStarted, 1, 0) != 0)
                return;

            ShutdownInProgress = true;

            PropertyManager.ResyncVariables();
            PropertyManager.StopUpdating();

            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                log.Debug("Logging off all players...");

                // logout each player
                foreach (var player in PlayerManager.GetAllOnline())
                    player.Session.LogOffPlayer(true);
            }));

            // Wait for all players to log out
            var logUpdateTS = DateTime.MinValue;
            int playerCount;
            var playerLogoffStart = DateTime.UtcNow;
            while ((playerCount = PlayerManager.GetOnlineCount()) > 0)
            {
                logUpdateTS = LogStatusUpdate(logUpdateTS, $"Waiting for {playerCount} player{(playerCount > 1 ? "s" : "")} to log off...");
                Thread.Sleep(10);
                if (playerCount > 0 && DateTime.UtcNow - playerLogoffStart > TimeSpan.FromMinutes(5))
                {
                    playerLogoffStart = DateTime.UtcNow;
                    log.WarnFormat("5 minute log off failsafe reached and there are {0} player{1} still online.", playerCount, (playerCount > 1 ? "s" : ""));
                    foreach (var player in PlayerManager.GetAllOnline())
                    {
                        log.WarnFormat("Player {0} (0x{1}) appears to be stuck in world and unable to log off normally. Requesting Forced Logoff...", player.Name, player.Guid);
                        player.ForcedLogOffRequested = true;
                        player.ForceLogoff();
                    }    
                }
            }

            WorldManager.EnqueueAction(new ActionEventDelegate(() =>
            {
                log.Debug("Disconnecting all sessions...");

                // disconnect each session
                NetworkManager.DisconnectAllSessionsForShutdown();
            }));

            // Wait for all sessions to drop out
            logUpdateTS = DateTime.MinValue;
            int sessionCount;
            while ((sessionCount = NetworkManager.GetAuthenticatedSessionCount()) > 0)
            {
                logUpdateTS = LogStatusUpdate(logUpdateTS, $"Waiting for {sessionCount} authenticated session{(sessionCount > 1 ? "s" : "")} to disconnect...");
                Thread.Sleep(10);
            }

            log.Debug("Adding all landblocks to destruction queue...");

            // Queue unloading of all the landblocks
            // The actual unloading will happen in WorldManager.UpdateGameWorld
            LandblockManager.AddAllActiveLandblocksToDestructionQueue();

            // Wait for all landblocks to unload
            logUpdateTS = DateTime.MinValue;
            int landblockCount;
            while ((landblockCount = LandblockManager.GetLoadedLandblocks().Count) > 0)
            {
                logUpdateTS = LogStatusUpdate(logUpdateTS, $"Waiting for {landblockCount} loaded landblock{(landblockCount > 1 ? "s" : "")} to unload...");
                Thread.Sleep(10);
            }

            log.Debug("Stopping world...");

            // Disabled thread update loop
            WorldManager.StopWorld();

            // Halt mods
            ModManager.Shutdown();

            // Wait for world to end
            logUpdateTS = DateTime.MinValue;
            while (WorldManager.WorldActive)
            {
                logUpdateTS = LogStatusUpdate(logUpdateTS, "Waiting for world to stop...");
                Thread.Sleep(10);
            }

            log.Info("Saving OfflinePlayers that have unsaved changes...");
            PlayerManager.SaveOfflinePlayersWithChanges();

            // Wait for the database queue to empty, bounded by ShutdownDrainSeconds. Shared with the
            // fast path so both shutdowns save the same work in the same way.
            DrainShardQueue(TimeSpan.FromSeconds(GetShutdownDrainSeconds()));

            // Write exit to console/log
            log.Info($"Exiting at {DateTime.UtcNow.ToCommonString()}");

            // System exit
            Environment.Exit(Environment.ExitCode);
        }

        private static DateTime LogStatusUpdate(DateTime logUpdateTS, string logMessage)
        {
            if (logUpdateTS == DateTime.MinValue || DateTime.UtcNow > logUpdateTS.ToUniversalTime())
            {
                log.Info(logMessage);
                logUpdateTS = DateTime.UtcNow.AddSeconds(10);
            }

            return logUpdateTS;
        }

        private static DateTime NotifyPlayersOfPendingShutdown(DateTime lastNoticeTime, DateTime shutdownTime)
        {
            var notify = false;

            var sdt = shutdownTime - DateTime.UtcNow;
                var timeHrs = $"{(sdt.Hours >= 1 ? $"{sdt.ToString("%h")}" : "")}{(sdt.Hours >= 2 ? $" hours" : sdt.Hours == 1 ? " hour" : "")}";
                var timeMins = $"{(sdt.Minutes != 0 ? $"{sdt.ToString("%m")}" : "")}{(sdt.Minutes >= 2 ? $" minutes" : sdt.Minutes == 1 ? " minute" : "")}";
                var timeSecs = $"{(sdt.Seconds != 0 ? $"{sdt.ToString("%s")}" : "")}{(sdt.Seconds >= 2 ? $" seconds" : sdt.Seconds == 1 ? " second" : "")}";
                var time = $"{(timeHrs != "" ? timeHrs : "")}{(timeMins != "" ? $"{((timeHrs != "") ? ", " : "")}" + timeMins : "")}{(timeSecs != "" ? $"{((timeHrs != "" || timeMins != "") ? " and " : "")}" + timeSecs : "")}";

            switch (time)
            {
                case "2 hours":
                case "1 hour":
                case "45 minutes":
                case "30 minutes":
                case "15 minutes":
                case "10 minutes":
                case "5 minutes":
                case "2 minutes":
                case "1 minute and 30 seconds":
                case "1 minute":
                case "30 seconds":
                case "15 seconds":
                case "10 seconds":
                case "5 seconds":
                    notify = true;
                    break;
            }

            // Console.WriteLine(time);

            if (notify && (DateTime.UtcNow - lastNoticeTime).TotalSeconds > 2)
            {
                foreach (var player in PlayerManager.GetAllOnline())
                    if (sdt.TotalSeconds > 10)
                        player.Session.WorldBroadcast($"Broadcast from System> {(sdt.TotalMinutes > 1.5 ? "ATTENTION" : "WARNING")} - This Asheron's Call Server will be shutting down in {time}{(sdt.TotalMinutes <= 1 ? "!" : ".")}{(sdt.TotalMinutes <= 3 ? $" Please log out{(sdt.TotalMinutes <= 1 ? "!" : ".")}" : "")}");
                    else
                        player.Session.WorldBroadcast($"Broadcast from System> ATTENTION - This Asheron's Call Server is shutting down NOW!!!!");

                return DateTime.UtcNow;
            }
            else
                return lastNoticeTime;
        }

        public static void StartupAbort()
        {
            ShutdownInitiated = true;
        }

        public static string ShutdownNoticeText()
        {
            var sdt = ShutdownTime - DateTime.UtcNow;

            var timeToShutdown = $"{(sdt.Hours > 0 ? $"{sdt.Hours} hour{(sdt.Hours > 1 ? "s" : "")}" : "")}";
            timeToShutdown += $"{(timeToShutdown.Length > 0 ? ", " : "")}{(sdt.Minutes > 0 ? $"{sdt.Minutes} minute{(sdt.Minutes > 1 ? "s" : "")}" : "")}";
            timeToShutdown += $"{(timeToShutdown.Length > 0 ? " and " : "")}{(sdt.Seconds > 0 ? $"{sdt.Seconds} second{(sdt.Seconds > 1 ? "s" : "")}" : "")}";

            if (sdt.TotalSeconds > 10)
               return $"Broadcast from System> {(sdt.TotalMinutes > 1.5 ? "ATTENTION" : "WARNING")} - This Asheron's Call Server will be shutting down in {timeToShutdown}{(sdt.TotalMinutes <= 1 ? "!" : ".")}{(sdt.TotalMinutes <= 3 ? $" Please log out{(sdt.TotalMinutes <= 1 ? "!" : ".")}" : "")}";
            else
               return $"Broadcast from System> ATTENTION - This Asheron's Call Server is shutting down NOW!!!!";
        }
    }
}
