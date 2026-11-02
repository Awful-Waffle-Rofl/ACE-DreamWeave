using System;
using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Preset export and import: round trip, merge vs replace, bad names, bad files. The preset folder is a per-test temp directory.</summary>
    [TestClass]
    public class WorldEventSettingsExportImportTests
    {
        private static readonly SettingScope Global = SettingScope.Global;

        [TestMethod]
        public void RoundTrip_ExportThenResetThenImport_RestoresTheValues()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.Service.Set(Global, "g_long", "30", null);
            rig.Service.Set(Global, "g_zero", "0", null);
            rig.Service.Set(Global, "g_bool", "off", null);
            rig.Service.Set(Global, "g_mode", "y", null);

            var export = rig.Service.Export(Global, "round1", all: false);
            StringAssert.Contains(export.Reply, "Exported 4 setting(s)");
            Assert.IsTrue(File.Exists(Path.Combine(rig.Folder, "round1.json")));

            rig.Service.Reset(Global, "all", null);
            Assert.IsFalse(rig.Service.Describe(rig.Registry.Rows.First(r => r.Key == "g_long")).Overridden);

            var import = rig.Service.Import(Global, "round1", merge: false, null);

            Assert.AreEqual("30", rig.Values["t_g_long"]);
            Assert.AreEqual("0", rig.Values["t_g_zero"]);
            Assert.AreEqual("False", rig.Values["t_g_bool"]);
            Assert.AreEqual("y", rig.Values["t_g_mode"]);
            Assert.AreEqual(4, import.Written.Count);
            Assert.AreEqual(0, import.Refused.Count, import.Reply);
        }

        [TestMethod]
        public void ExportAll_IncludesDefaultsAsEffectiveValues()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.Service.Set(Global, "g_long", "30", null);

            var file = rig.Service.BuildPreset(Global, "x", all: true);

            Assert.AreEqual(4 + 1, file.Settings.Count, "every Global row");
            Assert.AreEqual("30", file.Settings["g_long"]);
            Assert.AreEqual("60", file.Settings["g_zero"]);
            Assert.AreEqual("on", file.Settings["g_bool"]);
        }

        [TestMethod]
        public void BadPresetNames_AreRefused_AndNothingIsWritten()
        {
            using var rig = new WorldEventSettingsTestRig();
            var bad = new[] { "", "   ", "../x", "..\\x", "a/b", "a\\b", "has space", "UP PER", "x.y", "dot.dot.json", new string('a', 41), "C:\\evil", "%2e%2e", "a\0b", "--all", "--merge", "-x", "-" };

            foreach (var name in bad)
            {
                var export = rig.Service.Export(Global, name, all: true);
                StringAssert.StartsWith(export.Reply, "Not exported", $"export '{name}'");

                var import = rig.Service.Import(Global, name, merge: false, null);
                StringAssert.StartsWith(import.Reply, "Not imported", $"import '{name}'");
            }

            Assert.IsFalse(Directory.Exists(rig.Folder), "no folder or file was created");
            Assert.AreEqual(0, rig.Writes.Count);
        }

        [TestMethod]
        public void GoodNames_AcceptedWithOrWithoutJsonExtension_CaseInsensitive()
        {
            using var rig = new WorldEventSettingsTestRig();

            StringAssert.StartsWith(rig.Service.Export(Global, "Night_Mode-2.json", all: true).Reply, "Exported");
            Assert.IsTrue(File.Exists(Path.Combine(rig.Folder, "night_mode-2.json")));
            StringAssert.StartsWith(rig.Service.Import(Global, "night_mode-2", merge: true, null).Reply, "Imported");
        }

        [TestMethod]
        public void NoPresetFolder_IsRefused()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.UseFolderOverride = true;
            rig.FolderOverride = null;

            StringAssert.Contains(rig.Service.Export(Global, "x", all: false).Reply, "no axis folder");
            StringAssert.Contains(rig.Service.Import(Global, "x", merge: false, null).Reply, "no axis folder");
        }

        [TestMethod]
        public void Import_MissingFile_NotAPreset_AndOversize_AreRefused()
        {
            using var rig = new WorldEventSettingsTestRig();
            Directory.CreateDirectory(rig.Folder);

            StringAssert.Contains(rig.Service.Import(Global, "nope", merge: false, null).Reply, "no preset at");

            File.WriteAllText(Path.Combine(rig.Folder, "junk.json"), "{ not json");
            StringAssert.Contains(rig.Service.Import(Global, "junk", merge: false, null).Reply, "is not a preset");

            File.WriteAllText(Path.Combine(rig.Folder, "big.json"), new string(' ', 300 * 1024));
            StringAssert.Contains(rig.Service.Import(Global, "big", merge: false, null).Reply, "larger than");

            Assert.AreEqual(0, rig.Writes.Count);
        }

        [TestMethod]
        public void Import_ListsUnknownRefusedAndClamped_AndAppliesTheClampedValue()
        {
            using var rig = new WorldEventSettingsTestRig();
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "p.json"),
                "{\"settings\":{\"g_long\":\"999\",\"g_dbl\":\"NaN\",\"g_bool\":\"maybe\",\"nonsense\":\"1\",\"g_zero\":\"\"}}");

            var o = rig.Service.Import(Global, "p", merge: true, null);

            Assert.AreEqual("200", rig.Values["t_g_long"], "clamped, not rejected");
            CollectionAssert.AreEqual(new[] { "nonsense" }, o.Unknown);
            Assert.AreEqual(2, o.Refused.Count);
            Assert.AreEqual(1, o.Warnings.Count);
            Assert.IsFalse(rig.WroteProperty("t_g_zero"), "an empty value is skipped");
            Assert.IsFalse(rig.WroteProperty("t_g_dbl"));
        }

        [TestMethod]
        public void Import_Replace_ResetsUnmentionedRows_Merge_KeepsThem()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.Service.Set(Global, "g_long", "30", null);
            rig.Service.Set(Global, "g_dbl", "2", null);
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "one.json"), "{\"settings\":{\"g_long\":\"44\"}}");

            rig.Service.Import(Global, "one", merge: true, null);
            Assert.AreEqual("44", rig.Values["t_g_long"]);
            Assert.AreEqual("2", rig.Values["t_g_dbl"]);

            var o = rig.Service.Import(Global, "one", merge: false, null);
            Assert.AreEqual("0", rig.Values["t_g_dbl"], "replace reset the row the file left out");
            StringAssert.Contains(o.Reply, "1 other setting(s) reset");
            Assert.IsTrue(rig.Audits.Any(a => a.Contains("imported")));
        }

        [TestMethod]
        public void Import_RowNamedTwiceByCase_IsAppliedOnceLastWins_AndListedOnce()
        {
            using var rig = new WorldEventSettingsTestRig();
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "dup.json"),
                "{\"settings\":{\"g_long\":\"10\",\"G_LONG\":\"20\",\"t_g_long\":\"30\",\"g_dbl\":\"NaN\",\"G_DBL\":\"bad\",\"Nope\":\"1\",\"NOPE\":\"2\"}}");

            var o = rig.Service.Import(Global, "dup", merge: true, null);

            Assert.AreEqual(1, rig.Writes.Count(w => w.Property == "t_g_long"), "written once");
            Assert.AreEqual("30", rig.Values["t_g_long"], "the last entry for the row wins");
            CollectionAssert.AreEqual(new[] { "g_long" }, o.Written);
            Assert.AreEqual(1, o.Refused.Count, "the refused row is listed once");
            CollectionAssert.AreEqual(new[] { "Nope" }, o.Unknown);
        }

        [TestMethod]
        public void Import_Reply_ListsAtMostTenEntriesAndCountsTheRest()
        {
            using var rig = new WorldEventSettingsTestRig();
            Directory.CreateDirectory(rig.Folder);
            var unknown = string.Join(",", Enumerable.Range(0, 25).Select(i => $"\"junk{i:D2}\":\"1\""));
            File.WriteAllText(Path.Combine(rig.Folder, "many.json"), "{\"settings\":{" + unknown + "}}");

            var o = rig.Service.Import(Global, "many", merge: true, null);

            Assert.AreEqual(25, o.Unknown.Count);
            StringAssert.Contains(o.Reply, "and 15 more");
            StringAssert.Contains(o.Reply, "junk09");
            Assert.IsFalse(o.Reply.Contains("junk10"));
        }

        [TestMethod]
        public void Import_EchoesTheFileOwnPresetNameOnlyWhenItIsAValidName()
        {
            using var rig = new WorldEventSettingsTestRig();
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "real.json"), "{\"preset\":\"evil name\\nINJECT\",\"settings\":{}}");
            File.WriteAllText(Path.Combine(rig.Folder, "fine.json"), "{\"preset\":\"nice-name\",\"settings\":{}}");

            var bad = rig.Service.Import(Global, "real", merge: true, null);
            Assert.IsFalse(bad.Reply.Contains("INJECT"));
            StringAssert.StartsWith(bad.Reply, "Imported 'real'");
            Assert.IsFalse(rig.Audits.Any(a => a.Contains("INJECT")));

            StringAssert.StartsWith(rig.Service.Import(Global, "fine", merge: true, null).Reply, "Imported 'nice-name'");
        }

        [TestMethod]
        public void Reset_ReplySaysItWritesTheDefault_NeverThatItUnsets()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.Service.Set(Global, "g_zero", "5", null);
            rig.Service.Set(Global, "g_long", "9", null);

            var named = rig.Service.Reset(Global, "g_zero", null);
            StringAssert.Contains(named.Reply, "Reset writes the current default: g_zero=60");
            Assert.AreEqual("60", named.WrittenValues["g_zero"]);

            var all = rig.Service.Reset(Global, "all", null);
            StringAssert.Contains(all.Reply, "g_long=0");
            Assert.IsFalse(all.Reply.Contains("unset"));
            Assert.IsTrue(rig.Audits.Any(a => a.Contains("to their current default")));

            StringAssert.Contains(rig.Service.Reset(Global, "all", null).Reply, "already its default");
        }

        [TestMethod]
        public void BulkWrites_CarryNoSession_AndTheSummaryIsAudited()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.Service.Set(Global, "g_long", "30", null);
            rig.Service.Set(Global, "g_dbl", "2", null);
            rig.Writes.Clear();

            rig.Service.Reset(Global, "all", null);

            Assert.AreEqual(2, rig.Writes.Count);
            Assert.IsTrue(rig.Writes.All(w => !w.HadSession));
            Assert.AreEqual(1, rig.Audits.Count(a => a.StartsWith("reset 2")));
        }
    }
}
