using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The NegativeIsUnset rule: below 0 is "not set" (the JSON value, else the code default), 0 and above are real values
    /// clamped to the row's range. Every test goes through WorldEventSettingsTestRig because PropertyManager reads throw in a unit test.
    /// </summary>
    [TestClass]
    public class WorldEventSettingsNegativeUnsetTests
    {
        private static readonly SettingScope Hold = WorldEventSettingsTestRig.Hold;

        private static WorldEventSettingRow R(string key, SettingKind kind, string def, double min, double max, SettingUnsetRule unset, string json = null) =>
            new WorldEventSettingRow
            {
                Key = key, PropertyName = "t_" + key, Kind = kind, Scope = Hold, Group = "neg", CodeDefault = def, Min = min, Max = max,
                Unset = unset, JsonPath = json, Applies = SettingApplies.Live, Description = key
            };

        private static List<WorldEventSettingRow> Rows() => new List<WorldEventSettingRow>
        {
            R("n_dbl", SettingKind.Double, "-1", 0, 5, SettingUnsetRule.NegativeIsUnset, json: "protect.x"),
            R("n_min", SettingKind.Double, "-1", 0.25, 5, SettingUnsetRule.NegativeIsUnset),
            R("n_long", SettingKind.Long, "-1", 2, 10, SettingUnsetRule.NegativeIsUnset),
            R("p_dbl", SettingKind.Double, "0", 0, 5, SettingUnsetRule.NonPositiveIsUnset, json: "protect.y"),
            new WorldEventSettingRow
            {
                Key = "n_pick", PropertyName = "t_n_pick", Kind = SettingKind.Choice, Scope = Hold, Group = "neg", CodeDefault = "on",
                Choices = new[] { "on", "off" }, Unset = SettingUnsetRule.EmptyIsUnset, JsonPath = "protect.pose", Applies = SettingApplies.Live, Description = "n_pick"
            }
        };

        private static WorldEventSettingRow Row(WorldEventSettingsTestRig rig, string key)
        {
            Assert.IsTrue(rig.Registry.TryFind(key, out var row), key);
            return row;
        }

        // ---- describe ----------------------------------------------------------------------------------------------

        [TestMethod]
        public void Describe_ZeroIsHonoured_WhereNonPositiveTreatsItAsUnset()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            rig.Json["protect.x"] = "0.7";
            rig.Json["protect.y"] = "0.7";

            rig.Values["t_n_dbl"] = "0";
            rig.Values["t_p_dbl"] = "0";

            var neg = rig.Service.Describe(Row(rig, "n_dbl"));
            Assert.AreEqual(SettingSource.Property, neg.Source, "0 is a real value under NegativeIsUnset");
            Assert.AreEqual("0", neg.Value);
            Assert.IsTrue(neg.Overridden);

            var np = rig.Service.Describe(Row(rig, "p_dbl"));
            Assert.AreEqual(SettingSource.Json, np.Source, "the control: NonPositiveIsUnset reads 0 as not set");
            Assert.AreEqual("0.7", np.Value);
        }

        [TestMethod]
        public void Describe_NegativeValues_FallThroughToJsonThenDefault()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            var row = Row(rig, "n_dbl");

            foreach (var stored in new[] { "-1", "-0.5", "-250" })
            {
                rig.Values["t_n_dbl"] = stored;
                rig.Json.Remove("protect.x");

                var d = rig.Service.Describe(row);
                Assert.AreEqual(SettingSource.Default, d.Source, stored);
                Assert.IsFalse(d.Overridden, stored);
                Assert.IsFalse(d.Clamped, $"{stored}: a negative value is never clamped");

                rig.Json["protect.x"] = "1.5";
                d = rig.Service.Describe(row);
                Assert.AreEqual(SettingSource.Json, d.Source, stored);
                Assert.AreEqual("1.5", d.Value, stored);
            }
        }

        [TestMethod]
        public void Describe_OutOfTheBox_IsNotSet_AndTheListingSaysSo()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());

            var d = rig.Service.Describe(Row(rig, "n_long"));
            Assert.AreEqual(SettingSource.Default, d.Source);
            Assert.AreEqual("-1", d.Value, "Describe keeps the raw text so an export round-trips as unset");
            Assert.IsFalse(d.Overridden);

            var line = rig.Service.Lines(new[] { Row(rig, "n_long") }).Single();
            StringAssert.StartsWith(line, "n_long = not set");

            rig.Values["t_n_long"] = "4";
            StringAssert.StartsWith(rig.Service.Lines(new[] { Row(rig, "n_long") }).Single(), "n_long = 4");
        }

        [TestMethod]
        public void Describe_StoredZeroBelowMin_IsFlaggedAsClamped()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            rig.Values["t_n_min"] = "0";

            var d = rig.Service.Describe(Row(rig, "n_min"));
            Assert.AreEqual(SettingSource.Property, d.Source);
            Assert.IsTrue(d.Clamped, "0 is set, and below the 0.25 minimum");
        }

        // ---- check -------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Check_ZeroIsStoredAsZero()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());

            var c = rig.Service.Check(Row(rig, "n_dbl"), "0");
            Assert.IsFalse(c.Refused);
            Assert.AreEqual("0", c.Normalized);
            Assert.IsNull(c.Warning);
            Assert.IsFalse(c.Clamped);
        }

        [TestMethod]
        public void Check_NegativeIsStoredAsMinusOne_NeverClamped()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());

            var one = rig.Service.Check(Row(rig, "n_dbl"), "-1");
            Assert.AreEqual("-1", one.Normalized);
            Assert.IsNull(one.Warning, "-1 is the unset marker itself");

            var half = rig.Service.Check(Row(rig, "n_dbl"), "-0.5");
            Assert.AreEqual("-1", half.Normalized);
            Assert.IsFalse(half.Clamped);
            StringAssert.Contains(half.Warning, "stored as -1");

            var lng = rig.Service.Check(Row(rig, "n_long"), "-7");
            Assert.AreEqual("-1", lng.Normalized);
            Assert.IsFalse(lng.Clamped);
        }

        [TestMethod]
        public void Check_PositiveBelowMin_ClampsToMin()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());

            var low = rig.Service.Check(Row(rig, "n_min"), "0.1");
            Assert.IsTrue(low.Clamped);
            Assert.AreEqual("0.25", low.Normalized);

            var zero = rig.Service.Check(Row(rig, "n_min"), "0");
            Assert.IsTrue(zero.Clamped, "0 is a set value, so it clamps to Min");
            Assert.AreEqual("0.25", zero.Normalized);

            var high = rig.Service.Check(Row(rig, "n_long"), "50");
            Assert.AreEqual("10", high.Normalized);
        }

        // ---- write paths -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Set_ThenReset_WritesTheShippedMinusOne()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());

            var set = rig.Service.Set(Hold, "n_long", "4", null);
            CollectionAssert.Contains(set.Written, "n_long");
            Assert.AreEqual("4", rig.Values["t_n_long"]);

            var reset = rig.Service.Reset(Hold, "n_long", null);
            CollectionAssert.Contains(reset.Written, "n_long");
            Assert.AreEqual("-1", rig.Values["t_n_long"], "reset writes the property's shipped default, not 0");
            Assert.AreEqual(SettingSource.Default, rig.Service.Describe(Row(rig, "n_long")).Source);
        }

        [TestMethod]
        public void Set_Negative_IsTheExplicitUnsetPath()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            rig.Values["t_n_dbl"] = "2";

            var o = rig.Service.Set(Hold, "n_dbl", "-3", null);
            CollectionAssert.Contains(o.Written, "n_dbl");
            Assert.AreEqual("-1", rig.Values["t_n_dbl"]);
        }

        [TestMethod]
        public void Set_FromGlobalCaller_IsRefused()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());

            var o = rig.Service.Set(SettingScope.Global, "n_dbl", "2", null);
            Assert.AreEqual(1, o.Refused.Count);
            Assert.IsFalse(rig.WroteProperty("t_n_dbl"));
        }

        // ---- presets: an unset row is never frozen at its fallback ------------------------------------------------------

        [TestMethod]
        public void PlainExport_OmitsUnsetRows()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            rig.Json["protect.x"] = "1.5";
            rig.Json["protect.pose"] = "off";
            rig.Values["t_n_long"] = "4";

            var file = rig.Service.BuildPreset(Hold, "p", all: false);

            CollectionAssert.AreEquivalent(new[] { "n_long" }, new List<string>(file.Settings.Keys));
        }

        [TestMethod]
        public void ExportAll_WritesUnsetMarkers_NotTheFallbacks()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            rig.Json["protect.x"] = "1.5";
            rig.Json["protect.y"] = "0.7";
            rig.Json["protect.pose"] = "off";
            rig.Values["t_n_long"] = "4";

            var file = rig.Service.BuildPreset(Hold, "p", all: true);

            Assert.AreEqual("-1", file.Settings["n_dbl"], "not the goals.json 1.5");
            Assert.AreEqual("-1", file.Settings["n_min"]);
            Assert.AreEqual("0", file.Settings["p_dbl"], "not the goals.json 0.7");
            Assert.AreEqual("", file.Settings["n_pick"], "not the goals.json off, nor the code default on");
            Assert.AreEqual("4", file.Settings["n_long"], "a set row exports its value");
        }

        [TestMethod]
        public void ExportAll_ThenReplaceImport_LeavesUnsetRowsUnwritten_AndRoundTripsSetRows()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            rig.Json["protect.x"] = "1.5";
            rig.Json["protect.pose"] = "off";
            rig.Values["t_n_long"] = "4";

            CollectionAssert.Contains(rig.Service.Export(Hold, "snap", all: true).Written, "n_dbl");

            rig.Values.Clear();
            rig.Writes.Clear();

            var o = rig.Service.Import(Hold, "snap", merge: false, null);

            Assert.IsFalse(rig.Values.ContainsKey("t_n_dbl"), "an unset NegativeIsUnset row stays unwritten");
            Assert.IsFalse(rig.Values.ContainsKey("t_n_pick"), "an unset Choice row stays unwritten");
            Assert.IsFalse(rig.Values.ContainsKey("t_p_dbl"), "an unset NonPositiveIsUnset row stays unwritten");
            Assert.AreEqual("4", rig.Values["t_n_long"], "the set row round-trips");
            CollectionAssert.AreEqual(new[] { "n_long" }, o.Written);

            // The rows still follow goals.json after the round trip.
            rig.Json["protect.x"] = "2.5";
            Assert.AreEqual("2.5", rig.Service.Describe(Row(rig, "n_dbl")).Value);
        }

        [TestMethod]
        public void MergeImport_OfAnUnsetMarker_UnsetsARowThatIsSet()
        {
            using var rig = new WorldEventSettingsTestRig(Rows());
            rig.Service.Export(Hold, "blank", all: true);

            rig.Values["t_n_dbl"] = "3";
            rig.Service.Import(Hold, "blank", merge: true, null);

            Assert.AreEqual("-1", rig.Values["t_n_dbl"], "the file says not set and the row is set, so the marker is written");
        }
        // ---- registry validation -----------------------------------------------------------------------------------

        [TestMethod]
        public void Registry_AcceptsNegativeIsUnsetOnNumbersOnly()
        {
            _ = new WorldEventSettingsRegistry(new[] { R("a", SettingKind.Long, "-1", 0, 3, SettingUnsetRule.NegativeIsUnset) });
            _ = new WorldEventSettingsRegistry(new[] { R("a", SettingKind.Double, "-1", 0, 3, SettingUnsetRule.NegativeIsUnset) });

            foreach (var kind in new[] { SettingKind.Bool, SettingKind.Choice, SettingKind.Text })
                Assert.ThrowsExactly<InvalidOperationException>(
                    () => new WorldEventSettingsRegistry(new[] { R("a", kind, "", 0, 3, SettingUnsetRule.NegativeIsUnset) }), kind.ToString());
        }
    }
}
