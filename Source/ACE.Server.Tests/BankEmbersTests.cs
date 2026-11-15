using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Embers in the bank: banked like Marks of the Hopeslayer, and bonded, so never transferred.</summary>
    [TestClass]
    public class BankEmbersTests
    {
        [TestMethod]
        public void EmberTokens_AreRefusedForTransfer()
        {
            foreach (var token in new[] { "e", "ember", "embers" })
            {
                Assert.IsTrue(Player.IsEmberToken(token));
                Assert.IsTrue(Player.TryGetBankTransferRefusal(token, out var message), token);
                Assert.AreEqual(Player.EmbersTransferRefusedMessage, message);
            }

            Assert.IsFalse(Player.IsEmberToken("m"));
        }

        [TestMethod]
        public void Embers_HaveTheirOwnBankProperty_AndName()
        {
            Assert.AreEqual(9029, (int)PropertyInt64.BankedEmbers);
            Assert.AreEqual(ACE.Server.Frays.FraysDefs.EmberWcid, Player.EmberWcid);
            Assert.AreEqual("Embers", Player.GetAlternateCurrencyName(Player.EmberWcid));
        }
    }
}
