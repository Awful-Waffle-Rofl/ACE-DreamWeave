using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// DreamWeave allegiance passup: the per-vassal share diminishes as a patron takes on more
    /// vassals, so their combined take rises from the first-vassal rate to the max-total rate at
    /// the 11-vassal cap instead of scaling linearly to 275%.
    /// </summary>
    [TestClass]
    public class AllegiancePassupTests
    {
        private const double FirstVassal = AllegianceManager.PatronPassupRate;
        private const double MaxTotal = AllegianceManager.MaxTotalPassupRate;

        private static double CombinedTake(int vassalCount)
        {
            return AllegianceManager.CalculatePatronPassupRate(vassalCount, FirstVassal, MaxTotal) * vassalCount;
        }

        [TestMethod]
        public void SingleVassal_PassesUpTheFirstVassalRate()
        {
            Assert.AreEqual(0.25, AllegianceManager.CalculatePatronPassupRate(1, FirstVassal, MaxTotal), 1e-9);
        }

        [TestMethod]
        public void MaxVassals_CombinedTakeHitsTheMaxTotalRate()
        {
            Assert.AreEqual(1.0, CombinedTake(AllegianceManager.MaxDirectVassals), 1e-9);
        }

        [TestMethod]
        public void PerVassalShare_StrictlyDecreasesAsVassalsAreAdded()
        {
            for (var n = 1; n < AllegianceManager.MaxDirectVassals; n++)
            {
                var current = AllegianceManager.CalculatePatronPassupRate(n, FirstVassal, MaxTotal);
                var next = AllegianceManager.CalculatePatronPassupRate(n + 1, FirstVassal, MaxTotal);

                Assert.IsTrue(next < current, $"per-vassal share did not fall from {n} to {n + 1} vassals: {current} -> {next}");
            }
        }

        [TestMethod]
        public void CombinedTake_StrictlyIncreasesButNeverExceedsMaxAtTheCap()
        {
            for (var n = 1; n < AllegianceManager.MaxDirectVassals; n++)
                Assert.IsTrue(CombinedTake(n) < CombinedTake(n + 1), $"combined take did not rise from {n} to {n + 1} vassals");

            for (var n = 1; n <= AllegianceManager.MaxDirectVassals; n++)
                Assert.IsTrue(CombinedTake(n) <= MaxTotal + 1e-9, $"combined take at {n} vassals exceeded the max total: {CombinedTake(n)}");
        }

        [TestMethod]
        public void ZeroVassals_IsTreatedAsOne()
        {
            // TotalVassals should never be 0 on a patron node that is receiving passup, but the
            // curve must not divide by zero or go infinite if a stale tree says otherwise.
            Assert.AreEqual(FirstVassal, AllegianceManager.CalculatePatronPassupRate(0, FirstVassal, MaxTotal), 1e-9);
        }

        [TestMethod]
        public void PastTheCap_KeepsDiminishingRatherThanSnappingBack()
        {
            var atCap = AllegianceManager.CalculatePatronPassupRate(AllegianceManager.MaxDirectVassals, FirstVassal, MaxTotal);
            var pastCap = AllegianceManager.CalculatePatronPassupRate(AllegianceManager.MaxDirectVassals + 5, FirstVassal, MaxTotal);

            Assert.IsTrue(pastCap < atCap);
        }

        [TestMethod]
        public void ZeroFirstVassalRate_DisablesPassupEntirely()
        {
            for (var n = 1; n <= AllegianceManager.MaxDirectVassals; n++)
                Assert.AreEqual(0.0, AllegianceManager.CalculatePatronPassupRate(n, 0.0, MaxTotal), 1e-12);
        }

        [TestMethod]
        public void MaxTotalBelowFirstVassalRate_FlattensInsteadOfInverting()
        {
            // A misconfigured max-total must not produce a rate that climbs with vassal count.
            // Clamped to the first-vassal rate, the combined take simply stays flat at 25%.
            for (var n = 1; n <= AllegianceManager.MaxDirectVassals; n++)
                Assert.AreEqual(FirstVassal / n, AllegianceManager.CalculatePatronPassupRate(n, FirstVassal, 0.1), 1e-9, $"n={n}");
        }

        [TestMethod]
        public void RaisingMaxTotal_RaisesEveryRateAboveOneVassal()
        {
            for (var n = 2; n <= AllegianceManager.MaxDirectVassals; n++)
            {
                var baseline = AllegianceManager.CalculatePatronPassupRate(n, FirstVassal, MaxTotal);
                var raised = AllegianceManager.CalculatePatronPassupRate(n, FirstVassal, MaxTotal * 2.0);

                Assert.IsTrue(raised > baseline, $"raising allegiance_passup_max_total did not raise the rate at {n} vassals");
            }
        }

        [TestMethod]
        public void MisconfiguredTunables_NeverPassUpMoreThanTheVassalEarned()
        {
            // Both endpoints are admin-set, and the ulong cast in DoPassXP saturates rather than
            // throwing, so an unclamped rate would silently mint XP.
            var absurd = new[] { 5.0, 1000.0, double.MaxValue, double.PositiveInfinity };

            foreach (var maxTotal in absurd)
            {
                foreach (var first in new[] { 0.25, 1.0, 5.0, double.PositiveInfinity })
                {
                    for (var n = 1; n <= AllegianceManager.MaxDirectVassals + 20; n++)
                    {
                        var rate = AllegianceManager.CalculatePatronPassupRate(n, first, maxTotal);

                        Assert.IsTrue(rate >= 0.0 && rate <= 1.0, $"rate out of range for first={first}, maxTotal={maxTotal}, n={n}: {rate}");
                    }
                }
            }
        }

        [TestMethod]
        public void NegativeOrNaNTunables_PassUpNothing()
        {
            Assert.AreEqual(0.0, AllegianceManager.CalculatePatronPassupRate(5, -1.0, MaxTotal), 1e-12);
            Assert.AreEqual(0.0, AllegianceManager.CalculatePatronPassupRate(5, double.NaN, MaxTotal), 1e-12);
            Assert.AreEqual(0.0, AllegianceManager.CalculatePatronPassupRate(5, FirstVassal, double.NaN), 1e-12);
        }

        [TestMethod]
        public void GrandPatronTakesAFifthOfThePatronShare()
        {
            Assert.AreEqual(0.05, FirstVassal * AllegianceManager.GrandPatronPassupShare, 1e-9);

            var atCap = AllegianceManager.CalculatePatronPassupRate(AllegianceManager.MaxDirectVassals, FirstVassal, MaxTotal);

            Assert.AreEqual(0.2, atCap * AllegianceManager.MaxDirectVassals * AllegianceManager.GrandPatronPassupShare, 1e-9);
        }
    }
}
