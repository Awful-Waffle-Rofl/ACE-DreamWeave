using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pins for the scheduler's live wiring, which no behavioural test reaches (it needs the world thread). Each
    /// matches whole statements, comments stripped, by exact trimmed text, not a substring.
    /// </summary>
    [TestClass]
    public class WorldEventSchedulerCallSiteTests
    {
        private static string[] Statements(string relativePath)
        {
            var path = Path.Combine(WorldEventHooksCallSiteTests.RepoRoot(), "Source", "ACE.Server", relativePath);
            Assert.IsTrue(File.Exists(path), path);

            return WorldEventHooksCallSiteTests.StripComments(File.ReadAllText(path))
                .Split('\n')
                .Select(l => l.Trim())
                .ToArray();
        }

        private static int IndexOfExactly(string[] lines, string statement, int from = 0, int to = int.MaxValue)
        {
            var hits = Enumerable.Range(from, Math.Min(lines.Length, to) - from).Where(i => lines[i] == statement).ToList();
            Assert.AreEqual(1, hits.Count, $"expected exactly one '{statement}' in range [{from},{to}), found {hits.Count}");
            return hits[0];
        }

        [TestMethod]
        public void TickLive_RunsInsideManagerTick_BeforeTheNoEventEarlyReturn()
        {
            var lines = Statements(Path.Combine("WorldEvents", "WorldEventManager.cs"));

            var tickStart = IndexOfExactly(lines, "public static void Tick()");
            var nextMethod = Enumerable.Range(tickStart + 1, lines.Length - tickStart - 1)
                .First(i => lines[i].StartsWith("public static ") || lines[i].StartsWith("internal static ") || lines[i].StartsWith("private static "));

            var throttle = IndexOfExactly(lines, "nextTickTime = now + 1.0;", tickStart, nextMethod);
            var scheduler = IndexOfExactly(lines, "WorldEventScheduler.TickLive();", tickStart, nextMethod);
            var currentRead = IndexOfExactly(lines, "evt = Current;", tickStart, nextMethod);
            var earlyReturn = IndexOfExactly(lines, "if (evt == null)", tickStart, nextMethod);

            Assert.IsTrue(throttle < scheduler, "the scheduler is ticked after the one-second throttle");
            Assert.IsTrue(scheduler < currentRead, "the scheduler is ticked before the manager reads the current event");
            Assert.IsTrue(scheduler < earlyReturn,
                "WorldEventScheduler.TickLive() must come before the 'if (evt == null) return;' early return, or the scheduler never runs while no event is current");
        }

        [TestMethod]
        public void SchedulerAuditWriter_IsWiredAfterWorldStartup_NotInsideTheMarketOptionsBuilder()
        {
            var lines = Statements("Program.cs");

            var worldInit = IndexOfExactly(lines, "WorldManager.Initialize();");
            var assignment = IndexOfExactly(lines,
                "ACE.Server.WorldEvents.WorldEventScheduler.AuditWriter = (actorLabel, message) => PlayerManager.BroadcastToAuditChannel(actorLabel, message);");
            var marketBuilder = Enumerable.Range(0, lines.Length)
                .First(i => lines[i].StartsWith("private static ACE.Server.Managers.Market.MarketApiOptions BuildMarketApiOptions()"));

            Assert.IsTrue(worldInit < assignment, "wired after WorldManager.Initialize");
            Assert.IsTrue(assignment < marketBuilder, "wired in startup, ahead of BuildMarketApiOptions, which only runs when the Market API is on");
        }
    }
}
