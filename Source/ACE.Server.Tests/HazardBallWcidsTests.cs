using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.HazardMechanics;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The shared ball_wcids arg (HazardBallWcids) of the balls and rain mechanics: grammar, refusals, the RequiredWcids
    /// union, and the colour picker's own stream.
    /// </summary>
    [TestClass]
    public class HazardBallWcidsTests
    {
        private static readonly uint[] Colours = { 1006951, 1006957, 1006958, 1006959 };

        [TestMethod]
        public void Absent_arg_is_valid_and_falls_back_to_ball_wcid()
        {
            var args = HazardArgs.Parse("ball_wcid=1006958");

            Assert.IsTrue(HazardBallWcids.TryParse(args, out var list, out var error));
            Assert.IsNull(list);
            Assert.IsNull(error);
            Assert.IsNull(HazardBallWcids.Validate(args));
            CollectionAssert.AreEqual(new uint[] { 1006958 }, HazardBallWcids.Candidates(args, 1006958).ToArray());
        }

        [TestMethod]
        public void Parses_a_comma_list_with_spaces_trimmed()
        {
            Assert.IsTrue(HazardBallWcids.TryParse(HazardArgs.Parse("ball_wcids=1006951,1006957,1006958,1006959"), out var list, out _));
            CollectionAssert.AreEqual(Colours, list.ToArray());

            var spaced = HazardArgs.FromTokens(new[] { "ball_wcids=1006951, 1006957 ,1006958" });
            Assert.IsTrue(HazardBallWcids.TryParse(spaced, out list, out var error), error);
            CollectionAssert.AreEqual(new uint[] { 1006951, 1006957, 1006958 }, list.ToArray());

            Assert.IsTrue(HazardBallWcids.TryParse(HazardArgs.Parse("ball_wcids=1006957"), out list, out _));
            CollectionAssert.AreEqual(new uint[] { 1006957 }, list.ToArray());
        }

        [TestMethod]
        public void Refuses_bad_entries()
        {
            void Bad(string value, string fragment)
            {
                var args = HazardArgs.FromTokens(new[] { "ball_wcids=" + value });
                var error = HazardBallWcids.Validate(args);
                Assert.IsNotNull(error, $"'{value}' must be refused");
                StringAssert.Contains(error, fragment);
                Assert.IsFalse(HazardBallWcids.TryParse(args, out var list, out _));
                Assert.IsNull(list);
            }

            Bad("1006951,,1006957", "empty entry");
            Bad("1006951,", "empty entry");
            Bad("1006951,abc", "'abc' is not a wcid");
            Bad("0", "'0' is not a wcid");
            Bad("-5", "'-5' is not a wcid");
            Bad("0x10", "'0x10' is not a wcid");
            Bad("3000000000", "'3000000000' is not a wcid");
            Bad("1006951,1006951", "twice");
            Bad(string.Join(",", Enumerable.Range(1, HazardBallWcids.MaxEntries + 1)), "at most");
        }

        [TestMethod]
        public void A_bad_list_falls_back_to_the_single_wcid_for_candidates()
        {
            CollectionAssert.AreEqual(new uint[] { 77 }, HazardBallWcids.Candidates(HazardArgs.Parse("ball_wcids=1,,2"), 77).ToArray());
        }

        [TestMethod]
        public void Both_mechanics_refuse_a_bad_list_and_require_the_whole_union()
        {
            var good = HazardArgs.Parse("ball_wcids=1006951,1006957,1006958,1006959");
            foreach (var mechanic in new IHazardMechanic[] { new RollingBallMechanic(), new RainMechanic() })
            {
                var name = mechanic.GetType().Name;
                var required = mechanic.RequiredWcids(good).ToList();

                foreach (var w in Colours)
                    CollectionAssert.Contains(required, w, $"{name} requires every colour");

                CollectionAssert.Contains(required, HazardDefaults.EffectHostWcid, $"{name} still requires the effect host");
                Assert.AreEqual(Colours.Length + 1, required.Count, $"{name}: the colours and the effect host, nothing else");

                // Absent: exactly the old requirement.
                var old = mechanic.RequiredWcids(HazardArgs.Parse("ball_wcid=1006957")).ToList();
                CollectionAssert.AreEqual(new uint[] { 1006957, HazardDefaults.EffectHostWcid }, old, $"{name} without the list");

                var region = HazardRegion.Rect("t", 0x0089, 0, false, 195, 225, -225, -195, HazardGroundMode.Plane, 0f, null, 0.5f, 16f, 1f, 4f);
                var pattern = mechanic is RainMechanic ? null : "fan";
                var error = mechanic.Validate(region, HazardArgs.Parse("drop_z=14 speed=10 lead=1 ball_wcids=1006951,x"), pattern);
                Assert.IsNotNull(error, $"{name} refuses a bad list");
                StringAssert.Contains(error, "ball_wcids", name);
            }
        }

        [TestMethod]
        public void The_picker_is_its_own_deterministic_stream()
        {
            var a = HazardBallWcids.Picker(4242);
            var b = HazardBallWcids.Picker(4242);
            var picksA = Enumerable.Range(0, 64).Select(_ => HazardBallWcids.Pick(Colours, a)).ToList();
            var picksB = Enumerable.Range(0, 64).Select(_ => HazardBallWcids.Pick(Colours, b)).ToList();

            CollectionAssert.AreEqual(picksA, picksB, "same run seed, same colours");
            Assert.AreEqual(Colours.Length, picksA.Distinct().Count(), "64 picks over four colours use every colour");

            // Not the pattern stream: the picks differ from the same picks drawn from new Random(seed), which HazardContext.Rng is.
            var pattern = new Random(4242);
            var fromPattern = Enumerable.Range(0, 64).Select(_ => Colours[pattern.Next(Colours.Length)]).ToList();
            CollectionAssert.AreNotEqual(fromPattern, picksA, "the colour stream is not the pattern Rng");
            Assert.AreEqual(64, a.Draws, "one draw per ball");
        }

        [TestMethod]
        public void A_single_candidate_draws_nothing()
        {
            var picker = HazardBallWcids.Picker(1);
            Assert.AreEqual(1006957u, HazardBallWcids.Pick(new uint[] { 1006957 }, picker));
            Assert.AreEqual(0, picker.Draws);

            HazardBallWcids.Pick(Colours, picker);
            Assert.AreEqual(1, picker.Draws, "positive control: a list draws once per ball");
        }

        [TestMethod]
        public void The_picker_spreads_evenly_and_stays_in_range()
        {
            var picker = HazardBallWcids.Picker(99);
            var counts = new int[4];

            for (var i = 0; i < 4000; i++)
                counts[picker.Next(4)]++;

            foreach (var c in counts)
                Assert.IsTrue(c > 850 && c < 1150, $"roughly a quarter each: [{string.Join(", ", counts)}]");

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => picker.Next(0));
        }
    }
}
