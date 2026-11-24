using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>
    /// Fit probe for the main-chamber storm wisps (owner ruling 2026-11-19: final n=6, phase n=8 kept, faster cycles). Builds the
    /// formation each mixed_pool pattern really places in the region the live lightning run uses, with the args that reach
    /// StartHazard, through WispFormations.Plan (the production planner). The count a pattern places is the most points any
    /// step of any of 40 seeded plans holds, so a pattern that cannot fit n is visible as a count below n.
    /// </summary>
    [TestClass]
    public class HarbingerChamberWispFitTests
    {
        private static readonly HarbingerSettings S = HarbingerSettings.Defaults;
        private static readonly string[] Patterns = { "row", "column", "diagonal", "ring", "sweep" };

        private static (HazardRegion Region, HazardArgs Args) LiveLightning(bool final)
        {
            var p = new FakePinnacleFightPort(1);
            var s = new HarbingerScript(S);
            s.OnStart(p);
            s.EnterPhase(final ? 5 : (int)HarbingerElement.Lightning + 1, p);

            var call = p.Hazards.Single(h => h.Tag == HarbingerData.Element(HarbingerElement.Lightning).Tag);
            var region = PinnacleHazardRegions.Resolve(call.Options, HazardRegionPresets.HarbingerName, 0, out var error);
            Assert.IsNotNull(region, error);
            return (region, call.Args);
        }

        /// <summary>The most wisps any step of any seeded plan of <paramref name="pattern"/> places.</summary>
        private static int Placed(HazardRegion region, HazardArgs args, string pattern)
        {
            var settings = WispSettings.From(args);
            var most = 0;

            for (var seed = 0; seed < 40; seed++)
            {
                var plan = WispFormations.Plan(region, settings, pattern, new Random(seed));
                most = Math.Max(most, plan.Steps.Max(st => st.Points.Count));
            }

            return most;
        }

        private static Dictionary<string, int> Table(bool final, int? n = null)
        {
            var (region, args) = LiveLightning(final);

            if (n != null)
                args = HazardArgs.Parse(System.Text.RegularExpressions.Regex.Replace(args.ToString(), @"\bn=\d+", "n=" + n));

            return Patterns.ToDictionary(pt => pt, pt => Placed(region, args, pt));
        }

        private static string Describe(Dictionary<string, int> t) => string.Join(" ", Patterns.Select(p => p + "=" + t[p]));

        [TestMethod]
        public void LightningArgs_CarryTheEightPhaseAndSixFinalWisps()
        {
            Assert.AreEqual(8, S.LightningArgs(S.WispPct, false).GetInt("n", 0, 0, 99));
            Assert.AreEqual(6, S.LightningArgs(S.WispPct, true).GetInt("n", 0, 0, 99));

            Assert.AreEqual(8, LiveLightning(false).Args.GetInt("n", 0, 0, 99), "the phase run that reaches StartHazard");
            Assert.AreEqual(6, LiveLightning(true).Args.GetInt("n", 0, 0, 99), "the final run that reaches StartHazard");
        }

        [TestMethod]
        public void BothRuns_StillValidateOnTheChamber_AtTheFasterCycle()
        {
            foreach (var final in new[] { false, true })
            {
                var (region, args) = LiveLightning(final);

                Assert.AreEqual(2.0, args.GetDouble("move", 0, 0, 99), 1e-9);
                Assert.AreEqual(1.0, args.GetDouble("rest", 0, 0, 99), 1e-9);
                Assert.IsNull(WispRules.Validate(region, args, "mixed"), $"final={final}: hold floor and the 600 s cap");
            }

            // Negative control: a hold under the escape floor must be refused, so the assertion above can fail.
            var (r, phase) = LiveLightning(false);
            var tooShort = HazardArgs.Parse(System.Text.RegularExpressions.Regex.Replace(phase.ToString(), @"\bhold=[\d.]+", "hold=1.0"));
            Assert.AreEqual(1.0, tooShort.GetDouble("hold", 0, 0, 99), 1e-9);
            Assert.IsNotNull(WispRules.Validate(r, tooShort, "mixed"), "hold=1.0 is under the hold floor");
        }

        [TestMethod]
        public void TwelveWisps_AreClippedToTheEightWispCounts_InThePhaseRun()
            => CollectionAssert.AreEqual(Table(false), Table(false, 12), "the chamber places no more at n=12, which is why phase n stays 8");

        [TestMethod]
        public void PhaseRun_PlacesPinnedWispCounts_PerPattern()
        {
            // Honest finding: the chamber is too small for more than 8 in a row or column (4), diagonal (6), ring (8) or sweep row (3),
            // so more than 8 would place the same counts. The pin still guards the fit: a pattern that stops fitting shows here.
            Assert.AreEqual("row=4 column=4 diagonal=6 ring=8 sweep=3", Describe(Table(false)));
        }

        [TestMethod]
        public void FinalRun_PlacesPinnedWispCounts_PerPattern()
            => Assert.AreEqual("row=6 column=6 diagonal=6 ring=6 sweep=6", Describe(Table(true)), "all five fit n=6 (n=8 would clip row and column to 7)");
    }
}
