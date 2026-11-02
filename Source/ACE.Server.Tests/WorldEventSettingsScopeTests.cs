using System.IO;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The structural scope rule: a writer called with scope S refuses every row whose scope is not S, in single writes and
    /// inside reset-all and replace-import loops. Each test uses a capturing fake writer and asserts on what reached it.
    /// </summary>
    [TestClass]
    public class WorldEventSettingsScopeTests
    {
        private static readonly string[] GlobalProps = { "t_g_long", "t_g_zero", "t_g_dbl", "t_g_bool", "t_g_mode" };
        private static readonly string[] HoldProps = { "t_h_dbl", "t_h_long", "t_h_bool", "t_h_pick", "t_h_text" };

        /// <summary>Every row set to a non-default value, so a reset or replace-import has something to touch in both scopes.</summary>
        private static void SetEverything(WorldEventSettingsTestRig rig)
        {
            rig.Values["t_g_long"] = "25";
            rig.Values["t_g_zero"] = "0";
            rig.Values["t_g_dbl"] = "2.5";
            rig.Values["t_g_bool"] = "False";
            rig.Values["t_g_mode"] = "x";
            rig.Values["t_h_dbl"] = "0.9";
            rig.Values["t_h_long"] = "7";
            rig.Values["t_h_bool"] = "True";
            rig.Values["t_h_pick"] = "c";
            rig.Values["t_h_text"] = "keep";
        }

        [TestMethod]
        public void ResetAll_GoalScope_NeverWritesGlobalKey()
        {
            using var rig = new WorldEventSettingsTestRig();
            SetEverything(rig);

            var o = rig.Service.Reset(WorldEventSettingsTestRig.Hold, "all", null);

            foreach (var p in GlobalProps)
            {
                Assert.IsFalse(rig.WroteProperty(p), $"{p} must never reach the writer from a goal scope");
                Assert.AreNotEqual(rig.Defaults[p], rig.Values[p], $"{p} must still hold its set value");
            }

            CollectionAssert.AreEquivalent(new[] { "h_dbl", "h_long", "h_bool", "h_pick", "h_text" }, o.Written);
            Assert.AreEqual(0, o.Refused.Count, "a reset-all loop skips other scopes silently");
            StringAssert.Contains(o.Reply, "Reset writes the current default");
            Assert.IsTrue(HoldProps.All(rig.WroteProperty));
        }

        [TestMethod]
        public void ResetGroup_GoalScope_RefusesTheGlobalRowOfAMixedGroup()
        {
            using var rig = new WorldEventSettingsTestRig();
            SetEverything(rig);

            var o = rig.Service.Reset(WorldEventSettingsTestRig.Hold, "mixed", null);

            Assert.IsFalse(rig.WroteProperty("t_g_mode"));
            Assert.IsTrue(rig.WroteProperty("t_h_pick"));
            CollectionAssert.AreEqual(new[] { "h_pick" }, o.Written);
            Assert.AreEqual(0, o.Refused.Count, "a group loop skips other scopes silently");
        }

        [TestMethod]
        public void ResetNamedGlobalKey_GoalScope_IsRefusedEvenWhenUnset()
        {
            using var rig = new WorldEventSettingsTestRig();

            var o = rig.Service.Reset(WorldEventSettingsTestRig.Hold, "g_long", null);

            Assert.AreEqual(0, rig.Writes.Count);
            Assert.AreEqual(1, o.Refused.Count);
            Assert.AreEqual(0, o.Written.Count);
        }

        [TestMethod]
        public void ResetAll_GlobalScope_NeverWritesGoalKey()
        {
            using var rig = new WorldEventSettingsTestRig();
            SetEverything(rig);

            var o = rig.Service.Reset(SettingScope.Global, "all", null);

            foreach (var p in HoldProps)
                Assert.IsFalse(rig.WroteProperty(p), p);

            CollectionAssert.AreEquivalent(new[] { "g_long", "g_zero", "g_dbl", "g_bool", "g_mode" }, o.Written);
            Assert.AreEqual(0, o.Refused.Count);
        }

        [TestMethod]
        public void Set_RefusesRowsOutsideTheCallersScope_BeforeTheWriterIsReached()
        {
            using var rig = new WorldEventSettingsTestRig();

            var global = rig.Service.Set(WorldEventSettingsTestRig.Hold, "g_long", "10", null);
            var goal = rig.Service.Set(SettingScope.Global, "h_dbl", "0.4", null);
            var other = rig.Service.Set(SettingScope.Goal("kill"), "h_dbl", "0.4", null);

            Assert.AreEqual(0, rig.Writes.Count);
            Assert.AreEqual(1, global.Refused.Count);
            Assert.AreEqual(1, goal.Refused.Count);
            Assert.AreEqual(1, other.Refused.Count);
            StringAssert.Contains(global.Reply, "Not set");

            var ok = rig.Service.Set(SettingScope.Global, "g_long", "10", null);
            Assert.AreEqual(1, ok.Written.Count);
            Assert.AreEqual(1, rig.Writes.Count);
        }

        [TestMethod]
        public void Import_GoalScope_NeverWritesGlobalKey()
        {
            using var rig = new WorldEventSettingsTestRig();
            SetEverything(rig);
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "mixed.json"),
                "{\"preset\":\"mixed\",\"settings\":{\"g_long\":\"99\",\"g_bool\":\"on\",\"h_dbl\":\"0.1\",\"h_text\":\"new\"}}");

            var o = rig.Service.Import(WorldEventSettingsTestRig.Hold, "mixed", merge: false, null);

            foreach (var p in GlobalProps)
                Assert.IsFalse(rig.WroteProperty(p), $"{p} must never reach the writer from a goal-scoped import");

            Assert.AreEqual("25", rig.Values["t_g_long"]);
            Assert.AreEqual("False", rig.Values["t_g_bool"]);
            Assert.AreEqual("0.1", rig.Values["t_h_dbl"]);
            Assert.AreEqual("new", rig.Values["t_h_text"]);

            // The global keys the FILE names are refused loudly; the other set global rows are skipped silently.
            var refusedKeys = o.Refused.Select(r => r.Split(' ')[0]).ToList();
            CollectionAssert.AreEquivalent(new[] { "g_long", "g_bool" }, refusedKeys);

            // Replace mode reset the goal's own rows the file did not mention.
            Assert.AreEqual(rig.Defaults["t_h_long"], rig.Values["t_h_long"]);
        }

        [TestMethod]
        public void Import_GoalScope_Merge_NeverWritesGlobalKey()
        {
            using var rig = new WorldEventSettingsTestRig();
            SetEverything(rig);
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "m.json"), "{\"settings\":{\"g_zero\":\"5\",\"h_long\":\"9\"}}");

            var o = rig.Service.Import(WorldEventSettingsTestRig.Hold, "m", merge: true, null);

            Assert.IsFalse(rig.WroteProperty("t_g_zero"));
            Assert.AreEqual("0", rig.Values["t_g_zero"]);
            Assert.AreEqual("9", rig.Values["t_h_long"]);
            Assert.AreEqual("0.9", rig.Values["t_h_dbl"], "merge keeps what the file does not mention");
            Assert.AreEqual(1, o.Refused.Count);
        }

        [TestMethod]
        public void Import_GlobalScope_NeverWritesGoalKey()
        {
            using var rig = new WorldEventSettingsTestRig();
            SetEverything(rig);
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "g.json"), "{\"settings\":{\"g_long\":\"5\",\"h_dbl\":\"0.2\"}}");

            rig.Service.Import(SettingScope.Global, "g", merge: false, null);

            foreach (var p in HoldProps)
                Assert.IsFalse(rig.WroteProperty(p), p);

            Assert.AreEqual("5", rig.Values["t_g_long"]);
        }

        [TestMethod]
        public void Export_IsScopePure()
        {
            using var rig = new WorldEventSettingsTestRig();
            SetEverything(rig);

            var goal = rig.Service.BuildPreset(WorldEventSettingsTestRig.Hold, "p", all: false);
            var global = rig.Service.BuildPreset(SettingScope.Global, "p", all: false);

            Assert.IsTrue(goal.Settings.Keys.All(k => k.StartsWith("h_")));
            Assert.IsTrue(global.Settings.Keys.All(k => k.StartsWith("g_")));
            Assert.AreEqual("goal:hold", goal.Scope);
            Assert.AreEqual("global", global.Scope);
        }

        [TestMethod]
        public void FailedWrite_IsReportedAsFailed_NotRefused()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.FailWriteFor = "t_g_long";

            var o = rig.Service.Set(SettingScope.Global, "g_long", "10", null);

            Assert.AreEqual(1, o.Failed.Count);
            Assert.AreEqual(0, o.Refused.Count);
            StringAssert.Contains(o.Reply, "forced failure");
        }
    }
}
