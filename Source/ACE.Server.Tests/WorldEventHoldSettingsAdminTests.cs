using System;
using System.IO;
using System.Linq;

using ACE.Server.Command.Handlers;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>"/worldevent hold ...": parsing, set, reset, and the export/import round trip.</summary>
    [TestClass]
    public class WorldEventHoldSettingsAdminTests
    {
        private HoldSettingsScope scope;
        private Func<string, string, string, ACE.Server.Network.Session, string> previousWriter;
        private Func<string> previousFolder;
        private string folder;

        [TestInitialize]
        public void Setup()
        {
            scope = new HoldSettingsScope();
            previousWriter = HoldSettingsAdmin.Writer;
            previousFolder = HoldSettingsAdmin.PresetFolder;
            folder = Path.Combine(Path.GetTempPath(), "hold-presets-test-" + Guid.NewGuid().ToString("N"));

            // Writes land in the scope's property store, exactly as PropertyAdminService would leave the cache.
            HoldSettingsAdmin.Writer = (type, property, value, session) =>
            {
                if (type != "string")
                    return null;

                scope.Set(property, value);
                return null;
            };
            HoldSettingsAdmin.PresetFolder = () => folder;
        }

        [TestCleanup]
        public void Teardown()
        {
            HoldSettingsAdmin.Writer = previousWriter;
            HoldSettingsAdmin.PresetFolder = previousFolder;
            scope.Dispose();

            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch
            {
            }
        }

        private static WorldEventCommands.HoldArgs Parse(params string[] words)
        {
            Assert.IsTrue(WorldEventCommands.TryParseHoldArgs(words, out var args, out var error), error);
            return args;
        }

        [TestMethod]
        public void Parse_TheHoldCommands()
        {
            Assert.AreEqual("settings", Parse().Verb);
            Assert.AreEqual("knights", Parse("settings", "knights").Target);

            var set = Parse("set", "knight_max_health_by_wcid", "2000005=12000,", "2000007=8000");
            Assert.AreEqual("knight_max_health_by_wcid", set.Target);
            Assert.AreEqual("2000005=12000, 2000007=8000", set.Value, "a value may have spaces");

            Assert.IsTrue(Parse("export", "easy", "--all").All);
            Assert.IsTrue(Parse("import", "hard", "--merge").Merge);
            Assert.AreEqual("presets", Parse("presets").Verb);

            Assert.IsFalse(WorldEventCommands.TryParseHoldArgs(new[] { "set", "shrine_heal" }, out _, out _), "set needs a value");
            Assert.IsFalse(WorldEventCommands.TryParseHoldArgs(new[] { "export", "easy", "--merge" }, out _, out _), "--merge is import's flag");
            Assert.IsFalse(WorldEventCommands.TryParseHoldArgs(new[] { "reset" }, out _, out _));
            Assert.IsFalse(WorldEventCommands.TryParseHoldArgs(new[] { "dance" }, out _, out _));
        }

        [TestMethod]
        public void Set_IsLive_AndWarnsWhenItClamps()
        {
            var reply = HoldSettingsAdmin.Set("shrine_heal", "0.25", null);
            StringAssert.Contains(reply, "shrine_heal");
            Assert.AreEqual("0.25", HoldSettings.RawValue(HoldSettings.All.First(s => s.Key == HoldKeys.ShrineHeal)));
            Assert.AreEqual(0.25, HoldSettings.Number(HoldKeys.ShrineHeal, 0.15), "the next read sees it: no restart, no reload");

            reply = HoldSettingsAdmin.Set("knight_max_health", "999999", null);
            StringAssert.Contains(reply, "Warning");
            StringAssert.Contains(reply, "out of range");
            Assert.AreEqual(HoldSettings.KnightHealthMax, HoldSettings.Integer(HoldKeys.KnightMaxHealth, 0));

            StringAssert.Contains(HoldSettingsAdmin.Set("min_survivors", "any", null), "min_survivors");
            Assert.AreEqual("3", HoldSettings.RawValue(HoldSettings.All.First(s => s.Key == HoldKeys.MinSurvivors)), "the word is stored as its number");

            StringAssert.Contains(HoldSettingsAdmin.Set("chain", "off", null), "chain");
            Assert.IsFalse(HoldSettings.Toggle(HoldKeys.Chain, true));

            StringAssert.Contains(HoldSettingsAdmin.Set("no_such_setting", "1", null), "No Hold setting");
        }

        [TestMethod]
        public void Reset_NameGroupAndAll()
        {
            HoldSettingsAdmin.Set("shrine_heal", "0.3", null);
            HoldSettingsAdmin.Set("shrine_cooldown_seconds", "10", null);
            HoldSettingsAdmin.Set("knight_max_health", "15000", null);

            StringAssert.Contains(HoldSettingsAdmin.Reset("shrine_heal", null), "Reset 1");
            Assert.IsFalse(HoldSettings.IsSet(HoldKeys.ShrineHeal));
            Assert.IsTrue(HoldSettings.IsSet(HoldKeys.ShrineCooldownSeconds));

            StringAssert.Contains(HoldSettingsAdmin.Reset("healing", null), "Reset 1");
            Assert.IsFalse(HoldSettings.IsSet(HoldKeys.ShrineCooldownSeconds));
            Assert.IsTrue(HoldSettings.IsSet(HoldKeys.KnightMaxHealth), "another group is untouched");

            HoldSettingsAdmin.Reset("all", null);
            Assert.IsFalse(HoldSettings.All.Where(s => !s.IsReused).Any(s => HoldSettings.IsSet(s.Key)), "all: nothing set");
        }

        [TestMethod]
        public void ExportImport_RoundTrip()
        {
            HoldSettingsAdmin.Set("knight_max_health", "20000", null);
            HoldSettingsAdmin.Set("shrine_heal", "0.3", null);
            HoldSettingsAdmin.Set("shrine_cooldown_seconds", "0", null);
            HoldSettingsAdmin.Set("chain", "off", null);
            HoldSettingsAdmin.Set("finale_target", "both", null);

            var before = HoldSettings.All.Where(s => !s.IsReused).ToDictionary(s => s.Key, s => HoldSettings.RawValue(s));

            StringAssert.Contains(HoldSettingsAdmin.Export("easy", false), "Exported 5");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "easy.json")));
            CollectionAssert.Contains(HoldSettingsAdmin.Presets(), "easy");

            HoldSettingsAdmin.Reset("all", null);
            HoldSettingsAdmin.Set("undefended_seconds", "100", null);   // not in the preset: import resets it

            var reply = HoldSettingsAdmin.Import("easy", false, null);
            StringAssert.Contains(reply, "5 setting(s) applied");

            var after = HoldSettings.All.Where(s => !s.IsReused).ToDictionary(s => s.Key, s => HoldSettings.RawValue(s));
            CollectionAssert.AreEquivalent(before.ToList(), after.ToList(), "identical after the round trip");

            // --merge keeps what is set that the file does not name.
            HoldSettingsAdmin.Set("undefended_seconds", "100", null);
            HoldSettingsAdmin.Import("easy", true, null);
            Assert.AreEqual(100, HoldSettings.Number(HoldKeys.UndefendedSeconds, 0));
        }

        [TestMethod]
        public void ExportAll_WritesEveryEffectiveValue()
        {
            HoldSettingsAdmin.Export("snapshot", true);
            var text = File.ReadAllText(Path.Combine(folder, "snapshot.json"));

            foreach (var s in HoldSettings.All)
                StringAssert.Contains(text, $"\"{s.Key}\"", s.Key);
        }

        [TestMethod]
        public void Import_UnknownKeysAndBadValues_AndBadNames()
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "odd.json"),
                "{ \"preset\": \"odd\", \"settings\": { \"shrine_heal\": \"2\", \"dragons\": \"5\", \"chain\": \"maybe\", \"knight_count\": \"2\" } }");

            var reply = HoldSettingsAdmin.Import("odd", false, null);

            StringAssert.Contains(reply, "Unknown, skipped: dragons");
            StringAssert.Contains(reply, "out of range", "shrine_heal 2 is applied and clamped to 1, and said");
            Assert.AreEqual(1.0, HoldSettings.Number(HoldKeys.ShrineHeal, 0));
            Assert.AreEqual(2, HoldSettings.Integer(HoldKeys.KnightCount, 0));
            Assert.IsFalse(HoldSettings.IsSet(HoldKeys.Chain), "'maybe' does not parse: listed, not applied as a value");

            StringAssert.Contains(HoldSettingsAdmin.Export("../evil", false), "Not exported");
            StringAssert.Contains(HoldSettingsAdmin.Import("C:\\temp\\x", false, null), "Not imported");
            StringAssert.Contains(HoldSettingsAdmin.Import("missing", false, null), "no preset");
        }

        [TestMethod]
        public void Settings_ListsGroupsAndValues()
        {
            HoldSettingsAdmin.Set("shrine_heal", "0.3", null);

            var summary = HoldSettingsAdmin.Settings(null);
            StringAssert.Contains(summary, "Groups:");
            StringAssert.Contains(summary, "shrine_heal = 0.3");
            StringAssert.Contains(summary, "*set");

            var healing = HoldSettingsAdmin.Settings("healing");
            StringAssert.Contains(healing, "shrine_cooldown_seconds");
            StringAssert.Contains(healing, "[0..600]");

            StringAssert.Contains(HoldSettingsAdmin.Settings("nonsense"), "No Hold setting group");
        }
    }
}
