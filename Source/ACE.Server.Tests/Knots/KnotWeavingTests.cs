using System.Linq;

using ACE.Server.Knots;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// What is left of the weave plumbing: the Knot level math, and the Fibers-only Pouch container rules. (The weave
    /// prompts were retired when the Pattern became the only way to make a Knot; see KnotSeamTests for PatternFill.)
    /// </summary>
    [TestClass]
    public class KnotWeavingTests
    {
        [TestMethod]
        public void KnotLevel_IsTheFloorOfTheMean()
        {
            var levels = Enumerable.Repeat(300, KnotItems.FibersPerKnot).ToArray();

            // 9 x 300 + 1 x 305 = 3005, mean 300.5 -> 300 (floor, not round)
            levels[9] = 305;
            Assert.AreEqual(300, KnotWeaving.KnotLevelFor(levels));

            // 9 x 300 + 1 x 309 = 3009, mean 300.9 -> 300
            levels[9] = 309;
            Assert.AreEqual(300, KnotWeaving.KnotLevelFor(levels));

            // a mixed set: 100..190 step 10 -> mean 145
            Assert.AreEqual(145, KnotWeaving.KnotLevelFor(new[] { 100, 110, 120, 130, 140, 150, 160, 170, 180, 190 }));

            Assert.AreEqual(0, KnotWeaving.KnotLevelFor(new int[0]));
        }

        [TestMethod]
        public void ExplicitMove_PouchTakesFibersOnly_OtherContainersUnaffected()
        {
            Assert.IsTrue(KnotWeaving.AcceptsExplicitMove(KnotItemKind.Pouch, KnotItemKind.Fiber));

            Assert.IsFalse(KnotWeaving.AcceptsExplicitMove(KnotItemKind.Pouch, KnotItemKind.None), "an ordinary item");
            Assert.IsFalse(KnotWeaving.AcceptsExplicitMove(KnotItemKind.Pouch, KnotItemKind.Knot));
            Assert.IsFalse(KnotWeaving.AcceptsExplicitMove(KnotItemKind.Pouch, KnotItemKind.Pattern));
            Assert.IsFalse(KnotWeaving.AcceptsExplicitMove(KnotItemKind.Pouch, KnotItemKind.Pouch), "a Pouch inside a Pouch");

            foreach (var kind in new[] { KnotItemKind.None, KnotItemKind.Fiber, KnotItemKind.Knot, KnotItemKind.Pattern, KnotItemKind.Pouch })
                Assert.IsTrue(KnotWeaving.AcceptsExplicitMove(KnotItemKind.None, kind), $"ordinary container takes kind {kind}");
        }

        [TestMethod]
        public void SplitAndMerge_NeverIntoAPouch_OtherContainersUnaffected()
        {
            Assert.IsFalse(KnotWeaving.AcceptsSplitInto(KnotItemKind.Pouch));
            Assert.IsFalse(KnotWeaving.AcceptsMergeInto(KnotItemKind.Pouch));

            foreach (var kind in new[] { KnotItemKind.None, KnotItemKind.Fiber, KnotItemKind.Knot, KnotItemKind.Pattern })
            {
                Assert.IsTrue(KnotWeaving.AcceptsSplitInto(kind), $"split into kind {kind}");
                Assert.IsTrue(KnotWeaving.AcceptsMergeInto(kind), $"merge into kind {kind}");
            }
        }

        [TestMethod]
        public void PouchText_IsAsciiAndDistinct()
        {
            var lines = new[] { KnotItems.PouchAcceptsFibersOnly, KnotItems.PouchRefusesSplit, KnotItems.PouchRefusesMerge };

            Assert.IsTrue(lines.All(s => !string.IsNullOrWhiteSpace(s) && s.All(c => c >= 0x20 && c < 0x7F)));
            Assert.AreEqual(lines.Length, lines.Distinct().Count());
        }
    }
}
