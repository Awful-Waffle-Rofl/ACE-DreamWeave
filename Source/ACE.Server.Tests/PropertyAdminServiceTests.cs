using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Server.Entity.Actions;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    [TestClass]
    public class PropertyAdminServiceTests
    {
        private static void SetBoolCache(string key, bool value, string description)
        {
            var field = typeof(PropertyManager).GetField("CachedBooleanSettings", BindingFlags.NonPublic | BindingFlags.Static);
            var cache = (ConcurrentDictionary<string, ConfigurationEntry<bool>>)field.GetValue(null);
            cache[key] = new ConfigurationEntry<bool>(false, value, description);
        }

        [TestMethod]
        public void TryResolveType_EachDictionary_AndUnknown()
        {
            Assert.IsTrue(PropertyAdminService.TryResolveType("chat_log_debug", out var boolType));
            Assert.AreEqual(PropertyAdminService.TypeBool, boolType);

            Assert.IsTrue(PropertyAdminService.TryResolveType("account_vault_entry_cap", out var longType));
            Assert.AreEqual(PropertyAdminService.TypeLong, longType);

            Assert.IsTrue(PropertyAdminService.TryResolveType("pk_respite_timer", out var doubleType));
            Assert.AreEqual(PropertyAdminService.TypeDouble, doubleType);

            Assert.IsTrue(PropertyAdminService.TryResolveType("server_motd", out var stringType));
            Assert.AreEqual(PropertyAdminService.TypeString, stringType);

            Assert.IsFalse(PropertyAdminService.TryResolveType("not_a_real_property_key", out var unknownType));
            Assert.IsNull(unknownType);
        }

        [TestMethod]
        public void FormatCurrent_EqualsEnumerateWithPrefixCurrent()
        {
            // Seed every key first: an uncached read falls through to DatabaseManager.ShardConfig,
            // which is null (NREs) with no live shard database in this test environment. Seeded (and
            // restored) to each key's own default - PersonalVendorTests.cs:1080's precedent - so a
            // later test that assumes the default is never left with a stale value.
            PropertyManager.ModifyBool("chat_log_debug", false);
            PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            PropertyManager.ModifyDouble("pk_respite_timer", 300);
            PropertyManager.ModifyString("server_motd", "");
            try
            {
                AssertMatchesEnumerate(PropertyAdminService.TypeBool, "chat_log_debug");
                AssertMatchesEnumerate(PropertyAdminService.TypeLong, "account_vault_entry_cap");
                AssertMatchesEnumerate(PropertyAdminService.TypeDouble, "pk_respite_timer");
                AssertMatchesEnumerate(PropertyAdminService.TypeString, "server_motd");
            }
            finally
            {
                PropertyManager.ModifyBool("chat_log_debug", false);
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
                PropertyManager.ModifyDouble("pk_respite_timer", 300);
                PropertyManager.ModifyString("server_motd", "");
            }
        }

        private static void AssertMatchesEnumerate(string type, string key)
        {
            var expected = PropertyManager.EnumerateWithPrefix(key).Single(r => r.key == key).current;
            Assert.AreEqual(expected, PropertyAdminService.FormatCurrent(type, key), $"mismatch for {key}");
        }

        [TestMethod]
        public void InGame_SameLong_StillAuditsAsToday()
        {
            var priorAudit = PropertyAdminService.AuditInGame;
            var auditCalls = 0;
            try
            {
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
                PropertyAdminService.AuditInGame = (p, m) => auditCalls++;

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeLong, "account_vault_entry_cap", "500", PropertyAdminActor.InGame(null));

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome);
                Assert.AreEqual(1, auditCalls, "in-game /modifylong re-audits an unchanged value, unlike the web path");
            }
            finally
            {
                PropertyAdminService.AuditInGame = priorAudit;
                PropertyManager.ModifyLong("account_vault_entry_cap", 500);
            }
        }

        [TestMethod]
        public void InGame_SameBool_BlankDescription_StillFiresSideEffect()
        {
            var priorAudit = PropertyAdminService.AuditInGame;
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            var auditCalls = 0;
            var sideEffectCalls = 0;
            try
            {
                SetBoolCache("pk_server", true, "");
                PropertyAdminService.AuditInGame = (p, m) => auditCalls++;
                PropertyAdminService.WorldTypeChanged = (k, v) => sideEffectCalls++;

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "pk_server", "true", PropertyAdminActor.InGame(null));

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome, "a blank description means the 'already' short-circuit never fires (AdminCommands.cs:4490)");
                Assert.AreEqual(1, auditCalls);
                Assert.AreEqual(1, sideEffectCalls);
            }
            finally
            {
                PropertyAdminService.AuditInGame = priorAudit;
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyManager.ModifyBool("pk_server", false);
            }
        }

        [TestMethod]
        public void Web_Updated_WritesCache_AuditsAsAccount()
        {
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorAuditInGame = PropertyAdminService.AuditInGame;
            (string label, string message)? captured = null;
            var inGameCalls = 0;
            try
            {
                PropertyManager.ModifyBool("chat_log_debug", false);
                PropertyAdminService.AuditAs = (label, message) => captured = (label, message);
                PropertyAdminService.AuditInGame = (p, m) => inGameCalls++;

                var expected = PropertyAdminService.FormatCurrent(PropertyAdminService.TypeBool, "chat_log_debug");
                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "chat_log_debug", "True", PropertyAdminActor.Web("weft"), expected);

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome);
                Assert.IsNotNull(captured);
                Assert.AreEqual("weft", captured.Value.label);
                Assert.AreEqual("Successfully changed server bool property chat_log_debug from False to True via the web admin panel", captured.Value.message);
                Assert.AreEqual(0, inGameCalls);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.AuditInGame = priorAuditInGame;
                PropertyManager.ModifyBool("chat_log_debug", false);
            }
        }

        [TestMethod]
        public void InGame_Updated_AuditsInGame_NeverAuditAs()
        {
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorAuditInGame = PropertyAdminService.AuditInGame;
            var auditAsCalls = 0;
            var inGameCalls = 0;
            try
            {
                PropertyManager.ModifyBool("chat_log_debug", false);
                PropertyAdminService.AuditAs = (label, message) => auditAsCalls++;
                PropertyAdminService.AuditInGame = (p, m) => inGameCalls++;

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "chat_log_debug", "true", PropertyAdminActor.InGame(null));

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome);
                Assert.AreEqual(1, inGameCalls);
                Assert.AreEqual(0, auditAsCalls);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.AuditInGame = priorAuditInGame;
                PropertyManager.ModifyBool("chat_log_debug", false);
            }
        }

        [TestMethod]
        public void Web_StaleExpected_Conflict_NoMutation_NoAudit()
        {
            var priorAuditAs = PropertyAdminService.AuditAs;
            var auditCalls = 0;
            try
            {
                PropertyManager.ModifyBool("chat_log_debug", false);
                PropertyAdminService.AuditAs = (label, message) => auditCalls++;

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "chat_log_debug", "True", PropertyAdminActor.Web("weft"), "True");

                Assert.AreEqual(PropertyModifyOutcome.Conflict, result.Outcome);
                Assert.AreEqual("False", result.CurrentText);
                Assert.AreEqual(0, auditCalls);
                Assert.AreEqual(false, PropertyManager.GetBool("chat_log_debug").Item, "the cache value must be unchanged");
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyManager.ModifyBool("chat_log_debug", false);
            }
        }

        [TestMethod]
        public void Web_SameValue_Unchanged_NoAudit_NoSideEffect()
        {
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            var auditCalls = 0;
            var sideEffectCalls = 0;
            try
            {
                PropertyManager.ModifyBool("chat_log_debug", false);
                PropertyAdminService.AuditAs = (label, message) => auditCalls++;
                PropertyAdminService.WorldTypeChanged = (k, v) => sideEffectCalls++;

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "chat_log_debug", "False", PropertyAdminActor.Web("weft"), "False");

                Assert.AreEqual(PropertyModifyOutcome.Unchanged, result.Outcome);
                Assert.AreEqual(0, auditCalls);
                Assert.AreEqual(0, sideEffectCalls);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyManager.ModifyBool("chat_log_debug", false);
            }
        }

        [DataTestMethod]
        [DataRow("1,5")]
        [DataRow("1,000")]
        [DataRow("NaN")]
        [DataRow("Infinity")]
        [DataRow("")]
        public void Web_InvalidDouble_Refused(string raw)
        {
            PropertyManager.ModifyDouble("pk_respite_timer", 300);
            var expected = PropertyAdminService.FormatCurrent(PropertyAdminService.TypeDouble, "pk_respite_timer");

            var result = PropertyAdminService.TryModify(PropertyAdminService.TypeDouble, "pk_respite_timer", raw, PropertyAdminActor.Web("weft"), expected);

            Assert.AreEqual(PropertyModifyOutcome.InvalidValue, result.Outcome);
        }

        [TestMethod]
        public void Web_StringWithControlChar_Refused()
        {
            PropertyManager.ModifyString("server_motd", "");
            var expected = PropertyAdminService.FormatCurrent(PropertyAdminService.TypeString, "server_motd");

            var result = PropertyAdminService.TryModify(PropertyAdminService.TypeString, "server_motd", "hello\nworld", PropertyAdminActor.Web("weft"), expected);

            Assert.AreEqual(PropertyModifyOutcome.InvalidValue, result.Outcome);
        }

        [TestMethod]
        public void Web_EmptyString_AcceptedForStringSetting()
        {
            var priorAuditAs = PropertyAdminService.AuditAs;
            try
            {
                PropertyManager.ModifyString("server_motd", "hello");
                PropertyAdminService.AuditAs = (label, message) => { };

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeString, "server_motd", "", PropertyAdminActor.Web("weft"), "hello");

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome);
                Assert.AreEqual("", result.CurrentText);
            }
            finally
            {
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyManager.ModifyString("server_motd", "");
            }
        }

        [DataTestMethod]
        [DataRow("pk_server")]
        [DataRow("pkl_server")]
        public void PkServer_FiresSideEffect_FromInGame(string key)
        {
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            var priorAuditInGame = PropertyAdminService.AuditInGame;
            (string key, bool value)? captured = null;
            try
            {
                SetBoolCache(key, false, "test");
                PropertyAdminService.WorldTypeChanged = (k, v) => captured = (k, v);
                PropertyAdminService.AuditInGame = (p, m) => { };

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, key, "true", PropertyAdminActor.InGame(null));

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome);
                Assert.IsNotNull(captured);
                Assert.AreEqual(key, captured.Value.key);
                Assert.IsTrue(captured.Value.value);
            }
            finally
            {
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyAdminService.AuditInGame = priorAuditInGame;
                PropertyManager.ModifyBool(key, false);
            }
        }

        [DataTestMethod]
        [DataRow("pk_server")]
        [DataRow("pkl_server")]
        public void PkServer_FiresSideEffect_FromWeb(string key)
        {
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            var priorAuditAs = PropertyAdminService.AuditAs;
            (string key, bool value)? captured = null;
            try
            {
                PropertyManager.ModifyBool(key, false);
                PropertyAdminService.WorldTypeChanged = (k, v) => captured = (k, v);
                PropertyAdminService.AuditAs = (label, message) => { };

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, key, "True", PropertyAdminActor.Web("weft"), "False");

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome);
                Assert.IsNotNull(captured);
                Assert.AreEqual(key, captured.Value.key);
                Assert.IsTrue(captured.Value.value);
            }
            finally
            {
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyManager.ModifyBool(key, false);
            }
        }

        [TestMethod]
        public void OtherBool_NoSideEffect_BothPaths()
        {
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            var priorAuditInGame = PropertyAdminService.AuditInGame;
            var priorAuditAs = PropertyAdminService.AuditAs;
            var sideEffectCalls = 0;
            try
            {
                PropertyManager.ModifyBool("chat_log_debug", false);
                PropertyAdminService.WorldTypeChanged = (k, v) => sideEffectCalls++;
                PropertyAdminService.AuditInGame = (p, m) => { };
                PropertyAdminService.AuditAs = (label, message) => { };

                PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "chat_log_debug", "true", PropertyAdminActor.InGame(null));
                PropertyManager.ModifyBool("chat_log_debug", false);
                PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "chat_log_debug", "True", PropertyAdminActor.Web("weft"), "False");

                Assert.AreEqual(0, sideEffectCalls);
            }
            finally
            {
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyAdminService.AuditInGame = priorAuditInGame;
                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyManager.ModifyBool("chat_log_debug", false);
            }
        }

        [TestMethod]
        public void RunOnWorld_NotRunBeforeTimeout_ReturnsNull_ThenSkips()
        {
            IAction stored = null;
            var ran = false;

            var result = PropertyAdminService.RunOnWorld(a => stored = a, () => { ran = true; return new PropertyModifyResult { Outcome = PropertyModifyOutcome.Updated }; }, TimeSpan.FromMilliseconds(50));

            Assert.IsNull(result);
            Assert.IsFalse(ran);

            stored.Act();

            Assert.IsFalse(ran, "an abandoned action must skip the work entirely once the waiter has given up");
        }

        [TestMethod]
        public void RunOnWorld_Throw_Faulted()
        {
            var result = PropertyAdminService.RunOnWorld(a => a.Act(), () => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(5));

            Assert.IsNotNull(result);
            Assert.AreEqual(PropertyModifyOutcome.Faulted, result.Outcome);
        }

        [TestMethod]
        public void Web_AuditThrows_ValueApplied_SideEffectStillFires_WarnLogged()
        {
            var priorAuditAs = PropertyAdminService.AuditAs;
            var priorSideEffect = PropertyAdminService.WorldTypeChanged;
            (string key, bool value)? captured = null;

            var appender = new MemoryAppender();
            var hierarchy = (Hierarchy)LogManager.GetRepository(typeof(PropertyAdminService).Assembly);
            var priorLevel = hierarchy.Root.Level;
            var priorConfigured = hierarchy.Configured;

            try
            {
                hierarchy.Root.AddAppender(appender);
                hierarchy.Root.Level = Level.All;
                hierarchy.Configured = true;

                PropertyManager.ModifyBool("pk_server", false);
                PropertyAdminService.AuditAs = (label, message) => throw new InvalidOperationException("discord relay down");
                PropertyAdminService.WorldTypeChanged = (k, v) => captured = (k, v);

                var result = PropertyAdminService.TryModify(PropertyAdminService.TypeBool, "pk_server", "True", PropertyAdminActor.Web("weft"), "False");

                Assert.AreEqual(PropertyModifyOutcome.Updated, result.Outcome);
                Assert.AreEqual(true, PropertyManager.GetBool("pk_server").Item);
                Assert.IsNotNull(captured);
                Assert.AreEqual("pk_server", captured.Value.key);
                Assert.IsTrue(captured.Value.value);

                var warnings = appender.GetEvents().Where(e => e.Level == Level.Warn).Select(e => e.RenderedMessage).ToList();
                Assert.IsTrue(warnings.Any(m => m.Contains("[MARKET][ADMIN] audit failed:")), "expected a Warn log containing [MARKET][ADMIN] audit failed:");
            }
            finally
            {
                hierarchy.Root.RemoveAppender(appender);
                hierarchy.Root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;

                PropertyAdminService.AuditAs = priorAuditAs;
                PropertyAdminService.WorldTypeChanged = priorSideEffect;
                PropertyManager.ModifyBool("pk_server", false);
            }
        }
    }
}
