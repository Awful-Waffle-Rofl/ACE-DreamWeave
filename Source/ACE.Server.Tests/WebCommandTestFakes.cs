using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Web;
using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Network;

namespace ACE.Server.Tests
{
    /// <summary>Records (what, managed thread id) in order, from any thread.</summary>
    internal sealed class WebCmdRecorder
    {
        private readonly object sync = new object();
        private readonly List<(string What, int Thread)> events = new List<(string What, int Thread)>();

        public void Add(string what)
        {
            lock (sync)
                events.Add((what, Environment.CurrentManagedThreadId));
        }

        public List<(string What, int Thread)> Snapshot()
        {
            lock (sync)
                return events.ToList();
        }
    }

    /// <summary>A command handler that counts, records and can block. Its delegate lives in the test assembly.</summary>
    internal sealed class WebCmdHandlerProbe
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);
        public bool SawNullSession;
        public Session LastSession;
        public string[] LastParameters;
        public int ThreadId;
        public WebCmdRecorder Recorder;
        public ManualResetEventSlim Gate;
        public Action<Session, string[]> Body;

        /// <summary>Set when the handler has been entered, before it blocks on Gate.</summary>
        public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);

        public CommandHandler Handler => (session, parameters) =>
        {
            Interlocked.Increment(ref calls);
            SawNullSession = session == null;
            LastSession = session;
            LastParameters = parameters;
            ThreadId = Environment.CurrentManagedThreadId;
            Recorder?.Add("invoke");
            Entered.Set();
            Gate?.Wait(10_000);
            Body?.Invoke(session, parameters);
        };
    }

    internal sealed class FakeWebCommandCatalog : ICommandCatalog
    {
        public readonly ConcurrentDictionary<string, CommandHandlerInfo> Entries = new ConcurrentDictionary<string, CommandHandlerInfo>(StringComparer.OrdinalIgnoreCase);

        private int lookupCalls;
        private int getCommandHandlerCalls;

        public int LookupCalls => Volatile.Read(ref lookupCalls);
        public int GetCommandHandlerCalls => Volatile.Read(ref getCommandHandlerCalls);

        /// <summary>When set, answers GetCommandHandler instead of the default (lookup plus the parameter rule).</summary>
        public Func<Session, string, string[], (CommandHandlerResponse Response, CommandHandlerInfo Info)> OnGetCommandHandler;

        public CommandHandlerInfo Add(CommandHandlerInfo info)
        {
            Entries[info.Attribute.Command] = info;
            return info;
        }

        public IReadOnlyList<CommandHandlerInfo> GetCommands() => Entries.Values.ToList();

        public bool TryGetCommandInfo(string name, out CommandHandlerInfo info)
        {
            Interlocked.Increment(ref lookupCalls);
            return Entries.TryGetValue(name, out info);
        }

        public CommandHandlerResponse GetCommandHandler(Session session, string command, string[] parameters, out CommandHandlerInfo info)
        {
            Interlocked.Increment(ref getCommandHandlerCalls);

            if (OnGetCommandHandler != null)
            {
                var answer = OnGetCommandHandler(session, command, parameters);
                info = answer.Info;
                return answer.Response;
            }

            if (!Entries.TryGetValue(command, out info))
                return CommandHandlerResponse.InvalidCommand;

            return CommandManager.HasEnoughParameters(info.Attribute, parameters) ? CommandHandlerResponse.Ok : CommandHandlerResponse.InvalidParameterCount;
        }
    }

    /// <summary>
    /// The world seam. By default each enqueued action runs at once on a new "world" thread; with
    /// HoldActions it waits in Held until RunHeld, which runs each on its own thread and joins it.
    /// </summary>
    internal sealed class FakeWebCommandWorld : IWebCommandWorld
    {
        private int findCalls;
        private int enqueueCalls;
        private int worldThreadId;

        public WebCmdRecorder Recorder;
        public WebCommandOnlineCharacter Character;
        public Exception FindThrows;
        public Exception PostAuditThrows;
        public volatile bool StillEligible = true;
        public volatile bool HoldActions;

        public readonly ConcurrentQueue<IAction> Held = new ConcurrentQueue<IAction>();
        public readonly List<(string Label, string Message)> AuditLines = new List<(string Label, string Message)>();
        public readonly List<(string Command, string[] Parameters, Session Session)> LogCalls = new List<(string Command, string[] Parameters, Session Session)>();

        public int FindCalls => Volatile.Read(ref findCalls);
        public int EnqueueCalls => Volatile.Read(ref enqueueCalls);
        public int WorldThreadId => Volatile.Read(ref worldThreadId);

        public WebCommandOnlineCharacter FindCharacter(uint accountId)
        {
            Interlocked.Increment(ref findCalls);

            if (FindThrows != null)
                throw FindThrows;

            return Character;
        }

        public bool IsStillEligible(WebCommandOnlineCharacter character) => StillEligible;

        public void EnqueueWorld(IAction action)
        {
            Interlocked.Increment(ref enqueueCalls);

            if (HoldActions)
                Held.Enqueue(action);
            else
                StartWorldThread(action);
        }

        public void RunHeld()
        {
            while (Held.TryDequeue(out var action))
                StartWorldThread(action).Join(10_000);
        }

        private Thread StartWorldThread(IAction action)
        {
            var thread = new Thread(() =>
            {
                Volatile.Write(ref worldThreadId, Environment.CurrentManagedThreadId);
                action.Act();
            })
            {
                IsBackground = true,
                Name = "fake world",
            };

            thread.Start();
            return thread;
        }

        public void LogCommandAudit(Session session, CommandHandlerInfo info, string[] parameters)
        {
            lock (LogCalls)
                LogCalls.Add((info.Attribute.Command, parameters, session));

            Recorder?.Add("log");
        }

        public void PostAudit(string actorLabel, string message)
        {
            if (PostAuditThrows != null)
                throw PostAuditThrows;

            lock (AuditLines)
                AuditLines.Add((actorLabel, message));

            Recorder?.Add("audit");
        }

        public List<(string Label, string Message)> Audits()
        {
            lock (AuditLines)
                return AuditLines.ToList();
        }

        public List<(string Command, string[] Parameters, Session Session)> Logs()
        {
            lock (LogCalls)
                return LogCalls.ToList();
        }
    }

    internal static class WebCmd
    {
        public const uint AccountId = 42;

        public static AdminPrincipal Admin(uint accountId = AccountId, string accountName = "weft") =>
            new AdminPrincipal(accountId, accountName) { Level = AccessLevel.Admin };

        public static CommandHandlerInfo Info(string name, CommandHandler handler, AccessLevel access = AccessLevel.Developer,
            CommandHandlerFlag flags = CommandHandlerFlag.None, int parameterCount = -1, bool includeRaw = false,
            string description = "A test command.", string usage = "<x>") => new CommandHandlerInfo
        {
            Attribute = new CommandHandlerAttribute(name, access, flags, includeRaw, parameterCount, description, usage),
            Handler = handler,
        };

        public static ResolvedCommand Resolved(string bucket, params string[] rowFlags) => new ResolvedCommand
        {
            Bucket = bucket,
            Capture = "captured",
            Source = CommandClassification.SourceServer,
            Row = new CommandClassificationRow
            {
                Command = "test",
                Handler = "test",
                Bucket = bucket,
                Basis = "override",
                Capture = "captured",
                Flags = rowFlags ?? Array.Empty<string>(),
                Note = "test row",
            },
            Note = "",
        };

        /// <summary>An identity-only Session: no constructor ran, so only reference comparisons are safe on it.</summary>
        public static Session BareSession() => (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));

        public static bool WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
        {
            var sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition())
                    return true;

                Thread.Sleep(10);
            }

            return condition();
        }
    }

    /// <summary>
    /// Seeds one PropertyManager cache entry and puts the prior entry (or its absence) back on Dispose.
    /// The caches are process-static and shared by every test class, so a key a test reads must be
    /// seeded by that test, never assumed from an earlier class.
    /// </summary>
    internal sealed class PropertyCacheSeed : IDisposable
    {
        private readonly Action restore;

        private PropertyCacheSeed(Action restore)
        {
            this.restore = restore;
        }

        public static PropertyCacheSeed Bool(string key, bool value)
        {
            var field = typeof(PropertyManager).GetField("CachedBooleanSettings", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
                throw new InvalidOperationException("PropertyManager.CachedBooleanSettings was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<string, ConfigurationEntry<bool>>)field.GetValue(null);
            var had = cache.TryGetValue(key, out var prior);

            cache[key] = new ConfigurationEntry<bool>(false, value, "test");

            return new PropertyCacheSeed(() =>
            {
                if (had)
                    cache[key] = prior;
                else
                    cache.TryRemove(key, out _);
            });
        }

        public void Dispose() => restore();
    }
}
