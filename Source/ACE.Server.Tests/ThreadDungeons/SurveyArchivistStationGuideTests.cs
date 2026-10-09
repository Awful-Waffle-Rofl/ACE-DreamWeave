using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pins the guide-book constants on SurveyArchivistStation (the first-contact give of "Thread Gems:
    /// What We Know"). Nothing else in that feature is unit-testable: the branches that decide whether to
    /// give the book live in TryGiveGuide, which needs a live Player (the test harness builds none), so
    /// those branches ride on live verification instead - see the class doc comment on TryGiveGuide.
    /// </summary>
    [TestClass]
    public class SurveyArchivistStationGuideTests
    {
        [TestMethod]
        public void GuideBookWcid_is_the_shipped_book_weenie()
        {
            Assert.AreEqual(1003625u, SurveyArchivistStation.GuideBookWcid);
        }

        [TestMethod]
        public void GuideQuest_matches_the_shipped_quest_row()
        {
            Assert.IsTrue(SurveyArchivistStation.GuideQuest == "DynDungeonGuide");
        }

        [TestMethod]
        public void GivenMessage_and_NoRoomMessage_are_pinned()
        {
            Assert.IsTrue(SurveyArchivistStation.GivenMessage ==
                "Take this. It is what I have written down so far about the threads, and it is not finished. Add to it.");

            Assert.IsTrue(SurveyArchivistStation.NoRoomMessage ==
                "I have something written for you, and no hand free to put it in. Make room and speak to me again.");
        }
    }
}
