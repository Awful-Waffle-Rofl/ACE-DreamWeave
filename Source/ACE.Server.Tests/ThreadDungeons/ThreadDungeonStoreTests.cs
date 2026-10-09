using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadDungeonStoreTests
    {
        internal static string FindDynamicDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "dungeons", "dynamic");
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.json")))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        internal static ThreadDungeonStore LoadShipped()
        {
            var dir = FindDynamicDir();
            if (dir == null)
                Assert.Inconclusive("Could not locate Content/dungeons/dynamic by walking up from the test assembly -- skipping.");
            return ThreadDungeonStore.Load(dir);
        }

        [TestMethod]
        public void Shipped_files_parse_with_no_diagnostics()
        {
            var store = LoadShipped();
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("\n", store.Diagnostics));
            Assert.IsTrue(store.Dungeons.ContainsKey("filos_doom"));
            Assert.IsTrue(store.Bosses.Count > 0);
            Assert.IsTrue(store.Modifiers.ContainsKey("hardy"));
            Assert.IsTrue(store.XpLadder.Count >= 6);
        }

        /// <summary>
        /// Shipped_files_parse_with_no_diagnostics proves nothing about puzzle-gates.json on its own: the loader is fail-open and
        /// ReadOrEmpty turns a missing file into "no sites" with zero diagnostics. So this pins the file's presence and that
        /// every site in it SURVIVED validation: the per-dungeon count the loader returns must equal the count in the JSON, and
        /// any dropped site would also have left a diagnostic.
        /// </summary>
        [TestMethod]
        public void Shipped_puzzle_gates_file_is_read_and_every_site_survives_validation()
        {
            var store = LoadShipped();
            var path = Path.Combine(FindDynamicDir(), "puzzle-gates.json");
            Assert.IsTrue(File.Exists(path), "Content/dungeons/dynamic/puzzle-gates.json is missing; the loader would silently load zero sites");

            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)).AsObject();
            var total = 0;
            foreach (var (id, node) in root["dungeons"].AsObject())
            {
                var expected = node["sites"].AsArray().Count;
                total += expected;
                Assert.AreEqual(expected, store.GetPuzzleSites(id).Count, $"dungeon '{id}': the loader kept a different number of sites than the file lists\n" + string.Join("\n", store.Diagnostics));
            }

            Assert.IsTrue(total > 0, "the shipped puzzle-gates.json lists no sites");
            Assert.AreEqual(0, store.Diagnostics.Count, string.Join("\n", store.Diagnostics));
        }

        /// <summary>
        /// The reward scene happens only if a dungeon has a reward site AND the picker can draw it. Pins both on the shipped file:
        /// at least one reward site exists, and for every dungeon that lists one the picker, asked for a reward with no gates,
        /// returns a reward pick (an unusable reward site would otherwise be silently inert in play).
        /// </summary>
        [TestMethod]
        public void Shipped_puzzle_gates_file_has_reward_sites_the_picker_can_draw()
        {
            var store = LoadShipped();
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(FindDynamicDir(), "puzzle-gates.json"))).AsObject();
            var withReward = 0;
            foreach (var (id, node) in root["dungeons"].AsObject())
            {
                if (!node["sites"].AsArray().Any(s => s["kind"].GetValue<string>() == "reward"))
                    continue;

                withReward++;
                var picks = ThreadPuzzleSitePicker.Pick(store.GetPuzzleSites(id), 0, true, 12345);
                Assert.IsTrue(picks.Any(p => p.IsReward), $"dungeon '{id}' lists a reward site but the picker draws none from it");
            }

            Assert.IsTrue(withReward > 0, "the shipped puzzle-gates.json has no reward site at all");
        }

        [TestMethod]
        public void Dungeon_entry_binds_landblock_and_points()
        {
            var store = LoadShipped();
            var d = store.Dungeons["filos_doom"];
            Assert.AreEqual((ushort)0x0150, d.Landblock);
            Assert.IsTrue(d.Points.Count > 0);
            Assert.IsNotNull(d.BossAnchor);
            Assert.AreEqual(0x0150u, d.Points[0].Cell >> 16);
        }

        [TestMethod]
        public void Index_entry_without_dungeon_file_is_a_diagnostic()
        {
            var index = "{\"dungeons\":[{\"id\":\"ghost\",\"landblock\":\"0x0ABC\",\"name\":\"Ghost\",\"minLevel\":1,\"maxLevel\":10,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";
            var store = ThreadDungeonStore.Parse(index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", new Dictionary<string, string>());
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("ghost") && m.Contains("0x0ABC.json")));
        }

        [TestMethod]
        public void Xp_ladder_must_be_ascending()
        {
            var mods = "{\"modifiers\":[],\"xpLadder\":[{\"level\":50,\"xp\":10000},{\"level\":20,\"xp\":3500}]}";
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", mods, new Dictionary<string, string>());
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("xpLadder")));
        }

        /// <summary>
        /// The shipped ladder has to reach past the HIGHEST BOSS LEVEL a run can stamp, not merely past the run
        /// level itself. DungeonRewardMath.LadderXp returns the top rung's flat value for every level at or above
        /// it, so a ladder that stopped short - as it did at 275 before the 2026-09-09 ceiling raise, and at 415
        /// before the 2026-10-08 run ceiling raise to 500 - pays every boss above its top rung the same flat XP,
        /// and both the boss term and the band-widening uplift term in DungeonPopulationBuilder.BaseXp go inert
        /// exactly where the new run levels live.
        ///
        /// Re-derived 2026-10-08 (owner ruling: run level up to 500, reach-up from the pivot upward). The boss
        /// level is a max of floors (DungeonPopulationBuilder's I1 block): ceil(L x margin), L + 1, and one
        /// above the highest PROJECTED pack level. A reach-up stamp never exceeds ceil(L x BandHigh), because the
        /// projection maps the authored top onto the band's high edge at the run level, so the highest boss
        /// level a run at the ceiling can stamp is max(ceil(500 x 1.10), ceil(500 x 1.15) + 1) = 576. Both
        /// expressions are reproduced from the shipped constants rather than hard-coded, so a changed margin or
        /// band edge fails here instead of silently flattening rewards in a play session.
        /// </summary>
        [TestMethod]
        public void The_shipped_xp_ladder_reaches_the_highest_boss_level_at_the_run_ceiling()
        {
            var store = LoadShipped();

            var marginTerm = (int)Math.Ceiling(DungeonGemSpec.MaxRunLevel * DungeonPopulationLimits.DefaultBossLevelMargin - 1e-9);
            var packTerm = (int)Math.Ceiling(DungeonGemSpec.MaxRunLevel * DungeonRosterSelector.BandHigh - 1e-9) + 1;
            var bossLevel = Math.Max(Math.Max(marginTerm, packTerm), DungeonGemSpec.MaxRunLevel + 1);
            Assert.AreEqual(576, bossLevel, "max(ceil(500 x 1.10), ceil(500 x 1.15) + 1, 501)");

            var topRung = store.XpLadder[store.XpLadder.Count - 1];
            Assert.IsTrue(topRung.Level >= bossLevel,
                $"the top ladder rung is level {topRung.Level}; it must sit at or above the highest boss level {bossLevel} " +
                $"a run at the ceiling {DungeonGemSpec.MaxRunLevel} can stamp, or every boss above the top rung reads flat");

            // Strictly increasing across the whole extension, so no boss level in (375, 576] reads the same XP as
            // the boss one rung below it.
            Assert.IsTrue(DungeonRewardMath.LadderXp(store.XpLadder, bossLevel) > DungeonRewardMath.LadderXp(store.XpLadder, 550));
            Assert.IsTrue(DungeonRewardMath.LadderXp(store.XpLadder, DungeonGemSpec.MaxRunLevel) > DungeonRewardMath.LadderXp(store.XpLadder, 415));
            Assert.IsTrue(DungeonRewardMath.LadderXp(store.XpLadder, DungeonGemSpec.MaxGemLevel) > DungeonRewardMath.LadderXp(store.XpLadder, 275));
        }

        /// <summary>
        /// The rungs above 415 are the wiki curve's extrapolation verbatim
        /// (Source/.claude/skills/weenie-generator/references/wiki/monster_reward_curve.tsv, xp_source "extrap"),
        /// pinned so a hand edit to one rung is visible here.
        /// </summary>
        [TestMethod]
        public void The_shipped_xp_ladder_extension_matches_the_reward_curve_extrapolation()
        {
            var store = LoadShipped();
            var expected = new Dictionary<int, long>
            {
                { 425, 11000000 }, { 450, 13500000 }, { 475, 16550000 }, { 500, 20250000 },
                { 525, 24800000 }, { 550, 30400000 }, { 575, 37200000 }, { 600, 45550000 },
            };

            foreach (var kvp in expected)
                Assert.AreEqual(kvp.Value, DungeonRewardMath.LadderXp(store.XpLadder, kvp.Key), $"rung {kvp.Key}");
        }

        // ---- dungeon level-range lint --------------------------------------------------------------------

        /// <summary>
        /// One valid dungeon index entry with the given level range, plus the spawn file it needs to survive
        /// every structural check ahead of the level-range lint. Without the spawn file the entry is dropped
        /// long before the lint runs, which is why the other index tests in this file never trip it.
        /// </summary>
        private static ThreadDungeonStore ParseIndexWithLevels(int minLevel, int maxLevel, bool enabled = true)
        {
            var index = "{\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos' Doom\",\"minLevel\":"
                        + minLevel + ",\"maxLevel\":" + maxLevel + ",\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":"
                        + (enabled ? "true" : "false") + "}]}";
            var spawnFile = "{\"version\":1,\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22020491,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";
            var byStem = new Dictionary<string, string> { { "0x0150", spawnFile } };

            return ThreadDungeonStore.Parse(index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", byStem);
        }

        /// <summary>
        /// The check that makes a forgotten index.json edit loud. An "any" gem picks its dungeon by filtering
        /// on [minLevel, maxLevel] (ThreadDungeonGemHandler's dungeon selection), so a roster that caps below the
        /// RUN ceiling (DungeonGemSpec.MaxRunLevel, 500 since the 2026-10-08 split) leaves a top gem with no
        /// candidates and the only player-visible symptom is one chat line - "No dungeon answers this gem's call right now." Nothing in the log said why until this lint.
        ///
        /// The entry is REPORTED, not dropped: a level range is a tuning value, and taking a whole dungeon
        /// offline over one would be the worse outcome.
        /// </summary>
        [TestMethod]
        public void A_dungeon_capped_below_the_run_ceiling_is_a_diagnostic()
        {
            var store = ParseIndexWithLevels(60, DungeonGemSpec.MaxRunLevel - 1);

            Assert.AreEqual(1, store.Dungeons.Count, "the entry is reported, never dropped");
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("filos_doom") && m.Contains("below the run level ceiling")),
                string.Join("\n", store.Diagnostics));

            // The discriminating half: an entry that DOES admit the ceiling is silent, so the lint is
            // measuring the cap rather than firing on every dungeon.
            var atCeiling = ParseIndexWithLevels(60, DungeonGemSpec.MaxRunLevel);
            Assert.AreEqual(1, atCeiling.Dungeons.Count);
            Assert.IsFalse(atCeiling.Diagnostics.Any(m => m.Contains("below the run level ceiling")),
                string.Join("\n", atCeiling.Diagnostics));

            // A DISABLED entry is exempt: it is out of the candidate pool either way, so it cannot strand a
            // gem and an author parking one at an old ceiling should not be nagged.
            var disabled = ParseIndexWithLevels(60, DungeonGemSpec.MaxRunLevel - 1, enabled: false);
            Assert.IsFalse(disabled.Diagnostics.Any(m => m.Contains("below the run level ceiling")),
                string.Join("\n", disabled.Diagnostics));
        }

        /// <summary>An inverted range can never match any gem level, and is named even on a parked entry.</summary>
        [TestMethod]
        public void A_dungeon_whose_minLevel_exceeds_its_maxLevel_is_a_diagnostic()
        {
            var store = ParseIndexWithLevels(DungeonGemSpec.MaxRunLevel + 25, DungeonGemSpec.MaxRunLevel);

            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("filos_doom") && m.Contains("exceeds maxLevel")),
                string.Join("\n", store.Diagnostics));

            var disabled = ParseIndexWithLevels(DungeonGemSpec.MaxRunLevel + 25, DungeonGemSpec.MaxRunLevel, enabled: false);
            Assert.IsTrue(disabled.Diagnostics.Any(m => m.Contains("exceeds maxLevel")),
                "unlike the ceiling check, this one is NOT gated on enabled: " + string.Join("\n", disabled.Diagnostics));
        }

        /// <summary>
        /// The live-tree half of the same check, read off the REAL Content/dungeons/dynamic/index.json rather
        /// than a fixture: every enabled dungeon must admit a gem at the RUN ceiling. Raising
        /// DungeonGemSpec.MaxRunLevel without raising these entries (all 25 moved 375 -> 500 on 2026-10-08) is
        /// exactly the silent failure the lint above exists for, and this is the test that catches it in the shipped content.
        /// </summary>
        [TestMethod]
        public void Every_enabled_shipped_dungeon_admits_a_gem_at_the_level_ceiling()
        {
            var store = LoadShipped();

            var enabled = store.Dungeons.Values.Where(d => d.Enabled).ToList();
            Assert.IsTrue(enabled.Count > 0, "the shipped index parsed to zero enabled dungeons");

            var stranding = enabled
                .Where(d => DungeonGemSpec.MaxRunLevel < d.MinLevel || DungeonGemSpec.MaxRunLevel > d.MaxLevel)
                .Select(d => $"{d.Id} [{d.MinLevel}, {d.MaxLevel}]")
                .ToList();

            Assert.AreEqual(0, stranding.Count,
                $"enabled dungeons that a level-{DungeonGemSpec.MaxRunLevel} gem could never roll: " + string.Join("; ", stranding));
        }

        [TestMethod]
        public void ResolveFolder_subpath_prefers_override_then_content_then_exe()
        {
            var existing = new HashSet<string> { @"C:\ovr", Path.Combine(@"C:\content", "dungeons", "dynamic"), Path.Combine(@"C:\exe", "Content", "dungeons", "dynamic") };
            Func<string, bool> exists = p => existing.Contains(p);
            var sub = new[] { "dungeons", "dynamic" };

            Assert.AreEqual(@"C:\ovr", WorldEventAxisStore.ResolveFolder(@"C:\ovr", @"C:\content", @"C:\exe", sub, exists, out var r1));
            Assert.AreEqual("override", r1);
            Assert.AreEqual(Path.Combine(@"C:\content", "dungeons", "dynamic"), WorldEventAxisStore.ResolveFolder("", @"C:\content", @"C:\exe", sub, exists, out var r2));
            Assert.AreEqual("content_folder", r2);
            Assert.AreEqual(Path.Combine(@"C:\exe", "Content", "dungeons", "dynamic"), WorldEventAxisStore.ResolveFolder("", "", @"C:\exe", sub, exists, out var r3));
            Assert.AreEqual("exe-adjacent", r3);
            Assert.IsNull(WorldEventAxisStore.ResolveFolder("", "", "", sub, exists, out var r4));
            Assert.AreEqual("none", r4);
        }

        // ---- lint rule 24: drawWeight (owner ruling, 2026-09-08) -------------------------------------

        /// <summary>
        /// One modifier row carrying the given drawWeight token, and nothing else worth linting. Fed to
        /// <see cref="ParseModifiers"/> (defined with the salvage-affinity tests below), which takes ROWS
        /// rather than a whole file.
        /// </summary>
        private static string HardyRowWithDrawWeight(string token)
            => "{\"id\":\"hardy\",\"display\":\"Hardy\",\"rarity\":\"common\",\"target\":\"monster\","
               + "\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\",\"drawWeight\":"
               + token + "}";

        /// <summary>
        /// RULE 24. A drawWeight that is not a finite number >= 0 is CORRECTED to the 1.0 default with a
        /// diagnostic rather than dropping the row: a tuning value is not worth losing a modifier over, and
        /// both failure modes it prevents are silent ones. A negative would subtract from the running total
        /// inside the weighted pick; an infinity or a NaN would poison the total for every OTHER row sharing
        /// the pool.
        ///
        /// The two tokens asserted are the two that actually REACH the rule, measured rather than assumed:
        /// a negative arrives as written, and an overflowing literal arrives as Infinity. The NaN case has
        /// its own test below, because it never gets that far.
        /// </summary>
        [TestMethod]
        public void Rule24_a_negative_or_infinite_drawWeight_is_corrected_to_one()
        {
            foreach (var token in new[] { "-1.5", "-0.0001", "1e400" })
            {
                var store = ParseModifiers(HardyRowWithDrawWeight(token));

                Assert.IsTrue(store.Modifiers.ContainsKey("hardy"), $"'{token}': the row is corrected, never dropped");
                Assert.AreEqual(1.0, store.Modifiers["hardy"].DrawWeight, 0.0001, $"'{token}': corrected to the default");
                Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("hardy") && m.Contains("drawWeight")),
                    $"'{token}': the correction is announced: {string.Join(" | ", store.Diagnostics)}");
            }
        }

        /// <summary>
        /// A bare NaN token cannot reach rule 24 at all: System.Text.Json refuses it without
        /// AllowNamedFloatingPointLiterals, so the WHOLE modifiers.json fails to deserialize and every
        /// modifier is lost with a diagnostic. That is louder than a correction, not quieter, so it is
        /// pinned as the behaviour rather than worked around - and it is why the rule's NaN arm is a guard
        /// on the VALUE rather than a claim about a token any file can carry.
        /// </summary>
        [TestMethod]
        public void Rule24_a_NaN_drawWeight_token_fails_the_whole_file_rather_than_reaching_the_rule()
        {
            var store = ParseModifiers(HardyRowWithDrawWeight("NaN"));

            Assert.AreEqual(0, store.Modifiers.Count, "the file did not deserialize, so no modifier loaded");
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("modifiers.json")),
                $"the failure is announced: {string.Join(" | ", store.Diagnostics)}");
        }

        /// <summary>
        /// The default and the legal values either side of it. An ABSENT field is 1.0, 0.0 is legal and
        /// means "never drawn at random", and there is deliberately NO upper clamp - a weight above 1.0 is
        /// the way a row is made commoner than the field once there is play data to justify it.
        /// </summary>
        [TestMethod]
        public void Rule24_absent_zero_and_above_one_drawWeights_are_all_left_alone()
        {
            var absent = ParseModifiers(
                "{\"id\":\"hardy\",\"display\":\"Hardy\",\"rarity\":\"common\",\"target\":\"monster\","
                + "\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\"}");

            Assert.AreEqual(1.0, absent.Modifiers["hardy"].DrawWeight, 0.0001, "an absent field is the 1.0 default");
            Assert.IsFalse(absent.Diagnostics.Any(m => m.Contains("drawWeight")), "and is not a diagnostic");

            foreach (var (token, expected) in new[] { ("0", 0.0), ("0.35", 0.35), ("2.5", 2.5) })
            {
                var store = ParseModifiers(HardyRowWithDrawWeight(token));

                Assert.AreEqual(expected, store.Modifiers["hardy"].DrawWeight, 0.0001, $"'{token}' is legal");
                Assert.IsFalse(store.Diagnostics.Any(m => m.Contains("drawWeight")), $"'{token}' raises no diagnostic");
            }
        }

        // ---- boss-target monsterEffectKind allowlist (bug fix, 2026-09-24) ----------------------------

        /// <summary>
        /// A target: "boss" row whose monsterEffectKind has no Boss* plan field (crit_rating here; see
        /// DungeonRewardMath.BossEligibleMonsterEffectKinds) is dropped at load time rather than silently
        /// reaching neither the pack nor the boss.
        /// </summary>
        [TestMethod]
        public void A_boss_target_row_with_an_unsupported_kind_is_dropped()
        {
            var store = ParseModifiers("{\"id\":\"boss_precise\",\"target\":\"boss\",\"minMagnitude\":10,\"maxMagnitude\":30,\"monsterEffectKind\":\"crit_rating\"}");

            Assert.AreEqual(0, store.Modifiers.Count, "the row must be dropped, not half-loaded");
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("boss_precise") && m.Contains("no boss path")),
                string.Join(" | ", store.Diagnostics));
        }

        /// <summary>Control: a boss-target row of a SUPPORTED kind (damage_rating) is kept with no diagnostic.</summary>
        [TestMethod]
        public void A_boss_target_row_with_a_supported_kind_is_kept()
        {
            var store = ParseModifiers("{\"id\":\"boss_enraged\",\"target\":\"boss\",\"minMagnitude\":20,\"maxMagnitude\":60,\"monsterEffectKind\":\"damage_rating\"}");

            Assert.IsTrue(store.Modifiers.ContainsKey("boss_enraged"));
            Assert.IsFalse(store.Diagnostics.Any(m => m.Contains("boss_enraged")), string.Join(" | ", store.Diagnostics));
        }

        // ---- hollow magnitude range (owner ruling, 2026-09-17) ---------------------------------------

        /// <summary>
        /// A hollow-kind row's magnitude is a fraction ACE.Server.Entity.HollowMath clamps to [0, 1], so a
        /// row authored outside 0 &lt; min &lt;= max &lt;= 1 would silently mean something other than what it
        /// says. Dropped, not corrected - there is no single sane fallback fraction.
        /// </summary>
        [TestMethod]
        public void A_hollow_row_outside_0_to_1_is_dropped()
        {
            foreach (var (minToken, maxToken) in new[] { ("0", "1"), ("0.5", "1.5"), ("-0.1", "1") })
            {
                var store = ParseModifiers(
                    "{\"id\":\"hollow_bad\",\"target\":\"monster\",\"minMagnitude\":" + minToken + ",\"maxMagnitude\":" + maxToken
                    + ",\"monsterEffectKind\":\"hollow\"}");

                Assert.AreEqual(0, store.Modifiers.Count, $"min={minToken} max={maxToken}: the row must be dropped");
                Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("hollow_bad") && m.Contains("hollow magnitude range")),
                    $"min={minToken} max={maxToken}: {string.Join(" | ", store.Diagnostics)}");
            }
        }

        /// <summary>The legal range, inclusive at both ends, is accepted with no diagnostic.</summary>
        [TestMethod]
        public void A_hollow_row_within_0_to_1_is_kept()
        {
            var store = ParseModifiers("{\"id\":\"hollow_ok\",\"target\":\"monster\",\"minMagnitude\":0.01,\"maxMagnitude\":1.0,\"monsterEffectKind\":\"hollow\"}");

            Assert.IsTrue(store.Modifiers.ContainsKey("hollow_ok"));
            Assert.IsFalse(store.Diagnostics.Any(m => m.Contains("hollow_ok")), string.Join(" | ", store.Diagnostics));
        }

        /// <summary>
        /// THE SHIPPED TUNING, asserted here because this is the file's own test class: hollow is the ONE
        /// row below the default, at 0.25 (raised from 0.1 when hollow stopped being all-or-nothing - owner
        /// ruling 2026-09-17), and every other row is left at 1.0 until there is play data to tune from
        /// (owner ruling, 2026-09-08). A second reduced row landing without this test moving is the thing
        /// worth noticing.
        /// </summary>
        [TestMethod]
        public void The_shipped_file_reduces_hollow_and_nothing_else()
        {
            var store = LoadShipped();

            Assert.AreEqual(0.25, store.Modifiers["hollow"].DrawWeight, 0.0001, "hollow is the deadliest modifier in the catalog");

            var reduced = store.Modifiers.Values.Where(m => m.DrawWeight < 1.0).Select(m => m.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(new[] { "hollow" }, reduced,
                $"exactly one row is below 1.0: {string.Join(", ", reduced)}");
        }

        [TestMethod]
        public void Parse_tolerates_null_modifiers_array()
        {
            var mods = "{\"modifiers\":null,\"xpLadder\":[{\"level\":20,\"xp\":3500}]}";
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", mods, new Dictionary<string, string>());
            Assert.AreEqual(0, store.Modifiers.Count);
        }

        [TestMethod]
        public void Parse_tolerates_null_xpLadder_array()
        {
            var mods = "{\"modifiers\":[],\"xpLadder\":null}";
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", mods, new Dictionary<string, string>());
            Assert.AreEqual(0, store.XpLadder.Count);
        }

        [TestMethod]
        public void Parse_tolerates_null_bosses_array()
        {
            var mods = "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}";
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":null}", mods, new Dictionary<string, string>());
            Assert.AreEqual(0, store.Bosses.Count);
        }

        [TestMethod]
        public void Parse_tolerates_null_dungeons_array()
        {
            var mods = "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}";
            var store = ThreadDungeonStore.Parse("{\"dungeons\":null}", "{\"bosses\":[]}", mods, new Dictionary<string, string>());
            Assert.AreEqual(0, store.Dungeons.Count);
        }

        [TestMethod]
        public void Parse_tolerates_null_modifiers_on_a_boss_entry()
        {
            var mods = "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}";
            var bosses = "{\"bosses\":[{\"wcid\":10981,\"name\":\"Aun Tanua\",\"level\":110,\"families\":null,\"modifiers\":null}]}";
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", bosses, mods, new Dictionary<string, string>());
            Assert.AreEqual(1, store.Bosses.Count);
            Assert.AreEqual(0, store.Bosses[0].Modifiers.Count);
        }

        [TestMethod]
        public void Parse_tolerates_null_points_on_a_landblock_file()
        {
            var index = "{\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos' Doom\",\"minLevel\":1,\"maxLevel\":10,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";
            var spawnFile = "{\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":null,\"bossAnchor\":{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"qw\":1,\"qx\":0,\"qy\":0,\"qz\":0,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";
            var byStem = new Dictionary<string, string> { { "0x0150", spawnFile } };
            var store = ThreadDungeonStore.Parse(index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", byStem);
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("no points")));
        }

        /// <summary>
        /// TECH-DESIGN S9.4: a file whose schema version this build does not know is a diagnostic and is
        /// treated as empty, never half-read.
        /// </summary>
        [TestMethod]
        public void Unknown_index_version_is_a_diagnostic_and_yields_no_dungeons()
        {
            var index = "{\"version\":2,\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos' Doom\",\"minLevel\":1,\"maxLevel\":10,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";
            var spawnFile = "{\"version\":1,\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22020491,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";
            var byStem = new Dictionary<string, string> { { "0x0150", spawnFile } };

            var store = ThreadDungeonStore.Parse(index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", byStem);

            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("index.json") && m.Contains("unsupported version 2")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Unknown_spawn_file_version_drops_only_that_dungeon()
        {
            var index = "{\"version\":1,\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos' Doom\",\"minLevel\":1,\"maxLevel\":10,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";
            var spawnFile = "{\"version\":2,\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22020491,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";
            var byStem = new Dictionary<string, string> { { "0x0150", spawnFile } };

            var store = ThreadDungeonStore.Parse(index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", byStem);

            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("0x0150.json") && m.Contains("unsupported version 2")), string.Join("\n", store.Diagnostics));
        }

        /// <summary>A file with no "version" key is version 1 - the shipped files predate the field.</summary>
        [TestMethod]
        public void A_missing_version_key_is_treated_as_version_1()
        {
            // attunementJson is not supplied (defaults to null), so the store's own "attunement.json: empty"
            // diagnostic (attunement lint rule 2) is expected here; it is unrelated to the version key under test.
            var store = ThreadDungeonStore.Parse("{\"dungeons\":[]}", "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", new Dictionary<string, string>());
            Assert.IsFalse(store.Diagnostics.Any(m => m.Contains("version")), string.Join("\n", store.Diagnostics));
        }

        /// <summary>
        /// The boss anchor is checked against the landblock alongside the points: it is placed by the same
        /// spawner, so an anchor on another landblock would put the boss outside the copy.
        /// </summary>
        [TestMethod]
        public void Boss_anchor_off_the_landblock_is_a_diagnostic()
        {
            var index = "{\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos' Doom\",\"minLevel\":1,\"maxLevel\":10,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";
            // The point's cell 22020490 (0x0150018A) is on 0x0150; the anchor's 22085889 (0x01510101) is on 0x0151.
            var spawnFile = "{\"landblock\":\"0x0150\",\"entry\":\"0x0150018A 30 0 0.005 1 0 0 0\",\"points\":[{\"cell\":22020490,\"x\":30,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":1,\"curated\":true}],\"bossAnchor\":{\"cell\":22085889,\"x\":31,\"y\":12,\"z\":0.005,\"clearance\":4.0,\"depth\":2,\"curated\":true}}";
            var byStem = new Dictionary<string, string> { { "0x0150", spawnFile } };

            var store = ThreadDungeonStore.Parse(index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", byStem);

            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("a point or the boss anchor")), string.Join("\n", store.Diagnostics));
        }

        /// <summary>
        /// TECH-DESIGN S9 invariant 5: Load returns a diagnostic and an empty store, never a throw.
        /// ThreadDungeonManager.Initialize runs from Program.Main outside any try, so a throw here would
        /// abort server startup for a feature that ships disabled.
        /// </summary>
        [TestMethod]
        public void An_unreadable_spawn_file_is_a_diagnostic_not_a_throw()
        {
            var temp = Path.Combine(Path.GetTempPath(), "ddstore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            try
            {
                File.WriteAllText(Path.Combine(temp, "index.json"), "{\"dungeons\":[]}");
                File.WriteAllText(Path.Combine(temp, "bosses.json"), "{\"bosses\":[]}");
                File.WriteAllText(Path.Combine(temp, "modifiers.json"), "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}");

                var locked = Path.Combine(temp, "0xBAD.json");
                File.WriteAllText(locked, "{}");

                // Held with FileShare.None, so File.ReadAllText on it throws - the "unreadable entry" case.
                using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var store = ThreadDungeonStore.Load(temp);

                    Assert.AreEqual(0, store.Dungeons.Count);
                    Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("0xBAD.json") && m.Contains("could not read")), string.Join("\n", store.Diagnostics));
                }
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        [TestMethod]
        public void A_missing_folder_is_a_diagnostic_not_a_throw()
        {
            var missing = Path.Combine(Path.GetTempPath(), "ddstore_missing_" + Guid.NewGuid().ToString("N"));

            var store = ThreadDungeonStore.Load(missing);

            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("could not enumerate")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void Parse_tolerates_null_dungeonJsonByStem()
        {
            var index = "{\"dungeons\":[{\"id\":\"filos_doom\",\"landblock\":\"0x0150\",\"name\":\"Filos' Doom\",\"minLevel\":1,\"maxLevel\":10,\"families\":[],\"creatureTypes\":[],\"exitPortalWcid\":1003601,\"enabled\":true}]}";
            var store = ThreadDungeonStore.Parse(index, "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}", null);
            Assert.AreEqual(0, store.Dungeons.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("filos_doom") && m.Contains("0x0150.json")));
        }

        // ---- salvage affinity validation ---------------------------------------------------------------

        private static ThreadDungeonStore ParseModifiers(string modifierRows) => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}", "{\"bosses\":[]}",
            "{\"modifiers\":[" + modifierRows + "],\"xpLadder\":[{\"level\":20,\"xp\":3500}]}",
            new Dictionary<string, string>());

        [TestMethod]
        public void A_salvage_affinity_row_with_an_undefined_material_is_dropped()
        {
            // Zero is the case that matters: MaterialType.Unknown IS a defined enum member, so a bare
            // Enum.IsDefined would wave an unset field straight through and ship an item that appraises as
            // nothing and salvages into nothing.
            var store = ParseModifiers("{\"id\":\"affinity_bad\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":0,\"salvageBaseWcid\":2398}");

            Assert.AreEqual(0, store.Modifiers.Count, "the row must be dropped, not half-loaded");
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("affinity_bad") && m.Contains("salvageMaterial")), string.Join("\n", store.Diagnostics));

            // And a value past the end of the enum, which is the other way to fat-finger it.
            var outOfRange = ParseModifiers("{\"id\":\"affinity_bad\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":9999,\"salvageBaseWcid\":2398}");
            Assert.AreEqual(0, outOfRange.Modifiers.Count);
            Assert.IsTrue(outOfRange.Diagnostics.Any(m => m.Contains("salvageMaterial")));
        }

        [TestMethod]
        public void A_salvage_affinity_row_with_no_base_wcid_is_dropped()
        {
            var store = ParseModifiers("{\"id\":\"affinity_bad\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":43}");

            Assert.AreEqual(0, store.Modifiers.Count);
            Assert.IsTrue(store.Diagnostics.Any(m => m.Contains("affinity_bad") && m.Contains("salvageBaseWcid")), string.Join("\n", store.Diagnostics));
        }

        [TestMethod]
        public void A_valid_salvage_affinity_row_survives_parse()
        {
            var store = ParseModifiers("{\"id\":\"affinity_tourmaline\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":43,\"salvageBaseWcid\":2398}");

            Assert.AreEqual(1, store.Modifiers.Count, string.Join("\n", store.Diagnostics));
            Assert.AreEqual(43, store.Modifiers["affinity_tourmaline"].SalvageMaterial);
            Assert.AreEqual(2398u, store.Modifiers["affinity_tourmaline"].SalvageBaseWcid);
        }

        /// <summary>
        /// The half Parse cannot do, because it is pure and the world database is not up when it runs: a base
        /// wcid that names no weenie can only ever produce a null from WorldObjectFactory at the death path,
        /// so it is reported AND dropped rather than left to pay nothing every kill.
        /// </summary>
        [TestMethod]
        public void A_salvage_affinity_row_whose_base_wcid_does_not_resolve_is_dropped_at_world_start()
        {
            var store = ParseModifiers(
                "{\"id\":\"affinity_tourmaline\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":43,\"salvageBaseWcid\":2398}," +
                "{\"id\":\"affinity_ghost\",\"target\":\"monster\",\"minMagnitude\":5,\"maxMagnitude\":25,\"monsterEffectKind\":\"salvage_affinity\",\"salvageMaterial\":12,\"salvageBaseWcid\":999999}," +
                "{\"id\":\"hardy\",\"target\":\"monster\",\"minMagnitude\":1.3,\"maxMagnitude\":2.0,\"monsterEffectKind\":\"health_mult\"}");

            Assert.AreEqual(3, store.Modifiers.Count, "guard: all three survive parse, which cannot check the world database");

            var problems = store.ValidateWorldWcids(wcid => wcid == 2398);

            Assert.IsTrue(problems.Any(m => m.Contains("affinity_ghost") && m.Contains("999999")), string.Join("\n", problems));
            Assert.IsFalse(store.Modifiers.ContainsKey("affinity_ghost"), "the unresolvable row must be dropped");
            Assert.IsTrue(store.Modifiers.ContainsKey("affinity_tourmaline"), "a resolvable row survives");
            Assert.IsTrue(store.Modifiers.ContainsKey("hardy"), "a non-affinity row is never touched by the wcid sweep");
        }

        /// <summary>Every shipped salvage_affinity row names a material and a base wcid that survive validation.</summary>
        [TestMethod]
        public void Shipped_salvage_affinity_rows_are_well_formed()
        {
            var store = LoadShipped();
            var affinities = store.Modifiers.Values.Where(m => m.MonsterEffectKind == DungeonRewardMath.SalvageAffinity).ToList();

            // The owner's chosen material list (ruling 2026-09-07), which replaced the gem-name alignment:
            // tourmaline, amethyst and obsidian kept from pre-v2, plus serpentine, steel, iron, mahogany and
            // green garnet. The count is asserted so a row added without a home in the powder slot is
            // noticed - see The_powder_slot_covers_every_affinity, which is the assertion that actually
            // matters. Adding a ninth material is one row here plus one powder converted from wildcard to
            // mapped, and both tests move together.
            Assert.AreEqual(8, affinities.Count);

            // Every material is distinct: two rows sharing one MaterialType would be two ways to roll the
            // same drop while reading as different affinities on the panel.
            CollectionAssert.AllItemsAreUnique(affinities.Select(m => m.SalvageMaterial).ToList());

            foreach (var def in affinities)
            {
                Assert.IsTrue(ThreadDungeonStore.IsSalvageMaterial(def.SalvageMaterial), $"{def.Id} material {def.SalvageMaterial}");
                Assert.AreNotEqual(0u, def.SalvageBaseWcid, def.Id);

                // Apart from id, display, material and base wcid the rows must be identical - no material
                // gets a wider magnitude range to compensate for anything.
                Assert.AreEqual("uncommon", def.Rarity, def.Id);
                Assert.AreEqual("monster", def.Target, def.Id);
                Assert.AreEqual(5.0, def.MinMagnitude, 1e-9, def.Id);
                Assert.AreEqual(25.0, def.MaxMagnitude, 1e-9, def.Id);
                Assert.AreEqual("constant", def.RewardXpKind, def.Id);
                Assert.AreEqual(1.0, def.RewardXpBase, 1e-9, def.Id);
                Assert.AreEqual(1.0, def.LootQuantityMult, 1e-9, $"{def.Id} must carry no loot-quantity factor");
                Assert.AreEqual(0.0, def.LootQualityBonus, 1e-9, $"{def.Id} must carry no loot-quality bonus");
            }
        }
    }
}
