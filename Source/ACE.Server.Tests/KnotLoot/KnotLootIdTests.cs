using ACE.Entity.Enum.Properties;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// Knot treasure tables (Docs/Knots/KNOT-LOOT-DESIGN.md): the fork-allocated property ids are pinned, because items
    /// persist them on the biota and a renumber would silently orphan every stored value.
    /// </summary>
    [TestClass]
    public class KnotLootIdTests
    {
        [TestMethod]
        public void Ids_ArePinned()
        {
            Assert.AreEqual(9093, (int)PropertyInt.KnotLootLevel);
            Assert.AreEqual(9015, (int)PropertyFloat.KnotTraitCastSpeed);
            Assert.AreEqual(9016, (int)PropertyFloat.KnotTraitShieldTrade);
            Assert.AreEqual(9017, (int)PropertyFloat.KnotTraitFrugalHand);
            Assert.AreEqual(9018, (int)PropertyFloat.KnotTraitWristTrade);
            Assert.AreEqual(9020, (int)PropertyFloat.KnotTraitOverpoweringIntent);
            Assert.AreEqual(9029, (int)PropertyString.KnotLootTable);
        }
    }
}