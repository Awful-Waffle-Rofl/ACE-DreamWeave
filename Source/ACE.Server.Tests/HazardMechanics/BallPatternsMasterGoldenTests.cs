using System;
using System.Linq;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// Review 2026-10-20: the opt-in planner args (origin_clear, no_overlap, distinct_dirs; Grael round 2, E1) must leave
    /// every non-row pattern's plan byte-identical to master when they are off. The goldens were captured from master
    /// (BallPatternsMasterFingerprints.CaptureCommit) in a scratch build of that commit; this test only reads them.
    /// </summary>
    [TestClass]
    public class BallPatternsMasterGoldenTests
    {
        /// <summary>The new args, explicitly off: must change nothing.</summary>
        private const string NewArgsOff = "origin_clear=0 no_overlap=0 distinct_dirs=0";

        /// <summary>Same format as BallGapsTests.Fingerprint (and the capture probe): routes | gaps | markers | summary.</summary>
        private static string Fingerprint(BallWave w)
            => string.Join(";", w.Routes.Select(r => FormattableString.Invariant($"{r.From.X:0.###},{r.From.Y:0.###}>{r.To.X:0.###},{r.To.Y:0.###}")))
               + "|" + string.Join(";", w.Gaps.Select(g => FormattableString.Invariant($"{g.Side}:{g.Lateral:0.###}")))
               + "|" + string.Join(";", w.Markers.Select(m => FormattableString.Invariant($"{m.X:0.###},{m.Y:0.###}")))
               + "|" + w.Summary;

        private static string Plan(string pattern, string args, int seed)
        {
            var merged = HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls));
            return Fingerprint(BallPatterns.Plan(HazardRegionPresets.Pinnacle(0), pattern, BallRules.Read(merged, pattern), new Random(seed)));
        }

        [TestMethod]
        public void NonRowPatterns_MatchTheMasterGoldens_WithTheNewArgsAbsentOrOff()
        {
            var plans = BallPatternsMasterFingerprints.Plans;

            Assert.AreEqual(50, plans.Length, "5 patterns x 2 arg sets x 5 seeds");
            CollectionAssert.AreEquivalent(new[] { "ring-out", "ring-in", "ring-alt", "fan", "random" }, plans.Select(p => p.Pattern).Distinct().ToArray());
            Assert.AreEqual("29a231718", BallPatternsMasterFingerprints.CaptureCommit);

            foreach (var (pattern, set, seed, golden) in plans)
            {
                var args = BallPatternsMasterFingerprints.ArgSets[set];

                Assert.AreEqual(golden, Plan(pattern, args, seed), $"{pattern} set {set} seed {seed}: new args absent");
                Assert.AreEqual(golden, Plan(pattern, (args + " " + NewArgsOff).Trim(), seed), $"{pattern} set {set} seed {seed}: new args off");
            }
        }

        [TestMethod]
        public void Control_OriginClearOn_ChangesTheRingAndFanPlans()
        {
            // the goldens discriminate: switching origin_clear on moves the ring and fan start points
            foreach (var pattern in new[] { "ring-out", "fan" })
            {
                var golden = BallPatternsMasterFingerprints.Plans.First(p => p.Pattern == pattern && p.ArgSet == 1 && p.Seed == 1).Fingerprint;
                Assert.AreNotEqual(golden, Plan(pattern, BallPatternsMasterFingerprints.ArgSets[1] + " origin_clear=3", 1), pattern);
            }
        }
    }
}
