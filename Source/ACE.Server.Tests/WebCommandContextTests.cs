using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Entity.Enum;
using ACE.Server.Command.Handlers;
using ACE.Server.Command.Web;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.Network.GameMessages;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>WebCommandContext and its capture chokepoints (PLAN-P4.md sections 3.1, 3.3 and 7).</summary>
    [TestClass]
    public class WebCommandContextTests
    {
        private MemoryAppender appender;
        private Hierarchy hierarchy;
        private Level priorLevel;
        private bool priorConfigured;

        [TestInitialize]
        public void Setup()
        {
            appender = new MemoryAppender();
            hierarchy = (Hierarchy)LogManager.GetRepository(typeof(PlayerManager).Assembly);
            priorLevel = hierarchy.Root.Level;
            priorConfigured = hierarchy.Configured;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.All;
            hierarchy.Configured = true;
        }

        [TestCleanup]
        public void Teardown()
        {
            hierarchy.Root.RemoveAppender(appender);
            hierarchy.Root.Level = priorLevel;
            hierarchy.Configured = priorConfigured;

            Assert.AreEqual(0, WebCommandContext.ActiveCount, "a test left a web command context entered");
        }

        private List<string> Messages() => appender.GetEvents().Select(e => e.RenderedMessage).ToList();

        private static WebCommandContext Web(Session session = null, string label = "weft") => new WebCommandContext(42, "weft", label, session, null);

        /// <summary>
        /// A Session whose NetworkSession can really run EnqueueSend: no socket, only the fields that
        /// method touches (the owning session and the per-group bundle locks and bundles).
        /// </summary>
        private static Session SessionWithNetwork()
        {
            const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

            var session = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
            var network = (NetworkSession)RuntimeHelpers.GetUninitializedObject(typeof(NetworkSession));

            var count = (int)GameMessageGroup.QueueMax;
            var locks = new object[count];
            var bundles = new NetworkBundle[count];

            for (var i = 0; i < count; i++)
            {
                locks[i] = new object();
                bundles[i] = new NetworkBundle();
            }

            typeof(NetworkSession).GetField("session", Instance).SetValue(network, session);
            typeof(NetworkSession).GetField("currentBundleLocks", Instance).SetValue(network, locks);
            typeof(NetworkSession).GetField("currentBundles", Instance).SetValue(network, bundles);

            session.Network = network;
            return session;
        }

        [TestMethod]
        public void WriteOutputInfo_NullSession_CapturedAndStillLogged()
        {
            var ctx = Web();

            using (WebCommandContext.Enter(ctx))
            {
                CommandHandlerHelper.WriteOutputInfo(null, "info line");
                CommandHandlerHelper.WriteOutputDebug(null, "debug line");
                CommandHandlerHelper.WriteOutputError(null, "error line");
            }

            var (lines, truncated) = ctx.Snapshot();

            Assert.IsFalse(truncated);
            CollectionAssert.AreEqual(new[] { "info:info line", "debug:debug line", "error:error line" }, lines.Select(l => $"{l.Level}:{l.Text}").ToList());

            var messages = Messages();
            CollectionAssert.IsSubsetOf(new[] { "info line", "debug line", "error line" }, messages, "capture must not replace the log output");
        }

        [TestMethod]
        public void WriteOutputInfo_NoContext_NotCaptured_StillLogged()
        {
            CommandHandlerHelper.WriteOutputInfo(null, "console only");

            Assert.IsTrue(Messages().Contains("console only"));
            Assert.AreEqual(0, WebCommandContext.ActiveCount);
        }

        [TestMethod]
        public void EnqueueSend_SystemChat_OwnSessionCaptured_OtherSessionNot()
        {
            // GameMessage's writer encodes cp1252, which Program.cs registers at startup. Registered here
            // too, because a class run alone must not depend on another test class having done it first.
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var mine = SessionWithNetwork();
            var theirs = SessionWithNetwork();
            var ctx = new WebCommandContext(42, "weft", "weft via +Weft", mine, null);
            var enumerations = 0;

            IEnumerable<GameMessage> Lazy()
            {
                enumerations++;
                yield return new GameMessageSystemChat("mine, lazy", ChatMessageType.Broadcast);
            }

            using (WebCommandContext.Enter(ctx))
            {
                mine.Network.EnqueueSend(new GameMessageSystemChat("mine", ChatMessageType.Broadcast));
                theirs.Network.EnqueueSend(new GameMessageSystemChat("theirs", ChatMessageType.Broadcast));
                mine.Network.EnqueueSend(Lazy());

                // A web-bucket (null session) write is not addressed to this context's session.
                CommandHandlerHelper.WriteOutputInfo(null, "console line");
            }

            var (lines, _) = ctx.Snapshot();

            CollectionAssert.AreEqual(new[] { "chat:mine", "chat:mine, lazy" }, lines.Select(l => $"{l.Level}:{l.Text}").ToList());
            Assert.AreEqual(1, enumerations, "the IEnumerable overload must enumerate its sequence once");
        }

        [TestMethod]
        public void ContextDoesNotLeakAcrossConcurrentRequests()
        {
            const int Runs = 16;
            var barrier = new Barrier(Runs);
            var contexts = Enumerable.Range(0, Runs).Select(i => new WebCommandContext((uint)i, $"acct{i}", $"acct{i}", null, null)).ToArray();

            var tasks = Enumerable.Range(0, Runs).Select(i => Task.Factory.StartNew(() =>
            {
                using (WebCommandContext.Enter(contexts[i]))
                {
                    barrier.SignalAndWait(10_000);

                    for (var n = 0; n < 20; n++)
                    {
                        WebCommandContext.TryCapture(null, WebCommandContext.LevelInfo, $"run{i} line{n}");
                        Thread.Yield();
                    }
                }
            }, TaskCreationOptions.LongRunning)).ToArray();

            Assert.IsTrue(Task.WaitAll(tasks, 30_000));

            for (var i = 0; i < Runs; i++)
            {
                var (lines, _) = contexts[i].Snapshot();
                CollectionAssert.AreEqual(Enumerable.Range(0, 20).Select(n => $"run{i} line{n}").ToList(), lines.Select(l => l.Text).ToList(), $"run {i} holds exactly its own lines");
            }
        }

        [TestMethod]
        public void ContextClearedAfterRun_SameThread()
        {
            var ctx = Web();

            void EnterAndLeaveInsideAMethod()
            {
                using (WebCommandContext.Enter(ctx))
                    Assert.AreSame(ctx, WebCommandContext.Current);
            }

            EnterAndLeaveInsideAMethod();

            Assert.IsNull(WebCommandContext.Current, "an AsyncLocal set in a synchronous method persists on the thread unless the scope clears it");
            Assert.IsTrue(ctx.IsSealed);
            Assert.IsFalse(WebCommandContext.TryCapture(null, WebCommandContext.LevelInfo, "after"));
        }

        [TestMethod]
        public void LateWriteAfterSeal_NotCaptured()
        {
            var ctx = Web();
            ExecutionContext captured;

            using (WebCommandContext.Enter(ctx))
            {
                // What a timer, task or thread started by the handler would carry.
                captured = ExecutionContext.Capture();
                CommandHandlerHelper.WriteOutputInfo(null, "during");
            }

            ExecutionContext.Run(captured, _ => CommandHandlerHelper.WriteOutputInfo(null, "late"), null);

            var (lines, _) = ctx.Snapshot();
            CollectionAssert.AreEqual(new[] { "during" }, lines.Select(l => l.Text).ToList());
            Assert.IsTrue(Messages().Contains("late"), "a late write still reaches the log");
        }

        [TestMethod]
        public void ActiveCountZero_WhenIdle()
        {
            Assert.AreEqual(0, WebCommandContext.ActiveCount);
            Assert.IsFalse(WebCommandContext.IsCapturing);

            using (WebCommandContext.Enter(Web()))
            {
                Assert.AreEqual(1, WebCommandContext.ActiveCount);
                Assert.IsTrue(WebCommandContext.IsCapturing);
            }

            Assert.AreEqual(0, WebCommandContext.ActiveCount);
            Assert.IsFalse(WebCommandContext.IsCapturing);
        }

        [TestMethod]
        public void Enter_Twice_Throws()
        {
            var ctx = Web();

            using (WebCommandContext.Enter(ctx))
                Assert.ThrowsExactly<InvalidOperationException>(() => WebCommandContext.Enter(ctx));
        }

        [TestMethod]
        public void OutputCap_500Lines_Truncated()
        {
            var ctx = Web();

            using (WebCommandContext.Enter(ctx))
            {
                for (var i = 0; i < WebCommandContext.MaxLines + 1; i++)
                    WebCommandContext.TryCapture(null, WebCommandContext.LevelInfo, $"line {i}");
            }

            var (lines, truncated) = ctx.Snapshot();
            Assert.AreEqual(500, lines.Count);
            Assert.IsTrue(truncated);
            Assert.AreEqual("line 499", lines.Last().Text);

            // An exception line still ends the output, and the cap still holds.
            ctx.AddFinalLine(WebCommandContext.LevelError, "NullReferenceException");
            var (after, _) = ctx.Snapshot();
            Assert.AreEqual(500, after.Count);
            Assert.AreEqual("NullReferenceException", after.Last().Text);
        }

        [TestMethod]
        public void OutputCap_64KB_Truncated()
        {
            var ctx = Web();
            var kilobyte = new string('x', 1024);

            using (WebCommandContext.Enter(ctx))
            {
                for (var i = 0; i < 70; i++)
                    WebCommandContext.TryCapture(null, WebCommandContext.LevelInfo, kilobyte);
            }

            var (lines, truncated) = ctx.Snapshot();
            Assert.AreEqual(64, lines.Count);
            Assert.IsTrue(truncated);
        }

        [TestMethod]
        public void Current_NullOnThreadStartedDuringRun_AfterSeal()
        {
            using var logAudit = PropertyCacheSeed.Bool("log_audit", true);
            using var chatLogAudit = PropertyCacheSeed.Bool("chat_log_audit", false);
            using var discord = PropertyCacheSeed.Bool("discord_relay_enabled", false);

            var ctx = Web();
            var gate = new ManualResetEventSlim(false);
            object seen = "unset";
            Thread late;

            using (WebCommandContext.Enter(ctx))
            {
                // The ServerManager "Shutdown Server" / deadline thread shape: started during the run, runs after it.
                late = new Thread(() =>
                {
                    gate.Wait(10_000);
                    seen = WebCommandContext.Current;
                    CommandHandlerHelper.WriteOutputInfo(null, "late output");
                    PlayerManager.BroadcastToAuditChannel((Player)null, "late audit");
                });
                late.Start();
            }

            gate.Set();
            Assert.IsTrue(late.Join(10_000));

            Assert.IsNull(seen);
            Assert.AreEqual(0, ctx.Snapshot().Lines.Count, "a sealed context captures nothing");

            var messages = Messages();
            Assert.IsTrue(messages.Contains("late output"), "WriteOutputInfo falls back to the log");
            Assert.IsTrue(messages.Contains("[AUDIT] late audit"), "the audit line falls back to today's CONSOLE form");
            Assert.IsFalse(messages.Any(m => m.StartsWith("[AUDIT] weft", StringComparison.Ordinal)), "and is not attributed to the web account");
        }
    }
}
