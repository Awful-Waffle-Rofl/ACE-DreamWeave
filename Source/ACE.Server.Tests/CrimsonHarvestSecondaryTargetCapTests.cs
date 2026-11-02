using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for CrimsonHarvestMath.SecondaryTargetCap, the per-rank EXTRA-creature count (beyond the
    /// primary target). Pure and Player-free: it reads no PropertyManager key, so there is nothing to seed
    /// and the class passes alone or in any order.
    /// </summary>
    [TestClass]
    public class CrimsonHarvestSecondaryTargetCapTests
    {
        [TestMethod]
        public void RankZeroAndBelow_StrikeNoExtraTargets()
        {
            Assert.AreEqual(0, CrimsonHarvestMath.SecondaryTargetCap(0, 2, 3, 4));
            Assert.AreEqual(0, CrimsonHarvestMath.SecondaryTargetCap(-1, 2, 3, 4));
        }

        [TestMethod]
        public void RanksOneToThree_ReturnTheirOwnExtras()
        {
            Assert.AreEqual(2, CrimsonHarvestMath.SecondaryTargetCap(1, 2, 3, 4));
            Assert.AreEqual(3, CrimsonHarvestMath.SecondaryTargetCap(2, 2, 3, 4));
            Assert.AreEqual(4, CrimsonHarvestMath.SecondaryTargetCap(3, 2, 3, 4));
        }

        [TestMethod]
        public void RankAboveThree_ClampsToRankThree()
        {
            Assert.AreEqual(4, CrimsonHarvestMath.SecondaryTargetCap(4, 2, 3, 4));
            Assert.AreEqual(4, CrimsonHarvestMath.SecondaryTargetCap(99, 2, 3, 4));
        }

        [TestMethod]
        public void NegativeTunable_NeverYieldsANegativeCount()
        {
            Assert.AreEqual(0, CrimsonHarvestMath.SecondaryTargetCap(1, -5, 3, 4));
            Assert.AreEqual(0, CrimsonHarvestMath.SecondaryTargetCap(3, 2, 3, -1));
        }

        [TestMethod]
        public void HugeTunable_ClampsToIntRange()
        {
            Assert.AreEqual(int.MaxValue, CrimsonHarvestMath.SecondaryTargetCap(1, long.MaxValue, 3, 4));
        }
    }
}
