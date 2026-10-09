using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers the SurveyLevelRing codec: the rolling depth-10 ring of gem levels that scales the daily-survey
    /// reward. Pure - nothing here touches a Player or PropertyManager (whose reads throw under the harness).
    ///
    /// The reference level everywhere is DungeonGemSpec.MaxGemLevel, never a literal, so raising the level
    /// ceiling does not silently invalidate these cases.
    /// </summary>
    [TestClass]
    public class SurveyLevelRingTests
    {
        private const int MaxLevel = DungeonGemSpec.MaxGemLevel;

        [TestMethod]
        public void Depth_is_ten()
        {
            // Owner ruling R3. Pinned here because the ring's whole shape and the tier table's top rung agree
            // on this number, and nothing else in the fork may spell it as a literal.
            Assert.AreEqual(10, SurveyLevelRing.Depth);
        }

        [TestMethod]
        public void Round_trips_through_serialize_and_parse()
        {
            var ring = SurveyLevelRing.FromLevels(new[] { MaxLevel, 185, 60, 1 });

            var text = ring.Serialize();
            var read = SurveyLevelRing.Parse(text);

            Assert.AreEqual("v1|lv=" + MaxLevel + ",185,60,1", text);
            CollectionAssert.AreEqual(new[] { MaxLevel, 185, 60, 1 }, read.Levels.ToArray());
        }

        [TestMethod]
        public void An_empty_ring_still_emits_the_levels_field()
        {
            // One canonical on-character form: an empty ring is "v1|lv=", never "v1" and never "".
            Assert.AreEqual("v1|lv=", SurveyLevelRing.Empty.Serialize());
            Assert.IsTrue(SurveyLevelRing.Parse("v1|lv=").IsEmpty);
        }

        [TestMethod]
        public void An_absent_value_reads_as_an_empty_ring()
        {
            // The pre-feature character. Ruling R8 pays these an unscaled reward, so this branch must be
            // silent and must never throw.
            Assert.IsTrue(SurveyLevelRing.Parse(null).IsEmpty);
            Assert.IsTrue(SurveyLevelRing.Parse(string.Empty).IsEmpty);
            Assert.IsTrue(SurveyLevelRing.Parse("   ").IsEmpty);
        }

        [TestMethod]
        public void A_bad_version_reads_as_an_empty_ring()
        {
            Assert.IsFalse(SurveyLevelRing.TryParse("v2|lv=185", out _, out var error));
            StringAssert.Contains(error, "version");

            Assert.IsTrue(SurveyLevelRing.Parse("v2|lv=185").IsEmpty);
            Assert.IsTrue(SurveyLevelRing.Parse("lv=185").IsEmpty);
        }

        [TestMethod]
        public void An_unknown_field_reads_as_an_empty_ring()
        {
            Assert.IsFalse(SurveyLevelRing.TryParse("v1|lv=185|tier=3", out _, out var error));
            StringAssert.Contains(error, "unknown field");

            Assert.IsTrue(SurveyLevelRing.Parse("v1|lv=185|tier=3").IsEmpty);
        }

        [TestMethod]
        public void A_missing_levels_field_reads_as_an_empty_ring()
        {
            Assert.IsFalse(SurveyLevelRing.TryParse("v1", out _, out var error));
            StringAssert.Contains(error, "missing field");
        }

        [TestMethod]
        public void A_non_integer_entry_reads_as_an_empty_ring()
        {
            Assert.IsFalse(SurveyLevelRing.TryParse("v1|lv=185,abc", out _, out var error));
            StringAssert.Contains(error, "not an integer");

            Assert.IsTrue(SurveyLevelRing.Parse("v1|lv=185,abc").IsEmpty);
        }

        [TestMethod]
        public void An_out_of_range_entry_reads_as_an_empty_ring()
        {
            Assert.IsFalse(SurveyLevelRing.TryParse("v1|lv=" + (MaxLevel + 1), out _, out _));
            Assert.IsFalse(SurveyLevelRing.TryParse("v1|lv=0", out _, out _));
            Assert.IsFalse(SurveyLevelRing.TryParse("v1|lv=-5", out _, out _));

            Assert.IsTrue(SurveyLevelRing.Parse("v1|lv=" + (MaxLevel + 1)).IsEmpty);
        }

        [TestMethod]
        public void An_over_length_ring_is_truncated_to_the_newest_entries_rather_than_refused()
        {
            // Entries are newest first, so the head is kept. Truncating rather than refusing is what keeps a
            // LOWERED depth from bricking a character who already stored more than the new depth.
            var stored = "v1|lv=" + string.Join(",", Enumerable.Range(1, SurveyLevelRing.Depth + 3).Select(i => i * 10));

            Assert.IsTrue(SurveyLevelRing.TryParse(stored, out var ring, out _));
            Assert.AreEqual(SurveyLevelRing.Depth, ring.Count);
            Assert.AreEqual(10, ring.Levels[0]);
            Assert.AreEqual(SurveyLevelRing.Depth * 10, ring.Levels[SurveyLevelRing.Depth - 1]);
        }

        [TestMethod]
        public void Push_puts_the_newest_survey_first()
        {
            var ring = SurveyLevelRing.Empty.Push(60).Push(185).Push(MaxLevel);

            CollectionAssert.AreEqual(new[] { MaxLevel, 185, 60 }, ring.Levels.ToArray());
        }

        [TestMethod]
        public void Push_evicts_the_oldest_entry_once_the_ring_is_full()
        {
            var ring = SurveyLevelRing.Empty;

            for (var i = 1; i <= SurveyLevelRing.Depth; i++)
                ring = ring.Push(i);

            Assert.AreEqual(SurveyLevelRing.Depth, ring.Count);
            Assert.AreEqual(SurveyLevelRing.Depth, ring.Levels[0]);
            Assert.AreEqual(1, ring.Levels[SurveyLevelRing.Depth - 1]);

            ring = ring.Push(MaxLevel);

            Assert.AreEqual(SurveyLevelRing.Depth, ring.Count);
            Assert.AreEqual(MaxLevel, ring.Levels[0]);
            Assert.AreEqual(2, ring.Levels[SurveyLevelRing.Depth - 1], "the oldest entry (1) fell off the back");
        }

        [TestMethod]
        public void Push_clamps_an_out_of_range_level_so_the_ring_always_serializes_back()
        {
            var ring = SurveyLevelRing.Empty.Push(MaxLevel + 500).Push(0).Push(-3);

            CollectionAssert.AreEqual(new[] { 1, 1, MaxLevel }, ring.Levels.ToArray());

            // The whole point of clamping: whatever went in, the stored form parses back unchanged.
            Assert.IsTrue(SurveyLevelRing.TryParse(ring.Serialize(), out var read, out _));
            CollectionAssert.AreEqual(ring.Levels.ToArray(), read.Levels.ToArray());
        }

        [TestMethod]
        public void FromLevels_truncates_and_clamps()
        {
            var levels = new List<int>();
            for (var i = 0; i < SurveyLevelRing.Depth + 5; i++)
                levels.Add(MaxLevel + 1);

            var ring = SurveyLevelRing.FromLevels(levels);

            Assert.AreEqual(SurveyLevelRing.Depth, ring.Count);
            Assert.IsTrue(ring.Levels.All(l => l == MaxLevel));
            Assert.IsTrue(SurveyLevelRing.FromLevels(null).IsEmpty);
        }
    }
}
