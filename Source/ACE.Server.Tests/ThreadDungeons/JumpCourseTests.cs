using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Server.Physics.Animation;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Jump encounter's pure course generator and reach maths (JumpCourse): determinism, the rise and gap ranges, the
    /// flight-time proof that every step is reachable at the design Jump skill (derived from the server's own
    /// MovementSystem.GetJumpHeight, with an oversized-step positive control), no inflated overlap, ground refusal, and the
    /// landblock and perimeter bounds. No world, no database, no PropertyManager read.
    /// </summary>
    [TestClass]
    public class JumpCourseTests
    {
        private const ushort Lb = 0xAAB3;
        private static readonly Vector3 Npc = new Vector3(96f, 96f, 40f);

        private static float? Flat(float x, float y) => 40f;

        private static bool InLandblock(float x, float y) => JumpEncounterType.Allowed(Lb, x, y, null, 0, 0);

        private static JumpLayout Make(int seed, JumpConfig config = null, Func<float, float, float?> ground = null, Func<float, float, bool> allowed = null, Vector3? npc = null)
            => JumpCourse.Generate(config ?? new JumpConfig(), new Random(seed), npc ?? Npc, ground ?? Flat, allowed ?? InLandblock, out _);

        private static IEnumerable<JumpLayout> ManySeeds(int count = 200, JumpConfig config = null, Func<float, float, float?> ground = null)
        {
            for (var seed = 1; seed <= count; seed++)
            {
                var layout = Make(seed, config, ground);
                Assert.IsNotNull(layout, $"seed {seed}: no course on open flat ground");
                yield return layout;
            }
        }

        [TestMethod]
        public void The_design_jump_height_comes_from_the_server_formula()
        {
            // MovementSystem.GetJumpHeight: burdenMod * (S / (S + 1300) * 22.2 + 0.05) * power, floor 0.35.
            var expected = 300f / (300f + 1300f) * 22.2f + 0.05f;
            Assert.AreEqual(expected, JumpCourse.JumpHeight(300), 1e-4f);
            Assert.AreEqual(MovementSystem.GetJumpHeight(0f, 300, 1f, 1f), JumpCourse.JumpHeight(300));
            Assert.AreEqual(4.21f, JumpCourse.JumpHeight(300), 0.01f, "about 4.2 m at Jump 300");
            Assert.AreEqual(0.35f, JumpCourse.JumpHeight(0), 1e-6f, "the 0.35 m floor");
        }

        [TestMethod]
        public void The_same_seed_gives_the_same_layout()
        {
            for (var seed = 1; seed <= 25; seed++)
            {
                var a = Make(seed);
                var b = Make(seed);

                Assert.IsNotNull(a);
                CollectionAssert.AreEqual(a.Pads.ToList(), b.Pads.ToList(), $"seed {seed}");
                Assert.AreEqual(a.Goal, b.Goal);
            }

            Assert.IsFalse(Make(1).Pads.SequenceEqual(Make(2).Pads), "different seeds differ");
        }

        [TestMethod]
        public void Rises_are_within_config_and_strictly_increase()
        {
            var config = new JumpConfig();

            foreach (var layout in ManySeeds())
            {
                Assert.AreEqual(config.Platforms, layout.Pads.Count);

                for (var i = 0; i < layout.Pads.Count; i++)
                {
                    var pad = layout.Pads[i];
                    Assert.IsTrue(pad.Rise >= config.RiseMin && pad.Rise <= config.RiseMax, $"pad {i} rise {pad.Rise}");
                    Assert.AreEqual(JumpCourse.PadThickness, pad.Top - pad.Bottom, 1e-4f);

                    if (i > 0)
                    {
                        Assert.IsTrue(pad.Top > layout.Pads[i - 1].Top, $"pad {i} is higher than pad {i - 1}");
                        Assert.AreEqual(pad.Rise, pad.Top - layout.Pads[i - 1].Top, 1e-3f);
                    }
                    else
                        Assert.AreEqual(40f + pad.Rise, pad.Top, 1e-3f, "the first rises from the terrain under it");
                }

                var top = layout.TopPad;
                Assert.AreEqual(new Vector3(top.X, top.Y, top.Top + JumpCourse.GoalLift), layout.Goal, "the goal sits on the top pad's centre");
            }
        }

        [TestMethod]
        public void Gaps_are_within_config()
        {
            var config = new JumpConfig();

            foreach (var layout in ManySeeds())
            {
                for (var i = 1; i < layout.Pads.Count; i++)
                {
                    var a = layout.Pads[i - 1];
                    var b = layout.Pads[i];
                    var gap = JumpCourse.EdgeGap(b.X - a.X, b.Y - a.Y);

                    Assert.IsTrue(gap >= config.GapMin - 0.01f && gap <= config.GapMax + 0.01f, $"pads {i - 1}-{i}: gap {gap}");
                    Assert.AreEqual(b.Gap, gap, 0.01f, "the recorded gap is the measured one");
                }
            }
        }

        [TestMethod]
        public void Every_step_is_reachable_at_the_design_jump_skill()
        {
            var config = new JumpConfig();
            Assert.AreEqual(300u, config.DesignJumpSkill);

            var h = JumpCourse.JumpHeight(config.DesignJumpSkill);

            foreach (var layout in ManySeeds())
            {
                foreach (var pad in layout.Pads)
                {
                    Assert.IsTrue(JumpCourse.IsStepReachable(pad.Rise, pad.Gap, config.DesignJumpSkill, config.DesignRunSpeed, out var why), why);

                    // The proof spelled out: flight time to come down to the rise, times the run speed, covers the gap.
                    var vz = Math.Sqrt(h * 2 * 9.8);
                    var t = (vz + Math.Sqrt(vz * vz - 2 * 9.8 * pad.Rise)) / 9.8;
                    Assert.IsTrue(config.DesignRunSpeed * t >= pad.Gap + JumpCourse.LandingMargin, $"rise {pad.Rise} gap {pad.Gap}: reach {config.DesignRunSpeed * t}");
                    Assert.IsTrue(pad.Rise <= h - JumpCourse.RiseMargin);
                }
            }

            // The hardest step the default config allows.
            Assert.IsTrue(JumpCourse.IsStepReachable(config.RiseMax, config.GapMax, 300, 4f, out var hardest), hardest);
        }

        [TestMethod]
        public void A_deliberately_oversized_step_fails_the_reach_check()
        {
            // Positive controls: each must fail, or the reachability test above proves nothing.
            var h = JumpCourse.JumpHeight(300);

            Assert.IsFalse(JumpCourse.IsStepReachable(h - 0.5f, 1.5f, 300, 4f, out var tooHigh), "a rise inside the 0.8 m margin");
            StringAssert.Contains(tooHigh, "rise");
            Assert.IsFalse(JumpCourse.IsStepReachable(5f, 0f, 300, 4f, out _), "a rise above the jump height");
            Assert.IsFalse(JumpCourse.IsStepReachable(3f, 6f, 300, 4f, out var tooFar), "a 6 m gap at a 3 m rise");
            StringAssert.Contains(tooFar, "gap");
            Assert.IsTrue(JumpCourse.IsStepReachable(3f, 3f, 300, 4f, out _), "the shipped hardest step passes");
            Assert.IsFalse(JumpCourse.IsStepReachable(3f, 3f, 100, 4f, out _), "the same step is out of reach at Jump 100: heights are not scaled down");

            // An oversized config is refused at parse, so it can never reach the generator.
            var cfg = JumpEncounterType.Validate(new JumpConfig { RiseMin = 2f, RiseMax = 4f }, out var error);
            Assert.IsNull(cfg);
            StringAssert.Contains(error, "not reachable");
        }

        [TestMethod]
        public void No_two_inflated_footprints_overlap_and_none_is_near_the_npc()
        {
            var config = new JumpConfig();

            foreach (var layout in ManySeeds())
            {
                var pads = layout.Pads;

                for (var i = 0; i < pads.Count; i++)
                {
                    Assert.IsTrue(JumpCourse.DistanceToFootprint(Npc.X, Npc.Y, pads[i].X, pads[i].Y) >= config.FirstDistance - 1e-3f, $"pad {i} too close to the NPC");

                    for (var j = i + 1; j < pads.Count; j++)
                        Assert.IsFalse(JumpCourse.InflatedOverlap(pads[i].X, pads[i].Y, pads[j].X, pads[j].Y), $"pads {i} and {j} overlap once inflated");
                }
            }

            Assert.IsTrue(JumpCourse.InflatedOverlap(0, 0, 2.9f, 0), "control: 0.9 m apart edge to edge is inside the 0.5 m inflation each");
            Assert.IsFalse(JumpCourse.InflatedOverlap(0, 0, 3.1f, 0));
        }

        [TestMethod]
        public void Refuses_when_the_ground_probe_fails()
        {
            var layout = JumpCourse.Generate(new JumpConfig(), new Random(3), Npc, (x, y) => null, InLandblock, out var failure);

            Assert.IsNull(layout);
            StringAssert.Contains(failure, "ground refused");

            // A refused strip east of the NPC is walked around, never built over.
            var holey = Make(3, ground: (x, y) => x > 100f ? (float?)null : 40f);
            Assert.IsNotNull(holey);
            Assert.IsTrue(holey.Pads.All(p => p.X + JumpCourse.PadHalf <= 100f));
        }

        [TestMethod]
        public void Every_pad_clears_sloped_terrain()
        {
            float? Slope(float x, float y) => 40f + 0.15f * (x - 96f) + 0.1f * (y - 96f);

            foreach (var layout in ManySeeds(100, ground: Slope))
            {
                foreach (var pad in layout.Pads)
                {
                    foreach (var (x, y) in JumpCourse.FootprintSamples(pad.X, pad.Y))
                        Assert.IsTrue(pad.Bottom >= Slope(x, y).Value + JumpCourse.GroundClearance - 1e-4f, $"pad at ({pad.X}, {pad.Y}) cuts the terrain");
                }
            }
        }

        [TestMethod]
        public void The_first_step_is_reachable_from_the_lowest_approach_ground_on_a_slope()
        {
            // A steep downslope (0.3 m per m along x): the ground beside the first pad, and the NPC's own ground, can sit
            // well under the ground below the pad's footprint. The first top must still be in reach from the lowest of them.
            float? Steep(float x, float y) => 40f + 0.3f * (x - 96f);

            var config = new JumpConfig();
            var limit = JumpCourse.JumpHeight(config.DesignJumpSkill) - JumpCourse.RiseMargin;
            var npc = new Vector3(96f, 96f, Steep(96f, 96f).Value);
            var placed = 0;

            for (var seed = 1; seed <= 200; seed++)
            {
                var layout = Make(seed, ground: Steep, npc: npc);

                if (layout == null)
                    continue;

                placed++;
                var first = layout.Pads[0];
                var lowest = JumpCourse.LowestApproachGround(npc, first.X, first.Y, Steep, InLandblock);

                Assert.IsTrue(first.Top - lowest <= limit + 1e-4f,
                    $"seed {seed}: the first pad at x {first.X:0.0} is {first.Top - lowest:0.00} m above the lowest approach ground, over the {limit:0.00} m limit");
            }

            Assert.IsTrue(placed >= 100, $"most seeds still place a course on the slope ({placed}/200)");

            // The approach ground includes the NPC's own z and the ring, never only the footprint.
            Assert.AreEqual(30f, JumpCourse.LowestApproachGround(new Vector3(0, 0, 30f), 50, 50, Flat, InLandblock), "the NPC's ground");
            Assert.AreEqual(40f - 0.3f * JumpCourse.ApproachRingOffset, JumpCourse.LowestApproachGround(new Vector3(0, 0, 100f), 96f, 96f, Steep, InLandblock), 1e-3f, "the downhill ring point");
        }

        [TestMethod]
        public void Stays_within_the_disc_the_landblock_and_the_perimeter()
        {
            var config = new JumpConfig();

            foreach (var layout in ManySeeds())
            {
                foreach (var pad in layout.Pads)
                {
                    foreach (var (x, y) in JumpCourse.FootprintSamples(pad.X, pad.Y))
                    {
                        Assert.IsTrue(Vector2.Distance(new Vector2(x, y), new Vector2(Npc.X, Npc.Y)) <= config.Radius + 1e-3f, "inside the disc");
                        Assert.IsTrue(x >= 0.5f && x <= 191.5f && y >= 0.5f && y <= 191.5f, "on the landblock");
                    }
                }
            }

            // A perimeter whose edge (less the 5 m inset) cuts through the disc: every sample stays inside it.
            var gx = OutdoorSiteGeometry.ToGlobalX(Lb, 96f);
            var gy = OutdoorSiteGeometry.ToGlobalY(Lb, 96f);
            bool Perimeter(float x, float y) => JumpEncounterType.Allowed(Lb, x, y, gx - 10, gy, 20);

            var placed = 0;

            for (var seed = 1; seed <= 60; seed++)
            {
                var layout = Make(seed, allowed: Perimeter);

                if (layout == null)
                    continue;

                placed++;

                foreach (var pad in layout.Pads)
                    foreach (var (x, y) in JumpCourse.FootprintSamples(pad.X, pad.Y))
                        Assert.IsTrue(Perimeter(x, y), $"seed {seed}: ({x}, {y}) is outside the perimeter less 5 m");
            }

            Assert.IsTrue(placed > 0, "some courses still fit inside the narrowed perimeter");

            // An NPC at the landblock edge: nothing is ever placed off it.
            var edge = new Vector3(8f, 96f, 40f);

            for (var seed = 1; seed <= 30; seed++)
            {
                var layout = Make(seed, npc: edge);

                if (layout == null)
                    continue;

                foreach (var pad in layout.Pads)
                    Assert.IsTrue(pad.X - JumpCourse.PadHalf >= 0.5f, $"seed {seed}: pad at x {pad.X} leaves the landblock");
            }
        }

        [TestMethod]
        public void The_allowed_bounds_check_is_strict()
        {
            Assert.IsTrue(JumpEncounterType.Allowed(Lb, 96, 96, null, 0, 0));
            Assert.IsFalse(JumpEncounterType.Allowed(Lb, 0.2f, 96, null, 0, 0));
            Assert.IsFalse(JumpEncounterType.Allowed(Lb, 96, 191.9f, null, 0, 0));

            var gx = OutdoorSiteGeometry.ToGlobalX(Lb, 96f);
            var gy = OutdoorSiteGeometry.ToGlobalY(Lb, 96f);
            Assert.IsTrue(JumpEncounterType.Allowed(Lb, 96 + 14.9f, 96, gx, gy, 20), "14.9 m from the centre of a 20 m perimeter");
            Assert.IsFalse(JumpEncounterType.Allowed(Lb, 96 + 15.1f, 96, gx, gy, 20), "past the 5 m inset");
        }
    }
}
