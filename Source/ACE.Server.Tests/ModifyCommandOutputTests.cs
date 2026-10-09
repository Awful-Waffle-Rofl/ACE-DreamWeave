using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Characterization tests for /modifybool, /modifylong, /modifydouble, /modifystring
    /// (PLAN-P2.md section 5). These were landed FIRST and proven green on unmodified 911bc6220 code
    /// before PropertyAdminService existed, and they still exercise the exact same handlers (now thin
    /// wrappers around PropertyAdminService.TryModify) with a null Session, so output goes through
    /// log.Info (CommandHandlerHelper.WriteOutputInfo) and lands in this MemoryAppender.
    /// </summary>
    [TestClass]
    public class ModifyCommandOutputTests
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
            hierarchy = (Hierarchy)LogManager.GetRepository(typeof(PropertyManager).Assembly);
            priorLevel = hierarchy.Root.Level;
            priorConfigured = hierarchy.Configured;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.All;
            hierarchy.Configured = true;

            // log_audit is not a DefaultBooleanProperties key, so ModifyBool refuses it (PropertyManager.cs:124)
            // - seeded here via the restore-capable helper instead, the same way tests reach every
            // uncached key with no live shard database.
            seeds = new List<IDisposable>
            {
                PropertyCacheSeed.Bool("chat_log_audit", false),
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

        // ---- bool ----

        [TestMethod]
        public void ModifyBool_Success_OutputThenAuditLine()
        {
            // Seed the cache first: GetBool on an uncached key falls through to
            // DatabaseManager.ShardConfig, which is null (NREs) with no live shard database.
            PropertyManager.ModifyBool("chat_log_debug", false);
            var prior = PropertyManager.GetBool("chat_log_debug").Item;
            try
            {
                AdminCommands.HandleModifyServerBoolProperty(null, "chat_log_debug", "true");

                var messages = Messages();
                Assert.AreEqual(2, messages.Count);
                StringAssert.StartsWith(messages[0], "Bool property successfully updated!");
                StringAssert.StartsWith(messages[1], "[AUDIT] Successfully changed server bool property chat_log_debug to True");
            }
            finally
            {
                PropertyManager.ModifyBool("chat_log_debug", prior);
            }
        }

        [TestMethod]
        public void ModifyBool_AlreadySet_AlreadyText_NoAudit()
        {
            PropertyManager.ModifyBool("chat_log_debug", false);
            var prior = PropertyManager.GetBool("chat_log_debug").Item;
            try
            {
                PropertyManager.ModifyBool("chat_log_debug", true);
                appender.Clear();

                AdminCommands.HandleModifyServerBoolProperty(null, "chat_log_debug", "true");

                var messages = Messages();
                Assert.AreEqual(1, messages.Count);
                StringAssert.StartsWith(messages[0], "Bool property is already True for chat_log_debug!");
            }
            finally
            {
                PropertyManager.ModifyBool("chat_log_debug", prior);
            }
        }

        [TestMethod]
        public void ModifyBool_UnknownKey_UnknownText_NoAudit()
        {
            // An unknown key must still be pre-seeded in the cache: GetBool's DB fallback NREs with no
            // live shard config in this test environment (the same trap every uncached-key test hits).
            SetBoolCache("not_a_real_bool_property_key", false, "test");

            AdminCommands.HandleModifyServerBoolProperty(null, "not_a_real_bool_property_key", "true");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Unknown bool property was not updated.");
        }

        private static void SetBoolCache(string key, bool value, string description)
        {
            var field = typeof(PropertyManager).GetField("CachedBooleanSettings", BindingFlags.NonPublic | BindingFlags.Static);
            var cache = (System.Collections.Concurrent.ConcurrentDictionary<string, ConfigurationEntry<bool>>)field.GetValue(null);
            cache[key] = new ConfigurationEntry<bool>(false, value, description);
        }

        [TestMethod]
        public void ModifyBool_Garbage_ValidBoolText()
        {
            AdminCommands.HandleModifyServerBoolProperty(null, "chat_log_debug", "not-a-bool");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Please input a valid bool");
        }

        // ---- long ----

        [TestMethod]
        public void ModifyLong_Success_OutputThenAuditLine()
        {
            try
            {
                AdminCommands.HandleModifyServerLongProperty(null, "account_vault_entry_cap", "750");

                var messages = Messages();
                Assert.AreEqual(2, messages.Count);
                StringAssert.StartsWith(messages[0], "Long property successfully updated!");
                StringAssert.StartsWith(messages[1], "[AUDIT] Successfully changed server long property account_vault_entry_cap to 750");
            }
            finally
            {
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            }
        }

        [TestMethod]
        public void ModifyLong_UnknownKey()
        {
            AdminCommands.HandleModifyServerLongProperty(null, "not_a_real_long_property_key", "1");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Unknown long property was not updated.");
        }

        [TestMethod]
        public void ModifyLong_Garbage()
        {
            AdminCommands.HandleModifyServerLongProperty(null, "account_vault_entry_cap", "not-a-long");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Please input a valid long");
        }

        // ---- double ----

        [TestMethod]
        public void ModifyDouble_Success_OutputThenAuditLine()
        {
            try
            {
                AdminCommands.HandleModifyServerFloatProperty(null, "pk_respite_timer", "301");

                var messages = Messages();
                Assert.AreEqual(2, messages.Count);
                StringAssert.StartsWith(messages[0], "Double property successfully updated!");
                StringAssert.StartsWith(messages[1], "[AUDIT] Successfully changed server double property pk_respite_timer to 301");
            }
            finally
            {
                PropertyManager.ModifyDouble("pk_respite_timer", 300);
            }
        }

        [TestMethod]
        public void ModifyDouble_UnknownKey()
        {
            AdminCommands.HandleModifyServerFloatProperty(null, "not_a_real_double_property_key", "1");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Unknown double property was not updated.");
        }

        [TestMethod]
        public void ModifyDouble_Garbage()
        {
            AdminCommands.HandleModifyServerFloatProperty(null, "pk_respite_timer", "not-a-double");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Please input a valid double");
        }

        // ---- string ----

        [TestMethod]
        public void ModifyString_Success_OutputThenAuditLine()
        {
            try
            {
                AdminCommands.HandleModifyServerStringProperty(null, "server_motd", "hello");

                var messages = Messages();
                Assert.AreEqual(2, messages.Count);
                StringAssert.StartsWith(messages[0], "String property successfully updated!");
                StringAssert.StartsWith(messages[1], "[AUDIT] Successfully changed server string property server_motd to hello");
            }
            finally
            {
                PropertyManager.ModifyString("server_motd", "");
            }
        }

        [TestMethod]
        public void ModifyString_UnknownKey()
        {
            AdminCommands.HandleModifyServerStringProperty(null, "not_a_real_string_property_key", "hello");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Unknown string property was not updated.");
        }

        // ---- added AFTER the refactor: document the intended, now-invariant parse (PLAN-P2.md ruling 2) ----

        [TestMethod]
        public void ModifyDouble_ThousandsSeparator_NowValidDoubleText()
        {
            AdminCommands.HandleModifyServerFloatProperty(null, "pk_respite_timer", "1,000.5");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Please input a valid double");
        }

        [TestMethod]
        public void ModifyDouble_NaN_NowValidDoubleText()
        {
            AdminCommands.HandleModifyServerFloatProperty(null, "pk_respite_timer", "NaN");

            var messages = Messages();
            Assert.AreEqual(1, messages.Count);
            StringAssert.StartsWith(messages[0], "Please input a valid double");
        }

        [TestMethod]
        public void ModifyDouble_CommaDecimalThreadCulture_DotStillAccepted()
        {
            var priorCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            try
            {
                AdminCommands.HandleModifyServerFloatProperty(null, "pk_respite_timer", "301.5");

                // The dot-separated input must still PARSE under a comma-decimal thread culture
                // (PLAN-P2.md ruling 2) - the audit line's own number formatting is untouched by this
                // refactor (section 2: "keep the audit interpolation... exactly as today") and stays
                // culture-dependent, so only the successful-update line is asserted here.
                var messages = Messages();
                Assert.AreEqual(2, messages.Count);
                StringAssert.StartsWith(messages[0], "Double property successfully updated!");
                StringAssert.StartsWith(messages[1], "[AUDIT] Successfully changed server double property pk_respite_timer to");
                Assert.AreEqual(301.5, PropertyManager.GetDouble("pk_respite_timer").Item);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = priorCulture;
                PropertyManager.ModifyDouble("pk_respite_timer", 300);
            }
        }
    }
}
