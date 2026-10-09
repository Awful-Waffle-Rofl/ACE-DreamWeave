using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Covers the level gate on the Drift Network full-respec NPC's Luminance fee
    /// (<see cref="ClassAbilityTrainer.FullRespecFee"/>): free while the character is still levelling,
    /// the flat class_ability_full_respec_lum_cost from class_ability_full_respec_free_below_level up.
    /// Only the pure price function is testable here - HandleRespec itself needs a live Player.
    /// </summary>
    [TestClass]
    public class ClassAbilityFullRespecFeeTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static long L(string key) => PropertyManager.GetLong(key).Item;

        [TestMethod]
        public void Defaults_AreFreeBelow275AndFlatMillionAtAndAbove()
        {
            Assert.AreEqual(275, L("class_ability_full_respec_free_below_level"));
            Assert.AreEqual(1000000, L("class_ability_full_respec_lum_cost"));
        }

        [TestMethod]
        public void BelowTheFreeLevel_CostsNothing()
        {
            Assert.AreEqual(0, ClassAbilityTrainer.FullRespecFee(1));
            Assert.AreEqual(0, ClassAbilityTrainer.FullRespecFee(100));
            Assert.AreEqual(0, ClassAbilityTrainer.FullRespecFee(274));
        }

        [TestMethod]
        public void AtAndAboveTheFreeLevel_ChargesTheFlatFee()
        {
            var flat = L("class_ability_full_respec_lum_cost");

            // 275 itself PAYS - the boundary is "free while still levelling", and 275 is the level at
            // which the build is settled (same anchor as class_ability_xp_min_level).
            Assert.AreEqual(flat, ClassAbilityTrainer.FullRespecFee(275));
            Assert.AreEqual(flat, ClassAbilityTrainer.FullRespecFee(276));
            Assert.AreEqual(flat, ClassAbilityTrainer.FullRespecFee(9999));
        }

        [TestMethod]
        public void FreeLevelOfZero_DisablesTheWaiver()
        {
            var flat = L("class_ability_full_respec_lum_cost");

            PropertyManager.ModifyLong("class_ability_full_respec_free_below_level", 0);

            try
            {
                Assert.AreEqual(flat, ClassAbilityTrainer.FullRespecFee(1));
                Assert.AreEqual(flat, ClassAbilityTrainer.FullRespecFee(274));
            }
            finally
            {
                PropertyManager.ModifyLong("class_ability_full_respec_free_below_level", 275);
            }
        }
    }
}
