using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>FillSurface: the pure surface-Z model (Grael pit: floor -47.70, top -42.35, 0.05 m/s).</summary>
    [TestClass]
    public class FillSurfaceTests
    {
        private const double Floor = -47.70;
        private const double Top = -42.35;
        private const double Tol = 1e-6;

        private static FillSurface Rising() => new FillSurface(Floor, Top, Floor, 0.05);

        [TestMethod]
        public void ZAt_RisesAtTheRate_AndStopsAtTheTop()
        {
            var s = Rising();

            Assert.AreEqual(-45.70, s.ZAt(40), Tol);
            Assert.AreEqual(Top, s.ZAt(200), Tol);
            Assert.AreEqual(107.0, s.SecondsToNextEvent(0).Value, Tol);
        }

        [TestMethod]
        public void SetRate_RebasesAndClearsTheTarget()
        {
            var s = Rising();

            s.SetRate(20, -0.1);

            Assert.AreEqual(-47.20, s.ZAt(25), Tol);
            Assert.AreEqual(Floor, s.ZAt(40), Tol);
            Assert.AreEqual(10.0, s.SecondsToNextEvent(20).Value, Tol);
            Assert.IsNull(s.Target);
        }

        [TestMethod]
        public void SetRate_SameRateNoTarget_LeavesVersionAndTrajectoryAlone()
        {
            var s = Rising();
            var v = s.Version;

            s.SetRate(20, 0.05);

            Assert.AreEqual(v, s.Version);
            Assert.AreEqual(-45.70, s.ZAt(40), Tol);
            Assert.AreEqual(107.0, s.SecondsToNextEvent(0).Value, Tol);

            s.SetRate(20, 0.06);
            Assert.AreEqual(v + 1, s.Version);
        }

        [TestMethod]
        public void SetRate_SameRateWithATarget_StillClearsTheTarget()
        {
            var s = Rising();
            s.SetTarget(0, -46.0, 0.05);
            var v = s.Version;

            s.SetRate(10, 0.05);

            Assert.IsNull(s.Target);
            Assert.AreEqual(v + 1, s.Version);
        }

        [TestMethod]
        public void SetRate_Zero_Holds_AndHasNoNextEvent()
        {
            var s = Rising();

            s.SetRate(10, 0);

            Assert.AreEqual(s.ZAt(10), s.ZAt(50), Tol);
            Assert.IsNull(s.SecondsToNextEvent(10));
        }

        [TestMethod]
        public void FillSurface_TargetWithZeroRate_UsesFallbackMagnitude()
        {
            var s = new FillSurface(Floor, Top, -47.20, 0.0);

            s.SetTarget(7, -48.0, 0.05);

            Assert.AreEqual(-0.05, s.Rate, Tol, "falls toward the clamped target at the fallback speed");
            Assert.AreEqual(Floor, s.Target.Value, Tol, "the target is clamped to the floor");
            Assert.AreEqual(Floor, s.ZAt(17), Tol);
            Assert.AreEqual(10.0, s.SecondsToNextEvent(7).Value, Tol);
        }

        [TestMethod]
        public void SetTarget_StopsThere()
        {
            var s = Rising();

            s.SetTarget(0, -46.0, 0.05);

            Assert.AreEqual(-46.0, s.ZAt(34), Tol);
            Assert.AreEqual(-46.0, s.ZAt(100), Tol);
            Assert.AreEqual(34.0, s.SecondsToNextEvent(0).Value, Tol);
            Assert.IsNull(s.SecondsToNextEvent(100), "standing at the target is no event");
        }

        [TestMethod]
        public void SetZ_KeepsTheRate_AndClamps()
        {
            var s = Rising();

            s.SetZ(5, -44.0);

            Assert.AreEqual(-43.5, s.ZAt(15), Tol);

            s.SetZ(5, -10.0);

            Assert.AreEqual(Top, s.ZAt(5), Tol);
        }

        [TestMethod]
        public void SetZ_WithATarget_ResignsTowardIt()
        {
            var s = Rising();

            s.SetTarget(0, -46.0, 0.05);
            s.SetZ(2, -44.0);

            Assert.AreEqual(-0.05, s.Rate, Tol);
            Assert.AreEqual(-44.5, s.ZAt(12), Tol);
        }

        [TestMethod]
        public void Version_IncrementsOnEverySet()
        {
            var s = Rising();
            var v = s.Version;

            s.SetRate(1, 0.1);
            Assert.AreEqual(v + 1, s.Version);
            s.SetTarget(2, -46.0, 0.05);
            Assert.AreEqual(v + 2, s.Version);
            s.SetZ(3, -47.0);
            Assert.AreEqual(v + 3, s.Version);
            s.ZAt(4);
            s.SecondsToNextEvent(4);
            Assert.AreEqual(v + 3, s.Version, "reads do not bump it");
        }
    }
}
