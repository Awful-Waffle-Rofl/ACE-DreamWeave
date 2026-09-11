using System;
using System.IO;
using System.Text;
using System.Threading;

using log4net;

using ACE.Common;

namespace ACE.Server.Managers
{
    /// <summary>
    /// Watches the world-simulation thread from OUTSIDE it and reports whether it is alive.
    ///
    /// Why this exists. On 2026-09-01 the production world thread died to an unhandled exception. The
    /// .NET process stayed alive because the world thread is only one of several: SocketManager's
    /// listener threads kept the UDP port bound, so the container healthcheck kept reporting healthy,
    /// and the one gauge that could have shown the truth (ace.players.online) is served by the metrics
    /// scrape thread and reported its last value quite happily with a dead world behind it. Every login
    /// failed for 5h22m and nothing anywhere said so. Two things follow, and this class is both:
    ///
    ///  1. Something must observe the world thread on a thread that does not depend on it. That is the
    ///     poll loop below, and its two outputs - the heartbeat file (for a container healthcheck) and
    ///     the ace.world.* gauges (for Prometheus) - are the first signals that go unhealthy.
    ///  2. A dead world cannot recover on its own, so the process should stop pretending. That is the
    ///     self-exit, which hands the container runtime's restart policy something to act on.
    ///
    /// Nothing here opens a socket or hosts HTTP - see the standing rule in ServerMetrics' doc comment.
    /// The heartbeat file is the whole external interface, and its format is a hard contract with the
    /// container healthcheck script that reads it (see WriteHeartbeat).
    ///
    /// All of the policy lives in <see cref="Decide"/>, which is pure and takes primitives only, so the
    /// decision that can kill a production process is unit-testable with no running server. The poll
    /// loop below does nothing but gather inputs, call Decide, and publish the answer.
    /// </summary>
    public static class WorldWatchdog
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Health of the world-simulation thread. The integer values are part of the ace.world.status
        /// metric's contract and of the heartbeat file's state word - do not renumber them.
        /// </summary>
        public enum WorldState
        {
            /// <summary>The world thread has not ticked yet. Normal during LandblockManager.PreloadConfigLandblocks().</summary>
            Starting = 0,

            /// <summary>The world thread ticked recently.</summary>
            Ticking = 1,

            /// <summary>The world thread is alive but has not ticked for WorldWatchdogStallSeconds.</summary>
            Stalled = 2,

            /// <summary>The world thread faulted, or stopped ticking outside of a shutdown. It will not recover.</summary>
            Dead = 3
        }

        /// <summary>
        /// Fixed grace between first observing a dead world and exiting the process. NOT a knob, on
        /// purpose: its job is only to let the world thread's own log.Fatal line flush and to let at
        /// least one Prometheus scrape (15s in the standard sidecar config) observe the dead state, so
        /// the post-mortem has something to look at. Making it configurable would invite someone to set
        /// it to zero and lose exactly the evidence the outage needed.
        /// </summary>
        public const long DeadGraceMs = 20000;

        /// <summary>Environment variable naming the heartbeat file. The container sets it; local runs do not.</summary>
        public const string HeartbeatPathVariable = "ACE_HEARTBEAT_FILE";

        /// <summary>Last state published by the poll loop. Starting until the first poll runs.</summary>
        public static WorldState CurrentState { get; private set; } = WorldState.Starting;

        /// <summary>
        /// True once the poll thread has been started. False means CurrentState / StaleSeconds are
        /// frozen at their initial values and the heartbeat file is not being written - use
        /// <see cref="Classify"/> instead of the published state whenever a decision depends on it.
        /// </summary>
        public static bool WatchdogRunning { get; private set; }

        /// <summary>
        /// Whole seconds since the last world tick as of the last poll, or -1 when the world has never
        /// ticked. Published for the ace.world.heartbeat.age gauge and for shutdown triage logging.
        /// </summary>
        public static int StaleSeconds { get; private set; } = -1;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static string heartbeatPath;
        private static int exitOnStallMs;
        private static bool exitOnDeath;

