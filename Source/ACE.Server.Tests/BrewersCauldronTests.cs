using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.RefireStations;

namespace ACE.Server.Tests
{
    /// <summary>
    /// BrewersCauldronStation's pure helpers (IsTavernBlessing, HasTavernBlessing, IsBrewable, BrewableSpells,
    /// PickRandom). A Player/WorldObject cannot be constructed in a test in this repo (see CoverageLoomTests'
    /// remarks), so VerifyEligible/Apply are not exercised here - only the pure math the station's HandleGive
    /// delegates to. levelOf/isCantrip are faked so no test touches the dat or DB.
    /// </summary>
    [TestClass]
    public class BrewersCauldronTests
    {
        // ---------------- IsTavernBlessing ----------------

        [DataTestMethod]
        [DataRow(3533, DisplayName = "Brighteyes' Favor")]
        [DataRow(3864, DisplayName = "Zongo's Fist")]
        [DataRow(3531, DisplayName = "Bobo's Quickening")]
        [DataRow(3862, DisplayName = "Duke Raoul's Pride")]
        [DataRow(3863, DisplayName = "Hunter's Hardiness")]
        [DataRow(3530, DisplayName = "Ketnan's Eye")]
        public void IsTavernBlessing_SixBlessings_AreTrue(int spellId)
        {
            Assert.IsTrue(BrewersCauldronStation.IsTavernBlessing(spellId));
        }

        [DataTestMethod]
        [DataRow(3532, DisplayName = "Bobo's Focused Blessing - not on the owner's list")]
        [DataRow(2226, DisplayName = "not on the owner's list")]
        [DataRow(2227, DisplayName = "not on the owner's list")]
        [DataRow(1, DisplayName = "arbitrary unrelated spell")]
        public void IsTavernBlessing_NonBlessings_AreFalse(int spellId)
        {
            Assert.IsFalse(BrewersCauldronStation.IsTavernBlessing(spellId));
        }

        // ---------------- HasTavernBlessing ----------------

        [TestMethod]
        public void HasTavernBlessing_EmptySpellbook_IsFalse()
        {
            var found = BrewersCauldronStation.HasTavernBlessing(new List<int>(), out var blessingId);

            Assert.IsFalse(found);
            var expectedNone = 0;
            Assert.AreEqual(expectedNone, blessingId);
        }

        [TestMethod]
        public void HasTavernBlessing_NoBlessingPresent_IsFalse()
        {
            var spellbook = new List<int> { 100, 200, 300 };

            var found = BrewersCauldronStation.HasTavernBlessing(spellbook, out var blessingId);

            Assert.IsFalse(found);
        }

        [TestMethod]
        public void HasTavernBlessing_OneBlessingPresent_ReturnsWhichId()
        {
            var expectedId = 3862;
            var spellbook = new List<int> { 100, expectedId, 300 };

            var found = BrewersCauldronStation.HasTavernBlessing(spellbook, out var blessingId);

            Assert.IsTrue(found);
            Assert.AreEqual(expectedId, blessingId);
        }

        // ---------------- IsBrewable ----------------

        [TestMethod]
        public void IsBrewable_LevelEight_IsTrue()
        {
            var isBrewable = BrewersCauldronStation.IsBrewable(500, id => 8, id => false);

            Assert.IsTrue(isBrewable);
        }

        [TestMethod]
        public void IsBrewable_LevelSeven_NotCantrip_IsFalse()
        {
            var isBrewable = BrewersCauldronStation.IsBrewable(500, id => 7, id => false);

            Assert.IsFalse(isBrewable);
        }

        [TestMethod]
        public void IsBrewable_CantripAtLevelZero_IsTrue()
        {
            var isBrewable = BrewersCauldronStation.IsBrewable(500, id => 0, id => true);

            Assert.IsTrue(isBrewable);
        }

        [TestMethod]
        public void IsBrewable_TavernBlessing_NeverBrewable()
        {
            // even if the fakes claim it is level 8 AND a cantrip, a blessing id must never be brewable
            var isBrewable = BrewersCauldronStation.IsBrewable(3530, id => 8, id => true);

            Assert.IsFalse(isBrewable);
        }

        // ---------------- BrewableSpells ----------------

