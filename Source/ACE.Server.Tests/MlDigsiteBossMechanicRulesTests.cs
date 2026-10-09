using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.MlDigsite;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The PURE half of the ML digsite Boss Rush mechanic sets: the authoring grammar, the shipped set table,
    /// the set roll, and the arithmetic each of the five mechanics makes its decision from.
    ///
    /// Nothing here reads PropertyManager (MlDigsiteBossMechanicRules never does, and PropertyManager throws
    /// under this harness for a key nothing has seeded), stands up a world, or needs a creature - which is
    /// exactly why the modules are split pure-decision / thin-effect in the first place.
    ///
    /// EVERY TEST HERE FAILS IF THE FEATURE IS REVERTED, because every one of them names a rule that only
    /// exists because of it.
    /// </summary>
    [TestClass]
    public class MlDigsiteBossMechanicRulesTests
    {
        // ---- the args reader -----------------------------------------------------------------------------

        [TestMethod]
        public void ParseArgs_reads_whitespace_separated_key_value_tokens()
        {
            var args = MlDigsiteBossMechanicRules.ParseArgs("every=20 count=3 radius=6.5");

            Assert.AreEqual(20.0, args.GetDouble("every", -1), 0.0001);
            Assert.AreEqual(3, args.GetInt("count", -1));
            Assert.AreEqual(6.5, args.GetDouble("radius", -1), 0.0001);
        }

        [TestMethod]
        public void ParseArgs_falls_back_to_the_documented_default_for_a_missing_arg_never_to_zero()
        {
            var args = MlDigsiteBossMechanicRules.ParseArgs("every=20");

            Assert.AreEqual(7.5, args.GetDouble("radius", 7.5), 0.0001, "a missing arg must read as its documented default");
            Assert.AreEqual(4, args.GetInt("count", 4));
            Assert.IsFalse(args.Has("radius"));
        }

        [TestMethod]
        public void ParseArgs_drops_a_malformed_token_and_reports_it_rather_than_failing_the_whole_string()
        {
            var errors = new List<string>();

            var args = MlDigsiteBossMechanicRules.ParseArgs("every=20 nonsense count=3 =5 trailing=", errors);

            Assert.AreEqual(20.0, args.GetDouble("every", -1), 0.0001, "the good tokens survive");
            Assert.AreEqual(3, args.GetInt("count", -1));
            Assert.AreEqual(3, errors.Count, string.Join(" | ", errors));
        }

        [TestMethod]
        public void ParseArgs_keeps_the_first_of_a_repeated_key_and_says_so()
        {
            var errors = new List<string>();

            var args = MlDigsiteBossMechanicRules.ParseArgs("count=3 count=9", errors);

            Assert.AreEqual(3, args.GetInt("count", -1));
            Assert.AreEqual(1, errors.Count);
        }

        [TestMethod]
        public void GetFractions_drops_out_of_range_entries_and_falls_back_when_nothing_survives()
        {
            var defaults = new[] { 0.9 };

            var good = MlDigsiteBossMechanicRules.ParseArgs("t=0.85,0.50,0.25");
            CollectionAssert.AreEqual(new[] { 0.85, 0.50, 0.25 }, good.GetFractions("t", defaults).ToArray());

            // 0 and 1.5 are outside (0, 1]; 1.0 is inside it
            var mixed = MlDigsiteBossMechanicRules.ParseArgs("t=0,1.5,1.0,abc,0.4");
            CollectionAssert.AreEqual(new[] { 1.0, 0.4 }, mixed.GetFractions("t", defaults).ToArray());

            var junk = MlDigsiteBossMechanicRules.ParseArgs("t=abc,,9");
            CollectionAssert.AreEqual(defaults, junk.GetFractions("t", defaults).ToArray(),
                "a wholly mis-typed list falls back rather than silently disabling the mechanic");
        }

        // ---- the set table -------------------------------------------------------------------------------

        [TestMethod]
        public void The_shipped_set_table_is_exactly_the_five_sets_the_tester_specified()
        {
            var errors = new List<string>();
            var sets = MlDigsiteBossMechanicRules.ParseSets(MlDigsiteBossMechanicRules.DefaultSets, errors);

            Assert.AreEqual(0, errors.Count, string.Join(" | ", errors));
            Assert.AreEqual(5, sets.Count);

            // RoZ round 13, verbatim: "Set 1: main mechanic Volatile adds. Set 2: main Drum cadence,
            // secondary Volatile adds. Set 3: main Immune phases with adds, secondary Drum cadence. Set 4:
            // main Interrupt object, secondary Volatile adds. Set 5: main Shifting safe zones, secondary
            // Immune phase at 50% health only."
            var expected = new[]
            {
                (1, MlDigsiteMechanic.Volatile, MlDigsiteMechanic.None),
                (2, MlDigsiteMechanic.Drums, MlDigsiteMechanic.Volatile),
                (3, MlDigsiteMechanic.ImmunePhases, MlDigsiteMechanic.Drums),
                (4, MlDigsiteMechanic.Interrupt, MlDigsiteMechanic.Volatile),
                (5, MlDigsiteMechanic.SafeZones, MlDigsiteMechanic.Immune50),
            };

            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].Item1, sets[i].SetId, $"set at index {i}");
                Assert.AreEqual(expected[i].Item2, sets[i].Main, $"set {sets[i].SetId} main");
                Assert.AreEqual(expected[i].Item3, sets[i].Secondary, $"set {sets[i].SetId} secondary");
            }
        }

        [TestMethod]
        public void The_shipped_sets_scale_the_one_shared_add_weenie_per_set()
        {
            // Owner ruling 2026-09-20: ONE reused add weenie whose health and speed scale per set. The
            // volatile sets (1, 2, 4) want fast and frail; the immune-phase sets (3, 5) want something that
            // has to actually be killed.
            var sets = MlDigsiteBossMechanicRules.ParseSets(MlDigsiteBossMechanicRules.DefaultSets)
                .ToDictionary(s => s.SetId);

            foreach (var id in new[] { 1, 2, 4 })
            {
                Assert.AreEqual(0.25, sets[id].AddHealth, 0.0001, $"set {id} adds should be frail");
                Assert.IsTrue(sets[id].AddSpeed > 1.0, $"set {id} adds should be fast");
            }

            foreach (var id in new[] { 3, 5 })
            {
                Assert.IsTrue(sets[id].AddHealth > sets[1].AddHealth, $"set {id} adds gate an immunity and must be sturdier than a bomb");
                Assert.IsTrue(sets[id].AddSpeed < sets[1].AddSpeed, $"set {id} adds are a kill order, not a chase");
            }
        }

        [TestMethod]
        public void ParseSets_skips_a_malformed_record_and_keeps_the_rest()
        {
            var errors = new List<string>();

            var sets = MlDigsiteBossMechanicRules.ParseSets(
                "set=1 main=volatile; set=2 main=nonsense; main=drums; set=3 main=drums; set=3 main=safezones", errors);

            CollectionAssert.AreEqual(new[] { 1, 3 }, sets.Select(s => s.SetId).ToArray());
            Assert.AreEqual(3, errors.Count, string.Join(" | ", errors));
        }

        [TestMethod]
        public void ParseSets_returns_nothing_for_a_wholly_broken_table_so_the_caller_can_fall_back()
        {
            Assert.AreEqual(0, MlDigsiteBossMechanicRules.ParseSets(null).Count);
            Assert.AreEqual(0, MlDigsiteBossMechanicRules.ParseSets("   ").Count);
            Assert.AreEqual(0, MlDigsiteBossMechanicRules.ParseSets("this is not a table at all").Count);
        }

        [TestMethod]
        public void ParseSets_treats_an_unrecognised_secondary_as_none_rather_than_dropping_the_set()
        {
            var errors = new List<string>();

            var sets = MlDigsiteBossMechanicRules.ParseSets("set=1 main=volatile secondary=wibble", errors);

            Assert.AreEqual(1, sets.Count, "the set still runs its main mechanic");
            Assert.AreEqual(MlDigsiteMechanic.None, sets[0].Secondary);
            Assert.AreEqual(1, errors.Count);
        }

        [TestMethod]
        public void ClampAddScale_confines_a_typo_to_a_playable_range()
        {
            Assert.AreEqual(0.25, MlDigsiteBossMechanicRules.ClampAddScale(0.25), 0.0001);
            Assert.AreEqual(0.05, MlDigsiteBossMechanicRules.ClampAddScale(0.0), 0.0001, "a zero-health add would die before anyone saw it");
            Assert.AreEqual(10.0, MlDigsiteBossMechanicRules.ClampAddScale(1000.0), 0.0001, "an add must never be harder than the boss");
            Assert.AreEqual(1.0, MlDigsiteBossMechanicRules.ClampAddScale(double.NaN), 0.0001);
        }

        // ---- the roll ------------------------------------------------------------------------------------

        [TestMethod]
        public void RollSet_draws_uniformly_over_the_table_and_is_reproducible_for_a_pinned_seed()
        {
            var sets = MlDigsiteBossMechanicRules.ParseSets(MlDigsiteBossMechanicRules.DefaultSets);

            var first = Enumerable.Range(0, 40)
                .Select(_ => MlDigsiteBossMechanicRules.RollSet(sets, 0, null)).ToList();

            Assert.IsTrue(first.All(s => s != null));

            // every set is reachable, which is the "random each time, reused across bosses" half of the ask
            var seen = new HashSet<int>();

            var rng = new Random(20260920);

            for (var i = 0; i < 500; i++)
                seen.Add(MlDigsiteBossMechanicRules.RollSet(sets, 0, rng).Value.SetId);

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4, 5 }, seen.ToArray());

            // and a pinned seed reproduces its own sequence exactly
            var a = Enumerable.Range(0, 20).Select(_ => MlDigsiteBossMechanicRules.RollSet(sets, 0, new Random(7)).Value.SetId).ToArray();
            var b = Enumerable.Range(0, 20).Select(_ => MlDigsiteBossMechanicRules.RollSet(sets, 0, new Random(7)).Value.SetId).ToArray();

            CollectionAssert.AreEqual(a, b);
        }

        [TestMethod]
        public void RollSet_honours_a_forced_set_id_and_ignores_one_the_table_does_not_carry()
        {
            var sets = MlDigsiteBossMechanicRules.ParseSets(MlDigsiteBossMechanicRules.DefaultSets);

            for (var id = 1; id <= 5; id++)
            {
                for (var i = 0; i < 10; i++)
                    Assert.AreEqual(id, MlDigsiteBossMechanicRules.RollSet(sets, id, new Random(i)).Value.SetId);
            }

            // a stale pin must never stop Boss Rush having a mechanic
            Assert.IsNotNull(MlDigsiteBossMechanicRules.RollSet(sets, 99, new Random(1)));
        }

        [TestMethod]
        public void RollSet_returns_null_only_for_an_empty_table()
        {
            Assert.IsNull(MlDigsiteBossMechanicRules.RollSet(null, 0, null));
            Assert.IsNull(MlDigsiteBossMechanicRules.RollSet(new List<MlDigsiteMechanicSet>(), 0, null));
        }

        // ---- cadence -------------------------------------------------------------------------------------

        [TestMethod]
        public void CadenceSeconds_slows_a_secondary_slot_and_leaves_a_main_alone()
        {
            Assert.AreEqual(20.0, MlDigsiteBossMechanicRules.CadenceSeconds(20.0, MlDigsiteMechanicSlot.Main, 2.0), 0.0001);
            Assert.AreEqual(40.0, MlDigsiteBossMechanicRules.CadenceSeconds(20.0, MlDigsiteMechanicSlot.Secondary, 2.0), 0.0001);
        }

        [TestMethod]
        public void CadenceSeconds_floors_at_the_one_second_driver_tick()
        {
            // The driver runs on the digsite's existing 1 s tick; anything finer is not expressible and a 0
            // would fire the mechanic on every tick.
            Assert.AreEqual(1.0, MlDigsiteBossMechanicRules.CadenceSeconds(0.0, MlDigsiteMechanicSlot.Main, 2.0), 0.0001);
            Assert.AreEqual(2.0, MlDigsiteBossMechanicRules.CadenceSeconds(-5.0, MlDigsiteMechanicSlot.Secondary, 2.0), 0.0001,
                "the floor is applied to the authored value first, and the secondary multiplier then rides on top of it");
            Assert.AreEqual(1.0, MlDigsiteBossMechanicRules.CadenceSeconds(double.NaN, MlDigsiteMechanicSlot.Main, 2.0), 0.0001);
            Assert.AreEqual(20.0, MlDigsiteBossMechanicRules.CadenceSeconds(20.0, MlDigsiteMechanicSlot.Secondary, 0.0), 0.0001,
                "a nonsense multiplier falls back to 1x rather than collapsing the cadence");
        }

        // ---- immune thresholds ---------------------------------------------------------------------------

        [TestMethod]
        public void ImmuneThresholds_reads_the_authored_list_descending()
        {
            var args = MlDigsiteBossMechanicRules.ParseArgs("thresholds=0.25,0.85,0.50");

            CollectionAssert.AreEqual(new[] { 0.85, 0.50, 0.25 },
                MlDigsiteBossMechanicRules.ImmuneThresholds(MlDigsiteMechanic.ImmunePhases, args).ToArray(),
                "the driver fires the highest threshold the boss has fallen below first");
        }

        [TestMethod]
        public void ImmuneThresholds_narrows_an_immune50_slot_to_half_health_whatever_the_string_says()
        {
            // The tester's set 5 secondary is "Immune phase at 50% health only".
            var args = MlDigsiteBossMechanicRules.ParseArgs("thresholds=0.85,0.50,0.25");

            CollectionAssert.AreEqual(new[] { 0.50 },
                MlDigsiteBossMechanicRules.ImmuneThresholds(MlDigsiteMechanic.Immune50, args).ToArray());
        }

        [TestMethod]
        public void ImmuneThresholds_falls_back_to_the_shipped_three_when_nothing_parses()
        {
            CollectionAssert.AreEqual(MlDigsiteBossMechanicRules.DefaultImmuneThresholds.ToArray(),
                MlDigsiteBossMechanicRules.ImmuneThresholds(MlDigsiteMechanic.ImmunePhases,
                    MlDigsiteBossMechanicRules.ParseArgs("thresholds=nonsense")).ToArray());
        }

        [TestMethod]
        public void The_shipped_immune_string_is_the_testers_own_85_50_25()
        {
            var args = MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultImmune);

            CollectionAssert.AreEqual(new[] { 0.85, 0.50, 0.25 },
                MlDigsiteBossMechanicRules.ImmuneThresholds(MlDigsiteMechanic.ImmunePhases, args).ToArray());

            Assert.AreEqual(4, args.GetInt("adds", -1));
            Assert.IsTrue(args.GetDouble("timeout", -1) > 0.0, "the failsafe must exist or a lost add hard-locks the fight");
        }

        // ---- the milestone-line suppression (owner ruling Q3) --------------------------------------------

        [TestMethod]
        public void ImmuneCoversMilestone_suppresses_the_existing_50_percent_line_only_for_a_set_that_goes_immune_there()
        {
            var args = MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultImmune);
            var sets = MlDigsiteBossMechanicRules.ParseSets(MlDigsiteBossMechanicRules.DefaultSets).ToDictionary(s => s.SetId);

            // set 3: main=immunephases, thresholds 85/50/25
            Assert.IsTrue(MlDigsiteBossMechanicRules.ImmuneCoversMilestone(sets[3], args, 50));
            Assert.IsTrue(MlDigsiteBossMechanicRules.ImmuneCoversMilestone(sets[3], args, 25));
            Assert.IsFalse(MlDigsiteBossMechanicRules.ImmuneCoversMilestone(sets[3], args, 75),
                "75 is a milestone with no immune phase behind it and must still be announced");

            // set 5: secondary=immune50, so ONLY 50 is covered - 25 must still announce
            Assert.IsTrue(MlDigsiteBossMechanicRules.ImmuneCoversMilestone(sets[5], args, 50));
            Assert.IsFalse(MlDigsiteBossMechanicRules.ImmuneCoversMilestone(sets[5], args, 25));

            // sets with no immune mechanic at all suppress nothing
            foreach (var id in new[] { 1, 2, 4 })
            {
                foreach (var pct in new[] { 75, 50, 25 })
                    Assert.IsFalse(MlDigsiteBossMechanicRules.ImmuneCoversMilestone(sets[id], args, pct), $"set {id} at {pct}%");
            }
        }

        // ---- the interrupt distance clamp (design risk R8) -----------------------------------------------

        [TestMethod]
        public void ClampInterruptDistance_keeps_the_object_strictly_inside_the_tether()
        {
            // At or past ml_digsite_boss_tether_radius, running to the object pulls the boss off its leash and
            // the interrupt becomes a reset button.
            Assert.AreEqual(30.0, MlDigsiteBossMechanicRules.ClampInterruptDistance(30.0, 40.0), 0.0001);
            Assert.AreEqual(35.0, MlDigsiteBossMechanicRules.ClampInterruptDistance(40.0, 40.0), 0.0001);
            Assert.AreEqual(35.0, MlDigsiteBossMechanicRules.ClampInterruptDistance(999.0, 40.0), 0.0001);
            Assert.AreEqual(5.0, MlDigsiteBossMechanicRules.ClampInterruptDistance(1.0, 40.0), 0.0001);
        }

        [TestMethod]
        public void ClampInterruptDistance_survives_a_tether_too_small_to_hold_both_ends()
        {
            Assert.AreEqual(4.0, MlDigsiteBossMechanicRules.ClampInterruptDistance(30.0, 8.0), 0.0001, "half the tether");
            Assert.IsTrue(MlDigsiteBossMechanicRules.ClampInterruptDistance(30.0, 0.0) > 0.0, "a disabled tether must not produce a zero distance");
            Assert.IsTrue(MlDigsiteBossMechanicRules.ClampInterruptDistance(double.NaN, 40.0) >= 5.0);
        }

        [TestMethod]
        public void The_shipped_interrupt_distance_is_inside_the_shipped_tether()
        {
            // The two shipped defaults must agree with each other out of the box, not only after a clamp.
            var args = MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultInterrupt);

            var distance = args.GetDouble("distance", -1);

            Assert.IsTrue(distance > 0.0, "the shipped string must name a distance");
            Assert.IsTrue(distance < 40.0, $"the shipped distance {distance} must be inside the shipped ml_digsite_boss_tether_radius of 40");
            Assert.AreEqual(distance, MlDigsiteBossMechanicRules.ClampInterruptDistance(distance, 40.0), 0.0001,
                "the shipped default must survive its own clamp unchanged");
        }

        [TestMethod]
        public void The_shipped_interrupt_miss_is_survivable_alone()
        {
            // Owner ruling 2026-09-20: "A missed interrupt is survivable: 35% of max health, non-lethal."
            var args = MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultInterrupt);

            Assert.AreEqual(0.35, args.GetDouble("damage", -1), 0.0001);
        }

        // ---- the safe-zone forgiveness rule ---------------------------------------------------------------

        [TestMethod]
        public void MissIsLethal_forgives_exactly_the_first_two_misses_and_kills_on_the_third()
        {
            // "give players room for 2 mistakes on instant-death hits" (RoZ round 13).
            Assert.IsFalse(MlDigsiteBossMechanicRules.MissIsLethal(0, 2), "first miss");
            Assert.IsFalse(MlDigsiteBossMechanicRules.MissIsLethal(1, 2), "second miss");
            Assert.IsTrue(MlDigsiteBossMechanicRules.MissIsLethal(2, 2), "third miss");
            Assert.IsTrue(MlDigsiteBossMechanicRules.MissIsLethal(9, 2));

            // forgiveness=0 is "every miss kills", which is a legal authoring choice
            Assert.IsTrue(MlDigsiteBossMechanicRules.MissIsLethal(0, 0));
        }

        [TestMethod]
        public void NonLethalDamage_caps_at_one_point_short_of_current_health_by_construction()
        {
            // The guarantee has to hold at any gear level and any boss level, which is why it is a cap on the
            // write rather than a number somebody tuned.
            Assert.AreEqual(999u, MlDigsiteBossMechanicRules.NonLethalDamage(5000, 1000));
            Assert.AreEqual(400u, MlDigsiteBossMechanicRules.NonLethalDamage(400, 1000), "a hit that was never lethal is not changed");
            Assert.AreEqual(999u, MlDigsiteBossMechanicRules.NonLethalDamage(1000, 1000), "exactly lethal is still capped");
            Assert.AreEqual(0u, MlDigsiteBossMechanicRules.NonLethalDamage(5000, 1), "a player already at 1 takes nothing");
            Assert.AreEqual(0u, MlDigsiteBossMechanicRules.NonLethalDamage(5000, 0));
        }

        [TestMethod]
        public void MissWarning_counts_the_misses_left_until_death_not_the_ones_still_forgiven()
        {
            // THE OFF-BY-ONE THIS PINS. With forgiveness=2 a player who has just taken their SECOND miss has
            // no forgiveness left at all - the next one kills - and the old wording told them they had one
            // more to spend. Design section 5.5 is the authority: "Two more like that and you are finished."
            // / "...One more." The count includes the lethal miss, because that is the one the player has to
            // plan around.
            Assert.AreEqual("The stone grinds past you. Two more like that and you are finished.",
                MlDigsiteBossMechanicRules.MissWarning(1, 2), "first miss of a forgiveness=2 fight");
            Assert.AreEqual("The stone grinds past you. One more like that and you are finished.",
                MlDigsiteBossMechanicRules.MissWarning(2, 2), "second miss: the next one is lethal");

            // miss 3 is lethal and never warned (SafeZonesMechanic only warns on a forgiven miss), so this is
            // the degenerate guard rather than a line a player sees
            Assert.AreEqual("The stone grinds past you. The next one will not.",
                MlDigsiteBossMechanicRules.MissWarning(3, 2));

            // and the whole ladder lines up for a retuned forgiveness
            StringAssert.Contains(MlDigsiteBossMechanicRules.MissWarning(1, 4), "4 more");
            StringAssert.Contains(MlDigsiteBossMechanicRules.MissWarning(2, 4), "3 more");
            StringAssert.Contains(MlDigsiteBossMechanicRules.MissWarning(3, 4), "Two more");
            StringAssert.Contains(MlDigsiteBossMechanicRules.MissWarning(4, 4), "One more");

            // the warning and the lethality rule have to agree about which miss is the last forgiven one
            for (var forgiveness = 0; forgiveness <= 4; forgiveness++)
            {
                for (var before = 0; before < forgiveness; before++)
                {
                    Assert.IsFalse(MlDigsiteBossMechanicRules.MissIsLethal(before, forgiveness));

                    var expectedLeft = forgiveness - before;
                    var warning = MlDigsiteBossMechanicRules.MissWarning(before + 1, forgiveness);

                    Assert.IsTrue(expectedLeft == 1
                        ? warning.Contains("One more")
                        : expectedLeft == 2 ? warning.Contains("Two more") : warning.Contains($"{expectedLeft} more"),
                        $"forgiveness={forgiveness}, {before} misses before this one: '{warning}'");
                }
            }

            foreach (var line in new[] { MlDigsiteBossMechanicRules.MissWarning(1, 2), MlDigsiteBossMechanicRules.MissWarning(2, 2) })
            {
                Assert.IsFalse(line.IndexOf((char)0x2013) >= 0 || line.IndexOf((char)0x2014) >= 0, "player-facing text is ASCII hyphens only");
            }
        }

        // ---- the drum cadence's shapes --------------------------------------------------------------------

        [TestMethod]
        public void RollBeats_only_ever_rolls_one_two_or_three()
        {
            var rng = new Random(4242);
            var seen = new HashSet<int>();

            for (var i = 0; i < 2000; i++)
            {
                var beats = MlDigsiteBossMechanicRules.RollBeats(rng);

                Assert.IsTrue(beats >= 1 && beats <= 3, $"rolled {beats}");
                seen.Add(beats);
            }

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, seen.ToArray(), "all three beat counts must be reachable");
        }

        [TestMethod]
        public void ShapeForBeats_maps_the_beat_count_to_the_shape_the_tester_named()
        {
            // "the count triggers a different attack (for example a ring, a wall or a ball volley)".
            Assert.AreEqual(MlDigsiteDrumShape.Ring, MlDigsiteBossMechanicRules.ShapeForBeats(1));
            Assert.AreEqual(MlDigsiteDrumShape.Wall, MlDigsiteBossMechanicRules.ShapeForBeats(2));
            Assert.AreEqual(MlDigsiteDrumShape.Volley, MlDigsiteBossMechanicRules.ShapeForBeats(3));
        }

        [TestMethod]
        public void InsideWall_measures_a_real_perpendicular_distance_not_a_distance_to_a_marker()
        {
            // heading due north (+Y), a 6 m wide band through the origin, unbounded along its own axis
            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 0, 50, 6, 0), "far along an unbounded line is still on the line");
            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 2.9, -30, 6, 0), "just inside the band");
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 3.1, 0, 6, 0), "just outside the band");
            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 3.0, 0, 6, 0), "exactly on the edge is inside");

            // a diagonal heading, to prove the normal is computed rather than assumed axis-aligned
            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideWall(0, 0, 1, 1, 10, 10, 6, 0));
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideWall(0, 0, 1, 1, 10, -10, 6, 0));

            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 0, 0, 0, 6, 0), "a zero heading hits nobody");
        }

        [TestMethod]
        public void InsideWall_is_bounded_along_its_own_axis_to_the_length_that_was_DRAWN()
        {
            // THE DEFECT THIS PINS. The telegraph draws the wall as a span of width * WallLengthFactor end to
            // end; the resolve used to apply an unbounded band, so the wall hit everyone on an infinite line
            // through the boss - roughly three times further than any marker stood. A player who read the
            // telegraph correctly and stood past its end was hit by a shape they were never shown.
            const double width = 6.0;
            var length = width * MlDigsiteBossMechanicRules.WallLengthFactor;

            Assert.AreEqual(24.0, length, 0.0001, "the shipped 6 m wall is drawn 24 m end to end");

            // heading due north, so "along the axis" is Y
            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 0, 10, width, length),
                "10 m along a 24 m wall is inside it");
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 0, 30, width, length),
                "30 m along is past the end of a 24 m wall and must not be hit");

            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 0, 12, width, length), "the end itself is inside");
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 0, 12.1, width, length), "just past it is not");
            Assert.IsTrue(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 0, -12, width, length), "and it is centred, so both ends bound");
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 0, -12.1, width, length));

            // the perpendicular bound still applies inside the length
            Assert.IsFalse(MlDigsiteBossMechanicRules.InsideWall(0, 0, 0, 1, 3.1, 5, width, length));
        }

        // ---- the add health scale ------------------------------------------------------------------------

        [TestMethod]
        public void ScaledAddHealth_scales_DOWN_which_is_the_whole_reason_it_exists()
        {
            // WorldEventSpawner.ScaledStartingValue returns its input unchanged for any multiplier at or below
            // 1.0, and every volatile set's addhp is below 1.0 by design.
            Assert.AreEqual(250u, MlDigsiteBossMechanicRules.ScaledAddHealth(1000, 1000, 0.25));
            Assert.AreEqual(2000u, MlDigsiteBossMechanicRules.ScaledAddHealth(1000, 1000, 2.0), "and it still scales up");
            Assert.AreEqual(1000u, MlDigsiteBossMechanicRules.ScaledAddHealth(1000, 1000, 1.0), "1.0 is a no-op");
        }

        [TestMethod]
        public void ScaledAddHealth_preserves_the_gap_between_starting_value_and_max()
        {
            // MaxValue carries an endurance-derived part on top of StartingValue; the scale works in the DELTA
            // so that part survives rather than being recomputed away.
            const uint starting = 800;
            const uint max = 1000;

            var scaled = MlDigsiteBossMechanicRules.ScaledAddHealth(starting, max, 0.5);

            Assert.AreEqual(300u, scaled, "max wants to be 500, which is 500 less than 1000, so starting drops by 500");
        }

        [TestMethod]
        public void ScaledAddHealth_floors_at_one_and_never_wraps()
        {
            Assert.AreEqual(1u, MlDigsiteBossMechanicRules.ScaledAddHealth(100, 10000, 0.01),
                "an add that cannot be seen before it dies is worse than no add");
            Assert.AreEqual(uint.MaxValue, MlDigsiteBossMechanicRules.ScaledAddHealth(uint.MaxValue - 5, 1000, 10.0));
            Assert.AreEqual(500u, MlDigsiteBossMechanicRules.ScaledAddHealth(500, 0, 0.25), "no base max, no change");
            Assert.AreEqual(500u, MlDigsiteBossMechanicRules.ScaledAddHealth(500, 1000, double.NaN));
            Assert.AreEqual(500u, MlDigsiteBossMechanicRules.ScaledAddHealth(500, 1000, -1.0));
        }

        // ---- every shipped string parses -----------------------------------------------------------------

        [TestMethod]
        public void Every_shipped_mechanic_string_parses_with_no_errors_and_names_its_own_cadence()
        {
            // These ship as strings, so nothing but a test catches a typo before a live boss silently fights
            // without the mechanic - the same argument MlDigsiteBossRushOverlayTests makes for
            // ml_digsite_bossrush_mechanic.
            var strings = new Dictionary<string, string>
            {
                { "volatile", MlDigsiteBossMechanicRules.DefaultVolatile },
                { "drums", MlDigsiteBossMechanicRules.DefaultDrums },
                { "interrupt", MlDigsiteBossMechanicRules.DefaultInterrupt },
                { "safezones", MlDigsiteBossMechanicRules.DefaultSafeZones },
            };

            foreach (var pair in strings)
            {
                var errors = new List<string>();
                var args = MlDigsiteBossMechanicRules.ParseArgs(pair.Value, errors);

                Assert.AreEqual(0, errors.Count, $"{pair.Key}: {string.Join(" | ", errors)}");
                Assert.IsTrue(args.GetDouble("every", -1) >= 1.0, $"{pair.Key} must name a cadence of at least the 1 s driver tick");
            }

            // the immune string is threshold-driven rather than cadence-driven and is checked separately
            var immuneErrors = new List<string>();
            MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultImmune, immuneErrors);
            Assert.AreEqual(0, immuneErrors.Count, string.Join(" | ", immuneErrors));
        }

        [TestMethod]
        public void Every_shipped_damage_number_is_positive_so_no_mechanic_ships_inert()
        {
            // "New settings ship at their designed value, never 0."
            Assert.IsTrue(MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultVolatile).GetDouble("damage", 0) > 0);
            Assert.IsTrue(MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultDrums).GetDouble("damage", 0) > 0);
            Assert.IsTrue(MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultSafeZones).GetDouble("damage", 0) > 0);
            Assert.IsTrue(MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultInterrupt).GetDouble("damage", 0) > 0);
        }

        // ---- the one damage convention ---------------------------------------------------------------------

        [TestMethod]
        public void ResolveDamage_reads_a_value_at_or_below_one_as_a_fraction_of_max_health()
        {
            // ONE convention for the whole feature, so an operator retuning a number live cannot misread its
            // units. At or below 1.0 is a fraction of the hit player's own max health; above it is flat.
            Assert.AreEqual(3500.0, MlDigsiteBossMechanicRules.ResolveDamage(0.35, 10000), 0.0001, "the interrupt's shipped 35 percent");
            Assert.AreEqual(10000.0, MlDigsiteBossMechanicRules.ResolveDamage(1.0, 10000), 0.0001, "1.0 is the whole health bar");
            Assert.AreEqual(900.0, MlDigsiteBossMechanicRules.ResolveDamage(900, 10000), 0.0001, "above 1.0 is a flat number, unscaled");
            Assert.AreEqual(1.0001, MlDigsiteBossMechanicRules.ResolveDamage(1.0001, 10000), 0.0001, "and the boundary is strict");

            Assert.AreEqual(0.0, MlDigsiteBossMechanicRules.ResolveDamage(0.0, 10000), 0.0001);
            Assert.AreEqual(0.0, MlDigsiteBossMechanicRules.ResolveDamage(-5, 10000), 0.0001);
            Assert.AreEqual(0.0, MlDigsiteBossMechanicRules.ResolveDamage(double.NaN, 10000), 0.0001);
            Assert.AreEqual(0.0, MlDigsiteBossMechanicRules.ResolveDamage(0.5, 0), 0.0001, "a player with no max health takes nothing");
        }

        [TestMethod]
        public void The_shipped_safe_zone_hit_is_lethal_before_forgiveness_caps_it()
        {
            // "give players room for 2 mistakes on INSTANT-DEATH hits" (RoZ round 13). A cap on a survivable
            // number is a decoration, so the raw hit has to be one that would kill: damage=1.0, the player's
            // whole max health. This is the assertion that makes the tester's sentence literally true.
            var authored = MlDigsiteBossMechanicRules.ParseArgs(MlDigsiteBossMechanicRules.DefaultSafeZones).GetDouble("damage", 0);

            const uint maxHealth = 4200;

            var raw = MlDigsiteBossMechanicRules.ResolveDamage(authored, maxHealth);

            Assert.IsTrue(raw >= maxHealth, $"a raw safe-zone miss ({raw}) must be able to kill a player at full health ({maxHealth})");

            // and the first misses survive it only because the cap is applied by construction
            Assert.AreEqual(maxHealth - 1, MlDigsiteBossMechanicRules.NonLethalDamage((uint)raw, maxHealth));
        }

        // ---- the boss's monster-effect overlay ---------------------------------------------------------------

        [TestMethod]
        public void ComposeBossOverlay_adds_the_immune_record_for_exactly_the_sets_that_need_it()
        {
            // THE DEFECT THIS PINS. ImmunePhasesMechanic sets Creature.P_DigsiteImmune, and the only thing
            // that reads it is the "immune" monster-effect record. Without the record composed onto the boss
            // at spawn, the phase is a chat line and a shield script over a boss that takes full damage
            // throughout - and nothing in the driver would say so.
            const string authored = "ward on=hpbelow trigger=0.5 pcthp=0.2 secs=10";

            var immuneSet = new MlDigsiteMechanicSet(3, MlDigsiteMechanic.ImmunePhases, MlDigsiteMechanic.Drums, 0.4, 1.1);
            var immune50Set = new MlDigsiteMechanicSet(5, MlDigsiteMechanic.SafeZones, MlDigsiteMechanic.Immune50, 0.4, 1.1);
            var plainSet = new MlDigsiteMechanicSet(1, MlDigsiteMechanic.Volatile, MlDigsiteMechanic.None, 0.25, 1.3);

            Assert.AreEqual(authored + "; immune", MlDigsiteBossMechanicRules.ComposeBossOverlay(authored, immuneSet),
                "an immune main keeps the authored overlay and adds the filter record");
            Assert.AreEqual(authored + "; immune", MlDigsiteBossMechanicRules.ComposeBossOverlay(authored, immune50Set),
                "and so does an immune50 secondary - both slots share one phase state");

            Assert.AreEqual(authored, MlDigsiteBossMechanicRules.ComposeBossOverlay(authored, plainSet),
                "a set with no immune mechanic carries exactly what it carried before the mechanics landed");
            Assert.AreEqual(authored, MlDigsiteBossMechanicRules.ComposeBossOverlay(authored, null),
                "and so does a Boss Rush with no driver state at all");

            // the grammar survives the edges: a blank overlay, and one already ending in the separator
            Assert.AreEqual("immune", MlDigsiteBossMechanicRules.ComposeBossOverlay("", immuneSet));
            Assert.AreEqual("immune", MlDigsiteBossMechanicRules.ComposeBossOverlay(null, immuneSet));
            Assert.AreEqual("ward secs=10; immune", MlDigsiteBossMechanicRules.ComposeBossOverlay("ward secs=10; ", immuneSet));
        }

        [TestMethod]
        public void Every_shipped_set_that_runs_an_immune_mechanic_is_recognised_as_needing_the_record()
        {
            // Read off the shipped table rather than hand-listed, so a sixth set authored with an immune slot
            // cannot be added without this staying true.
            var sets = MlDigsiteBossMechanicRules.ParseSets(MlDigsiteBossMechanicRules.DefaultSets);

            var needing = sets.Where(s => MlDigsiteBossMechanicRules.NeedsImmuneRecord(s)).Select(s => s.SetId).ToList();

            CollectionAssert.AreEquivalent(new[] { 3, 5 }, needing,
                "sets 3 (immunephases) and 5 (immune50) are the two that need the filter record");

            foreach (var set in sets)
            {
                var overlay = MlDigsiteBossMechanicRules.ComposeBossOverlay("ward secs=10", set);

                Assert.AreEqual(MlDigsiteBossMechanicRules.NeedsImmuneRecord(set), overlay.Contains("immune"),
                    $"set {set.SetId} composes the record if and only if it needs it");
            }
        }

        // ---- the interrupt countdown -------------------------------------------------------------------------

        [TestMethod]
        public void CountdownMarkDue_latches_at_or_below_the_mark_so_a_starved_tick_cannot_skip_a_reminder()
        {
            // The driver tick is nominally 1 s, but a stalled landblock or a long reap can take a window from
            // 7 s left straight to 5 s. An equality test dropped that window's 6 s reminder entirely -
            // silently, and precisely when the server is already struggling.
            Assert.IsFalse(MlDigsiteBossMechanicRules.CountdownMarkDue(7, 6), "not yet");
            Assert.IsTrue(MlDigsiteBossMechanicRules.CountdownMarkDue(6, 6), "on the mark");
            Assert.IsTrue(MlDigsiteBossMechanicRules.CountdownMarkDue(5, 6), "and past it, which is the jump case");
            Assert.IsTrue(MlDigsiteBossMechanicRules.CountdownMarkDue(1, 3));

            Assert.IsFalse(MlDigsiteBossMechanicRules.CountdownMarkDue(0, 3), "0 is the expiry's business, not a reminder's");
            Assert.IsFalse(MlDigsiteBossMechanicRules.CountdownMarkDue(-1, 3), "and -1 is 'no window open'");

            CollectionAssert.AreEqual(new[] { 6, 3 }, MlDigsiteBossMechanicRules.InterruptCountdownMarks.ToArray(),
                "descending, so a single tick that is past both reports both");
        }
    }
}