        /// <summary>
        /// Cached stall threshold in ms. Resolved lazily by <see cref="GetStallMs"/> rather than only by
        /// Initialize, because Classify has to work with the watchdog turned off - and with it off,
        /// Initialize never runs. A zero here would make every world look stalled.
        /// </summary>
        private static int stallMsCache;

        private static int initialized;
        private static int exitLatched;

        /// <summary>
        /// Environment.TickCount64 when the Dead state was first observed, or 0 while not dead. Tracked
        /// here rather than inside Decide so Decide stays pure and clock-free.
        /// </summary>
        private static long deadSinceTicks;

        /// <summary>Last poll failure already logged, so a permanently failing heartbeat write logs once, not once a second.</summary>
        private static string lastLoggedPollError;

        /// <summary>
        /// Starts the watchdog thread. Call once, after WorldManager.Initialize() (so there is a world
        /// thread to watch) and before ServerMetrics.Initialize() (so the gauges have a state to read).
        /// Caller is responsible for the WorldWatchdogEnabled gate.
        /// </summary>
        public static void Initialize()
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
                return;

            var config = ConfigManager.Config.Server;

            heartbeatPath = ResolveHeartbeatPath();
            exitOnStallMs = config.WorldWatchdogExitOnStallSeconds <= 0 ? 0 : config.WorldWatchdogExitOnStallSeconds * 1000;
            exitOnDeath = config.WorldWatchdogExitOnDeath;

            log.Info($"World watchdog starting: heartbeat file {heartbeatPath}, stall threshold {GetStallMs() / 1000}s, " +
                     $"exit on death {exitOnDeath}, exit on stall {(exitOnStallMs == 0 ? "disabled" : $"{exitOnStallMs / 1000}s")}.");

            var thread = new Thread(PollLoop);
            thread.Name = "World Watchdog";
            thread.IsBackground = true;   // must never hold the process open by itself
            thread.Priority = ThreadPriority.Normal;
            thread.Start();

