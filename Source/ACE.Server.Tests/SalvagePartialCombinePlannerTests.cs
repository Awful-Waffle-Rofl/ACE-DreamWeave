using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Entity.AccountVault;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// SalvagePartialCombinePlanner is pure: raw ints in, planned bags out. It pours with
    /// SalvageCombine.Pour, the same core Player.TryAddSalvage now runs, so the "same math" guarantee is
    /// asserted by driving the real TryAddSalvage against constructed WorldObjects (see
    /// Plan_TwoBagPour_MatchesTheRealHandCombinePath). Like SalvageCombineTests, nothing here touches
    /// PropertyManager, the database or a live Player.
    ///
    /// Value caveat: AddSalvage (the value half of the hand path) reads PropertyManager and a Player's
    /// skills, so it cannot run here. The hand-path value oracle therefore calls SalvageCombine.ValueShare
    /// and CapValue, the two helpers AddSalvage itself calls for a bag source.
    /// </summary>
    [TestClass]
    public class SalvagePartialCombinePlannerTests
    {
        private const int Max = 100;
        private const long BigCap = 150000;

        private static (int Structure, int Iw, int Nim, int Value) Bag(int structure, int nim, int iw, int value)
            => (structure, iw, nim, value);

        private static IReadOnlyList<(int Structure, int Iw, int Nim, int Value)> Mix(params (int, int, int, int)[] bags)
            => bags.Select(b => (b.Item1, b.Item2, b.Item3, b.Item4)).ToList();

        private static IEnumerable<IReadOnlyList<(int Structure, int Iw, int Nim, int Value)>> Mixes()
        {
            // exact-100 fill, integer average (rounding is a no-op)
            yield return Mix(Bag(40, 8, 56, 4000), Bag(60, 12, 84, 6000));
            // exact-100 fill, fractional average
            yield return Mix(Bag(40, 5, 33, 4000), Bag(60, 9, 61, 6000));
            // one split: 130 -> 100 + 30
            yield return Mix(Bag(70, 10, 65, 7000), Bag(60, 9, 61, 6000));
            // three bags, 130 -> 100 + 30
            yield return Mix(Bag(50, 5, 35, 5000), Bag(50, 5, 36, 5000), Bag(30, 3, 21, 3000));
            // five bags, 150 -> 100 + 50, two splits
            yield return Mix(Bag(30, 9, 61, 3000), Bag(30, 9, 61, 3000), Bag(30, 9, 61, 3000), Bag(30, 9, 61, 3000), Bag(30, 9, 61, 3000));
            // all partial, nothing fills: 20 + 30 = 50
            yield return Mix(Bag(20, 3, 20, 2000), Bag(30, 4, 27, 3000));
            // two overflows: 99 + 99 + 99 = 297 -> 100 + 100 + 97
            yield return Mix(Bag(99, 14, 95, 9900), Bag(99, 14, 95, 9900), Bag(99, 14, 95, 9900));
        }

        // ---------------- oracle 1: structure ----------------

        [TestMethod]
        public void Plan_ConservesStructureAcrossEveryMix()
        {
            foreach (var mix in Mixes())
            {
                var plan = SalvagePartialCombinePlanner.Plan(mix, Max, BigCap);

                Assert.IsFalse(plan.Aborted);
                Assert.AreEqual(mix.Sum(b => b.Structure), plan.Outputs.Sum(o => o.Structure));

                for (var i = 0; i < plan.Outputs.Count - 1; i++)
                    Assert.IsTrue(plan.Outputs[i].IsFull, "all outputs but the last must be full");

                foreach (var o in plan.Outputs)
                    Assert.AreEqual($"Salvage ({o.Structure})", o.Name);
            }
        }

        [TestMethod]
        public void Plan_ExactHundredFill_ProducesOneFullBag()
        {
            var plan = SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 8, 56, 4000), Bag(60, 12, 84, 6000)), Max, BigCap);

            Assert.AreEqual(1, plan.Outputs.Count);
            Assert.IsTrue(plan.Outputs[0].IsFull);
            Assert.AreEqual(100, plan.Outputs[0].Structure);
        }

        // ---------------- oracle 2: no-split sums ----------------

        [TestMethod]
        public void Plan_NoSplit_IwAndNimSumsAreExactlyConserved()
        {
            // full output with an integer average (56+84)/(8+12) = 7: Player.RoundBagWorkmanship is a no-op
            var full = SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 8, 56, 4000), Bag(60, 12, 84, 6000)), Max, BigCap);
            Assert.AreEqual(140, full.Outputs.Sum(o => o.Iw));
            Assert.AreEqual(20, full.Outputs.Sum(o => o.Nim));

            // partial output (never rounded), fractional average 60/9: the sums are untouched by any rounding
            var partial = SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 5, 33, 4000), Bag(30, 4, 27, 3000)), Max, BigCap);
            Assert.AreEqual(1, partial.Outputs.Count);
            Assert.IsFalse(partial.Outputs[0].IsFull);
            Assert.AreEqual(60, partial.Outputs[0].Iw);
            Assert.AreEqual(9, partial.Outputs[0].Nim);
        }

        // ---------------- oracle 3: split averages ----------------

        [TestMethod]
        public void Plan_Split_EachOutputAverageStaysWithinTheHalfUnitPerFragmentBudget()
        {
            // every source carries the same average, 61/9, so any drift is the split's own rounding
            var srcAverage = 61.0 / 9.0;

            foreach (var count in new[] { 2, 3, 5, 7 })
            {
                var mix = Mix(Enumerable.Range(0, count).Select(_ => Bag(45, 9, 61, 4500)).ToArray());
                var plan = SalvagePartialCombinePlanner.Plan(mix, Max, BigCap);

                Assert.IsFalse(plan.Aborted);

                foreach (var o in plan.Outputs)
                {
                    // each pour contributes at most one rounded fragment of up to half a unit; a full output
                    // is additionally rounded to a whole workmanship (another half unit of average)
                    var budget = count * 0.5 / o.Nim + (o.IsFull ? 0.5 : 0.0) + 1e-9;
                    var actual = (double)o.Iw / o.Nim;

                    Assert.AreEqual(srcAverage, actual, budget, $"count={count} output {o.Name}: W={o.Iw} N={o.Nim}");
                }
            }
        }

        // ---------------- oracle 4: value and the cap ----------------

        [TestMethod]
        public void Plan_NoCapPressure_ValueOutEqualsValueInWhenNothingSplits()
        {
            var plan = SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 8, 56, 4000), Bag(60, 12, 84, 6000)), Max, BigCap);

            Assert.AreEqual(0, plan.ValueCappedOff);
            Assert.AreEqual(0, plan.ValueRoundingDelta);
            Assert.AreEqual(10000, plan.Outputs.Sum(o => (long)o.Value));
        }

        [TestMethod]
        public void Plan_ValueOutEqualsValueInMinusCappedOff_WithinSplitRounding()
        {
            foreach (var mix in Mixes())
            {
                var plan = SalvagePartialCombinePlanner.Plan(mix, Max, BigCap);

                var valueIn = mix.Sum(b => (long)b.Value);
                var valueOut = plan.Outputs.Sum(o => (long)o.Value);

                Assert.AreEqual(valueIn - plan.ValueCappedOff - plan.ValueRoundingDelta, valueOut);

                // each split rounds two fragments independently, so at most 1 per source can be off
                Assert.IsTrue(Math.Abs(plan.ValueRoundingDelta) <= mix.Count - 1,
                    $"rounding delta {plan.ValueRoundingDelta} exceeds one per pour");
            }
        }

        [TestMethod]
        public void Plan_CapBites_ValueCappedOffIsPositiveAndOutputsStayAtTheCap()
        {
            const long smallCap = 5000;

            var plan = SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 8, 56, 4000), Bag(60, 12, 84, 6000)), Max, smallCap);

            Assert.AreEqual(1, plan.Outputs.Count);
            Assert.AreEqual(5000, plan.Outputs[0].Value, "the 10000 combined value must clamp to the cap");
            Assert.AreEqual(5000, plan.ValueCappedOff);
            Assert.AreEqual(10000, plan.Outputs.Sum(o => (long)o.Value) + plan.ValueCappedOff + plan.ValueRoundingDelta);
        }

        // ---------------- oracle 5: same math as the hand path ----------------

        private static uint nextGuid = 0x7D100000;
        private static uint nextWcid = 994000;

        private static WorldObject MakeBag(int? structure, int? numItems, int? workmanship)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Salvage" } },
            };

            var bag = new GenericObject(weenie, new ObjectGuid(nextGuid++))
            {
                NumItemsInMaterial = numItems,
                ItemWorkmanship = workmanship,
            };

            bag.MaxStructure = 100;
            bag.Structure = structure == null ? (ushort?)null : (ushort)structure.Value;

            return bag;
        }

        private static Player MakeInertPlayer() => (Player)FormatterServices.GetUninitializedObject(typeof(Player));

        [TestMethod]
        public void Plan_TwoBagPour_MatchesTheRealHandCombinePath()
        {
            var dest = Bag(70, 10, 65, 7000);
            var src = Bag(60, 9, 61, 6000);

            var plan = SalvagePartialCombinePlanner.Plan(new[] { dest, src }, Max, BigCap);
            Assert.AreEqual(2, plan.Outputs.Count);

            // the hand path: AddSalvage's loop around the shipped TryAddSalvage
            var player = MakeInertPlayer();
            var target = MakeBag(dest.Structure, dest.Nim, dest.Iw);
            var source = MakeBag(src.Structure, src.Nim, src.Iw);
            var bags = new List<WorldObject> { target };
            var values = new List<int> { dest.Value };

            var remaining = src.Structure;
            while (remaining > 0)
            {
                var bag = bags.FirstOrDefault(b => (b.Structure ?? 0) < (b.MaxStructure ?? 0));
                if (bag == null)
                {
                    bag = MakeBag(null, null, null);
                    bags.Add(bag);
                    values.Add(0);
                }

                var added = player.TryAddSalvage(bag, source, remaining);
                remaining -= added;

                // AddSalvage's value step for a bag source
                var idx = bags.IndexOf(bag);
                values[idx] = SalvageCombine.CapValue(values[idx], SalvageCombine.ValueShare(src.Value, added, src.Structure), (int)BigCap, out _);
            }

            Assert.AreEqual(bags.Count, plan.Outputs.Count);

            for (var i = 0; i < bags.Count; i++)
            {
                var o = plan.Outputs[i];
                var handIw = bags[i].ItemWorkmanship.Value;
                var handNim = bags[i].NumItemsInMaterial.Value;

                if (SalvageForge.IsFullBag(bags[i]))
                    handIw = Player.RoundBagWorkmanship(handIw, handNim);

                Assert.AreEqual((int)bags[i].Structure.Value, o.Structure, $"output {i} structure");
                Assert.AreEqual(handIw, o.Iw, $"output {i} IW");
                Assert.AreEqual(handNim, o.Nim, $"output {i} NIM");
                Assert.AreEqual(values[i], o.Value, $"output {i} value");
                Assert.AreEqual(bags[i].Name, o.Name, $"output {i} name");
            }
        }

        // ---------------- oracle 6: idempotence ----------------

        [TestMethod]
        public void Plan_ReplanningTheOutputs_IsANoOp()
        {
            foreach (var mix in Mixes())
            {
                var plan = SalvagePartialCombinePlanner.Plan(mix, Max, BigCap);

                var partials = plan.Outputs
                    .Where(o => !o.IsFull)
                    .Select(o => (o.Structure, o.Iw, o.Nim, o.Value))
                    .ToList();

                Assert.IsTrue(partials.Count <= 1, "at most one partial may remain");

                var again = SalvagePartialCombinePlanner.Plan(partials, Max, BigCap);
                Assert.IsTrue(again.IsNoOp);
                Assert.AreEqual(0, again.Outputs.Count);
            }
        }

        [TestMethod]
        public void Plan_FewerThanTwoInputs_IsANoOp()
        {
            Assert.IsTrue(SalvagePartialCombinePlanner.Plan(Mix(), Max, BigCap).IsNoOp);
            Assert.IsTrue(SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 8, 56, 4000)), Max, BigCap).IsNoOp);
        }

        // ---------------- oracle 7: abort ----------------

        [TestMethod]
        public void Plan_OutputQuotientOutsideOneToTen_AbortsTheWholeGroup()
        {
            // 200 / 10 = 20 per item
            var plan = SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 5, 100, 4000), Bag(60, 5, 100, 6000)), Max, BigCap);

            Assert.IsTrue(plan.Aborted);
            Assert.IsFalse(plan.IsNoOp);
            Assert.AreEqual(0, plan.Outputs.Count);

            var low = SalvagePartialCombinePlanner.Plan(Mix(Bag(40, 5, 2, 4000), Bag(60, 5, 2, 6000)), Max, BigCap);
            Assert.IsTrue(low.Aborted);
        }

        // ---------------- oracle 8: eligibility ----------------

        [TestMethod]
        public void IsEligible_AcceptsAWellFormedPartial()
        {
            Assert.IsTrue(SalvagePartialCombinePlanner.IsEligible(40, 56, 8, "Salvage (40)", Max));
        }

        [TestMethod]
        public void IsEligible_RejectsFullEmptyAndOverfullBags()
        {
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(100, 700, 100, "Salvage (100)", Max));
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(0, 56, 8, "Salvage (0)", Max));
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(120, 700, 100, "Salvage (120)", Max));
        }

        [TestMethod]
        public void IsEligible_RejectsRenamedBags()
        {
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 56, 8, "My Stash", Max));
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 56, 8, "Salvage (41)", Max));
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 56, 8, null, Max));
        }

        [TestMethod]
        public void IsEligible_RejectsMissingOrNonPositiveIwAndNim()
        {
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, null, 8, "Salvage (40)", Max));
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 56, null, "Salvage (40)", Max));
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 0, 8, "Salvage (40)", Max));
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 56, 0, "Salvage (40)", Max));
        }

        [TestMethod]
        public void Plan_SingleItemSplit_CountsTheItemOnBothSides_MatchesHandCombine()
        {
            // DELIBERATE PIN (owner ruling 2026-10-11): when a Nim 1 source is split, Player.SplitSalvage counts
            // the single item on BOTH sides, exactly as hand-combining does. Mixing workmanship with single-item
            // bags can therefore raise the structure-weighted average: 120 structure at 5.0 in (3 and 7), out
            // 100 at 5.0 plus 20 at 7.0, which weighs (500 + 140) / 120 = 5.33. Do not "fix" this here without changing the hand path too.
            // Inputs are in the store's order: Structure desc, then Iw, Nim, Value ascending.
            var plan = SalvagePartialCombinePlanner.Plan(Mix(Bag(60, 1, 3, 500), Bag(60, 1, 7, 500)), Max, BigCap);

            Assert.IsFalse(plan.Aborted);
            Assert.AreEqual(2, plan.Outputs.Count);

            var full = plan.Outputs[0];
            Assert.IsTrue(full.IsFull);
            Assert.AreEqual(100, full.Structure);
            Assert.AreEqual(10, full.Iw, "3 + 7, then RoundBagWorkmanship(10, 2) = 5 * 2");
            Assert.AreEqual(2, full.Nim);

            var rest = plan.Outputs[1];
            Assert.IsFalse(rest.IsFull);
            Assert.AreEqual(20, rest.Structure);
            Assert.AreEqual(7, rest.Iw, "the split source keeps its own average");
            Assert.AreEqual(1, rest.Nim, "the single item is counted on both sides");
        }
        [TestMethod]
        public void IsEligible_RejectsQuotientOutsideOneToTen_AndAcceptsTheEdges()
        {
            Assert.IsTrue(SalvagePartialCombinePlanner.IsEligible(40, 79, 8, "Salvage (40)", Max));    // 9.875
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 81, 8, "Salvage (40)", Max));   // 10.125
            Assert.IsFalse(SalvagePartialCombinePlanner.IsEligible(40, 7, 8, "Salvage (40)", Max));    // 0.875
            Assert.IsTrue(SalvagePartialCombinePlanner.IsEligible(40, 80, 8, "Salvage (40)", Max));    // exactly 10
            Assert.IsTrue(SalvagePartialCombinePlanner.IsEligible(40, 8, 8, "Salvage (40)", Max));     // exactly 1
        }
    }
}
