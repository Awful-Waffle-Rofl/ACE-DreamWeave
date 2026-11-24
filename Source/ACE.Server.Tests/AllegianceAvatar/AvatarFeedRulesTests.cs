using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AvatarFeedRulesTests
    {
        private static AvatarFeedSource? Classify(XpType t, bool thread = false, bool publicEvent = false, bool privateEvent = false, int fellows = 0)
            => AvatarFeedRules.Classify(t, thread, publicEvent, privateEvent, fellows);

        [TestMethod]
        public void Allegiance_Passup_IsExcluded()
        {
            Assert.IsNull(Classify(XpType.Allegiance));
            Assert.IsNull(Classify(XpType.Allegiance, thread: true, publicEvent: true, fellows: 3));
        }

        [TestMethod]
        public void Proficiency_IsExcluded()
        {
            Assert.IsNull(Classify(XpType.Proficiency));
            Assert.IsNull(Classify(XpType.Admin));
            Assert.IsNull(Classify(XpType.Emote));
        }

        [TestMethod]
        public void PrivateAvatarEvent_IsExcluded()
        {
            foreach (var t in new[] { XpType.Kill, XpType.Quest, XpType.Fellowship })
                Assert.IsNull(Classify(t, thread: true, publicEvent: true, privateEvent: true, fellows: 4), t.ToString());
        }

        [TestMethod]
        public void FellowshipTwoAccounts_BeatsThread()
        {
            Assert.AreEqual(AvatarFeedSource.Fellowship, Classify(XpType.Kill, thread: true, publicEvent: true, fellows: 2));
        }

        [TestMethod]
        public void OneAccountFellowship_IsNotFellowshipSource()
        {
            Assert.AreEqual(AvatarFeedSource.Base, Classify(XpType.Kill, fellows: 1));
            Assert.AreEqual(AvatarFeedSource.Thread, Classify(XpType.Kill, thread: true, fellows: 1));
        }

        [TestMethod]
        public void PublicEventParticipant_IsWorldEvent()
        {
            Assert.AreEqual(AvatarFeedSource.WorldEvent, Classify(XpType.Kill, publicEvent: true));
            Assert.AreEqual(AvatarFeedSource.WorldEvent, Classify(XpType.Kill, thread: true, publicEvent: true));
        }

        [TestMethod]
        public void Quest_InThread_IsThread()
        {
            Assert.AreEqual(AvatarFeedSource.Thread, Classify(XpType.Quest, thread: true));
        }

        [TestMethod]
        public void Kill_Quest_And_FellowshipShare_PlainContext_AreBase()
        {
            Assert.AreEqual(AvatarFeedSource.Base, Classify(XpType.Kill));
            Assert.AreEqual(AvatarFeedSource.Base, Classify(XpType.Quest));
            Assert.AreEqual(AvatarFeedSource.Base, Classify(XpType.Fellowship));
        }
    }
}
