using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The fragment appraisal lines (PHASE-2-IMPLEMENTATION-PLAN.md task D4c, plus ruling P2-R20). Pressed:
    /// and the Meridian flavour line are fragment-only, Stability: is on both items, and a locked modifier is
    /// marked. The gem's two closing lines are pinned here as a control against phase 1's text.
    /// </summary>
    [TestClass]
    public class DungeonGemLongDescTests
    {
        private static ModifierDef Modifier(string id, string display, string kind, string target = "monster")
            => new ModifierDef { Id = id, Display = display, MonsterEffectKind = kind, Target = target };

        private static ModifierDef Lookup(string id)
        {
            switch (id)
            {
                case "savage": return new ModifierDef { Id = "savage", Display = "Savage", MonsterEffectKind = "damage_rating", Target = "monster", MinMagnitude = 10, MaxMagnitude = 40 };
                case "precise": return Modifier("precise", "Precise", "crit_rating");
                case "affinity": return new ModifierDef { Id = "affinity", Display = "Tourmaline Affinity", MonsterEffectKind = "salvage_affinity", Target = "monster", SalvageMaterial = 43, MinMagnitude = 5, MaxMagnitude = 25 };
                case "affinity_bad": return new ModifierDef { Id = "affinity_bad", Display = "Mystery Affinity", MonsterEffectKind = "salvage_affinity", Target = "monster", SalvageMaterial = 999999, MinMagnitude = 5, MaxMagnitude = 25 };

                // One row per category, for the "Possible effects:" counts. Values mirror the shipped
                // Content/dungeons/dynamic/modifiers.json rows they are named after: boss_guarded is
                // target "boss", and radiant is monsterEffectKind "none" with a non-neutral luminance pair.
                case "boss_guarded": return new ModifierDef { Id = "boss_guarded", Display = "Guarded", MonsterEffectKind = "health_mult", Target = "boss", MinMagnitude = 1.5, MaxMagnitude = 3.0 };
                case "radiant": return new ModifierDef { Id = "radiant", Display = "Radiant", MonsterEffectKind = "none", Target = "run", MinMagnitude = 1.25, MaxMagnitude = 2.0, RewardLumBase = 0.0, RewardLumSlope = 1.0 };

                // Three DISTINCT salvage affinities in one category, mirroring the shape the retired Powdered
                // Quartz had. Distinct ids on purpose: the dedup rule under test is the
                // per-component/per-category one, so it must not be able to pass by collapsing on id.
                case "quartz_a": return new ModifierDef { Id = "quartz_a", Display = "Red Quartz Affinity", MonsterEffectKind = "salvage_affinity", Target = "monster", SalvageMaterial = 43, MinMagnitude = 5, MaxMagnitude = 25 };
                case "quartz_b": return new ModifierDef { Id = "quartz_b", Display = "White Quartz Affinity", MonsterEffectKind = "salvage_affinity", Target = "monster", SalvageMaterial = 44, MinMagnitude = 5, MaxMagnitude = 25 };
                case "quartz_c": return new ModifierDef { Id = "quartz_c", Display = "Rose Quartz Affinity", MonsterEffectKind = "salvage_affinity", Target = "monster", SalvageMaterial = 45, MinMagnitude = 5, MaxMagnitude = 25 };

                // The same shape again in the MONSTER category, which the powder exception does not touch.
                // The counted-category form of the per-component dedup rule needs its own fixture now that
                // the only shipped multi-op component was a powder, and a powder is named rather than
                // counted - without these three, that rule would only ever be exercised on the named path.
                case "feral_a": return new ModifierDef { Id = "feral_a", Display = "Feral", MonsterEffectKind = "damage_rating", Target = "monster", MinMagnitude = 10, MaxMagnitude = 40 };
                case "feral_b": return new ModifierDef { Id = "feral_b", Display = "Rabid", MonsterEffectKind = "damage_rating", Target = "monster", MinMagnitude = 10, MaxMagnitude = 40 };
                case "feral_c": return new ModifierDef { Id = "feral_c", Display = "Frenzied", MonsterEffectKind = "damage_rating", Target = "monster", MinMagnitude = 10, MaxMagnitude = 40 };

                default: return null;
            }
        }

        private static string ComponentName(uint wcid)
        {
            switch (wcid)
            {
                case 1650u: return "Red Taper";
                case 1643u: return "Blue Taper";
                case 687u: return "Gold Scarab";
                default: return $"wcid {wcid}";
            }
        }

        /// <summary>
        /// Component defs for the "Possible effects:" tests. Only ops that can ADD a modifier (add_or_raise,
        /// set_max) should surface here - Lockbox's lock_one names "savage" but never adds it, so it must
        /// contribute nothing to the block.
        /// </summary>
        private static ComponentDef ComponentLookup(uint wcid)
        {
            switch (wcid)
            {
                case 5001u:
                    return new ComponentDef { Wcid = 5001, Name = "Ambush Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "savage", Weight = 1 } } };
                case 5002u:
                    return new ComponentDef { Wcid = 5002, Name = "Ambush Taper Two", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "savage", Weight = 1 } } };
                case 5003u:
                    return new ComponentDef { Wcid = 5003, Name = "Lockbox", Type = "talisman",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.LockOne, Modifier = "savage", Weight = 1 } } };
                case 5004u:
                    return new ComponentDef { Wcid = 5004, Name = "Nothing Charm", Type = "potion",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.Nothing, Weight = 1 } } };

                // One component per category, plus one naming a modifier the lookup cannot resolve.
                case 5005u:
                    return new ComponentDef { Wcid = 5005, Name = "Guarded Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "boss_guarded", Weight = 1 } } };
                case 5006u:
                    return new ComponentDef { Wcid = 5006, Name = "Radiant Herb", Type = "herb",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.SetMax, Modifier = "radiant", Weight = 1 } } };
                case 5007u:
                    return new ComponentDef { Wcid = 5007, Name = "Precise Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "precise", Weight = 1 } } };
                case 5008u:
                    return new ComponentDef { Wcid = 5008, Name = "Ghost Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "ghost_mod", Weight = 1 } } };

                // Press v2 op shapes. 5009 and 5014 are the multi-op shape - several ops, several DIFFERENT
                // modifiers of one category, mutually exclusive alternatives for the single op the slot
                // draws - once on the NAMED affinity path and once on the COUNTED category path, because the
                // powder exception splits them. 5010 is the control: ops that genuinely straddle two
                // categories must still contribute one entry to EACH. 5011/5012 are add_random, which names
                // no modifier at all. 5015 is the mapped powder and 5016 the wildcard.
                case 5009u:
                    return new ComponentDef { Wcid = 5009, Name = "Powdered Quartz", Type = "powder",
                        Ops = new List<OpDef>
                        {
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "quartz_a", Weight = 1 },
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "quartz_b", Weight = 1 },
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "quartz_c", Weight = 1 },
                        } };
                case 5010u:
                    return new ComponentDef { Wcid = 5010, Name = "Straddling Taper", Type = "taper",
                        Ops = new List<OpDef>
                        {
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "savage", Weight = 1 },
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "boss_guarded", Weight = 1 },
                        } };
                case 5011u:
                    return new ComponentDef { Wcid = 5011, Name = "Turquoise Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddRandom, Scope = AttunementScopes.Difficulty, Weight = 1 } } };
                case 5012u:
                    return new ComponentDef { Wcid = 5012, Name = "Bosswards Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddRandom, Scope = AttunementScopes.Boss, Weight = 1 } } };
                case 5013u:
                    return new ComponentDef { Wcid = 5013, Name = "Second Turquoise Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddRandom, Scope = AttunementScopes.Difficulty, Weight = 1 } } };
                case 5014u:
                    return new ComponentDef { Wcid = 5014, Name = "Triple Taper", Type = "taper",
                        Ops = new List<OpDef>
                        {
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "feral_a", Weight = 1 },
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "feral_b", Weight = 1 },
                            new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "feral_c", Weight = 1 },
                        } };
                case 5015u:
                    return new ComponentDef { Wcid = 5015, Name = "Powdered Onyx", Type = "powder",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "affinity", Weight = 1 } } };
                case 5016u:
                    return new ComponentDef { Wcid = 5016, Name = "Powdered Agate", Type = "powder",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddRandom, Scope = AttunementScopes.Salvage, Weight = 1 } } };

                // ILLEGAL CONTENT ON PURPOSE. Lint rule 23 refuses a non-powder that reaches a salvage
                // affinity, so the shipped loader can no longer produce these - which is exactly why they are
                // built here by hand. They exist to prove the RENDERER does not depend on that rule: if it is
                // ever deliberately relaxed, the panel must start printing a second affinity rather than
                // silently dropping it (review, 2026-09-07).
                case 5017u:
                    return new ComponentDef { Wcid = 5017, Name = "Iron Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddOrRaise, Modifier = "quartz_b", Weight = 1 } } };
                case 5018u:
                    return new ComponentDef { Wcid = 5018, Name = "Wildcard Taper", Type = "taper",
                        Ops = new List<OpDef> { new OpDef { Op = AttunementOps.AddRandom, Scope = AttunementScopes.Salvage, Weight = 1 } } };

                default:
                    return null;
            }
        }

        /// <summary>A fragment: seed 0, no run binding.</summary>
        private static DungeonGemSpec Fragment(int level = 185, int tier = 7,
            (string Id, double Magnitude)[] mods = null,
            string[] locks = null, (uint Wcid, int Doses)[] load = null)
            => new DungeonGemSpec("any", level, tier, "any", 0, mods ?? new (string, double)[0], 0, 0, 0, locks, load);

        /// <summary>A finished gem: non-zero seed, empty load.</summary>
        private static DungeonGemSpec Gem((string Id, double Magnitude)[] mods = null)
            => new DungeonGemSpec("filos_doom", 100, 6, "any", 4242, mods ?? new (string, double)[0], 0, 0, 1, null, null);

        /// <summary>
        /// Seeds PropertyManager's cache directly (ModifyBool/ModifyDouble, no DB round trip - see
        /// PropertyManager.ModifyBool/ModifyDouble) with the shipped compiled defaults for every tunable the
        /// 4-arg ComposeLongDesc store-bound overload reads. Needed by any test in this file that calls that
        /// overload rather than the pure core: a raw PropertyManager read throws under this harness (no live
        /// ShardConfig DB) until the key is cached, and this makes the seeded state explicit and pinned to a
        /// known value instead of accidental leftover state from another test's earlier run.
        /// </summary>
        private static void SeedRewardScaleProperties()
        {
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_xp_mult_cap", DungeonPopulationLimits.DefaultXpCap);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_lum_mult_cap", DungeonPopulationLimits.DefaultLumCap);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_loot_quantity_cap", DungeonPopulationLimits.DefaultLootQuantityCap);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_xp_scale", DungeonPopulationLimits.DefaultXpScale);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_lum_scale", DungeonPopulationLimits.DefaultLumScale);
            var modifyBoolOk = ACE.Server.Managers.PropertyManager.ModifyBool("dynamic_dungeons_reward_scaling", DungeonPopulationLimits.DefaultRewardScalingEnabled);
            if (!modifyBoolOk) throw new Exception("ModifyBool(dynamic_dungeons_reward_scaling) returned false - key not registered in DefaultBooleanProperties");
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_anchor", DungeonPopulationLimits.DefaultRewardScaleAnchor);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_exponent", DungeonPopulationLimits.DefaultRewardScaleExponent);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_floor", DungeonPopulationLimits.DefaultRewardScaleFloor);
            ACE.Server.Managers.PropertyManager.ModifyDouble("dynamic_dungeons_reward_scale_cap", DungeonPopulationLimits.DefaultRewardScaleCap);
        }

        /// <summary>
        /// The LongDesc string the shipped level-185 rung actually carries, read off disk by walking up from
        /// the test assembly the same way ThreadDungeonStoreTests.FindDynamicDir does, with the SQL file's
        /// \n escapes turned back into real newlines.
        /// </summary>
        private static string ShippedRungLongDesc()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;

            while (dir != null && path == null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies", "1003615 Raw Fragment Level 185.sql");
                if (File.Exists(candidate)) path = candidate;
                dir = dir.Parent;
            }

            if (path == null)
                Assert.Inconclusive("Could not locate the 1003615 rung SQL by walking up from the test assembly -- skipping.");

            var match = Regex.Match(File.ReadAllText(path), @"\(1003615,\s*16,\s*'(?<desc>[^']*)'\)");
            Assert.IsTrue(match.Success, $"No LongDesc (type 16) row found in {path}");

            return match.Groups["desc"].Value.Replace("\\n", "\n");
        }

        /// <summary>
        /// Same lookup as <see cref="ShippedRungLongDesc"/>, generalized to an arbitrary rung wcid/file so the
        /// 300/325/350/375 ceiling-raise rungs (gem-ceiling-375) can be pinned against their own shipped SQL
        /// the same way the level-185 rung is.
        /// </summary>
        private static string ShippedRungLongDesc(int wcid, string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;

            while (dir != null && path == null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies", fileName);
                if (File.Exists(candidate)) path = candidate;
                dir = dir.Parent;
            }

            if (path == null)
                Assert.Inconclusive($"Could not locate the {wcid} rung SQL by walking up from the test assembly -- skipping.");

            var match = Regex.Match(File.ReadAllText(path), $@"\({wcid},\s*16,\s*'(?<desc>[^']*)'\)");
            Assert.IsTrue(match.Success, $"No LongDesc (type 16) row found in {path}");

            return match.Groups["desc"].Value.Replace("\\n", "\n");
        }

        /// <summary>
        /// Ruling P2-R20: the composer's output for a rung's shipped spec is now the rung SQL's LongDesc
        /// EXACTLY, flavour line included. Asserted against the file itself rather than a literal, so the
        /// composer and the ten shipped rung weenies cannot drift apart unnoticed.
        ///
        /// Carries the Experience/Luminance lines at the shipped 2.0 default scale, GEM-LEVEL SCALED (owner
        /// requirement, 2026-09-07): this fragment is level 185, so the total is 1 + ratio(185) * (2.0 - 1) =
        /// x1.25, not the flat x2.00 every rung showed before that feature. Even though this fragment's spec
        /// has mods= empty: with the total (not the bare product) driving the differs-from-1.00 rule (owner
        /// follow-up, 2026-09-06, see the reward block on ComposeLongDesc), an empty modifier set still totals
        /// something other than 1.00 on both axes, and the ten shipped rung rows were regenerated from this
        /// exact composer output to keep P2-R20 intact - each rung now carries a DIFFERENT total from its
        /// neighbours (level 185 = x1.25, level 275 = x1.78), where before this feature all ten read the same
        /// flat x2.00.
        ///
        /// There is no "Stability" line any more. Removing the instability mechanic (owner ruling,
        /// 2026-09-07) dropped it from the composer, which left this assertion RED for the length of the
        /// press v2 branch: the rows were deliberately not regenerated at that point, because the
        /// component-model redesign on the same branch was going to change the LongDesc again and
        /// regenerating twice is wasted work. Leaving it red rather than weakening it was the point - a
        /// temporary loosening is exactly the kind that ships by accident. The ten rung rows were
        /// regenerated from this composer as the last step of that redesign and it is green again.
        /// </summary>
        [TestMethod]
        public void Fragment_shows_Pressed_nothing_and_matches_the_shipped_rung_row()
        {
            // The 4-arg ComposeLongDesc overload reads PropertyManager directly, and a raw PropertyManager
            // read throws under the unit-test harness (no live ShardConfig DB) UNLESS the key is already
            // cached - ModifyBool/ModifyDouble write straight to the cache with no DB round trip, which is
            // the established way other test files seed PropertyManager (see e.g. MarketConfigTests.cs). Set
            // explicitly to the shipped compiled defaults so this test's expectations are pinned to a KNOWN
            // state rather than to whatever an earlier test in the run happened to leave cached.
            // Deliberately the TRUE 4-arg overload (no lookup/componentName arguments) - passing Lookup and
            // ComponentName here would resolve to the 6-arg prefix of the pure-core overload instead, which
            // defaults rewardScaleRatio to 1.0 and silently skips PropertyManager entirely, defeating the
            // point of this test. Safe against ThreadDungeonManager.Store being Empty in this harness because
            // Fragment() has no Modifiers and no Load, so LookupModifier/LookupComponentName are never invoked.
            SeedRewardScaleProperties();

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(), "any dungeon", 3, 3);

            var expected = string.Join("\n", new[]
            {
                "Meridian issue. A fragment of a door with no wall behind it.",
                "Dungeon: Any",
                "Level: 185",
                "Loot Tier: 7",
                "Modifiers: none",
                "Pressed: nothing",
                "Experience: x1.25",
                "Luminance: x1.25",
                "Entries: 3 of 3",
                "Unpressed. Load spell components onto it, then press it at the Fragment Press.",
            });

            Assert.AreEqual(expected, desc);
            Assert.AreEqual(ShippedRungLongDesc(), desc);
        }

        /// <summary>
        /// gem-ceiling-375: the level-300 rung (wcid 1003626), tier 8 (DungeonGemSpec.MaxTier - the ceiling
        /// raise adds no new tier, only new levels above it). Level 300 is the reward-scale anchor
        /// (DungeonPopulationLimits.DefaultRewardScaleAnchor), so its total is the flat x2.00 the composer
        /// falls back to at the anchor.
        /// </summary>
        [TestMethod]
        public void Fragment_shows_Pressed_nothing_and_matches_the_shipped_300_rung_row()
        {
            SeedRewardScaleProperties();

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(level: 300, tier: 8), "any dungeon", 3, 3);

            var expected = string.Join("\n", new[]
            {
                "Meridian issue. A fragment of a door with no wall behind it.",
                "Dungeon: Any",
                "Level: 300",
                "Loot Tier: 8",
                "Modifiers: none",
                "Pressed: nothing",
                "Experience: x2.00",
                "Luminance: x2.00",
                "Entries: 3 of 3",
                "Unpressed. Load spell components onto it, then press it at the Fragment Press.",
            });

            Assert.AreEqual(expected, desc);
            Assert.AreEqual(ShippedRungLongDesc(1003626, "1003626 Raw Fragment Level 300.sql"), desc);
        }

        /// <summary>gem-ceiling-375: the level-325 rung (wcid 1003627), tier 8.</summary>
        [TestMethod]
        public void Fragment_shows_Pressed_nothing_and_matches_the_shipped_325_rung_row()
        {
            SeedRewardScaleProperties();

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(level: 325, tier: 8), "any dungeon", 3, 3);

            var expected = string.Join("\n", new[]
            {
                "Meridian issue. A fragment of a door with no wall behind it.",
                "Dungeon: Any",
                "Level: 325",
                "Loot Tier: 8",
                "Modifiers: none",
                "Pressed: nothing",
                "Experience: x2.26",
                "Luminance: x2.26",
                "Entries: 3 of 3",
                "Unpressed. Load spell components onto it, then press it at the Fragment Press.",
            });

            Assert.AreEqual(expected, desc);
            Assert.AreEqual(ShippedRungLongDesc(1003627, "1003627 Raw Fragment Level 325.sql"), desc);
        }

        /// <summary>gem-ceiling-375: the level-350 rung (wcid 1003628), tier 8.</summary>
        [TestMethod]
        public void Fragment_shows_Pressed_nothing_and_matches_the_shipped_350_rung_row()
        {
            SeedRewardScaleProperties();

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(level: 350, tier: 8), "any dungeon", 3, 3);

            var expected = string.Join("\n", new[]
            {
                "Meridian issue. A fragment of a door with no wall behind it.",
                "Dungeon: Any",
                "Level: 350",
                "Loot Tier: 8",
                "Modifiers: none",
                "Pressed: nothing",
                "Experience: x2.56",
                "Luminance: x2.56",
                "Entries: 3 of 3",
                "Unpressed. Load spell components onto it, then press it at the Fragment Press.",
            });

            Assert.AreEqual(expected, desc);
            Assert.AreEqual(ShippedRungLongDesc(1003628, "1003628 Raw Fragment Level 350.sql"), desc);
        }

        /// <summary>gem-ceiling-375: the level-375 rung (wcid 1003629), tier 8 - the new ceiling.</summary>
        [TestMethod]
        public void Fragment_shows_Pressed_nothing_and_matches_the_shipped_375_rung_row()
        {
            SeedRewardScaleProperties();

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(level: 375, tier: 8), "any dungeon", 3, 3);

            var expected = string.Join("\n", new[]
            {
                "Meridian issue. A fragment of a door with no wall behind it.",
                "Dungeon: Any",
                "Level: 375",
                "Loot Tier: 8",
                "Modifiers: none",
                "Pressed: nothing",
                "Experience: x2.90",
                "Luminance: x2.90",
                "Entries: 3 of 3",
                "Unpressed. Load spell components onto it, then press it at the Fragment Press.",
            });

            Assert.AreEqual(expected, desc);
            Assert.AreEqual(ShippedRungLongDesc(1003629, "1003629 Raw Fragment Level 375.sql"), desc);
        }

        /// <summary>
        /// The master switch (dynamic_dungeons_reward_scaling) is the only live rollback if the 2.87 exponent
        /// turns out wrong on a shard, so it must be proven through the SAME live PropertyManager read the
        /// composer actually uses in production - ThreadDungeonSpawner.ResolveRewardScaleRatio's GetBool
        /// branch - not just through a DungeonPopulationLimits constructed directly in code (that path is
        /// covered by DungeonPopulationBuilderTests.Master_switch_off_reproduces_the_raw_scale_regardless_of_level,
        /// which never reaches this GetBool branch at all). SeedRewardScaleProperties leaves the switch at its
        /// default true, so this test overrides it to false with its own ModifyBool call and restores it in a
        /// finally block - PropertyManager's cache is static and shared across every test in the assembly, so
        /// leaving it false would leak into whichever sibling test runs next.
        /// </summary>
        [TestMethod]
        public void Master_switch_off_reports_the_flat_pre_feature_total_through_the_live_composer()
        {
            SeedRewardScaleProperties();
            var toggledOff = ACE.Server.Managers.PropertyManager.ModifyBool("dynamic_dungeons_reward_scaling", false);
            Assert.IsTrue(toggledOff, "ModifyBool(dynamic_dungeons_reward_scaling, false) returned false - key not registered in DefaultBooleanProperties");

            try
            {
                // Level 185 is deliberately non-anchor: with the switch on this level's total is x1.25 (see
                // Fragment_shows_Pressed_nothing_and_matches_the_shipped_rung_row above), so a flat x2.00 here
                // proves the switch, not a coincidental match at the anchor level (300).
                var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(level: 185), "any dungeon", 3, 3);

                StringAssert.Contains(desc, "Experience: x2.00");
                StringAssert.Contains(desc, "Luminance: x2.00");
                Assert.IsFalse(desc.Contains("x1.25"), "the switch is off - no level-scaled total should appear");
            }
            finally
            {
                ACE.Server.Managers.PropertyManager.ModifyBool("dynamic_dungeons_reward_scaling", DungeonPopulationLimits.DefaultRewardScalingEnabled);
            }
        }

        /// <summary>
        /// The flavour line is composed, not content-only, so it survives a load and a re-open rather than
        /// vanishing the first time the fragment's description is rewritten.
        /// </summary>
        [TestMethod]
        public void Fragment_keeps_the_Meridian_prefix()
        {
            var loaded = Fragment(mods: new (string Id, double Magnitude)[] { ("savage", 10) }, load: new[] { (1650u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(loaded, "any dungeon", 3, 3, Lookup, ComponentName);

            StringAssert.StartsWith(desc, ThreadDungeonGemHandler.FragmentFlavourLine + "\n");

            // And a gem never carries it, however freshly pressed it is.
            var gem = ThreadDungeonGemHandler.ComposeLongDesc(Gem(), "Filo's Doom", 2, 3, Lookup, ComponentName);
            Assert.IsFalse(gem.Contains(ThreadDungeonGemHandler.FragmentFlavourLine), gem);
        }

        [TestMethod]
        public void Fragment_shows_loaded_components_with_counts()
        {
            var spec = Fragment(load: new[] { (1650u, 2), (1643u, 1), (687u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName);

            StringAssert.Contains(desc, "\nPressed: Red Taper x2, Blue Taper, Gold Scarab\n");
        }

        [TestMethod]
        public void Gem_omits_the_Pressed_line()
        {
            var desc = ThreadDungeonGemHandler.ComposeLongDesc(Gem(), "Filo's Doom", 2, 3, Lookup, ComponentName);

            Assert.IsFalse(desc.Contains("Pressed:"), desc);
        }

        /// <summary>
        /// REWRITTEN from Gem_shows_the_Stability_line, which asserted the panel carried
        /// "Stability: fracturing" for an unstable gem. The instability mechanic was removed (owner ruling,
        /// 2026-09-07), so the line's subject no longer exists; the test is inverted rather than deleted, to
        /// pin that the retired line does not creep back onto either item.
        /// </summary>
        [TestMethod]
        public void Neither_item_carries_a_Stability_line_any_more()
        {
            var gem = ThreadDungeonGemHandler.ComposeLongDesc(Gem(), "Filo's Doom", 2, 3, Lookup, ComponentName);
            Assert.IsFalse(gem.Contains("Stability:"), gem);

            var fragment = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(), "any dungeon", 3, 3, Lookup, ComponentName);
            Assert.IsFalse(fragment.Contains("Stability:"), fragment);
        }

        [TestMethod]
        public void A_locked_modifier_is_marked()
        {
            var mods = new (string Id, double Magnitude)[] { ("savage", 17.5), ("precise", 10) };
            var spec = Fragment(mods: mods, locks: new[] { "savage" });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName);

            StringAssert.Contains(desc, "\nSavage: monsters attack with +18 damage rating, hitting harder (locked)\n");
            StringAssert.Contains(desc, "\nPrecise: monsters have +10 critical strike rating, landing more critical hits\n");
        }

        // ---- salvage_affinity wording (Task 2) -----------------------------------------------------------

        /// <summary>
        /// A salvage_affinity modifier renders as a percent chance per kill THAT NAMES THE MATERIAL:
        /// "17% of kills leave tourmaline to salvage".
        ///
        /// THIS REVERSES AN OWNER CORRECTION OF 2026-09-07, which this test previously recorded: that
        /// correction argued the Display already names the material ("Tourmaline Affinity") so the value text
        /// need not repeat it, and settled on "17% per kill". The later detail ruling (also 2026-09-07)
        /// reversed it - "23% chance of WHAT per kill" - because the shortened form is a percentage attached
        /// to no stated outcome, and the reader has to carry the material across from the line's own label and
        /// still guess what happens to it. The material now comes from SalvageMaterial rather than from the
        /// Display, so the two cannot disagree.
        /// </summary>
        [TestMethod]
        public void Salvage_affinity_names_the_material_it_leaves()
        {
            var spec = Fragment(mods: new (string Id, double Magnitude)[] { ("affinity", 17) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName);

            StringAssert.Contains(desc, "\nTourmaline Affinity: 17% of kills leave tourmaline to salvage\n");
        }

        /// <summary>
        /// An invalid/unmapped SalvageMaterial id must not throw and must not leak the raw id into the text.
        /// It degrades to the UNNAMED form - "25% of kills leave extra salvage" - rather than to a bare
        /// percentage: the sentence still states an outcome, it just cannot say which material.
        ///
        /// Nothing shipped can reach this branch (ThreadDungeonStore's lint drops a row whose salvageMaterial
        /// is not a defined MaterialType, and ThreadDungeonStoreTests asserts every shipped affinity passes
        /// that check), so this is about hand-built defs and about a future caller, which is exactly why the
        /// degrade path has to exist rather than being assumed unreachable.
        /// </summary>
        [TestMethod]
        public void Salvage_affinity_with_an_invalid_material_id_degrades_without_leaking_the_id()
        {
            var spec = Fragment(mods: new (string Id, double Magnitude)[] { ("affinity_bad", 25) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName);

            StringAssert.Contains(desc, "\nMystery Affinity: 25% of kills leave extra salvage\n");
            Assert.IsFalse(desc.Contains("999999"), desc);
        }

        // ---- "Possible effects:" projection (fragment only) ----------------------------------------------

        /// <summary>
        /// REWRITTEN for the vague-feedback ruling (owner, 2026-09-07), which reversed PR #969: the block now
        /// carries a COUNT PER CATEGORY rather than a named modifier and its range. The dedup rule it was
        /// originally written to pin is unchanged and is still what this test proves - two components both
        /// reaching "savage" contribute ONE monster modifier to the count, not two.
        /// </summary>
        [TestMethod]
        public void Fragment_possible_effects_counts_a_modifier_shared_by_two_components_once()
        {
            var spec = Fragment(load: new[] { (5001u, 1), (5002u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\n1 monster modifier\n");

            // Singular noun for a count of one, and the count really is one line, not two.
            Assert.AreEqual(1, Regex.Matches(desc, "monster modifier").Count, desc);
        }

        /// <summary>
        /// THE RULING'S CORE ASSERTION: the pre-press block must never leak a modifier's identity or its
        /// magnitude. Asserted negatively against the display name AND the range text that PR #969 used to
        /// print, so a regression that reinstates either is caught here rather than in review.
        /// </summary>
        [TestMethod]
        public void Possible_effects_names_no_modifier_and_no_magnitude()
        {
            var spec = Fragment(load: new[] { (5001u, 1), (5005u, 1), (5006u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            var block = desc.Substring(desc.IndexOf("Possible effects:", StringComparison.Ordinal));

            foreach (var leak in new[] { "Savage", "Guarded", "Radiant", "damage rating", "health", "luminance", "+10", "+40", "x1.50", "x3.00" })
                Assert.IsFalse(block.Contains(leak), $"the vague block must not contain '{leak}'; got:\n{block}");
        }

        /// <summary>
        /// All three categories at once, each with its own count, in the order their first reachable modifier
        /// appears in LOAD order - monster (savage, from 5001), boss (boss_guarded, from 5005), bonus
        /// (radiant, from 5006) - and a second monster modifier (precise, from 5007) loaded LAST, to prove the
        /// count grows without the category moving to the end of the list.
        /// </summary>
        [TestMethod]
        public void Possible_effects_counts_each_category_in_first_appearance_order()
        {
            var spec = Fragment(load: new[] { (5001u, 1), (5005u, 1), (5006u, 1), (5007u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\n2 monster modifiers\n1 boss modifier\n1 bonus modifier\n");
        }

        /// <summary>
        /// A modifier id the lookup cannot resolve is still COUNTED, as "monster" - dropping it would
        /// undercount something a press really could add. Pins DungeonModifierCategories.Of's null contract
        /// through the block that depends on it.
        /// </summary>
        [TestMethod]
        public void An_unresolvable_modifier_is_counted_as_monster()
        {
            var spec = Fragment(load: new[] { (5008u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\n1 monster modifier\n");
            Assert.IsFalse(desc.Contains("ghost_mod"), desc);
        }

        [TestMethod]
        public void Fragment_with_an_empty_load_emits_no_possible_effects_header()
        {
            var spec = Fragment(load: null);

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            Assert.IsFalse(desc.Contains("Possible effects:"), desc);
        }

        [TestMethod]
        public void Fragment_whose_loaded_components_only_have_non_adding_ops_emits_no_possible_effects_header()
        {
            // Lockbox names "savage" via lock_one, which never ADDS a modifier; Nothing Charm names none at all.
            var spec = Fragment(load: new[] { (5003u, 1), (5004u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            Assert.IsFalse(desc.Contains("Possible effects:"), desc);
        }

        /// <summary>A pressed gem (non-zero seed) never emits this block, even when it happens to be handed a
        /// componentLookup - the block is gated on isFragment, not merely on componentLookup being non-null.</summary>
        [TestMethod]
        public void Pressed_gem_never_emits_the_possible_effects_block()
        {
            var gem = Gem();

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(gem, "Filo's Doom", 2, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            Assert.IsFalse(desc.Contains("Possible effects:"), desc);
        }

        /// <summary>
        /// REWRITTEN from Possible_effects_line_is_literally_RenderEffectRanges_own_output. The original
        /// pinned that this block and the chat load line shared one formatter (a magnitude-range renderer) so
        /// they could not disagree. The vague ruling replaced that shared vocabulary with the category word, so
        /// the SAME property is now pinned against the shared classifier instead: the word this block prints is
        /// literally DungeonModifierCategories.Word's output, not a string that merely looks like it today.
        ///
        /// That range formatter had no caller left afterwards and was deleted; nothing in this file covers it,
        /// and no test should be written to keep it alive. The finished gem's "Modifiers:" list is composed by
        /// RenderEffect, a separate single-magnitude method, and is pinned by the pressed-gem tests below.
        /// </summary>
        [TestMethod]
        public void Possible_effects_word_is_literally_the_shared_classifiers_own_output()
        {
            var expectedWord = DungeonModifierCategories.Word(Lookup("savage"));

            var spec = Fragment(load: new[] { (5001u, 1) });
            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, $"\nPossible effects:\n1 {expectedWord} modifier\n");
        }

        /// <summary>
        /// REGRESSION (review finding 1, 2026-09-07). A component whose several ops all land in ONE category
        /// contributes ONE entry, not one per op: the ops are mutually exclusive alternatives for the single
        /// op the slot draws, so "3 monster modifiers" would be an over-promise by a factor of three. The
        /// three modifier ids are distinct, so the older across-components id dedup cannot make this pass on
        /// its own.
        ///
        /// FIXTURE CHANGED with the powder exception (owner ruling, 2026-09-07). It used to run on the
        /// shipped Powdered Quartz's shape, but an affinity is now NAMED rather than counted, so that shape
        /// no longer exercises the counting path at all. The rule is identical and now has a fixture on each
        /// side: this one on the counted path, and A_powder_with_several_affinity_ops_names_one_entry on the
        /// named path.
        /// </summary>
        [TestMethod]
        public void A_component_with_several_same_category_ops_counts_once()
        {
            var spec = Fragment(load: new[] { (5014u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\n1 monster modifier\n");
            Assert.IsFalse(desc.Contains("3 monster modifiers"), desc);
        }

        /// <summary>
        /// The same dedup rule on the NAMED path: a powder whose several ops reach several different
        /// affinities still contributes ONE entry, and the first one wins so the component's own op order
        /// decides which. Without the rule this would print three affinity names for a slot that draws one.
        /// </summary>
        [TestMethod]
        public void A_powder_with_several_affinity_ops_names_one_entry()
        {
            var spec = Fragment(load: new[] { (5009u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\nRed Quartz Affinity\n");
            Assert.IsFalse(desc.Contains("White Quartz Affinity"), desc);
            Assert.IsFalse(desc.Contains("Rose Quartz Affinity"), desc);
        }

        // ---- the powder exception (owner ruling, 2026-09-07) ----------------------------------------------

        /// <summary>
        /// A MAPPED powder names its material, verbatim from the modifier's own Display and with no article
        /// in front of it. This is the whole exception, on the panel surface.
        /// </summary>
        [TestMethod]
        public void A_mapped_powder_names_its_material()
        {
            var spec = Fragment(load: new[] { (5015u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\nTourmaline Affinity\n");

            // Literally the modifier's own Display, not a string that merely looks like it today.
            StringAssert.Contains(desc, "\n" + Lookup("affinity").Display + "\n");
        }

        /// <summary>
        /// A WILDCARD powder says it is random and names no material - it has not drawn one. It is still an
        /// affinity ENTRY rather than a bonus count, so the two powder shapes read as the same slot.
        /// </summary>
        [TestMethod]
        public void A_wildcard_powder_says_random_and_names_no_material()
        {
            var spec = Fragment(load: new[] { (5016u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\nA random salvage affinity\n");

            foreach (var material in new[] { "Tourmaline", "Obsidian", "Quartz", "Mystery" })
                Assert.IsFalse(desc.Contains(material), $"a wildcard powder must name no material; found '{material}' in:\n{desc}");

            Assert.IsFalse(desc.Contains("bonus modifier"), desc);
        }

        /// <summary>
        /// THE DOUBLE-COUNT GUARD. An affinity is named INSTEAD OF being counted, never as well - a powder
        /// that produced both "Tourmaline Affinity" and "1 bonus modifier" would advertise one slot twice.
        /// A salvage affinity classifies as BONUS through DungeonModifierCategories, so this is the exact
        /// mistake the naive implementation makes.
        /// </summary>
        [TestMethod]
        public void A_named_affinity_is_not_also_counted_as_a_bonus()
        {
            var spec = Fragment(load: new[] { (5015u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            // Guard: the classifier really does call this a bonus, so the absence below is a deliberate
            // exclusion rather than a modifier that was never a bonus in the first place.
            Assert.AreEqual(DungeonModifierCategory.Bonus, DungeonModifierCategories.Of(Lookup("affinity")));

            Assert.IsFalse(desc.Contains("bonus modifier"), desc);
            StringAssert.Contains(desc, "\nPossible effects:\nTourmaline Affinity\n");
        }

        /// <summary>
        /// The whole block, as the owner specified it: counted categories in first-appearance order, then the
        /// named affinity, which here is loaded last. Pins the interleaving as well as the wording.
        /// </summary>
        [TestMethod]
        public void The_affinity_entry_sits_in_load_order_among_the_counts()
        {
            var spec = Fragment(load: new[] { (5001u, 1), (5007u, 1), (5006u, 1), (5015u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\n2 monster modifiers\n1 bonus modifier\nTourmaline Affinity\n");
        }

        /// <summary>
        /// The affinity entry is NOT pinned to the end - it takes its place in load order like any other
        /// entry. The powder is loaded FIRST here, and the same four components produce the same lines in a
        /// different order. Without this, "affinity last" could be hard-coded and the suite would agree.
        /// </summary>
        [TestMethod]
        public void The_affinity_entry_leads_when_the_powder_is_loaded_first()
        {
            var spec = Fragment(load: new[] { (5015u, 1), (5001u, 1), (5007u, 1), (5006u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\nTourmaline Affinity\n2 monster modifiers\n1 bonus modifier\n");
        }

        /// <summary>
        /// REGRESSION (review, 2026-09-07). TWO affinity entries both render; neither is dropped silently.
        ///
        /// The block used to hold a single field and keep the first entry, justified by CanLoad refusing a
        /// second powder. That refusal is per component TYPE, while the naming exception keys on the
        /// modifier's effect KIND - different conditions - so a taper naming an affinity would have loaded
        /// beside a powder and simply vanished from the panel, with no diagnostic and no failing test,
        /// because the only pinning test was powder against powder.
        ///
        /// Lint rule 23 now makes this content illegal, which is why BOTH components here are hand-built
        /// fixtures that the real loader would refuse. That is the point: the two defences are independent,
        /// and this one has to keep working if the rule is ever relaxed. Covers both affinity shapes, since a
        /// named entry and a wildcard entry take different paths into the list.
        /// </summary>
        [TestMethod]
        public void Two_affinity_entries_both_render()
        {
            var named = Fragment(load: new[] { (5015u, 1), (5017u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(named, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\nTourmaline Affinity\nWhite Quartz Affinity\n");

            // A named entry and a wildcard entry together, in load order.
            var mixed = Fragment(load: new[] { (5018u, 1), (5015u, 1) });

            var mixedDesc = ThreadDungeonGemHandler.ComposeLongDesc(mixed, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(mixedDesc, "\nPossible effects:\nA random salvage affinity\nTourmaline Affinity\n");
        }

        /// <summary>
        /// Two affinity entries keep their places among the COUNTED categories rather than clumping. The
        /// list-based renderer walks two sequences at once, so the interleaving is the part most likely to
        /// go wrong in a way a single-entry test cannot see.
        /// </summary>
        [TestMethod]
        public void Two_affinity_entries_interleave_with_the_counts()
        {
            var spec = Fragment(load: new[] { (5015u, 1), (5001u, 1), (5017u, 1), (5006u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc,
                "\nPossible effects:\nTourmaline Affinity\n1 monster modifier\nWhite Quartz Affinity\n1 bonus modifier\n");
        }

        /// <summary>
        /// THE DISCRIMINATING NEGATIVE: the exception did not leak. A taper and a herb loaded ALONGSIDE a
        /// powder still name no modifier - asserted against their Display strings, not merely against a
        /// category word being present, because a block that named everything would still contain the
        /// category words.
        ///
        /// The powder's own material is asserted PRESENT in the same block, so this cannot pass by the
        /// composer naming nothing at all, which is the way a negative-only test quietly stops testing.
        /// </summary>
        [TestMethod]
        public void The_naming_exception_does_not_leak_to_other_types()
        {
            var spec = Fragment(load: new[] { (5001u, 1), (5005u, 1), (5006u, 1), (5015u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            // Scoped to the "Possible effects:" listing itself, stopping before the Experience/Luminance
            // reward-total lines that follow it in the layout (see ComposeLongDesc's line order) - those
            // legitimately carry a scale-derived number (e.g. the shipped "x2.00" total) that has nothing to
            // do with whether THIS block leaks a modifier's magnitude, and an unbounded substring would flag
            // it as a false positive every time the reward scale happens to collide with one of the numbers
            // below.
            var afterPossibleEffects = desc.Substring(desc.IndexOf("Possible effects:", StringComparison.Ordinal));
            var rewardLinesIndex = afterPossibleEffects.IndexOf("\nExperience:", StringComparison.Ordinal);
            var block = rewardLinesIndex >= 0 ? afterPossibleEffects.Substring(0, rewardLinesIndex) : afterPossibleEffects;

            // The affinity IS named - the control that keeps the negatives below meaningful.
            StringAssert.Contains(block, "Tourmaline Affinity");

            foreach (var id in new[] { "savage", "boss_guarded", "radiant" })
            {
                var display = Lookup(id).Display;
                Assert.IsFalse(block.Contains(display),
                    $"'{display}' is a {id} modifier and must stay vague; got:\n{block}");
            }

            // And no magnitude for anything, the affinity included - the exception is about the name alone.
            foreach (var number in new[] { "10", "40", "1.50", "3.00", "1.25", "2.00", "5", "25" })
                Assert.IsFalse(block.Contains(number), $"no magnitude may appear; found '{number}' in:\n{block}");
        }

        /// <summary>
        /// The CONTROL for the test above: the per-component rule is per CATEGORY, not per component. A
        /// component whose two ops reach different categories still contributes one count to each, because
        /// those are different budgets. Without this, "dedup within a component" could be implemented as
        /// "one count per component" and the suite would not notice.
        /// </summary>
        [TestMethod]
        public void A_component_whose_ops_straddle_categories_counts_in_each()
        {
            var spec = Fragment(load: new[] { (5010u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\n1 monster modifier\n1 boss modifier\n");
        }

        /// <summary>
        /// REGRESSION (review finding 2, 2026-09-07). add_random adds a modifier without naming one, and was
        /// simply absent from the op filter when press v2 introduced it - so a fragment loaded with only a
        /// Turquoise Taper printed NO block at all, while adding a modifier is exactly what that component
        /// does. Its category comes from the op's SCOPE. "difficulty" spans monster and boss and reports
        /// monster (see ThreadDungeonGemHandler.ScopeCategory for why the majority case wins); scope "boss"
        /// narrows, and is asserted here so the scope is proven to be read rather than ignored.
        /// </summary>
        [TestMethod]
        public void An_add_random_component_is_counted_under_its_scopes_category()
        {
            var difficulty = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(load: new[] { (5011u, 1) }),
                "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(difficulty, "\nPossible effects:\n1 monster modifier\n");

            var boss = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(load: new[] { (5012u, 1) }),
                "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(boss, "\nPossible effects:\n1 boss modifier\n");
        }

        /// <summary>
        /// add_random carries no modifier id, so it has no key for the across-components dedup rule. Two
        /// SEPARATE components that each draw at random therefore contribute two counts - correct, because a
        /// press really can add two different modifiers that way. The per-component rule must not be allowed
        /// to reach across components and collapse them.
        /// </summary>
        [TestMethod]
        public void Two_add_random_components_in_one_category_count_twice()
        {
            var spec = Fragment(load: new[] { (5011u, 1), (5013u, 1) });

            var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap,
                DungeonPopulationLimits.DefaultLootQuantityCap, ComponentLookup);

            StringAssert.Contains(desc, "\nPossible effects:\n2 monster modifiers\n");
        }

        [TestMethod]
        public void Fragment_closing_line_points_at_the_press()
        {
            var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(), "any dungeon", 3, 3, Lookup, ComponentName);

            StringAssert.EndsWith(desc, "Unpressed. Load spell components onto it, then press it at the Fragment Press.");
        }

        /// <summary>
        /// Control against phase 1's text: neither closing line a finished gem can carry changed in D4c, and
        /// neither gained a prefix in P2-R20. Byte-identical, not a substring match.
        /// </summary>
        [TestMethod]
        public void Gem_closing_lines_are_unchanged()
        {
            var unbound = ThreadDungeonGemHandler.ComposeLongDesc(Gem(), "Filo's Doom", 2, 3, Lookup, ComponentName);
            StringAssert.StartsWith(unbound, "Dungeon: Filo's Doom\n");
            StringAssert.EndsWith(unbound, "\nUnbound. Use it to open a dungeon.");

            var bound = ThreadDungeonGemHandler.ComposeLongDesc(Gem().WithBinding(0x80001234u, 0x50000001u), "Filo's Doom", 2, 3, Lookup, ComponentName);
            StringAssert.EndsWith(bound, "\nBound to Filo's Doom. It crumbles when the dungeon is cleared or its last entry is spent; a death after the clear leaves no way back in.");
        }

        // ---- the LongDesc gap (owner requirement, 2026-09-07) -----------------------------------------

        /// <summary>An empty dungeon: no points, no boss anchor. Build's scale/loot-quantity/loot-quality/salvage math runs identically regardless of population, so this is enough to exercise it.</summary>
        private static DungeonEntryDef EmptyDungeon() => new DungeonEntryDef
        {
            Id = "empty", Landblock = 0x0001, Name = "Empty", ExitPortalWcid = 1,
            Points = new List<DungeonSpawnPointDef>(), BossAnchor = null, CreatureTypes = new List<string>(),
        };

        private static ThreadDungeonStore EmptyStore() => ThreadDungeonStore.Parse(
            "{\"dungeons\":[]}", "{\"bosses\":[]}", "{\"modifiers\":[],\"xpLadder\":[]}", new Dictionary<string, string>());

        private static int NeverCalledLevelOf(uint w) => throw new InvalidOperationException("no entries in an empty dungeon should ever ask for a creature's level");

        /// <summary>
        /// THE INVARIANT this feature stands or falls on: the composer's printed Experience/Luminance/Loot
        /// Quantity totals must equal what DungeonPopulationBuilder.Build actually computes for the SAME
        /// spec, at more than one level - not a pinned string, which would let the two silently drift apart
        /// again the way they did before this fix (a level-185 gem advertised "Experience: x2.00" while the
        /// run it opened paid x1.25).
        ///
        /// No modifiers on the spec, so XpMultiplier/LumMultiplier/the loot-quantity product are all exactly
        /// 1.0 on both sides (the same DungeonRewardMath functions, called with the same inputs) - which
        /// makes the Experience/Luminance totals equal to plan.XpScale/plan.LumScale directly, and isolates
        /// the assertion to the ratio-application arithmetic itself rather than also depending on modifier
        /// resolution being consistent between the two call paths.
        /// </summary>
        [TestMethod]
        public void The_printed_totals_equal_what_the_population_builder_computes_at_several_levels()
        {
            const double anchor = DungeonPopulationLimits.DefaultRewardScaleAnchor;
            const double exponent = DungeonPopulationLimits.DefaultRewardScaleExponent;
            const double floor = DungeonPopulationLimits.DefaultRewardScaleFloor;
            const double cap = DungeonPopulationLimits.DefaultRewardScaleCap;
            const double xpScale = DungeonPopulationLimits.DefaultXpScale;
            const double lumScale = DungeonPopulationLimits.DefaultLumScale;
            const double xpCap = DungeonPopulationLimits.DefaultXpCap;
            const double lumCap = DungeonPopulationLimits.DefaultLumCap;
            const double lootQuantityCap = DungeonPopulationLimits.DefaultLootQuantityCap;

            var limits = new DungeonPopulationLimits(120, xpCap, lumCap, 8, 0.5, lootQuantityCap: lootQuantityCap,
                xpScale: xpScale, lumScale: lumScale,
                rewardScalingEnabled: true, rewardScaleAnchor: anchor, rewardScaleExponent: exponent, rewardScaleFloor: floor, rewardScaleCap: cap);

            foreach (var level in new[] { 185, 245, 300, 400 })
            {
                var spec = Fragment(level: level);
                var ratio = DungeonRewardMath.RewardScaleRatio(level, anchor, exponent, floor, cap);

                var desc = ThreadDungeonGemHandler.ComposeLongDesc(spec, "any dungeon", 3, 3, Lookup, ComponentName,
                    xpCap, lumCap, lootQuantityCap, null, xpScale, lumScale, ratio);

                var plan = DungeonPopulationBuilder.Build(spec, EmptyDungeon(), EmptyStore(), new Dictionary<string, SpeciesTableDef>(),
                    NeverCalledLevelOf, w => 0u, limits, new Random(1));

                Assert.AreEqual(1.0, plan.XpMultiplier, 1e-9, $"guard level {level}: no modifiers, so the gem's own XP product must be 1.0");
                Assert.AreEqual(1.0, plan.LumMultiplier, 1e-9, $"guard level {level}: no modifiers, so the gem's own luminance product must be 1.0");

                var expectedXp = plan.XpScale.ToString("0.00", CultureInfo.InvariantCulture);
                var expectedLum = plan.LumScale.ToString("0.00", CultureInfo.InvariantCulture);

                StringAssert.Contains(desc, $"Experience: x{expectedXp}", $"level {level}:\n{desc}");
                StringAssert.Contains(desc, $"Luminance: x{expectedLum}", $"level {level}:\n{desc}");
            }
        }

        /// <summary>
        /// The master switch off must leave the composer printing the SAME flat total at every level - the
        /// pre-feature behaviour - exactly mirroring DungeonPopulationBuilder.Build's own "disabled reproduces
        /// today's behaviour" rule (ratio 1.0 always, regardless of spec.Level).
        /// </summary>
        [TestMethod]
        public void A_ratio_of_one_reproduces_the_flat_total_at_every_level()
        {
            foreach (var level in new[] { 185, 245, 300, 400 })
            {
                var desc = ThreadDungeonGemHandler.ComposeLongDesc(Fragment(level: level), "any dungeon", 3, 3, Lookup, ComponentName,
                    DungeonPopulationLimits.DefaultXpCap, DungeonPopulationLimits.DefaultLumCap, DungeonPopulationLimits.DefaultLootQuantityCap,
                    null, DungeonPopulationLimits.DefaultXpScale, DungeonPopulationLimits.DefaultLumScale, rewardScaleRatio: 1.0);

                StringAssert.Contains(desc, "Experience: x2.00", $"level {level}:\n{desc}");
                StringAssert.Contains(desc, "Luminance: x2.00", $"level {level}:\n{desc}");
            }
        }
    }
}
