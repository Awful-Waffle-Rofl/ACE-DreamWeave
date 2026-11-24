using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// VaultMarketItemStore.ClassifyBarrelFailure, fed the store's own refusal constants rather than
    /// retyped strings, so a reworded store message cannot silently fall through to the default.
    ///
    /// The bug this pins (Discord bug thread 1569220583785234502): a barrel at its cap was classified
    /// as barrel_unavailable, so the market told the player "try again in a moment" about a barrel
    /// that stays full until an administrator empties it.
    /// </summary>
    [TestClass]
    public class MarketBarrelFailureClassifyTests
    {
        [TestMethod]
        public void BarrelFullMessage_ClassifiesAsBarrelFull()
        {
            Assert.AreEqual(MarketError.BarrelFull, VaultMarketItemStore.ClassifyBarrelFailure(AccountVaultStore.BarrelFullMessage));
        }

        [TestMethod]
        public void BarrelUnavailableMessage_StillClassifiesAsBarrelUnavailable()
        {
            // The control: the transient refusal keeps its old code, so the assertion above is not a
            // tautology of every barrel refusal now reading as full.
            Assert.AreEqual(MarketError.BarrelUnavailable, VaultMarketItemStore.ClassifyBarrelFailure(AccountVaultStore.BarrelUnavailableMessage));
        }

        [TestMethod]
        public void OtherRefusals_KeepTheirClassification()
        {
            Assert.AreEqual(MarketError.ItemListed, VaultMarketItemStore.ClassifyBarrelFailure(AccountVaultStore.ItemListedMessage));
            Assert.AreEqual(MarketError.VaultUnavailable, VaultMarketItemStore.ClassifyBarrelFailure("some refusal the classifier does not know"));
        }

        [TestMethod]
        public void BarrelFull_HasItsOwnCodeStatusAndText()
        {
            Assert.AreEqual("barrel_full", MarketErrorCodes.ToCode(MarketError.BarrelFull));
            Assert.AreEqual(409, MarketApiHost.StatusFor(MarketError.BarrelFull));

            // The old text is kept for the transient case and must not be what a full barrel says.
            Assert.AreEqual("The barrel is unavailable, try again in a moment.", MarketErrorCodes.ToMessage(MarketError.BarrelUnavailable));
            Assert.AreEqual(503, MarketApiHost.StatusFor(MarketError.BarrelUnavailable));

            var full = MarketErrorCodes.ToMessage(MarketError.BarrelFull);
            StringAssert.Contains(full, "full");
            StringAssert.Contains(full, "will not help until an administrator empties it");
            Assert.IsFalse(full.Contains("try again in a moment"), "a full barrel must not invite a retry");
            Assert.IsFalse(full.Contains("Nothing was destroyed"), "the web's bulk barrel joins this per failed row beside rows that went");
        }
    }
}
