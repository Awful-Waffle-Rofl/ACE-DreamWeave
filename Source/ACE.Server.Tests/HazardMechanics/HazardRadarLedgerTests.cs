using ACE.Entity.Enum;
using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>HazardRadarLedger: the guid -> authored radar behaviour bookkeeping that makes a restore exact.</summary>
    [TestClass]
    public class HazardRadarLedgerTests
    {
        private sealed class Thing
        {
        }

        [TestMethod]
        public void HazardRadarLedger_FirstAuthoredValueWins_IncludingNull()
        {
            var ledger = new HazardRadarLedger<Thing>();
            var c = new Thing();

            Assert.IsTrue(ledger.Record(5, c, null));
            Assert.IsFalse(ledger.Record(5, c, RadarBehavior.ShowAlways), "the second record is refused");

            Assert.IsTrue(ledger.Record(6, new Thing(), RadarBehavior.ShowMovement));
            CollectionAssert.AreEquivalent(new uint[] { 5, 6 }, new System.Collections.Generic.List<uint>(ledger.Hidden));

            Assert.IsTrue(ledger.TryTake(5, out var back, out var authored));
            Assert.AreSame(c, back);
            Assert.IsNull(authored, "the authored null survived the second Record");

            Assert.IsFalse(ledger.TryTake(5, out _, out _), "a second take finds nothing");

            var rest = ledger.TakeAll();

            Assert.AreEqual(1, rest.Count);
            Assert.AreEqual(RadarBehavior.ShowMovement, rest[0].Authored);
            Assert.AreEqual(0, ledger.Hidden.Count);
            Assert.AreEqual(0, ledger.TakeAll().Count);
        }
    }
}
