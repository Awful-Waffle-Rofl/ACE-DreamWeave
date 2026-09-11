using System;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class DungeonGemSpecTests
    {
        private const string Unbound = "v1|dg=filos_doom|lvl=120|tier=6|fam=banderling|seed=48271|mods=hardy:1.6,savage:22";

        /// <summary>The same spec after the v1 -> v2 upgrade: the three phase 2 fields present and empty/zero.</summary>
        private const string UnboundV2 = "v2|dg=filos_doom|lvl=120|tier=6|fam=banderling|seed=48271|mods=hardy:1.6,savage:22|press=0|lock=|load=";

        /// <summary>A v2 spec with all three phase 2 fields populated.</summary>
        private const string FullV2 = "v2|dg=filos_doom|lvl=195|tier=7|fam=any|seed=0|mods=savage:17.5,precise:10|press=1|lock=savage|load=1650:2,1643:1";

        /// <summary>
        /// A REAL spec string observed on stage after the 2026-09-06 deploy, before the instability mechanic
        /// was removed. Every gem and fragment created there carries an inst= field like this one, so TryParse
        /// must go on reading it or those items become unreadable and the sweeper may destroy them.
        /// </summary>
        private const string LiveV2WithInst = "v2|dg=any|lvl=195|tier=7|fam=any|seed=544023030|mods=savage:17.5,precise:10|inst=32|press=1|lock=|load=";

        [TestMethod]
        public void Parses_an_unbound_spec()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(Unbound, out var spec, out var error), error);
            Assert.AreEqual("filos_doom", spec.DungeonId);
            Assert.AreEqual(120, spec.Level);
            Assert.AreEqual(6, spec.Tier);
            Assert.AreEqual("banderling", spec.Family);
            Assert.AreEqual(48271, spec.Seed);
            Assert.AreEqual(2, spec.Modifiers.Count);
            Assert.AreEqual("hardy", spec.Modifiers[0].Id);
            Assert.AreEqual(1.6, spec.Modifiers[0].Magnitude, 1e-9);
            Assert.AreEqual(22.0, spec.Modifiers[1].Magnitude, 1e-9);
            Assert.IsFalse(spec.IsBound);
        }

        [TestMethod]
        public void V1_parses_with_all_three_new_fields_empty()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(Unbound, out var spec, out var error), error);
            Assert.AreEqual(0, spec.Presses);
            Assert.AreEqual(0, spec.Locks.Count);
            Assert.AreEqual(0, spec.Load.Count);

            // The shipped placeholder on Content/sql/weenies/1003600 Thread Gem.sql:41, verbatim.
            const string shipped = "v1|dg=any|lvl=100|tier=6|fam=any|seed=1|mods=";
            Assert.IsTrue(DungeonGemSpec.TryParse(shipped, out var gem, out error), error);
            Assert.AreEqual(0, gem.Presses);
            Assert.AreEqual(0, gem.Locks.Count);
            Assert.AreEqual(0, gem.Load.Count);
        }

        [TestMethod]
        public void V1_parses_and_upgrades_to_v2()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(Unbound, out var spec, out var error), error);
            Assert.AreEqual(UnboundV2, spec.Serialize());
            Assert.IsTrue(DungeonGemSpec.TryParse(spec.Serialize(), out var again, out error), error);
            Assert.AreEqual(UnboundV2, again.Serialize());
        }

        [TestMethod]
        public void V2_round_trips_exactly()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(FullV2, out var spec, out var error), error);
            Assert.AreEqual(1, spec.Presses);
            CollectionAssert.AreEqual(new[] { "savage" }, spec.Locks.ToArray());
            Assert.AreEqual(2, spec.Load.Count);
            Assert.AreEqual(1650u, spec.Load[0].Wcid);
            Assert.AreEqual(2, spec.Load[0].Doses);
            Assert.AreEqual(1643u, spec.Load[1].Wcid);
            Assert.AreEqual(1, spec.Load[1].Doses);
            Assert.AreEqual(FullV2, spec.Serialize());

            // The same spec built through the constructor serializes to the identical string.
            var built = new DungeonGemSpec("filos_doom", 195, 7, "any", 0,
                new[] { ("savage", 17.5), ("precise", 10.0) }, 0, 0,
                presses: 1,
                locks: new[] { "savage" },
                load: new[] { (1650u, 2), (1643u, 1) });
            Assert.AreEqual(FullV2, built.Serialize());
        }

        /// <summary>
        /// Field order is irrelevant on input. The shuffled string deliberately still carries "inst=24", so
        /// this doubles as evidence that the retired field survives a parse in any position.
        /// </summary>
        [TestMethod]
        public void V2_field_order_in_input_is_irrelevant()
        {
            const string shuffled = "v2|load=1650:2,1643:1|tier=7|lock=savage|seed=0|lvl=195|press=1|mods=savage:17.5,precise:10|inst=24|fam=any|dg=filos_doom";
            Assert.IsTrue(DungeonGemSpec.TryParse(shuffled, out var spec, out var error), error);
            Assert.AreEqual(FullV2, spec.Serialize());
        }

        [TestMethod]
        public void Serialize_always_emits_the_three_fields_even_when_empty()
        {
            var spec = new DungeonGemSpec("any", 50, 3, "any", 7, Array.Empty<(string, double)>(), 0, 0);
            Assert.AreEqual("v2|dg=any|lvl=50|tier=3|fam=any|seed=7|mods=|press=0|lock=|load=", spec.Serialize());
        }

        // ---- inst= compatibility shim ---------------------------------------------------------------------

        /// <summary>
        /// THE COMPATIBILITY SHIM (see DungeonGemSpec's class header). The instability mechanic was removed by
        /// owner ruling on 2026-09-07 and Serialize stopped writing inst=, but stage was deployed on
        /// 2026-09-06 and every gem and fragment created there has an inst= field baked into its persisted
        /// DungeonGemSpec property. TryParse must therefore keep ACCEPTING the field and ignoring it: if it
        /// were dropped from the allowed-field set instead, each of those live items would fail to parse, read
        /// as unspecced, and become eligible for destruction by ThreadDungeonSweeper.
        ///
        /// This test replaces the deleted Instability_out_of_range_is_rejected, whose subject (the 0..100
        /// range check) no longer exists - the field is not read, so there is nothing to range-check.
        /// </summary>
        [TestMethod]
        public void A_v2_string_that_still_carries_inst_parses_and_the_field_is_ignored()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(LiveV2WithInst, out var spec, out var error),
                $"a live stage spec string must still parse; got: {error}");

            // Every other field survived intact - the shim drops inst= and nothing else.
            Assert.AreEqual("any", spec.DungeonId);
            Assert.AreEqual(195, spec.Level);
            Assert.AreEqual(7, spec.Tier);
            Assert.AreEqual(544023030, spec.Seed);
            Assert.AreEqual(2, spec.Modifiers.Count);
            Assert.AreEqual("savage", spec.Modifiers[0].Id);
            Assert.AreEqual(17.5, spec.Modifiers[0].Magnitude, 1e-9);
            Assert.AreEqual(1, spec.Presses);

            // Re-serializing drops the field, and the result parses again - so an item rewritten by this build
            // is readable, and reading it a third time is stable.
            var rewritten = spec.Serialize();
            Assert.IsFalse(rewritten.Contains("inst="), $"Serialize must no longer emit inst=; got: {rewritten}");
            Assert.AreEqual("v2|dg=any|lvl=195|tier=7|fam=any|seed=544023030|mods=savage:17.5,precise:10|press=1|lock=|load=", rewritten);
            Assert.IsTrue(DungeonGemSpec.TryParse(rewritten, out var again, out error), error);
            Assert.AreEqual(rewritten, again.Serialize());
        }

        /// <summary>
        /// The shim is deliberately unvalidated: the value is never read, so a value the old range check would
        /// have refused must NOT now make a live item unreadable. This is the discriminating half of the shim -
        /// without it, "accepted" could still mean "accepted only when it happens to be 0..100".
        /// </summary>
        [DataTestMethod]
        [DataRow("0")]
        [DataRow("100")]
        [DataRow("-1")]
        [DataRow("101")]
        [DataRow("abc")]
        [DataRow("")]
        public void Any_inst_value_at_all_is_accepted_and_ignored(string value)
        {
            var text = $"v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|inst={value}|press=0|lock=|load=";

            Assert.IsTrue(DungeonGemSpec.TryParse(text, out var spec, out var error),
                $"inst='{value}' must not make a spec unreadable; got: {error}");
            Assert.AreEqual("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|press=0|lock=|load=", spec.Serialize());
        }

        [TestMethod]
        public void Negative_press_count_is_rejected()
        {
            Assert.IsFalse(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|press=-1", out _, out var error));
            StringAssert.Contains(error, "press");
            StringAssert.Contains(error, ">= 0");

            Assert.IsTrue(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|press=0", out var zero, out var ok), ok);
            Assert.AreEqual(0, zero.Presses);
        }

        [TestMethod]
        public void Lock_naming_an_absent_modifier_is_rejected()
        {
            Assert.IsFalse(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=hardy:1.6|lock=savage", out _, out var error));
            StringAssert.Contains(error, "lock id 'savage' is not a present modifier");

            Assert.IsTrue(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=hardy:1.6", out var spec, out var ok), ok);
            Assert.ThrowsExactly<ArgumentException>(() => spec.WithLocks(new[] { "savage" }));
            Assert.ThrowsExactly<ArgumentException>(() =>
                new DungeonGemSpec("any", 50, 3, "any", 1, new[] { ("hardy", 1.6) }, 0, 0, locks: new[] { "savage" }));
        }

        [TestMethod]
        public void Duplicate_lock_id_is_rejected()
        {
            Assert.IsFalse(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=savage:1|lock=savage,savage", out _, out var error));
            StringAssert.Contains(error, "repeated");

            Assert.IsTrue(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=savage:1", out var spec, out var ok), ok);
            Assert.ThrowsExactly<ArgumentException>(() => spec.WithLocks(new[] { "savage", "savage" }));
        }

        [TestMethod]
        public void More_than_two_locks_is_rejected()
        {
            const string mods = "mods=savage:1,precise:2,hardy:3";
            Assert.IsTrue(DungeonGemSpec.TryParse($"v2|dg=any|lvl=50|tier=3|fam=any|seed=1|{mods}|lock=savage,precise", out var two, out var ok), ok);
            Assert.AreEqual(2, two.Locks.Count);

            Assert.IsFalse(DungeonGemSpec.TryParse($"v2|dg=any|lvl=50|tier=3|fam=any|seed=1|{mods}|lock=savage,precise,hardy", out _, out var error));
            StringAssert.Contains(error, "at most 2 lock ids");

            Assert.ThrowsExactly<ArgumentException>(() => two.WithLocks(new[] { "savage", "precise", "hardy" }));
        }

        [TestMethod]
        public void Load_entry_with_zero_wcid_is_rejected()
        {
            Assert.IsFalse(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|load=0:2", out _, out var error));
            StringAssert.Contains(error, "load wcid");
            StringAssert.Contains(error, "non-zero uint");
        }

        [DataTestMethod]
        [DataRow("1650:0")]
        [DataRow("1650:-1")]
        [DataRow("1650:100")]
        public void Load_entry_with_zero_or_negative_doses_is_rejected(string entry)
        {
            Assert.IsFalse(DungeonGemSpec.TryParse($"v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|load={entry}", out _, out var error));
            StringAssert.Contains(error, "load doses");
            StringAssert.Contains(error, "1..99");
        }

        [TestMethod]
        public void WithLoad_enforces_the_same_load_rule_as_TryParse()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=", out var spec, out var error), error);

            Assert.ThrowsExactly<ArgumentException>(() => spec.WithLoad(new[] { (1650u, DungeonGemSpec.MaxDoses + 1) }));
            Assert.ThrowsExactly<ArgumentException>(() => spec.WithLoad(new[] { (1650u, 0) }));
            Assert.ThrowsExactly<ArgumentException>(() => spec.WithLoad(new[] { (0u, 1) }));
            Assert.ThrowsExactly<ArgumentException>(() => spec.WithLoad(new[] { (1650u, 1), (1650u, 2) }));
            Assert.ThrowsExactly<ArgumentException>(() =>
                new DungeonGemSpec("any", 50, 3, "any", 1, Array.Empty<(string, double)>(), 0, 0,
                    load: new[] { (1650u, DungeonGemSpec.MaxDoses + 1) }));

            // The bound itself round-trips: Serialize can never emit a load entry TryParse refuses.
            var atBound = spec.WithLoad(new[] { (1650u, DungeonGemSpec.MaxDoses) });
            var text = atBound.Serialize();
            StringAssert.Contains(text, "|load=1650:99");
            Assert.IsTrue(DungeonGemSpec.TryParse(text, out var again, out error), error);
            Assert.AreEqual(DungeonGemSpec.MaxDoses, again.Load[0].Doses);
            Assert.AreEqual(text, again.Serialize());
        }

        [TestMethod]
        public void Duplicate_load_wcid_is_rejected()
        {
            Assert.IsFalse(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|load=1650:2,1650:1", out _, out var error));
            StringAssert.Contains(error, "load wcid '1650' repeated");
        }

        [TestMethod]
        public void Load_order_is_preserved_through_a_round_trip()
        {
            const string text = "v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|press=0|lock=|load=1653:1,1643:5,1650:2";
            Assert.IsTrue(DungeonGemSpec.TryParse(text, out var spec, out var error), error);
            CollectionAssert.AreEqual(new uint[] { 1653, 1643, 1650 }, spec.Load.Select(l => l.Wcid).ToArray());
            Assert.AreEqual(text, spec.Serialize());
        }

        [TestMethod]
        public void Unknown_field_is_still_rejected()
        {
            Assert.IsFalse(DungeonGemSpec.TryParse("v2|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|inst=0|press=0|lock=|load=|bogus=1", out _, out var error));
            StringAssert.Contains(error, "unknown field 'bogus'");
        }

        [TestMethod]
        public void Bound_v2_puts_run_and_owner_last()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(FullV2, out var spec, out var error), error);
            var bound = spec.WithBinding(0x80001234u, 0x50000001u);
            var text = bound.Serialize();
            Assert.AreEqual(FullV2 + "|run=2147488308|owner=1342177281", text);
            Assert.IsTrue(DungeonGemSpec.TryParse(text, out var again, out error), error);
            Assert.AreEqual(text, again.Serialize());
            Assert.AreEqual(1, again.Presses);
            Assert.AreEqual(1, again.Locks.Count);
            Assert.AreEqual(2, again.Load.Count);
        }

        [TestMethod]
        public void With_helpers_do_not_mutate_the_source()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(FullV2, out var spec, out var error), error);

            Assert.AreEqual(200, spec.WithLevel(200).Level);
            Assert.AreEqual(99, spec.WithSeed(99).Seed);
            Assert.AreEqual(4, spec.WithPresses(4).Presses);
            Assert.AreEqual(0, spec.WithLocks(null).Locks.Count);
            Assert.AreEqual(0, spec.WithLoad(null).Load.Count);
            Assert.IsTrue(spec.WithBinding(1u, 2u).IsBound);

            // WithModifiers drops a lock whose modifier is no longer present, and keeps the ones that survive.
            var reRolled = spec.WithModifiers(new[] { ("precise", 10.0) });
            Assert.AreEqual(0, reRolled.Locks.Count, "the savage lock must be dropped with its modifier");
            var kept = spec.WithModifiers(new[] { ("savage", 3.0), ("hardy", 1.1) });
            CollectionAssert.AreEqual(new[] { "savage" }, kept.Locks.ToArray());

            Assert.AreEqual(FullV2, spec.Serialize(), "no With* helper may mutate the source");
        }

        [TestMethod]
        public void Binding_adds_run_and_owner_and_round_trips()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse(Unbound, out var spec, out _));
            var bound = spec.WithBinding(0x80001234u, 0x50000001u);
            Assert.IsTrue(bound.IsBound);
            Assert.AreEqual(0x80001234u, bound.RunId);
            Assert.AreEqual(0x50000001u, bound.OwnerGuid);
            Assert.IsFalse(spec.IsBound, "WithBinding must not mutate the source");
            var text = bound.Serialize();
            StringAssert.EndsWith(text, "|run=2147488308|owner=1342177281");
            Assert.IsTrue(DungeonGemSpec.TryParse(text, out var again, out _));
            Assert.AreEqual(text, again.Serialize());
        }

        [TestMethod]
        public void Any_dungeon_and_any_family_are_legal()
        {
            Assert.IsTrue(DungeonGemSpec.TryParse("v1|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=", out var spec, out var error), error);
            Assert.AreEqual("any", spec.DungeonId);
            Assert.AreEqual("any", spec.Family);
            Assert.AreEqual(0, spec.Modifiers.Count);
        }

        [DataTestMethod]
        [DataRow("", "empty")]
        [DataRow("v3|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=", "version")]
        [DataRow("v0|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=", "version")]
        [DataRow("v1|dg=any|lvl=0|tier=3|fam=any|seed=1|mods=", "lvl")]
        [DataRow("v1|dg=any|lvl=50|tier=9|fam=any|seed=1|mods=", "tier")]
        [DataRow("v1|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=hardy", "mods")]
        [DataRow("v1|dg=any|lvl=50|tier=3|fam=any|seed=1|mods=|bogus=1", "bogus")]
        [DataRow("v1|dg=any|lvl=50|tier=3|fam=any|mods=", "seed")]
        [DataRow("v1|dg=Filos Doom|lvl=50|tier=3|fam=any|seed=1|mods=", "dg")]
        public void Rejects_malformed_specs(string text, string expectedErrorFragment)
        {
            Assert.IsFalse(DungeonGemSpec.TryParse(text, out _, out var error));
            StringAssert.Contains(error, expectedErrorFragment);
        }

        [TestMethod]
        public void Magnitude_formatting_is_invariant()
        {
            var spec = new DungeonGemSpec("any", 50, 3, "any", 7, new[] { ("hardy", 1.25), ("savage", 30.0) }, 0, 0);
            Assert.AreEqual("v2|dg=any|lvl=50|tier=3|fam=any|seed=7|mods=hardy:1.25,savage:30|press=0|lock=|load=", spec.Serialize());
        }
    }
}