            WatchdogRunning = true;
        }

        /// <summary>
        /// Stall threshold in milliseconds, resolved from config on first use and cached. Deliberately
        /// not dependent on Initialize having run: <see cref="Classify"/> is called from the shutdown
        /// path, which must behave identically whether or not the watchdog is enabled. Falls back to the
        /// GameConfiguration default if the config cannot be read at all, because returning 0 here would
        /// classify every world as stalled.
        /// </summary>
        private static int GetStallMs()
        {
            var cached = stallMsCache;

            if (cached > 0)
                return cached;

            var seconds = 60;

            try
            {
                seconds = ConfigManager.Config.Server.WorldWatchdogStallSeconds;
            }
            catch (Exception ex)
            {
                log.Warn("Could not read WorldWatchdogStallSeconds, defaulting the stall threshold to 60s.", ex);
            }

            cached = Math.Max(1, seconds) * 1000;
            stallMsCache = cached;

            return cached;
        }

        /// <summary>
        /// Resolves the heartbeat file path: ACE_HEARTBEAT_FILE when set and non-empty, otherwise a
        /// temp-directory fallback so a plain local Windows run works with no environment set up.
        /// </summary>
        private static string ResolveHeartbeatPath()
        {
            string fromEnvironment = null;

            try
            {
                fromEnvironment = Environment.GetEnvironmentVariable(HeartbeatPathVariable);
            }
            catch (Exception ex)
            {
                // A restricted host can refuse environment reads. Fall back rather than fail to start.
                log.Warn($"Could not read {HeartbeatPathVariable}, falling back to the temp directory.", ex);
            }

            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return fromEnvironment.Trim();

            return Path.Combine(Path.GetTempPath(), "ace-world-heartbeat");
        }

        /// <summary>
        /// ALL of the watchdog's policy, as a pure function: no statics read, no clock read, no side
        /// effects. Every input is passed in so the whole decision table can be pinned by unit tests
        /// (WorldWatchdogTests) without a running server.
        ///
        /// The safety argument for shipping a process that can kill itself rests on two properties of
        /// this function, and both are pinned by tests:
        ///
        ///  * Elapsed staleness ALONE can never cause an exit. A stall exit requires the operator to
        ///    have set exitOnStallMs to a non-zero value; at the shipped default of 0 the staleness
        ///    timer can move the reported state but can never take the process down.
        ///  * A world that has not started yet can never be exited FOR BEING SLOW. Preloading config
        ///    landblocks legitimately runs for minutes with no tick, and killing a server mid-preload
        ///    would turn a slow start into a restart loop.
        ///
        /// The one case that does exit while everStarted is false is a world thread that FAULTED during
        /// preload: the catch block in WorldManager has already run, so the world is unambiguously dead
        /// and will never tick. That is a crash signal, not a slowness signal, and it is the case the
        /// second property above is written around rather than against.
        /// </summary>
        /// <param name="everStarted">WorldManager.WorldEverStarted - has a world tick ever run.</param>
        /// <param name="faulted">WorldManager.WorldThreadFaulted - did the world thread die to an exception.</param>
        /// <param name="worldActive">WorldManager.WorldActive.</param>
        /// <param name="shutdownInitiated">ServerManager.ShutdownInitiated - a normal shutdown also clears WorldActive.</param>
        /// <param name="staleMs">Milliseconds since the last world tick. Ignored when everStarted is false.</param>
        /// <param name="stallMs">Staleness at which the world is reported Stalled.</param>
        /// <param name="exitOnStallMs">Staleness at which a Stalled world is exited. 0 disables the stall exit.</param>
        /// <param name="deadForMs">Milliseconds the Dead state has been continuously observed.</param>
        /// <param name="exitOnDeath">Whether a dead world should exit the process.</param>
        /// <param name="enabled">Master switch. When false, shouldExit is false in every state.</param>
        /// <param name="shouldExit">Set true only when the process should take itself down.</param>
        public static WorldState Decide(bool everStarted, bool faulted, bool worldActive, bool shutdownInitiated,
            long staleMs, long stallMs, long exitOnStallMs, long deadForMs, bool exitOnDeath, bool enabled,
            out bool shouldExit)
        {
            // A normal shutdown clears WorldActive too, which is why shutdownInitiated is a term here
            // and not an afterthought: without it, every clean /shutdown would be classified as a crash
            // and the watchdog would race the real shutdown to exit the process.
            var dead = faulted || (everStarted && !worldActive && !shutdownInitiated);

            WorldState state;

            if (dead)
                state = WorldState.Dead;
            else if (!everStarted)
                state = WorldState.Starting;
            else if (worldActive && staleMs >= stallMs)
                state = WorldState.Stalled;
            else
                state = WorldState.Ticking;

            shouldExit = false;

            if (!enabled)
                return state;

            if (state == WorldState.Dead && exitOnDeath && deadForMs >= DeadGraceMs)
                shouldExit = true;
            else if (state == WorldState.Stalled && exitOnStallMs > 0 && staleMs >= exitOnStallMs)
                shouldExit = true;

            return state;
        }

        /// <summary>
        /// Reads the world thread's liveness state from the WorldManager statics. Shared by the poll
        /// loop and by <see cref="Classify"/> so there is exactly one place that knows which statics
        /// make up a sample.
        ///
        /// Every one of these is maintained by the world thread itself, unconditionally - none of them
        /// depends on the watchdog running.
        /// </summary>
        private static void Sample(out bool everStarted, out bool faulted, out bool worldActive,
            out bool shutdownInitiated, out long nowTicks, out long staleMs)
        {
            nowTicks = Environment.TickCount64;

            everStarted = WorldManager.WorldEverStarted;
            faulted = WorldManager.WorldThreadFaulted;
            worldActive = WorldManager.WorldActive;
            shutdownInitiated = ServerManager.ShutdownInitiated;

            // Staleness is meaningless before the first tick - the stamp is still 0 - so it is forced to
            // 0 here and reported as -1 by callers. Math.Max guards the one-tick window where the world
            // thread stamps between our two reads and TickCount64 appears to run backwards.
            staleMs = everStarted ? Math.Max(0, nowTicks - WorldManager.LastWorldTickTicks) : 0L;
        }

        /// <summary>
        /// Classifies the world thread's state RIGHT NOW, computed on the calling thread from the
        /// WorldManager statics, and independent of whether the watchdog is enabled or running.
        ///
        /// This is what a decision must use, as opposed to <see cref="CurrentState"/>, which is only
        /// published while the poll thread runs. The distinction is not cosmetic: the container-stop
        /// triage in Program.InitiateContainerShutdown is the fix for the 2026-09-01 outage, and if it
        /// read CurrentState then setting WorldWatchdogEnabled to false would silently revert it to the
        /// exact pre-fix behaviour - CurrentState frozen at Starting, the triage never taken, and the
        /// shutdown falling back through to the stale PlayerManager.GetOnlineCount() read that produced
        /// 34 ghost sessions and a countdown on a dead thread. A shutdown correctness fix must not be a
        /// subscriber to an optional subsystem.
        ///
        /// It routes through the same <see cref="Decide"/> the watchdog uses, rather than re-deriving
        /// the predicate, so the two can never drift apart. It passes enabled: false, which makes it
        /// structurally incapable of authorising a process exit: classification and exit policy are
        /// separate concerns and only the poll loop owns the second.
        /// </summary>
        /// <param name="staleSeconds">Whole seconds since the last world tick, or -1 if it never ticked.</param>
        public static WorldState Classify(out int staleSeconds)
        {
            Sample(out var everStarted, out var faulted, out var worldActive, out var shutdownInitiated,
                out _, out var staleMs);

            staleSeconds = everStarted ? (int)(staleMs / 1000) : -1;

            return Decide(everStarted, faulted, worldActive, shutdownInitiated, staleMs, GetStallMs(),
                exitOnStallMs: 0, deadForMs: 0, exitOnDeath: false, enabled: false, shouldExit: out _);
        }

        private static void PollLoop()
        {
            while (true)
            {
                try
                {
                    Poll();
                }
                catch (Exception ex)
                {
                    // Fail safe toward "unhealthy", never toward "exit": a poll that throws publishes
                    // nothing and exits nothing, it just leaves the last state standing and stops
                    // advancing the heartbeat file - which is exactly what the healthcheck reads as
                    // unhealthy. Log at most once per distinct failure so a heartbeat file that can
                    // never be written (read-only mount, wrong path) does not emit a line every second
                    // for the life of the process.
                    var key = ex.GetType().FullName + ": " + ex.Message;

                    if (!string.Equals(key, lastLoggedPollError, StringComparison.Ordinal))
                    {
                        lastLoggedPollError = key;
                        log.Error("World watchdog poll failed. Further identical failures will not be logged.", ex);
                    }
                }

                Thread.Sleep(1000);
            }
        }

        private static void Poll()
        {
            Sample(out var everStarted, out var faulted, out var worldActive, out var shutdownInitiated,
                out var now, out var staleMs);

            var stallMs = GetStallMs();

            // Age of the Dead observation, measured from the previous poll's verdict so that Decide
            // itself never reads a clock. The first poll that sees Dead therefore reports 0, and the
            // grace starts from there.
            var deadForMs = deadSinceTicks == 0 ? 0L : now - deadSinceTicks;

            var state = Decide(everStarted, faulted, worldActive, shutdownInitiated, staleMs, stallMs,
                exitOnStallMs, deadForMs, exitOnDeath, enabled: true, shouldExit: out var shouldExit);

            if (state == WorldState.Dead)
            {
                if (deadSinceTicks == 0)
                {
                    deadSinceTicks = now;
                    log.Fatal($"World watchdog: the world thread is DEAD (faulted={faulted}, worldActive={worldActive}, " +
                              $"shutdownInitiated={shutdownInitiated}). The process is still bound to its port but cannot " +
                              $"serve players. Exit on death is {(exitOnDeath ? $"enabled - exiting in {DeadGraceMs / 1000}s" : "disabled")}.");
                }
            }
            else
            {
                deadSinceTicks = 0;
            }

            var staleSeconds = everStarted ? (int)(staleMs / 1000) : -1;

            if (state == WorldState.Stalled && CurrentState != WorldState.Stalled)
                log.Warn($"World watchdog: the world thread has not ticked for {staleSeconds}s (stall threshold {stallMs / 1000}s).");
            else if (state == WorldState.Ticking && CurrentState == WorldState.Stalled)
                log.Info("World watchdog: the world thread is ticking again.");

            CurrentState = state;
            StaleSeconds = staleSeconds;

            // Written on EVERY poll in EVERY state, including Starting: the reader proves this process
            // is alive from the timestamp field, so it has to keep advancing through a long preload.
            WriteHeartbeat(state, staleSeconds);

            if (shouldExit && Interlocked.CompareExchange(ref exitLatched, 1, 0) == 0)
            {
                var reason = state == WorldState.Dead
                    ? $"World watchdog: world thread dead for {deadForMs / 1000}s"
                    : $"World watchdog: world thread stalled for {staleSeconds}s";

                Program.InitiateWatchdogShutdown(reason);
            }
        }

        /// <summary>
        /// Writes the heartbeat file. HARD INTERFACE - the container healthcheck script parses this, so
        /// the format is not free to drift:
        ///
        ///   one LF-terminated line, three space-separated fields:
        ///   "&lt;state&gt; &lt;watchdogUnixSeconds&gt; &lt;worldStaleSeconds&gt;"
        ///   e.g. "ticking 1756702800 0"
        ///
        ///  * state is the lowercase state word: starting | ticking | stalled | dead.
        ///  * watchdogUnixSeconds is UTC seconds at write time. The reader uses it to prove the WATCHDOG
        ///    is alive, independently of what it says about the world.
        ///  * worldStaleSeconds is whole seconds since the last world tick, or -1 if it never ticked.
        ///
        /// Written to a sibling .tmp and moved into place, because a reader polling once a second will
        /// eventually catch a plain rewrite mid-flight and parse a torn line. On the same filesystem the
        /// move is atomic, so a reader sees either the old line or the new one and never half of each.
        /// Explicit "\n" and a BOM-less UTF-8 encoder, not Environment.NewLine and not the default
        /// encoder: the file is written on Windows during development and read by a POSIX shell in the
        /// container, and both a CR and a BOM would end up inside a shell-parsed field.
        /// </summary>
        private static void WriteHeartbeat(WorldState state, int staleSeconds)
        {
            var line = $"{StateWord(state)} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {staleSeconds}\n";

            var tempPath = heartbeatPath + ".tmp";

            File.WriteAllText(tempPath, line, Utf8NoBom);
            File.Move(tempPath, heartbeatPath, true);
        }

        /// <summary>
        /// Lowercase state word for the heartbeat file. Spelled out rather than ToString().ToLower() so
        /// that renaming an enum member cannot silently change a wire format another process parses.
        /// </summary>
        private static string StateWord(WorldState state)
        {
            switch (state)
            {
                case WorldState.Starting: return "starting";
                case WorldState.Ticking: return "ticking";
                case WorldState.Stalled: return "stalled";
                case WorldState.Dead: return "dead";
                default: return "starting";
            }
        }
    }
}
