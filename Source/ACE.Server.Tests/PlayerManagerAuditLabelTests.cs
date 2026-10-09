using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PLAN-P2.md section 2/5: PlayerManager.BroadcastToAuditChannel(string, string) and
    /// BroadcastToChannelFromLabel must produce the same [AUDIT]/[CHAT] log shape as the existing
    /// Player and console overloads, just with the web account name (or "CONSOLE") as the label.
    /// The online roster is empty in this test environment (PlayerManager.cs:31-33), so every send
    /// loop iterates zero players - only the log side is asserted here.
    /// </summary>
    [TestClass]
    public class PlayerManagerAuditLabelTests
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

            // BroadcastToAuditChannel reads PropertyManager.GetBool("log_audit", true)
            // (PlayerManager.cs ~:795, ~:815). log_audit is NOT a DefaultBooleanProperties key, so
            // ModifyBool refuses it - seeded here via the restore-capable helper instead, the same
            // trap every uncached-key test hits with no live shard database.
            seeds = new List<IDisposable>
            {
                PropertyCacheSeed.Bool("chat_log_audit", true),
                PropertyCacheSeed.Bool("discord_relay_enabled", false),
                PropertyCacheSeed.Bool("log_audit", true),
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

        [TestMethod]
        public void BroadcastToAuditChannel_LabelOverload_LogsAuditLineWithLabel()
        {
            PlayerManager.BroadcastToAuditChannel("weft", "msg");

            Assert.IsTrue(Messages().Any(m => m == "[AUDIT] weft: msg"));
        }

        [TestMethod]
        public void BroadcastToAuditChannel_PlayerOverload_NullIssuer_Unchanged()
        {
            PlayerManager.BroadcastToAuditChannel((Player)null, "msg");

            Assert.IsTrue(Messages().Any(m => m == "[AUDIT] msg"));
        }

        [TestMethod]
        public void BroadcastToChannelFromLabel_ChatLogNamesLabel()
        {
            PropertyManager.ModifyBool("chat_log_audit", true);

            PlayerManager.BroadcastToChannelFromLabel(Channel.Audit, "weft", "msg");

            Assert.IsTrue(Messages().Any(m => m == "[CHAT][AUDIT] weft says on the Audit channel, \"msg\""));
        }

        [TestMethod]
        public void BroadcastToChannelFromConsole_ChatLogStillSystem()
        {
            PropertyManager.ModifyBool("chat_log_audit", true);

            PlayerManager.BroadcastToChannelFromConsole(Channel.Audit, "msg");

            Assert.IsTrue(Messages().Any(m => m == "[CHAT][AUDIT] [SYSTEM] says on the Audit channel, \"msg\""));
        }
    }
}
