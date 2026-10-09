using ACE.Server.MlTreasure;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.MlTreasure
{
    /// <summary>
    /// TreasureMapHandler.ClampDigProgress - the only piece of the handler that is pure (everything
    /// else needs a live Player, which ACE.Server.Tests cannot construct). Code review 2026-09-10,
    /// finding 2: FinishDig can return without consuming the map on three paths (boss variant,
    /// zero-currency-wcid, full pack), each of which is a Dig stage the player can reach again, so the
    /// stored PropertyInt.TreasureMapDigProgress must never climb past digSteps.
    /// </summary>
    [TestClass]
    public class TreasureMapHandlerTests
    {
        [TestMethod]
        public void ClampDigProgress_BelowDigSteps_PassesThrough()
        {
            Assert.AreEqual(2, TreasureMapHandler.ClampDigProgress(rawProgress: 2, digSteps: 3));
        }

        [TestMethod]
        public void ClampDigProgress_AtDigSteps_PassesThrough()
        {
            Assert.AreEqual(3, TreasureMapHandler.ClampDigProgress(rawProgress: 3, digSteps: 3));
        }

        /// <summary>The exact repeated-use case: a map already at digSteps is used again (payout
        /// refused on one of FinishDig's non-consuming paths), and the raw increment would read
        /// digSteps + 1. The stored value must stay pinned at digSteps.</summary>
        [TestMethod]
        public void ClampDigProgress_OnePastDigSteps_ClampsToDigSteps()
        {
            Assert.AreEqual(3, TreasureMapHandler.ClampDigProgress(rawProgress: 4, digSteps: 3));
        }

        /// <summary>Repeated re-use keeps climbing the raw increment (5, 6, 7, ...) every time the
        /// stored value is read back and incremented again - the clamp must hold at every one of those,
        /// not just the first step past.</summary>
        [TestMethod]
        public void ClampDigProgress_ManyStepsPast_StaysClampedAtDigSteps()
        {
            Assert.AreEqual(3, TreasureMapHandler.ClampDigProgress(rawProgress: 10, digSteps: 3));
        }
    }
}
