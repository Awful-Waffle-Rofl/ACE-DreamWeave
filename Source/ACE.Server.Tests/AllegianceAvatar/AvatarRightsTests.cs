using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>The pure spend rule and the pure mapping behind the production allegiance reader (FactsFrom). The Player overloads read facts through a seam that AvatarVendorTests stubs; FactsFrom is what the unstubbed reader feeds.</summary>
    [TestClass]
    public class AvatarRightsTests
    {
        [TestMethod]
        public void FactsFrom_SpeakerIsNotSeneschal() => Assert.IsFalse(AvatarRights.FactsFrom(5, 9, 1).IsSeneschal);

        [TestMethod]
        public void FactsFrom_SeneschalAndCastellanAre()
        {
            Assert.IsTrue(AvatarRights.FactsFrom(5, 9, 2).IsSeneschal);
            Assert.IsTrue(AvatarRights.FactsFrom(5, 9, 3).IsSeneschal);
            Assert.IsFalse(AvatarRights.FactsFrom(5, 9, 0).IsSeneschal, "no officer record");
        }

        [TestMethod]
        public void FactsFrom_MonarchIsTheAllegiances_OrOwnGuidWithNone()
        {
            Assert.AreEqual(9u, AvatarRights.FactsFrom(5, 9, 0).MonarchGuid);
            Assert.AreEqual(5u, AvatarRights.FactsFrom(5, null, 0).MonarchGuid);
        }

        [TestMethod]
        public void Seneschal_CanSpend() => Assert.IsTrue(AvatarRights.CanSpend(isMember: true, isMonarch: false, isActingKeeper: false, officerLevel: 2));

        [TestMethod]
        public void Castellan_CanSpend() => Assert.IsTrue(AvatarRights.CanSpend(true, false, false, 3));

        [TestMethod]
        public void Speaker_Cannot() => Assert.IsFalse(AvatarRights.CanSpend(true, false, false, 1));

        [TestMethod]
        public void PlainMember_Cannot() => Assert.IsFalse(AvatarRights.CanSpend(true, false, false, 0));

        [TestMethod]
        public void Monarch_CanSpend() => Assert.IsTrue(AvatarRights.CanSpend(true, true, false, 0));

        [TestMethod]
        public void ActingKeeper_CanSpend() => Assert.IsTrue(AvatarRights.CanSpend(true, false, true, 0));

        [TestMethod]
        public void NonMember_Cannot()
        {
            Assert.IsFalse(AvatarRights.CanSpend(false, true, true, 3), "membership gates every other right");
            Assert.IsFalse(AvatarRights.CanSpend(false, false, false, 0));
        }
    }
}
