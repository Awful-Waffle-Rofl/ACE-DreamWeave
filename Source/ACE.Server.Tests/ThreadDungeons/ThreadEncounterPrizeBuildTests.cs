using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadEncounterTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ThreadEncounterPrize.BuildObjects (the object building extracted from Deliver), the gamble quantity rule, the
    /// oversized-stack split, and the per-run type filter (IThreadEncounterType.RejectRun through the picker). Objects are
    /// built from in-memory weenies through the CreateObject / Knots factory seams, so no dat files or database. Every seam
    /// swapped is restored in a finally. Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ThreadEncounterPrizeBuildTests
    {
        private static uint nextGuid = 0x7F100000;

        private static WorldObject Stack(uint wcid, string name, int max)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.StackSize, 1 }, { PropertyInt.MaxStackSize, max } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject Single(uint wcid, string name)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>Runs <paramref name="body"/> with the create seam swapped to <paramref name="create"/>, restoring every seam it touched.</summary>
        private static void WithSeams(Func<uint, WorldObject> create, Action body, Func<int, WorldObject> fiber = null, Func<ACE.Server.Knots.PinnacleDef, WorldObject> pattern = null)
        {
            var oCreate = ThreadEncounterPrize.CreateObject;
            var oExists = ThreadEncounterPrize.WeenieExists;
            var oFiber = ACE.Server.Knots.KnotDrops.FiberFactory;
            var oPattern = ACE.Server.Knots.KnotDrops.PatternFactory;

            try
            {
                ThreadEncounterPrize.CreateObject = create;
                ThreadEncounterPrize.WeenieExists = _ => true;

                if (fiber != null)
                    ACE.Server.Knots.KnotDrops.FiberFactory = fiber;

                if (pattern != null)
                    ACE.Server.Knots.KnotDrops.PatternFactory = pattern;

                body();
            }
            finally
            {
                ThreadEncounterPrize.CreateObject = oCreate;
                ThreadEncounterPrize.WeenieExists = oExists;
                ACE.Server.Knots.KnotDrops.FiberFactory = oFiber;
                ACE.Server.Knots.KnotDrops.PatternFactory = oPattern;
            }
        }

        // ---- the quantity rule ---------------------------------------------------------------------------------

        [TestMethod]
        public void GambleQuantity_grows_by_half_the_base_per_wave_and_a_single_valuable_never_grows()
        {
            CollectionAssert.AreEqual(new[] { 5, 7, 9, 11, 13 }, Enumerable.Range(0, 5).Select(w => ThreadEncounterPrize.GambleQuantity(5, w, false)).ToArray());
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, Enumerable.Range(0, 5).Select(w => ThreadEncounterPrize.GambleQuantity(2, w, false)).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1 }, Enumerable.Range(0, 5).Select(w => ThreadEncounterPrize.GambleQuantity(1, w, false)).ToArray(), "qty 1 never grows");
            CollectionAssert.AreEqual(new[] { 5, 5, 5, 5, 5 }, Enumerable.Range(0, 5).Select(w => ThreadEncounterPrize.GambleQuantity(5, w, true)).ToArray(), "a single valuable keeps its base");
            CollectionAssert.AreEqual(new[] { 10, 15, 20 }, new[] { 0, 1, 2 }.Select(w => ThreadEncounterPrize.GambleQuantity(10, w, false)).ToArray());
            Assert.AreEqual(5, ThreadEncounterPrize.GambleQuantity(5, -3, false), "negative waves count as none");
        }

        [TestMethod]
        public void StackSizes_splits_above_the_max_stack_and_agrees_with_StackFor_below_it()
        {
            CollectionAssert.AreEqual(new[] { 100, 100, 50 }, ThreadEncounterPrize.StackSizes(250, true, 100).ToArray());
            CollectionAssert.AreEqual(new[] { 100, 100 }, ThreadEncounterPrize.StackSizes(200, true, 100).ToArray());
            CollectionAssert.AreEqual(new[] { 10 }, ThreadEncounterPrize.StackSizes(10, true, 1000).ToArray());
            CollectionAssert.AreEqual(new[] { 1 }, ThreadEncounterPrize.StackSizes(10, false, 1000).ToArray(), "a non-stackable item is one");
            CollectionAssert.AreEqual(new[] { 1 }, ThreadEncounterPrize.StackSizes(10, true, null).ToArray(), "no max stack is one");
            CollectionAssert.AreEqual(new[] { 1 }, ThreadEncounterPrize.StackSizes(0, true, 1000).ToArray());

            foreach (var q in new[] { 1, 7, 10, 99 })
                CollectionAssert.AreEqual(new[] { q }, ThreadEncounterPrize.StackSizes(q, true, 100).ToArray(), "within the max, one stack of that size");
        }

        [TestMethod]
        public void StackSizes_is_bounded_by_the_stack_cap()
        {
            var sizes = ThreadEncounterPrize.StackSizes(10000, true, 1);
            Assert.AreEqual(ThreadEncounterPrize.MaxStacksPerEntry, sizes.Count);
            Assert.IsTrue(sizes.All(s => s == 1));

            Assert.AreEqual(ThreadEncounterPrize.MaxStacksPerEntry, ThreadEncounterPrize.StackSizes(int.MaxValue, true, 100).Count);
        }

        [TestMethod]
        public void GambleQuantity_clamps_waves_and_cannot_overflow()
        {
            Assert.AreEqual(13, ThreadEncounterPrize.GambleQuantity(5, 4, false));
            Assert.AreEqual(13, ThreadEncounterPrize.GambleQuantity(5, int.MaxValue, false));
            Assert.AreEqual(ThreadEncounterPrize.GambleQuantity(5, 0, false), ThreadEncounterPrize.GambleQuantity(5, -7, false));
            Assert.AreEqual(5, ThreadEncounterPrize.GambleQuantity(5, int.MinValue, false));
            Assert.AreEqual(int.MaxValue, ThreadEncounterPrize.GambleQuantity(int.MaxValue, 4, false), "saturates, never wraps");
            Assert.AreEqual(4, ThreadEncounterPrize.MaxBonusWaves);
        }

        [TestMethod]
        public void A_stack_beyond_the_cap_drops_the_remainder()
        {
            var pick = new ThreadEncounterPrizePick(new ThreadEncounterPrizeEntry("notes", new uint[] { 700 }, 10000, 1), new uint[] { 700 });

            WithSeams(w => Stack(w, "Note", 1), () =>
            {
                var built = ThreadEncounterPrize.BuildObjects(pick, 0);
                Assert.AreEqual(ThreadEncounterPrize.MaxStacksPerEntry, built.Count);
            });
        }

        [TestMethod]
        public void Extra_salvage_bags_draw_only_from_pool_wcids_that_exist_else_the_full_pool()
        {
            var pool = new uint[] { 21036, 21051, 21063 };
            var entry = new ThreadEncounterPrizeEntry("salvage", pool, 1, 1, new ThreadEncounterPrizeSalvage(2, 4, 100, 10));
            var pick = new ThreadEncounterPrizePick(entry, new uint[] { 21036, 21036 });

            WithSeams(w => Stack(w, $"Salvaged M{w}", 1), () =>
            {
                ThreadEncounterPrize.WeenieExists = w => w == 21036;
                var only = ThreadEncounterPrize.BuildObjects(pick, 4, new Random(3));
                Assert.AreEqual(2 + 4, only.Count);
                Assert.IsTrue(only.Skip(2).All(b => b.Item.WeenieClassId == 21036), "only the existing wcid is drawn");

                ThreadEncounterPrize.WeenieExists = _ => false;
                var fallback = ThreadEncounterPrize.BuildObjects(pick, 4, new Random(3));
                Assert.AreEqual(2 + 4, fallback.Count);
                Assert.IsTrue(fallback.Skip(2).All(b => pool.Contains(b.Item.WeenieClassId)), "none exist: the full pool");
            });
        }

        // ---- BuildObjects: equivalence with the old Deliver build at 0 waves ------------------------------------

        [TestMethod]
        public void An_item_entry_builds_one_stack_of_its_qty_at_zero_waves()
        {
            var entry = new ThreadEncounterPrizeEntry("notes", new uint[] { 20630 }, 5, 1);
            var pick = new ThreadEncounterPrizePick(entry, new uint[] { 20630 });

            WithSeams(w => Stack(w, "Trade Note", 1000), () =>
            {
                var built = ThreadEncounterPrize.BuildObjects(pick, 0);

                Assert.AreEqual(1, built.Count);
                Assert.AreEqual(5, built[0].Item.StackSize);
                Assert.AreEqual("Trade Note", built[0].Label);
                Assert.AreEqual(20630u, built[0].Item.WeenieClassId);
            });
        }

        [TestMethod]
        public void A_qty_one_item_never_grows_and_is_not_stacked()
        {
            var entry = new ThreadEncounterPrizeEntry("mantle", new uint[] { 555 }, 1, 1);
            var pick = new ThreadEncounterPrizePick(entry, new uint[] { 555 });

            WithSeams(w => Single(w, "Rear Guard Mantle"), () =>
            {
                foreach (var waves in new[] { 0, 1, 4 })
                {
                    var built = ThreadEncounterPrize.BuildObjects(pick, waves);
                    Assert.AreEqual(1, built.Count, $"waves {waves}");
                    Assert.AreEqual("Rear Guard Mantle", built[0].Label);
                    Assert.IsTrue((built[0].Item.StackSize ?? 1) == 1);
                }
            });
        }

        [TestMethod]
        public void Qty_grows_per_wave_and_an_oversized_stack_is_split_not_clamped()
        {
            var entry = new ThreadEncounterPrizeEntry("gems", new uint[] { 700 }, 5, 1);
            var pick = new ThreadEncounterPrizePick(entry, new uint[] { 700 });

            WithSeams(w => Stack(w, "Gem", 100), () =>
            {
                // qty 5 grows 5, 7, 9, 11, 13: one stack each.
                var sizes = Enumerable.Range(0, 5).Select(k => ThreadEncounterPrize.BuildObjects(pick, k).Sum(b => b.Item.StackSize ?? 1)).ToArray();
                CollectionAssert.AreEqual(new[] { 5, 7, 9, 11, 13 }, sizes);
            });

            // Max stack 4: 5 -> 4+1, 9 (2 waves) -> 4+4+1.
            WithSeams(w => Stack(w, "Gem", 4), () =>
            {
                CollectionAssert.AreEqual(new[] { 4, 1 }, ThreadEncounterPrize.BuildObjects(pick, 0).Select(b => b.Item.StackSize ?? 1).ToArray());
                CollectionAssert.AreEqual(new[] { 4, 4, 1 }, ThreadEncounterPrize.BuildObjects(pick, 2).Select(b => b.Item.StackSize ?? 1).ToArray());
            });

            // An authored qty above the max with no waves is several stacks, not one clamped stack.
            var big = new ThreadEncounterPrizePick(new ThreadEncounterPrizeEntry("notes", new uint[] { 700 }, 250, 1), new uint[] { 700 });

            WithSeams(w => Stack(w, "Note", 100), () =>
            {
                CollectionAssert.AreEqual(new[] { 100, 100, 50 }, ThreadEncounterPrize.BuildObjects(big, 0).Select(b => b.Item.StackSize ?? 1).ToArray());
            });
        }

        [TestMethod]
        public void A_missing_extra_stack_is_skipped_and_the_first_still_builds()
        {
            var entry = new ThreadEncounterPrizeEntry("gems", new uint[] { 700 }, 5, 1);
            var pick = new ThreadEncounterPrizePick(entry, new uint[] { 700 });
            var calls = 0;

            WithSeams(w => ++calls == 1 ? Stack(w, "Gem", 4) : null, () =>
            {
                var built = ThreadEncounterPrize.BuildObjects(pick, 0);
                Assert.AreEqual(1, built.Count);
                Assert.AreEqual(4, built[0].Item.StackSize);
            });
        }

        [TestMethod]
        public void Fibers_and_patterns_go_through_the_knots_seam_and_never_grow()
        {
            var fibers = new ThreadEncounterPrizeEntry("fiber", new uint[] { 1007000 }, 3, 1, fiber: true);
            var fiberPick = new ThreadEncounterPrizePick(fibers, new uint[] { 1007000, 1007000, 1007000 }, fiberLevel: 120);
            var levels = new List<int>();

            var pinnacle = new ACE.Server.Knots.PinnacleDef { Key = "p1", Name = "P One" };
            var patternPick = new ThreadEncounterPrizePick(new ThreadEncounterPrizeEntry("pattern", new uint[] { 1007003 }, 1, 1, pattern: true), new uint[] { 1007003 }, 0, pinnacle);
            ACE.Server.Knots.PinnacleDef seen = null;

            WithSeams(w => throw new AssertFailedException("the Knots seam builds these, never CreateObject"), () =>
            {
                foreach (var waves in new[] { 0, 3 })
                {
                    levels.Clear();
                    var built = ThreadEncounterPrize.BuildObjects(fiberPick, waves);

                    Assert.AreEqual(3, built.Count, $"waves {waves}: a Fiber is a single valuable");
                    Assert.AreEqual(3, levels.Count);
                    Assert.IsTrue(levels.All(l => l == 120));
                    Assert.IsTrue(built.All(b => b.Label == "Fiber (Level 120)"));

                    var p = ThreadEncounterPrize.BuildObjects(patternPick, waves);
                    Assert.AreEqual(1, p.Count);
                    Assert.AreEqual("Pattern: P One", p[0].Label);
                }

                Assert.AreSame(pinnacle, seen);
            },
            fiber: lvl => { levels.Add(lvl); return Single(1007000, $"Fiber (Level {lvl})"); },
            pattern: def => { seen = def; return Single(1007003, $"Pattern: {def.Name}"); });
        }

        [TestMethod]
        public void A_salvage_entry_builds_filled_bags_labelled_by_material_at_zero_waves()
        {
            var pool = new uint[] { 21036, 21051 };
            var entry = new ThreadEncounterPrizeEntry("salvage", pool, 1, 1, new ThreadEncounterPrizeSalvage(2, 4, 100, 10));
            var pick = new ThreadEncounterPrizePick(entry, new uint[] { 21036, 21051, 21036 });
            var names = new Dictionary<uint, string> { { 21036, "Salvaged Amethyst" }, { 21051, "Salvaged Green Jade" } };

            WithSeams(w => Stack(w, names[w], 1), () =>
            {
                var built = ThreadEncounterPrize.BuildObjects(pick, 0);

                CollectionAssert.AreEqual(new[] { "Amethyst", "Green Jade", "Amethyst" }, built.Select(b => b.Label).ToArray());

                foreach (var (item, _) in built)
                {
                    var fields = ThreadEncounterPrize.SalvageBagFields(100, 10);
                    Assert.AreEqual(fields.Name, item.Name);
                    Assert.AreEqual(fields.Structure, item.Structure);
                    Assert.AreEqual(fields.ItemWorkmanship, item.ItemWorkmanship);
                    Assert.AreEqual(fields.NumItemsInMaterial, item.NumItemsInMaterial);
                }
            });
        }

        [TestMethod]
        public void The_salvage_bag_count_grows_by_the_same_rule_and_each_extra_bag_draws_its_own_material()
        {
            var pool = new uint[] { 21036, 21051, 21063, 21082 };
            var entry = new ThreadEncounterPrizeEntry("salvage", pool, 1, 1, new ThreadEncounterPrizeSalvage(2, 4, 100, 10));

            // base -> expected count at waves 0..3
            var table = new Dictionary<int, int[]>
            {
                { 2, new[] { 2, 3, 4, 5 } },
                { 3, new[] { 3, 4, 5, 6 } },
                { 4, new[] { 4, 6, 8, 10 } },
            };

            WithSeams(w => Stack(w, $"Salvaged M{w}", 1), () =>
            {
                foreach (var kv in table)
                {
                    var pick = new ThreadEncounterPrizePick(entry, Enumerable.Repeat(pool[0], kv.Key).ToArray());

                    for (var waves = 0; waves < 4; waves++)
                    {
                        var built = ThreadEncounterPrize.BuildObjects(pick, waves, new Random(11 + waves));
                        Assert.AreEqual(kv.Value[waves], built.Count, $"base {kv.Key}, waves {waves}");
                        Assert.IsTrue(built.All(b => pool.Contains(b.Item.WeenieClassId)), "every bag is from the entry's pool");
                    }
                }

                // Independent draws: a long run of extra bags is not all one material.
                var many = new ThreadEncounterPrizePick(entry, Enumerable.Repeat(pool[0], 20).ToArray());
                var wide = ThreadEncounterPrize.BuildObjects(many, 4, new Random(5));
                Assert.AreEqual(20 + 4 * 10, wide.Count);
                Assert.IsTrue(wide.Skip(20).Select(b => b.Item.WeenieClassId).Distinct().Count() > 1, "extra bags draw independently");
            });
        }

        // ---- the per-run type filter -----------------------------------------------------------------------------

        private static ThreadDungeonRun FourPointRun()
            => DungeonRun(new[]
            {
                Point(DungeonCell(), 0, 0), Point(DungeonCell(), 40, 0), Point(DungeonCell(), 80, 0), Point(DungeonCell(), 120, 0),
            }, max: 2);

        [TestMethod]
        public void TypeUnavailable_reports_the_run_filters_reason_and_null_changes_nothing()
        {
            var def = Store(Type("a")).Types[0];

            Assert.IsNull(ThreadEncounterPicker.TypeUnavailable(def, new ThreadEncounterPickRequest()));
            Assert.IsNull(ThreadEncounterPicker.TypeUnavailable(def, new ThreadEncounterPickRequest { RunFilter = _ => null }));
            Assert.AreEqual("closed for this run", ThreadEncounterPicker.TypeUnavailable(def, new ThreadEncounterPickRequest { RunFilter = _ => "closed for this run" }));

            // The static reasons still come first.
            def.Enabled = false;
            Assert.AreEqual("disabled", ThreadEncounterPicker.TypeUnavailable(def, new ThreadEncounterPickRequest { RunFilter = _ => "closed for this run" }));
        }

        [TestMethod]
        public void A_type_whose_RejectRun_returns_a_reason_is_not_picked_and_the_reason_is_reported()
        {
            var fake = new FakeType { RunReject = (def, run) => "no peddler in this run" };
            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);

            Assert.AreEqual(0, run.EncounterPlan.Picks.Count);
            Assert.AreEqual(0, seams.Queued.Count);
            Assert.IsTrue(run.EncounterPlan.Dropped.Any(d => d.Contains("no peddler in this run")), string.Join("\n", run.EncounterPlan.Dropped));
        }

        [TestMethod]
        public void A_type_whose_RejectRun_returns_null_is_picked_as_before()
        {
            var seen = new List<ThreadDungeonRun>();
            var fake = new FakeType { RunReject = (def, run) => { seen.Add(run); return null; } };
            using var seams = new Seams(Store(Type("a")), fake);
            var run = FourPointRun();

            ThreadEncounterPass.OnRunPopulated(run);

            Assert.AreEqual(2, run.EncounterPlan.Picks.Count);
            Assert.IsTrue(seen.Count > 0 && seen.All(r => ReferenceEquals(r, run)), "the filter is handed this run");

            // And a type that leaves RejectRun at its default is unaffected.
            var plain = new FakeType();
            using var seams2 = new Seams(Store(Type("a")), plain);
            var run2 = FourPointRun();
            ThreadEncounterPass.OnRunPopulated(run2);
            Assert.AreEqual(2, run2.EncounterPlan.Picks.Count);
        }
    }
}
