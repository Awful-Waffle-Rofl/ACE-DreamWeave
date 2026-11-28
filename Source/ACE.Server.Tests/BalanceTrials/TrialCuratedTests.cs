using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.BalanceTrials;
using ACE.Server.ClassAbilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// The curated stamp and slot pick (TrialCurated). Pure: reads no PropertyManager key, no world, no database.
    /// </summary>
    [TestClass]
    public class TrialCuratedTests
    {
        private static readonly DateTime At = new DateTime(2026, 11, 24, 9, 30, 15, DateTimeKind.Utc);

        // ================================================================ stamp

        [TestMethod]
        public void Stamp_round_trips_id_class_level_and_time()
        {
            var stamp = TrialCurated.Stamp("void-tank", ClassAbilityClass.VoidSummon, 412, At);

            Assert.AreEqual("v1|adopted:void-tank:VoidSummon:412|adopted|promoted|2026-11-24T09:30:15Z", stamp);

            Assert.IsTrue(TrialCurated.TryParse(stamp, out var id, out var cls, out var level, out var at));
            Assert.AreEqual("void-tank", id);
            Assert.AreEqual(ClassAbilityClass.VoidSummon, cls);
            Assert.AreEqual(412, level);
            Assert.AreEqual(At, at);

            // Every class round-trips, at any level (R3: nothing is fixed at 400).
            foreach (var c in Enum.GetValues<ClassAbilityClass>().Where(c => c != ClassAbilityClass.None))
            {
                foreach (var l in new[] { 1, 275, 400, 401, 9999 })
                {
                    Assert.IsTrue(TrialCurated.TryParse(TrialCurated.Stamp("a-1", c, l, At), out var i2, out var c2, out var l2, out _), $"{c} {l}");
                    Assert.AreEqual(("a-1", c, l), (i2, c2, l2));
                }
            }

            // Two different identities never share a stamp: field 1 carries the identity.
            Assert.AreNotEqual(TrialCurated.Stamp("a", ClassAbilityClass.Rogue, 400, At), TrialCurated.Stamp("b", ClassAbilityClass.Rogue, 400, At));
            Assert.AreNotEqual(TrialCurated.Stamp("a", ClassAbilityClass.Rogue, 400, At), TrialCurated.Stamp("a", ClassAbilityClass.Archer, 400, At));
            Assert.AreNotEqual(TrialCurated.Stamp("a", ClassAbilityClass.Rogue, 400, At), TrialCurated.Stamp("a", ClassAbilityClass.Rogue, 401, At));
        }

        [TestMethod]
        public void Stamp_is_the_promoted_five_field_form_the_shared_parser_accepts()
        {
            var stamp = TrialCurated.Stamp("arch-a", ClassAbilityClass.Archmage, 400, At);

            Assert.AreEqual(5, stamp.Split('|').Length);
            Assert.IsTrue(TrialRebuild.TryParseStamp(stamp, out var field1, out var field2, out var promotedAt));
            Assert.IsNotNull(promotedAt, "a curated master must read as promoted, or the reseed DELETEs it");
            Assert.AreEqual(TrialCurated.Identity("arch-a", ClassAbilityClass.Archmage, 400), field1);
            Assert.AreEqual(TrialCurated.CalibrationToken, field2);
        }

        [TestMethod]
        public void TryParse_refuses_seeded_three_field_and_malformed_stamps()
        {
            var good = TrialCurated.Stamp("arch-a", ClassAbilityClass.Archmage, 400, At);
            Assert.IsTrue(TrialCurated.TryParse(good, out _, out _, out _, out _), "positive control");

            foreach (var bad in new[]
            {
                null,
                "",
                "v1|adopted:arch-a:Archmage:400|adopted",                                     // 3 fields: not promoted
                TrialRebuild.PromotedStamp(new string('a', 64), new string('b', 64), At),     // seeded, then promoted
                "v1|adopted:arch-a:Archmage:400|calsha|promoted|2026-11-24T09:30:15Z",       // field 2 is not the token
                "v1|adopted|adopted|promoted|2026-11-24T09:30:15Z",                           // no identity
                "v1|adopted:arch-a:Archmage|adopted|promoted|2026-11-24T09:30:15Z",          // no level
                "v1|adopted:arch-a:Archmage:400:x|adopted|promoted|2026-11-24T09:30:15Z",    // extra part
                "v1|Adopted:arch-a:Archmage:400|adopted|promoted|2026-11-24T09:30:15Z",      // prefix case
                "v1|adopted:Arch-A:Archmage:400|adopted|promoted|2026-11-24T09:30:15Z",      // id rule
                "v1|adopted:arch-a:archmage:400|adopted|promoted|2026-11-24T09:30:15Z",      // class case
                "v1|adopted:arch-a:None:400|adopted|promoted|2026-11-24T09:30:15Z",          // not a class
                "v1|adopted:arch-a:Archmage\n:400|adopted|promoted|2026-11-24T09:30:15Z",    // trailing newline: $ would pass it, \z does not
                "v1|adopted:arch-a:Archmage:0400|adopted|promoted|2026-11-24T09:30:15Z",     // leading zero
                "v1|adopted:arch-a:Archmage:+400|adopted|promoted|2026-11-24T09:30:15Z",     // sign
                "v1|adopted:arch-a:Archmage:0|adopted|promoted|2026-11-24T09:30:15Z",        // level 0
                "v1|adopted:arch-a:Archmage:400|adopted|promoted|2026-11-24 09:30:15",       // time format
            })
                Assert.IsFalse(TrialCurated.TryParse(bad, out _, out _, out _, out _), bad ?? "null");
        }

        [TestMethod]
        public void A_class_with_a_trailing_newline_is_not_a_class()
        {
            Assert.IsTrue(TrialCurated.TryParseClass("Archmage", out _), "positive control");
            Assert.IsFalse(TrialCurated.TryParseClass("Archmage\n", out _));
            Assert.IsFalse(TrialCurated.TryParseClass("Archmage\r\n", out _));
            Assert.IsFalse(TrialCurated.TryParseClass(" Archmage", out _));
        }

        [TestMethod]
        public void Id_rule_is_the_loaders_plus_no_colon_and_at_most_40()
        {
            Assert.AreEqual(40, TrialCurated.MaxIdLength);
            Assert.IsTrue(TrialCurated.IsValidId("a", out _));
            Assert.IsTrue(TrialCurated.IsValidId(new string('a', 40), out _), "40 characters is accepted");
            Assert.IsTrue(TrialCurated.IsValidId("void-tank-2", out _));

            Assert.IsFalse(TrialCurated.IsValidId(new string('a', 41), out var tooLong), "41 characters is refused");
            StringAssert.Contains(tooLong, "at most 40");

            foreach (var bad in new[] { null, "", new string('a', 41), "a:b", "A", "a_b", "a b", "a.b", "a|b" })
            {
                Assert.IsFalse(TrialCurated.IsValidId(bad, out var reason), bad ?? "null");
                Assert.IsNotNull(reason);
                Assert.ThrowsExactly<ArgumentException>(() => TrialCurated.Stamp(bad, ClassAbilityClass.Rogue, 400, At), bad ?? "null");
            }

            Assert.ThrowsExactly<ArgumentException>(() => TrialCurated.Stamp("a", ClassAbilityClass.None, 400, At));
            Assert.ThrowsExactly<ArgumentException>(() => TrialCurated.Stamp("a", ClassAbilityClass.Rogue, 0, At));
        }

        [TestMethod]
        public void The_longest_field_1_fits_the_64_character_checksum_column()
        {
            // The longest class name, from the enum itself, so a longer class added later fails here.
            var longestClass = Enum.GetValues<ClassAbilityClass>().Where(c => c != ClassAbilityClass.None).OrderByDescending(c => c.ToString().Length).First();

            var field1 = TrialCurated.Identity(new string('z', TrialCurated.MaxIdLength), longestClass, 9999);

            Assert.IsTrue(field1.Length <= 64, $"'{field1}' is {field1.Length} characters; trial_run.archetype_checksum is CHAR(64)");
            Assert.AreEqual(64, TrialCurated.MaxIdentityLength);

            // And it is field 1 of the stamp exactly as the run log receives it.
            Assert.IsTrue(TrialRebuild.TryParseStamp(TrialCurated.Stamp(new string('z', TrialCurated.MaxIdLength), longestClass, 9999, At), out var stampField1, out _, out _));
            Assert.AreEqual(field1, stampField1);
        }

        // ================================================================ PickSlot

        private static List<TrialCurated.SlotFacts> EmptySlots()
            => Enumerable.Range(0, TrialRoster.MasterCount).Select(s => new TrialCurated.SlotFacts(s, null, false, false, false, null, false)).ToList();

        private static void Set(List<TrialCurated.SlotFacts> slots, TrialCurated.SlotFacts f) => slots[f.Slot] = f;

        [TestMethod]
        public void PickSlot_never_takes_slot_0()
        {
            var slots = EmptySlots();

            Assert.IsTrue(TrialCurated.TryPickSlot("cur-a", slots, out var slot, out var reused, out var refusal), refusal);
            Assert.AreEqual(1, slot, "slot 0 is free here and is still skipped");
            Assert.IsFalse(reused);
        }

        [TestMethod]
        public void PickSlot_skips_a_slot_with_a_character_row()
        {
            var slots = EmptySlots();
            Set(slots, slots[1] with { HasCharacter = true });

            Assert.IsTrue(TrialCurated.TryPickSlot("cur-a", slots, out var slot, out _, out var refusal), refusal);
            Assert.AreEqual(2, slot, "a character row with no stamp is not free");
        }

        [TestMethod]
        public void PickSlot_skips_broken_held_stamped_and_seeded_slots()
        {
            var slots = EmptySlots();
            Set(slots, slots[1] with { Broken = true });
            Set(slots, slots[2] with { GateHeld = true });
            Set(slots, slots[3] with { Stamp = "v1|" + new string('a', 64) + "|" + new string('b', 64) });
            Set(slots, slots[4] with { Seeded = true });
            Set(slots, slots[5] with { Stamp = TrialCurated.Stamp("other", ClassAbilityClass.Rogue, 400, At), HasCharacter = true });

            Assert.IsTrue(TrialCurated.TryPickSlot("cur-a", slots, out var slot, out var reused, out var refusal), refusal);
            Assert.AreEqual(6, slot);
            Assert.IsFalse(reused);
        }

        [TestMethod]
        public void PickSlot_reuses_the_slot_of_an_existing_curated_id()
        {
            var slots = EmptySlots();
            Set(slots, slots[7] with { Stamp = TrialCurated.Stamp("cur-a", ClassAbilityClass.Archmage, 380, At), HasCharacter = true });

            Assert.IsTrue(TrialCurated.TryPickSlot("cur-a", slots, out var slot, out var reused, out var refusal), refusal);
            Assert.AreEqual(7, slot);
            Assert.IsTrue(reused);

            // A failed adopt purged the stamp; the broken record's lineage still names the id, so a re-adopt goes back there.
            slots = EmptySlots();
            Set(slots, slots[9] with { Broken = true, BrokenLineage = TrialCurated.Identity("cur-a", ClassAbilityClass.Archmage, 380) });

            Assert.IsTrue(TrialCurated.TryPickSlot("cur-a", slots, out slot, out reused, out refusal), refusal);
            Assert.AreEqual(9, slot);
            Assert.IsTrue(reused);

            // Another id never lands on that broken slot.
            Assert.IsTrue(TrialCurated.TryPickSlot("cur-b", slots, out slot, out reused, out refusal), refusal);
            Assert.AreEqual(1, slot);
            Assert.IsFalse(reused);
        }

        [TestMethod]
        public void PickSlot_refuses_when_nothing_is_free_an_invalid_id_and_a_seeded_reuse()
        {
            var full = EmptySlots().Select(s => s with { HasCharacter = true }).ToList();
            Assert.IsFalse(TrialCurated.TryPickSlot("cur-a", full, out var slot, out _, out var refusal));
            Assert.AreEqual(-1, slot);
            StringAssert.Contains(refusal, "no free master slot");

            Assert.IsFalse(TrialCurated.TryPickSlot("Cur:A", EmptySlots(), out _, out _, out refusal));
            StringAssert.Contains(refusal, "lower-case");

            var seeded = EmptySlots();
            Set(seeded, seeded[0] with { Seeded = true, Stamp = TrialCurated.Stamp("cur-a", ClassAbilityClass.Archmage, 400, At) });
            Assert.IsFalse(TrialCurated.TryPickSlot("cur-a", seeded, out _, out _, out refusal));
            StringAssert.Contains(refusal, "seeded");

            // A slot with no facts is unknown, never free.
            Assert.IsFalse(TrialCurated.TryPickSlot("cur-a", new List<TrialCurated.SlotFacts>(), out _, out _, out refusal));
            StringAssert.Contains(refusal, "no free master slot");
        }
    }
}
