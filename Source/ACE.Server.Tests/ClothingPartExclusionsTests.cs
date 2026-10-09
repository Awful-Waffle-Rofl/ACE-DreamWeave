using System.Collections.Generic;

using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers ClothingPartExclusions.Parse, the pure parser for PropertyString.ClothingPartExclusions.
    /// Creature.CalculateObjDesc itself reads client_portal.dat and a live EquippedObjects collection
    /// and is not unit-testable (see WornItemObjDescMergeTests); the parser is the whole of the new
    /// behaviour and is pure.
    /// </summary>
    [TestClass]
    public class ClothingPartExclusionsTests
    {
        [TestMethod]
        public void Parse_Null_ReturnsEmptySet()
        {
            var result = ClothingPartExclusions.Parse(null);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Parse_Empty_ReturnsEmptySet()
        {
            var result = ClothingPartExclusions.Parse(string.Empty);

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Parse_Whitespace_ReturnsEmptySet()
        {
            var result = ClothingPartExclusions.Parse("   ");

            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Parse_SingleValue_ReturnsThatValue()
        {
            var result = ClothingPartExclusions.Parse("16");

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.Contains((byte)16));
        }

        [TestMethod]
        public void Parse_MultipleValuesWithWhitespace_TrimsTokens()
        {
            var result = ClothingPartExclusions.Parse(" 12, 15 ,16");

            Assert.AreEqual(3, result.Count);
            Assert.IsTrue(result.Contains((byte)12));
            Assert.IsTrue(result.Contains((byte)15));
            Assert.IsTrue(result.Contains((byte)16));
        }

        [TestMethod]
        public void Parse_DuplicateValues_Collapse()
        {
            var result = ClothingPartExclusions.Parse("16,16");

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.Contains((byte)16));
        }

        [TestMethod]
        public void Parse_InvalidAndOutOfRangeTokens_AreIgnored()
        {
            var result = ClothingPartExclusions.Parse("a,300,-1,16");

            Assert.AreEqual(1, result.Count);
            Assert.IsTrue(result.Contains((byte)16));
        }

        [TestMethod]
        public void TryClipBaseAppearance_EntirelyBelowRegion_ReturnsFalse()
        {
            Assert.IsFalse(ClothingPartExclusions.TryClipBaseAppearance(0, 0x28, out _, out _));
            Assert.IsFalse(ClothingPartExclusions.TryClipBaseAppearance(0x18, 0x8, out _, out _));
        }

        [TestMethod]
        public void TryClipBaseAppearance_StraddlingRegion_ClipsToRegionEnd()
        {
            Assert.IsTrue(ClothingPartExclusions.TryClipBaseAppearance(0, 0x40, out var offset1, out var length1));
            Assert.AreEqual((ushort)0x28, offset1);
            Assert.AreEqual((ushort)0x18, length1);

            Assert.IsTrue(ClothingPartExclusions.TryClipBaseAppearance(0x20, 0x10, out var offset2, out var length2));
            Assert.AreEqual((ushort)0x28, offset2);
            Assert.AreEqual((ushort)0x8, length2);
        }

        [TestMethod]
        public void TryClipBaseAppearance_EntirelyAboveRegion_IsUnchanged()
        {
            Assert.IsTrue(ClothingPartExclusions.TryClipBaseAppearance(0x28, 0x10, out var offset1, out var length1));
            Assert.AreEqual((ushort)0x28, offset1);
            Assert.AreEqual((ushort)0x10, length1);

            Assert.IsTrue(ClothingPartExclusions.TryClipBaseAppearance(0x48, 0x18, out var offset2, out var length2));
            Assert.AreEqual((ushort)0x48, offset2);
            Assert.AreEqual((ushort)0x18, length2);
        }

        [TestMethod]
        public void TryClipBaseAppearance_ZeroLength_ReturnsFalse()
        {
            Assert.IsFalse(ClothingPartExclusions.TryClipBaseAppearance(0, 0, out _, out _));
            Assert.IsFalse(ClothingPartExclusions.TryClipBaseAppearance(0x28, 0, out _, out _));
        }

        private static HashSet<int> Units(params (int Start, int EndInclusive)[] ranges)
        {
            var set = new HashSet<int>();
            foreach (var (start, endInclusive) in ranges)
                for (int u = start; u <= endInclusive; u++)
                    set.Add(u);
            return set;
        }

        [TestMethod]
        public void ClipToUnits_AllUnitsNeeded_ReturnsOriginalRange()
        {
            var runs = ClothingPartExclusions.ClipToUnits(40, 24, Units((40, 63)));

            Assert.AreEqual(1, runs.Count);
            Assert.AreEqual(((ushort)40, (ushort)24), runs[0]);
        }

        [TestMethod]
        public void ClipToUnits_NoUnitsNeeded_ReturnsEmpty()
        {
            // The Strathelar suit's [240,250) range: used only by its excluded head.
            var runs = ClothingPartExclusions.ClipToUnits(240, 10, Units((40, 63), (72, 95), (136, 174), (209, 239)));

            Assert.AreEqual(0, runs.Count);
        }

        [TestMethod]
        public void ClipToUnits_TrimsBothEnds()
        {
            // [186,252) against a visible set of 209-239 keeps only [209,240).
            var runs = ClothingPartExclusions.ClipToUnits(186, 66, Units((209, 239)));

            Assert.AreEqual(1, runs.Count);
            Assert.AreEqual(((ushort)209, (ushort)31), runs[0]);
        }

        [TestMethod]
        public void ClipToUnits_GapInMiddle_SplitsIntoRuns()
        {
            // [174,240) against 174 and 209-239 splits into two runs.
            var runs = ClothingPartExclusions.ClipToUnits(174, 66, Units((174, 174), (209, 239)));

            Assert.AreEqual(2, runs.Count);
            Assert.AreEqual(((ushort)174, (ushort)1), runs[0]);
            Assert.AreEqual(((ushort)209, (ushort)31), runs[1]);
        }

        [TestMethod]
        public void ClipToUnits_RunReachingRangeEnd_IsClosed()
        {
            var runs = ClothingPartExclusions.ClipToUnits(10, 5, Units((12, 20)));

            Assert.AreEqual(1, runs.Count);
            Assert.AreEqual(((ushort)12, (ushort)3), runs[0]);
        }

        [TestMethod]
        public void ClipToUnits_ZeroLengthOrEmptySet_ReturnsEmpty()
        {
            Assert.AreEqual(0, ClothingPartExclusions.ClipToUnits(40, 0, Units((40, 63))).Count);
            Assert.AreEqual(0, ClothingPartExclusions.ClipToUnits(40, 24, new HashSet<int>()).Count);
            Assert.AreEqual(0, ClothingPartExclusions.ClipToUnits(40, 24, null).Count);
        }
    }
}
