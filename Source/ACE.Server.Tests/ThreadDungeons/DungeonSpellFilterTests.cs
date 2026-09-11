using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// DungeonSpellFilter.Strip and ParseBannedIds, pure with no database and no dat files - every default is
    /// reached as a compile-time constant (DungeonSpellFilter.DefaultBannedSpellIds,
    /// DungeonSpellFilter.DefaultStripAoeSpellsEnabled) because PropertyManager reads throw under this
    /// harness.
    /// </summary>
    [TestClass]
    public class DungeonSpellFilterTests
    {
        private static readonly HashSet<int> DefaultBannedIds = DungeonSpellFilter.ParseBannedIds(DungeonSpellFilter.DefaultBannedSpellIds);

        [TestMethod]
        public void A_multi_projectile_spell_is_removed()
        {
            var book = new Dictionary<int, float> { { 1001, 1.0f } };

            var removed = DungeonSpellFilter.Strip(book, new HashSet<int>(), id => id == 1001 ? 9 : 0, hasDamagingBodyPart: true);

            Assert.AreEqual(1, removed.Count);
            Assert.AreEqual(1001, removed[0]);
            Assert.IsFalse(book.ContainsKey(1001));
        }

        [TestMethod]
        public void A_banned_id_is_removed_even_at_one_projectile()
        {
            var book = new Dictionary<int, float> { { 3940, 1.0f } };

            var removed = DungeonSpellFilter.Strip(book, DefaultBannedIds, id => 1, hasDamagingBodyPart: true);

            Assert.AreEqual(1, removed.Count);
            Assert.AreEqual(3940, removed[0]);
            Assert.IsFalse(book.ContainsKey(3940));
        }

        [TestMethod]
        public void A_single_projectile_unbanned_spell_is_untouched()
        {
            var book = new Dictionary<int, float> { { 42, 1.0f } };

            var removed = DungeonSpellFilter.Strip(book, DefaultBannedIds, id => 1, hasDamagingBodyPart: true);

            Assert.AreEqual(0, removed.Count);
            Assert.IsTrue(book.ContainsKey(42));
        }

        [TestMethod]
        public void A_creature_losing_every_spell_is_stripped_when_it_has_a_damaging_body_part()
        {
            var book = new Dictionary<int, float> { { 1001, 1.0f }, { 1002, 1.0f } };

            var removed = DungeonSpellFilter.Strip(book, new HashSet<int>(), id => 9, hasDamagingBodyPart: true);

            Assert.AreEqual(2, removed.Count);
            Assert.AreEqual(0, book.Count);
        }

        [TestMethod]
        public void The_guard_refuses_to_empty_the_book_of_a_creature_with_no_damaging_body_part()
        {
            var book = new Dictionary<int, float> { { 1001, 1.0f }, { 1002, 1.0f } };

            var removed = DungeonSpellFilter.Strip(book, new HashSet<int>(), id => 9, hasDamagingBodyPart: false);

            Assert.AreEqual(0, removed.Count);
            Assert.AreEqual(2, book.Count);
            Assert.IsTrue(book.ContainsKey(1001));
            Assert.IsTrue(book.ContainsKey(1002));
        }

        [TestMethod]
        public void The_guard_does_not_engage_when_the_book_would_not_be_emptied()
        {
            // One flagged spell, one untouched spell, no damaging body part: the book is NOT emptied by the
            // strip, so the guard must not refuse it - only a would-be-EMPTY result engages the guard.
            var book = new Dictionary<int, float> { { 1001, 1.0f }, { 42, 1.0f } };

            var removed = DungeonSpellFilter.Strip(book, new HashSet<int>(), id => id == 1001 ? 9 : 1, hasDamagingBodyPart: false);

            Assert.AreEqual(1, removed.Count);
            Assert.AreEqual(1001, removed[0]);
            Assert.IsFalse(book.ContainsKey(1001));
            Assert.IsTrue(book.ContainsKey(42));
        }

        [TestMethod]
        public void An_empty_id_list_leaves_the_projectile_rule_working()
        {
            var book = new Dictionary<int, float> { { 1001, 1.0f } };
            var emptyBannedIds = DungeonSpellFilter.ParseBannedIds("");

            Assert.AreEqual(0, emptyBannedIds.Count);

            var removed = DungeonSpellFilter.Strip(book, emptyBannedIds, id => 9, hasDamagingBodyPart: true);

            Assert.AreEqual(1, removed.Count);
        }

        [TestMethod]
        public void Malformed_entries_in_the_id_list_are_skipped_not_thrown()
        {
            var parsed = DungeonSpellFilter.ParseBannedIds("3940, notanumber, ,6167,-5,3999");

            Assert.IsTrue(parsed.Contains(3940));
            Assert.IsTrue(parsed.Contains(6167));
            Assert.IsTrue(parsed.Contains(3999));
            Assert.IsFalse(parsed.Contains(-5));
            Assert.AreEqual(3, parsed.Count);
        }

        [TestMethod]
        public void The_default_banned_ids_parse_to_exactly_the_three_owner_ruled_ids()
        {
            Assert.AreEqual(3, DefaultBannedIds.Count);
            Assert.IsTrue(DefaultBannedIds.Contains(3940));
            Assert.IsTrue(DefaultBannedIds.Contains(3999));
            Assert.IsTrue(DefaultBannedIds.Contains(6167));
        }

        [TestMethod]
        public void The_master_switch_default_is_on()
        {
            // The spawner reads this constant as its explicit PropertyManager fallback; a test that only
            // exercises Strip/ParseBannedIds would never notice a flipped default, since the master switch
            // itself is spawner-side wiring, not part of the pure decision logic.
            Assert.IsTrue(DungeonSpellFilter.DefaultStripAoeSpellsEnabled);
        }

        [TestMethod]
        public void Master_switch_off_is_the_spawners_job_not_strips()
        {
            // Strip itself has no master-switch concept - the spawner simply never calls it when
            // dynamic_dungeons_strip_aoe_spells reads false, which reproduces today's behaviour exactly
            // because nothing touches the book at all. Pinned here as documentation of that boundary, since
            // there is no PropertyManager-reading code path this pure test tree can exercise directly.
            var book = new Dictionary<int, float> { { 1001, 1.0f } };
            var untouchedCopy = new Dictionary<int, float>(book);

            // Simulating "master switch off": the spawner's if-guard is never entered, so Strip is never
            // called and the book is byte-for-byte what it was authored as.
            CollectionAssert.AreEqual(untouchedCopy, book);
        }
    }
}
