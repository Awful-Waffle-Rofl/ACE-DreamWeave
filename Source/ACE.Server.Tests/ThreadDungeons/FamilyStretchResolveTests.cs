using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class FamilyStretchResolveTests
    {
        private static readonly DungeonRosterBand Band = DungeonRosterBand.Default;

        private static IReadOnlyDictionary<string, SpeciesTableDef> T
        {
            get
            {
                var t = ShippedRosterFixture.Tables;
                Assert.IsNotNull(t, "Content/events/axes/species not found from the test output directory");
                return t;
            }
        }

        private static int LevelOf(uint w) => ShippedRosterFixture.LevelOf(w);

        private static int Pivot => DungeonPopulationBuilder.ReachUpPivot(DungeonPopulationBuilder.AuthoredTopLevel(T, LevelOf), Band.High);

        private static Func<uint, int> ProjAt(int level)
        {
            var k = DungeonPopulationBuilder.ReachUpScale(level, Pivot);
            return w => DungeonPopulationBuilder.ProjectLevel(LevelOf(w), k);
        }

        private static StretchedFamily At400(string id) => FamilyStretch.Resolve(T[id], T[id], 400, LevelOf, ProjAt(400), Band, 6);

        [TestMethod]
        public void Production_pivot_is_326()
        {
            Assert.AreEqual(326, Pivot);
        }

        private static SpeciesTableDef Synth(params (uint Wcid, int Role, int Level)[] m)
            => new SpeciesTableDef
            {
                Id = "s",
                Members = m.Select(x => new SpeciesMemberDef { Wcid = x.Wcid, Role = x.Role, Level = x.Level }).ToList(),
            };

        private static int SynthLevel(SpeciesTableDef t, uint w) => t.Members.First(m => m.Wcid == w).Level;

        [TestMethod]
        public void TopTrash_breaks_level_ties_by_lower_wcid()
        {
            var t = Synth((9u, 0, 50), (3u, 0, 50));
            var top = FamilyStretch.TopTrash(t, w => SynthLevel(t, w), 1);

            Assert.AreEqual(1, top.Count);
            Assert.AreEqual(3u, top[0].Wcid);
        }

        [TestMethod]
        public void TopTrash_counts_a_duplicate_wcid_once()
        {
            var t = Synth((5u, 0, 50), (5u, 0, 50), (6u, 0, 40));
            var top = FamilyStretch.TopTrash(t, w => SynthLevel(t, w), 6);

            CollectionAssert.AreEqual(new[] { 5u, 6u }, top.Select(x => x.Wcid).ToList());
        }

        [TestMethod]
        public void Role_2_member_is_an_elite_candidate()
        {
            var t = Synth((1u, 0, 50), (2u, 2, 60));
            Func<uint, int> lv = w => SynthLevel(t, w);
            var f = FamilyStretch.Resolve(t, t, 400, lv, lv, Band, 6);

            CollectionAssert.AreEqual(new[] { 2u }, f.ElitePool.ToList());
            Assert.AreEqual(460, f.EliteLevels[2]);
        }

        [TestMethod]
        public void Wisp_at_201_fields_no_elite_above_the_band()
        {
            var k = DungeonPopulationBuilder.ReachUpScale(201, Pivot);
            Func<uint, int> proj = w => DungeonPopulationBuilder.ProjectLevel(LevelOf(w), k);

            var f201 = FamilyStretch.Resolve(T["wisp"], T["wisp"], 201, LevelOf, proj, Band, 6);
            Assert.IsNotNull(f201);
            Assert.AreEqual(0, f201.ElitePool.Count, "Torment Wisp (265) and Rage Wisp (255) both exceed ceil(1.15*201)=232");

            var k2 = DungeonPopulationBuilder.ReachUpScale(222, Pivot);
            Func<uint, int> proj2 = w => DungeonPopulationBuilder.ProjectLevel(LevelOf(w), k2);

            var f222 = FamilyStretch.Resolve(T["wisp"], T["wisp"], 222, LevelOf, proj2, Band, 6);
            Assert.IsNotNull(f222);
            CollectionAssert.AreEqual(new[] { 46951u }, f222.ElitePool.ToList());
        }

        [TestMethod]
        public void Elite_ties_break_by_lower_wcid()
        {
            var t = Synth((1u, 0, 50), (8u, 1, 60), (4u, 2, 60));
            Func<uint, int> lv = w => SynthLevel(t, w);
            var f = FamilyStretch.Resolve(t, t, 400, lv, lv, Band, 6);

            CollectionAssert.AreEqual(new[] { 4u }, f.ElitePool.ToList());
        }

        [TestMethod]
        public void Distinct_levels_map_strictly_increasing_and_bound_the_family()
        {
            var t = Synth((1u, 0, 10), (2u, 0, 20), (3u, 0, 30), (4u, 0, 40));
            Func<uint, int> lv = w => SynthLevel(t, w);
            var f = FamilyStretch.Resolve(t, t, 400, lv, lv, Band, 6);

            var mapped = new[] { 1u, 2u, 3u, 4u }.Select(w => f.TrashLevels[w]).ToList();

            for (var i = 1; i < mapped.Count; i++)
                Assert.IsTrue(mapped[i] > mapped[i - 1], "not strictly increasing at " + i);

            CollectionAssert.AreEqual(new[] { 400, 420, 440, 460 }, mapped);
            Assert.AreEqual(400, f.MappedLow);
            Assert.AreEqual(460, f.MappedHigh);
        }

        [TestMethod]
        public void Drudge_at_400_maps_its_top_six_by_rank()
        {
            var f = At400("drudge");

            Assert.AreEqual(400, f.LowEdge);
            Assert.AreEqual(460, f.HighEdge);
            CollectionAssert.AreEquivalent(new[] { 73227u, 22899u, 25563u, 30680u, 23480u, 24278u }, f.TrashPool.ToList());
            Assert.AreEqual(460, f.TrashLevels[73227]);
            Assert.AreEqual(423, f.TrashLevels[22899]);
            Assert.AreEqual(400, f.TrashLevels[24278]);
            Assert.AreEqual(0, f.ElitePool.Count);
        }

        [TestMethod]
        public void Snowman_and_wisp_and_mosswart_at_400()
        {
            var s = At400("snowman");
            var expected = new Dictionary<uint, int> { [88344] = 460, [46278] = 433, [14466] = 402, [5761] = 400, [5766] = 400 };

            foreach (var kv in expected)
                Assert.AreEqual(kv.Value, s.TrashLevels[kv.Key], "snowman " + kv.Key);

            Assert.AreEqual(1.25, T["snowman"].Overscale);
            Assert.AreEqual(T["snowman"].Overscale, s.Overscale);

            var w = At400("wisp");
            Assert.AreEqual(414, w.TrashLevels[87571]);
            Assert.AreEqual(460, w.EliteLevels[46710]);

            var m = At400("mosswart");
            Assert.AreEqual(460, m.EliteLevels[1619]);
            Assert.AreEqual(460, m.MappedLevelOf(1619, DungeonRole.Elite));
            Assert.AreEqual(0, m.MappedLevelOf(73227, DungeonRole.Trash));
        }

        [TestMethod]
        public void Short_and_flat_families_map_to_the_midpoint()
        {
            Assert.AreEqual(430, FamilyStretch.MapLevels(new[] { (1u, 50) }, 400, 460)[1]);

            var two = FamilyStretch.MapLevels(new[] { (1u, 50), (2u, 50) }, 400, 460);
            Assert.AreEqual(430, two[1]);
            Assert.AreEqual(430, two[2]);
        }

        [TestMethod]
        public void Natively_in_band_and_gap_families_never_stretch()
        {
            Assert.IsFalse(FamilyStretch.ShouldStretch(T["drudge"], 180, LevelOf, Band));
            Assert.IsTrue(FamilyStretch.ShouldStretch(T["drudge"], 201, LevelOf, Band));
            Assert.IsFalse(FamilyStretch.ShouldStretch(T["snowman"], 150, LevelOf, Band));
            Assert.IsFalse(FamilyStretch.ShouldStretch(T["ghost"], 480, ProjAt(480), Band));
            Assert.IsFalse(FamilyStretch.ShouldStretch(T["chicken"], 400, ProjAt(400), Band));
        }

        [TestMethod]
        public void Families_without_usable_trash_never_stretch()
        {
            var eliteOnly = new SpeciesTableDef
            {
                Id = "x",
                Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 1, Role = 1, Level = 10 } },
            };
            var zeroTrash = new SpeciesTableDef
            {
                Id = "y",
                Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = 2, Role = 0, Level = 10 } },
            };

            Func<uint, int> ten = w => 10;
            Func<uint, int> zero = w => 0;

            Assert.IsFalse(FamilyStretch.ShouldStretch(eliteOnly, 400, ten, Band));
            Assert.IsNull(FamilyStretch.Resolve(eliteOnly, eliteOnly, 400, ten, ten, Band, 6));
            Assert.IsFalse(FamilyStretch.ShouldStretch(zeroTrash, 400, zero, Band));
            Assert.IsNull(FamilyStretch.Resolve(zeroTrash, zeroTrash, 400, zero, zero, Band, 6));
        }

        [TestMethod]
        public void ResolveAll_counts_on_the_shipped_roster()
        {
            // 400 and 480 were 49 until 2026-11-21: slithis (the Tendrils of T'thuun) was the 49th stretched family and went
            // "enabled": false, so it is in the excluded set below as well.
            var excluded = new HashSet<string> { "anekshay", "ghost", "golem", "gromnie", "mukkir", "olthoi", "shadow", "undead", "virindi", "zefir", "chicken", "rabbit", "scarecrow", "slithis" };
            var expected = new Dictionary<int, int> { [150] = 13, [200] = 24, [400] = 48, [480] = 48 };

            foreach (var kv in expected)
            {
                var all = FamilyStretch.ResolveAll(T, T, kv.Key, LevelOf, ProjAt(kv.Key), Band, 6);
                Assert.AreEqual(kv.Value, all.Count, "count at " + kv.Key);

                var ids = all.Select(f => f.Family.Id).ToList();
                CollectionAssert.AreEqual(ids.OrderBy(i => i, StringComparer.Ordinal).ToList(), ids);
                Assert.IsFalse(ids.Any(excluded.Contains), "excluded id present at " + kv.Key);
            }
        }

        [TestMethod]
        public void Selection_table_supplies_the_members()
        {
            var raw = T["drudge"];
            var sel = new SpeciesTableDef { Id = raw.Id, Members = raw.Members.Where(m => m.Wcid != 73227).ToList() };

            var f = FamilyStretch.Resolve(raw, sel, 400, LevelOf, ProjAt(400), Band, 6);

            Assert.IsNotNull(f);
            Assert.IsFalse(f.TrashPool.Contains(73227u));
            Assert.AreEqual(6, f.TrashPool.Count);

            foreach (var w in new[] { 22899u, 25563u, 30680u, 23480u, 24278u })
                Assert.IsTrue(f.TrashPool.Contains(w), "missing " + w);

            Assert.AreEqual(460, f.TrashLevels[22899]);

            var none = new Dictionary<string, SpeciesTableDef>();
            Assert.IsNull(FamilyStretch.Resolve(raw, null, 400, LevelOf, ProjAt(400), Band, 6));
            Assert.AreEqual(0, FamilyStretch.ResolveAll(T, none, 400, LevelOf, ProjAt(400), Band, 6).Count);
        }

        [TestMethod]
        public void Active_is_off_for_speed_courses()
        {
            var on = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, familyStretch: true);

            Assert.IsFalse(FamilyStretch.Active(on.WithForcedFamilyWidening()));
            Assert.IsTrue(FamilyStretch.Active(on));
        }
    }
}
