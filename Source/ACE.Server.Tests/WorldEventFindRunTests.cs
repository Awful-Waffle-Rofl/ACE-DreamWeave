using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// <see cref="WorldEventManager.FindRun(WorldEvent, uint)"/>, the pure core the claim handler now resolves a
    /// cache's run through, and its equivalence with the predicate it replaced. No PropertyManager read, and
    /// WorldEventManager.Current is never touched.
    /// </summary>
    [TestClass]
    public class WorldEventFindRunTests
    {
        [TestMethod]
        public void MatchingId_ReturnsTheCurrentRun()
        {
            var run = new WorldEvent(7, null, null, null);

            Assert.AreSame(run, WorldEventManager.FindRun(run, 7));
        }

        [TestMethod]
        public void MismatchedId_ReturnsNull()
        {
            var run = new WorldEvent(7, null, null, null);

            Assert.IsNull(WorldEventManager.FindRun(run, 6));
            Assert.IsNull(WorldEventManager.FindRun(run, 8));
            Assert.IsNull(WorldEventManager.FindRun(run, 0));
        }

        [TestMethod]
        public void NoCurrentRun_ReturnsNull()
        {
            Assert.IsNull(WorldEventManager.FindRun(null, 7));
            Assert.IsNull(WorldEventManager.FindRun(null, 0));
        }

        /// <summary>
        /// The claim handler used to refuse when <c>evt == null || runId == null || (uint)runId.Value != evt.RunId</c>
        /// with evt = Current; it now refuses when <c>(runId == null ? null : FindRun((uint)runId.Value)) == null</c>.
        /// The two must agree on every (current run, cache stamp) pair, including negative stamps that wrap.
        /// </summary>
        [TestMethod]
        public void ClaimResolution_AgreesWithThePredicateItReplaced()
        {
            var runs = new WorldEvent[] { null, new WorldEvent(0, null, null, null), new WorldEvent(7, null, null, null), new WorldEvent(uint.MaxValue, null, null, null) };
            var stamps = new int?[] { null, 0, 7, 8, -1, int.MaxValue, int.MinValue };

            foreach (var current in runs)
            {
                foreach (var runId in stamps)
                {
                    var oldRefuses = current == null || runId == null || (uint)runId.Value != current.RunId;

                    var resolved = runId == null ? null : WorldEventManager.FindRun(current, (uint)runId.Value);
                    var newRefuses = resolved == null;

                    Assert.AreEqual(oldRefuses, newRefuses, $"current={current?.RunId.ToString() ?? "none"} stamp={runId?.ToString() ?? "none"}");

                    if (!newRefuses)
                        Assert.AreSame(current, resolved, "an accepted claim resolves to the same run object");
                }
            }
        }
    }
}
