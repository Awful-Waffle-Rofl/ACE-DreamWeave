using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

using ACE.Server.Physics.Animation;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>One platform of a jump course, in the NPC landblock's local metres. Top = Bottom + the slab thickness.</summary>
    public readonly record struct JumpPad(float X, float Y, float Bottom, float Top, float Rise, float Gap);

    /// <summary>A generated course: the platforms from the first (lowest) to the top, and where the goal sits.</summary>
    public sealed class JumpLayout
    {
        public JumpLayout(IReadOnlyList<JumpPad> pads, Vector3 goal)
        {
            Pads = pads;
            Goal = goal;
        }

        public IReadOnlyList<JumpPad> Pads { get; }

        public JumpPad TopPad => Pads[Pads.Count - 1];

        /// <summary>The goal's position: the top pad's centre, 0.05 m above its top face.</summary>
        public Vector3 Goal { get; }
    }

    /// <summary>
    /// The pure half of the Jump encounter (Docs/Threads/ENCOUNTERS.md "Jump"): the course generator and the reach maths
    /// that proves every step can be made. No world, no PropertyManager, no Player: the caller hands in the ground and
    /// bounds as functions, so the generator is the same in production (ThreadDungeonSpawner.ProbeOutdoorGround on the
    /// point's landblock) and in a unit test.
    ///
    /// Heights are FIXED, not scaled to the player (owner ruling 2026-11-09): every step is sized for the config's
    /// designJumpSkill (300) with the server's own jump formula, MovementSystem.GetJumpHeight.
    /// </summary>
    public static class JumpCourse
    {
        /// <summary>Half the platform's side: Setup 0x02000DB3 is a 2 x 2 m slab at scale 1 (x and y -1..1).</summary>
        public const float PadHalf = 1.0f;

        /// <summary>The slab's thickness: Setup 0x02000DB3 spans z 0..0.4 with its origin at the bottom face.</summary>
        public const float PadThickness = 0.4f;

        /// <summary>How far each footprint is inflated before the no-overlap check, so no pad is ever under another one's edge.</summary>
        public const float OverlapInflation = 0.5f;

        /// <summary>The least clearance between a pad's bottom and the terrain under it (the #2013 floor lift).</summary>
        public const float GroundClearance = 0.05f;

        /// <summary>The goal sits this far above the top pad's top face.</summary>
        public const float GoalLift = 0.05f;

        /// <summary>Each rise must sit at least this far under the design jump height.</summary>
        public const float RiseMargin = 0.8f;

        /// <summary>The flight must carry this far past the gap, so the feet land on the pad rather than its edge.</summary>
        public const float LandingMargin = 0.5f;

        /// <summary>The server's gravity, m/s^2 (PhysicsGlobals.Gravity is -9.8).</summary>
        public const float Gravity = 9.8f;

        public const int MaxCourseAttempts = 60;
        public const int MaxPadAttempts = 40;

        // ---- reach maths ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The peak jump height for <paramref name="jumpSkill"/> at full power and no burden penalty: the server's own
        /// MovementSystem.GetJumpHeight (burdenMod * (S/(S+1300)*22.2 + 0.05) * power, floor 0.35 m). About 4.21 m at 300.
        /// </summary>
        public static float JumpHeight(uint jumpSkill) => MovementSystem.GetJumpHeight(0f, jumpSkill, 1.0f, 1.0f);

        /// <summary>
        /// The horizontal distance a jump of <paramref name="jumpSkill"/> carries at <paramref name="runSpeed"/> before the
        /// feet come back down to <paramref name="rise"/> above the take-off: vz = sqrt(2 g h) (WeenieObject.InqJumpVelocity
        /// uses sqrt(h * 19.6)), t = (vz + sqrt(vz^2 - 2 g rise)) / g. NaN when the rise is above the peak. Pure.
        /// </summary>
        public static float HorizontalReach(float rise, uint jumpSkill, float runSpeed)
        {
            var h = JumpHeight(jumpSkill);
            var vz = Math.Sqrt(h * 2.0 * Gravity);
            var disc = vz * vz - 2.0 * Gravity * rise;

            if (disc < 0)
                return float.NaN;

            var t = (vz + Math.Sqrt(disc)) / Gravity;
            return (float)(runSpeed * t);
        }

        /// <summary>
        /// Can a player of <paramref name="jumpSkill"/> running at <paramref name="runSpeed"/> make a step of
        /// <paramref name="rise"/> up and <paramref name="gap"/> across (edge to edge)? The rise must sit
        /// <see cref="RiseMargin"/> under the peak, and the flight must cover the gap plus <see cref="LandingMargin"/>. Pure.
        /// </summary>
        public static bool IsStepReachable(float rise, float gap, uint jumpSkill, float runSpeed, out string why)
        {
            why = null;
            var h = JumpHeight(jumpSkill);

            if (!(rise <= h - RiseMargin))
            {
                why = string.Format(CultureInfo.InvariantCulture, "rise {0:0.00} m is above the jump height {1:0.00} m less {2:0.0} m", rise, h, RiseMargin);
                return false;
            }

            var reach = HorizontalReach(rise, jumpSkill, runSpeed);

            if (!(reach >= gap + LandingMargin))
            {
                why = string.Format(CultureInfo.InvariantCulture, "gap {0:0.00} m plus {1:0.0} m is beyond the reach {2:0.00} m at rise {3:0.00} m", gap, LandingMargin, reach, rise);
                return false;
            }

            return true;
        }

        // ---- geometry helpers ----------------------------------------------------------------------------------------

        /// <summary>The edge-to-edge (closest-point) distance between two axis-aligned squares of half side <see cref="PadHalf"/>. Pure.</summary>
        public static float EdgeGap(float dx, float dy)
        {
            var gx = Math.Max(0f, Math.Abs(dx) - 2 * PadHalf);
            var gy = Math.Max(0f, Math.Abs(dy) - 2 * PadHalf);
            return MathF.Sqrt(gx * gx + gy * gy);
        }

        /// <summary>The distance from a point to a pad's footprint (0 inside it). Pure.</summary>
        public static float DistanceToFootprint(float px, float py, float cx, float cy)
        {
            var gx = Math.Max(0f, Math.Abs(px - cx) - PadHalf);
            var gy = Math.Max(0f, Math.Abs(py - cy) - PadHalf);
            return MathF.Sqrt(gx * gx + gy * gy);
        }

        /// <summary>Do the two footprints overlap once each is inflated by <see cref="OverlapInflation"/>? Pure.</summary>
        public static bool InflatedOverlap(float ax, float ay, float bx, float by)
        {
            var span = 2 * (PadHalf + OverlapInflation);
            return Math.Abs(ax - bx) < span && Math.Abs(ay - by) < span;
        }

        /// <summary>The centre and the four corners of a pad's footprint: the points every ground and bounds check samples.</summary>
        public static IEnumerable<(float X, float Y)> FootprintSamples(float cx, float cy)
        {
            yield return (cx, cy);
            yield return (cx - PadHalf, cy - PadHalf);
            yield return (cx + PadHalf, cy - PadHalf);
            yield return (cx - PadHalf, cy + PadHalf);
            yield return (cx + PadHalf, cy + PadHalf);
        }

        /// <summary>
        /// The centre-to-centre distance along <paramref name="dir"/> (a unit vector) at which two pads are exactly
        /// <paramref name="gap"/> apart edge to edge. EdgeGap is monotone in the distance, so a bisection is exact enough.
        /// </summary>
        public static float CentreDistanceForGap(Vector2 dir, float gap)
        {
            float lo = 0f, hi = gap + 4 * PadHalf + 1f;

            for (var i = 0; i < 40; i++)
            {
                var mid = (lo + hi) * 0.5f;

                if (EdgeGap(dir.X * mid, dir.Y * mid) < gap)
                    lo = mid;
                else
                    hi = mid;
            }

            return hi;
        }

        /// <summary>How far beyond the first pad's centre the approach ring is sampled: its half side plus 1 m.</summary>
        public const float ApproachRingOffset = PadHalf + 1.0f;

        /// <summary>
        /// The lowest ground a player may take off from to reach the first pad: the NPC's own ground z, and the terrain at 8
        /// points on a ring of <see cref="ApproachRingOffset"/> around the pad's centre (points outside the bounds, or
        /// whose ground is refused, are skipped). Pure.
        /// </summary>
        public static float LowestApproachGround(Vector3 npc, float cx, float cy, Func<float, float, float?> ground, Func<float, float, bool> allowed)
        {
            var lowest = npc.Z;

            foreach (var (x, y) in ApproachSamples(cx, cy))
            {
                if (allowed != null && !allowed(x, y))
                    continue;

                var g = ground?.Invoke(x, y);

                if (g != null && !float.IsNaN(g.Value))
                    lowest = Math.Min(lowest, g.Value);
            }

            return lowest;
        }

        /// <summary>The 8 approach-ring points around a pad centre.</summary>
        public static IEnumerable<(float X, float Y)> ApproachSamples(float cx, float cy)
        {
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                yield return (cx + (float)Math.Cos(a) * ApproachRingOffset, cy + (float)Math.Sin(a) * ApproachRingOffset);
            }
        }

        // ---- generator -----------------------------------------------------------------------------------------------

        /// <summary>
        /// Generates a course of <see cref="JumpConfig.Platforms"/> pads around <paramref name="npcLocal"/> (landblock-local x,
        /// y and the NPC's ground z) by a jittered random walk inside a disc of <see cref="JumpConfig.Radius"/>. The first pad
        /// rises from the lowest terrain under its footprint; each later one rises from the previous pad's top. Every pad:
        /// its footprint wholly inside the disc and accepted by <paramref name="allowed"/> (landblock and perimeter bounds), at
        /// least <see cref="JumpConfig.FirstDistance"/> from the NPC, no inflated overlap with another pad, its bottom at
        /// least <see cref="GroundClearance"/> above the terrain at every sample, and every step reachable at the design jump
        /// skill. <paramref name="ground"/> returns the terrain height at a local x, y, or null where the ground is refused
        /// (not walkable, a building, off the landblock): a refused sample refuses that candidate. Bounded retries; null with
        /// <paramref name="failure"/> when no course fits. Deterministic for a given <paramref name="rng"/> seed. Pure.
        /// </summary>
        public static JumpLayout Generate(JumpConfig config, Random rng, Vector3 npcLocal, Func<float, float, float?> ground, Func<float, float, bool> allowed,
            out string failure)
        {
            failure = null;

            if (config == null || rng == null || ground == null || allowed == null)
            {
                failure = "no config, random stream, ground or bounds";
                return null;
            }

            var lastWhy = "no attempt";

            for (var attempt = 0; attempt < MaxCourseAttempts; attempt++)
            {
                var pads = TryWalk(config, rng, npcLocal, ground, allowed, out lastWhy);

                if (pads != null)
                {
                    var top = pads[pads.Count - 1];
                    return new JumpLayout(pads, new Vector3(top.X, top.Y, top.Top + GoalLift));
                }
            }

            failure = $"no course fits after {MaxCourseAttempts} attempts (last: {lastWhy})";
            return null;
        }

        private static List<JumpPad> TryWalk(JumpConfig config, Random rng, Vector3 npc, Func<float, float, float?> ground, Func<float, float, bool> allowed, out string why)
        {
            why = null;
            var pads = new List<JumpPad>(config.Platforms);
            var heading = rng.NextDouble() * Math.PI * 2;

            for (var i = 0; i < config.Platforms; i++)
            {
                var placed = false;

                for (var t = 0; t < MaxPadAttempts && !placed; t++)
                {
                    float cx, cy, gap;
                    double dirAngle;

                    if (i == 0)
                    {
                        // Anywhere around the NPC, its footprint FirstDistance to FirstDistance + gap.max away.
                        dirAngle = rng.NextDouble() * Math.PI * 2;
                        var edge = config.FirstDistance + (float)rng.NextDouble() * config.GapMax;
                        var d = DistanceAlong(new Vector2((float)Math.Cos(dirAngle), (float)Math.Sin(dirAngle)), edge);
                        cx = npc.X + (float)Math.Cos(dirAngle) * d;
                        cy = npc.Y + (float)Math.Sin(dirAngle) * d;
                        gap = 0f;
                    }
                    else
                    {
                        // Jittered walk: up to +-100 degrees off the last heading, so the course curls inside the disc.
                        dirAngle = heading + (rng.NextDouble() * 2 - 1) * (100.0 * Math.PI / 180.0);
                        gap = config.GapMin + (float)rng.NextDouble() * (config.GapMax - config.GapMin);
                        var dir = new Vector2((float)Math.Cos(dirAngle), (float)Math.Sin(dirAngle));
                        var d = CentreDistanceForGap(dir, gap);
                        cx = pads[i - 1].X + dir.X * d;
                        cy = pads[i - 1].Y + dir.Y * d;
                    }

                    var rise = config.RiseMin + (float)rng.NextDouble() * (config.RiseMax - config.RiseMin);

                    if (!TryPad(config, npc, pads, cx, cy, rise, gap, ground, allowed, out var pad, out why))
                        continue;

                    pads.Add(pad);
                    heading = dirAngle;
                    placed = true;
                }

                if (!placed)
                    return null;
            }

            return pads;
        }

        /// <summary>Centre distance from a POINT (the NPC) to a pad whose footprint is <paramref name="edge"/> away along <paramref name="dir"/>.</summary>
        private static float DistanceAlong(Vector2 dir, float edge)
        {
            float lo = 0f, hi = edge + 2 * PadHalf + 1f;

            for (var i = 0; i < 40; i++)
            {
                var mid = (lo + hi) * 0.5f;

                if (DistanceToFootprint(0f, 0f, dir.X * mid, dir.Y * mid) < edge)
                    lo = mid;
                else
                    hi = mid;
            }

            return hi;
        }

        private static bool TryPad(JumpConfig config, Vector3 npc, List<JumpPad> pads, float cx, float cy, float rise, float gap,
            Func<float, float, float?> ground, Func<float, float, bool> allowed, out JumpPad pad, out string why)
        {
            pad = default;

            // Bounds: every sample inside the disc and allowed.
            foreach (var (x, y) in FootprintSamples(cx, cy))
            {
                if (Vector2.Distance(new Vector2(x, y), new Vector2(npc.X, npc.Y)) > config.Radius)
                {
                    why = "outside the disc";
                    return false;
                }

                if (!allowed(x, y))
                {
                    why = "outside the landblock or perimeter bounds";
                    return false;
                }
            }

            if (DistanceToFootprint(npc.X, npc.Y, cx, cy) < config.FirstDistance)
            {
                why = "too close to the NPC";
                return false;
            }

            foreach (var other in pads)
            {
                if (InflatedOverlap(cx, cy, other.X, other.Y))
                {
                    why = "overlaps another pad";
                    return false;
                }
            }

            // Ground under every sample: refused anywhere refuses the candidate.
            float minGround = float.MaxValue, maxGround = float.MinValue;

            foreach (var (x, y) in FootprintSamples(cx, cy))
            {
                var g = ground(x, y);

                if (g == null || float.IsNaN(g.Value))
                {
                    why = "ground refused";
                    return false;
                }

                minGround = Math.Min(minGround, g.Value);
                maxGround = Math.Max(maxGround, g.Value);
            }

            var top = pads.Count == 0 ? minGround + rise : pads[pads.Count - 1].Top + rise;
            var bottom = top - PadThickness;

            if (bottom < maxGround + GroundClearance)
            {
                why = "the pad would cut the terrain";
                return false;
            }

            if (!IsStepReachable(rise, gap, config.DesignJumpSkill, config.DesignRunSpeed, out why))
                return false;

            // The first pad is jumped onto from the GROUND around it, which can sit lower than the ground under its own
            // footprint on a slope. Its top must be in reach from the lowest approach: the NPC's ground and a ring
            // ApproachRingOffset beyond its edge.
            if (pads.Count == 0)
            {
                var lowest = LowestApproachGround(npc, cx, cy, ground, allowed);

                if (!(top - lowest <= JumpHeight(config.DesignJumpSkill) - RiseMargin))
                {
                    why = string.Format(CultureInfo.InvariantCulture, "the first pad is {0:0.00} m above the lowest approach ground", top - lowest);
                    return false;
                }
            }

            pad = new JumpPad(cx, cy, bottom, top, rise, gap);
            why = null;
            return true;
        }
    }

    /// <summary>
    /// A jump row's parsed config (encounters.json "config"). Every key is optional; the defaults below are the shipped
    /// values, so an empty {} (the admin default row) is a working course.
    /// </summary>
    public sealed class JumpConfig
    {
        public const uint DefaultNpcWcid = 1007960;
        public const uint DefaultPlatformWcid = 1007961;
        public const uint DefaultGoalWcid = 1007962;

        public const int MinPlatforms = 7;
        public const int MaxPlatforms = 9;

        public uint NpcWcid { get; init; } = DefaultNpcWcid;
        public uint PlatformWcid { get; init; } = DefaultPlatformWcid;
        public uint GoalWcid { get; init; } = DefaultGoalWcid;

        /// <summary>Platforms per course, 7 to 9.</summary>
        public int Platforms { get; init; } = 8;

        /// <summary>Each pad's rise above the last (the first: above the lowest terrain under it), metres.</summary>
        public float RiseMin { get; init; } = 2.0f;
        public float RiseMax { get; init; } = 3.0f;

        /// <summary>Edge-to-edge gap between consecutive pads, metres.</summary>
        public float GapMin { get; init; } = 1.5f;
        public float GapMax { get; init; } = 3.0f;

        /// <summary>The disc around the NPC every footprint must stay inside, metres.</summary>
        public float Radius { get; init; } = 12f;

        /// <summary>The least distance from the NPC to any pad's footprint, metres.</summary>
        public float FirstDistance { get; init; } = 2.5f;

        /// <summary>The Jump skill every step is sized for (heights are fixed, not scaled to the player).</summary>
        public uint DesignJumpSkill { get; init; } = 300;

        /// <summary>The horizontal speed the reach maths assumes: MotionInterp.RunAnimSpeed, 4 m/s, a run rate of 1.</summary>
        public float DesignRunSpeed { get; init; } = 4.0f;
    }
}
