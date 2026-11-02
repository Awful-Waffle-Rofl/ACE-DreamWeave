using ACE.Server.KnotLoot;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>Knot loot level window math (pure; no PropertyManager).</summary>
    [TestClass]
    public class KnotLootWindowTests
    {
        private static readonly KnotWindowSettings W = KnotWindowSettings.Default;

        private static void AssertWin(double lo, double hi, (double Low, double High) actual)
        {
            Assert.AreEqual(lo, actual.Low, 1e-9);
            Assert.AreEqual(hi, actual.High, 1e-9);
        }

        [TestMethod]
        public void Window_SpecAnchors()
        {
            AssertWin(0.00, 0.30, KnotLootWindow.Window(200, W));
            AssertWin(0.35, 0.65, KnotLootWindow.Window(300, W));
            AssertWin(0.70, 1.00, KnotLootWindow.Window(400, W));
        }

        [TestMethod]
        public void Window_EdgeTable()
        {
            AssertWin(0.00, 0.30, KnotLootWindow.Window(1, W));
            AssertWin(0.70, 1.00, KnotLootWindow.Window(999, W));
            AssertWin(0.175, 0.475, KnotLootWindow.Window(250, W));
            AssertWin(0.70, 1.00, KnotLootWindow.Window(300, W with { Width = 0.5, LowStart = 0.7 }));
            Assert.AreEqual(0.35, KnotLootWindow.Position(300, double.NaN, W), 1e-9);
            Assert.AreEqual(0.65, KnotLootWindow.Position(300, 7.0, W), 1e-9);
            Assert.AreEqual(0.35, KnotLootWindow.Position(300, -1.0, W), 1e-9);
        }

        [TestMethod]
        public void Window_EqualLevels_DoesNotDivideByZero()
        {
            var s = W with { LowLevel = 300, HighLevel = 300 };
            AssertWin(0.00, 0.30, KnotLootWindow.Window(299, s));
            AssertWin(0.70, 1.00, KnotLootWindow.Window(300, s));
            AssertWin(0.70, 1.00, KnotLootWindow.Window(301, s));
        }

        [TestMethod]
        public void Position_IsLinearInsideTheWindow()
            => Assert.AreEqual(0.50, KnotLootWindow.Position(300, 0.5, W), 1e-9);

        [TestMethod]
        public void Lerp_MapsOntoRange()
        {
            Assert.AreEqual(312.5, KnotLootWindow.Lerp(125, 500, 0.5), 1e-9);
            Assert.AreEqual(25.0, KnotLootWindow.Lerp(25, 100, 0.0), 1e-9);
        }
    }
}