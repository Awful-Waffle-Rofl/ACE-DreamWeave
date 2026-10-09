using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Player.SavageBlowsSurcharge is the pure per-swing stamina surcharge calculation behind the Savage
    /// Blows class ability (Berserker T1): a fraction of the swing's base attack stamina, floored at 1
    /// whenever the fraction is positive so a small base cost can never round the surcharge away to 0 -
    /// which would make the ability's "each swing costs extra stamina" text false. Mirrors
    /// ClassAbilityP8PreWriteMechanicsTests' exercise-the-pure-statics style: Player's static initializer
    /// cannot run under this test host, so the magnitude lives in a pure static method.
    /// </summary>
    [TestClass]
    public class SavageBlowsStaminaSurchargeTests
    {
        [DataRow(4, 0.25, 1)]
        [DataRow(2, 0.25, 1)]
        [DataRow(10, 0.25, 3)]
        [DataRow(8, 0.25, 2)]
        [DataRow(1, 0.25, 1)]
        [DataRow(10, 0.0, 0)]
        [DataRow(10, -1.0, 0)]
        [DataTestMethod]
        public void SavageBlowsSurcharge_RoundsToNearestButFloorsAtOneWhenPositive(int baseStaminaCost, double fraction, int expected)
        {
            Assert.AreEqual(expected, Player.SavageBlowsSurcharge(baseStaminaCost, fraction));
        }

        /// <summary>
        /// Player.DecideSavageBlowsStamina is the pure gate behind ApplySavageBlowsStamina: whether a swing
        /// pays the Savage Blows stamina surcharge and gets the flag that unlocks its damage bonus. Same
        /// "cannot construct a live Player under this test host" constraint as above, so the gate's inputs
        /// are passed in directly rather than read off a real Player/PropertyManager/target.
        /// </summary>
        [TestMethod]
        public void DecideSavageBlowsStamina_Unaffordable_FlagFalseCostUnchanged()
        {
            // base cost 4, surcharge at 25% = 1, but only 3 stamina available and costSoFar already 3
            var (paid, cost) = Player.DecideSavageBlowsStamina(
                classAbilitiesEnabled: true, targetIsPlayer: false, hasSavageBlows: true,
                staminaCurrent: 3, baseStaminaCost: 4, costSoFar: 3, surchargeFraction: 0.25);

            Assert.IsFalse(paid);
            Assert.AreEqual(3, cost);
        }

        [TestMethod]
        public void DecideSavageBlowsStamina_Affordable_FlagTrueCostPlusSurcharge()
        {
            // base cost 4, surcharge at 25% = 1, 10 stamina available, costSoFar 3 -> 4
            var (paid, cost) = Player.DecideSavageBlowsStamina(
                classAbilitiesEnabled: true, targetIsPlayer: false, hasSavageBlows: true,
                staminaCurrent: 10, baseStaminaCost: 4, costSoFar: 3, surchargeFraction: 0.25);

            Assert.IsTrue(paid);
            Assert.AreEqual(4, cost);
        }

        [TestMethod]
        public void DecideSavageBlowsStamina_PlayerTarget_ShortCircuitsFlagFalse()
        {
            // plenty of stamina and the ability learned, but a Player target must still fail closed
            var (paid, cost) = Player.DecideSavageBlowsStamina(
                classAbilitiesEnabled: true, targetIsPlayer: true, hasSavageBlows: true,
                staminaCurrent: 100, baseStaminaCost: 4, costSoFar: 0, surchargeFraction: 0.25);

            Assert.IsFalse(paid);
            Assert.AreEqual(0, cost);
        }

        [TestMethod]
        public void DecideSavageBlowsStamina_UnlearnedAbility_ShortCircuitsFlagFalse()
        {
            var (paid, cost) = Player.DecideSavageBlowsStamina(
                classAbilitiesEnabled: true, targetIsPlayer: false, hasSavageBlows: false,
                staminaCurrent: 100, baseStaminaCost: 4, costSoFar: 0, surchargeFraction: 0.25);

            Assert.IsFalse(paid);
            Assert.AreEqual(0, cost);
        }
    }
}