        [TestMethod]
        public void BrewableSpells_FiltersAndSortsAscending()
        {
            var spellbook = new List<int> { 500, 100, 3530, 300 };

            // 500 -> level 8 (brewable), 100 -> cantrip (brewable), 3530 -> blessing (never brewable),
            // 300 -> neither
            int LevelOf(int id) => id == 500 ? 8 : 0;
            bool IsCantripFake(int id) => id == 100;

            var result = BrewersCauldronStation.BrewableSpells(spellbook, LevelOf, IsCantripFake);

            CollectionAssert.AreEqual(new List<int> { 100, 500 }, result);
        }

        [TestMethod]
        public void BrewableSpells_NoneEligible_ReturnsEmpty()
        {
            var spellbook = new List<int> { 100, 200 };

            var result = BrewersCauldronStation.BrewableSpells(spellbook, id => 1, id => false);

            var expectedCount = 0;
            Assert.AreEqual(expectedCount, result.Count);
        }

        // ---------------- PickRandom ----------------

        [TestMethod]
        public void PickRandom_NextReturnsMin_PicksFirst()
        {
            var candidates = new List<int> { 10, 20, 30 };

            var picked = BrewersCauldronStation.PickRandom(candidates, (min, max) => min);

            var expected = 10;
            Assert.AreEqual(expected, picked);
        }

        [TestMethod]
        public void PickRandom_NextReturnsMax_PicksLast()
        {
            var candidates = new List<int> { 10, 20, 30 };

            var picked = BrewersCauldronStation.PickRandom(candidates, (min, max) => max);

            var expected = 30;
            Assert.AreEqual(expected, picked);
        }

        [TestMethod]
        public void PickRandom_SingleElementList_AlwaysPicksIt()
        {
            var candidates = new List<int> { 42 };

            var picked = BrewersCauldronStation.PickRandom(candidates, (min, max) => min);

            var expected = 42;
            Assert.AreEqual(expected, picked);
        }

        // ---------------- TavernBlessings table ----------------

        [TestMethod]
        public void TavernBlessings_IsExactlyTheSixOwnerListedSpells()
        {
            var expected = new[] { 3530, 3531, 3533, 3862, 3863, 3864 };
            var actual = BrewersCauldronStation.TavernBlessings.OrderBy(i => i).ToArray();

            CollectionAssert.AreEqual(expected, actual);
        }

        [TestMethod]
        public void PickRandom_OverTavernBlessings_AlwaysReturnsABlessing()
        {
            var table = BrewersCauldronStation.TavernBlessings;

            for (var i = 0; i < table.Length; i++)
            {
                var pick = BrewersCauldronStation.PickRandom(table, (min, max) => i);
                Assert.IsTrue(BrewersCauldronStation.IsTavernBlessing(pick));
            }
        }    
        [DataTestMethod]
        [DataRow(297u)]    // ring
        [DataRow(624u)]    // ringjeweled
        [DataRow(622u)]    // necklace
        [DataRow(295u)]    // bracelet
        public void IsLootJewelryWcid_AcceptsTreasureJewelryWcids(uint wcid)
        {
            Assert.IsTrue(BrewersCauldronStation.IsLootJewelryWcid(wcid), $"wcid {wcid} should be loot jewelry");
        }

        [DataTestMethod]
        [DataRow(0u)]
        [DataRow(1u)]           // not jewelry
        [DataRow(20630u)]       // Trade Note
        [DataRow(1002759u)]     // fork wcid (Brewer's Cauldron itself) - above ushort range
        [DataRow(70000u)]       // above ushort range
        public void IsLootJewelryWcid_RejectsNonLootWcids(uint wcid)
        {
            Assert.IsFalse(BrewersCauldronStation.IsLootJewelryWcid(wcid), $"wcid {wcid} should NOT be loot jewelry");
        }

        [TestMethod]
        public void IsLootJewelryWcid_MatchesJewelryWcidsTable()
        {
            // Every wcid the treasure system can roll for jewelry is accepted; the helper is a pure wrapper.
            foreach (ACE.Server.Factories.Enum.WeenieClassName name in System.Enum.GetValues(typeof(ACE.Server.Factories.Enum.WeenieClassName)))
            {
                var expected = ACE.Server.Factories.Tables.Wcids.JewelryWcids.Contains(name);
                Assert.AreEqual(expected, BrewersCauldronStation.IsLootJewelryWcid((uint)name), name.ToString());
            }
        }
}
}
