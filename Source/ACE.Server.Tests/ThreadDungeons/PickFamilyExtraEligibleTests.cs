using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// PickFamily's optional extraEligible list (stretched families): null/empty is byte-identical (result and draws on
    /// both streams), extras join the ordinal-sorted draw with the same preference weighting, a forced family found in
    /// the extras skips the band test.
    /// </summary>
    [TestClass]
    public class PickFamilyExtraEligibleTests
    {
        private const int Level = 100;

        private static SpeciesTableDef Table(string id, int level)
            => new SpeciesTableDef
            {
                Id = id,
                Members = new List<SpeciesMemberDef> { new SpeciesMemberDef { Wcid = (uint)(id[0] * 10), Role = 0, Level = level } },
            };

        private static readonly SpeciesTableDef A = Table("a", 100);
        private static readonly SpeciesTableDef B = Table("b", 100);
        private static readonly SpeciesTableDef C = Table("c", 10);

        private static readonly Dictionary<string, SpeciesTableDef> Tables =
            new Dictionary<string, SpeciesTableDef> { ["a"] = A, ["b"] = B, ["c"] = C };

        private static int LevelOf(uint wcid) => Tables.Values.SelectMany(t => t.Members).First(m => m.Wcid == wcid).Level;

        private static SpeciesTableDef Pick(string forced, Random rng, IReadOnlyList<string> families = null,
            Random burn = null, IReadOnlyCollection<SpeciesTableDef> extra = null, bool useExtraArg = true)
            => useExtraArg
                ? DungeonRosterSelector.PickFamily(Tables, forced, families, null, Level, LevelOf, rng, null, false, burn, extra)
                : DungeonRosterSelector.PickFamily(Tables, forced, families, null, Level, LevelOf, rng, null, false, burn);

        [TestMethod]
        public void Null_and_empty_extra_are_byte_identical()
        {
            for (var s = 1; s <= 50; s++)
            {
                var r0 = new Random(s); var b0 = new Random(s + 1000);
                var baseline = Pick(null, r0, null, b0, null, useExtraArg: false);

                var r1 = new Random(s); var b1 = new Random(s + 1000);
                var viaNull = Pick(null, r1, null, b1, null);

                var r2 = new Random(s); var b2 = new Random(s + 1000);
                var viaEmpty = Pick(null, r2, null, b2, Array.Empty<SpeciesTableDef>());

                Assert.AreSame(new[] { A, B }[new Random(s).Next(2)], baseline, "literal pick, seed " + s);
                Assert.AreSame(baseline, viaNull, "seed " + s);
                Assert.AreSame(baseline, viaEmpty, "seed " + s);
                var nextR = r0.Next();
                var nextB = b0.Next();
                Assert.AreEqual(nextR, r1.Next(), "rng draw count, null, seed " + s);
                Assert.AreEqual(nextR, r2.Next(), "rng draw count, empty, seed " + s);
                Assert.AreEqual(nextB, b1.Next(), "burn draw count, null, seed " + s);
                Assert.AreEqual(nextB, b2.Next(), "burn draw count, empty, seed " + s);
            }
        }

        [TestMethod]
        public void Extra_eligible_families_join_the_draw_in_ordinal_order()
        {
            var all = new[] { A, B, C };
            for (var s = 1; s <= 50; s++)
                Assert.AreSame(all[new Random(s).Next(3)], Pick(null, new Random(s), extra: new[] { C }), "seed " + s);
        }

        [TestMethod]
        public void A_family_in_both_lists_counts_once()
        {
            var native = new[] { A, B };
            for (var s = 1; s <= 50; s++)
                Assert.AreSame(native[new Random(s).Next(2)], Pick(null, new Random(s), extra: new[] { A }), "seed " + s);
        }

        [TestMethod]
        public void A_preferred_extra_family_is_weighted_like_a_native()
        {
            var prefs = new[] { "c" };
            for (var s = 1; s <= 50; s++)
            {
                var roll = new Random(s).Next(5); // a=1, b=1, c=3
                var expected = roll < 1 ? A : roll < 2 ? B : C;
                var burn = new Random(s + 7);
                Assert.AreSame(expected, Pick(null, new Random(s), prefs, burn, new[] { C }), "seed " + s);
            }
        }

        [TestMethod]
        public void Null_and_empty_extra_are_identical_with_themedOnly_and_a_preferred_family()
        {
            var prefs = new[] { "b" };
            for (var s = 1; s <= 50; s++)
            {
                var r0 = new Random(s); var b0 = new Random(s + 1000);
                var baseline = DungeonRosterSelector.PickFamily(Tables, null, prefs, null, Level, LevelOf, r0, null, true, b0);
                var r1 = new Random(s); var b1 = new Random(s + 1000);
                var viaEmpty = DungeonRosterSelector.PickFamily(Tables, null, prefs, null, Level, LevelOf, r1, null, true, b1, Array.Empty<SpeciesTableDef>());

                Assert.AreSame(B, baseline, "themed draw is over the preferred family alone, seed " + s);
                Assert.AreSame(B, viaEmpty, "seed " + s);
                var mirror = new Random(s);
                mirror.Next(3);
                var mirrorBurn = new Random(s + 1000);
                mirrorBurn.Next(3);
                Assert.AreEqual(mirror.Next(), r1.Next(), "rng, seed " + s);
                Assert.AreEqual(mirrorBurn.Next(), b1.Next(), "burn, seed " + s);
            }
        }

        [TestMethod]
        public void Extras_are_re_sorted_ordinally_even_when_they_sort_before_the_natives()
        {
            var bNative = Table("b", 100);
            var cNative = Table("c", 100);
            var aExtra = Table("a", 10);
            var local = new Dictionary<string, SpeciesTableDef> { ["b"] = bNative, ["c"] = cNative };
            Func<uint, int> levelOf = w => local.Values.SelectMany(t => t.Members).First(m => m.Wcid == w).Level;
            var expected = new[] { aExtra, bNative, cNative };

            for (var s = 1; s <= 50; s++)
                Assert.AreSame(expected[new Random(s).Next(3)],
                    DungeonRosterSelector.PickFamily(local, null, null, null, Level, levelOf, new Random(s), null, false, null, new[] { aExtra }), "single extra, seed " + s);

            // Multi-element extra supplied in reverse order: natives only b; extras d, c, a.
            var local2 = new Dictionary<string, SpeciesTableDef> { ["b"] = bNative };
            var dExtra = Table("d", 10);
            var cExtra = Table("c", 10);
            var expected2 = new[] { aExtra, bNative, cExtra, dExtra };
            for (var s = 1; s <= 50; s++)
                Assert.AreSame(expected2[new Random(s).Next(4)],
                    DungeonRosterSelector.PickFamily(local2, null, null, null, Level, levelOf, new Random(s), null, false, null, new[] { dExtra, cExtra, aExtra }), "reverse extras, seed " + s);
        }

        [TestMethod]
        public void Forced_family_in_extra_is_returned_without_a_band_test()
        {
            Assert.AreSame(C, Pick("c", new Random(1), extra: new[] { C }));
            Assert.ThrowsExactly<ArgumentException>(() => Pick("c", new Random(1)));
            Assert.ThrowsExactly<ArgumentException>(() => Pick("c", new Random(1), extra: new[] { A }));
        }
    }
}
