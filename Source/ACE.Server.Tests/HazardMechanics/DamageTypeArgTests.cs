using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// HAZARDS-PLAN A2 (fan-out invariant "Damage"): args damage_type reaches every damage call of the wisps and balls
    /// mechanics through ctx.DamageType with the text left unset, and an unknown word is refused by each mechanic's
    /// Validate with the one shared line. A3 (rain) copies the same pattern. Pure: no landblock, no PropertyManager.
    /// </summary>
    [TestClass]
    public class DamageTypeArgTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static HazardArgs Merged(string args) => HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));

        private static string Line(string x) => $"damage_type='{x}' is not one of fire|acid|cold|electric|bludgeon";

        private static string WispsValidate(string args)
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate(HazardMechanicRegistry.Wisps, out var m));
            return m.Validate(Pinnacle(), HazardArgs.Parse(args), "scatter");
        }

        private static string BallsValidate(string args)
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate(HazardMechanicRegistry.Balls, out var m));
            return m.Validate(Pinnacle(), Merged(args), "row");
        }

        [TestMethod]
        public void BadDamageType_AcceptsTheFiveWordsInAnyCase_AndNothingWhenAbsent()
        {
            Assert.IsNull(HazardContext.BadDamageType(null));
            Assert.IsNull(HazardContext.BadDamageType(HazardArgs.Empty));

            foreach (var word in new[] { "fire", "acid", "cold", "electric", "bludgeon", "FIRE", " Acid ", "Electric" })
                Assert.IsNull(HazardContext.BadDamageType(HazardArgs.Parse("damage_type=" + word.Trim())), word);
        }

        [TestMethod]
        public void BadDamageType_RefusesAnythingElse_WithTheExactLine()
        {
            foreach (var word in new[] { "plasma", "slash", "nether", "lightning" })
                Assert.AreEqual(Line(word), HazardContext.BadDamageType(HazardArgs.Parse("damage_type=" + word)), word);
        }

        [TestMethod]
        public void BothMechanics_RefuseAnUnknownDamageType_WithTheSameLine_AndAcceptTheFive()
        {
            Assert.AreEqual(Line("plasma"), WispsValidate("cycles=1 damage_type=plasma"));
            Assert.AreEqual(Line("plasma"), BallsValidate("waves=2 wave_every=3 damage_type=plasma"));

            foreach (var word in new[] { "fire", "acid", "cold", "electric", "bludgeon" })
            {
                Assert.IsNull(WispsValidate("cycles=1 damage_type=" + word), "wisps " + word);
                Assert.IsNull(BallsValidate("waves=2 wave_every=3 damage_type=" + word), "balls " + word);
            }

            Assert.IsNull(WispsValidate("cycles=1"), "absent is fine (fire)");
            Assert.IsNull(BallsValidate("waves=2 wave_every=3"), "absent is fine (fire)");
        }

        [TestMethod]
        public void TheContext_ReadsTheElement_FireWhenAbsent()
        {
            foreach (var (word, expected) in new[]
            {
                ("fire", DamageType.Fire), ("acid", DamageType.Acid), ("cold", DamageType.Cold),
                ("electric", DamageType.Electric), ("bludgeon", DamageType.Bludgeon),
            })
            {
                Assert.AreEqual(expected, RunWith("damage_type=" + word).Context.DamageType, word);
            }

            Assert.AreEqual(DamageType.Fire, RunWith("").Context.DamageType);
        }

        private static HazardRun RunWith(string args)
        {
            HazardMechanicRegistry.TryCreate(HazardMechanicRegistry.Wisps, out var mechanic);
            var region = HazardRegion.Rect("t", 0x0069, 0, false, 0, 10, 0, 10, HazardGroundMode.Plane, 0, null, 0, null, 1, 1);
            return new HazardRun(1, HazardMechanicRegistry.Wisps, "scatter", region, HazardArgs.Parse(args), 1, null, mechanic, null, DateTime.UtcNow);
        }

        private static string MechanicSource(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "HazardMechanics", file);

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);

                dir = dir.Parent;
            }

            Assert.Fail("Could not find " + file + " by walking up from " + AppContext.BaseDirectory);
            return null;
        }

        [TestMethod]
        public void EveryDamageCall_OfTheWispsAndBalls_PassesTheContextsElement_AndNoText()
        {
            foreach (var file in new[] { "WispSquadMechanic.cs", "RollingBallMechanic.cs" })
            {
                var text = MechanicSource(file);
                var calls = Regex.Matches(text, @"\.HitPlayers?\([^;]*;").Cast<Match>().ToList();

                Assert.IsTrue(calls.Count >= 1, file + ": at least one damage call");

                foreach (var call in calls)
                {
                    StringAssert.Contains(call.Value, "damageType: ctx.DamageType", file + ": " + call.Value);
                    Assert.IsFalse(call.Value.Contains("text:"), file + ": the hit line stays the default (HitVerb): " + call.Value);
                }
            }
        }
    }
}