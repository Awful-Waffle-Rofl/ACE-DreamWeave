using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// VaultSearch - the match rule shared by the vendor panel's filter (plain /mule search) and the
    /// /mule search all chat report, and the budgeted Scan the report runs per mule. The panel half is
    /// still pinned by the PersonalVendorTests SetSearchFilter/LastRebuild* tests; these cover the
    /// shared rule and Scan directly.
    /// </summary>
    [TestClass]
    public class VaultSearchTests
    {
        private const uint SwordWcid = 91001;
        private const uint ShieldWcid = 91002;
        private const uint UnknownWcid = 91003;

        private static Regex Compile(string pattern)
        {
            Assert.IsTrue(ItemTextSearch.TryCompile(pattern, out var regex, out var error), error);
            return regex;
        }

        private static Weenie NamedWeenie(uint wcid, string name) => new Weenie
        {
            WeenieClassId = wcid,
            PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
        };

        private static readonly Dictionary<uint, Weenie> Weenies = new Dictionary<uint, Weenie>
        {
            { SwordWcid, NamedWeenie(SwordWcid, "Ancient Sword") },
            { ShieldWcid, NamedWeenie(ShieldWcid, "Kite Shield") },
        };

        private static Weenie Lookup(uint wcid) => Weenies.TryGetValue(wcid, out var w) ? w : null;

        private static VaultSearchBudget Unlimited() => new VaultSearchBudget(TimeSpan.FromSeconds(1), () => TimeSpan.Zero);

        [TestMethod]
        public void Matches_LedgerRow_IsMatchedAgainstItsWeenie()
        {
            Assert.IsTrue(VaultSearch.Matches(Compile("sword"), VaultEntry.ForLedger(SwordWcid, 3), Lookup));
            Assert.IsFalse(VaultSearch.Matches(Compile("sword"), VaultEntry.ForLedger(ShieldWcid, 3), Lookup));
        }

        [TestMethod]
        public void Matches_LedgerRowWithAnUnresolvableWeenie_IsNoMatchNotAThrow()
        {
            Assert.IsFalse(VaultSearch.Matches(Compile(".*"), VaultEntry.ForLedger(UnknownWcid, 1), Lookup));
        }

        [TestMethod]
        public void Matches_StoredItem_IsMatchedAgainstTheLiveWorldObject()
        {
            var item = FakeVaultWorld.MakeStack(92001, 4, 100);   // named "Test Item 92001"

            Assert.IsTrue(VaultSearch.Matches(Compile("test item"), VaultEntry.ForItem(item), Lookup));
            Assert.IsFalse(VaultSearch.Matches(Compile("sword"), VaultEntry.ForItem(item), Lookup));
        }

        [TestMethod]
        public void Scan_KeepsOnlyTheFirstHits_ButCountsEveryMatch()
        {
            var entries = Enumerable.Range(0, 8).Select(_ => VaultEntry.ForLedger(SwordWcid, 1))
                .Concat(new[] { VaultEntry.ForLedger(ShieldWcid, 1) })
                .ToList();

            var result = VaultSearch.Scan(entries, Compile("sword"), 5, Unlimited(), Lookup);

            Assert.AreEqual(8, result.Total);
            Assert.AreEqual(5, result.Hits.Count);
            Assert.IsFalse(result.BudgetHit);
            Assert.AreEqual("Ancient Sword", result.Hits[0].Name);
        }

        [TestMethod]
        public void Scan_CarriesTheEntryCount_ForTheXnSuffix()
        {
            var result = VaultSearch.Scan(new[] { VaultEntry.ForLedger(SwordWcid, 12) }, Compile("sword"), 5, Unlimited(), Lookup);

            Assert.AreEqual(12, result.Hits.Single().Count);
        }

        /// <summary>
        /// The budget is checked before each entry, so an exhausted budget stops the scan with the flag
        /// set and the total as a lower bound. Time is injected: the clock reads 0, 100, 200, 300 ms on
        /// successive checks against a 250 ms limit, so exactly three entries are examined.
        /// </summary>
        [TestMethod]
        public void Scan_StopsWhenTheBudgetIsSpent_AndSaysSo()
        {
            var reads = 0;
            var budget = new VaultSearchBudget(TimeSpan.FromMilliseconds(250), () => TimeSpan.FromMilliseconds(100 * reads++));

            var entries = Enumerable.Range(0, 10).Select(_ => VaultEntry.ForLedger(SwordWcid, 1)).ToList();

            var result = VaultSearch.Scan(entries, Compile("sword"), 5, budget, Lookup);

            Assert.IsTrue(result.BudgetHit);
            Assert.AreEqual(3, result.Total);
        }

        [TestMethod]
        public void Scan_OnNullEntries_ReturnsAnEmptyResult()
        {
            var result = VaultSearch.Scan(null, Compile("x"), 5, Unlimited(), Lookup);

            Assert.AreEqual(0, result.Total);
            Assert.AreEqual(0, result.Hits.Count);
        }
    }
}
