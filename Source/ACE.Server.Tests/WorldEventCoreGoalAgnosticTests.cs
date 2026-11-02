using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan guard (World Events refactor R4): the main WorldEvent.cs stays goal-agnostic. Goal-specific
    /// behaviour reaches the run through IWorldEventObjective's hooks, or lives in a WorldEvent.*.cs partial;
    /// it never becomes a branch on a goal type inside WorldEvent.cs. Only the main file is scanned - the
    /// partials are where goal code is allowed to go.
    ///
    /// The scan strips comments, then counts every identifier-like token containing "hold" or "protect" (any
    /// case, string literals included). The census must EQUAL the allowlist below: every token on it, at
    /// exactly its count, and nothing else. A lower count fails too, so a change that removes or renames one
    /// of these tokens updates the allowlist in the same commit and the list never drifts from the file.
    /// </summary>
    [TestClass]
    public class WorldEventCoreGoalAgnosticTests
    {
        private const string RelativePath = "Source/ACE.Server/WorldEvents/WorldEvent.cs";

        /// <summary>
        /// Every hold/protect token WorldEvent.cs contains, with its exact count. All of them are the
        /// LANDBLOCK hold (permaload of the anchor and its adjacents), the pace hold, or Asheron's Protection
        /// (death protection) - none is the Hold goal.
        /// </summary>
        private static readonly Dictionary<string, int> Allowed = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // Present before R4.
            ["DeathProtectionEnabled"] = 2,
            ["DeathProtectionEndLine"] = 1,
            ["deathProtectionFlag"] = 6,
            ["deathProtectionStartAnnounced"] = 3,
            ["DeathProtectionStartLine"] = 1,
            ["HazardHoldsAdjacents"] = 2,
            ["Hold"] = 1,
            ["hold"] = 6,
            ["HoldAdjacentLandblocks"] = 1,
            ["holdAdjacents"] = 3,
            ["HoldLandblocks"] = 2,
            ["HoldPace"] = 2,
            ["protection"] = 1,
            ["ReleaseLandblockHold"] = 2,
            ["RingHoldsAdjacents"] = 2,
            ["ShouldHoldPace"] = 1,
            ["world_event_death_protection"] = 1,

            // 1 before R4 (ComputeRelease), +3 from R4 (ComputeHoldReasons, HoldsAdjacents, HoldReasonsLogSuffix).
            ["WorldEventLandblockHold"] = 4,

            // Added by R4: the landblock hold's reason flags, and the objective hook that feeds them.
            ["AdjacentHoldReasons"] = 2,
            ["ComputeHoldReasons"] = 1,
            ["HoldReasonsLogSuffix"] = 1,
            ["HoldsAdjacentLandblocks"] = 2,
            ["HoldsAdjacents"] = 1,
            ["WorldEventHoldReason"] = 1,
        };

        private static string FindWorldEventSource()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, RelativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static string ReadCodeWithoutComments()
        {
            var path = FindWorldEventSource();

            // Fail, never Inconclusive: a guard that cannot find its file must not read as a pass.
            if (path == null)
                Assert.Fail($"Could not find {RelativePath} by walking up from {AppContext.BaseDirectory}");

            var text = File.ReadAllText(path, Encoding.UTF8);

            text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", "");

            return text;
        }

        private static Dictionary<string, int> Census(string code)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Match m in Regex.Matches(code, @"\b\w*(?:hold|protect)\w*\b", RegexOptions.IgnoreCase))
                counts[m.Value] = counts.TryGetValue(m.Value, out var n) ? n + 1 : 1;

            return counts;
        }

        [TestMethod]
        public void WorldEventCs_NamesNoGoalTypeHold()
        {
            var code = ReadCodeWithoutComments();

            Assert.AreEqual(0, Regex.Matches(code, @"GoalType\s*\.\s*Hold\b").Count,
                "WorldEvent.cs must not branch on GoalType.Hold; use an IWorldEventObjective hook or a WorldEvent.*.cs partial");
        }

        [TestMethod]
        public void WorldEventCs_HoldAndProtectTokens_MatchTheAllowlistExactly()
        {
            var census = Census(ReadCodeWithoutComments());

            var violations = census.Keys.Union(Allowed.Keys, StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => (Token: k, Found: census.TryGetValue(k, out var f) ? f : 0, Listed: Allowed.TryGetValue(k, out var a) ? a : 0))
                .Where(x => x.Found != x.Listed)
                .Select(x => $"{x.Token} found {x.Found}, listed {x.Listed}")
                .ToList();

            var report = string.Join(", ", census.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

            Assert.AreEqual(0, violations.Count,
                "WorldEvent.cs hold/protect tokens differ from the allowlist: " + string.Join("; ", violations) +
                ". New goal code belongs behind an IWorldEventObjective hook or in a WorldEvent.*.cs partial; a removed or" +
                " renamed landblock-hold token updates the allowlist. Census: " + report);
        }

        /// <summary>Positive control: the scan really does see tokens in this file, so an empty census is not a silent pass.</summary>
        [TestMethod]
        public void Census_SeesTheKnownLandblockHoldTokens()
        {
            var census = Census(ReadCodeWithoutComments());

            Assert.IsTrue(census.ContainsKey("HoldLandblocks"), "the scan must see HoldLandblocks");
            Assert.IsTrue(census.ContainsKey("HoldsAdjacentLandblocks"), "the scan must see the objective hook read");
        }

        /// <summary>Negative control: the census flags a goal-specific token the allowlist does not carry.</summary>
        [TestMethod]
        public void Census_FlagsAnUnlistedToken()
        {
            var census = Census("var h = Objective as HoldObjective; if (goal.Protect != null) { }");

            Assert.IsTrue(census.ContainsKey("HoldObjective"));
            Assert.IsTrue(census.ContainsKey("Protect"));
            Assert.IsFalse(Allowed.ContainsKey("HoldObjective"));
            Assert.IsFalse(Allowed.ContainsKey("Protect"));
        }
    }
}
