using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Thread-Guide ladder table, NextRung, and RewardCap. RewardCap is checked against
    /// EnlightenmentXpCurve.LevelProportionalXp, the pure helper Player.GrantLevelProportionalXp delegates to,
    /// on a synthetic chart - so the three-case test is a test of the formula the grant actually pays with.
    /// </summary>
    [TestClass]
    public class ThreadGuideLadderTests
    {
        // ---- table shape -----------------------------------------------------------------------------

        [TestMethod]
        public void The_ladder_is_fourteen_rungs_from_50_to_375_in_steps_of_25()
        {
            Assert.AreEqual(14, ThreadGuideLadder.Rungs.Count);
            CollectionAssert.AreEqual(Enumerable.Range(0, 14).Select(i => 50 + 25 * i).ToArray(),
                ThreadGuideLadder.Rungs.Select(r => r.Level).ToArray());
            Assert.AreEqual(ThreadGuideLadder.FirstRung, ThreadGuideLadder.Rungs[0].Level);
            Assert.AreEqual(ThreadGuideLadder.LastRung, ThreadGuideLadder.Rungs[13].Level);
            Assert.AreEqual(DungeonGemSpec.MaxGemLevel, ThreadGuideLadder.LastRung, "the ladder tops out at the gem ceiling");
        }

        [TestMethod]
        public void Special_types_sit_on_exactly_50_to_175_in_the_teaching_order()
        {
            var expected = new (int Level, string Type)[]
            {
                (50, RawFragmentRules.ScarabType),
                (75, RawFragmentRules.TaperType),
                (100, RawFragmentRules.HerbType),
                (125, RawFragmentRules.PowderType),
                (150, RawFragmentRules.TalismanType),
                (175, RawFragmentRules.PotionType),
            };

            foreach (var rung in ThreadGuideLadder.Rungs)
            {
                var match = expected.Where(e => e.Level == rung.Level).ToList();

                if (match.Count == 1)
                {
                    Assert.AreEqual(match[0].Type, rung.RequiredType, $"rung {rung.Level}");
                    Assert.AreEqual(match[0].Type, ThreadGuideLadder.RequiredType(rung.Level), $"rung {rung.Level}");
                    Assert.IsFalse(string.IsNullOrWhiteSpace(rung.Explanation), $"rung {rung.Level} needs an explanation");
                    Assert.IsTrue(rung.Explanation.All(c => c >= 0x20 && c < 0x7F), $"rung {rung.Level}: explanation must be plain ASCII");
                }
                else
                {
                    Assert.IsNull(rung.RequiredType, $"rung {rung.Level} is a plain rung");
                    Assert.IsNull(ThreadGuideLadder.RequiredType(rung.Level), $"rung {rung.Level}");
                    Assert.IsNull(rung.Explanation, $"rung {rung.Level}");
                }
            }

            // Each of the six slot types is taught exactly once.
            CollectionAssert.AreEquivalent(RawFragmentRules.SlotCounts.Keys.ToArray(),
                ThreadGuideLadder.Rungs.Where(r => r.RequiredType != null).Select(r => r.RequiredType).ToArray());

            Assert.IsNull(ThreadGuideLadder.RequiredType(60), "a non-rung level has no type");
        }

        /// <summary>
        /// Below 185 the tiers are the owner's ruling for the guide ladder (50 = 3, 75 = 4, 100 = 5, 125 = 5,
        /// 150 = 6, 175 = 6); from 200 up the tier follows the shipped Raw Fragment rungs (185..235 tier 7,
        /// 245..375 tier 8). Every rung has a tier, and the tier never falls as the rung rises.
        /// </summary>
        [TestMethod]
        public void Tiers_follow_the_owner_ruling_below_185_and_the_shipped_rungs_above()
        {
            var expected = new Dictionary<int, int>
            {
                [50] = 3, [75] = 4, [100] = 5, [125] = 5, [150] = 6, [175] = 6,
                [200] = 7, [225] = 7, [250] = 8, [275] = 8, [300] = 8, [325] = 8, [350] = 8, [375] = 8,
            };

            Assert.AreEqual(ThreadGuideLadder.Rungs.Count, expected.Count, "every rung is pinned");

            int? previous = null;
            foreach (var rung in ThreadGuideLadder.Rungs)
            {
                Assert.IsNotNull(rung.Tier, $"rung {rung.Level} must carry a tier");
                Assert.IsTrue(previous == null || rung.Tier >= previous, $"rung {rung.Level}: tier must not fall");
                previous = rung.Tier;
            }
            foreach (var pair in expected)
                Assert.AreEqual(pair.Value, ThreadGuideLadder.Get(pair.Key).Tier, $"rung {pair.Key}");
        }

        /// <summary>
        /// The live-content half of the tier claim: each tiered guide rung's tier equals the tier= written
        /// into the shipped Raw Fragment weenie at or below it (DungeonGemFactory.RungWcid's round-down) AND
        /// into the shipped rung above it, so the value is not an artefact of which neighbour was picked.
        /// </summary>
        [TestMethod]
        public void Tiered_rungs_match_the_tier_in_the_shipped_raw_fragment_weenies()
        {
            // Walk up for the repo's Content/sql/weenies. Not via FindDynamicDir: the build copies
            // Content/dungeons/dynamic next to the test assembly, but not the weenie SQL.
            string weenieDir = null;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && weenieDir == null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");
                if (Directory.Exists(candidate) && Directory.GetFiles(candidate, DungeonGemFactory.RawFragmentBaseWcid + " *.sql").Length == 1)
                    weenieDir = candidate;
            }

            if (weenieDir == null)
                Assert.Inconclusive("Could not locate Content/sql/weenies by walking up from the test assembly -- skipping.");

            int ShippedTier(uint wcid)
            {
                var file = Directory.GetFiles(weenieDir, wcid + " *.sql").Single();
                var m = Regex.Match(File.ReadAllText(file), @"\|tier=(\d+)\|");
                Assert.IsTrue(m.Success, $"{file} has no tier= in its DungeonGemSpec");
                return int.Parse(m.Groups[1].Value);
            }

            // Only the rungs at or above the first shipped weenie: below it the tier is an owner ruling with
            // no shipped weenie to agree with (RungWcid would clamp up to the level-185 rung).
            foreach (var rung in ThreadGuideLadder.Rungs.Where(r => r.Level >= DungeonGemFactory.RungBaseLevel))
            {
                var below = DungeonGemFactory.RungWcid(rung.Level);
                Assert.AreEqual(ShippedTier(below), rung.Tier, $"rung {rung.Level} vs shipped weenie {below}");

                var above = DungeonGemFactory.Rungs.FirstOrDefault(r => r.Level >= rung.Level);
                if (above.Wcid != 0)
                    Assert.AreEqual(ShippedTier(above.Wcid), rung.Tier, $"rung {rung.Level} vs shipped weenie {above.Wcid}");
            }
        }

        // ---- NextRung --------------------------------------------------------------------------------

        [TestMethod]
        [DataRow(0, 50)]
        [DataRow(50, 75)]
        [DataRow(75, 100)]
        [DataRow(175, 200)]
        [DataRow(350, 375)]
        [DataRow(-10, 50)]
        [DataRow(49, 50)]
        [DataRow(60, 75)]
        [DataRow(374, 375)]
        public void NextRung_is_the_smallest_rung_above_the_highest_won(int highestWon, int expected)
        {
            Assert.AreEqual(expected, ThreadGuideLadder.NextRung(highestWon));
        }

        [TestMethod]
        [DataRow(375)]
        [DataRow(400)]
        [DataRow(int.MaxValue)]
        public void NextRung_is_none_once_the_ladder_is_complete(int highestWon)
        {
            Assert.IsNull(ThreadGuideLadder.NextRung(highestWon));
        }

        [TestMethod]
        public void Walking_NextRung_from_zero_visits_every_rung_once_and_stops()
        {
            var visited = new List<int>();
            int? next = ThreadGuideLadder.NextRung(0);
            while (next != null)
            {
                visited.Add(next.Value);
                next = ThreadGuideLadder.NextRung(next.Value);
            }

            CollectionAssert.AreEqual(ThreadGuideLadder.Rungs.Select(r => r.Level).ToArray(), visited.ToArray());
        }

        // ---- RewardCap -------------------------------------------------------------------------------

        /// <summary>
        /// A synthetic cumulative chart, levels 0..400, whose per-level delta is 1000 + 10 * level: so the
        /// XP from L to L + 1 is known in closed form and strictly grows with level.
        /// </summary>
        private static IReadOnlyList<ulong> SyntheticChart()
        {
            var totals = new List<ulong> { 0 };
            for (var level = 0; level < 400; level++)
                totals.Add(totals[level] + (ulong)(1000 + 10 * level));
            return totals;
        }

        private static long Delta(int level) => 1000 + 10 * level;

        /// <summary>
        /// THE VERIFY CLAUSE. GrantLevelProportionalXp(1.0, 0, cap) must pay min(player's next-level XP, the
        /// next-level XP of a player AT the rung). Below the rung that is the player's own delta, at the rung
        /// it is the rung's (the same number), above it the cap binds. Asserted as literals derived from the
        /// closed form, AND as the min() identity, through the helper the player method pays with.
        /// </summary>
        [TestMethod]
        public void RewardCap_pays_min_of_player_and_rung_next_level_xp()
        {
            var chart = SyntheticChart();
            const int rung = 100;
            var cap = ThreadGuideLadder.RewardCap(rung, chart);

            Assert.AreEqual(2000L, cap, "the rung's own next-level delta: 1000 + 10 * 100");

            long Pays(int playerLevel) => EnlightenmentXpCurve.LevelProportionalXp(chart, playerLevel, 1.0, 0, cap);

            Assert.AreEqual(1800L, Pays(80), "below the rung: the player's own delta, 1000 + 10 * 80");
            Assert.AreEqual(2000L, Pays(100), "at the rung: the rung's delta");
            Assert.AreEqual(2000L, Pays(150), "above the rung: capped at the rung's delta, not 2500");

            foreach (var level in new[] { 1, 49, 80, 99, 100, 101, 150, 275, 375 })
                Assert.AreEqual(Math.Min(Delta(level), Delta(rung)), Pays(level), $"player level {level}");

            // And without the cap the same chart pays the player's full delta, so the cap is what bound above.
            Assert.AreEqual(2500L, EnlightenmentXpCurve.LevelProportionalXp(chart, 150, 1.0, 0, 0));
        }

        [TestMethod]
        public void RewardCap_is_positive_for_every_rung()
        {
            var chart = SyntheticChart();
            foreach (var rung in ThreadGuideLadder.Rungs)
            {
                var cap = ThreadGuideLadder.RewardCap(rung.Level, chart);
                Assert.IsTrue(cap > 0, $"rung {rung.Level}: a cap of 0 would mean UNCAPPED");
                Assert.AreEqual(Delta(rung.Level), cap, $"rung {rung.Level}");
            }

            // A degenerate flat chart has zero deltas; the cap must still not be 0.
            var flat = Enumerable.Repeat(0UL, 401).ToList();
            foreach (var rung in ThreadGuideLadder.Rungs)
                Assert.AreEqual(1L, ThreadGuideLadder.RewardCap(rung.Level, flat), $"rung {rung.Level} on a flat chart");
        }

        [TestMethod]
        public void RewardCap_refuses_a_level_that_is_not_a_rung()
        {
            Assert.ThrowsExactly<ArgumentException>(() => ThreadGuideLadder.RewardCap(60, SyntheticChart()));
            Assert.ThrowsExactly<ArgumentException>(() => ThreadGuideLadder.RewardCap(0, SyntheticChart()));
        }

        /// <summary>
        /// The pure helpers keep GetXPBetweenLevels' clamp: levelA to [1, ceiling - 1], levelB to [1, ceiling].
        /// </summary>
        [TestMethod]
        public void GetXPBetweenLevels_keeps_the_ceiling_clamp()
        {
            var chart = SyntheticChart();
            Assert.AreEqual((ulong)Delta(399), EnlightenmentXpCurve.GetXPBetweenLevels(chart, 399, 400));
            Assert.AreEqual((ulong)Delta(399), EnlightenmentXpCurve.GetXPBetweenLevels(chart, 400, 401), "both ends clamp to the last step");
            Assert.AreEqual((ulong)Delta(1), EnlightenmentXpCurve.GetXPBetweenLevels(chart, 0, 2), "levelA clamps up to 1");

            // min floors, max caps, and max <= 0 is uncapped.
            Assert.AreEqual(5000L, EnlightenmentXpCurve.LevelProportionalXp(chart, 10, 1.0, 5000, 0));
            Assert.AreEqual(550L, EnlightenmentXpCurve.LevelProportionalXp(chart, 10, 0.5, 0, -1));
        }
    }
}
