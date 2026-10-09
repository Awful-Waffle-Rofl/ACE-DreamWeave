using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Item XP leveling math (ExperienceSystem). The intent enforced here is structural:
    /// ItemTotalXPToLevel must be the exact inverse of ItemLevelToTotalXP (a mismatch silently
    /// over- or under-levels items as XP is granted), and out-of-range inputs must clamp.
    /// </summary>
    [TestClass]
    public class ExperienceSystemTests
    {
        private const ulong BaseXP = 1000;
        private const int MaxLevel = 10;

        private static readonly ItemXpStyle[] Schemes =
        {
            ItemXpStyle.Fixed,
            ItemXpStyle.ScalesWithLevel,
            ItemXpStyle.FixedPlusBase,
        };

        [TestMethod]
        public void LevelToXPToLevel_RoundTripsForEveryLevelAndScheme()
        {
            foreach (var scheme in Schemes)
            {
                for (var level = 1; level <= MaxLevel; level++)
                {
                    var totalXP = ExperienceSystem.ItemLevelToTotalXP(level, BaseXP, MaxLevel, scheme);
                    var roundTripped = ExperienceSystem.ItemTotalXPToLevel(totalXP, BaseXP, MaxLevel, scheme);

                    Assert.AreEqual(level, roundTripped, $"scheme={scheme} level={level} totalXP={totalXP}");
                }
            }
        }

        [TestMethod]
        public void XPJustBelowNextLevel_DoesNotGrantIt()
        {
            foreach (var scheme in Schemes)
            {
                for (var level = 1; level < MaxLevel; level++)
                {
                    var nextLevelXP = ExperienceSystem.ItemLevelToTotalXP(level + 1, BaseXP, MaxLevel, scheme);
                    var achieved = ExperienceSystem.ItemTotalXPToLevel(nextLevelXP - 1, BaseXP, MaxLevel, scheme);

                    Assert.AreEqual(level, achieved, $"scheme={scheme} level={level}");
                }
            }
        }

        [TestMethod]
        public void ScalesWithLevel_DoublesTheCostOfEachLevel()
        {
            // each level costs double the previous one, so total XP for level L is (2^L - 1) * base
            Assert.AreEqual(BaseXP, ExperienceSystem.ItemLevelToTotalXP(1, BaseXP, MaxLevel, ItemXpStyle.ScalesWithLevel));
            Assert.AreEqual(3 * BaseXP, ExperienceSystem.ItemLevelToTotalXP(2, BaseXP, MaxLevel, ItemXpStyle.ScalesWithLevel));
            Assert.AreEqual(7 * BaseXP, ExperienceSystem.ItemLevelToTotalXP(3, BaseXP, MaxLevel, ItemXpStyle.ScalesWithLevel));
            Assert.AreEqual(((1ul << MaxLevel) - 1) * BaseXP, ExperienceSystem.ItemLevelToTotalXP(MaxLevel, BaseXP, MaxLevel, ItemXpStyle.ScalesWithLevel));
        }

        [TestMethod]
        public void Fixed_CostsTheSameForEachLevel()
        {
            for (var level = 1; level <= MaxLevel; level++)
                Assert.AreEqual((ulong)level * BaseXP, ExperienceSystem.ItemLevelToTotalXP(level, BaseXP, MaxLevel, ItemXpStyle.Fixed), $"level={level}");
        }

        [TestMethod]
        public void LevelInputs_ClampInsteadOfWrapping()
        {
            foreach (var scheme in Schemes)
            {
                Assert.AreEqual(0ul, ExperienceSystem.ItemLevelToTotalXP(0, BaseXP, MaxLevel, scheme), $"scheme={scheme}");
                Assert.AreEqual(0ul, ExperienceSystem.ItemLevelToTotalXP(-5, BaseXP, MaxLevel, scheme), $"scheme={scheme}");

                var atMax = ExperienceSystem.ItemLevelToTotalXP(MaxLevel, BaseXP, MaxLevel, scheme);
                Assert.AreEqual(atMax, ExperienceSystem.ItemLevelToTotalXP(MaxLevel + 5, BaseXP, MaxLevel, scheme), $"scheme={scheme}");
            }
        }

        [TestMethod]
        public void XPBeyondMaxLevel_ClampsToMaxLevel()
        {
            foreach (var scheme in Schemes)
            {
                var overshoot = ExperienceSystem.ItemLevelToTotalXP(MaxLevel, BaseXP, MaxLevel, scheme) * 10;

                Assert.AreEqual(MaxLevel, ExperienceSystem.ItemTotalXPToLevel(overshoot, BaseXP, MaxLevel, scheme), $"scheme={scheme}");
            }
        }
    }
}
