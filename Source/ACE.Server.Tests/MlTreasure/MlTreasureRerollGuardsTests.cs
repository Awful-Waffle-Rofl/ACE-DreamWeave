using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// MlTreasureRerollGuards - the decidable parts of /rrtm (TREASURE-HUNT-PLAN.md section 6
    /// "Reroll", step 8) that do not need a live Player or PropertyManager. Everything else in
    /// TreasureMapRerollCommands (target resolution, the indoor check, the Marae Lassel box check, the
    /// per-character cooldown) reads live state and is not unit-testable in this fork
    /// (ACE.Server.Tests cannot construct a Player, and PropertyManager reads throw under unit test).
    /// </summary>
    [TestClass]
    public class MlTreasureRerollGuardsTests
    {
        [TestMethod]
        public void IsAlreadyRerolled_False_AllowsReroll()
        {
            Assert.IsFalse(MlTreasureRerollGuards.IsAlreadyRerolled(rerolledFlag: false));
        }

        [TestMethod]
        public void IsAlreadyRerolled_True_RefusesReroll()
        {
            Assert.IsTrue(MlTreasureRerollGuards.IsAlreadyRerolled(rerolledFlag: true));
        }

        [TestMethod]
        public void IsTooFarToReroll_WithinMax_False()
        {
            Assert.IsFalse(MlTreasureRerollGuards.IsTooFarToReroll(distanceMetres: 500f, maxMetres: 1000f));
        }

        /// <summary>The boundary itself is NOT too far - only strictly beyond it refuses, matching
        /// MlTreasureGeometry.Classify's own > comparison convention.</summary>
        [TestMethod]
        public void IsTooFarToReroll_ExactlyAtMax_False()
        {
            Assert.IsFalse(MlTreasureRerollGuards.IsTooFarToReroll(distanceMetres: 1000f, maxMetres: 1000f));
        }

        [TestMethod]
        public void IsTooFarToReroll_BeyondMax_True()
        {
            Assert.IsTrue(MlTreasureRerollGuards.IsTooFarToReroll(distanceMetres: 1000.01f, maxMetres: 1000f));
        }

        [TestMethod]
        public void IsTooFarToReroll_ZeroDistance_False()
        {
            Assert.IsFalse(MlTreasureRerollGuards.IsTooFarToReroll(distanceMetres: 0f, maxMetres: 1000f));
        }
    }
}
