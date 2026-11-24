using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>Pure helpers for Jewelry Aspects. No database, no dat files, no PropertyManager reads.</summary>
    [TestClass]
    public class JewelryAspectMathTests
    {
        private static uint nextGuid = 0x7F2A0000;

        private static WorldObject Item(ItemType type, EquipMask locations)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)type },
                { PropertyInt.ValidLocations, (int)locations },
                { PropertyInt.Value, 10 },
                { PropertyInt.EncumbranceVal, 1 },
            };

            var weenie = new Weenie
            {
                WeenieClassId = 1008100,
                WeenieType = WeenieType.Generic,
                PropertiesInt = ints,
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static Func<int, int> Seeded(int seed)
        {
            var rng = new Random(seed);
            return max => rng.Next(max);
        }

        private static Func<int, int> Script(params int[] draws)
        {
            var i = 0;
            return max => draws[i++];
        }

        // --- IsAspectablePiece ---

        [TestMethod] public void Piece_Ring_IsAspectable() => Assert.IsTrue(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.FingerWear)));
        [TestMethod] public void Piece_Bracelet_IsAspectable() => Assert.IsTrue(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.WristWear)));
        [TestMethod] public void Piece_Necklace_IsAspectable() => Assert.IsTrue(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.NeckWear)));
        [TestMethod] public void Piece_SingleHandRing_IsAspectable() => Assert.IsTrue(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.FingerWearLeft)));
        [TestMethod] public void Piece_Cloak_IsNot() => Assert.IsFalse(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.Cloak)));
        [TestMethod] public void Piece_Trinket_IsNot() => Assert.IsFalse(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.TrinketOne)));
        [TestMethod] public void Piece_Sigil_IsNot() => Assert.IsFalse(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.SigilOne)));
        [TestMethod] public void Piece_MixedMask_IsNot() => Assert.IsFalse(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.NeckWear | EquipMask.Cloak)));
        [TestMethod] public void Piece_ZeroLocations_IsNot() => Assert.IsFalse(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Jewelry, EquipMask.None)));
        [TestMethod] public void Piece_NotJewelryType_IsNot() => Assert.IsFalse(JewelryAspectMath.IsAspectablePiece(Item(ItemType.Armor, EquipMask.NeckWear)));
        [TestMethod] public void Piece_Null_IsNot() => Assert.IsFalse(JewelryAspectMath.IsAspectablePiece(null));

        // --- counts read and write ---

        [TestMethod]
        public void Counts_RoundTripThroughProperties()
        {
            var item = Item(ItemType.Jewelry, EquipMask.NeckWear);

            Assert.IsFalse(JewelryAspectMath.IsAspected(item));
            Assert.AreEqual(0, JewelryAspectMath.ReadCounts(item).Total);

            JewelryAspectMath.WriteCounts(item, new AspectCounts(3, 5, 2));

            var read = JewelryAspectMath.ReadCounts(item);
            Assert.AreEqual(3, read.Attack);
            Assert.AreEqual(5, read.Defense);
            Assert.AreEqual(2, read.Utility);
            Assert.IsTrue(JewelryAspectMath.IsAspected(item));
        }

        [TestMethod]
        public void IsAspected_AcceptsOneThroughTen_AndRefusesZeroOverTenAndNegative()
        {
            var item = Item(ItemType.Jewelry, EquipMask.NeckWear);

            JewelryAspectMath.WriteCounts(item, new AspectCounts(0, 0, 0));
            Assert.IsFalse(JewelryAspectMath.IsAspected(item), "no aspect yet");

            JewelryAspectMath.WriteCounts(item, new AspectCounts(0, 1, 0));
            Assert.IsTrue(JewelryAspectMath.IsAspected(item), "the first aspect");

            JewelryAspectMath.WriteCounts(item, new AspectCounts(3, 3, 3));
            Assert.IsTrue(JewelryAspectMath.IsAspected(item), "nine");

            JewelryAspectMath.WriteCounts(item, new AspectCounts(4, 4, 3));
            Assert.IsFalse(JewelryAspectMath.IsAspected(item), "eleven is over the cap");

            JewelryAspectMath.WriteCounts(item, new AspectCounts(12, -2, 0));
            Assert.IsFalse(JewelryAspectMath.IsAspected(item), "a negative count");

            JewelryAspectMath.WriteCounts(item, new AspectCounts(5, -1, 0));
            Assert.IsFalse(JewelryAspectMath.IsAspected(item), "a negative count even with a small total");

            JewelryAspectMath.WriteCounts(item, new AspectCounts(10, 0, 0));
            Assert.IsTrue(JewelryAspectMath.IsAspected(item), "ten");
        }

        [TestMethod]
        public void Add_AddsExactlyOneOfTheDrawnType()
        {
            var counts = new AspectCounts(1, 2, 3);

            var after = JewelryAspectMath.Add(counts, Script(1), out var added);

            Assert.AreEqual(AspectType.Defense, added);
            Assert.AreEqual(1, after.Attack);
            Assert.AreEqual(3, after.Defense);
            Assert.AreEqual(3, after.Utility);
            Assert.AreEqual(counts.Total + 1, after.Total);
        }

        [TestMethod]
        public void Add_FromEmptyToTen_NeverSwaps_AndEveryStepGrowsByOne()
        {
            var random = new Random(11);
            var counts = new AspectCounts();

            for (var step = 1; step <= 10; step++)
            {
                var before = counts;
                counts = JewelryAspectMath.Add(counts, n => random.Next(n), out var added);

                Assert.AreEqual(step, counts.Total);
                Assert.AreEqual(before[added] + 1, counts[added]);

                foreach (AspectType type in Enum.GetValues(typeof(AspectType)))
                {
                    if (type != added)
                        Assert.AreEqual(before[type], counts[type], "an add never moves another slot");
                }
            }
        }

        [TestMethod]
        public void Add_AtTenOrInvalid_Throws_AndBadDrawThrows()
        {
            Assert.Throws<ArgumentException>(() => JewelryAspectMath.Add(new AspectCounts(4, 3, 3), Script(0), out _));
            Assert.Throws<ArgumentException>(() => JewelryAspectMath.Add(new AspectCounts(-1, 3, 3), Script(0), out _));
            Assert.Throws<InvalidOperationException>(() => JewelryAspectMath.Add(new AspectCounts(1, 0, 0), n => 3, out _));
        }

        [TestMethod] public void IsAspected_Null_IsFalse() => Assert.IsFalse(JewelryAspectMath.IsAspected(null));

        [TestMethod]
        public void AspectCounts_IndexerAndTotal()
        {
            var c = new AspectCounts(1, 2, 3);
            Assert.AreEqual(6, c.Total);
            Assert.AreEqual(1, c[AspectType.Attack]);
            Assert.AreEqual(2, c[AspectType.Defense]);
            Assert.AreEqual(3, c[AspectType.Utility]);
            c[AspectType.Defense] = 9;
            Assert.AreEqual(9, c.Defense);
        }

        // --- SumWorn ---

        private static WorldObject Worn(EquipMask worn, int attack, int defense, int utility)
        {
            var item = Item(ItemType.Jewelry, EquipMask.NeckWear);
            JewelryAspectMath.WriteCounts(item, new AspectCounts(attack, defense, utility));
            item.CurrentWieldedLocation = worn;
            return item;
        }

        [TestMethod]
        public void SumWorn_NeckPiece_IsSummed()
        {
            var sum = JewelryAspectMath.SumWorn(new[] { Worn(EquipMask.NeckWear, 2, 3, 5) });

            Assert.AreEqual(2, sum.Attack);
            Assert.AreEqual(3, sum.Defense);
            Assert.AreEqual(5, sum.Utility);
        }

        [TestMethod]
        public void SumWorn_AddsAcrossNeckWristAndFinger()
        {
            var sum = JewelryAspectMath.SumWorn(new[]
            {
                Worn(EquipMask.NeckWear, 10, 0, 0),
                Worn(EquipMask.WristWearLeft, 0, 10, 0),
                Worn(EquipMask.FingerWearRight, 0, 0, 10),
            });

            Assert.AreEqual(10, sum.Attack);
            Assert.AreEqual(10, sum.Defense);
            Assert.AreEqual(10, sum.Utility);
        }

        [TestMethod]
        public void SumWorn_TrinketAndCloakLocations_AreNotSummed()
        {
            Assert.AreEqual(0, JewelryAspectMath.SumWorn(new[] { Worn(EquipMask.TrinketOne, 0, 10, 0) }).Total);
            Assert.AreEqual(0, JewelryAspectMath.SumWorn(new[] { Worn(EquipMask.Cloak, 0, 10, 0) }).Total);
        }

        [TestMethod]
        public void SumWorn_UnwornPiece_IsNotSummed()
        {
            var carried = Item(ItemType.Jewelry, EquipMask.NeckWear);
            JewelryAspectMath.WriteCounts(carried, new AspectCounts(0, 10, 0));

            Assert.AreEqual(0, JewelryAspectMath.SumWorn(new[] { carried }).Total);
        }

        [TestMethod]
        public void SumWorn_UnaspectedPiece_ContributesNothing()
        {
            var plain = Item(ItemType.Jewelry, EquipMask.NeckWear);
            plain.CurrentWieldedLocation = EquipMask.NeckWear;

            Assert.AreEqual(0, JewelryAspectMath.SumWorn(new[] { plain }).Total, "no counts at all");
            Assert.AreEqual(0, JewelryAspectMath.SumWorn(new[] { Worn(EquipMask.NeckWear, 0, 0, 0) }).Total, "zero aspects");
            Assert.AreEqual(0, JewelryAspectMath.SumWorn(new[] { Worn(EquipMask.NeckWear, 6, 4, 1) }).Total, "over ten is invalid");
            Assert.AreEqual(0, JewelryAspectMath.SumWorn(new[] { Worn(EquipMask.NeckWear, 5, -1, 0) }).Total, "a negative count is invalid");
        }

        [TestMethod]
        public void SumWorn_PartiallyFilledPiece_Counts()
        {
            var sum = JewelryAspectMath.SumWorn(new[] { Worn(EquipMask.NeckWear, 2, 3, 0), Worn(EquipMask.FingerWearLeft, 0, 0, 1) });

            Assert.AreEqual(2, sum.Attack);
            Assert.AreEqual(3, sum.Defense);
            Assert.AreEqual(1, sum.Utility);
        }

        [TestMethod]
        public void SumWorn_NullSequence_IsEmpty() => Assert.AreEqual(0, JewelryAspectMath.SumWorn(null).Total);

        // --- Filling a piece: ten successive Adds ---

        /// <summary>A piece built the way play builds it: ten successive Adds from nothing.</summary>
        private static AspectCounts Fill(Func<int, int> next)
        {
            var counts = new AspectCounts();

            for (var i = 0; i < JewelryAspectMath.SlotCount; i++)
                counts = JewelryAspectMath.Add(counts, next, out _);

            return counts;
        }

        [TestMethod]
        public void Fill_AlwaysTotalsTen_Over10kSeededPieces()
        {
            var next = Seeded(1234);

            for (var i = 0; i < 10_000; i++)
            {
                var c = Fill(next);
                Assert.AreEqual(10, c.Total);
                Assert.IsTrue(c.Attack >= 0 && c.Defense >= 0 && c.Utility >= 0);
            }
        }

        [TestMethod]
        public void Fill_IsUniformOverTheThreeTypes()
        {
            var next = Seeded(99);
            long a = 0, d = 0, u = 0;
            const int pieces = 20_000;

            for (var i = 0; i < pieces; i++)
            {
                var c = Fill(next);
                a += c.Attack;
                d += c.Defense;
                u += c.Utility;
            }

            var expected = pieces * 10 / 3.0;

            // 200k draws total, ~66.7k per type; SD ~211, band is ~4.7 SD
            Assert.AreEqual(expected, a, expected * 0.015);
            Assert.AreEqual(expected, d, expected * 0.015);
            Assert.AreEqual(expected, u, expected * 0.015);
        }

        [TestMethod]
        public void Fill_DefenseCountHistogram_MatchesBinomialTenOneThird()
        {
            // 30,000 pieces of ten Adds each: a piece's Defense count is Binomial(10, 1/3). Each of the 11 bins must sit within
            // 4 SD of N*p_k, where SD = sqrt(N*p_k*(1-p_k)); a correct generator misses a 4 SD band in about 6 of 100,000 draws
            // per bin, and the seed is fixed, so the test is deterministic.
            const int pieces = 30_000;
            var next = Seeded(20261109);
            var histogram = new int[11];

            for (var i = 0; i < pieces; i++)
                histogram[Fill(next).Defense]++;

            Assert.AreEqual(pieces, histogram.Sum());

            for (var k = 0; k <= 10; k++)
            {
                var p = Choose(10, k) * Math.Pow(1.0 / 3.0, k) * Math.Pow(2.0 / 3.0, 10 - k);
                var expected = pieces * p;
                var sd = Math.Sqrt(pieces * p * (1.0 - p));

                Assert.AreEqual(expected, histogram[k], 4.0 * sd, $"Defense = {k}: observed {histogram[k]}, expected {expected:F1} +/- {4.0 * sd:F1}");
            }
        }

        private static double Choose(int n, int k)
        {
            double r = 1;

            for (var i = 1; i <= k; i++)
                r = r * (n - k + i) / i;

            return r;
        }
        // --- Swap ---

        [TestMethod]
        public void Swap_MovesExactlyOneSlot_AndNeverFromEqualsTo()
        {
            var next = Seeded(7);
            var rollNext = Seeded(8);

            for (var i = 0; i < 10_000; i++)
            {
                var before = Fill(rollNext);
                var after = JewelryAspectMath.Swap(before, next, out var from, out var to);

                Assert.AreNotEqual(from, to);
                Assert.AreEqual(10, after.Total);
                Assert.AreEqual(before[from] - 1, after[from]);
                Assert.AreEqual(before[to] + 1, after[to]);

                foreach (AspectType t in Enum.GetValues(typeof(AspectType)))
                {
                    if (t != from && t != to)
                        Assert.AreEqual(before[t], after[t]);
                }
            }
        }

        [TestMethod]
        public void Swap_NeverTakesFromAnEmptyType()
        {
            var next = Seeded(5);
            var counts = new AspectCounts(10, 0, 0);

            for (var i = 0; i < 1000; i++)
            {
                JewelryAspectMath.Swap(counts, next, out var from, out _);
                Assert.AreEqual(AspectType.Attack, from);
            }
        }

        [TestMethod]
        public void Swap_PicksFromTypeByCountWeight()
        {
            // counts 2/3/5: draw 0-1 -> Attack, 2-4 -> Defense, 5-9 -> Utility
            var counts = new AspectCounts(2, 3, 5);
            var expect = new[] { AspectType.Attack, AspectType.Attack, AspectType.Defense, AspectType.Defense, AspectType.Defense,
                AspectType.Utility, AspectType.Utility, AspectType.Utility, AspectType.Utility, AspectType.Utility };

            for (var draw = 0; draw < 10; draw++)
            {
                JewelryAspectMath.Swap(counts, Script(draw, 0), out var from, out _);
                Assert.AreEqual(expect[draw], from, "draw " + draw);
            }
        }

        [TestMethod]
        public void Swap_SecondDraw_ChoosesBetweenTheOtherTwoInEnumOrder()
        {
            var counts = new AspectCounts(4, 3, 3);

            JewelryAspectMath.Swap(counts, Script(0, 0), out var from0, out var to0);
            Assert.AreEqual(AspectType.Attack, from0);
            Assert.AreEqual(AspectType.Defense, to0);

            JewelryAspectMath.Swap(counts, Script(0, 1), out var from1, out var to1);
            Assert.AreEqual(AspectType.Attack, from1);
            Assert.AreEqual(AspectType.Utility, to1);
        }

        [TestMethod]
        public void Swap_EmptyMiddleType_FromIsAttackOrUtilityNeverDefense()
        {
            // counts 5/0/5: draws 0-4 -> Attack, 5-9 -> Utility; Defense holds nothing to give
            var counts = new AspectCounts(5, 0, 5);

            JewelryAspectMath.Swap(counts, Script(4, 0), out var fromLow, out _);
            Assert.AreEqual(AspectType.Attack, fromLow);

            JewelryAspectMath.Swap(counts, Script(5, 0), out var fromHigh, out _);
            Assert.AreEqual(AspectType.Utility, fromHigh);
        }

        [TestMethod]
        public void Swap_BadCounts_Throws()
        {
            Assert.Throws<ArgumentException>(() => JewelryAspectMath.Swap(new AspectCounts(3, 3, 3), Script(0, 0), out _, out _));
        }

        // --- Layer ---

        [TestMethod] public void Layer_TenDefenseSlots_Is10Percent() => Assert.AreEqual(0.10, JewelryAspectMath.Layer(10, 0.01, 0.50, true, false), 1e-9);
        [TestMethod] public void Layer_CapsAtCap() => Assert.AreEqual(0.50, JewelryAspectMath.Layer(100, 0.01, 0.50, true, false), 1e-9);
        [TestMethod] public void Layer_Disabled_IsZero() => Assert.AreEqual(0.0, JewelryAspectMath.Layer(30, 0.01, 0.50, false, false));
        [TestMethod] public void Layer_Pvp_IsZero() => Assert.AreEqual(0.0, JewelryAspectMath.Layer(30, 0.01, 0.50, true, true));
        [TestMethod] public void Layer_NoSlots_IsZero() => Assert.AreEqual(0.0, JewelryAspectMath.Layer(0, 0.01, 0.50, true, false));
        [TestMethod] public void Layer_CapAboveOne_ClampsToOne() => Assert.AreEqual(1.0, JewelryAspectMath.Layer(1000, 0.01, 5.0, true, false), 1e-9);

        [TestMethod]
        public void Layer_BadInputs_AreZero()
        {
            Assert.AreEqual(0.0, JewelryAspectMath.Layer(10, double.NaN, 0.5, true, false));
            Assert.AreEqual(0.0, JewelryAspectMath.Layer(10, -0.01, 0.5, true, false));
            Assert.AreEqual(0.0, JewelryAspectMath.Layer(10, double.PositiveInfinity, 0.5, true, false));
            Assert.AreEqual(0.0, JewelryAspectMath.Layer(10, 0.01, double.NaN, true, false));
            Assert.AreEqual(0.0, JewelryAspectMath.Layer(10, 0.01, -0.5, true, false));
            Assert.AreEqual(0.0, JewelryAspectMath.Layer(-3, 0.01, 0.5, true, false));
        }

        // --- Fortitude ---

        [TestMethod] public void Fortitude_Reduces10Percent() => Assert.AreEqual(90u, JewelryAspectMath.ApplyFortitude(100, 100, 0.10));
        [TestMethod] public void Fortitude_RoundsHalfAwayFromZero() => Assert.AreEqual(7u, JewelryAspectMath.ApplyFortitude(10, 10, 0.25));
        [TestMethod] public void Fortitude_OnlyEligiblePartIsReduced() => Assert.AreEqual(95u, JewelryAspectMath.ApplyFortitude(100, 50, 0.10));
        [TestMethod] public void Fortitude_ZeroEligible_ChangesNothing() => Assert.AreEqual(100u, JewelryAspectMath.ApplyFortitude(100, 0, 0.50));
        [TestMethod] public void Fortitude_FloorsAtZero() => Assert.AreEqual(0u, JewelryAspectMath.ApplyFortitude(3, 100, 1.0));
        [TestMethod] public void Fortitude_ZeroDamage_StaysZero() => Assert.AreEqual(0u, JewelryAspectMath.ApplyFortitude(0, 0, 0.5));
        [TestMethod] public void Fortitude_NaNFraction_ChangesNothing() => Assert.AreEqual(100u, JewelryAspectMath.ApplyFortitude(100, 100, double.NaN));
        [TestMethod] public void Fortitude_NegativeFraction_ChangesNothing() => Assert.AreEqual(100u, JewelryAspectMath.ApplyFortitude(100, 100, -0.5));

        // --- Penetration ---

        // k = 1 unless stated: mod' = max(mod + (1 - mod) * p for mod < 1 else mod, mod * (1 + p * k))
        [TestMethod] public void Penetrate_Resisted_MovesTowardOne() => Assert.AreEqual(0.55f, JewelryAspectMath.Penetrate(0.5f, 0.10, 1.0), 1e-6f);
        [TestMethod] public void Penetrate_Neutral_TakesBonus() => Assert.AreEqual(1.40f, JewelryAspectMath.Penetrate(1.0f, 0.40, 1.0), 1e-6f);
        [TestMethod] public void Penetrate_Weak_ScalesUp() => Assert.AreEqual(2.1f, JewelryAspectMath.Penetrate(1.5f, 0.40, 1.0), 1e-6f);
        [TestMethod] public void Penetrate_ResistedWhereAmplifyWins() => Assert.AreEqual(0.95f * 1.32f, JewelryAspectMath.Penetrate(0.95f, 0.32, 1.0), 1e-6f);
        [TestMethod] public void Penetrate_ResistedWherePenetrationWins() => Assert.AreEqual(0.52f, JewelryAspectMath.Penetrate(0.2f, 0.40, 1.0), 1e-6f);
        [TestMethod] public void Penetrate_NeverEndsBelowRemovalOnly()
        {
            foreach (var mod in new[] { 0.0f, 0.1f, 0.2f, 0.5f, 0.8f, 0.95f, 1.0f, 1.5f })
                foreach (var p in new[] { 0.05, 0.2, 0.4, 1.0 })
                    Assert.IsTrue(JewelryAspectMath.Penetrate(mod, p, 1.0) >= JewelryAspectMath.Penetrate(mod, p, 0.0) - 1e-6f, $"mod {mod} p {p}");
        }
        [TestMethod] public void Penetrate_KZero_IsRemovalOnly()
        {
            Assert.AreEqual(0.7f, JewelryAspectMath.Penetrate(0.5f, 0.40, 0.0), 1e-6f);
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(1.0f, 0.40, 0.0), 1e-6f);
            Assert.AreEqual(1.5f, JewelryAspectMath.Penetrate(1.5f, 0.40, 0.0), 1e-6f);
        }
        [TestMethod] public void Penetrate_NaNK_IsRemovalOnly()
        {
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(1.0f, 0.40, double.NaN), 1e-6f);
            Assert.AreEqual(0.7f, JewelryAspectMath.Penetrate(0.5f, 0.40, double.NaN), 1e-6f);
        }
        [TestMethod] public void Penetrate_HugeK_ClampsToTheMaximum()
        {
            var huge = JewelryAspectMath.Penetrate(1.0f, 0.4, 1e300);

            Assert.IsFalse(float.IsInfinity(huge) || float.IsNaN(huge));
            Assert.AreEqual(JewelryAspectMath.Penetrate(1.0f, 0.4, JewelryAspectMath.MaxPenetrationAmplify), huge, 1e-6f);
            Assert.AreEqual(5.0f, huge, 1e-5f, "1 + 0.4 * 10");
            Assert.AreEqual(huge, JewelryAspectMath.Penetrate(1.0f, 0.4, 11.0), 1e-6f);
        }
        [TestMethod] public void Penetrate_InfinityK_BehavesLikeZero()
        {
            Assert.AreEqual(JewelryAspectMath.Penetrate(1.0f, 0.4, 0.0), JewelryAspectMath.Penetrate(1.0f, 0.4, double.PositiveInfinity), 1e-6f);
            Assert.AreEqual(JewelryAspectMath.Penetrate(1.0f, 0.4, 0.0), JewelryAspectMath.Penetrate(1.0f, 0.4, double.NegativeInfinity), 1e-6f);
        }
        [TestMethod] public void Penetrate_NegativeK_IsRemovalOnly()
        {
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(1.0f, 0.40, -1.0), 1e-6f);
            Assert.AreEqual(1.5f, JewelryAspectMath.Penetrate(1.5f, 0.40, -0.5), 1e-6f);
        }
        [TestMethod] public void Penetrate_ZeroP_Unchanged()
        {
            Assert.AreEqual(0.5f, JewelryAspectMath.Penetrate(0.5f, 0.0, 1.0));
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(1.0f, 0.0, 1.0));
            Assert.AreEqual(1.5f, JewelryAspectMath.Penetrate(1.5f, -0.2, 1.0));
        }
        [TestMethod] public void Penetrate_NaNP_Unchanged()
        {
            Assert.AreEqual(0.5f, JewelryAspectMath.Penetrate(0.5f, double.NaN, 1.0));
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(1.0f, double.NaN, 1.0));
        }
        [TestMethod] public void Penetrate_FullP_ReachesOne() => Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(0.2f, 1.0, 0.0), 1e-6f);
        [TestMethod] public void Penetrate_PAboveOne_CappedAtOne()
        {
            Assert.AreEqual(2.0f, JewelryAspectMath.Penetrate(1.0f, 5.0, 1.0), 1e-6f);
            Assert.AreEqual(1.0f, JewelryAspectMath.Penetrate(0.2f, 5.0, 0.0), 1e-6f);
        }

        // --- Resolve (chance on kill to remove one monster-cast debuff) ---

        private const uint PlayerGuid = 0x50000001;
        private const uint OtherPlayerGuid = 0x50000002;
        private const uint MonsterGuid = 0x80000100;

        private static PropertiesEnchantmentRegistry Debuff(int spellId = 100, double duration = 60.0, uint caster = MonsterGuid) =>
            new PropertiesEnchantmentRegistry { SpellId = spellId, Duration = duration, CasterObjectId = caster };

        [TestMethod] public void ResolveRolls_ChanceOne_AlwaysSucceeds_EvenAtTheTopOfTheDrawRange() => Assert.IsTrue(JewelryAspectMath.ResolveRolls(1.0, 0.9999999999));
        [TestMethod] public void ResolveRolls_ChanceAboveOne_AlwaysSucceeds() => Assert.IsTrue(JewelryAspectMath.ResolveRolls(1.5, 0.99));
        [TestMethod] public void ResolveRolls_ChanceZero_NeverSucceeds_EvenAtADrawOfZero() => Assert.IsFalse(JewelryAspectMath.ResolveRolls(0.0, 0.0));
        [TestMethod] public void ResolveRolls_NegativeOrNaNChance_NeverSucceeds()
        {
            Assert.IsFalse(JewelryAspectMath.ResolveRolls(-0.5, 0.0));
            Assert.IsFalse(JewelryAspectMath.ResolveRolls(double.NaN, 0.0));
        }
        [TestMethod] public void ResolveRolls_DrawBelowChance_Succeeds_DrawAtOrAboveFails()
        {
            Assert.IsTrue(JewelryAspectMath.ResolveRolls(0.2, 0.19));
            Assert.IsFalse(JewelryAspectMath.ResolveRolls(0.2, 0.2), "strictly below");
            Assert.IsFalse(JewelryAspectMath.ResolveRolls(0.2, 0.5));
            Assert.IsFalse(JewelryAspectMath.ResolveRolls(0.2, double.NaN));
        }

        [TestMethod]
        public void SelectResolvable_KeepsOnlyHarmfulTimedRealSpellMonsterCastEntries()
        {
            var monsterDebuff = Debuff(100);
            var otherMonsterDebuff = Debuff(101, caster: 0x80000200);
            var entries = new List<PropertiesEnchantmentRegistry>
            {
                monsterDebuff,
                otherMonsterDebuff,
                Debuff(102, caster: OtherPlayerGuid),   // PvP debuff
                Debuff(103, caster: PlayerGuid),        // self-cast
                Debuff(104, caster: 0),                 // unknown caster
                Debuff(105, duration: -1),              // vitae or item-sourced
                Debuff(short.MaxValue + 1),             // pseudo-spell
                Debuff(106),                            // beneficial (isHarmful false below)
            };

            var result = JewelryAspectMath.SelectResolvable(entries, PlayerGuid, id => id != 106);

            CollectionAssert.AreEquivalent(new[] { monsterDebuff, otherMonsterDebuff }, result);
        }

        [TestMethod]
        public void SelectResolvable_NullInputs_AreEmpty()
        {
            Assert.AreEqual(0, JewelryAspectMath.SelectResolvable(null, PlayerGuid, id => true).Count);
            Assert.AreEqual(0, JewelryAspectMath.SelectResolvable(new[] { Debuff() }, PlayerGuid, null).Count);
        }

        [TestMethod]
        public void SelectAllLayers_TakesEveryEligibleLayerInTheChosenCategory_ButNotOtherCategoriesOrIneligibleCopies()
        {
            var chosen = Debuff(100, caster: 0x80000100);
            chosen.SpellCategory = SpellCategory.AcidResistanceLowering;
            var otherMonsterCopy = Debuff(100, caster: 0x80000200);
            otherMonsterCopy.SpellCategory = SpellCategory.AcidResistanceLowering;
            var playerCopy = Debuff(100, caster: OtherPlayerGuid);
            playerCopy.SpellCategory = SpellCategory.AcidResistanceLowering;
            var otherSpell = Debuff(101, caster: 0x80000100);
            otherSpell.SpellCategory = SpellCategory.AcidResistanceLowering;
            var otherCategory = Debuff(100, caster: 0x80000300);
            otherCategory.SpellCategory = SpellCategory.FireResistanceLowering;
            var itemCopy = Debuff(100, duration: -1, caster: 0x80000400);
            itemCopy.SpellCategory = SpellCategory.AcidResistanceLowering;

            var layers = JewelryAspectMath.SelectAllLayers(chosen, new[] { chosen, otherMonsterCopy, playerCopy, otherSpell, otherCategory, itemCopy }, PlayerGuid, id => true);

            CollectionAssert.AreEquivalent(new[] { chosen, otherMonsterCopy, otherSpell }, layers, "a different spell id in the same category IS taken; the player, item and other-category copies are not");
        }

        [TestMethod]
        public void SelectAllLayers_AlwaysIncludesTheChosenEntry_AndIsEmptyForNull()
        {
            var chosen = Debuff(100);

            CollectionAssert.AreEqual(new[] { chosen }, JewelryAspectMath.SelectAllLayers(chosen, new List<PropertiesEnchantmentRegistry>(), PlayerGuid, id => true));
            Assert.AreEqual(0, JewelryAspectMath.SelectAllLayers(null, new[] { chosen }, PlayerGuid, id => true).Count);
        }

        [TestMethod]
        public void PickOne_ChoosesTheDrawnIndex_AndIsNullWhenEmpty()
        {
            var list = new List<PropertiesEnchantmentRegistry> { Debuff(1), Debuff(2), Debuff(3) };
            var seen = new List<(int Min, int Max)>();

            var pick = JewelryAspectMath.PickOne(list, (min, max) => { seen.Add((min, max)); return 2; });

            Assert.AreSame(list[2], pick);
            Assert.AreEqual((0, 2), seen.Single(), "the draw is inclusive: 0 .. Count - 1");
            Assert.IsNull(JewelryAspectMath.PickOne(new List<PropertiesEnchantmentRegistry>(), (min, max) => 0));
            Assert.IsNull(JewelryAspectMath.PickOne(null, (min, max) => 0));
        }

        [TestMethod]
        public void PickOne_EveryEntryIsReachable_WithTheRealRandom()
        {
            var list = new List<PropertiesEnchantmentRegistry> { Debuff(1), Debuff(2), Debuff(3) };
            var hits = new HashSet<int>();

            for (var i = 0; i < 400; i++)
                hits.Add(JewelryAspectMath.PickOne(list, ACE.Common.ThreadSafeRandom.Next).SpellId);

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, hits.ToList());
        }
    }
}