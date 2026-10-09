using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two pure statics behind fellowship leech-management warnings:
    /// <see cref="Fellowship.ParseLeechWarnSeconds(string)"/> and
    /// <see cref="Fellowship.SelectLeechWarnThreshold(double, double?, IReadOnlyList{double})"/>.
    ///
    /// Only these are tested here - PropertyManager reads throw in unit tests, so OnTick and the
    /// Fellowship constructor (which read 'fellowship_leech_check_default' etc) are out of reach.
    /// </summary>
    [TestClass]
    public class FellowshipLeechWarningTests
    {
        [TestMethod]
        public void Parse_HappyPath_ReturnsDescending()
        {
            CollectionAssert.AreEqual(new List<double> { 600, 300, 60 }, Fellowship.ParseLeechWarnSeconds("600,300,60"));
        }

        [TestMethod]
        public void Parse_UnsortedInput_SortsDescending()
        {
            CollectionAssert.AreEqual(new List<double> { 600, 300, 60 }, Fellowship.ParseLeechWarnSeconds("60,600,300"));
        }

        [TestMethod]
        public void Parse_WhitespaceIsTolerated()
        {
            CollectionAssert.AreEqual(new List<double> { 600, 300, 60 }, Fellowship.ParseLeechWarnSeconds("  600 , 300,  60  "));
        }

        [TestMethod]
        public void Parse_JunkTokens_AreSkipped()
        {
            CollectionAssert.AreEqual(new List<double> { 600, 60 }, Fellowship.ParseLeechWarnSeconds("600,abc,,60"));
        }

        [TestMethod]
        public void Parse_ZeroAndNegative_AreDropped()
        {
            CollectionAssert.AreEqual(new List<double> { 600, 60 }, Fellowship.ParseLeechWarnSeconds("600,0,-30,60"));
        }

        [TestMethod]
        public void Parse_Duplicates_AreCollapsed()
        {
            CollectionAssert.AreEqual(new List<double> { 600, 300 }, Fellowship.ParseLeechWarnSeconds("600,300,600,300"));
        }

        [TestMethod]
        public void Parse_Null_ReturnsEmpty()
        {
            Assert.AreEqual(0, Fellowship.ParseLeechWarnSeconds(null).Count);
        }

        [TestMethod]
        public void Parse_EmptyString_ReturnsEmpty()
        {
            Assert.AreEqual(0, Fellowship.ParseLeechWarnSeconds("").Count);
        }

        private static readonly List<double> Thresholds = new List<double> { 600, 300, 60 };

        [TestMethod]
        public void Select_NothingCrossed_ReturnsNull()
        {
            Assert.IsNull(Fellowship.SelectLeechWarnThreshold(700, null, Thresholds));
        }

        [TestMethod]
        public void Select_ExactlyOneCrossed_ReturnsThatOne()
        {
            Assert.AreEqual(600, Fellowship.SelectLeechWarnThreshold(500, null, Thresholds));
        }

        [TestMethod]
        public void Select_SeveralCrossedAtOnce_ReturnsSmallest()
        {
            // anti-spam invariant: a long tick gap crossing 600, 300, and 60 all at once must fire
            // only the tightest mark (60), never all three or the largest.
            Assert.AreEqual(60, Fellowship.SelectLeechWarnThreshold(10, null, Thresholds));
        }

        [TestMethod]
        public void Select_RefiringSameMark_ReturnsNull()
        {
            Assert.IsNull(Fellowship.SelectLeechWarnThreshold(500, 600, Thresholds));
        }

        [TestMethod]
        public void Select_TighterMarkAfterLooserAlreadyWarned_Fires()
        {
            Assert.AreEqual(300, Fellowship.SelectLeechWarnThreshold(250, 600, Thresholds));
        }

        [TestMethod]
        public void Select_LooserMarkAfterTighterAlreadyWarned_ReturnsNull()
        {
            // already warned at 300 (tighter); remaining now back up to crossing only 600 -> must not re-fire 600.
            Assert.IsNull(Fellowship.SelectLeechWarnThreshold(500, 300, Thresholds));
        }

        [TestMethod]
        public void Select_BoundaryRemainingEqualsThreshold_CountsAsCrossed()
        {
            Assert.AreEqual(600, Fellowship.SelectLeechWarnThreshold(600, null, Thresholds));
        }

        [TestMethod]
        public void Select_EmptyThresholdList_ReturnsNull()
        {
            Assert.IsNull(Fellowship.SelectLeechWarnThreshold(10, null, new List<double>()));
        }
    }
}
