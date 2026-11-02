using System;

using ACE.Server.Knots;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Knots
{
    /// <summary>
    /// KnotTunables. PropertyManager keeps its values in static caches shared by every test class in the
    /// process, so each test seeds every key it reads and restores the shipped default in a finally.
    /// </summary>
    [TestClass]
    public class KnotTunablesTests
    {
        [TestMethod]
        public void ShippedDefaults_AreTheDesignedOnes_AndMatchTheFallbacks()
        {
            Assert.IsFalse(DefaultPropertyManager.DefaultBooleanProperties[KnotTunables.FiberEnabledKey].Item);
            Assert.AreEqual(0.20, DefaultPropertyManager.DefaultDoubleProperties[KnotTunables.FiberChanceKey].Item, 1e-12);
            Assert.AreEqual(0.05, DefaultPropertyManager.DefaultDoubleProperties[KnotTunables.FiberPityStepKey].Item, 1e-12);
            Assert.AreEqual(0.07, DefaultPropertyManager.DefaultDoubleProperties[KnotTunables.PatternChanceKey].Item, 1e-12);

            Assert.IsTrue(KnotTunables.DefaultFiberPerSeat);
            Assert.AreEqual(KnotTunables.DefaultFiberPerSeat, DefaultPropertyManager.DefaultBooleanProperties[KnotTunables.FiberPerSeatKey].Item);
            Assert.AreEqual(KnotTunables.DefaultFiberEnabled, DefaultPropertyManager.DefaultBooleanProperties[KnotTunables.FiberEnabledKey].Item);
            Assert.AreEqual(KnotTunables.DefaultFiberChance, DefaultPropertyManager.DefaultDoubleProperties[KnotTunables.FiberChanceKey].Item, 1e-12);
            Assert.AreEqual(KnotTunables.DefaultFiberPityStep, DefaultPropertyManager.DefaultDoubleProperties[KnotTunables.FiberPityStepKey].Item, 1e-12);
            Assert.AreEqual(KnotTunables.DefaultPatternChance, DefaultPropertyManager.DefaultDoubleProperties[KnotTunables.PatternChanceKey].Item, 1e-12);
        }

        [TestMethod]
        public void Readers_ReturnTheSeededValues_ThenTheDefaultsAfterRestore()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyBool(KnotTunables.FiberEnabledKey, true));
                Assert.IsTrue(PropertyManager.ModifyBool(KnotTunables.FiberPerSeatKey, false));
                Assert.IsTrue(PropertyManager.ModifyDouble(KnotTunables.FiberChanceKey, 0.5));
                Assert.IsTrue(PropertyManager.ModifyDouble(KnotTunables.FiberPityStepKey, 0.1));
                Assert.IsTrue(PropertyManager.ModifyDouble(KnotTunables.PatternChanceKey, 0.25));

                Assert.IsTrue(KnotTunables.FiberEnabled);
                Assert.IsFalse(KnotTunables.FiberPerSeat);
                Assert.AreEqual(0.5, KnotTunables.FiberChance, 1e-12);
                Assert.AreEqual(0.1, KnotTunables.FiberPityStep, 1e-12);
                Assert.AreEqual(0.25, KnotTunables.PatternChance, 1e-12);
            }
            finally
            {
                Restore();
            }

            Assert.IsFalse(KnotTunables.FiberEnabled);
            Assert.IsTrue(KnotTunables.FiberPerSeat);
            Assert.AreEqual(0.20, KnotTunables.FiberChance, 1e-12);
            Assert.AreEqual(0.05, KnotTunables.FiberPityStep, 1e-12);
            Assert.AreEqual(0.07, KnotTunables.PatternChance, 1e-12);
        }

        [TestMethod]
        public void Chances_AreClampedToZeroOne()
        {
            try
            {
                PropertyManager.ModifyDouble(KnotTunables.FiberChanceKey, 7.0);
                PropertyManager.ModifyDouble(KnotTunables.FiberPityStepKey, -1.0);
                PropertyManager.ModifyDouble(KnotTunables.PatternChanceKey, double.NaN);

                Assert.AreEqual(1.0, KnotTunables.FiberChance, 1e-12);
                Assert.AreEqual(0.0, KnotTunables.FiberPityStep, 1e-12);
                Assert.AreEqual(0.0, KnotTunables.PatternChance, 1e-12);
            }
            finally
            {
                Restore();
            }
        }

        private static void Restore()
        {
            PropertyManager.ModifyBool(KnotTunables.FiberEnabledKey, KnotTunables.DefaultFiberEnabled);
            PropertyManager.ModifyBool(KnotTunables.FiberPerSeatKey, KnotTunables.DefaultFiberPerSeat);
            PropertyManager.ModifyDouble(KnotTunables.FiberChanceKey, KnotTunables.DefaultFiberChance);
            PropertyManager.ModifyDouble(KnotTunables.FiberPityStepKey, KnotTunables.DefaultFiberPityStep);
            PropertyManager.ModifyDouble(KnotTunables.PatternChanceKey, KnotTunables.DefaultPatternChance);
        }
    }
}
