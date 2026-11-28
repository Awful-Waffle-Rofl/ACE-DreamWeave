using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.BalanceTrials;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// The enemy rotation: the pure walk, the gem it produces, the start-time check and the status fragment. Inline fixtures
    /// only; reads no PropertyManager key and touches no process-static state.
    /// </summary>
    [TestClass]
    public class TrialRotationTests
    {
        private static readonly TrialGem Gem = new TrialGem(400, 8, "artifex_collegium", "any", Array.Empty<(string, double)>(), 1234, TrialSeedMode.Fixed);

        private static readonly IReadOnlyList<TrialRotationEntry> Four = new[]
        {
            new TrialRotationEntry("virindi"),
            new TrialRotationEntry("olthoi"),
            new TrialRotationEntry("lugian", "filos_doom"),
            new TrialRotationEntry("undead"),
        };

        [TestMethod]
        public void Ten_reps_over_four_entries_wrap_around()
        {
            var seq = Enumerable.Range(1, 10).Select(rep => TrialRotation.EntryIndex(Four, rep) + 1).ToArray();
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 1, 2, 3, 4, 1, 2 }, seq);
        }

        [TestMethod]
        public void Gem_for_replaces_the_family_and_the_dungeon_only_when_the_entry_sets_one()
        {
            var first = TrialRotation.GemFor(Gem, Four, 1);
            Assert.AreEqual("virindi", first.Family);
            Assert.AreEqual("artifex_collegium", first.Dungeon, "no dungeon on the entry keeps the gem's");

            var third = TrialRotation.GemFor(Gem, Four, 3);
            Assert.AreEqual("lugian", third.Family);
            Assert.AreEqual("filos_doom", third.Dungeon);

            Assert.AreEqual("virindi", TrialRotation.GemFor(Gem, Four, 5).Family, "the fifth rep wraps to the first entry");

            // Everything else about the gem is untouched.
            Assert.AreEqual(Gem with { Family = "undead" }, TrialRotation.GemFor(Gem, Four, 4));
        }

        [TestMethod]
        public void No_rotation_leaves_the_gem_alone()
        {
            Assert.AreSame(Gem, TrialRotation.GemFor(Gem, null, 7));
            Assert.AreSame(Gem, TrialRotation.GemFor(Gem, Array.Empty<TrialRotationEntry>(), 7));
            Assert.IsNull(TrialRotation.NextLabel(null, 1));
        }

        [TestMethod]
        public void The_rolled_spec_carries_the_entrys_family()
        {
            // Roll with a pinned (empty) mod list is the pure half: no store, no fragment.
            for (var rep = 1; rep <= 8; rep++)
            {
                var entry = Four[(rep - 1) % Four.Count];
                var roll = TrialGemRoller.Roll(TrialRotation.GemFor(Gem, Four, rep), rep, null, 0, null, null, default);
                Assert.AreEqual(entry.Family, roll.Spec.Family, $"rep {rep}");
                Assert.AreEqual(entry.Dungeon ?? Gem.Dungeon, roll.Spec.DungeonId, $"rep {rep}");
            }
        }

        [TestMethod]
        public void Next_label_names_the_entry_position_and_family()
        {
            Assert.AreEqual("next=1:virindi", TrialRotation.NextLabel(Four, 1));
            Assert.AreEqual("next=4:undead", TrialRotation.NextLabel(Four, 4));
            Assert.AreEqual("next=1:virindi", TrialRotation.NextLabel(Four, 5));
            Assert.AreEqual("next=2:olthoi", TrialRotation.NextLabel(Four, 10));
        }

        [TestMethod]
        public void A_rep_index_below_one_is_refused()
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TrialRotation.EntryIndex(Four, 0));
        }

        private static TrialScenario ScenarioWith(IReadOnlyList<TrialRotationEntry> rotation)
            => new TrialScenario("s", "r", Gem, 1, new[] { new TrialWave(ACE.Server.ThreadDungeons.DungeonRole.Trash, 6) }, "off", 10, 600, 0, null, rotation);

        private static string Check(TrialScenario scenario, Func<TrialGem, int, IReadOnlyList<string>> f, int start = 1) => TrialRotation.Precheck(scenario, f, out _, start);

        private static readonly IReadOnlyList<string> Good = Array.Empty<string>();

        [TestMethod]
        public void Start_check_passes_when_every_entry_builds_and_is_skipped_without_a_rotation()
        {
            var seen = new List<(string Family, int Index)>();

            Assert.IsNull(Check(ScenarioWith(Four), (g, k) => { seen.Add((g.Family, k)); return Good; }));
            CollectionAssert.AreEqual(new[] { ("virindi", 1), ("olthoi", 2), ("lugian", 3), ("undead", 4) }, seen, "Fixed seed: every distinct entry is built once, with its own gem");

            Assert.IsNull(Check(ScenarioWith(null), (g, k) => throw new InvalidOperationException("must not be asked")));
        }

        [TestMethod]
        public void Start_check_refuses_a_shortfall_naming_the_entry()
        {
            var refusal = Check(ScenarioWith(Four), (g, k) => g.Family == "lugian" ? new[] { "Trash: wanted 6, got 0" } : Good);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "rotation entry 3");
            StringAssert.Contains(refusal, "'lugian'");
            StringAssert.Contains(refusal, "level 400");
            StringAssert.Contains(refusal, "Trash: wanted 6, got 0");
        }

        [TestMethod]
        public void Start_check_treats_a_throwing_entry_as_a_shortfall()
        {
            var refusal = Check(ScenarioWith(Four), (g, k) => g.Family == "olthoi" ? throw new InvalidOperationException("dungeon 'x' is not in the dungeon store") : Good);

            StringAssert.Contains(refusal, "rotation entry 2");
            StringAssert.Contains(refusal, "not in the dungeon store");
        }

        private static IReadOnlyList<TrialRotationEntry> Nine => Enumerable.Range(1, 9).Select(i => new TrialRotationEntry("fam" + i)).ToArray();

        [TestMethod]
        public void Step_mode_checks_every_index_the_run_reaches_and_catches_a_late_failure()
        {
            var step = Gem with { SeedMode = TrialSeedMode.Step };
            var scenario = ScenarioWith(Nine) with { Gem = step, Reps = 10 };

            // Fails only at index 10 (entry 1 again): the first nine indices pass, so only a check of index 10 refuses.
            var refusal = Check(scenario, (g, k) => k == 10 ? new[] { "Trash: wanted 6, got 0" } : Good);

            Assert.IsNotNull(refusal);
            StringAssert.Contains(refusal, "rotation entry 1");
            StringAssert.Contains(refusal, "'fam1'");
            StringAssert.Contains(refusal, "rep index 10");
        }

        [TestMethod]
        public void A_resumed_start_index_checks_exactly_the_indices_the_run_will_use()
        {
            var step = ScenarioWith(Four) with { Gem = Gem with { SeedMode = TrialSeedMode.Step }, Reps = 3 };
            var seen = new List<int>();

            Assert.IsNull(Check(step, (g, k) => { seen.Add(k); return Good; }, start: 7));
            CollectionAssert.AreEqual(new[] { 7, 8, 9 }, seen);

            // Fixed seed: the same window, deduplicated by entry (7,8,9,10 -> entries 3,4,1,2; Reps 3 reaches 3 of them).
            seen.Clear();
            Assert.IsNull(Check(ScenarioWith(Four) with { Reps = 3 }, (g, k) => { seen.Add(k); return Good; }, start: 7));
            CollectionAssert.AreEqual(new[] { 7, 8, 9 }, seen);
        }

        [TestMethod]
        public void Fewer_reps_than_entries_do_not_check_the_entries_the_run_never_reaches()
        {
            var scenario = ScenarioWith(Four) with { Reps = 2 };

            Assert.IsNull(Check(scenario, (g, k) => g.Family == "lugian" ? new[] { "short" } : Good), "lugian is entry 3 and a 2-rep run never reaches it");
            Assert.IsNotNull(Check(scenario with { Reps = 3 }, (g, k) => g.Family == "lugian" ? new[] { "short" } : Good), "control: three reps reach it");
        }

        [TestMethod]
        public void A_long_step_run_checks_only_the_first_thirty_indices()
        {
            var scenario = ScenarioWith(Four) with { Gem = Gem with { SeedMode = TrialSeedMode.Step }, Reps = 1000 };
            var calls = 0;

            Assert.IsNull(Check(scenario, (g, k) => { calls++; return Good; }));
            Assert.AreEqual(30, calls);
            Assert.AreEqual(970, TrialRotation.UncheckedReps(scenario));

            Assert.AreEqual(0, TrialRotation.UncheckedReps(scenario with { Reps = 30 }), "a run of exactly 30 is fully checked");
            Assert.AreEqual(0, TrialRotation.UncheckedReps(scenario with { Gem = Gem }), "a Fixed seed is bounded by its entries, not by this cap");
            Assert.AreEqual(0, TrialRotation.UncheckedReps(scenario with { Rotation = null }), "no rotation, nothing to check");
        }

        [TestMethod]
        public void Fixed_seed_checks_a_repeated_entry_once()
        {
            var seen = new List<int>();
            Assert.IsNull(Check(ScenarioWith(Four) with { Reps = 10 }, (g, k) => { seen.Add(k); return Good; }));
            Assert.AreEqual(4, seen.Count, "ten reps over four entries build four plans, not ten");
        }

        [TestMethod]
        public void A_plan_that_drew_another_family_is_a_shortfall()
        {
            CollectionAssert.AreEqual(new string[0], TrialRotation.ShortfallLines(Good, "lugian", "lugian", new[] { "family: whatever" }).ToArray(), "a good plan adds nothing, notes included");

            var drawn = TrialRotation.ShortfallLines(Good, "lugian", "olthoi", null);
            CollectionAssert.AreEqual(new[] { "forced family not drawn (plan family: olthoi)" }, drawn.ToArray());

            var none = TrialRotation.ShortfallLines(Good, "lugian", null, null);
            CollectionAssert.AreEqual(new[] { "forced family not drawn (plan family: none)" }, none.ToArray());
        }

        [TestMethod]
        public void Shortfall_glue_appends_only_the_first_family_or_band_note()
        {
            var lines = TrialRotation.ShortfallLines(new[] { "Trash: wanted 6, got 0" }, "lugian", "lugian",
                new[] { "stretch: lifted", "band: level-400 band is empty", "family: second note", "family: third" });

            CollectionAssert.AreEqual(new[] { "Trash: wanted 6, got 0", "band: level-400 band is empty" }, lines.ToArray());

            var viaFamily = TrialRotation.ShortfallLines(new[] { "Trash: wanted 6, got 0" }, "lugian", null, new[] { "other", "family: no species table has a trash member" });
            CollectionAssert.AreEqual(new[] { "Trash: wanted 6, got 0", "forced family not drawn (plan family: none)", "family: no species table has a trash member" }, viaFamily.ToArray());
        }

        [TestMethod]
        public void The_refusal_text_is_capped_and_the_full_text_is_kept()
        {
            var longLine = new string('x', 600);
            var refusal = TrialRotation.Precheck(ScenarioWith(Four), (g, k) => new[] { longLine }, out var full);

            Assert.IsTrue(refusal.Length <= TrialRotation.MaxRefusalChars, refusal.Length.ToString());
            Assert.IsTrue("trial refused: ".Length + refusal.Length <= 255, "the chat line fits 255 characters");
            Assert.IsTrue(refusal.EndsWith("...", StringComparison.Ordinal));
            StringAssert.Contains(full, longLine);
            Assert.AreEqual("short", TrialRotation.Cap("short"));
        }

        [TestMethod]
        public void Status_names_the_next_rep_one_past_the_walk_while_a_rep_runs()
        {
            Assert.AreEqual(3, TrialRotation.NextRepIndex(3, TrialPhase.Ready));
            Assert.AreEqual(4, TrialRotation.NextRepIndex(3, TrialPhase.Running));
            Assert.AreEqual("next=4:undead", TrialRotation.NextLabel(Four, TrialRotation.NextRepIndex(3, TrialPhase.Running)));
            Assert.AreEqual("next=3:lugian", TrialRotation.NextLabel(Four, TrialRotation.NextRepIndex(3, TrialPhase.Ready)));
        }
    }
}
