using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.Knots;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// pinnacles.json parse and lint (Docs/Knots/KNOTS-DESIGN.md section 8), through the same
    /// ThreadDungeonStore.Parse entry the shipped loader uses. Every lint rule has a discriminating pair: the
    /// row that breaks it is dropped with a diagnostic, and the otherwise-identical valid row loads.
    /// </summary>
    [TestClass]
    public class PinnacleRegistryTests
    {
        private const string EmptyIndex = "{\"dungeons\":[]}";
        private const string EmptyBosses = "{\"bosses\":[]}";
        private const string EmptyModifiers = "{\"modifiers\":[],\"xpLadder\":[]}";

        private static ThreadDungeonStore Parse(string pinnaclesJson)
            => ThreadDungeonStore.Parse(EmptyIndex, EmptyBosses, EmptyModifiers, new Dictionary<string, string>(), pinnaclesJson: pinnaclesJson);

        private static string File(params string[] rows)
            => "{\"version\":1,\"pinnacles\":[" + string.Join(",", rows) + "]}";

        /// <summary>A valid row with <paramref name="patch"/> spliced in as extra or overriding members.</summary>
        private static string Row(string key = "ember", string patch = null)
            => "{\"key\":\"" + key + "\",\"name\":\"Ember Tyrant\",\"destination\":{\"kind\":\"instanced_landblock\",\"landblock\":\"0x0069\",\"bossWcid\":1006901}," +
               "\"phases\":[1.0,0.66,0.33],\"rewardTable\":\"ember_rewards\"" + (patch == null ? "" : "," + patch) + "}";

        // Only this file's lines: the store also reports its other optional files as empty in a bare fixture.
        private static string[] PinDiags(ThreadDungeonStore s) => s.Diagnostics.Where(d => d.StartsWith("pinnacles.json")).ToArray();

        private static string Diag(ThreadDungeonStore s) => string.Join(" | ", PinDiags(s));

        [TestMethod]
        public void NoFile_IsAnEmptyListAndNoDiagnostic()
        {
            var s = Parse(null);
            Assert.AreEqual(0, s.Pinnacles.Count);
            Assert.AreEqual(0, PinDiags(s).Length, Diag(s));
        }

        [TestMethod]
        public void ShippedFile_LoadsGraelAndMatronWithNoDiagnostics()
        {
            var path = FindShippedFile();
            Assert.IsNotNull(path, "Content/dungeons/dynamic/pinnacles.json not found by walking up from the test assembly");

            var s = Parse(System.IO.File.ReadAllText(path));
            Assert.AreEqual(2, s.Pinnacles.Count, "the shipped file ships the grael row and the matron row");
            var g = s.GetPinnacle("grael");
            Assert.IsNotNull(g);
            Assert.AreEqual("Grael", g.Name);
            Assert.AreEqual("grael", g.Destination.Kind);
            Assert.AreEqual(3, g.Entries);
            Assert.AreEqual(1, g.PartyMax);
            Assert.IsTrue(g.Enabled);
            CollectionAssert.AreEqual(new[] { 1.0 }, g.Phases);
            Assert.AreEqual("grael", g.RewardTable);

            var m = s.GetPinnacle("matron");
            Assert.IsNotNull(m);
            Assert.IsTrue(m.Enabled, "the matron row ships enabled");
            Assert.AreEqual("matron", m.Destination.Kind);
            Assert.AreEqual("matron", m.RewardTable);
            Assert.AreEqual(0, PinDiags(s).Length, Diag(s));
        }

        [TestMethod]
        public void ValidRow_LoadsWithDefaults()
        {
            var s = Parse(File(Row()));
            Assert.AreEqual(0, PinDiags(s).Length, Diag(s));
            Assert.AreEqual(1, s.Pinnacles.Count);

            var p = s.GetPinnacle("ember");
            Assert.IsNotNull(p);
            Assert.AreEqual("Ember Tyrant", p.Name);
            Assert.AreEqual(3, p.Entries, "entries defaults to 3");
            Assert.AreEqual(1, p.PartyMax);
            Assert.IsTrue(p.Enabled, "enabled defaults to true");
            Assert.AreEqual("instanced_landblock", p.Destination.Kind);
            Assert.IsTrue(p.Destination.Data.ContainsKey("landblock") && p.Destination.Data.ContainsKey("bossWcid"),
                "free-form destination data must survive next to kind");
            CollectionAssert.AreEqual(new[] { 1.0, 0.66, 0.33 }, p.Phases);
            Assert.AreEqual("ember_rewards", p.RewardTable);

            var b = p.RollBand;
            Assert.AreEqual(185, b.LowLevel);
            Assert.AreEqual(375, b.HighLevel);
            CollectionAssert.AreEqual(new[] { 1, 40 }, b.LowBand);
            CollectionAssert.AreEqual(new[] { 60, 100 }, b.HighBand);
            Assert.AreEqual(80, b.ClampLow);
        }

        [TestMethod]
        public void LookupAndEnabledView_DisabledIsExcludedFromTheView_ButStillLookupable()
        {
            var s = Parse(File(Row("a"), Row("b", "\"enabled\":false"), Row("c")));
            Assert.AreEqual(0, PinDiags(s).Length, Diag(s));
            Assert.AreEqual(3, s.Pinnacles.Count);
            CollectionAssert.AreEqual(new[] { "a", "c" }, s.EnabledPinnacles.Select(p => p.Key).ToArray());
            Assert.IsNotNull(s.GetPinnacle("b"));
            Assert.IsNull(s.GetPinnacle("nope"));
            Assert.IsNull(s.GetPinnacle(null));
        }

        [TestMethod]
        public void DuplicateKey_SecondRowDropped()
        {
            var s = Parse(File(Row("a"), Row("a")));
            Assert.AreEqual(1, s.Pinnacles.Count);
            StringAssert.Contains(Diag(s), "duplicate pinnacle key 'a'");
        }

        [TestMethod]
        public void Entries_BelowOne_Dropped_OneAccepted()
        {
            var s = Parse(File(Row("zero", "\"entries\":0"), Row("one", "\"entries\":1")));
            CollectionAssert.AreEqual(new[] { "one" }, s.Pinnacles.Select(p => p.Key).ToArray());
            StringAssert.Contains(Diag(s), "entries 0 must be at least 1");
        }

        [TestMethod]
        public void PartyMax_AboveOne_Dropped()
        {
            var s = Parse(File(Row("grp", "\"partyMax\":2")));
            Assert.AreEqual(0, s.Pinnacles.Count);
            StringAssert.Contains(Diag(s), "partyMax 2 must be 1");
        }

        [TestMethod]
        public void Phases_MustStartAtOne()
        {
            var s = Parse(File(Row("p", "\"phases\":[0.9,0.5]")));
            Assert.AreEqual(0, s.Pinnacles.Count);
            StringAssert.Contains(Diag(s), "must start at 1.0");
        }

        [TestMethod]
        public void Phases_MustStrictlyDescend()
        {
            var equal = Parse(File(Row("p", "\"phases\":[1.0,0.5,0.5]")));
            Assert.AreEqual(0, equal.Pinnacles.Count, "an equal pair is not strictly descending");
            StringAssert.Contains(Diag(equal), "strictly descend");

            var up = Parse(File(Row("p", "\"phases\":[1.0,0.4,0.6]")));
            Assert.AreEqual(0, up.Pinnacles.Count);

            var ok = Parse(File(Row("p", "\"phases\":[1.0,0.5,0.25]")));
            Assert.AreEqual(1, ok.Pinnacles.Count, Diag(ok));
        }

        [TestMethod]
        public void Phases_OutsideZeroToOne_Dropped()
        {
            var zero = Parse(File(Row("p", "\"phases\":[1.0,0.5,0.0]")));
            Assert.AreEqual(0, zero.Pinnacles.Count);
            StringAssert.Contains(Diag(zero), "outside (0, 1]");

            var over = Parse(File(Row("p", "\"phases\":[1.5]")));
            Assert.AreEqual(0, over.Pinnacles.Count);

            var single = Parse(File(Row("p", "\"phases\":[1.0]")));
            Assert.AreEqual(1, single.Pinnacles.Count, "a single-phase boss is valid");

            var empty = Parse(File(Row("p", "\"phases\":[]")));
            Assert.AreEqual(0, empty.Pinnacles.Count);
        }

        [TestMethod]
        public void RollBand_InsaneNumbers_Dropped()
        {
            string Band(string body) => Row("b", "\"rollBand\":{" + body + "}");

            Assert.AreEqual(1, Parse(File(Band("\"clampLow\":80"))).Pinnacles.Count, "control: a sane band loads");
            Assert.AreEqual(0, Parse(File(Band("\"clampLow\":120"))).Pinnacles.Count, "clamp above 100");
            Assert.AreEqual(0, Parse(File(Band("\"lowBand\":[50,40]"))).Pinnacles.Count, "inverted low band");
            Assert.AreEqual(0, Parse(File(Band("\"highBand\":[60]"))).Pinnacles.Count, "band with one number");
            Assert.AreEqual(0, Parse(File(Band("\"lowLevel\":375,\"highLevel\":185"))).Pinnacles.Count, "levels reversed");
            Assert.AreEqual(0, Parse(File(Band("\"lowBand\":[1,140]"))).Pinnacles.Count, "band number above 100");
            Assert.AreEqual(0, Parse(File(Band("\"clampLow\":0"))).Pinnacles.Count, "clamp below the low band's low edge");
        }

        [TestMethod]
        public void MissingKeyNameOrDestination_Dropped()
        {
            var noKey = Parse(File("{\"name\":\"X\",\"destination\":{\"kind\":\"k\"}}"));
            Assert.AreEqual(0, noKey.Pinnacles.Count);
            StringAssert.Contains(Diag(noKey), "empty key");

            var noName = Parse(File("{\"key\":\"x\",\"destination\":{\"kind\":\"k\"}}"));
            Assert.AreEqual(0, noName.Pinnacles.Count);

            var noDest = Parse(File("{\"key\":\"x\",\"name\":\"X\"}"));
            Assert.AreEqual(0, noDest.Pinnacles.Count);
            StringAssert.Contains(Diag(noDest), "destination.kind");
        }

        [TestMethod]
        public void BadRowCostsOnlyThatRow_MalformedFileAndVersionAreFailOpen()
        {
            var mixed = Parse(File(Row("good"), "{\"key\":\"bad\",\"entries\":\"three\"}", Row("alsogood")));
            CollectionAssert.AreEqual(new[] { "good", "alsogood" }, mixed.Pinnacles.Select(p => p.Key).ToArray());
            StringAssert.Contains(Diag(mixed), "malformed");

            var garbage = Parse("{ not json");
            Assert.AreEqual(0, garbage.Pinnacles.Count);
            StringAssert.Contains(Diag(garbage), "pinnacles.json");

            var v2 = Parse("{\"version\":2,\"pinnacles\":[" + Row() + "]}");
            Assert.AreEqual(0, v2.Pinnacles.Count);
            StringAssert.Contains(Diag(v2), "unsupported version 2");
        }

        [TestMethod]
        public void EmptyStore_HasNoPinnacles()
        {
            Assert.AreEqual(0, ThreadDungeonStore.Empty.Pinnacles.Count);
            Assert.AreEqual(0, ThreadDungeonStore.Empty.EnabledPinnacles.Count);
        }

        private static string FindShippedFile()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "dungeons", "dynamic", "pinnacles.json");
                if (System.IO.File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
