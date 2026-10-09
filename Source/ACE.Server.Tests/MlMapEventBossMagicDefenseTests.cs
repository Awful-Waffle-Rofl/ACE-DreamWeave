using ACE.Server.MlShared;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// RoZ round 19 owner ruling M1: MlMapEventBossMagicDefense.ScaleInitLevel is the pure arithmetic both
    /// MlDigsiteSpawner and MlRelariaSpawner apply at spawn to lower a map-event boss's MagicDefense. It
    /// takes no PropertyManager reads (see the class remarks), so it is testable with no seeding at all.
    /// </summary>
    [TestClass]
    public class MlMapEventBossMagicDefenseTests
    {
        /// <summary>Scale of 1.0 must be a complete no-op: authored InitLevel comes back unchanged.</summary>
        [TestMethod]
        public void Scale_1_0_IsNoOp()
        {
            Assert.AreEqual(200u, MlMapEventBossMagicDefense.ScaleInitLevel(200, 500, 1.0));
        }

        /// <summary>
        /// The documented default: 0.90 on a boss with Current 500 and InitLevel 200 lowers Current to
        /// round(500 * 0.90) = 450, a drop of 50, taken entirely off InitLevel.
        /// </summary>
        [TestMethod]
        public void Scale_0_90_LowersInitLevelByTheSkillDelta()
        {
            Assert.AreEqual(150u, MlMapEventBossMagicDefense.ScaleInitLevel(200, 500, 0.90));
        }

        /// <summary>
        /// InitLevel is a uint and must never go negative: a scale demanding a bigger drop than InitLevel
        /// can supply clamps at 0 rather than underflowing or throwing.
        /// </summary>
        [TestMethod]
        public void DropLargerThanInitLevel_ClampsToZero()
        {
            // Current 500, scale 0.10 -> target 50 -> delta 450, but InitLevel is only 200.
            Assert.AreEqual(0u, MlMapEventBossMagicDefense.ScaleInitLevel(200, 500, 0.10));
        }

        /// <summary>Scale 0.0 asks for the whole authored skill to be removed; still clamps at 0, never negative.</summary>
        [TestMethod]
        public void Scale_0_0_ClampsToZero()
        {
            Assert.AreEqual(0u, MlMapEventBossMagicDefense.ScaleInitLevel(50, 500, 0.0));
        }

        /// <summary>A scale above 1.0 is clamped to 1.0 defensively (the caller is expected to have already
        /// clamped it, but this method must never raise InitLevel or the skill above authored).</summary>
        [TestMethod]
        public void ScaleAbove1_ClampsTo1_NoOp()
        {
            Assert.AreEqual(200u, MlMapEventBossMagicDefense.ScaleInitLevel(200, 500, 1.5));
        }

        /// <summary>A negative scale is clamped to 0.0, same as an explicit 0.</summary>
        [TestMethod]
        public void NegativeScale_ClampsTo0()
        {
            Assert.AreEqual(0u, MlMapEventBossMagicDefense.ScaleInitLevel(50, 500, -0.5));
        }

        /// <summary>NaN is treated as 1.0 (no-op) rather than propagating into Math.Round and corrupting the result.</summary>
        [TestMethod]
        public void NaNScale_TreatedAsNoOp()
        {
            Assert.AreEqual(200u, MlMapEventBossMagicDefense.ScaleInitLevel(200, 500, double.NaN));
        }

        /// <summary>Rounding uses away-from-zero at the .5 boundary, matching Math.Round's documented default elsewhere in this fork.</summary>
        [TestMethod]
        public void HalfwayTarget_RoundsAwayFromZero()
        {
            // Current 5, scale 0.5 -> target round(2.5) = 3 (away from zero) -> delta 2.
            Assert.AreEqual(8u, MlMapEventBossMagicDefense.ScaleInitLevel(10, 5, 0.5));
        }
    }
}
