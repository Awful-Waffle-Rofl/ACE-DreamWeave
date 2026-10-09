using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two pure helpers behind lossless salvage-bag combining: Player.RoundBagWorkmanship, which makes a
    /// completed bag's displayed workmanship a whole number, and Player.SplitSalvage, which divides a source
    /// bag between the bag being poured into and the remainder that spills to the next one.
    ///
    /// Both are static and take only integers, so nothing here needs a Player, a session, the database, or
    /// PropertyManager. That is deliberate: PropertyManager reads throw on a cache miss under the unit-test
    /// host, so the salvage logic was written to keep its arithmetic out of reach of any tunable.
    ///
    /// The invariant every assertion is written against is the one the player actually sees: WorldObject's
    /// Workmanship getter reports ItemWorkmanship / NumItemsInMaterial, so a bag's AVERAGE is the quantity
    /// that must be preserved, and NumItemsInMaterial is a divisor that must never reach zero.
    ///
    /// The real Player.TryAddSalvage IS driven directly, against constructed WorldObjects, in the
    /// "real TryAddSalvage" region at the bottom. That matters because SimulateCombine re-creates the
    /// combine branch rather than calling it, so on its own it would pass even if TryAddSalvage went
    /// back to returning tryAmount (the original data-loss bug) or swapped which SplitSalvage output
    /// went where. Those tests are the ones that fail if the shipped method regresses.
    ///
    /// What is NOT covered here, because it needs a live Player with inventory and networking:
    ///   - HandleSalvaging end to end, including where the rounding pass sits relative to the item
    ///     loop, and the GetSalvageBag call that chooses the destination.
    /// </summary>
    [TestClass]
    public class SalvageCombineTests
    {
        /// <summary>A salvage bag's default capacity when its weenie carries no MaxStructure.</summary>
        private const int MaxStructure = 100;

        // ---------------- RoundBagWorkmanship ----------------

        [TestMethod]
        public void RoundBagWorkmanship_FractionalAverage_RoundsToNearestWhole()
        {
            // 672 / 100 = 6.72, which the player sees as a fractional workmanship
            var result = Player.RoundBagWorkmanship(672, 100);

            Assert.AreEqual(700, result, "6.72 should round up to a flat 7 across 100 items");
            Assert.AreEqual(7.0, (double)result / 100, 1e-9);
        }

        [TestMethod]
        public void RoundBagWorkmanship_FractionalAverage_RoundsDownWhenBelowMidpoint()
        {
            // 628 / 100 = 6.28
            var result = Player.RoundBagWorkmanship(628, 100);

            Assert.AreEqual(600, result);
            Assert.AreEqual(6.0, (double)result / 100, 1e-9);
        }

        [TestMethod]
        public void RoundBagWorkmanship_ExactMidpoint_RoundsAwayFromZeroNotToEven()
        {
            // 13 / 2 = 6.5 exactly. Banker's rounding (Math.Round's default) would send this to 6.
            var result = Player.RoundBagWorkmanship(13, 2);

            Assert.AreEqual(14, result, "6.5 must round to 7, not to the even 6 that banker's rounding gives");
            Assert.AreEqual(7.0, (double)result / 2, 1e-9);

            // and the same at an even/odd pair going the other way: 7 / 2 = 3.5 -> 4, not 4 by luck
            Assert.AreEqual(8, Player.RoundBagWorkmanship(7, 2));
        }

        [TestMethod]
        public void RoundBagWorkmanship_ClampsBelowOneUpToOne()
        {
            // an average of 0 would fall outside [1, 10] and trip the Workmanship getter's legacy branch
            var result = Player.RoundBagWorkmanship(0, 5);

            Assert.AreEqual(5, result);
            Assert.AreEqual(1.0, (double)result / 5, 1e-9);
        }

        [TestMethod]
        public void RoundBagWorkmanship_ClampsAboveTenDownToTen()
        {
            var result = Player.RoundBagWorkmanship(500, 5);   // average 100

            Assert.AreEqual(50, result);
            Assert.AreEqual(10.0, (double)result / 5, 1e-9);
        }

        [TestMethod]
        public void RoundBagWorkmanship_ZeroOrNegativeItemCount_TreatedAsOne()
        {
            Assert.AreEqual(7, Player.RoundBagWorkmanship(7, 0));
            Assert.AreEqual(7, Player.RoundBagWorkmanship(7, -3));
        }

        [TestMethod]
        public void RoundBagWorkmanship_AlwaysDividesEvenlyAndStaysInLegalRange()
        {
            for (var n = 1; n <= 60; n++)
            {
                for (var w = 0; w <= 12 * n; w++)
                {
                    var result = Player.RoundBagWorkmanship(w, n);

                    Assert.AreEqual(0, result % n, $"W={w} N={n}: {result} is not a whole multiple of the item count");

                    var avg = result / n;
                    Assert.IsTrue(avg >= 1 && avg <= 10, $"W={w} N={n}: average {avg} is outside the legal [1, 10] range");
                }
            }
        }

        // ---------------- SplitSalvage: full transfer ----------------

        [TestMethod]
        public void SplitSalvage_FullTransfer_MovesEverythingAndLeavesNoRemainder()
        {
            Player.SplitSalvage(9, 63, 90, 90, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

            Assert.AreEqual(9, moveNum);
            Assert.AreEqual(63, moveWorkmanship);
            Assert.AreEqual(0, remNum, "a full transfer must leave nothing behind");
            Assert.AreEqual(0, remWorkmanship);
        }

        [TestMethod]
        public void SplitSalvage_MoreSpaceThanNeeded_IsStillAFullTransfer()
        {
            Player.SplitSalvage(4, 28, 100, 40, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

            Assert.AreEqual(4, moveNum);
            Assert.AreEqual(28, moveWorkmanship);
            Assert.AreEqual(0, remNum);
            Assert.AreEqual(0, remWorkmanship);
        }

        [TestMethod]
        public void SplitSalvage_NonPositiveTryAmount_IsAFullTransfer()
        {
            Player.SplitSalvage(3, 21, 0, 0, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

            Assert.AreEqual(3, moveNum);
            Assert.AreEqual(21, moveWorkmanship);
            Assert.AreEqual(0, remNum);
            Assert.AreEqual(0, remWorkmanship);
        }

        // ---------------- SplitSalvage: partial transfer ----------------

        [TestMethod]
        public void SplitSalvage_PartialTransfer_BothSidesKeepTheSourceAverage()
        {
            const int srcNum = 10;
            const int srcWorkmanship = 65;      // average 6.5
            const double srcAverage = 6.5;

            Player.SplitSalvage(srcNum, srcWorkmanship, 30, 100, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

            Assert.AreEqual(3, moveNum, "30 of 100 units is 30% of a 10 item source");
            Assert.AreEqual(7, remNum);

            AssertAverageWithinRounding(srcAverage, moveWorkmanship, moveNum, "moved");
            AssertAverageWithinRounding(srcAverage, remWorkmanship, remNum, "remainder");
        }

        [TestMethod]
        public void SplitSalvage_PartialTransfer_MoveNumTracksTheStructureRatio()
        {
            Player.SplitSalvage(100, 700, 50, 100, out var moveNum, out _, out var remNum, out _);

            Assert.AreEqual(50, moveNum, "half the structure should carry half the items");
            Assert.AreEqual(50, remNum);
        }

        /// <summary>
        /// Regression test for the old `if (prevNumItems == newItems) newItems--;` bug: with a single-item
        /// source, the decrement drove the transferred count to 0, so the source bag kept its whole
        /// NumItemsInMaterial and workmanship while the destination bag had ALSO received them - the item
        /// count and workmanship were duplicated outright.
        /// </summary>
        [TestMethod]
        public void SplitSalvage_SingleItemSource_PartialTransfer_CountsOneOnBothSides()
        {
            Player.SplitSalvage(1, 7, 30, 100, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

            Assert.AreEqual(1, moveNum, "the destination must receive at least one item");
            Assert.AreEqual(1, remNum, "the remainder must keep at least one item - it is the Workmanship getter's divisor");

            Assert.AreEqual(7.0, (double)moveWorkmanship / moveNum, 1e-9);
            Assert.AreEqual(7.0, (double)remWorkmanship / remNum, 1e-9);
        }

        [TestMethod]
        public void SplitSalvage_PartialTransfer_NeitherSideIsEverZeroItems()
        {
            for (var srcNum = 1; srcNum <= 25; srcNum++)
            {
                for (var added = 1; added < MaxStructure; added++)
                {
                    var srcWorkmanship = 7 * srcNum;

                    Player.SplitSalvage(srcNum, srcWorkmanship, added, MaxStructure, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

                    Assert.IsTrue(moveNum >= 1, $"srcNum={srcNum} added={added}: moveNum was {moveNum}");
                    Assert.IsTrue(remNum >= 1, $"srcNum={srcNum} added={added}: remNum was {remNum}");

                    AssertAverageWithinRounding(7.0, moveWorkmanship, moveNum, $"moved (srcNum={srcNum} added={added})");
                    AssertAverageWithinRounding(7.0, remWorkmanship, remNum, $"remainder (srcNum={srcNum} added={added})");
                }
            }
        }

        [TestMethod]
        public void SplitSalvage_PartialTransfer_PreservesAverageAcrossFractionalSources()
        {
            // 61 / 9 = 6.777..., an average that cannot survive as an integer pair on either side
            const int srcNum = 9;
            const int srcWorkmanship = 61;
            var srcAverage = (double)srcWorkmanship / srcNum;

            for (var added = 1; added < MaxStructure; added++)
            {
                Player.SplitSalvage(srcNum, srcWorkmanship, added, MaxStructure, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

                AssertAverageWithinRounding(srcAverage, moveWorkmanship, moveNum, $"moved (added={added})");
                AssertAverageWithinRounding(srcAverage, remWorkmanship, remNum, $"remainder (added={added})");
            }
        }

        // ---------------- the spill loop ----------------

        /// <summary>
        /// One salvage bag as AddSalvage's loop sees it: how much structure it holds, how many source items
        /// went into it, and their summed workmanship.
        /// </summary>
        private class SimBag
        {
            public int Structure;
            public int Num;
            public int Workmanship;

            public double Average => Num == 0 ? 0.0 : (double)Workmanship / Num;
        }

        /// <summary>
        /// Re-creates AddSalvage's `while (remaining &gt; 0)` loop and the bag-combining half of
        /// TryAddSalvage, driving the real Player.SplitSalvage. GetSalvageBag's rule is reproduced too: the
        /// first bag with space, otherwise a fresh one.
        /// </summary>
        private static List<SimBag> SimulateCombine(SimBag destination, int srcStructure, int srcNum, int srcWorkmanship)
        {
            var bags = new List<SimBag> { destination };

            var remaining = srcStructure;
            var itemNum = srcNum;
            var itemWorkmanship = srcWorkmanship;

            while (remaining > 0)
            {
                var bag = bags.FirstOrDefault(b => b.Structure < MaxStructure);
                if (bag == null)
                {
                    bag = new SimBag();
                    bags.Add(bag);
                }

                var space = MaxStructure - bag.Structure;
                var amount = Math.Min(remaining, space);

                bag.Structure += amount;

                Player.SplitSalvage(itemNum, itemWorkmanship, amount, remaining, out var moveNum, out var moveWorkmanship, out var remNum, out var remWorkmanship);

                if (amount < remaining)
                {
                    itemNum = remNum;
                    itemWorkmanship = remWorkmanship;
                }

                bag.Workmanship += moveWorkmanship;
                bag.Num += moveNum;

                remaining -= amount;
            }

            return bags;
        }

        /// <summary>
        /// The headline case: pouring a 90 structure bag into another 90 structure bag must leave a full bag
        /// AND an 80 structure bag. Before this change the 80 was silently destroyed.
        /// </summary>
        [TestMethod]
        public void Combine_NinetyIntoNinety_ProducesAFullBagAndAnEightyBag()
        {
            var destination = new SimBag { Structure = 90, Num = 9, Workmanship = 63 };   // average 7

            var bags = SimulateCombine(destination, 90, 9, 63);

            Assert.AreEqual(2, bags.Count, "the overflow must spill into a second bag, not vanish");
            Assert.AreEqual(MaxStructure, bags[0].Structure);
            Assert.AreEqual(80, bags[1].Structure, "the 80 units that used to be destroyed");

            Assert.AreEqual(180, bags.Sum(b => b.Structure), "structure must be conserved");

            foreach (var bag in bags)
                Assert.AreEqual(7.0, bag.Average, 1e-9, "an average of exactly 7 survives the split exactly");
        }

        [TestMethod]
        public void Combine_ConservesStructureAndAverageAcrossEveryFillLevel()
        {
            // a source average that does not divide evenly, so every split has to round
            const int srcNum = 9;
            const int srcWorkmanship = 61;
            var srcAverage = (double)srcWorkmanship / srcNum;

            for (var destFill = 1; destFill < MaxStructure; destFill++)
            {
                for (var srcStructure = 1; srcStructure <= MaxStructure; srcStructure++)
                {
                    // the destination starts holding content at the same average, so any drift is the split's
                    var destNum = 9;
                    var destWorkmanship = 61;

                    var destination = new SimBag { Structure = destFill, Num = destNum, Workmanship = destWorkmanship };

                    var bags = SimulateCombine(destination, srcStructure, srcNum, srcWorkmanship);

                    Assert.AreEqual(destFill + srcStructure, bags.Sum(b => b.Structure),
                        $"destFill={destFill} srcStructure={srcStructure}: structure was lost or invented");

                    foreach (var bag in bags)
                    {
                        Assert.IsTrue(bag.Num >= 1, $"destFill={destFill} srcStructure={srcStructure}: a bag ended with {bag.Num} items");

                        AssertAverageWithinRounding(srcAverage, bag.Workmanship, bag.Num,
                            $"bag (destFill={destFill} srcStructure={srcStructure})");
                    }
                }
            }
        }

        // ---------------- the real TryAddSalvage ----------------

        private static uint nextGuid = 0x7D000000;   // static guid range, clear of GuidManager
        private static uint nextWcid = 993000;

        /// <summary>
        /// A salvage bag as a real WorldObject, built from an in-memory weenie so nothing here touches the
        /// database or the dat files (GenericObject's SetEphemeralValues only special-cases wcid 4142).
        ///
        /// MaxStructure 100 is not a test convenience: the 98 material weenies in ace_world that carry a
        /// MaxStructure all carry exactly 100 (verified by query, PropertyInt 91), and GetSalvageBag's
        /// reuse filter reads MaxStructure directly, so a bag without one would never be poured into at
        /// all. Passing null exercises TryAddSalvage's DefaultMaxStructure fallback instead.
        /// </summary>
        private static WorldObject MakeBag(int? structure, int? numItems, int? workmanship, int? maxStructure = 100)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Salvage" } },
            };

            var bag = new GenericObject(weenie, new ObjectGuid(nextGuid++))
            {
                NumItemsInMaterial = numItems,
                ItemWorkmanship = workmanship,
            };

            bag.MaxStructure = maxStructure == null ? (ushort?)null : (ushort)maxStructure.Value;
            bag.Structure = structure == null ? (ushort?)null : (ushort)structure.Value;

            return bag;
        }

        /// <summary>
        /// TryAddSalvage is an instance method on Player, but its body never reads or writes any instance
        /// state - no skill lookup, no session, no inventory, no networking. An uninitialized Player is
        /// therefore a sufficient and honest receiver, and is the pattern this assembly already uses for
        /// exactly this situation (MuleSummonTests.cs, ten call sites, with the rationale in its class
        /// remarks). It is safe here for the same stated reason it is safe there: no Player member is
        /// touched, so Player's static initializer - which would hit a live World database - never runs.
        /// </summary>
        private static Player MakeInertPlayer() => (Player)FormatterServices.GetUninitializedObject(typeof(Player));

        /// <summary>
        /// The regression test for the original data-loss bug. TryAddSalvage used to return tryAmount
        /// rather than the structure it actually moved, so AddSalvage's `remaining -= added` went straight
        /// to 0 and the loop dropped the overflow on the floor.
        /// </summary>
        [TestMethod]
        public void TryAddSalvage_PartialTransfer_ReturnsOnlyTheStructureItMoved()
        {
            var player = MakeInertPlayer();

            var destination = MakeBag(90, 9, 63);   // 10 units of space
            var source = MakeBag(90, 9, 63);

            var added = player.TryAddSalvage(destination, source, 90);

            Assert.AreEqual(10, added, "the return value is what the caller subtracts from remaining - returning the full 90 is what destroyed the overflow");
            Assert.AreEqual((ushort)100, destination.Structure);
        }

        [TestMethod]
        public void TryAddSalvage_PartialTransfer_RewritesTheSourceDownToTheRemainder()
        {
            var player = MakeInertPlayer();

            var destination = MakeBag(90, 9, 63);   // average 7
            var source = MakeBag(90, 9, 63);

            player.TryAddSalvage(destination, source, 90);

            // 10 of 90 units carried 1 of 9 items and 7 of 63 workmanship
            Assert.AreEqual(10, destination.NumItemsInMaterial);
            Assert.AreEqual(70, destination.ItemWorkmanship);
            Assert.AreEqual(7.0, (double)destination.ItemWorkmanship.Value / destination.NumItemsInMaterial.Value, 1e-9);

            Assert.AreEqual(8, source.NumItemsInMaterial, "the source must be rewritten down for the next pass, not left whole");
            Assert.AreEqual(56, source.ItemWorkmanship);
            Assert.AreEqual(7.0, (double)source.ItemWorkmanship.Value / source.NumItemsInMaterial.Value, 1e-9);

            Assert.AreEqual("Salvage (100)", destination.Name);
        }

        [TestMethod]
        public void TryAddSalvage_FullTransfer_MovesEverythingAndLeavesTheSourceUntouched()
        {
            var player = MakeInertPlayer();

            var destination = MakeBag(0, null, null);
            var source = MakeBag(90, 9, 63);

            var added = player.TryAddSalvage(destination, source, 90);

            Assert.AreEqual(90, added);
            Assert.AreEqual((ushort)90, destination.Structure);
            Assert.AreEqual(9, destination.NumItemsInMaterial);
            Assert.AreEqual(63, destination.ItemWorkmanship);

            // a full transfer must NOT rewrite the source - remNum is 0, and a bag with
            // NumItemsInMaterial 0 divides by zero in the Workmanship getter
            Assert.AreEqual(9, source.NumItemsInMaterial);
            Assert.AreEqual(63, source.ItemWorkmanship);
        }

        [TestMethod]
        public void TryAddSalvage_NoMaxStructure_FallsBackToTheDefaultCapacity()
        {
            var player = MakeInertPlayer();

            var destination = MakeBag(90, 9, 63, maxStructure: null);
            var source = MakeBag(90, 9, 63);

            var added = player.TryAddSalvage(destination, source, 90);

            Assert.AreEqual(10, added, "a bag with no MaxStructure must still cap at SalvageForge.DefaultMaxStructure");
            Assert.AreEqual((ushort)100, destination.Structure);
        }

        /// <summary>
        /// The headline case driven through the SHIPPED method: AddSalvage's loop, with GetSalvageBag's
        /// "first bag with space, else a fresh one" rule, pouring a 90 structure bag into another one.
        /// This is the test that fails if TryAddSalvage's return value regresses, because the loop then
        /// exits after a single pass and the second bag is never created.
        /// </summary>
        [TestMethod]
        public void TryAddSalvage_DrivenAsAddSalvageDoes_ProducesAFullBagAndAnEightyBag()
        {
            var player = MakeInertPlayer();

            var source = MakeBag(90, 9, 63);                            // average 7
            var bags = new List<WorldObject> { MakeBag(90, 9, 63) };    // average 7

            var remaining = 90;
            var passes = 0;

            while (remaining > 0)
            {
                Assert.IsTrue(++passes <= 10, "the spill loop did not terminate");

                // GetSalvageBag's rule, reproduced: the first bag with space, otherwise a new one. A fresh
                // bag arrives with Structure, ItemWorkmanship and NumItemsInMaterial nulled, as GetSalvageBag
                // nulls them, while MaxStructure survives from the weenie.
                var bag = bags.FirstOrDefault(b => (b.Structure ?? 0) < (b.MaxStructure ?? 0));
                if (bag == null)
                {
                    bag = MakeBag(null, null, null);
                    bags.Add(bag);
                }

                var added = player.TryAddSalvage(bag, source, remaining);

                Assert.IsTrue(added > 0, "a zero-length transfer would spin forever");
                remaining -= added;
            }

            Assert.AreEqual(2, bags.Count, "the overflow must spill into a second bag, not vanish");
            Assert.AreEqual((ushort)100, bags[0].Structure);
            Assert.AreEqual((ushort)80, bags[1].Structure, "the 80 units that used to be destroyed");

            Assert.AreEqual(180, bags.Sum(b => (int)(b.Structure ?? 0)), "structure must be conserved");

            foreach (var bag in bags)
                Assert.AreEqual(7.0, (double)bag.ItemWorkmanship.Value / bag.NumItemsInMaterial.Value, 1e-9);
        }

        // ---------------- shared assertion ----------------

        /// <summary>
        /// A bag's average is stored as two integers, so it can only be as exact as integer rounding allows.
        /// Each contribution rounds the workmanship SUM by at most 0.5, and no bag here takes more than two
        /// contributions, so a total sum error above 1.0 is real drift rather than rounding. Expressed as an
        /// average, that budget is 1.0 / count.
        /// </summary>
        private static void AssertAverageWithinRounding(double expectedAverage, int workmanship, int count, string what)
        {
            Assert.IsTrue(count >= 1, $"{what}: item count was {count}");

            var tolerance = 1.0 / count + 1e-9;
            var actual = (double)workmanship / count;

            Assert.AreEqual(expectedAverage, actual, tolerance,
                $"{what}: average {actual} (W={workmanship} N={count}) drifted from {expectedAverage}");
        }
    }
}
