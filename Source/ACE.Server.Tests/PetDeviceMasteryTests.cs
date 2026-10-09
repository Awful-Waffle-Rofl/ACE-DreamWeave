using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The summoning-mastery gate on pet devices: PetDevice.MeetsMasteryRequirement, the pure predicate behind
    /// CheckUseRequirements, in both modes of the summoning_mastery_unrestricted kill-switch.
    /// </summary>
    [TestClass]
    public class PetDeviceMasteryTests
    {
        [TestMethod]
        public void DefaultSource_WithoutShardConfig_ReturnsTrue()
        {
            // No shard config here, so the PropertyManager read either throws (caught, falls back to true) or
            // hits a warm cache holding the row default (true). Either way the kill-switch defaults ON.
            Assert.IsTrue(PetDevice.MasteryUnrestrictedSource());
        }

        [TestMethod]
        public void NoRequirement_Passes_InBothModes()
        {
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(null, SummoningMastery.Primalist, false));
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(null, null, false));
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(null, SummoningMastery.Primalist, true));
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(null, null, true));
        }

        [TestMethod]
        public void MatchingMastery_Passes_InBothModes()
        {
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(SummoningMastery.Necromancer, SummoningMastery.Necromancer, false));
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(SummoningMastery.Necromancer, SummoningMastery.Necromancer, true));
        }

        [TestMethod]
        public void MismatchedMastery_Restricted_Fails()
        {
            Assert.IsFalse(PetDevice.MeetsMasteryRequirement(SummoningMastery.Necromancer, SummoningMastery.Primalist, false),
                "retail rule: a Primalist must not use a Necromancer essence when the kill-switch is off");
        }

        [TestMethod]
        public void MismatchedMastery_Unrestricted_Passes()
        {
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(SummoningMastery.Necromancer, SummoningMastery.Primalist, true));
        }

        [TestMethod]
        public void NullPlayerMastery_WithRequirement_Restricted_Fails()
        {
            Assert.IsFalse(PetDevice.MeetsMasteryRequirement(SummoningMastery.Naturalist, null, false));
        }

        [TestMethod]
        public void NullPlayerMastery_WithRequirement_Unrestricted_Passes()
        {
            Assert.IsTrue(PetDevice.MeetsMasteryRequirement(SummoningMastery.Naturalist, null, true));
        }
    }
}
