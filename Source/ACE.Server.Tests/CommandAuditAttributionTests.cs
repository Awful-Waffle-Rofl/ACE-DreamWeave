using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Entity.Enum;
using ACE.Server.Command;
using ACE.Server.Command.Web;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// [CMD_AUDIT], [WEBCMD] and Audit channel attribution for web console runs (PLAN-P4.md invariant 12),
    /// and that with no web context every one of them is byte-identical to today. The online roster is
    /// empty in this test environment, so only the log side of the Audit channel is observable.
    /// </summary>
    [TestClass]
    public class CommandAuditAttributionTests
    {
        private MemoryAppender appender;
        private Hierarchy hierarchy;
        private Level priorLevel;
        private bool priorConfigured;
        private List<IDisposable> seeds;

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

            // Every key the audit path reads, seeded here rather than assumed from another test class.
            seeds = new List<IDisposable>
            {
                PropertyCacheSeed.Bool("log_audit", true),
                PropertyCacheSeed.Bool("chat_log_audit", false),
                PropertyCacheSeed.Bool("discord_relay_enabled", false),
            };
        }

        [TestCleanup]
        public void Teardown()
        {
            hierarchy.Root.RemoveAppender(appender);
            hierarchy.Root.Level = priorLevel;
            hierarchy.Configured = priorConfigured;

            for (var i = seeds.Count - 1; i >= 0; i--)
                seeds[i].Dispose();
        }

        private List<string> Messages() => appender.GetEvents().Select(e => e.RenderedMessage).ToList();

        private static CommandHandlerInfo AdminInfo(string name) => WebCmd.Info(name, (s, p) => { }, AccessLevel.Admin);

        [TestMethod]
        public void LogCommandAudit_WebContext_NamesWebAccount()
        {
            using (WebCommandContext.Enter(new WebCommandContext(42, "weft", "weft", null, null)))
                CommandManager.LogCommandAudit(null, AdminInfo("boot"), new[] { "Someone" }, false);

            CollectionAssert.Contains(Messages(), "[CMD_AUDIT] web:weft (account id 42) [Admin] @boot Someone");
        }

        [TestMethod]
        public void LogCommandAudit_NoContext_ConsoleUnchanged()
        {
            CommandManager.LogCommandAudit(null, AdminInfo("boot"), new[] { "Someone" }, false);

            CollectionAssert.Contains(Messages(), "[CMD_AUDIT] <console> [Admin] @boot Someone");
        }

        [TestMethod]
        public void LogCommandAudit_RedactionUnchanged()
        {
            CommandManager.LogCommandAudit(null, AdminInfo("accountcreate"), new[] { "bob", "hunter2" }, false);

            CollectionAssert.Contains(Messages(), "[CMD_AUDIT] <console> [Admin] @accountcreate bob [redacted]");
        }

        [TestMethod]
        public void BroadcastToAuditChannel_NullIssuerInContext_UsesActorLabel()
        {
            using (WebCommandContext.Enter(new WebCommandContext(42, "weft", "weft", null, null)))
                PlayerManager.BroadcastToAuditChannel((Player)null, "msg");

            var messages = Messages();
            CollectionAssert.Contains(messages, "[AUDIT] weft: msg");
            CollectionAssert.DoesNotContain(messages, "[AUDIT] msg");
        }

        [TestMethod]
        public void BroadcastToAuditChannel_ActorPlayerInContext_UsesActorLabel()
        {
            // Identity only: the hook compares references and never touches a member of this Player.
            var actor = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            using (WebCommandContext.Enter(new WebCommandContext(42, "weft", "weft via +Weft", WebCmd.BareSession(), actor)))
                PlayerManager.BroadcastToAuditChannel(actor, "banned someone");

            CollectionAssert.Contains(Messages(), "[AUDIT] weft via +Weft: banned someone");
        }

        [TestMethod]
        public void BroadcastToAuditChannel_NoContext_ConsoleUnchanged()
        {
            PlayerManager.BroadcastToAuditChannel((Player)null, "msg");

            CollectionAssert.Contains(Messages(), "[AUDIT] msg");
        }

        [TestMethod]
        public void WebRun_EmitsWebcmdLine_Redacted()
        {
            var catalog = new FakeWebCommandCatalog();
            var info = catalog.Add(WebCmd.Info("accountcreate", (s, p) => { }, AccessLevel.Admin));
            var world = new FakeWebCommandWorld();

            using var dispatcher = new WebCommandDispatcher(catalog, i => WebCmd.Resolved(WebCommandBuckets.Web), world, () => Environment.TickCount64, 5000);

            var outcome = dispatcher.Run(WebCmd.Admin(), "@accountcreate bob hunter2");
            Assert.AreEqual("ok", outcome.Result);

            var line = Messages().Single(m => m.StartsWith("[WEBCMD] ", StringComparison.Ordinal));
            StringAssert.StartsWith(line, "[WEBCMD] account weft (id 42) @accountcreate bob [redacted] bucket=web result=ok elapsed_ms=");
            Assert.IsFalse(line.Contains("hunter2"));
        }

        [TestMethod]
        public void WebRun_Refusal_WebcmdLineCarriesErrorCode()
        {
            var catalog = new FakeWebCommandCatalog();
            using var dispatcher = new WebCommandDispatcher(catalog, i => WebCmd.Resolved(WebCommandBuckets.Web), new FakeWebCommandWorld(), () => Environment.TickCount64, 5000);

            Assert.AreEqual(MarketError.UnknownCommand, dispatcher.Run(WebCmd.Admin(), "nosuch x").Error);

            var line = Messages().Single(m => m.StartsWith("[WEBCMD] ", StringComparison.Ordinal));
            StringAssert.StartsWith(line, "[WEBCMD] account weft (id 42) @nosuch x bucket=- result=unknown_command elapsed_ms=");
        }

        /// <summary>
        /// R11 end to end on the P2 base: the real modifybool handler, the real classification (web,
        /// self_audit), the live [CMD_AUDIT] and Audit channel sinks. Exactly one [AUDIT] line, sent as
        /// the web label, carrying PropertyAdminService's text.
        /// </summary>
        [TestMethod]
        public void ModifyBool_FromWebConsole_ExactlyOneAuditLine()
        {
            using var debug = PropertyCacheSeed.Bool("chat_log_debug", false);

            var registered = CommandClassification.ReflectRegistered(typeof(CommandManager).Assembly.GetTypes()).Single(r => r.Command == "modifybool");
            var catalog = new FakeWebCommandCatalog();
            catalog.Add(new CommandHandlerInfo { Attribute = registered.Attribute, Handler = Delegate.CreateDelegate(typeof(CommandHandler), registered.Method) });

            using var dispatcher = new WebCommandDispatcher(catalog, CommandClassification.LoadEmbedded(), LiveWebCommandWorld.Instance, () => Environment.TickCount64, 5000);

            var outcome = dispatcher.Run(WebCmd.Admin(), "@modifybool chat_log_debug true");

            Assert.AreEqual(MarketError.None, outcome.Error);
            Assert.AreEqual("ok", outcome.Result);
            Assert.AreEqual("web", outcome.Bucket);
            Assert.IsTrue(outcome.Output.Any(o => o.Level == "info" && o.Text == "Bool property successfully updated!"), "the handler's output is captured");
            Assert.IsTrue(PropertyManager.GetBool("chat_log_debug").Item);

            var messages = Messages();
            var audits = messages.Where(m => m.StartsWith("[AUDIT] ", StringComparison.Ordinal)).ToList();

            Assert.AreEqual(1, audits.Count, $"exactly one audit line; got: {string.Join(" | ", audits)}");
            Assert.AreEqual("[AUDIT] weft: Successfully changed server bool property chat_log_debug to True", audits[0]);
            Assert.IsTrue(messages.Any(m => m.StartsWith("[CMD_AUDIT] web:weft (account id 42) [Admin] @modifybool chat_log_debug true", StringComparison.Ordinal)));
        }
    }
}
