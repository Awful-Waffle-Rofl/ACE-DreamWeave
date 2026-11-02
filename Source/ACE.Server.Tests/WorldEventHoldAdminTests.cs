using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.Command.Handlers;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.Tests
{
    /// <summary>
    /// "/worldevent hold ..." over the SHIPPED registry (global and Hold rows) behind the in-memory settings rig: the commands
    /// reach SettingsReplies with the Hold scope, never write a global key, and validate the four text-shaped rows on write.
    /// No PropertyManager value is read (those reads throw in a unit test), so nothing is seeded or needs restoring there.
    /// </summary>
    [TestClass]
    public class WorldEventHoldAdminTests
    {
        private const string GlobalProp = "world_events_max_alive";

        private static WorldEventSettingsTestRig NewRig() => new WorldEventSettingsTestRig(WorldEventSettingsRegistry.Default.Rows);

        private static string Prop(string key) => K.PropertyPrefix + key;

        private static List<string> Hold(WorldEventSettingsTestRig rig, params string[] args) =>
            WorldEventCommands.HoldReplies(rig.Service, args, null);

        // ---- the rows carry the parsers ---------------------------------------------------------------------------------

        [TestMethod]
        public void TheFourTextShapedRows_CarryTheirWriteSideParser()
        {
            foreach (var key in new[] { K.KnightMaxHealthByWcid, K.MilestonePercents, K.CorruptedTint, K.MinSurvivors })
            {
                Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind(key, out var row), key);
                Assert.IsNotNull(row.ParseValue, key + " has no ParseValue hook");
            }

            var others = WorldEventSettingsRegistry.Default.Rows.Where(r => r.ParseValue != null).Select(r => r.Key).OrderBy(k => k).ToArray();
            CollectionAssert.AreEqual(new[] { K.CorruptedTint, K.KnightMaxHealthByWcid, K.MilestonePercents, K.MinSurvivors }.OrderBy(k => k).ToArray(), others);
        }

        // ---- hold set: validation on the write side ---------------------------------------------------------------------

        [TestMethod]
        public void HoldSet_KnightHealthByWcid_StoresCanonicalText_AndRefusesMalformed()
        {
            using var rig = NewRig();

            var ok = Hold(rig, "set", K.KnightMaxHealthByWcid, "1007652=8000,,1007650=12000");
            StringAssert.Contains(ok[0], "Not set", "a stray comma is an empty entry");

            ok = Hold(rig, "set", K.KnightMaxHealthByWcid, "1007652=8000,1007650=12000");
            StringAssert.Contains(ok[0], K.KnightMaxHealthByWcid + " = 1007650=12000,1007652=8000");
            Assert.AreEqual("1007650=12000,1007652=8000", rig.Values[Prop(K.KnightMaxHealthByWcid)]);

            rig.Writes.Clear();
            var bad = Hold(rig, "set", K.KnightMaxHealthByWcid, "1007650=500");
            StringAssert.Contains(bad[0], "Not set");
            StringAssert.Contains(bad[0], "outside 1000..100000");
            Assert.AreEqual(0, rig.Writes.Count, "a refused value writes nothing");
            Assert.AreEqual("1007650=12000,1007652=8000", rig.Values[Prop(K.KnightMaxHealthByWcid)], "and leaves the old value");
        }

        [TestMethod]
        public void HoldSet_MilestonesAndTint_StoreNormalizedText()
        {
            using var rig = NewRig();

            Hold(rig, "set", K.MilestonePercents, "25,75,50,75");
            Assert.AreEqual("75,50,25", rig.Values[Prop(K.MilestonePercents)]);

            Hold(rig, "set", K.MilestonePercents, "none");
            Assert.AreEqual("none", rig.Values[Prop(K.MilestonePercents)]);

            Hold(rig, "set", K.CorruptedTint, "67113857");
            Assert.AreEqual("0x04001381", rig.Values[Prop(K.CorruptedTint)]);

            rig.Writes.Clear();
            StringAssert.Contains(Hold(rig, "set", K.MilestonePercents, "75,100")[0], "Not set");
            StringAssert.Contains(Hold(rig, "set", K.CorruptedTint, "teal")[0], "Not set");
            Assert.AreEqual(0, rig.Writes.Count);
        }

        [TestMethod]
        public void HoldSet_MinSurvivorsAliases_StoreTheirNumber_AndTheRowStaysOneToThree()
        {
            using var rig = NewRig();

            Hold(rig, "set", K.MinSurvivors, "any");
            Assert.AreEqual("3", rig.Values[Prop(K.MinSurvivors)]);

            Hold(rig, "set", K.MinSurvivors, "all");
            Assert.AreEqual("1", rig.Values[Prop(K.MinSurvivors)]);

            Hold(rig, "set", K.MinSurvivors, "2");
            Assert.AreEqual("2", rig.Values[Prop(K.MinSurvivors)]);

            var clamped = Hold(rig, "set", K.MinSurvivors, "9");
            Assert.AreEqual("3", rig.Values[Prop(K.MinSurvivors)], "a number is still clamped to the row's 1..3");
            StringAssert.Contains(clamped[0], "clamped");

            rig.Writes.Clear();
            StringAssert.Contains(Hold(rig, "set", K.MinSurvivors, "most")[0], "Not set");
            Assert.AreEqual(0, rig.Writes.Count);

            Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind(K.MinSurvivors, out var row));
            Assert.AreEqual(SettingKind.Long, row.Kind);
            Assert.AreEqual(1d, row.Min);
            Assert.AreEqual(3d, row.Max);
        }

        // ---- the scope guard: a Hold command never writes a global key ---------------------------------------------------

        [TestMethod]
        public void HoldSet_OfAGlobalKey_IsRefused_AndWritesNothing()
        {
            using var rig = NewRig();

            var reply = Hold(rig, "set", "max_alive", "40");

            StringAssert.Contains(reply[0], "Not set");
            StringAssert.Contains(reply[0], "scoped");
            Assert.AreEqual(0, rig.Writes.Count);
            Assert.IsFalse(rig.Values.ContainsKey(GlobalProp));

            // The property name is accepted by the registry lookup too; it is refused the same way.
            Hold(rig, "set", GlobalProp, "40");
            Assert.AreEqual(0, rig.Writes.Count);
        }

        [TestMethod]
        public void HoldReset_All_TouchesOnlyHoldRows()
        {
            using var rig = NewRig();

            // A global key that IS overridden: the one a loop over every overridden row without a scope filter would reset.
            rig.Values[GlobalProp] = "5";
            rig.Values["world_events_announce_chat_type"] = "9";

            Hold(rig, "set", K.KnightMaxHealth, "12000");
            Hold(rig, "set", K.MinSurvivors, "any");
            rig.Writes.Clear();

            var reply = Hold(rig, "reset", "all");

            Assert.AreEqual(2, rig.Writes.Count, string.Join("; ", reply));
            Assert.IsTrue(rig.Writes.All(w => w.Property.StartsWith(K.PropertyPrefix, StringComparison.Ordinal)), "every write is a Hold property");
            Assert.AreEqual("5", rig.Values[GlobalProp], "the overridden global key is untouched");
            Assert.AreEqual("9", rig.Values["world_events_announce_chat_type"]);
            Assert.AreEqual("10000", rig.Values[Prop(K.KnightMaxHealth)], "the Hold row was written back to its default");
            StringAssert.StartsWith(reply[0], "Reset writes the current default");
            Assert.IsFalse(string.Join(" ", reply).Contains("Refused"), "a bulk loop skips other scopes silently");
        }

        [TestMethod]
        public void HoldReset_ANamedGlobalKey_OrAGlobalGroup_IsRefusedOrSkipped()
        {
            using var rig = NewRig();
            rig.Values[GlobalProp] = "5";

            var named = Hold(rig, "reset", "max_alive");
            StringAssert.Contains(named[0], "Refused");
            Assert.AreEqual(0, rig.Writes.Count);
            Assert.AreEqual("5", rig.Values[GlobalProp]);

            // "bosses" is a global-only group: to a Hold caller it does not exist, as in "hold settings bosses".
            StringAssert.StartsWith(Hold(rig, "reset", "bosses")[0], "No setting or group 'bosses'");
            Assert.AreEqual(0, rig.Writes.Count);
            Assert.AreEqual("5", rig.Values[GlobalProp]);
        }

        [TestMethod]
        public void HoldReset_ASharedGroup_ResetsOnlyTheHoldRowsInIt()
        {
            using var rig = NewRig();

            // "messages" holds both a global row (announce_chat_type) and a Hold row (milestone_percents).
            rig.Values["world_events_announce_chat_type"] = "9";
            Hold(rig, "set", K.MilestonePercents, "80,60");
            rig.Writes.Clear();

            var reply = Hold(rig, "reset", "messages");
            Assert.IsFalse(string.Join(" ", reply).Contains("Refused"));

            Assert.AreEqual(1, rig.Writes.Count);
            Assert.AreEqual(Prop(K.MilestonePercents), rig.Writes[0].Property);
            Assert.AreEqual("9", rig.Values["world_events_announce_chat_type"]);
        }

        [TestMethod]
        public void HoldSettings_ListsOnlyHoldRows()
        {
            using var rig = NewRig();
            rig.Values[GlobalProp] = "5";
            Hold(rig, "set", K.KnightMaxHealth, "12000");

            var head = string.Join("\n", Hold(rig, "settings"));
            StringAssert.Contains(head, "knights");
            StringAssert.Contains(head, K.KnightMaxHealth + " = 12000");
            Assert.IsFalse(head.Contains("max_alive"), "a global row is not in the Hold listing, set or not");
            Assert.IsFalse(head.Contains("<global>"));

            var all = string.Join("\n", Hold(rig, "settings", "all"));
            Assert.IsFalse(all.Contains("<global>"));
            Assert.IsTrue(all.Contains("<goal:hold>"));

            var group = string.Join("\n", Hold(rig, "settings", "knights"));
            StringAssert.Contains(group, K.MinSurvivors);

            StringAssert.StartsWith(Hold(rig, "settings", "max_alive")[0], "No setting group or setting");
            StringAssert.StartsWith(Hold(rig, "settings", "bosses")[0], "No setting group or setting");

            var one = Hold(rig, "settings", K.MinSurvivors);
            Assert.AreEqual(2, one.Count);
            StringAssert.Contains(one[1], K.MinSurvivors);
        }

        [TestMethod]
        public void HoldCheck_OfAGlobalKey_WouldBeRefused()
        {
            using var rig = NewRig();

            StringAssert.StartsWith(Hold(rig, "check", "max_alive", "40")[0], "Would be refused");
            StringAssert.Contains(Hold(rig, "check", K.MinSurvivors, "any")[0], "3 would be stored");
            Assert.AreEqual(0, rig.Writes.Count);
        }

        [TestMethod]
        public void HoldUsage_NamesTheHoldCommand_AndGlobalCommandStillListsEveryRow()
        {
            using var rig = NewRig();

            var use = Hold(rig, "bogus");
            Assert.AreEqual(1, use.Count);
            StringAssert.StartsWith(use[0], "Use: /worldevent hold settings");

            StringAssert.StartsWith(Hold(rig, "set", K.KnightMaxHealth)[0], "Use: /worldevent hold");

            var global = string.Join("\n", WorldEventCommands.SettingsReplies(rig.Service, SettingScope.Global, new[] { "describe", "all" }, null));
            Assert.IsTrue(global.Contains("<global>") && global.Contains("<goal:hold>"), "the global listing is unchanged: every row");
        }

        // ---- the command reaches SettingsReplies with the Hold scope ------------------------------------------------------

        [TestMethod]
        public void HandleHold_RunsTheServiceAtHoldScope()
        {
            using var rig = NewRig();
            var saved = WorldEventCommands.SettingsService;

            try
            {
                WorldEventCommands.SettingsService = () => rig.Service;

                Assert.AreEqual(WorldEventSettingsRegistry.HoldScope, WorldEventCommands.HoldCaller);
                Assert.IsFalse(WorldEventCommands.HoldCaller.IsGlobal);

                WorldEventCommands.HandleHold(null, new[] { "set", K.KnightMaxHealth, "14000" });
                Assert.AreEqual("14000", rig.Values[Prop(K.KnightMaxHealth)], "a Hold row is written");

                WorldEventCommands.HandleHold(null, new[] { "set", "max_alive", "40" });
                Assert.IsFalse(rig.WroteProperty(GlobalProp), "a global row is refused, so the caller scope was Hold");
                Assert.AreEqual(1, rig.Writes.Count);

                WorldEventCommands.HandleHold(null, new[] { "reset", "all" });
                Assert.IsTrue(rig.Writes.All(w => w.Property.StartsWith(K.PropertyPrefix, StringComparison.Ordinal)));

                // And the global command still cannot reach a Hold row.
                WorldEventCommands.HandleSettings(null, new[] { "set", K.KnightMaxHealth, "15000" });
                Assert.AreEqual("10000", rig.Values[Prop(K.KnightMaxHealth)]);
            }
            finally
            {
                WorldEventCommands.SettingsService = saved;
            }
        }

        [TestMethod]
        public void HoldReplies_LongErrorReply_FitsTheChatLineAfterWrapping()
        {
            using var rig = NewRig();

            var junk = string.Join(" ", Enumerable.Repeat("junkvalue", 25));
            Assert.IsTrue(junk.Length >= 220);

            var replies = Hold(rig, "set", K.KnightMaxHealthByWcid, junk);
            Assert.IsTrue(replies[0].Length > 250, "the raw reply is long enough to need wrapping: " + replies[0].Length);
            Assert.IsTrue(replies.SelectMany(l => WorldEventCommands.WrapChatLine(l, 240)).All(p => p.Length <= 250));
        }

        [TestMethod]
        public void Usage_BareCommand_IsWrappedAndListsHoldCheck()
        {
            var field = typeof(WorldEventCommands).GetField("Usage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var usage = (string)field.GetRawConstantValue();

            StringAssert.Contains(usage, "hold check <key> <value>");
            Assert.IsTrue(WorldEventCommands.WrapChatLine(usage, 240).All(p => p.Length <= 250));
        }

        [TestMethod]
        public void HoldReplies_UnknownKeyHint_NamesTheHoldCommand_AndGlobalHintIsUnchanged()
        {
            using var rig = NewRig();

            StringAssert.Contains(Hold(rig, "check", "nope", "1")[0], "/worldevent hold settings");
            Assert.AreEqual("No setting 'nope'. See /worldevent settings describe all.", rig.Service.CheckReply("nope", "1"));
        }

        [TestMethod]
        public void KnightHealthRange_IsPinnedToTheRowsRange()
        {
            Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind(K.KnightMaxHealth, out var row));
            Assert.AreEqual(row.Min, (double)WorldEventHoldRules.KnightHealthMin);
            Assert.AreEqual(row.Max, (double)WorldEventHoldRules.KnightHealthMax);
        }

        [TestMethod]
        public void Describe_StoredGarbageInAParsedRow_ShowsAWarning()
        {
            using var rig = NewRig();
            rig.Values[Prop(K.MilestonePercents)] = "75,banana";
            rig.Values[Prop(K.KnightMaxHealthByWcid)] = "1007650=12000";

            var lines = string.Join("\n", Hold(rig, "settings", "all")).Split('\n');
            var bad = lines.Single(l => l.Contains(K.MilestonePercents + " ="));
            var good = lines.Single(l => l.Contains(K.KnightMaxHealthByWcid + " ="));

            StringAssert.Contains(bad, "warning: stored value is invalid");
            Assert.IsFalse(good.Contains("warning:"));
        }

        // ---- presets are one shared folder: a preset of the other scope is never touched ---------------------------------

        [TestMethod]
        public void GlobalImport_OfAHoldExportedPreset_IsRefusedWholesale()
        {
            using var rig = NewRig();
            Hold(rig, "set", K.KnightMaxHealth, "13000");
            rig.Values[GlobalProp] = "5";
            Hold(rig, "export", "hp");
            rig.Writes.Clear();

            var reply = WorldEventCommands.SettingsReplies(rig.Service, SettingScope.Global, new[] { "import", "hp" }, null)[0];

            StringAssert.StartsWith(reply, "Not imported: hp is a goal:hold preset; this command is global");
            Assert.AreEqual(0, rig.Writes.Count, "nothing is written or reset");
            Assert.AreEqual("5", rig.Values[GlobalProp]);
        }

        [TestMethod]
        public void HoldImport_OfAGlobalExportedPreset_IsRefusedWholesale()
        {
            using var rig = NewRig();
            rig.Values[GlobalProp] = "5";
            Hold(rig, "set", K.KnightMaxHealth, "13000");
            WorldEventCommands.SettingsReplies(rig.Service, SettingScope.Global, new[] { "export", "gp" }, null);
            rig.Writes.Clear();

            var reply = Hold(rig, "import", "gp")[0];

            StringAssert.StartsWith(reply, "Not imported: gp is a global preset; this command is goal:hold");
            Assert.AreEqual(0, rig.Writes.Count);
            Assert.AreEqual("13000", rig.Values[Prop(K.KnightMaxHealth)]);
        }

        [TestMethod]
        public void HoldExport_OntoAGlobalPreset_LeavesTheFileByteIdentical()
        {
            using var rig = NewRig();
            rig.Values[GlobalProp] = "5";
            WorldEventCommands.SettingsReplies(rig.Service, SettingScope.Global, new[] { "export", "prod" }, null);
            var path = Path.Combine(rig.Folder, "prod.json");
            var before = File.ReadAllBytes(path);
            Hold(rig, "set", K.KnightMaxHealth, "13000");

            var reply = Hold(rig, "export", "prod")[0];

            StringAssert.StartsWith(reply, "Not exported: prod is a global preset");
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));

            // The same scope may overwrite its own preset.
            StringAssert.StartsWith(Hold(rig, "export", "mine")[0], "Exported");
            StringAssert.StartsWith(Hold(rig, "export", "mine")[0], "Exported");
        }
        // ---- export / import under the Hold scope --------------------------------------------------------------------------

        [TestMethod]
        public void HoldExportImport_RoundTrips_AndNeverCarriesAGlobalKey()
        {
            using var rig = NewRig();
            rig.Values[GlobalProp] = "5";

            Hold(rig, "set", K.KnightMaxHealthByWcid, "1007650=12000,1007652=8000");
            Hold(rig, "set", K.MilestonePercents, "80,60,40");
            Hold(rig, "set", K.CorruptedTint, "0x0400137D");
            Hold(rig, "set", K.MinSurvivors, "any");
            Hold(rig, "set", K.KnightMaxHealth, "13000");

            var export = Hold(rig, "export", "night");
            StringAssert.StartsWith(export[0], "Exported 5 setting(s)");

            var json = File.ReadAllText(Path.Combine(rig.Folder, "night.json"));
            StringAssert.Contains(json, "goal:hold");
            Assert.IsFalse(json.Contains("max_alive"), "the preset holds Hold keys only");

            Hold(rig, "reset", "all");
            Assert.AreEqual("10000", rig.Values[Prop(K.KnightMaxHealth)]);

            var import = Hold(rig, "import", "night");
            StringAssert.StartsWith(import[0], "Imported 'night'");
            Assert.AreEqual("1007650=12000,1007652=8000", rig.Values[Prop(K.KnightMaxHealthByWcid)]);
            Assert.AreEqual("80,60,40", rig.Values[Prop(K.MilestonePercents)]);
            Assert.AreEqual("0x0400137D", rig.Values[Prop(K.CorruptedTint)]);
            Assert.AreEqual("3", rig.Values[Prop(K.MinSurvivors)]);
            Assert.AreEqual("13000", rig.Values[Prop(K.KnightMaxHealth)]);
            Assert.AreEqual("5", rig.Values[GlobalProp], "the global key survived export, reset and a replacing import");
            Assert.IsFalse(rig.WroteProperty(GlobalProp));
        }

        [TestMethod]
        public void HoldExport_All_IsHoldRowsOnly()
        {
            using var rig = NewRig();

            Hold(rig, "export", "--all", "everything");
            var file = rig.Service.BuildPreset(WorldEventSettingsRegistry.HoldScope, "x", true);

            Assert.AreEqual(WorldEventSettingsRegistry.Default.Rows.Count(r => r.Scope.Equals(WorldEventSettingsRegistry.HoldScope)), file.Settings.Count);
            Assert.IsFalse(file.Settings.ContainsKey("max_alive"));
            Assert.IsTrue(file.Settings.ContainsKey(K.MinSurvivors));
            Assert.IsFalse(File.ReadAllText(Path.Combine(rig.Folder, "everything.json")).Contains("max_alive"));
        }

        [TestMethod]
        public void HoldImport_RefusesAGlobalKeyAndAMalformedValue_FromAHandWrittenPreset()
        {
            using var rig = NewRig();
            Directory.CreateDirectory(rig.Folder);
            File.WriteAllText(Path.Combine(rig.Folder, "hand.json"),
                "{\"preset\":\"hand\",\"settings\":{\"max_alive\":\"40\",\"" + K.MilestonePercents + "\":\"0\",\"" + K.KnightMaxHealthByWcid + "\":\"1007650=abc\",\"" +
                K.MinSurvivors + "\":\"all\",\"nonsense\":\"1\"}}");

            var reply = Hold(rig, "import", "hand", "--merge")[0];

            Assert.IsFalse(rig.WroteProperty(GlobalProp), "a global key in a Hold preset is never written");
            Assert.AreEqual("1", rig.Values[Prop(K.MinSurvivors)], "the valid alias entry is applied");
            Assert.IsFalse(rig.WroteProperty(Prop(K.MilestonePercents)), "a malformed value is refused");
            Assert.IsFalse(rig.WroteProperty(Prop(K.KnightMaxHealthByWcid)));
            StringAssert.Contains(reply, "Refused");
            StringAssert.Contains(reply, "scoped");
            StringAssert.Contains(reply, "Unknown, skipped: nonsense");
        }

        [TestMethod]
        public void HoldImport_ReplacingImport_DoesNotResetAnOverriddenGlobalKey()
        {
            using var rig = NewRig();
            rig.Values[GlobalProp] = "5";
            Hold(rig, "set", K.KnightMaxHealth, "13000");
            Hold(rig, "export", "keep");
            Hold(rig, "set", K.UndefendedSeconds, "300");
            rig.Writes.Clear();

            Hold(rig, "import", "keep");

            Assert.IsTrue(rig.Writes.All(w => w.Property.StartsWith(K.PropertyPrefix, StringComparison.Ordinal)));
            Assert.AreEqual("5", rig.Values[GlobalProp]);
        }
    }
}
