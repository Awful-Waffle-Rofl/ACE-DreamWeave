using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using log4net;

using ACE.Common.Extensions;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network;
using ACE.Server.Network.Enum;
using ACE.Server.Network.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Web
{
    /// <summary>The account's eligible online character, as found on the request thread.</summary>
    internal sealed class WebCommandOnlineCharacter
    {
        public WebCommandOnlineCharacter(Session session, Player player, string name)
        {
            Session = session;
            Player = player;
            Name = name;
        }

        public Session Session { get; }
        public Player Player { get; }
        public string Name { get; }
    }

    /// <summary>Everything the dispatcher needs from the running server, behind a seam so tests need no live Player, Session or world loop.</summary>
    internal interface IWebCommandWorld
    {
        /// <summary>A WorldConnected session with a Player that is not logging out, or null. May throw; the dispatcher answers server_error.</summary>
        WebCommandOnlineCharacter FindCharacter(uint accountId);

        /// <summary>World-thread re-validation: the same session still holds the same Player, WorldConnected, not logging out.</summary>
        bool IsStillEligible(WebCommandOnlineCharacter character);

        /// <summary>NetworkManager.InboundMessageQueue, the queue GameActionTalk runs from.</summary>
        void EnqueueWorld(IAction action);

        /// <summary>CommandManager.LogCommandAudit(session, info, parameters, false).</summary>
        void LogCommandAudit(Session session, CommandHandlerInfo info, string[] parameters);

        /// <summary>PlayerManager.BroadcastToAuditChannel(string actorLabel, string message).</summary>
        void PostAudit(string actorLabel, string message);
    }

    internal sealed class LiveWebCommandWorld : IWebCommandWorld
    {
        public static readonly LiveWebCommandWorld Instance = new LiveWebCommandWorld();

        private LiveWebCommandWorld()
        {
        }

        /// <summary>
        /// PLAN-P4.md section 2's eligibility rule, shared by GET /v1/admin/commands (the character line)
        /// and the run path. NetworkManager.Find uses SingleOrDefault, so two sessions for one account
        /// throw; that propagates to the caller.
        /// </summary>
        public WebCommandOnlineCharacter FindCharacter(uint accountId)
        {
            var session = NetworkManager.Find(accountId);

            if (session == null || session.State != SessionState.WorldConnected)
                return null;

            var player = session.Player;

            return player == null || player.IsLoggingOut ? null : new WebCommandOnlineCharacter(session, player, player.Name);
        }

        public bool IsStillEligible(WebCommandOnlineCharacter character)
        {
            var session = character?.Session;
            var player = character?.Player;

            return session != null && player != null
                && session.State == SessionState.WorldConnected
                && ReferenceEquals(session.Player, player)
                && !player.IsLoggingOut;
        }

        public void EnqueueWorld(IAction action) => NetworkManager.InboundMessageQueue.EnqueueAction(action);

        public void LogCommandAudit(Session session, CommandHandlerInfo info, string[] parameters) => CommandManager.LogCommandAudit(session, info, parameters, false);

        public void PostAudit(string actorLabel, string message) => PlayerManager.BroadcastToAuditChannel(actorLabel, message);
    }

    /// <summary>POST /v1/admin/commands/run's answer. Error None means 200 with the remaining fields; anything else is that error envelope.</summary>
    internal sealed class WebCommandOutcome
    {
        public MarketError Error { get; init; }

        /// <summary>Set only with <see cref="MarketError.CommandBusy"/>: the command holding the worker or the account's slot.</summary>
        public string BusyCommand { get; init; }

        public string Command { get; init; }
        public string Bucket { get; init; }
        public string Result { get; init; }
        public bool Ran { get; init; }
        public string CharacterName { get; init; }
        public long ElapsedMs { get; init; }
        public bool Truncated { get; init; }
        public IReadOnlyList<WebCommandOutputLine> Output { get; init; }
    }

    /// <summary>
    /// The single serial "Web Command" worker thread (PLAN-P4.md rulings R2, R8): one web-bucket command
    /// at a time, console-thread parity. Created once, with ExecutionContext flow suppressed, so the
    /// long-lived thread never inherits the constructing thread's AsyncLocal values. A background thread,
    /// so a command still running cannot keep the process alive at shutdown.
    /// </summary>
    internal sealed class WebCommandWorker : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private readonly object gate = new object();
        private readonly AutoResetEvent signal = new AutoResetEvent(false);
        private string busyCommand;
        private Action pendingWork;
        private Action pendingFinished;
        private bool stopping;

        public WebCommandWorker()
        {
            if (ExecutionContext.IsFlowSuppressed())
            {
                StartThread();
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                    StartThread();
            }
        }

        private void StartThread()
        {
            var thread = new Thread(Loop)
            {
                Name = "Web Command",
                IsBackground = true,
            };

            thread.Start();
        }

        /// <summary>Claims the worker for <paramref name="commandName"/>. False, naming the holder, when it is busy. <paramref name="finished"/> runs on the worker AFTER the claim is released, so the next run can start as soon as the caller hears the result.</summary>
        public bool TryStart(string commandName, Action work, Action finished, out string holdingCommand)
        {
            lock (gate)
            {
                if (stopping)
                {
                    holdingCommand = commandName;
                    return false;
                }

                if (busyCommand != null)
                {
                    holdingCommand = busyCommand;
                    return false;
                }

                busyCommand = commandName;
                pendingWork = work;
                pendingFinished = finished;
            }

            signal.Set();
            holdingCommand = null;
            return true;
        }

        private void Loop()
        {
            for (; ; )
            {
                signal.WaitOne();

                Action work;
                Action finished;

                lock (gate)
                {
                    if (stopping)
                        return;

                    work = pendingWork;
                    finished = pendingFinished;
                    pendingWork = null;
                    pendingFinished = null;
                }

                if (work == null)
                    continue;

                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    log.Error($"[WEBCMD] the web command worker's work threw: {ex.GetFullMessage()}");
                }

                lock (gate)
                    busyCommand = null;

                try
                {
                    finished?.Invoke();
                }
                catch (Exception ex)
                {
                    log.Error($"[WEBCMD] the web command worker's completion threw: {ex.GetFullMessage()}");
                }
            }
        }

        /// <summary>Stops the thread once any run in progress returns. The live server never calls this; tests do, so their workers do not accumulate.</summary>
        public void Dispose()
        {
            lock (gate)
                stopping = true;

            signal.Set();
        }
    }

    /// <summary>
    /// The run half of the web command console (PLAN-P4.md section 3.2, P4b). GET stays on
    /// WebCommandListService; both share the classification and the online-character rule.
    ///
    /// Order, mirroring the published status table: text rules, parse, name lookup, the ACCOUNT access
    /// check (before any bucket decision, character lookup or GetCommandHandler call), bucket, the
    /// per-account single flight and the worker, then dispatch. A refusal before the handler could run
    /// is an error envelope; once a run may have started, the answer is a result.
    ///
    /// Web bucket: the dedicated worker invokes the handler delegate with a null session - never
    /// through GetCommandHandler(null, ...), whose null-session branch skips authorization.
    /// In-game character bucket: GetCommandHandler(characterSession, ...) on the world thread through
    /// NetworkManager.InboundMessageQueue, exactly as GameActionTalk, and its NotAuthorized is honoured.
    /// </summary>
    internal sealed class WebCommandDispatcher : IDisposable
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const int MaxTextLength = 1000;

        public const string ResultOk = "ok";
        public const string ResultException = "exception";
        public const string ResultTimeout = "timeout";
        public const string ResultUsage = "usage";
        public const string ResultNotInWorld = "not_in_world";

        /// <summary>R5: the character bucket's GetCommandHandler answered NotAuthorized although the account passed.</summary>
        internal const string CharacterFlagsLagNote = "character flags do not permit this command; relog the character";

        private const int StateQueued = 0;
        private const int StateStarted = 1;
        private const int StateAbandoned = 2;

        private readonly ICommandCatalog catalog;
        private readonly Func<CommandHandlerInfo, ResolvedCommand> resolve;
        private readonly IWebCommandWorld world;
        private readonly Func<long> monotonicMs;
        private readonly int runTimeoutMs;
        private readonly WebCommandWorker worker;
        private readonly ConcurrentDictionary<uint, RunSlot> accountSlots = new ConcurrentDictionary<uint, RunSlot>();

        private int workerStartAttempts;

        public WebCommandDispatcher(ICommandCatalog catalog, CommandClassification classification, IWebCommandWorld world, Func<long> monotonicMs, int runTimeoutMs = 2500)
            : this(catalog, (classification ?? throw new ArgumentNullException(nameof(classification))).Resolve, world, monotonicMs, runTimeoutMs)
        {
        }

        /// <summary>Test seam: a resolver in place of the embedded classification, so a handler that lives in the test assembly can be classified web or in_game_character.</summary>
        internal WebCommandDispatcher(ICommandCatalog catalog, Func<CommandHandlerInfo, ResolvedCommand> resolve, IWebCommandWorld world, Func<long> monotonicMs, int runTimeoutMs)
        {
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            this.monotonicMs = monotonicMs ?? throw new ArgumentNullException(nameof(monotonicMs));

            if (runTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(runTimeoutMs), "the run timeout must be positive");

            this.runTimeoutMs = runTimeoutMs;

            // Created ONCE, here (PLAN-P4.md 3.2), under suppressed flow - see WebCommandWorker.
            worker = new WebCommandWorker();
        }

        /// <summary>Test seam: how many times a run tried to claim the worker.</summary>
        internal int WorkerStartAttempts => Volatile.Read(ref workerStartAttempts);

        /// <summary>Test seam: runs first inside the web bucket's execution on the worker, outside its try blocks, so a test can force a throw there. Never set by the server.</summary>
        internal Action TestHookBeforeWebExecute { get; set; }

        public void Dispose() => worker.Dispose();

        public WebCommandOutcome Run(AdminPrincipal principal, string text)
        {
            var startedMs = monotonicMs();
            var trace = new RunTrace();

            WebCommandOutcome outcome;

            try
            {
                outcome = RunCore(principal, text, startedMs, trace);
            }
            catch (Exception ex)
            {
                // Only reachable before any hand-off (every hand-off path catches its own), so nothing ran.
                log.Error($"[WEBCMD] run failed before dispatch for account {principal.AccountName} (id {principal.AccountId}): {ex.GetFullMessage()}");
                outcome = new WebCommandOutcome { Error = MarketError.ServerError };
            }

            var result = outcome.Error == MarketError.None ? outcome.Result : MarketErrorCodes.ToCode(outcome.Error);
            var paramText = trace.Command == null ? "" : CommandManager.FormatAuditParameters(trace.Command, trace.Parameters);

            log.Info($"[WEBCMD] account {principal.AccountName} (id {principal.AccountId}) @{trace.Command ?? "-"} {paramText}".TrimEnd()
                + $" bucket={trace.Bucket ?? "-"} result={result} elapsed_ms={monotonicMs() - startedMs}"
                + (trace.Note != null ? $" note={trace.Note}" : ""));

            return outcome;
        }

        private sealed class RunTrace
        {
            public string Command;
            public string[] Parameters;
            public string Bucket;
            public string Note;
        }

        /// <summary>The per-account single-flight claim. Released exactly once, by whichever path owns it.</summary>
        private sealed class RunSlot
        {
            private readonly ConcurrentDictionary<uint, RunSlot> owner;
            private readonly uint accountId;
            private int released;

            public RunSlot(ConcurrentDictionary<uint, RunSlot> owner, uint accountId, string command)
            {
                this.owner = owner;
                this.accountId = accountId;
                Command = command;
            }

            public string Command { get; }

            public void Release()
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                    owner.TryRemove(new KeyValuePair<uint, RunSlot>(accountId, this));
            }
        }

        /// <summary>
        /// One handed-off run. State is atomic: the executing thread moves Queued to Started, a waiter that
        /// gives up moves Queued to Abandoned, and exactly one of them wins - so command_not_started means
        /// guaranteed not run, and a lost race means it started (fixing the check-then-set of the Sheet's
        /// AbandonFlag, which is harmless for a projection but not for a command).
        /// </summary>
        private sealed class PendingRun
        {
            public int State;
            public RunResult Result;
            public readonly TaskCompletionSource<RunResult> Completion = new TaskCompletionSource<RunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>What happened on the executing thread.</summary>
        private sealed class RunResult
        {
            public MarketError Error;
            public string Result;
            public bool Ran;

            public static RunResult Refused(MarketError error) => new RunResult { Error = error };
            public static RunResult NotRun(string result) => new RunResult { Result = result };
            public static RunResult Invoked(string result) => new RunResult { Result = result, Ran = true };
        }

        private static WebCommandOutcome Refuse(MarketError error, string busyCommand = null) =>
            new WebCommandOutcome { Error = error, BusyCommand = busyCommand };

        /// <summary>
        /// The text reaches the Audit channel, [CMD_AUDIT], [WEBCMD] and the Discord relay verbatim, so
        /// anything that could forge a second line or visually reorder one is refused: control characters,
        /// invisible format characters (bidi overrides, zero-width), line and paragraph separators, and
        /// unpaired surrogates. A valid surrogate pair is judged by the code point it encodes.
        /// </summary>
        internal static bool IsValidText(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
                return false;

            for (var i = 0; i < text.Length;)
            {
                if (System.Text.Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
                    return false;

                switch (System.Text.Rune.GetUnicodeCategory(rune))
                {
                    case System.Globalization.UnicodeCategory.Control:
                    case System.Globalization.UnicodeCategory.Format:
                    case System.Globalization.UnicodeCategory.LineSeparator:
                    case System.Globalization.UnicodeCategory.ParagraphSeparator:
                        return false;
                }

                i += consumed;
            }

            return true;
        }

        private WebCommandOutcome RunCore(AdminPrincipal principal, string text, long startedMs, RunTrace trace)
        {
            // Row 8. ParseCommand throws on whitespace-only text, so this runs first.
            if (!IsValidText(text))
                return Refuse(MarketError.InvalidCommandText);

            string command;
            string[] parameters;

            try
            {
                CommandManager.ParseCommand(text, out command, out parameters);
            }
            catch (Exception)
            {
                return Refuse(MarketError.InvalidCommandText);
            }

            if (string.IsNullOrEmpty(command) || parameters == null)
                return Refuse(MarketError.InvalidCommandText);

            trace.Command = command;
            trace.Parameters = parameters;

            // Row 9. sudo is never a registered name, and is refused by name so a registration could not change that.
            if (string.Equals(command, "sudo", StringComparison.OrdinalIgnoreCase)
                || !catalog.TryGetCommandInfo(command, out var info)
                || info?.Attribute == null
                || info.Handler == null)
                return Refuse(MarketError.UnknownCommand);

            // Row 10: the ACCOUNT check, before the bucket is even looked at (invariant 4).
            if (info.Attribute.Access > principal.Level)
                return Refuse(MarketError.CommandNotPermitted);

            // Row 11.
            var resolved = resolve(info);
            var bucket = resolved?.Bucket;
            trace.Bucket = bucket;

            if (bucket != WebCommandBuckets.Web && bucket != WebCommandBuckets.InGameCharacter)
                return Refuse(MarketError.CommandInGameOnly);

            if (bucket == WebCommandBuckets.Web && (info.Attribute.Flags & CommandHandlerFlag.RequiresWorld) != 0)
            {
                log.Error($"[WEBCMD] '{info.Attribute.Command}' resolved to the web bucket but its live handler has RequiresWorld; refused as in_game_only. The classification or the resolver is wrong.");
                return Refuse(MarketError.CommandInGameOnly);
            }

            if (bucket == WebCommandBuckets.InGameCharacter && (info.Attribute.Flags & CommandHandlerFlag.ConsoleInvoke) != 0)
            {
                log.Error($"[WEBCMD] '{info.Attribute.Command}' resolved to the in_game_character bucket but its live handler has ConsoleInvoke; refused as in_game_only.");
                return Refuse(MarketError.CommandInGameOnly);
            }

            if (!(info.Handler is CommandHandler))
                return Refuse(MarketError.CommandInGameOnly);

            // Row 12, first half: one run per account.
            var name = info.Attribute.Command;
            var slot = new RunSlot(accountSlots, principal.AccountId, name);

            if (!accountSlots.TryAdd(principal.AccountId, slot))
            {
                if (accountSlots.TryGetValue(principal.AccountId, out var holding))
                    return Refuse(MarketError.CommandBusy, holding.Command);

                // The holder released between the failed claim and the lookup: claim again once, so a
                // slot that has just come free is not refused, and never name this run as its own holder.
                if (!accountSlots.TryAdd(principal.AccountId, slot))
                {
                    accountSlots.TryGetValue(principal.AccountId, out holding);
                    return Refuse(MarketError.CommandBusy, holding?.Command);
                }
            }

            var selfAudit = resolved.Row != null && resolved.Row.HasFlag(CommandClassification.FlagSelfAudit);

            try
            {
                return bucket == WebCommandBuckets.Web
                    ? RunWeb(principal, text, command, parameters, info, selfAudit, slot, startedMs)
                    : RunCharacter(principal, text, command, parameters, info, selfAudit, slot, startedMs, trace);
            }
            catch
            {
                // Every hand-off path releases the slot itself once it owns the run; this only covers a
                // throw before that point, and Release is idempotent.
                slot.Release();
                throw;
            }
        }

        // ---- web bucket ----

        private WebCommandOutcome RunWeb(AdminPrincipal principal, string text, string command, string[] parameters, CommandHandlerInfo info, bool selfAudit, RunSlot slot, long startedMs)
        {
            var ctx = new WebCommandContext(principal.AccountId, principal.AccountName, WebActorLabel(principal.AccountName), null, null);
            var run = new PendingRun();

            void Work()
            {
                if (Interlocked.CompareExchange(ref run.State, StateStarted, StateQueued) != StateQueued)
                    return;

                try
                {
                    run.Result = ExecuteWeb(ctx, text, command, parameters, info, selfAudit);
                }
                catch (Exception ex)
                {
                    // ExecuteWeb catches the handler's own throws; anything reaching here was thrown before Invoke.
                    log.Error($"[WEBCMD] the web-bucket run of @{info.Attribute.Command} threw before invoking: {ex.GetFullMessage()}");
                    run.Result = RunResult.Refused(MarketError.ServerError);
                }
            }

            void Finished()
            {
                slot.Release();
                run.Completion.TrySetResult(run.Result);
            }

            Interlocked.Increment(ref workerStartAttempts);

            // Row 12, second half: the single serial worker.
            if (!worker.TryStart(info.Attribute.Command, Work, Finished, out var holding))
            {
                slot.Release();
                return Refuse(MarketError.CommandBusy, holding);
            }

            return Await(run, ctx, slot, info, WebCommandBuckets.Web, null, startedMs);
        }

        /// <summary>On the worker thread.</summary>
        private RunResult ExecuteWeb(WebCommandContext ctx, string text, string command, string[] parameters, CommandHandlerInfo info, bool selfAudit)
        {
            TestHookBeforeWebExecute?.Invoke();

            if (!CommandManager.HasEnoughParameters(info.Attribute, parameters))
            {
                AddUsageLines(ctx, info, parameters);
                return RunResult.NotRun(ResultUsage);
            }

            using (WebCommandContext.Enter(ctx))
            {
                string[] invokeParameters;

                try
                {
                    world.LogCommandAudit(null, info, parameters);

                    invokeParameters = info.Attribute.IncludeRaw
                        ? CommandManager.StuffRawIntoParameters(RawForStuffing(text), command, parameters)
                        : parameters;

                    PostRunAudit(ctx, info, selfAudit, parameters);
                }
                catch (Exception ex)
                {
                    // Nothing was invoked: a command must not run without its audit line (the P3 announce rule).
                    log.Error($"[WEBCMD] audit failed before invoking @{info.Attribute.Command}; the command was not run: {ex.GetFullMessage()}");
                    return RunResult.Refused(MarketError.ServerError);
                }

                try
                {
                    ((CommandHandler)info.Handler).Invoke(null, invokeParameters);
                    return RunResult.Invoked(ResultOk);
                }
                catch (Exception ex)
                {
                    log.Error($"[WEBCMD] @{info.Attribute.Command} threw for web account {ctx.AccountName}", ex);
                    ctx.AddFinalLine(WebCommandContext.LevelError, ex.GetType().Name);
                    return RunResult.Invoked(ResultException);
                }
            }
        }

        // ---- in-game character bucket ----

        private WebCommandOutcome RunCharacter(AdminPrincipal principal, string text, string command, string[] parameters, CommandHandlerInfo info, bool selfAudit, RunSlot slot, long startedMs, RunTrace trace)
        {
            WebCommandOnlineCharacter character;

            try
            {
                character = world.FindCharacter(principal.AccountId);
            }
            catch (Exception ex)
            {
                slot.Release();
                log.Warn($"[WEBCMD] the online character lookup threw for account {principal.AccountId}: {ex.GetFullMessage()}");
                return Refuse(MarketError.ServerError);
            }

            // Row 13, before enqueue.
            if (character == null)
            {
                slot.Release();
                return Refuse(MarketError.NoCharacterOnline);
            }

            var ctx = new WebCommandContext(principal.AccountId, principal.AccountName, CharacterActorLabel(principal.AccountName, character.Name), character.Session, character.Player);
            var run = new PendingRun();

            try
            {
                world.EnqueueWorld(new ActionEventDelegate(() =>
                {
                    if (Interlocked.CompareExchange(ref run.State, StateStarted, StateQueued) != StateQueued)
                        return;

                    RunResult result = null;

                    try
                    {
                        result = ExecuteCharacter(ctx, character, text, command, parameters, info, selfAudit, trace);
                    }
                    catch (Exception ex)
                    {
                        // ActionQueue.RunActions swallows throws, so without this the waiter would only ever see a timeout.
                        log.Error($"[WEBCMD] the character-bucket action for @{info.Attribute.Command} threw: {ex.GetFullMessage()}");
                        result = RunResult.Refused(MarketError.ServerError);
                    }
                    finally
                    {
                        slot.Release();
                        run.Completion.TrySetResult(result ?? RunResult.Refused(MarketError.ServerError));
                    }
                }));
            }
            catch (Exception ex)
            {
                slot.Release();
                log.Error($"[WEBCMD] could not enqueue @{info.Attribute.Command} onto the world queue: {ex.GetFullMessage()}");
                return Refuse(MarketError.ServerError);
            }

            return Await(run, ctx, slot, info, WebCommandBuckets.InGameCharacter, character.Name, startedMs);
        }

        /// <summary>On the world thread, inside InboundMessageQueue's phase.</summary>
        private RunResult ExecuteCharacter(WebCommandContext ctx, WebCommandOnlineCharacter character, string text, string command, string[] parameters, CommandHandlerInfo info, bool selfAudit, RunTrace trace)
        {
            // Row 13, again: a logout that began after the request thread looked is caught here.
            if (!world.IsStillEligible(character))
                return RunResult.Refused(MarketError.NoCharacterOnline);

            var session = character.Session;

            using (WebCommandContext.Enter(ctx))
            {
                var response = catalog.GetCommandHandler(session, command, parameters, out var worldInfo);

                // TryAddCommand can replace the entry between classification and now: never run a handler
                // this run was not classified against (invariant 18).
                if (!ReferenceEquals(worldInfo, info))
                {
                    log.Warn($"[WEBCMD] the handler registered for '{command}' changed between classification and the world thread; refused as in_game_only.");
                    return RunResult.Refused(MarketError.CommandInGameOnly);
                }

                switch (response)
                {
                    case CommandHandlerResponse.Ok:
                        break;

                    case CommandHandlerResponse.NotInWorld:
                        return RunResult.NotRun(ResultNotInWorld);

                    case CommandHandlerResponse.InvalidParameterCount:
                        AddUsageLines(ctx, info, parameters);
                        return RunResult.NotRun(ResultUsage);

                    case CommandHandlerResponse.NotAuthorized:
                        // R5: both gates must allow. Written before the handoff completes, so Run's [WEBCMD] line carries it.
                        trace.Note = CharacterFlagsLagNote;
                        return RunResult.Refused(MarketError.CommandNotPermitted);

                    default:
                        return RunResult.Refused(MarketError.CommandInGameOnly);
                }

                string[] invokeParameters;

                try
                {
                    world.LogCommandAudit(session, info, parameters);

                    invokeParameters = info.Attribute.IncludeRaw
                        ? CommandManager.StuffRawIntoParameters(RawForStuffing(text), command, parameters)
                        : parameters;

                    PostRunAudit(ctx, info, selfAudit, parameters);
                }
                catch (Exception ex)
                {
                    log.Error($"[WEBCMD] audit failed before invoking @{info.Attribute.Command}; the command was not run: {ex.GetFullMessage()}");
                    return RunResult.Refused(MarketError.ServerError);
                }

                try
                {
                    ((CommandHandler)info.Handler).Invoke(session, invokeParameters);
                    return RunResult.Invoked(ResultOk);
                }
                catch (Exception ex)
                {
                    log.Error($"[WEBCMD] @{info.Attribute.Command} threw for web account {ctx.AccountName} via its character", ex);
                    ctx.AddFinalLine(WebCommandContext.LevelError, ex.GetType().Name);
                    return RunResult.Invoked(ResultException);
                }
            }
        }

        // ---- shared ----

        private WebCommandOutcome Await(PendingRun run, WebCommandContext ctx, RunSlot slot, CommandHandlerInfo info, string bucket, string characterName, long startedMs)
        {
            RunResult result = null;

            if (run.Completion.Task.Wait(runTimeoutMs))
            {
                // Completed with no result means the executing side failed without recording one: it did
                // not reach Invoke, so it is a refusal, never a "timeout, ran" answer.
                result = run.Completion.Task.Result ?? RunResult.Refused(MarketError.ServerError);
            }
            else if (Interlocked.CompareExchange(ref run.State, StateAbandoned, StateQueued) == StateQueued)
            {
                // Row 14: the executing thread will see Abandoned and return without running.
                slot.Release();
                return Refuse(MarketError.CommandNotStarted);
            }
            else if (run.Completion.Task.IsCompleted)
            {
                // It started, and finished in the instant between the wait expiring and the exchange.
                result = run.Completion.Task.Result ?? RunResult.Refused(MarketError.ServerError);
            }

            if (result == null)
            {
                // Started and still running: its effects may still land (never retry automatically). The
                // slot and the worker stay held until it actually finishes.
                var (partial, partialTruncated) = ctx.Snapshot();
                return Outcome(info, bucket, characterName, ResultTimeout, ran: true, partial, partialTruncated, startedMs);
            }

            if (result.Error != MarketError.None)
                return Refuse(result.Error);

            var (lines, truncated) = ctx.Snapshot();
            return Outcome(info, bucket, characterName, result.Result, result.Ran, lines, truncated, startedMs);
        }

        private WebCommandOutcome Outcome(CommandHandlerInfo info, string bucket, string characterName, string result, bool ran, List<WebCommandOutputLine> lines, bool truncated, long startedMs) => new WebCommandOutcome
        {
            Error = MarketError.None,
            Command = info.Attribute.Command,
            Bucket = bucket,
            Result = result,
            Ran = ran,
            CharacterName = characterName,
            ElapsedMs = monotonicMs() - startedMs,
            Truncated = truncated,
            Output = lines,
        };

        /// <summary>
        /// R4: exactly one Audit channel line per privileged run that reaches Invoke, posted immediately
        /// before Invoke on the invoking thread. A self_audit row (R11, the four modifyX commands) posts
        /// none, because its service line is the one line.
        /// </summary>
        private void PostRunAudit(WebCommandContext ctx, CommandHandlerInfo info, bool selfAudit, string[] parameters)
        {
            if (selfAudit || info.Attribute.Access <= ACE.Entity.Enum.AccessLevel.Player)
                return;

            world.PostAudit(ctx.ActorLabel, RunAuditMessage(info.Attribute.Command, parameters));
        }

        internal static string RunAuditMessage(string command, string[] parameters)
        {
            var paramText = CommandManager.FormatAuditParameters(command, parameters);
            return paramText.Length == 0
                ? $"ran @{command} via the web console"
                : $"ran @{command} {paramText} via the web console";
        }

        /// <summary>The web account name, exactly the actor label the Settings and Announce routes already use (PropertyAdminService.TryModifyWeb, the announce route).</summary>
        internal static string WebActorLabel(string accountName) => accountName ?? string.Empty;

        internal static string CharacterActorLabel(string accountName, string characterName) => $"{accountName} via {characterName}";

        /// <summary>The three lines GameActionTalk sends for InvalidParameterCount, byte for byte.</summary>
        internal static IReadOnlyList<string> UsageLines(CommandHandlerInfo info, string[] parameters) => new[]
        {
            $"Invalid parameter count, got {parameters.Length}, expected {info.Attribute.ParameterCount}!",
            $"@{info.Attribute.Command} - {info.Attribute.Description}",
            $"Usage: @{info.Attribute.Command} {info.Attribute.Usage}",
        };

        private static void AddUsageLines(WebCommandContext ctx, CommandHandlerInfo info, string[] parameters)
        {
            foreach (var line in UsageLines(info, parameters))
                ctx.AddOwnLine(WebCommandContext.LevelInfo, line);
        }

        /// <summary>GameActionTalk stuffs the message with its leading @ removed; the web text's @ or / is optional, so strip exactly one if present.</summary>
        internal static string RawForStuffing(string text)
        {
            var raw = text.TrimStart();
            return raw.StartsWith("@", StringComparison.Ordinal) || raw.StartsWith("/", StringComparison.Ordinal) ? raw.Substring(1) : raw;
        }
    }
}
