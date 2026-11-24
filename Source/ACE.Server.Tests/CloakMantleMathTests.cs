using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Tests.KnotLoot;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Pure helpers for the Cloak Mantle rear guard and the proc amplifier. No database, no dat files.</summary>
    [TestClass]
    public class CloakMantleMathTests
    {
        private static ACE.Server.WorldObjects.WorldObject Carrier(bool cloak, double amplifier)
        {
            var item = UpgradedCloakTestItems.Item(cloak);
            item.SetProperty(PropertyFloat.CloakProcAmplifier, amplifier);
            return item;
        }

        [TestMethod] public void Level10_20PerLevel_Is200() => Assert.AreEqual(200.0, CloakMantleMath.RearGuardArmorLevel(true, 10, 20, true, false));
        [TestMethod] public void NoMantle_IsZero() => Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(false, 10, 20, true, false));
        [TestMethod] public void NullItemLevel_IsZero() => Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, null, 20, true, false));
        [TestMethod] public void ZeroItemLevel_IsZero() => Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, 0, 20, true, false));
        [TestMethod] public void Disabled_IsZero() => Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, 10, 20, false, false));
        [TestMethod] public void Pvp_IsZero() => Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, 10, 20, true, true));

        [TestMethod]
        public void NaNOrNegativePerLevel_IsZero()
        {
            Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, 10, double.NaN, true, false));
            Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, 10, -5, true, false));
            Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, 10, 0, true, false));
            Assert.AreEqual(0.0, CloakMantleMath.RearGuardArmorLevel(true, 10, double.PositiveInfinity, true, false));
        }

        [TestMethod] public void Mod_200AL_IsQuarter() => Assert.AreEqual(0.25f, CloakMantleMath.RearGuardMod(200, 1.0f), 1e-4f);
        [TestMethod] public void Mod_ZeroAL_IsOne() => Assert.AreEqual(1.0f, CloakMantleMath.RearGuardMod(0, 1.0f));
        [TestMethod] public void Mod_HalvedByShieldCleave() => Assert.AreEqual(0.4f, CloakMantleMath.RearGuardMod(200, 0.5f), 1e-4f);

        [TestMethod] public void Amplifier_Cloak25_Is125() => Assert.AreEqual(1.25, CloakMantleMath.AmplifierFactor(Carrier(true, 0.25), false), 1e-9);
        [TestMethod] public void Amplifier_Pvp_IsOne() => Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(Carrier(true, 0.25), true));
        [TestMethod] public void Amplifier_NonCloakCarrier_IsOne() => Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(Carrier(false, 0.25), false));
        [TestMethod] public void Amplifier_NullCloak_IsOne() => Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(null, false));
        [TestMethod] public void Amplifier_CloakWithoutValue_IsOne() => Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(UpgradedCloakTestItems.Item(cloak: true), false));

        [TestMethod]
        public void Amplifier_NaNOrNonPositive_IsOne()
        {
            Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(Carrier(true, double.NaN), false));
            Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(Carrier(true, double.PositiveInfinity), false));
            Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(Carrier(true, 0), false));
            Assert.AreEqual(1.0, CloakMantleMath.AmplifierFactor(Carrier(true, -0.1), false));
        }

        [TestMethod] public void AmplifyReduction_200x125_Is250() => Assert.AreEqual(250, CloakMantleMath.AmplifyReduction(200, 1.25));
        [TestMethod] public void AmplifyReduction_RoundsHalfAwayFromZero() => Assert.AreEqual(3, CloakMantleMath.AmplifyReduction(2, 1.25));
    }
}
