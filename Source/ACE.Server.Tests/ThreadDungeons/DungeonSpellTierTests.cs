using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers DungeonSpellTier: the spell-tier axis of the band-standard uplift. Nothing here touches a
    /// database, a landblock or PropertyManager - TierOf and Raise are pure functions of
    /// SpellLevelProgression's static tables.
    /// </summary>
    [TestClass]
    public class DungeonSpellTierTests
    {
        // ---- TierOf ----------------------------------------------------------------------------------------

        [TestMethod]
        public void TierOf_reads_the_1_based_index_in_an_8_long_family()
        {
            Assert.AreEqual(5, DungeonSpellTier.TierOf((int)SpellId.ImperilOther5), "Imperil Other V is the 5th form");

            // Verified this session against ace_world (SELECT id,name FROM spell WHERE id=4312): id 4312 is
            // "Incantation of Imperil Other", the LAST (tier 8) form in SpellLevelProgression.ImperilOther -
            // this family's naming runs the opposite direction from the "Incantation of X = tier 1" pattern
            // some other families use.
            Assert.AreEqual(4312, (int)SpellId.ImperilOther8, "pins the id this test's tier-8 claim is verified against");
            Assert.AreEqual(8, DungeonSpellTier.TierOf((int)SpellId.ImperilOther8));
            Assert.AreEqual(8, DungeonSpellTier.TierOf(4312));
        }

        [TestMethod]
        public void TierOf_is_zero_for_a_non_8_long_family_and_for_an_unknown_id()
        {
            // CantripHermeticLink is a 4-long family (SpellLevelProgression.cs) - the exact guard
            // LootGenerationFactory_Spells.cs:288 and SpellRerollTables.NumLevels both apply.
            Assert.AreEqual(0, DungeonSpellTier.TierOf((int)SpellId.CantripHermeticLink1), "a cantrip family is 4 long, not 8");

            Assert.AreEqual(0, DungeonSpellTier.TierOf(99999), "an id with no progression at all");
        }

        // ---- Raise -------------------------------------------------------------------------------------------

        private static Dictionary<int, float> BookOf(params (SpellId Id, float Probability)[] entries)
        {
            var book = new Dictionary<int, float>();

            foreach (var (id, probability) in entries)
                book[(int)id] = probability;

            return book;
        }

        [TestMethod]
        public void Raise_to_8_replaces_a_lower_tier_entry_and_leaves_a_non_tierable_id_untouched()
        {
            var book = BookOf(
                (SpellId.ImperilOther5, 0.35f),
                (SpellId.CantripHermeticLink1, 0.10f));

            var raised = DungeonSpellTier.Raise(book, 8);

            Assert.AreEqual(1, raised, "only the tier-5 entry was below tier 8");
            Assert.AreEqual(2, book.Count);
            Assert.IsFalse(book.ContainsKey((int)SpellId.ImperilOther5));
            Assert.AreEqual(0.35f, book[(int)SpellId.ImperilOther8], "the tier-5 entry's own probability, carried to its replacement");
            Assert.AreEqual(0.10f, book[(int)SpellId.CantripHermeticLink1], "non-tierable id untouched");
        }

        [TestMethod]
        public void Raise_to_8_leaves_an_already_tier_8_entry_completely_untouched()
        {
            var book = BookOf((SpellId.ImperilOther8, 0.90f));

            var raised = DungeonSpellTier.Raise(book, 8);

            Assert.AreEqual(0, raised);
            Assert.AreEqual(1, book.Count);
            Assert.AreEqual(0.90f, book[(int)SpellId.ImperilOther8]);
        }

        [TestMethod]
        public void Raise_preserves_the_books_original_enumeration_order()
        {
            // Monster_Magic.TryRollSpell_Unconditional walks the book in dictionary order and casts the first
            // spell that rolls under its probability, so a Raise that scrambled order would change which
            // spell a creature favours even when the id SET afterward is identical.
            var book = BookOf(
                (SpellId.CantripHermeticLink1, 0.10f),
                (SpellId.ImperilOther5, 0.20f),
                (SpellId.ImperilOther8, 0.30f));

            DungeonSpellTier.Raise(book, 8);

            CollectionAssert.AreEqual(
                new[] { (int)SpellId.CantripHermeticLink1, (int)SpellId.ImperilOther8 },
                book.Keys.ToArray());
        }

        [TestMethod]
        public void Raise_collapses_a_collision_to_one_entry_at_the_first_position_keeping_the_max_probability()
        {
            // The book already holds BOTH the tier-5 entry and its tier-8 replacement. Raising to 8 must
            // collapse them into one entry, at ImperilOther5's original (first) position, keeping whichever
            // probability was higher.
            var book = BookOf(
                (SpellId.ImperilOther5, 0.80f),
                (SpellId.CantripHermeticLink1, 0.10f),
                (SpellId.ImperilOther8, 0.25f));

            var raised = DungeonSpellTier.Raise(book, 8);

            Assert.AreEqual(1, raised);
            Assert.AreEqual(2, book.Count, "the collision collapses two entries into one");
            Assert.AreEqual(0.80f, book[(int)SpellId.ImperilOther8], "the MAX of the two probabilities, not the second one seen");

            CollectionAssert.AreEqual(
                new[] { (int)SpellId.ImperilOther8, (int)SpellId.CantripHermeticLink1 },
                book.Keys.ToArray(),
                "the collapsed entry sits at the FIRST position either occupied");
        }

        [TestMethod]
        public void A_tier_of_zero_or_below_is_a_no_op()
        {
            var book = BookOf((SpellId.ImperilOther5, 0.35f));
            var before = new Dictionary<int, float>(book);

            Assert.AreEqual(0, DungeonSpellTier.Raise(book, 0));
            CollectionAssert.AreEqual(before, book);

            Assert.AreEqual(0, DungeonSpellTier.Raise(book, -3));
            CollectionAssert.AreEqual(before, book);
        }

        [TestMethod]
        public void Raise_to_7_raises_a_tier_5_entry_to_tier_7_and_leaves_a_tier_8_entry_alone()
        {
            var book = BookOf(
                (SpellId.ImperilOther5, 0.35f),
                (SpellId.ImperilOther8, 0.90f));

            var raised = DungeonSpellTier.Raise(book, 7);

            Assert.AreEqual(1, raised);
            Assert.IsTrue(book.ContainsKey((int)SpellId.ImperilOther7), "tier 5 -> tier 7");
            Assert.AreEqual(0.35f, book[(int)SpellId.ImperilOther7]);
            Assert.AreEqual(0.90f, book[(int)SpellId.ImperilOther8], "already at/above tier 7, untouched");
        }

        [TestMethod]
        public void A_tier_above_8_is_clamped_to_8()
        {
            var book = BookOf((SpellId.ImperilOther5, 0.35f));

            var raised = DungeonSpellTier.Raise(book, 20);

            Assert.AreEqual(1, raised);
            Assert.IsTrue(book.ContainsKey((int)SpellId.ImperilOther8));
        }

        [TestMethod]
        public void A_null_or_empty_book_is_a_no_op()
        {
            Assert.AreEqual(0, DungeonSpellTier.Raise(null, 8));

            var empty = new Dictionary<int, float>();
            Assert.AreEqual(0, DungeonSpellTier.Raise(empty, 8));
            Assert.AreEqual(0, empty.Count);
        }
    }
}
