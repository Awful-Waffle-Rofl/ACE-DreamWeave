using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Quickness turn speed pure composition rule (ACE.Server/Entity/QuicknessTurnSpeed.cs). Exercises
    /// Compute directly rather than through Player.TurnToSpeed, because PropertyManager needs a live shard
    /// config table this project cannot provide.
    /// </summary>
    [TestClass]
    public class QuicknessTurnSpeedTests
    {
        // Shipped defaults - player_turnto_speed_per_quickness=0.004, player_turnto_speed_bonus_max=2.0.
        private const double DefaultPerPoint = 0.004;
        private const double DefaultMaxBonus = 2.0;

        /// <summary>
        /// Default table: +0.4% per point of buffed Quickness, reaching the +200% cap at 500 and held there.
        /// </summary>
        [DataTestMethod]
        [DataRow(0u, 1.0)]
        [DataRow(10u, 1.04)]
        [DataRow(100u, 1.4)]
        [DataRow(250u, 2.0)]
        [DataRow(499u, 2.996)]
        [DataRow(500u, 3.0)]
        [DataRow(750u, 3.0)]
        public void Compute_DefaultTable_MatchesExpected(uint quickness, double expected)
        {
            Assert.AreEqual(expected, QuicknessTurnSpeed.Compute(quickness, DefaultPerPoint, DefaultMaxBonus), 0.0001);
        }

        /// <summary>
        /// A negative or non-finite per-point bonus contributes nothing - a mis-set tunable must never slow
        /// a turn below retail.
        /// </summary>
        [DataTestMethod]
        [DataRow(-0.004)]
        [DataRow(double.NaN)]
        [DataRow(double.NegativeInfinity)]
        public void Compute_GarbagePerPoint_IsRetail(double perPoint)
        {
            Assert.AreEqual(1.0, QuicknessTurnSpeed.Compute(500, perPoint, DefaultMaxBonus), 0.0001);
        }

        /// <summary>
        /// A negative or non-finite cap clamps to 0, which is retail speed at any Quickness.
        /// </summary>
        [DataTestMethod]
        [DataRow(-1.0)]
        [DataRow(double.NaN)]
        [DataRow(double.PositiveInfinity)]
        public void Compute_GarbageMaxBonus_IsRetail(double maxBonus)
        {
            Assert.AreEqual(1.0, QuicknessTurnSpeed.Compute(500, DefaultPerPoint, maxBonus), 0.0001);
        }

        /// <summary>
        /// Oversized tunables clamp to the hard ceiling of 10x rather than running away.
        /// </summary>
        [TestMethod]
        public void Compute_HugeTunables_ClampToTenX()
        {
            Assert.AreEqual(10.0, QuicknessTurnSpeed.Compute(1000, 50.0, 1000.0), 0.0001);
        }
    }
}
