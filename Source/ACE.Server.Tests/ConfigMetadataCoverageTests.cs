using System;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source/ACE.Server/config-metadata.tsv (embedded resource) vs the live
    /// PropertyManager.EnumerateWithPrefix("") keys - the coverage guard PLAN-P1.md section 3 requires.
    /// No DB needed (see PropertyManagerEnumerateTests.cs, which this suite is modelled on).
    /// </summary>
    [TestClass]
    public class ConfigMetadataCoverageTests
    {
        private static ConfigMetadata metadata;

        [ClassInitialize]
        public static void ClassInit(TestContext _)
        {
            metadata = ConfigMetadata.LoadEmbedded();
        }

        [TestMethod]
        public void OneRowPerPropertyKey()
        {
            var liveKeys = PropertyManager.EnumerateWithPrefix("").Select(r => r.key).ToList();
            var liveSet = new System.Collections.Generic.HashSet<string>(liveKeys, StringComparer.Ordinal);
            var tsvSet = new System.Collections.Generic.HashSet<string>(metadata.RowsByKey.Keys, StringComparer.Ordinal);

            var missing = liveSet.Except(tsvSet).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var extra = tsvSet.Except(liveSet).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Assert.IsTrue(missing.Count == 0,
                $"config-metadata.tsv is missing {missing.Count} key(s): {string.Join(", ", missing)}. Run `ACE.Content.Tools configmetadata --write` and commit the result.");
            Assert.IsTrue(extra.Count == 0,
                $"config-metadata.tsv has {extra.Count} key(s) that no longer exist: {string.Join(", ", extra)}. Run `ACE.Content.Tools configmetadata --write` and commit the result.");

            Assert.AreEqual(liveKeys.Count, liveSet.Count, "PropertyManager.EnumerateWithPrefix returned a duplicate key - that is a PropertyManager bug, not a config-metadata.tsv one.");
        }

        [TestMethod]
        public void TabsKnown_OriginSensitiveValid_Sorted_GroupNonEmpty()
        {
            var knownTabIds = new System.Collections.Generic.HashSet<string>(ConfigMetadata.Tabs.Select(t => t.Id), StringComparer.Ordinal);

            var keysInFileOrder = metadata.RowsByKey.Values.ToList();

            foreach (var row in keysInFileOrder)
            {
                Assert.IsTrue(knownTabIds.Contains(row.Tab), $"{row.Key}: unknown tab '{row.Tab}'");
                Assert.IsTrue(row.Origin == "upstream" || row.Origin == "fork", $"{row.Key}: origin must be 'upstream' or 'fork', was '{row.Origin}'");
                Assert.IsFalse(string.IsNullOrWhiteSpace(row.Group), $"{row.Key}: group must not be empty");
            }

            // Read the raw embedded text directly for the sort check, rather than through the parsed
            // Dictionary - Dictionary<TKey,TValue> enumeration order is an implementation detail, not a
            // contract, so it must never be what this assertion depends on.
            var assembly = typeof(ConfigMetadata).Assembly;
            using var stream = assembly.GetManifestResourceStream(ConfigMetadata.EmbeddedResourceName);
            Assert.IsNotNull(stream, $"embedded resource '{ConfigMetadata.EmbeddedResourceName}' was not found");
            using var reader = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
            var lines = reader.ReadToEnd().Replace("\r\n", "\n").Split('\n');
            var fileOrderKeys = lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t')[0]).ToList();

            var sortedKeys = fileOrderKeys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(sortedKeys, fileOrderKeys, "config-metadata.tsv must be sorted by key, ordinal");
        }

        [TestMethod]
        public void WebhookUrlsSensitive()
        {
            foreach (var key in new[] { "discord_webhook_url_audit", "discord_webhook_url_events", "discord_webhook_url_general", "discord_webhook_url_trade" })
            {
                Assert.IsTrue(metadata.TryGet(key, out var row), $"expected {key} in config-metadata.tsv");
                Assert.IsTrue(row.Sensitive, $"{key} must be marked sensitive");
            }
        }

        [TestMethod]
        public void AdminWebEnabled_DefaultsOn_ServerWebFork()
        {
            // DefaultPropertyManager.DefaultBooleanProperties directly, NOT PropertyManager.GetBool -
            // GetBool throws for an uncached key with no live shard DB (DatabaseManager.ShardConfig is
            // null in this test host), and this assertion only needs the CODE default, not a live read.
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["admin_web_enabled"].Item, "admin_web_enabled must default ON");

            Assert.IsTrue(metadata.TryGet("admin_web_enabled", out var row));
            Assert.AreEqual("server", row.Tab);
            Assert.AreEqual("Web", row.Group);
            Assert.AreEqual("fork", row.Origin);
            Assert.IsFalse(row.Sensitive);
        }

        [TestMethod]
        public void AdminWebCommandsEnabled_DefaultsOn_ServerWebFork()
        {
            // The code default, not a live read (see AdminWebEnabled_DefaultsOn_ServerWebFork above).
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["admin_web_commands_enabled"].Item, "admin_web_commands_enabled must default ON (PLAN-P4.md ruling R6)");

            Assert.IsTrue(metadata.TryGet("admin_web_commands_enabled", out var row));
            Assert.AreEqual("server", row.Tab);
            Assert.AreEqual("Web", row.Group);
            Assert.AreEqual("fork", row.Origin);
            Assert.IsFalse(row.Sensitive);
        }
    }
}
