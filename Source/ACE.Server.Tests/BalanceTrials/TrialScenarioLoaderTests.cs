using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Server.BalanceTrials;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Pins the scenario and room file schemas. Inline JSON only: this class is published to the public mirror and never
    /// reads tools/. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class TrialScenarioLoaderTests
    {
        private const string Example = @"{
  ""id"": ""t400-arch-solo"", ""room"": ""proving-arena-1"",
  ""gem"": { ""level"": 400, ""tier"": 8, ""dungeon"": ""artifex_collegium"", ""family"": ""any"", ""mods"": ""rolled"", ""seed"": 1234 },
  ""group"": 1,
  ""waves"": [ { ""role"": ""Trash"", ""count"": 6, ""pull"": ""all"" }, { ""role"": ""Elite"", ""count"": 2 }, { ""role"": ""Boss"", ""count"": 1 } ],
  ""hazards"": ""off"", ""reps"": 10, ""timeoutSecs"": 600
}";

        private const string RoomJson = @"{
  ""id"": ""proving-arena-1"", ""landblock"": ""0x0150"",
  ""start"": { ""cell"": 22020352, ""x"": 10, ""y"": 20, ""z"": 0.5 },
  ""bossPoint"": { ""cell"": 22020353, ""x"": 30, ""y"": 40, ""z"": 0.5, ""qw"": 0, ""qz"": 1 },
  ""points"": [
    { ""cell"": 22020354, ""x"": 1, ""y"": 2, ""z"": 3 },
    { ""cell"": 22020355, ""x"": 4, ""y"": 5, ""z"": 6 }
  ],
  ""excludeRows"": { ""landblock"": ""0x0150"", ""minX"": 0, ""maxX"": 100, ""minY"": 0, ""maxY"": 100, ""minZ"": -10, ""maxZ"": 10 }
}";

        private static string With(string from, string to)
        {
            Assert.IsTrue(Example.Contains(from), $"fixture has no '{from}'");
            return Example.Replace(from, to);
        }

        private static void Rejects(string json, string fragment)
        {
            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialScenarioLoader.Parse(json));
            StringAssert.Contains(ex.Message, fragment);
        }

        [TestMethod]
        public void Parses_the_design_example()
        {
            var s = TrialScenarioLoader.Parse(Example);
            Assert.AreEqual("t400-arch-solo", s.Id);
            Assert.AreEqual("proving-arena-1", s.Room);
            Assert.AreEqual(400, s.Gem.Level);
            Assert.AreEqual(8, s.Gem.Tier);
            Assert.AreEqual("artifex_collegium", s.Gem.Dungeon);
            Assert.AreEqual("any", s.Gem.Family);
            Assert.IsNull(s.Gem.PinnedMods);
            Assert.AreEqual(1234, s.Gem.Seed);
            Assert.AreEqual(TrialSeedMode.Fixed, s.Gem.SeedMode);
            Assert.AreEqual(1, s.Group);
            Assert.AreEqual(3, s.Waves.Count);
            Assert.AreEqual(DungeonRole.Trash, s.Waves[0].Role);
            Assert.AreEqual(6, s.Waves[0].Count);
            Assert.AreEqual(DungeonRole.Elite, s.Waves[1].Role);
            Assert.AreEqual(DungeonRole.Boss, s.Waves[2].Role);
            Assert.AreEqual("off", s.Hazards);
            Assert.AreEqual(10, s.Reps);
            Assert.AreEqual(600, s.TimeoutSecs);
            Assert.AreEqual(0, s.HardCopyEvery);
        }

        [TestMethod]
        public void Buff_profile_override_is_optional_and_checked_by_the_apply_rule()
        {
            Assert.IsNull(TrialScenarioLoader.Parse(Example).BuffProfile, "absent: the archetype's profile stays in force");

            string WithProfile(string p) => With(@"""timeoutSecs"": 600", @"""timeoutSecs"": 600, ""buffProfile"": """ + p + @"""");

            Assert.AreEqual("adminAll:8", TrialScenarioLoader.Parse(WithProfile("adminAll:8")).BuffProfile);
            Assert.AreEqual("adminAll:1", TrialScenarioLoader.Parse(WithProfile("adminAll:1")).BuffProfile);
            Assert.AreEqual("ownSpellbook", TrialScenarioLoader.Parse(WithProfile("ownSpellbook")).BuffProfile);

            foreach (var bad in new[] { "adminAll:9", "adminAll:0", "adminAll:", "AdminAll:8", "", "ownspellbook", "buffAll" })
                Rejects(WithProfile(bad), $"buffProfile '{bad}'");

            // The loader's rule and Apply's are one rule: every value the loader accepts, Apply accepts as a profile shape.
            foreach (var p in new[] { "adminAll:8", "ownSpellbook" })
                Assert.IsTrue(TrialBuffs.IsValidProfile(p), p);
            Assert.ThrowsExactly<ArgumentException>(() => TrialBuffs.Apply(
                (ACE.Server.WorldObjects.Player)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ACE.Server.WorldObjects.Player)), "adminAll:9"));
        }

        [TestMethod]
        public void Scenario_buff_profile_overrides_the_archetype_profile()
        {
            var archetype = new TrialArchetype("archmage-400", 3, ACE.Server.ClassAbilities.ClassAbilityClass.Archmage, 400, null, null, "ownSpellbook");
            var plain = TrialScenarioLoader.Parse(Example);

            Assert.AreEqual("ownSpellbook", TrialBuffs.EffectiveProfile(plain, archetype));
            Assert.AreEqual("adminAll:8", TrialBuffs.EffectiveProfile(plain with { BuffProfile = "adminAll:8" }, archetype));
        }

        [TestMethod]
        public void Parses_pinned_mods_step_seed_mode_and_hard_copy_every()
        {
            var json = With(@"""mods"": ""rolled"", ""seed"": 1234", @"""mods"": [{ ""id"": ""boss_guarded"", ""magnitude"": 1.5 }], ""seed"": 7, ""seedMode"": ""step""")
                .Replace(@"""timeoutSecs"": 600", @"""timeoutSecs"": 600, ""hardCopyEvery"": 3");
            var s = TrialScenarioLoader.Parse(json);
            Assert.AreEqual(1, s.Gem.PinnedMods.Count);
            Assert.AreEqual("boss_guarded", s.Gem.PinnedMods[0].Id);
            Assert.AreEqual(1.5, s.Gem.PinnedMods[0].Magnitude);
            Assert.AreEqual(TrialSeedMode.Step, s.Gem.SeedMode);
            Assert.AreEqual(3, s.HardCopyEvery);
        }

        [TestMethod] public void Rejects_a_group_scenario() => Rejects(With(@"""group"": 1", @"""group"": 2"), "Plan D");
        [TestMethod] public void Rejects_hazards_that_are_not_off() => Rejects(With(@"""hazards"": ""off""", @"""hazards"": ""on"""), "hazards");
        [TestMethod] public void Rejects_an_empty_dungeon() => Rejects(With(@"""dungeon"": ""artifex_collegium""", @"""dungeon"": """""), "dungeon");
        [TestMethod] public void Rejects_dungeon_any() => Rejects(With(@"""dungeon"": ""artifex_collegium""", @"""dungeon"": ""any"""), "dungeon");
        [TestMethod] public void Rejects_level_zero() => Rejects(With(@"""level"": 400", @"""level"": 0"), "level");
        [TestMethod] public void Rejects_level_above_the_run_ceiling() => Rejects(With(@"""level"": 400", @"""level"": 501"), "level");
        [TestMethod] public void Rejects_tier_zero() => Rejects(With(@"""tier"": 8", @"""tier"": 0"), "tier");
        [TestMethod] public void Rejects_tier_above_the_max() => Rejects(With(@"""tier"": 8", @"""tier"": 9"), "tier");
        [TestMethod] public void Rejects_zero_reps() => Rejects(With(@"""reps"": 10", @"""reps"": 0"), "reps");
        [TestMethod] public void Rejects_too_many_reps() => Rejects(With(@"""reps"": 10", @"""reps"": 1001"), "reps");
        [TestMethod] public void Rejects_a_short_timeout() => Rejects(With(@"""timeoutSecs"": 600", @"""timeoutSecs"": 29"), "timeoutSecs");
        [TestMethod] public void Rejects_a_long_timeout() => Rejects(With(@"""timeoutSecs"": 600", @"""timeoutSecs"": 7201"), "timeoutSecs");
        [TestMethod] public void Rejects_a_zero_wave_count() => Rejects(With(@"""role"": ""Elite"", ""count"": 2", @"""role"": ""Elite"", ""count"": 0"), "count");
        [TestMethod] public void Rejects_a_repeated_role() => Rejects(With(@"{ ""role"": ""Elite"", ""count"": 2 }", @"{ ""role"": ""Trash"", ""count"": 2 }"), "Trash");
        [TestMethod] public void Rejects_a_seed_of_zero() => Rejects(With(@"""seed"": 1234", @"""seed"": 0"), "seed");
        [TestMethod] public void Rejects_a_negative_seed() => Rejects(With(@"""seed"": 1234", @"""seed"": -5"), "seed");
        [TestMethod] public void Rejects_a_seed_above_int_max_minus_one() => Rejects(With(@"""seed"": 1234", @"""seed"": 2147483647"), "seed");
        [TestMethod] public void Accepts_the_seed_bounds()
        {
            Assert.AreEqual(1, TrialScenarioLoader.Parse(With(@"""seed"": 1234", @"""seed"": 1")).Gem.Seed);
            Assert.AreEqual(int.MaxValue - 1, TrialScenarioLoader.Parse(With(@"""seed"": 1234", @"""seed"": 2147483646")).Gem.Seed);
        }
        [TestMethod] public void Rejects_a_boss_count_above_one() => Rejects(With(@"""role"": ""Boss"", ""count"": 1", @"""role"": ""Boss"", ""count"": 2"), "Boss");
        [TestMethod] public void Rejects_a_numeric_role() => Rejects(With(@"""role"": ""Elite""", @"""role"": ""7"""), "role");
        [TestMethod] public void Rejects_a_lower_case_role() => Rejects(With(@"""role"": ""Elite""", @"""role"": ""elite"""), "role");
        [TestMethod] public void Rejects_a_pull_other_than_all() => Rejects(With(@"""pull"": ""all""", @"""pull"": ""some"""), "pull");
        [TestMethod] public void Rejects_a_negative_hard_copy_every() => Rejects(With(@"""timeoutSecs"": 600", @"""timeoutSecs"": 600, ""hardCopyEvery"": -1"), "hardCopyEvery");
        [TestMethod] public void Rejects_an_unknown_key() => Rejects(With(@"""reps"": 10", @"""reps"": 10, ""repz"": 1"), "repz");

        [TestMethod]
        public void Rejects_a_pinned_magnitude_that_is_not_finite()
        {
            // 1e999 overflows a double; the loader must refuse it whether the parser reports false or Infinity.
            Rejects(With(@"""mods"": ""rolled""", @"""mods"": [{ ""id"": ""boss_guarded"", ""magnitude"": 1e999 }]"), "magnitude");
        }

        [TestMethod]
        public void Hash_ignores_whitespace_and_key_order()
        {
            var compact = @"{""timeoutSecs"":600,""reps"":10,""hazards"":""off"",""group"":1,
""waves"":[{""count"":6,""role"":""Trash"",""pull"":""all""},{""role"":""Elite"",""count"":2},{""role"":""Boss"",""count"":1}],
""gem"":{""seed"":1234,""mods"":""rolled"",""family"":""any"",""dungeon"":""artifex_collegium"",""tier"":8,""level"":400},
""room"":""proving-arena-1"",""id"":""t400-arch-solo""}";
            Assert.AreEqual(TrialArchetypeLoader.Sha256(Example), TrialArchetypeLoader.Sha256(compact));

            var dir = Path.Combine(Path.GetTempPath(), "trial-scn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "t400-arch-solo.json"), compact);
                var loaded = TrialScenarioLoader.LoadDirectory(dir);
                Assert.AreEqual(1, loaded.Count);
                Assert.AreEqual(TrialArchetypeLoader.Sha256(Example), loaded[0].Sha256);
                Assert.AreEqual("t400-arch-solo", loaded[0].Scenario.Id);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void LoadDirectory_refuses_a_file_name_that_differs_from_the_id()
        {
            var dir = Path.Combine(Path.GetTempPath(), "trial-scn-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "other.json"), Example);
                var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialScenarioLoader.LoadDirectory(dir));
                StringAssert.Contains(ex.Message, "other.json");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void Parses_a_room()
        {
            var r = TrialRoomLoader.Parse(RoomJson);
            Assert.AreEqual("proving-arena-1", r.Id);
            Assert.AreEqual((ushort)0x0150, r.Landblock);
            Assert.AreEqual(22020352u, r.Start.Cell);
            Assert.AreEqual(0f, r.BossPoint.QW);
            Assert.AreEqual(2, r.Points.Count);
            Assert.AreEqual(1f, r.Points[0].QW, "qw defaults to 1");
            Assert.AreEqual(100f, r.ExcludeRows.MaxX);
        }

        [TestMethod]
        public void Room_points_off_the_landblock_are_refused()
        {
            // cell 0x0151xxxx is landblock 0x0151, not 0x0150
            var bad = RoomJson.Replace(@"""cell"": 22020354", @"""cell"": 22085890");
            Assert.AreNotEqual(RoomJson, bad);
            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialRoomLoader.Parse(bad));
            StringAssert.Contains(ex.Message, "landblock");
        }

        [TestMethod]
        public void Room_exclusion_on_another_landblock_is_refused()
        {
            var bad = RoomJson.Replace(@"""excludeRows"": { ""landblock"": ""0x0150""", @"""excludeRows"": { ""landblock"": ""0x0151""");
            Assert.AreNotEqual(RoomJson, bad);
            var ex = Assert.ThrowsExactly<TrialArchetypeException>(() => TrialRoomLoader.Parse(bad));
            StringAssert.Contains(ex.Message, "excludeRows landblock");
        }

        [TestMethod]
        public void CheckFits_refuses_more_trash_than_points_and_a_boss_without_a_boss_point()
        {
            var room = TrialRoomLoader.Parse(RoomJson);
            var ok = TrialScenarioLoader.Parse(With(@"""count"": 6", @"""count"": 2"));
            Assert.IsNull(TrialRoomLoader.CheckFits(room, ok));

            var tooMany = TrialScenarioLoader.Parse(Example);
            var refusal = TrialRoomLoader.CheckFits(room, tooMany);
            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "Trash");

            var noBoss = TrialRoomLoader.Parse(RoomJson.Replace(@"""bossPoint"": { ""cell"": 22020353, ""x"": 30, ""y"": 40, ""z"": 0.5, ""qw"": 0, ""qz"": 1 },", ""));
            Assert.IsNull(noBoss.BossPoint);
            var bossRefusal = TrialRoomLoader.CheckFits(noBoss, ok);
            Assert.IsNotNull(bossRefusal);
            StringAssert.Contains(bossRefusal, "Boss");
        }
    }
}
