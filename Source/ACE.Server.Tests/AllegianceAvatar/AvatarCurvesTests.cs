using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;

namespace ACE.Server.Tests.Avatars
{
    [TestClass]
    public class AvatarCurvesTests
    {
        private static readonly AvatarTuning d = AvatarTuning.Defaults;
        private static readonly DateTime today = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void CreditAccountDay_Linear_Falloff_And_HardCap()
        {
            Assert.AreEqual(500L, AvatarCurves.CreditAccountDay(0, 500, d));    // at soft cap
            Assert.AreEqual(25L, AvatarCurves.CreditAccountDay(500, 100, d));   // falloff 0.25
            Assert.AreEqual(0L, AvatarCurves.CreditAccountDay(1_500, 1, d));    // hard cap 3x
        }

        [TestMethod]
        public void CreditAccountDay_CrossingSoftAndHardCaps()
        {
            // 100 linear + 1300 * 0.25 = 325 past the soft cap.
            Assert.AreEqual(425L, AvatarCurves.CreditAccountDay(400, 1_400, d));
            // 100 linear + 2475 past, but only 1000 of room under the 1500 hard cap: total lands on 1500.
            Assert.AreEqual(1_100L, AvatarCurves.CreditAccountDay(400, 10_000, d));
            Assert.AreEqual(0L, AvatarCurves.CreditAccountDay(0, 0, d));
            Assert.AreEqual(0L, AvatarCurves.CreditAccountDay(0, -5, d));
        }

        [TestMethod]
        public void RosterScale_Pins()
        {
            Assert.AreEqual(1.0, AvatarCurves.RosterScale(20, 100, d), 1e-9);   // 20% of server: linear
            Assert.AreEqual((20 + Math.Sqrt(20)) / 40, AvatarCurves.RosterScale(40, 100, d), 1e-9); // 0.6118
            Assert.IsTrue(AvatarCurves.RosterScale(40, 100, d) < 0.65);
            Assert.AreEqual(1.0, AvatarCurves.RosterScale(2, 3, d), 1e-9);      // tiny server: floor at min accounts
            Assert.AreEqual(1.0, AvatarCurves.RosterScale(0, 100, d), 1e-9);
            Assert.AreEqual(1.0, AvatarCurves.RosterScale(-1, 100, d), 1e-9);
        }

        private static readonly ulong[] Synthetic = { 0, 0, 10, 30, 60, 100 };

        [TestMethod]
        public void LevelForPool_SyntheticChart_Pins()
        {
            Assert.AreEqual(1, AvatarCurves.LevelForPool(0, Synthetic));
            Assert.AreEqual(1, AvatarCurves.LevelForPool(9, Synthetic));
            Assert.AreEqual(2, AvatarCurves.LevelForPool(10, Synthetic));
            Assert.AreEqual(3, AvatarCurves.LevelForPool(59, Synthetic));
            Assert.AreEqual(4, AvatarCurves.LevelForPool(60, Synthetic));
            Assert.AreEqual(5, AvatarCurves.LevelForPool(ulong.MaxValue, Synthetic));
        }

        [TestMethod]
        public void LevelForPool_ShortChart_Throws()
        {
            Assert.ThrowsExactly<ArgumentException>(() => AvatarCurves.LevelForPool(0, new ulong[] { 0, 10 }));
            Assert.ThrowsExactly<ArgumentException>(() => AvatarCurves.LevelForPool(0, null));
        }

        [TestMethod]
        public void AddFavor_CapsAndAdds()
        {
            Assert.AreEqual(20_000UL, AvatarCurves.AddFavor(19_999, 50, d));
            Assert.AreEqual(150UL, AvatarCurves.AddFavor(100, 50, d));
            Assert.AreEqual(20_000UL, AvatarCurves.AddFavor(ulong.MaxValue, ulong.MaxValue, d));
        }

        [TestMethod]
        public void DecayFavor_GraceInactiveAndSaturation()
        {
            Assert.AreEqual(90UL, AvatarCurves.DecayFavor(100, today.AddDays(-3), false, today, d));
            Assert.AreEqual(100UL, AvatarCurves.DecayFavor(100, today.AddDays(-2), false, today, d));
            Assert.AreEqual(90UL, AvatarCurves.DecayFavor(100, today, true, today, d));   // inactive decays regardless
            Assert.AreEqual(0UL, AvatarCurves.DecayFavor(0, today.AddDays(-30), false, today, d)); // nothing available: stays 0, no underflow
            Assert.AreEqual(9UL, AvatarCurves.DecayFavor(10, today, true, today, d));
            Assert.AreEqual(0UL, AvatarCurves.DecayFavor(1, today, true, today, d));     // floor(0.9)
        }

        [TestMethod]
        public void AbsorbXp_Quarter()
        {
            Assert.AreEqual(25UL, AvatarCurves.AbsorbXp(100, d));
            Assert.AreEqual(0UL, AvatarCurves.AbsorbXp(3, d));
        }

        [TestMethod]
        public void DecayFavor_NullLastContribution_CountsAsGraceElapsed()
        {
            Assert.AreEqual(90UL, AvatarCurves.DecayFavor(100, null, false, today, d));
        }

        [TestMethod]
        public void NonDefaultTuning_IsHonoured()
        {
            Assert.AreEqual(200UL, AvatarCurves.AddFavor(100, 50, d with { FavorRate = 2.0 }));
            Assert.AreEqual(1.0, AvatarCurves.RosterScale(40, 100, d with { RosterExcessExponent = 1.0 }), 0.0);
            // Hard cap 2x: 100 linear + 2475 past, 500 of room under 1000.
            Assert.AreEqual(600L, AvatarCurves.CreditAccountDay(400, 10_000, d with { AccountDailyHardCapMultiple = 2.0 }));
            Assert.AreEqual(50UL, AvatarCurves.AbsorbXp(101, d with { AbsorbFraction = 0.5 }));
        }

        [TestMethod]
        public void RosterScale_NeverExceedsOne_AndUsesMinAccountsFloor()
        {
            // S=101 gives T=20.2; effective 20.2 + sqrt(0.8) = 21.09 would be 1.004 of R=21.
            Assert.AreEqual(1.0, AvatarCurves.RosterScale(21, 101, d), 0.0);
            // S=0: T falls to minActiveAccounts (2).
            Assert.AreEqual((2 + Math.Sqrt(8)) / 10, AvatarCurves.RosterScale(10, 0, d), 1e-9);
        }

        [TestMethod]
        public void AddFavor_SaturatesInsteadOfWrapping()
        {
            Assert.AreEqual(20_000UL, AvatarCurves.AddFavor(0, ulong.MaxValue, d));
            Assert.AreEqual(long.MaxValue, (long)AvatarCurves.AddFavor(0, ulong.MaxValue, d with { FavorCap = long.MaxValue }));
        }

        [TestMethod]
        public void CreditAccountDay_OneUnderTheHardCap()
        {
            Assert.AreEqual(1L, AvatarCurves.CreditAccountDay(1_499, 100, d));
        }
    }
}
