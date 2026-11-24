using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The species-table opt-out ("enabled": false, SpeciesTableDef.Drawable). Owner ruling 2026-11-21: the Tendrils of
    /// T'thuun (species/slithis.json) are never a family. Each test asserts on what the runtime consumers do - the Threads
    /// eligibility test, the family draw, the stretch resolver, the fit-filter copy and the World Events catalog and family
    /// registration - not only that the field parses.
    /// </summary>
    [TestClass]
    public class SpeciesTableEnabledTests
    {
        private static readonly DungeonRosterBand Band = DungeonRosterBand.Default;

        [TestInitialize]
        public void Init() => TestGameTables.EnsureInitialized();

        private static SpeciesTableDef Table(string id, bool? enabled, params (uint Wcid, int Role, int Level)[] m)
            => new SpeciesTableDef
            {
                Id = id,
                DisplayName = id,
                CreatureType = id,
                Enabled = enabled,
                Members = m.Select(x => new SpeciesMemberDef { Wcid = x.Wcid, Role = x.Role, Level = x.Level }).ToList(),
            };

        private static Func<uint, int> LevelFrom(params SpeciesTableDef[] tables)
            => w => tables.SelectMany(t => t.Members).First(m => m.Wcid == w).Level;

        [TestMethod]
        public void Drawable_is_true_unless_enabled_is_explicitly_false()
        {
            Assert.IsTrue(Table("a", null).Drawable);
            Assert.IsTrue(Table("a", true).Drawable);
            Assert.IsFalse(Table("a", false).Drawable);
        }

        [TestMethod]
        public void A_disabled_table_is_never_eligible_even_with_in_band_trash()
        {
            var on = Table("on", null, (1u, 0, 200));
            var off = Table("off", false, (2u, 0, 200));
            var level = LevelFrom(on, off);

            Assert.IsTrue(DungeonRosterSelector.IsEligible(on, 200, level, Band), "control: the same table, enabled, is eligible");
            Assert.IsFalse(DungeonRosterSelector.IsEligible(off, 200, level, Band));

            var tables = new Dictionary<string, SpeciesTableDef> { ["on"] = on, ["off"] = off };

            Assert.AreEqual(1, DungeonRosterSelector.EligibleFamilyCount(tables, 200, level, Band));
            CollectionAssert.AreEqual(new[] { "on" }, DungeonRosterSelector.EligibleFamiliesOrdered(tables, 200, level, Band).Select(t => t.Id).ToList());
        }

        [TestMethod]
        public void The_family_draw_never_picks_a_disabled_table_and_would_without_the_flag()
        {
            var on = Table("aaa", null, (1u, 0, 200));
            var off = Table("bbb", false, (2u, 0, 200));
            var level = LevelFrom(on, off);
            var tables = new Dictionary<string, SpeciesTableDef> { ["aaa"] = on, ["bbb"] = off };
            var enabledCopy = new Dictionary<string, SpeciesTableDef> { ["aaa"] = on, ["bbb"] = Table("bbb", null, (2u, 0, 200)) };

            var picked = new HashSet<string>();
            var pickedControl = new HashSet<string>();

            for (var seed = 0; seed < 200; seed++)
            {
                picked.Add(DungeonRosterSelector.PickFamily(tables, "any", null, null, 200, level, new Random(seed), Band).Id);
                pickedControl.Add(DungeonRosterSelector.PickFamily(enabledCopy, "any", null, null, 200, level, new Random(seed), Band).Id);
            }

            CollectionAssert.AreEquivalent(new[] { "aaa" }, picked.ToList());
            CollectionAssert.AreEquivalent(new[] { "aaa", "bbb" }, pickedControl.ToList(), "control: with the flag absent the same draw does pick bbb");
        }

        [TestMethod]
        public void A_forced_disabled_family_is_refused_by_name()
        {
            var off = Table("bbb", false, (2u, 0, 200));
            var tables = new Dictionary<string, SpeciesTableDef> { ["bbb"] = off };

            var ex = Assert.ThrowsExactly<ArgumentException>(() => DungeonRosterSelector.PickFamily(tables, "bbb", null, null, 200, LevelFrom(off), new Random(1), Band));
            StringAssert.Contains(ex.Message, "disabled");
        }

        [TestMethod]
        public void A_disabled_table_never_stretches_and_ResolveAll_skips_it()
        {
            // Authored top 50, far below the level-400 band: an enabled table stretches, a disabled one must not.
            var on = Table("aaa", null, (1u, 0, 40), (2u, 0, 50));
            var off = Table("bbb", false, (3u, 0, 40), (4u, 0, 50));
            var all = new Dictionary<string, SpeciesTableDef> { ["aaa"] = on, ["bbb"] = off };
            var level = LevelFrom(on, off);

            Assert.IsTrue(FamilyStretch.ShouldStretch(on, 400, level, Band), "control");
            Assert.IsFalse(FamilyStretch.ShouldStretch(off, 400, level, Band));
            Assert.IsNull(FamilyStretch.Resolve(off, off, 400, level, level, Band, 6));
            CollectionAssert.AreEqual(new[] { "aaa" }, FamilyStretch.ResolveAll(all, all, 400, level, level, Band, 6).Select(f => f.Family.Id).ToList());
        }

        [TestMethod]
        public void The_fit_filter_projection_keeps_the_flag()
        {
            var off = Table("bbb", false, (2u, 0, 200));
            var on = Table("aaa", null, (1u, 0, 200));
            var projected = DungeonFitFilter.Project(new Dictionary<string, SpeciesTableDef> { ["bbb"] = off, ["aaa"] = on }, w => true).Species;

            Assert.IsFalse(projected["bbb"].Drawable, "PickFamily and IsEligible run against the PROJECTION, so a dropped flag would re-enable the family");
            Assert.IsTrue(projected["aaa"].Drawable);
        }

        [TestMethod]
        public void A_speed_course_naming_a_disabled_family_fails_the_family_lookup()
        {
            var tables = new Dictionary<string, SpeciesTableDef> { ["aaa"] = Table("aaa", null, (1u, 0, 200)), ["bbb"] = Table("bbb", false, (2u, 0, 200)) };
            var lookup = new ACE.Server.SpeedCourses.ThreadStoreCourseLookup(null, tables);

            Assert.IsTrue(lookup.FamilyExists("aaa"), "control");
            Assert.IsFalse(lookup.FamilyExists("bbb"));
            Assert.IsFalse(lookup.FamilyExists("missing"));
        }

        // ---- World Events ----------------------------------------------------------------------------

        private const string OffJson = "{\"id\":\"bbb\",\"displayName\":\"the Bs\",\"hueKey\":\"none\",\"enabled\":false,\"members\":[{\"wcid\":2,\"role\":0}]}";
        private const string OnJson = "{\"id\":\"aaa\",\"displayName\":\"the As\",\"hueKey\":\"none\",\"members\":[{\"wcid\":1,\"role\":0}]}";

        [TestMethod]
        public void World_Events_parse_keeps_a_disabled_table_for_statistics_but_registers_no_family_for_it()
        {
            var store = WorldEventAxisStore.Parse(null, null, null, null, null, null, null,
                new[] { ("aaa.json", OnJson), ("bbb.json", OffJson) });

            Assert.AreEqual(0, store.Diagnostics.Count, string.Join(" | ", store.Diagnostics));
            Assert.IsTrue(store.SpeciesTables.ContainsKey("bbb"), "stays in the roster: band statistics and the authored-top pivot must not move");
            Assert.IsFalse(store.SpeciesTables["bbb"].Drawable);
            Assert.IsTrue(store.Families.ContainsKey("aaa"), "control");
            Assert.IsFalse(store.Families.ContainsKey("bbb"));
        }

        private static Weenie Creature(uint wcid)
        {
            var w = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Creature,
                PropertiesBool = new Dictionary<PropertyBool, bool> { [PropertyBool.Attackable] = true },
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 100 },
                PropertiesString = new Dictionary<PropertyString, string> { [PropertyString.Name] = "Creature " + wcid },
            };
            return w;
        }

        [TestMethod]
        public void The_World_Events_catalog_never_builds_a_family_or_loads_wcids_for_a_disabled_table()
        {
            var tables = new List<SpeciesTableDef> { Table("aaa", null, (1u, 0, 100)), Table("bbb", false, (2u, 0, 100)) };
            var catalog = WorldEventCatalogBuilder.Build(new List<Weenie> { Creature(1), Creature(2) }, tables, out var diagnostics);

            Assert.IsTrue(catalog.Families.ContainsKey("aaa"), "control");
            Assert.IsFalse(catalog.Families.ContainsKey("bbb"));
            Assert.AreEqual(1, catalog.MemberCount);
            Assert.AreEqual(0, diagnostics.Count, string.Join(" | ", diagnostics));

            CollectionAssert.AreEquivalent(new uint[] { 1 }, WorldEventCatalogBuilder.CollectWcids(null, tables));
        }

        // ---- the shipped roster ----------------------------------------------------------------------

        [TestMethod]
        public void Shipped_slithis_is_the_one_disabled_table_and_no_enabled_table_lists_its_wcids()
        {
            var tables = ShippedRosterFixture.Tables;
            Assert.IsNotNull(tables, "shipped species tables not found");

            CollectionAssert.AreEquivalent(new[] { "slithis" }, tables.Values.Where(t => !t.Drawable).Select(t => t.Id).ToList());
            Assert.IsTrue(tables["slithis"].Members.Count > 0, "slithis stays in the roster file (band statistics), it is only not drawable");

            var slithisWcids = new HashSet<uint>(tables["slithis"].Members.Select(m => m.Wcid));

            foreach (var other in tables.Values.Where(t => t.Id != "slithis"))
                Assert.IsFalse(other.Members.Any(m => slithisWcids.Contains(m.Wcid)), $"{other.Id} lists a slithis wcid, which would draw it through another family");
        }

        private static DungeonSpawnPlan BuildPlan(IReadOnlyDictionary<string, SpeciesTableDef> tables, int level, int seed, DungeonPopulationLimits limits, string family = "any")
            => DungeonPopulationBuilder.Build(FamilyStretchFixture.Spec(level, seed, family), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                tables, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, limits, new Random(seed), FamilyStretchFixture.LevelProfile, null, null);

        [TestMethod]
        public void The_shipped_Threads_builder_never_draws_slithis_native_or_stretched_and_would_with_the_flag_removed()
        {
            var shipped = ShippedRosterFixture.Tables;
            Assert.IsNotNull(shipped, "shipped species tables not found");

            // The same roster with only slithis re-enabled: the positive control that this loop CAN see slithis.
            var reEnabled = shipped.ToDictionary(kv => kv.Key, kv => kv.Value);
            var s = shipped["slithis"];
            reEnabled["slithis"] = new SpeciesTableDef { Id = s.Id, DisplayName = s.DisplayName, HueKey = s.HueKey, CreatureType = s.CreatureType, Source = s.Source, Members = s.Members };

            var stretchOn = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, familyStretch: true);

            var level = new Func<uint, int>(ShippedRosterFixture.LevelOf);
            var eligibleControl = 0;
            var stretchControl = 0;

            foreach (var lvl in new[] { 200, 400, 480 })
            {
                // The runtime eligibility consumer (the family draw's candidate list) and the stretch gate, over the shipped roster.
                Assert.IsFalse(DungeonRosterSelector.IsEligible(shipped["slithis"], lvl, level, Band), $"shipped slithis is eligible at {lvl}");
                Assert.IsFalse(DungeonRosterSelector.EligibleFamiliesOrdered(shipped, lvl, level, Band).Any(t => t.Id == "slithis"), $"shipped draw list holds slithis at {lvl}");
                Assert.IsFalse(FamilyStretch.ShouldStretch(shipped["slithis"], lvl, level, Band), $"shipped slithis stretches at {lvl}");

                // Positive control: the same roster with only slithis re-enabled, read through the same consumers.
                if (DungeonRosterSelector.EligibleFamiliesOrdered(reEnabled, lvl, level, Band).Any(t => t.Id == "slithis"))
                    eligibleControl++;

                if (FamilyStretch.ShouldStretch(reEnabled["slithis"], lvl, level, Band))
                    stretchControl++;
            }

            Assert.IsTrue(eligibleControl > 0, "control: re-enabled slithis is native-eligible at some tested level, so the zero above is a real result");
            Assert.IsTrue(stretchControl > 0, "control: re-enabled slithis is stretch-eligible at some tested level, so the zero above is a real result");

            // End to end through the builder. Seed 33 is one where the re-enabled roster draws slithis at all three levels with the stretch on
            // (probed 2026-11-21 over seeds 1-50: 200, 400 and 480 all hit it), so the control is guaranteed nonzero and the run is 6 plans.
            var drawnShipped = 0;
            var drawnControl = 0;

            foreach (var lvl in new[] { 200, 400, 480 })
            {
                if (BuildPlan(shipped, lvl, 33, stretchOn).FamilyId == "slithis")
                    drawnShipped++;

                if (BuildPlan(reEnabled, lvl, 33, stretchOn).FamilyId == "slithis")
                    drawnControl++;
            }

            Assert.AreEqual(0, drawnShipped, "the shipped roster drew slithis");
            Assert.AreEqual(3, drawnControl, "control: with slithis re-enabled seed 33 draws it at every level, so the zero above is a real result");
        }

        [TestMethod]
        public void A_gem_that_forces_slithis_opens_with_no_family_and_a_note()
        {
            var plan = BuildPlan(ShippedRosterFixture.Tables, 200, 1, new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, familyStretch: true), "slithis");

            Assert.IsNull(plan.FamilyId);
            Assert.IsTrue(plan.Notes.Any(n => n.Contains("disabled")), string.Join(" | ", plan.Notes));
        }
    }
}
