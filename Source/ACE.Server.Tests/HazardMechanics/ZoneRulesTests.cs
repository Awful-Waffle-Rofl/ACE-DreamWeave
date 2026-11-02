using System.Collections.Generic;
using System.Numerics;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>The zones mechanic's pure rules (ZoneRules, ZoneClock) and its argument defaults. No landblock, no PropertyManager.</summary>
    [TestClass]
    public class ZoneRulesTests
    {
        private const double Eps = 1e-9;

        private static ZoneClock Clock(double tick = 1, double pct = 0.05, double pctEnd = 0.35, double ramp = 5, double reset = 1)
            => new ZoneClock(tick, pct, pctEnd, ramp, reset);

        /// <summary>Runs the clock in 0.1 s steps from <paramref name="from"/> for <paramref name="seconds"/>; returns (time, pct) of every hit.</summary>
        private static List<(double T, double Pct)> RunInside(ZoneClock clock, double from, double seconds, bool inside = true)
        {
            var hits = new List<(double, double)>();
            var steps = (int)System.Math.Round(seconds / 0.1);

            for (var i = 0; i <= steps; i++)
            {
                var t = from + i * 0.1;
                var hit = clock.Update(t, inside);

                if (hit.HasValue)
                    hits.Add((t, hit.Value));
            }

            return hits;
        }

        [TestMethod]
        public void PctAt_RampsLinearly_AndCapsAtRamp()
        {
            Assert.AreEqual(0.05, ZoneRules.PctAt(0, 0.05, 0.35, 5), Eps);
            Assert.AreEqual(0.20, ZoneRules.PctAt(2.5, 0.05, 0.35, 5), Eps);
            Assert.AreEqual(0.35, ZoneRules.PctAt(5, 0.05, 0.35, 5), Eps);
            Assert.AreEqual(0.35, ZoneRules.PctAt(50, 0.05, 0.35, 5), Eps);
            Assert.AreEqual(0.05, ZoneRules.PctAt(-3, 0.05, 0.35, 5), Eps, "negative dwell is the start value");
            Assert.AreEqual(0.35, ZoneRules.PctAt(0, 0.05, 0.35, 0), Eps, "ramp 0: the end value at once");
        }

        [TestMethod]
        public void Dwell_HitsOnEntry_ThenEveryTick_RampingToTheCap()
        {
            var hits = RunInside(Clock(), 0, 8);

            Assert.AreEqual(9, hits.Count, "t = 0..8 s, one hit per second");

            for (var i = 0; i < hits.Count; i++)
            {
                Assert.AreEqual(i, hits[i].T, 1e-6, "cadence is exactly the tick");
                Assert.AreEqual(ZoneRules.PctAt(i, 0.05, 0.35, 5), hits[i].Pct, 1e-6, "hit " + i);
            }

            Assert.AreEqual(0.05, hits[0].Pct, Eps);
            Assert.AreEqual(0.11, hits[1].Pct, 1e-6);
            Assert.AreEqual(0.35, hits[5].Pct, 1e-6, "capped at the ramp");
            Assert.AreEqual(0.35, hits[8].Pct, 1e-6, "and stays there");
        }

        [TestMethod]
        public void TickCadence_FollowsTheTickArgument()
        {
            var hits = RunInside(Clock(tick: 2.5), 0, 10);

            Assert.AreEqual(5, hits.Count, "0, 2.5, 5, 7.5, 10");
            Assert.AreEqual(2.5, hits[1].T, 1e-6);
            Assert.AreEqual(10.0, hits[4].T, 1e-6);
        }

        [TestMethod]
        public void StepOutBelowReset_KeepsTheClock_SoDwellAndCadenceResume()
        {
            var c = Clock();
            var first = RunInside(c, 0, 3.0); // hits at 0,1,2,3
            Assert.AreEqual(4, first.Count);

            // out for 0.5 s (below reset 1): the clock is kept
            Assert.IsNull(c.Update(3.2, false));
            Assert.IsNull(c.Update(3.5, false));
            Assert.IsFalse(c.Expired);

            // back in: dwell continues from 3.0, so the next hit is 1 s of INSIDE time later, at dwell 4
            Assert.IsNull(c.Update(3.6, true));
            var hits = RunInside(c, 3.7, 1.0);

            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(ZoneRules.PctAt(4, 0.05, 0.35, 5), hits[0].Pct, 1e-6, "dwell 4 s: the ramp did not restart");
        }

        [TestMethod]
        public void OutsideForReset_StartsOver_AndTheNextEntryHitsAtPctAgain()
        {
            var c = Clock();
            RunInside(c, 0, 4.0);

            Assert.IsNull(c.Update(4.5, false));
            Assert.IsFalse(c.Expired, "0.5 s out is below reset");
            Assert.IsNull(c.Update(5.5, false));
            Assert.IsTrue(c.Expired, "1.0 s out reaches reset");
            Assert.AreEqual(0.0, c.Dwell, Eps);

            var again = c.Update(6.0, true);
            Assert.IsTrue(again.HasValue, "a fresh entry hits at once");
            Assert.AreEqual(0.05, again.Value, Eps, "back at the start of the ramp");
        }

        [TestMethod]
        public void ResetZero_ExpiresTheMomentThePlayerStepsOut()
        {
            var c = Clock(reset: 0);
            Assert.IsNotNull(c.Update(0, true));
            Assert.IsFalse(c.Expired, "inside is never expired, even with reset 0");

            Assert.IsNull(c.Update(0.1, false));
            Assert.IsTrue(c.Expired);
        }

        [TestMethod]
        public void AStalledDriver_DoesNotOweTheMissedHits()
        {
            var c = Clock();
            Assert.IsNotNull(c.Update(0, true));

            // 5 s with no update, then one: ONE hit, not five
            var hit = c.Update(5.0, true);
            Assert.IsTrue(hit.HasValue);
            Assert.AreEqual(0.35, hit.Value, Eps);
            Assert.IsNull(c.Update(5.5, true));
            Assert.IsNotNull(c.Update(6.0, true));
        }

        [TestMethod]
        public void SeveralPillarsInReach_CountOnce()
        {
            var pillars = new List<Vector3> { new Vector3(10, 10, 0), new Vector3(12, 10, 0), new Vector3(60, 60, 0) };
            var player = new Vector3(11, 10, 0);

            Assert.IsTrue(ZoneRules.InAnyZone(pillars, player, 3, 0.5), "in reach of two pillars is still ONE bool");

            // the mechanic feeds that one bool to one clock per player: two overlapping pillars give one hit per tick
            var hits = RunInside(Clock(), 0, 3.0);
            Assert.AreEqual(4, hits.Count);
        }

        [TestMethod]
        public void InAnyZone_UsesRadiusPlusPlayerRadius_At2D()
        {
            var pillars = new List<Vector3> { new Vector3(0, 0, 0) };

            Assert.IsTrue(ZoneRules.InAnyZone(pillars, new Vector3(3.5f, 0, 99), 3, 0.5), "exactly on the rim, z ignored");
            Assert.IsFalse(ZoneRules.InAnyZone(pillars, new Vector3(3.6f, 0, 0), 3, 0.5));
            Assert.IsFalse(ZoneRules.InAnyZone(new List<Vector3>(), new Vector3(0, 0, 0), 3, 0.5));
            Assert.IsFalse(ZoneRules.InAnyZone(null, new Vector3(0, 0, 0), 3, 0.5));
        }

        private static HazardRegion Rect() => HazardRegion.Rect("t", 0x0069, 0, false, 2, 12, 4, 24, HazardGroundMode.Plane, 0, null, 0, null, 1, 1);

        private static HazardRegion Circle() => HazardRegion.Circle("c", 0x0069, 0, false, 10, 10, 8, HazardGroundMode.Plane, 0, null, 0, null, 1, 1);

        [TestMethod]
        public void RegionCorners_AreTheRectsFour_AndNothingForACircle()
        {
            var corners = ZoneRules.RegionCorners(Rect());

            CollectionAssert.AreEquivalent(
                new[] { new Vector2(2, 4), new Vector2(12, 4), new Vector2(12, 24), new Vector2(2, 24) },
                new List<Vector2>(corners));

            Assert.AreEqual(0, ZoneRules.RegionCorners(Circle()).Count);
            Assert.AreEqual(0, ZoneRules.RegionCorners(null).Count);
        }

        [TestMethod]
        public void Settings_DefaultsAreTheDesigns_AndClamp()
        {
            var d = ZoneSettings.Read(HazardArgs.Empty);

            Assert.AreEqual(3.0, d.Radius);
            Assert.AreEqual(1.0, d.Tick);
            Assert.AreEqual(0.05, d.Pct);
            Assert.AreEqual(0.35, d.PctEnd);
            Assert.AreEqual(5.0, d.Ramp);
            Assert.AreEqual(1.0, d.Reset);

            var wild = ZoneSettings.Read(HazardArgs.Parse("radius=999 tick=0 pct=-1 pct_end=99 ramp=-5 reset=999"));

            Assert.AreEqual(ZoneSettings.MaxRadius, wild.Radius);
            Assert.AreEqual(ZoneSettings.MinTick, wild.Tick);
            Assert.AreEqual(0.0, wild.Pct);
            Assert.AreEqual(5.0, wild.PctEnd);
            Assert.AreEqual(0.0, wild.Ramp);
            Assert.AreEqual(ZoneSettings.MaxReset, wild.Reset);
        }

        [TestMethod]
        public void Validate_RefusesACircleAndATypo_AcceptsARect()
        {
            var m = new ZonesMechanic();

            Assert.IsNull(m.Validate(Rect(), HazardArgs.Empty, "corners"));
            StringAssert.Contains(m.Validate(Circle(), HazardArgs.Empty, "corners"), "rect");
            StringAssert.Contains(m.Validate(Rect(), HazardArgs.Parse("tick=fast"), "corners"), "tick");
        }

        [TestMethod]
        public void Registry_KnowsZones_ButItIsNotOneOfTheFourLibraryKinds()
        {
            Assert.IsTrue(HazardMechanicRegistry.TryCreate("zones", out var mechanic));
            Assert.AreEqual("zones", mechanic.Kind);
            CollectionAssert.AreEqual(new[] { "corners" }, new List<string>(mechanic.Patterns));
            CollectionAssert.Contains(new List<string>(HazardMechanicRegistry.Kinds), "zones");
            CollectionAssert.DoesNotContain(new List<string>(HazardMechanicRegistry.MechanicKinds), "zones");
            Assert.IsNull(HazardTunables.SettingsKeyFor("zones"));
        }

        // ---- review 2026-10-20: the live Tick and Start wiring (a HazardContext needs a live landblock, so source scans) ----

        private static string ZonesSrc() => ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/ZonesMechanic.cs");

        [TestMethod]
        public void Tick_HitsThroughHitPlayer_AndRunsTheClocksDown()
        {
            var tick = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(ZonesSrc(), "public void Tick(HazardContext ctx)");

            var inZone = tick.IndexOf("ZoneRules.InAnyZone(centres, pos, settings.Radius, playerRadius)", System.StringComparison.Ordinal);
            var update = tick.IndexOf("var pct = clock.Update(ctx.Now, isIn);", System.StringComparison.Ordinal);
            var hit = tick.IndexOf("ctx.HitPlayer(source, player, pct.Value);", System.StringComparison.Ordinal);
            var gone = tick.IndexOf("if (!inside.ContainsKey(kv.Key))", System.StringComparison.Ordinal);
            var outside = tick.IndexOf("kv.Value.Update(ctx.Now, false);", System.StringComparison.Ordinal);
            var expired = tick.IndexOf("if (kv.Value.Expired)", System.StringComparison.Ordinal);
            var remove = tick.IndexOf("clocks.Remove(id);", System.StringComparison.Ordinal);

            Assert.IsTrue(inZone >= 0 && update > inZone && hit > update, "dwell test, clock, then the hit through HitPlayer");
            StringAssert.Contains(tick, "if (pct.HasValue)");
            Assert.IsTrue(gone > hit && outside > gone && expired > outside && remove > expired, "a player gone from the region runs down and an expired clock is removed");
            StringAssert.Contains(tick, "foreach (var id in gone)", "removed after the enumeration, never during it");
            StringAssert.Contains(tick, "ctx.End(HazardOutcome.Aborted, \"every pillar was lost\");");
        }

        [TestMethod]
        public void Start_PlacesAtThePillarPoints_AndWarnsWhenACornerIsMissing()
        {
            var start = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(ZonesSrc(), "public void Start(HazardContext ctx)");

            StringAssert.Contains(start, "foreach (var point in PillarPoints(ctx.Region, ctx.EdgeInset, ctx.GroundZ))");
            StringAssert.Contains(start, "allowRaise: false");

            var abort = start.IndexOf("if (pillars.Count == 0)", System.StringComparison.Ordinal);
            var warn = start.IndexOf("if (pillars.Count < corners.Count)", System.StringComparison.Ordinal);
            Assert.IsTrue(abort >= 0 && warn > abort, "none placed aborts; some missing warns");
            StringAssert.Contains(start.Substring(warn), "ctx.LogWarn(");
        }

        [TestMethod]
        public void PillarPoints_AreTheCornersClampedInside_AtGroundPlusLift()
        {
            var points = ZonesMechanic.PillarPoints(Rect(), 2f, (x, y) => -47.75f);

            Assert.AreEqual(4, points.Count);

            foreach (var p in points)
                Assert.AreEqual(-47.70f, p.Z, 1e-4f);

            var corners = ZoneRules.RegionCorners(Rect());

            for (var i = 0; i < corners.Count; i++)
            {
                var clamped = Rect().ClampInside(corners[i], 2f);
                Assert.AreEqual(clamped.X, points[i].X, 1e-4f);
                Assert.AreEqual(clamped.Y, points[i].Y, 1e-4f);
            }
        }
    }
}
