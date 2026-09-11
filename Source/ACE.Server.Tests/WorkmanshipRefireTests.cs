using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.Factories.Tables;
using ACE.Server.RefireStations;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Refire Forge's pure classification, roll and cost-split rules.
    ///
    /// A Player/WorldObject cannot be constructed in a test in this repo (see SpellRerollTests' remarks), so
    /// this only exercises the parts of WorkmanshipReforgeStation that need no live server: item-type/
    /// workmanship eligibility (IsEligibleItem), the never-repeats reroll helper (RollDifferent), the pack/
    /// bank cost split (SplitCost), and the roll source - WorkmanshipChance.Roll(6), used unmodified per the
    /// design intent recorded in WorkmanshipReforgeStation's class remarks.
    /// </summary>
    [TestClass]
    public class WorkmanshipRefireTests
    {
        // ---------------- eligibility ----------------

        [TestMethod]
        public void IsEligibleItem_FalseForNullWorkmanship()
        {
            Assert.IsFalse(WorkmanshipReforgeStation.IsEligibleItem(ItemType.Armor, null));
        }

        [TestMethod]
        public void IsEligibleItem_FalseForTinkeringMaterial()
        {
            // a salvage bag must never qualify, even if it somehow carried a workmanship
            Assert.IsFalse(WorkmanshipReforgeStation.IsEligibleItem(ItemType.TinkeringMaterial, 5));
        }

        [TestMethod]
        public void IsEligibleItem_FalseForGem()
        {
            Assert.IsFalse(WorkmanshipReforgeStation.IsEligibleItem(ItemType.Gem, 5));
        }

        [TestMethod]
        public void IsEligibleItem_TrueForArmorWeaponJewelryClothing()
        {
            Assert.IsTrue(WorkmanshipReforgeStation.IsEligibleItem(ItemType.Armor, 5));
            Assert.IsTrue(WorkmanshipReforgeStation.IsEligibleItem(ItemType.MeleeWeapon, 5));
            Assert.IsTrue(WorkmanshipReforgeStation.IsEligibleItem(ItemType.MissileWeapon, 5));
            Assert.IsTrue(WorkmanshipReforgeStation.IsEligibleItem(ItemType.Caster, 5));
            Assert.IsTrue(WorkmanshipReforgeStation.IsEligibleItem(ItemType.Jewelry, 5));
            Assert.IsTrue(WorkmanshipReforgeStation.IsEligibleItem(ItemType.Clothing, 5));
        }

        // ---------------- RollDifferent ----------------

        [TestMethod]
        public void RollDifferent_NeverReturnsOld_RealT6Roll()
        {
            for (var oldWorkmanship = 1; oldWorkmanship <= 10; oldWorkmanship++)
            {
                for (var i = 0; i < 2000; i++)
                {
                    var result = WorkmanshipReforgeStation.RollDifferent(oldWorkmanship, () => WorkmanshipChance.Roll(WorkmanshipReforgeStation.RollTier));

                    Assert.AreNotEqual(oldWorkmanship, result, $"RollDifferent returned the old value {oldWorkmanship}");
                }
            }
        }

        [TestMethod]
        public void RollDifferent_NeverReturnsOld_StubbedRollAlwaysReturnsOld_FallbackPath()
        {
            // degenerate roll function that always returns the old value - exhausts MaxRerollAttempts and
            // must fall back to a deterministic neighbour that still differs
            for (var oldWorkmanship = 1; oldWorkmanship <= 10; oldWorkmanship++)
            {
                var result = WorkmanshipReforgeStation.RollDifferent(oldWorkmanship, () => oldWorkmanship);

                Assert.AreNotEqual(oldWorkmanship, result, $"fallback path returned the old value {oldWorkmanship}");
            }
        }

        // ---------------- SplitCost ----------------

        [TestMethod]
        public void SplitCost_EmptyPack_AllFromBank()
        {
            var (fromPack, fromBank) = WorkmanshipReforgeStation.SplitCost(0);

            Assert.AreEqual(0, fromPack);
            Assert.AreEqual(10, fromBank);
        }

        [TestMethod]
        public void SplitCost_PartialPack_RemainderFromBank()
        {
            var (fromPack, fromBank) = WorkmanshipReforgeStation.SplitCost(4);

            Assert.AreEqual(4, fromPack);
            Assert.AreEqual(6, fromBank);
        }

        [TestMethod]
        public void SplitCost_FullPack_NothingFromBank()
        {
            var (fromPack, fromBank) = WorkmanshipReforgeStation.SplitCost(10);

            Assert.AreEqual(10, fromPack);
            Assert.AreEqual(0, fromBank);
        }

        [TestMethod]
        public void SplitCost_MoreThanCostInPack_ClampedToTotalCost()
        {
            // a pack of 15 notes still only spends the 10 the refire costs
            var (fromPack, fromBank) = WorkmanshipReforgeStation.SplitCost(15);

            Assert.AreEqual(10, fromPack);
            Assert.AreEqual(0, fromBank);
        }

        // ---------------- roll source: the T6 chance table, unmodified ----------------

        /// <summary>
        /// Large enough that the 5% P(10) assertion below clears its tolerance by a wide margin -
        /// WorkmanshipChanceTests uses the same order of magnitude (100_000) for tighter (1%) tolerances.
        /// </summary>
        private const int Rolls = 20_000;

        [TestMethod]
        public void RollTier_IsSix()
        {
            Assert.AreEqual(6, WorkmanshipReforgeStation.RollTier, "the refire must roll the T6 chance table, per the design intent recorded in the class remarks");
        }

        [TestMethod]
        public void Roll_StaysInOneToTenRange()
        {
            for (var i = 0; i < Rolls; i++)
            {
                var result = WorkmanshipChance.Roll(WorkmanshipReforgeStation.RollTier);

                Assert.IsTrue(result >= 1 && result <= 10, $"rolled workmanship {result}, outside 1-10");
            }
        }

        /// <summary>
        /// THE CENTRAL DESIGN CONSTRAINT: workmanship 10 must stay the rarest outcome, because it is what the
        /// Equipment/Weapon Mod systems want. The T6 table declares 5% at 10 (WorkmanshipChance.cs); this
        /// samples the roll itself, independent of that declared table, so a future re-tuning of T6 that
        /// silently loosens the 10-share would be caught here too.
        /// </summary>
        [TestMethod]
        public void Roll_TenShareStaysRare()
        {
            var counts = new Dictionary<int, int>();

            for (var i = 0; i < Rolls; i++)
            {
                var result = WorkmanshipChance.Roll(WorkmanshipReforgeStation.RollTier);
                counts.TryGetValue(result, out var prev);
                counts[result] = prev + 1;
            }

            counts.TryGetValue(10, out var tens);
            var share = (double)tens / Rolls;

            Assert.IsTrue(share >= 0.03 && share <= 0.07, $"P(workmanship 10) = {share:F4}, expected roughly 0.05 (T6's declared rate)");

            // and 10 must be the single rarest declared outcome among 6-10, the tier's non-trivial range
            var maxOtherShare = counts.Where(kvp => kvp.Key != 10 && kvp.Key >= 6)
                                       .Select(kvp => (double)kvp.Value / Rolls)
                                       .DefaultIfEmpty(0)
                                       .Max();

            Assert.IsTrue(share < maxOtherShare, $"workmanship 10 (share {share:F4}) is not rarer than every other 6-10 outcome (max {maxOtherShare:F4})");
        }
    }
}
