using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity.Facets;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Where a remembered worn item is found, and in what order. The wcid fallback exists because the
    /// account vault collapses any item provably identical to its weenie template into a ledger row
    /// and DESTROYS its biota - the original guid is gone, and withdrawing rebuilds a new object with a
    /// new guid. Falling back on wcid is correct for exactly those items, because collapse only ever
    /// happens to items that are interchangeable copies of their template.
    /// </summary>
    [TestClass]
    public class FacetGearTests
    {
        private static FacetEquipEntry Entry(uint guid, uint wcid, int slot = 1)
            => new FacetEquipEntry { Guid = guid, Wcid = wcid, Slot = slot };

        private static ISet<uint> Guids(params uint[] values) => new HashSet<uint>(values);

        private static IReadOnlyDictionary<uint, int> Ledger(params (uint wcid, int count)[] rows)
        {
            var map = new Dictionary<uint, int>();

            foreach (var row in rows)
                map[row.wcid] = row.count;

            return map;
        }

        [TestMethod]
        public void Resolve_PrefersInventory_OverVault()
        {
            var result = FacetGear.Resolve(Entry(100u, 31812u), Guids(100u), Guids(100u), Ledger((31812u, 5)));

            Assert.AreEqual(FacetGearSource.Inventory, result.Source);
            Assert.AreEqual(100u, result.Guid);
        }

        [TestMethod]
        public void Resolve_UsesVaultStoredItem_WhenNotInInventory()
        {
            var result = FacetGear.Resolve(Entry(100u, 31812u), Guids(), Guids(100u), Ledger());

            Assert.AreEqual(FacetGearSource.VaultItem, result.Source);
            Assert.AreEqual(100u, result.Guid);
        }

        [TestMethod]
        public void Resolve_FallsBackToLedgerByWcid_WhenTheGuidIsGone()
        {
            var result = FacetGear.Resolve(Entry(100u, 31812u), Guids(), Guids(), Ledger((31812u, 2)));

            Assert.AreEqual(FacetGearSource.VaultLedger, result.Source);
            Assert.AreEqual(31812u, result.Wcid);
        }

        [TestMethod]
        public void Resolve_ReportsNotFound_WhenNothingHoldsIt()
        {
            var result = FacetGear.Resolve(Entry(100u, 31812u), Guids(), Guids(), Ledger((99999u, 3)));

            Assert.AreEqual(FacetGearSource.NotFound, result.Source);
        }

        [TestMethod]
        public void Resolve_ReportsNotFound_WhenTheLedgerStackIsEmpty()
        {
            var result = FacetGear.Resolve(Entry(100u, 31812u), Guids(), Guids(), Ledger((31812u, 0)));

            Assert.AreEqual(FacetGearSource.NotFound, result.Source);
        }

        [TestMethod]
        public void ResolveAll_DoesNotLetTwoEntriesClaimTheSameLedgerStack()
        {
            // One stack of two, three remembered copies: the third must come back NotFound rather than
            // silently over-withdrawing.
            var entries = new List<FacetEquipEntry> { Entry(1u, 31812u, 1), Entry(2u, 31812u, 2), Entry(3u, 31812u, 4) };

            var results = FacetGear.ResolveAll(entries, Guids(), Guids(), Ledger((31812u, 2)));

            Assert.AreEqual(3, results.Count);
            Assert.AreEqual(FacetGearSource.VaultLedger, results[0].Source);
            Assert.AreEqual(FacetGearSource.VaultLedger, results[1].Source);
            Assert.AreEqual(FacetGearSource.NotFound, results[2].Source);
        }

        [TestMethod]
        public void ResolveAll_PreservesSlotOnEveryResult()
        {
            var entries = new List<FacetEquipEntry> { Entry(1u, 31812u, 16777216) };

            var results = FacetGear.ResolveAll(entries, Guids(1u), Guids(), Ledger());

            Assert.AreEqual(16777216, results[0].Slot);
        }
    }
}
