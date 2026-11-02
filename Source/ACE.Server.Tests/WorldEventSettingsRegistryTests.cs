using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Describe, check and registry validation. Every test goes through WorldEventSettingsTestRig because PropertyManager reads throw in a unit test.</summary>
    [TestClass]
    public class WorldEventSettingsRegistryTests
    {
        private static WorldEventSettingRow Row(WorldEventSettingsTestRig rig, string key)
        {
            Assert.IsTrue(rig.Registry.TryFind(key, out var row), key);
            return row;
        }

        // ---- describe: source per kind -------------------------------------------------------------------------

        [TestMethod]
        public void Describe_NonPositiveIsUnset_FallsBackToJsonThenDefault()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "g_long");

            var d = rig.Service.Describe(row);
            Assert.AreEqual(SettingSource.Default, d.Source);
            Assert.AreEqual("0", d.Value);
            Assert.IsFalse(d.Overridden);

            rig.Json["waves.maxAlive"] = "40";
            d = rig.Service.Describe(row);
            Assert.AreEqual(SettingSource.Json, d.Source);
            Assert.AreEqual("40", d.Value);
            Assert.IsFalse(d.Overridden);

            rig.Values["t_g_long"] = "25";
            d = rig.Service.Describe(row);
            Assert.AreEqual(SettingSource.Property, d.Source);
            Assert.AreEqual("25", d.Value);
            Assert.IsTrue(d.Overridden);

            rig.Values["t_g_long"] = "-4";
            Assert.AreEqual(SettingSource.Json, rig.Service.Describe(row).Source, "a negative number is unset too");
        }

        [TestMethod]
        public void Describe_ZeroIsAValue_SourceIsDefaultOnlyWhileItEqualsTheRegisteredDefault()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "g_zero");

            Assert.AreEqual(SettingSource.Default, rig.Service.Describe(row).Source);
            Assert.AreEqual("60", rig.Service.Describe(row).Value);

            rig.Values["t_g_zero"] = "0";
            var d = rig.Service.Describe(row);
            Assert.AreEqual(SettingSource.Property, d.Source);
            Assert.AreEqual("0", d.Value, "0 is the value, not a request for the default");
            Assert.IsTrue(d.Overridden);
        }

        [TestMethod]
        public void Describe_NonPositiveRow_WhosePropertyDefaultsAboveZero_IsNotSetOutOfTheBox()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "h_dbl");

            var d = rig.Service.Describe(row);
            Assert.AreEqual("0.5", d.Value);
            Assert.AreEqual(SettingSource.Default, d.Source);
            Assert.IsFalse(d.Overridden);

            rig.Values["t_h_dbl"] = "0.6";
            Assert.IsTrue(rig.Service.Describe(row).Overridden);
        }

        [TestMethod]
        public void Describe_Double_NonFinite_UsesTheDefaultAndWarns()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "h_dbl");

            rig.Values["t_h_dbl"] = "NaN";
            var d = rig.Service.Describe(row);

            Assert.AreEqual(SettingSource.Default, d.Source);
            Assert.AreEqual("0.5", d.Value);
            StringAssert.Contains(d.Warning, "not a finite number");
        }

        [TestMethod]
        public void Describe_OutOfRange_FlagsClampWithWarning()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "g_long");

            rig.Values["t_g_long"] = "500";
            var d = rig.Service.Describe(row);

            Assert.IsTrue(d.Clamped);
            Assert.AreEqual("500", d.Value, "describe shows the stored value; the runtime reader is unchanged");
            StringAssert.Contains(d.Warning, "clamp it to 200");
        }

        [TestMethod]
        public void Describe_Bool_ShowsOnOffAndSource()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "g_bool");

            var d = rig.Service.Describe(row);
            Assert.AreEqual("on", d.Value);
            Assert.AreEqual(SettingSource.Default, d.Source);

            rig.Values["t_g_bool"] = "False";
            d = rig.Service.Describe(row);
            Assert.AreEqual("off", d.Value);
            Assert.AreEqual(SettingSource.Property, d.Source);
        }

        [TestMethod]
        public void Describe_ChoiceAndText_EmptyIsUnset()
        {
            using var rig = new WorldEventSettingsTestRig();
            var pick = Row(rig, "h_pick");
            var text = Row(rig, "h_text");

            Assert.AreEqual(SettingSource.Default, rig.Service.Describe(pick).Source);
            Assert.AreEqual(SettingSource.Default, rig.Service.Describe(text).Source);

            rig.Values["t_h_pick"] = "b";
            rig.Values["t_h_text"] = "hello";
            Assert.AreEqual(SettingSource.Property, rig.Service.Describe(pick).Source);
            Assert.AreEqual("hello", rig.Service.Describe(text).Value);
            Assert.IsNull(rig.Service.Describe(pick).Warning);

            rig.Values["t_h_pick"] = "zzz";
            StringAssert.Contains(rig.Service.Describe(pick).Warning, "not one of");
        }

        // ---- check ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Check_Number_ClampsWithWarning()
        {
            using var rig = new WorldEventSettingsTestRig();

            var high = rig.Service.Check(Row(rig, "g_long"), "999");
            Assert.IsFalse(high.Refused);
            Assert.IsTrue(high.Clamped);
            Assert.AreEqual("200", high.Normalized);
            StringAssert.Contains(high.Warning, "clamped to 200");

            var low = rig.Service.Check(Row(rig, "h_long"), "-50");
            Assert.AreEqual("-5", low.Normalized);

            var ok = rig.Service.Check(Row(rig, "h_dbl"), "0.25");
            Assert.AreEqual("0.25", ok.Normalized);
            Assert.IsNull(ok.Warning);
        }

        [TestMethod]
        public void Check_RejectsNonFiniteAndUnparsable()
        {
            using var rig = new WorldEventSettingsTestRig();
            var dbl = Row(rig, "g_dbl");

            foreach (var bad in new[] { "NaN", "Infinity", "-Infinity", "abc", "1,5", "" })
                Assert.IsTrue(rig.Service.Check(dbl, bad).Refused, $"double '{bad}'");

            foreach (var bad in new[] { "1.5", "abc", "99999999999999999999" })
                Assert.IsTrue(rig.Service.Check(Row(rig, "g_long"), bad).Refused, $"long '{bad}'");

            Assert.IsTrue(rig.Service.Check(Row(rig, "g_bool"), "maybe").Refused);
            Assert.IsTrue(rig.Service.Check(Row(rig, "h_pick"), "d").Refused);
            Assert.IsTrue(rig.Service.Check(Row(rig, "h_text"), "a\tb").Refused);
        }

        [TestMethod]
        public void Check_NonPositiveIsUnset_StoresZeroWithoutClamping()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "h_dbl");

            var zero = rig.Service.Check(row, "0");
            Assert.AreEqual("0", zero.Normalized);
            Assert.IsNull(zero.Warning);

            var neg = rig.Service.Check(row, "-3");
            Assert.AreEqual("0", neg.Normalized);
            StringAssert.Contains(neg.Warning, "stored as 0");
        }

        [TestMethod]
        public void Check_BoolChoiceText_Normalize()
        {
            using var rig = new WorldEventSettingsTestRig();

            Assert.AreEqual("True", rig.Service.Check(Row(rig, "g_bool"), "ON").Normalized);
            Assert.AreEqual("False", rig.Service.Check(Row(rig, "g_bool"), "no").Normalized);
            Assert.AreEqual("b", rig.Service.Check(Row(rig, "h_pick"), " B ").Normalized);
            Assert.AreEqual("some text", rig.Service.Check(Row(rig, "h_text"), " some text ").Normalized);
        }

        // ---- registry validation -------------------------------------------------------------------------------

        private static WorldEventSettingRow Bare(string key, string prop, SettingKind kind = SettingKind.Long, SettingUnsetRule unset = SettingUnsetRule.NonPositiveIsUnset) =>
            new WorldEventSettingRow { Key = key, PropertyName = prop, Group = "g", Kind = kind, Unset = unset, CodeDefault = "0", Min = 0, Max = 10 };

        [TestMethod]
        public void Registry_RejectsInconsistentRows()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("a", "p1"), Bare("a", "p2") }));
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("a", "p1"), Bare("b", "p1") }));
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("a", "p1", SettingKind.Bool) }));
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("a", "p1", SettingKind.Text) }));
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("a", "p1", SettingKind.Choice, SettingUnsetRule.EmptyIsUnset) }));
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("A", "p1") }));

            var infinite = new WorldEventSettingRow { Key = "a", PropertyName = "p", Group = "g", Kind = SettingKind.Double, CodeDefault = "0" };
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { infinite }));
        }

        [TestMethod]
        public void Scope_Equality_AndGoalNameRules()
        {
            Assert.AreEqual(SettingScope.Global, SettingScope.Global);
            Assert.AreEqual(SettingScope.Goal("Hold"), SettingScope.Goal("hold"));
            Assert.AreNotEqual(SettingScope.Global, SettingScope.Goal("hold"));
            Assert.AreNotEqual(SettingScope.Goal("hold"), SettingScope.Goal("kill"));
            Assert.ThrowsExactly<ArgumentException>(() => SettingScope.Goal(""));
            Assert.ThrowsExactly<ArgumentException>(() => SettingScope.Goal("a b"));
        }

        // ---- the shipped rows ----------------------------------------------------------------------------------

        private static readonly string[] ExpectedGlobalKeys =
        {
            "max_alive", "teaser_lead_seconds", "progress_interval_seconds", "announce_chat_type", "boss_scale_multiplier",
            "boss_hits_to_kill", "boss_damage_rating_min", "boss_damage_rating_max", "boss_per_player", "boss_health_cap",
            "boss_throughput_floor_health", "boss_throughput_cap_health", "participation_payout_enabled"
        };

        [TestMethod]
        public void DefaultRows_GlobalRowsAreTheThirteenKeys_AndSeekShareIsAHoldRow()
        {
            var rows = WorldEventSettingsRegistry.Default.Rows;

            // The Hold slice (WorldEventSettingsRegistry.Hold.cs) added goal-scoped rows; the global rows are still exactly these 13.
            CollectionAssert.AreEquivalent(ExpectedGlobalKeys, rows.Where(r => r.Scope.IsGlobal).Select(r => r.Key).ToArray());
            Assert.IsTrue(rows.Where(r => !r.Scope.IsGlobal).All(r => r.Scope.Equals(WorldEventSettingsRegistry.HoldScope)));

            // seek_share is the Hold goal's own row, never a global one.
            Assert.IsTrue(WorldEventSettingsRegistry.Default.TryFind("world_events_hold_seek_share", out var seek));
            Assert.AreEqual(WorldEventSettingsRegistry.HoldScope, seek.Scope);
        }

        [TestMethod]
        public void DefaultRows_MatchTheRegisteredPropertyTypeAndDefault()
        {
            foreach (var row in WorldEventSettingsRegistry.Default.Rows)
            {
                Assert.IsTrue(PropertyAdminService.TryResolveType(row.PropertyName, out var type), $"{row.PropertyName} is not a registered property");
                Assert.AreEqual(WorldEventSettingsService.TypeFor(row), type, row.PropertyName);

                if (row.Kind == SettingKind.Long)
                    Assert.AreEqual(long.Parse(row.CodeDefault), DefaultPropertyManager.DefaultLongProperties[row.PropertyName].Item, row.PropertyName);
                else if (row.Kind == SettingKind.Double)
                    Assert.AreEqual(double.Parse(row.CodeDefault, System.Globalization.CultureInfo.InvariantCulture), DefaultPropertyManager.DefaultDoubleProperties[row.PropertyName].Item, row.PropertyName);
                else if (row.Kind == SettingKind.Bool)
                    Assert.AreEqual(bool.Parse(row.CodeDefault), DefaultPropertyManager.DefaultBooleanProperties[row.PropertyName].Item, row.PropertyName);
            }
        }

        // ---- the command surface -------------------------------------------------------------------------------

        [TestMethod]
        public void SettingsReplies_DescribeSetAndUsage()
        {
            using var rig = new WorldEventSettingsTestRig();
            var global = SettingScope.Global;

            var head = WorldEventCommands.SettingsReplies(rig.Service, global, new string[0], null);
            StringAssert.Contains(head[0], "Groups:");

            var set = WorldEventCommands.SettingsReplies(rig.Service, global, new[] { "set", "g_long", "30" }, null);
            StringAssert.Contains(set[0], "g_long = 30");
            Assert.AreEqual("30", rig.Values["t_g_long"]);

            var check = WorldEventCommands.SettingsReplies(rig.Service, global, new[] { "check", "g_long", "900" }, null);
            StringAssert.Contains(check[0], "clamped to 200");
            Assert.AreEqual("30", rig.Values["t_g_long"], "check never writes");

            var goalRow = WorldEventCommands.SettingsReplies(rig.Service, global, new[] { "set", "h_dbl", "0.4" }, null);
            StringAssert.Contains(goalRow[0], "Not set");
            Assert.IsFalse(rig.WroteProperty("t_h_dbl"));

            StringAssert.StartsWith(WorldEventCommands.SettingsReplies(rig.Service, global, new[] { "bogus" }, null)[0], "Use:");
            StringAssert.StartsWith(WorldEventCommands.SettingsReplies(rig.Service, global, new[] { "set", "g_long" }, null)[0], "Use:");
        }

        [TestMethod]
        public void Registry_RejectsKeysThatShadowReservedWordsOrGroups()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("all", "p1") }));
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("set", "p1") }));

            // Bare() puts every row in group "g"; a row keyed like another row's group is refused whichever comes first.
            Assert.ThrowsExactly<InvalidOperationException>(() => new WorldEventSettingsRegistry(new[] { Bare("g", "p1") }));
        }

        [TestMethod]
        public void Check_LongOverflow_ReportsOutOfRange_NotNotAWholeNumber()
        {
            using var rig = new WorldEventSettingsTestRig();
            var row = Row(rig, "g_long");

            StringAssert.Contains(rig.Service.Check(row, "99999999999999999999").Warning, "out of range");
            StringAssert.Contains(rig.Service.Check(row, "-99999999999999999999").Warning, "out of range");
            StringAssert.Contains(rig.Service.Check(row, "12abc").Warning, "not a whole number");
            StringAssert.Contains(rig.Service.Check(row, "1.5").Warning, "not a whole number");
        }

        [TestMethod]
        public void TakePresetArgs_AcceptsTheFlagInAnyPosition()
        {
            Assert.IsTrue(WorldEventCommands.TakePresetArgs(new[] { "--merge", "night" }, "--merge", out var name, out var flagged));
            Assert.AreEqual("night", name);
            Assert.IsTrue(flagged);

            Assert.IsTrue(WorldEventCommands.TakePresetArgs(new[] { "night", "--merge" }, "--merge", out name, out flagged));
            Assert.AreEqual("night", name);
            Assert.IsTrue(flagged);

            Assert.IsTrue(WorldEventCommands.TakePresetArgs(new[] { "night" }, "--all", out name, out flagged));
            Assert.IsFalse(flagged);

            Assert.IsFalse(WorldEventCommands.TakePresetArgs(new[] { "--merge" }, "--merge", out _, out _), "a flag alone is not a name");
            Assert.IsFalse(WorldEventCommands.TakePresetArgs(new[] { "-x", "night" }, "--merge", out _, out _), "an unknown flag is refused");
            Assert.IsFalse(WorldEventCommands.TakePresetArgs(new[] { "--all", "night" }, "--merge", out _, out _), "the other command's flag is refused");
            Assert.IsFalse(WorldEventCommands.TakePresetArgs(new[] { "a", "b" }, "--merge", out _, out _));
            Assert.IsFalse(WorldEventCommands.TakePresetArgs(new string[0], "--merge", out _, out _));
        }

        [TestMethod]
        public void SettingsReplies_ExportAndImport_TakeTheFlagBeforeTheName()
        {
            using var rig = new WorldEventSettingsTestRig();
            rig.Service.Set(SettingScope.Global, "g_long", "30", null);

            var export = WorldEventCommands.SettingsReplies(rig.Service, SettingScope.Global, new[] { "export", "--all", "night" }, null);
            StringAssert.StartsWith(export[0], "Exported");
            Assert.IsTrue(System.IO.File.Exists(System.IO.Path.Combine(rig.Folder, "night.json")));

            rig.Service.Reset(SettingScope.Global, "all", null);
            var import = WorldEventCommands.SettingsReplies(rig.Service, SettingScope.Global, new[] { "import", "--merge", "night" }, null);
            StringAssert.StartsWith(import[0], "Imported 'night'");
            Assert.AreEqual("30", rig.Values["t_g_long"]);
        }

        [TestMethod]
        public void HandleSettings_RunsTheServiceAtGlobalScope()
        {
            using var rig = new WorldEventSettingsTestRig();
            var saved = WorldEventCommands.SettingsService;

            try
            {
                WorldEventCommands.SettingsService = () => rig.Service;

                Assert.AreEqual(SettingScope.Global, WorldEventCommands.SettingsCaller);

                WorldEventCommands.HandleSettings(null, new[] { "set", "g_long", "12" });
                Assert.AreEqual("12", rig.Values["t_g_long"], "a Global row is written");

                WorldEventCommands.HandleSettings(null, new[] { "set", "h_dbl", "0.4" });
                Assert.IsFalse(rig.WroteProperty("t_h_dbl"), "a goal-scoped row is refused, so the caller scope was Global");
                Assert.AreEqual(1, rig.Writes.Count);

                WorldEventCommands.HandleSettings(null, new[] { "reset", "all" });
                Assert.IsFalse(rig.Writes.Any(w => w.Property.StartsWith("t_h_")));
            }
            finally
            {
                WorldEventCommands.SettingsService = saved;
            }
        }

        [TestMethod]
        public void WrapChatLine_KeepsEveryPieceUnderTheLimit()
        {
            var line = string.Join(" ", Enumerable.Repeat("word", 120));
            var pieces = WorldEventCommands.WrapChatLine(line, 240);

            Assert.IsTrue(pieces.Count > 1);
            Assert.IsTrue(pieces.All(p => p.Length <= 240));
            Assert.AreEqual(line, string.Join(" ", pieces));
            Assert.AreEqual(1, WorldEventCommands.WrapChatLine("short", 240).Count);
        }
    }
}
