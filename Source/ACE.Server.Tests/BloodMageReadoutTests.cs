using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the /abilities readout (IAbilityReadout.GetReadout) on the seven Blood Mage handlers.
    /// Five of the seven never touch the `player` parameter - SanguineReserve, WeakenedBlood, CrimsonHarvest,
    /// BloodPrice and SanguineWard all resolve purely from rank plus PropertyManager tunables, the same shape
    /// their pure math classes already have - so those five are called here directly with a null Player,
    /// exercising the actual GetReadout wiring (unit conversion to display percent/multiplier, Label/Unit/Per,
    /// CapNote) rather than re-deriving the arithmetic a second time.
    ///
    /// SKIPPED: TransfusionAbility.GetReadout and ExsanguinateAbility.GetReadout both call a live Player
    /// method (GetClassAbilityScaling / GetClassAbilityRank respectively) and Player's static initializer
    /// cannot run under this test host (project constraint - see BloodMageMechanicsTests' header and
    /// CLAUDE.md's "Player statics untestable" note). Their underlying pure statics
    /// (DrainSurplusDistribution.ShareFraction, BloodChargeMath.StackCap, ExsanguinateMath.BurstMultiplier)
    /// are already covered by BloodMageMechanicsTests and TransfusionShareTests.
    /// </summary>
    [TestClass]
    public class BloodMageReadoutTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- Sanguine Reserve ----------------------------------------------------------------------

        [TestMethod]
        public void SanguineReserve_Readout_ReportsPerChargeRateAsPercentWithNoCap()
        {
            var ability = new SanguineReserveAbility();

            var readout = ability.GetReadout(null, 3);

            Assert.IsTrue(readout.HasValue);
            Assert.AreEqual(7.0, readout.Skill, 1e-9); // class_ability_bloodmage_charge_per_stack default 0.07
            Assert.AreEqual(0.0, readout.Affinity, 1e-9);
            Assert.AreEqual(0.0, readout.Gear, 1e-9);
            Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
            Assert.AreEqual("%", readout.Unit);
            Assert.AreEqual("life dmg", readout.Label);
            Assert.AreEqual("/charge", readout.Per);
            Assert.IsFalse(readout.Capped);
        }

        [TestMethod]
        public void SanguineReserve_Readout_IsRankIndependent_TheTunableIsFlat()
        {
            var ability = new SanguineReserveAbility();

            // the per-charge tunable is not rank-scaled - only the stack CAP is - so every owned rank reads
            // the same per-charge rate
            Assert.AreEqual(ability.GetReadout(null, 1).Skill, ability.GetReadout(null, 3).Skill, 1e-9);
        }

        // ---- Weakened Blood --------------------------------------------------------------------------

        [TestMethod]
        public void WeakenedBlood_Readout_ReportsBareMultiplierAcrossRanks()
        {
            var ability = new WeakenedBloodAbility();

            var r1 = ability.GetReadout(null, 1);
            var r2 = ability.GetReadout(null, 2);
            var r3 = ability.GetReadout(null, 3);

            Assert.AreEqual(2.00, r1.Skill, 1e-9);
            Assert.AreEqual(2.50, r2.Skill, 1e-9);
            Assert.AreEqual(3.10, r3.Skill, 1e-9);

            foreach (var readout in new[] { r1, r2, r3 })
            {
                Assert.IsTrue(readout.HasValue);
                Assert.AreEqual(0.0, readout.Affinity, 1e-9);
                Assert.AreEqual(0.0, readout.Gear, 1e-9);
                Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
                Assert.AreEqual("x", readout.Unit);
                Assert.AreEqual("life vuln", readout.Label);
                Assert.IsNull(readout.Per);
                Assert.IsFalse(readout.Capped);
            }
        }

        // ---- Crimson Harvest -------------------------------------------------------------------------

        [TestMethod]
        public void CrimsonHarvest_Readout_HasNoScalableValue()
        {
            var ability = new CrimsonHarvestAbility();

            var readout = ability.GetReadout(null, 1);

            Assert.IsFalse(readout.HasValue);
        }

        // ---- Blood Price -----------------------------------------------------------------------------

        [TestMethod]
        public void BloodPrice_Readout_ReportsDamageBonusAsPercentWithNoCap()
        {
            var ability = new BloodPriceAbility();

            var r1 = ability.GetReadout(null, 1);
            var r2 = ability.GetReadout(null, 2);
            var r3 = ability.GetReadout(null, 3);

            Assert.AreEqual(10.0, r1.Skill, 1e-9);
            Assert.AreEqual(20.0, r2.Skill, 1e-9);
            Assert.AreEqual(30.0, r3.Skill, 1e-9);

            foreach (var readout in new[] { r1, r2, r3 })
            {
                Assert.IsTrue(readout.HasValue);
                Assert.AreEqual(0.0, readout.Affinity, 1e-9);
                Assert.AreEqual(0.0, readout.Gear, 1e-9);
                Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
                Assert.AreEqual("%", readout.Unit);
                Assert.AreEqual("spell dmg", readout.Label);
                Assert.IsFalse(readout.Capped); // the 20% health floor is a gate, never a CapNote
            }
        }

        // ---- Sanguine Ward ---------------------------------------------------------------------------

        [TestMethod]
        public void SanguineWard_Readout_ReportsWardFractionAsPercentWithNoCap()
        {
            var ability = new SanguineWardAbility();

            var r1 = ability.GetReadout(null, 1);
            var r2 = ability.GetReadout(null, 2);
            var r3 = ability.GetReadout(null, 3);

            Assert.AreEqual(60.0, r1.Skill, 1e-9);
            Assert.AreEqual(90.0, r2.Skill, 1e-9);
            Assert.AreEqual(120.0, r3.Skill, 1e-9);

            foreach (var readout in new[] { r1, r2, r3 })
            {
                Assert.IsTrue(readout.HasValue);
                Assert.AreEqual(0.0, readout.Affinity, 1e-9);
                Assert.AreEqual(0.0, readout.Gear, 1e-9);
                Assert.AreEqual(readout.Skill, readout.Effective, 1e-9);
                Assert.AreEqual("%", readout.Unit);
                Assert.AreEqual("ward", readout.Label);
                Assert.IsFalse(readout.Capped);
            }
        }

        [TestMethod]
        public void SanguineWard_Readout_SelfCostSecondary_ReportsPerRankFractionAsPercentWithNoAffinityOrGear()
        {
            var ability = new SanguineWardAbility();

            var r1 = ability.GetReadout(null, 1);
            var r2 = ability.GetReadout(null, 2);
            var r3 = ability.GetReadout(null, 3);

            // shipped defaults: class_ability_sanguine_ward_selfcost_r1/r2/r3 = 0.75/0.55/0.35
            AssertSelfCostSecondary(r1, 75.0);
            AssertSelfCostSecondary(r2, 55.0);
            AssertSelfCostSecondary(r3, 35.0);
        }

        private static void AssertSelfCostSecondary(ClassAbilityReadout readout, double expectedSkillPercent)
        {
            Assert.IsNotNull(readout.Secondary);
            Assert.AreEqual(1, readout.Secondary.Count);

            var secondary = readout.Secondary[0];

            Assert.IsTrue(secondary.HasValue);
            Assert.AreEqual(expectedSkillPercent, secondary.Skill, 1e-9);
            Assert.AreEqual(0.0, secondary.Affinity, 1e-9);
            Assert.AreEqual(0.0, secondary.Gear, 1e-9);
            Assert.AreEqual(expectedSkillPercent, secondary.Effective, 1e-9);
            Assert.AreEqual("%", secondary.Unit);
            Assert.AreEqual("self cost", secondary.Label);
            Assert.IsFalse(secondary.Capped);
        }
    }
}
