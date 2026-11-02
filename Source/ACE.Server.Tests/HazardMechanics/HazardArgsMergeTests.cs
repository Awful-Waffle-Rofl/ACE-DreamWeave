using System.Collections.Generic;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    [TestClass]
    public class HazardArgsMergeTests
    {
        [TestMethod]
        public void Args_OverrideTheSettingsString_OtherSettingsSurvive()
        {
            var settings = HazardArgs.Parse(HazardDefaults.Wisps);
            var args = HazardArgs.Parse("n=3 pct=0.25 extra=yes");

            var merged = HazardArgs.Merge(args, settings);

            Assert.AreEqual(3, merged.GetInt("n", 99, 1, 64), "arg wins");
            Assert.AreEqual(0.25, merged.GetDouble("pct", 0.6, 0, 5), 1e-9, "arg wins");
            Assert.AreEqual(2.5, merged.GetDouble("move", 0, 0, 100), 1e-9, "setting survives");
            Assert.AreEqual("yes", merged.GetString("extra"), "arg-only key kept");
        }

        [TestMethod]
        public void Merge_ToleratesNullSides()
        {
            Assert.AreEqual(0, HazardArgs.Merge(null, null).Count);
            Assert.AreEqual("1", HazardArgs.Merge(null, HazardArgs.Parse("a=1")).GetString("a"));
            Assert.AreEqual("2", HazardArgs.Merge(HazardArgs.Parse("a=2"), null).GetString("a"));
        }

        [TestMethod]
        public void Parse_FirstOfADuplicateKeyWins_AndBadTokensAreReported()
        {
            var errors = new List<string>();
            var a = HazardArgs.Parse("n=4 N=9 junk =5 k= ok=1", errors);

            Assert.AreEqual("4", a.GetString("n"), "first wins, keys case-insensitive");
            Assert.AreEqual("1", a.GetString("ok"));
            Assert.AreEqual(4, errors.Count, $"errors: {string.Join(" | ", errors)}");
        }

        [TestMethod]
        public void TypedAccessors_ClampAndFallBack()
        {
            var a = HazardArgs.Parse("big=1e9 small=-3 nan=NaN inf=Infinity word=abc i=500 bad=x");

            Assert.AreEqual(5.0, a.GetDouble("big", 0.6, 0, 5), 1e-9, "clamped to max");
            Assert.AreEqual(0.0, a.GetDouble("small", 0.6, 0, 5), 1e-9, "clamped to min");
            Assert.AreEqual(0.6, a.GetDouble("nan", 0.6, 0, 5), 1e-9, "non-finite -> default");
            Assert.AreEqual(0.6, a.GetDouble("inf", 0.6, 0, 5), 1e-9, "non-finite -> default");
            Assert.AreEqual(0.6, a.GetDouble("word", 0.6, 0, 5), 1e-9, "unparsable -> default");
            Assert.AreEqual(1.0, a.GetDouble("missing", 7, 0, 1), 1e-9, "the default is clamped too");
            Assert.AreEqual(64, a.GetInt("i", 8, 1, 64));
            Assert.AreEqual(8, a.GetInt("bad", 8, 1, 64));
        }

        [TestMethod]
        public void GetBool_And_GetChoice()
        {
            var a = HazardArgs.Parse("a=1 b=off c=maybe where=CEILING");

            Assert.IsTrue(a.GetBool("a", false));
            Assert.IsFalse(a.GetBool("b", true));
            Assert.IsTrue(a.GetBool("c", true), "unknown -> default");
            Assert.AreEqual("ceiling", a.GetChoice("where", "floor", "floor", "ceiling", "both"));
            Assert.AreEqual("floor", a.GetChoice("nope", "floor", "floor", "ceiling"));
        }

        [TestMethod]
        public void CommandRequest_PositionalPattern_ReservedKeysOut_RestAreArgs()
        {
            Assert.IsTrue(HazardCommandRequest.TryParse(new[] { "Wisps", "Row", "region=circle:12", "hz=1.5", "seed=42", "n=3" }, out var r, out var error), error);

            Assert.AreEqual("wisps", r.Kind);
            Assert.AreEqual("row", r.Pattern);
            Assert.AreEqual(12f, r.Shape.Value.Radius);
            Assert.AreEqual(1.5f, r.Hz);
            Assert.AreEqual(42, r.Seed);
            Assert.AreEqual(1, r.Args.Count, "only n= reaches the mechanic");
            Assert.AreEqual("3", r.Args.GetString("n"));
        }

        [TestMethod]
        public void CommandRequest_Refusals()
        {
            Assert.IsFalse(HazardCommandRequest.TryParse(new[] { "wisps", "preset=pinnacle", "region=circle:5" }, out _, out var e1));
            StringAssert.Contains(e1, "not both");

            Assert.IsFalse(HazardCommandRequest.TryParse(new[] { "wisps", "row", "stray" }, out _, out var e2));
            StringAssert.Contains(e2, "key=value");

            Assert.IsFalse(HazardCommandRequest.TryParse(new[] { "wisps", "seed=abc" }, out _, out _));
            Assert.IsFalse(HazardCommandRequest.TryParse(new[] { "wisps", "row", "pattern=ring" }, out _, out _));
            Assert.IsFalse(HazardCommandRequest.TryParse(new[] { "wisps", "region=hex:3" }, out _, out _));
        }

        [TestMethod]
        public void CommandRequest_Region_PresetOnTheWrongLandblockIsRefused_DefaultsToThePresetHere()
        {
            HazardCommandRequest.TryParse(new[] { "wisps", "preset=pinnacle" }, out var r, out _);

            Assert.IsFalse(r.TryResolveRegion(0x0070, 0, false, Vector3.Zero, out _, out var error));
            StringAssert.Contains(error, "0x0069");

            Assert.IsTrue(r.TryResolveRegion(0x0069, 7, false, Vector3.Zero, out var region, out error), error);
            Assert.AreEqual(HazardRegionPresets.PinnacleName, region.Name);

            HazardCommandRequest.TryParse(new[] { "wisps" }, out var bare, out _);

            Assert.IsTrue(bare.TryResolveRegion(0x0069, 7, false, Vector3.Zero, out region, out _));
            Assert.AreEqual(HazardRegionPresets.PinnacleName, region.Name, "a preset's own landblock defaults to it");

            Assert.IsTrue(bare.TryResolveRegion(0x01AB, 0, false, new Vector3(1, 2, 3), out region, out _));
            Assert.AreEqual("adhoc", region.Name);
            Assert.AreEqual(HazardDefaults.AdHocCircleRadius, region.Radius);
        }

        [TestMethod]
        public void HitRules_DamageIsCeilOfPctOfMaxHealth()
        {
            Assert.AreEqual(601u, HazardHitRules.DamageFor(1001, 0.60));
            Assert.AreEqual(600u, HazardHitRules.DamageFor(1000, 0.60));
            Assert.AreEqual(1u, HazardHitRules.DamageFor(1, 0.01), "at least 1 when pct > 0");
            Assert.AreEqual(0u, HazardHitRules.DamageFor(1000, 0.0), "pct 0 = visual only");
            Assert.AreEqual(0u, HazardHitRules.DamageFor(1000, double.NaN));
        }

        [TestMethod]
        public void HitRules_ReachIsTwoD_AndTheWindowDebounces()
        {
            Assert.IsTrue(HazardHitRules.InReach(new Vector3(0, 0, 0), new Vector3(2.5f, 0, 50), 2.0, 0.5), "z ignored, edge inclusive");
            Assert.IsFalse(HazardHitRules.InReach(new Vector3(0, 0, 0), new Vector3(2.6f, 0, 0), 2.0, 0.5));

            Assert.IsTrue(HazardHitRules.WindowOpen(null, 5, double.PositiveInfinity));
            Assert.IsFalse(HazardHitRules.WindowOpen(1, 500, double.PositiveInfinity), "infinite window = once per hazard");
            Assert.IsFalse(HazardHitRules.WindowOpen(1, 2.9, 2.0));
            Assert.IsTrue(HazardHitRules.WindowOpen(1, 3.0, 2.0));
        }
    }
}
