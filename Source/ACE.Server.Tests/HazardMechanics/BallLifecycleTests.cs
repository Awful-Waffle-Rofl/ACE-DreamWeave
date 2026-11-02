using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

using ACE.Server.HazardMechanics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.HazardMechanics
{
    /// <summary>
    /// The balls run's behaviour without a world: the wave state machine (BallWaveClock), the hit sweep (BallSweep),
    /// the wave timeline, escape_speed, the route cap, exclusion safe spots, the spawn nudge and marker honesty.
    /// Every guard is paired with a control that shows the test can fail.
    /// </summary>
    [TestClass]
    public class BallLifecycleTests
    {
        private static HazardRegion Pinnacle() => HazardRegionPresets.Pinnacle(0);

        private static BallParams P(string args, string pattern)
            => BallRules.Read(HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls)), pattern);

        private static string Validate(HazardRegion region, string args, string pattern)
            => BallRules.Validate(region, HazardArgs.Merge(HazardArgs.Parse(args), HazardArgs.Parse(HazardDefaults.Balls)), pattern);

        // ---- F1: the hit sweep -------------------------------------------------------------------------------------

        private static Vector3 Roll12(double t) => new Vector3((float)(12.0 * t), 0f, 0f);

        [TestMethod]
        public void Sweep_CatchesAPlayerBetweenTwoSlowTicks_ThatTickSamplingMisses()
        {
            // 12 m/s on a 400 ms tick: samples every 4.8 m, a ball hits within 1.8 + 0.5 = 2.3 m, so the 0.2 m between reaches is a hole
            var player = new Vector3(2.4f, 0f, 0f);

            var tickSamples = new[] { 0.0, 0.4, 0.8 }.Select(Roll12).ToList();
            Assert.IsFalse(tickSamples.Any(c => HazardHitRules.InReach(c, player, 1.8, 0.5)), "control: sampling only at ticks misses a player centred between two samples");

            var swept = new List<Vector3>();
            var from = 0.0;
            var first = true;

            foreach (var now in new[] { 0.4, 0.8 })
            {
                swept.AddRange(BallSweep.SweepPoints(Roll12, from, now, 2.3f * 0.5f, first));
                first = false;
                from = now;
            }

            Assert.IsTrue(swept.Any(c => HazardHitRules.InReach(c, player, 1.8, 0.5)), "the swept points reach the player");

            for (var i = 1; i < swept.Count; i++)
                Assert.IsTrue(Vector3.Distance(swept[i - 1], swept[i]) <= 1.15f + 1e-3f, $"step {i} is {Vector3.Distance(swept[i - 1], swept[i])} m");
        }

        [TestMethod]
        public void Sweep_ToThePathEnd_CoversTheLastSegmentTheTickSamplingNeverSaw()
        {
            // a 10 m leg at 12 m/s ends at t = 0.833; ticks at 0.4 and 0.8 sampled x = 4.8 and 9.6 and the leg then ended
            var legs = HazardContext.PlanLeg(Pinnacle(), null, new Vector3(0f, -20f, -45.75f), new Vector3(10f, -20f, -45.75f), 0.0, 10.0 / 12.0,
                new HazardMoveOptions { Follow = HazardFollow.Ground, GroundOffset = 2f });
            var path = new HazardPath(legs);
            var player = new Vector3(12.0f, -20f, -45.75f);

            Assert.IsFalse(HazardHitRules.InReach(new Vector3(path.PositionAt(0.8).X, -20f, 0f), new Vector3(player.X, -20f, 0f), 1.8, 0.5), "control: the last tick sample is 2.4 m short of the player");

            var tail = BallSweep.SweepPoints(path, 0.8, path.EndTime + 5.0, 2.3f * 0.5f);
            Assert.IsTrue(tail.Any(c => HazardHitRules.InReach(c, player, 1.8, 0.5)), "the final sweep to EndTime reaches the player");
            Assert.AreEqual(10f, tail.Last().X, 1e-3f, "and stops at the path end, not past it");
        }

        [TestMethod]
        public void Sweep_Degenerate_NoTimeElapsed_AddsNoRedundantPoint()
        {
            Assert.AreEqual(0, BallSweep.SweepPoints(Roll12, 1.0, 1.0, 1f).Count);
            Assert.AreEqual(1, BallSweep.SweepPoints(Roll12, 1.0, 1.0, 1f, includeStart: true).Count);
            Assert.AreEqual(0, BallSweep.SweepPoints(Roll12, 2.0, 1.0, 1f).Count, "time never runs backwards");
        }

        // ---- F3: the wave state machine ----------------------------------------------------------------------------

        private static void RunWave(BallWaveClock clock, int firstId, int count)
        {
            clock.StartWave(count);

            for (var i = 0; i < count; i++)
                clock.OnSpawned(firstId + i);

            for (var i = 0; i < count; i++)
                clock.OnLaunched(firstId + i);
        }

        [TestMethod]
        public void OneHitPerBallPerPlayer_AcrossTwoWaves()
        {
            // a stationary player in reach of every ball every tick; HitPlayers' ledger is keyed (ball id, player)
            var clock = new BallWaveClock(2);
            var ledger = new Dictionary<(int, uint), double>();
            var hits = 0;
            var ticks = 0;

            void Tick(double now, double window)
            {
                ticks++;

                foreach (var id in clock.Hittable())
                {
                    double? last = ledger.TryGetValue((id, 7u), out var t) ? t : (double?)null;

                    if (!HazardHitRules.WindowOpen(last, now, window))
                        continue;

                    ledger[(id, 7u)] = now;
                    hits++;
                }
            }

            RunWave(clock, 1, 3);
            for (var i = 0; i < 5; i++) Tick(i * 0.1, RollingBallMechanic.HitWindowSeconds);
            for (var id = 1; id <= 3; id++) clock.OnLegEnd(id);
            Assert.AreEqual(BallWaveStep.StartNextWave, clock.NextStep());

            RunWave(clock, 10, 3);
            for (var i = 5; i < 10; i++) Tick(i * 0.1, RollingBallMechanic.HitWindowSeconds);

            Assert.AreEqual(6, hits, "3 balls x 2 waves, each hitting the player once however many ticks it overlapped");

            // control: with a 0-second window (the wrong semantics) the same ticks hit every time
            var again = hits;
            hits = 0;
            ledger.Clear();
            for (var i = 0; i < 5; i++) Tick(i * 0.1, 0.0);
            Assert.IsTrue(hits > again, "the ledger test can fail");
        }

        [TestMethod]
        public void NoHitBeforeLaunch_NorAfterTheLegEnded_NorForADeadBall()
        {
            var clock = new BallWaveClock(1);
            clock.StartWave(3);
            clock.OnSpawned(1);
            clock.OnSpawned(2);
            clock.OnSpawned(3);

            Assert.AreEqual(0, clock.Hittable().Count, "a ball waiting out its lead at the start line cannot hit");

            clock.OnLaunched(1);
            clock.OnLaunched(2);
            clock.OnLaunched(3);
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, clock.Hittable().ToArray());

            clock.OnLegEnd(1);
            clock.OnDied(2);

            CollectionAssert.AreEqual(new[] { 3 }, clock.Hittable().ToArray(), "ended and dead balls are not hittable");
            Assert.AreEqual(1, clock.Lost, "only the dead one is lost");
            Assert.AreEqual(BallState.Ended, clock.StateOf(1));

            clock.OnLaunched(1);
            Assert.IsFalse(clock.IsHittable(1), "an ended ball is not relaunched");

            clock.OnDied(1);
            Assert.AreEqual(1, clock.Lost, "a ball that finished is not lost when its object is then destroyed");
        }

        [TestMethod]
        public void ANullSpawn_LetsTheWaveCarryOn_AndCountsAsLost()
        {
            var clock = new BallWaveClock(1);
            clock.StartWave(3);
            clock.OnSpawned(1);
            clock.OnSpawnFailed();
            clock.OnSpawned(3);
            clock.OnLaunched(1);
            clock.OnLaunched(3);

            Assert.AreEqual(BallWaveStep.Wait, clock.NextStep());

            clock.OnLegEnd(1);
            clock.OnLegEnd(3);

            Assert.IsTrue(clock.WaveOver, "two balls ended and one never spawned: nothing left to wait for");
            Assert.AreEqual(1, clock.Lost);
            Assert.AreEqual(2, clock.Spawned);
        }

        [TestMethod]
        public void CompletesAfterTheLastWave_EvenWithBallsLost_AndNotBefore()
        {
            var clock = new BallWaveClock(2);

            RunWave(clock, 1, 2);
            Assert.AreEqual(BallWaveStep.Wait, clock.NextStep());
            clock.OnLegEnd(1);
            Assert.AreEqual(BallWaveStep.Wait, clock.NextStep(), "one ball still rolling");
            clock.OnLegEnd(2);
            Assert.AreEqual(BallWaveStep.StartNextWave, clock.NextStep());
            Assert.IsFalse(clock.IsLastWave);

            RunWave(clock, 10, 2);
            clock.OnDied(10);
            clock.OnLegEnd(11);

            Assert.IsTrue(clock.IsLastWave);
            Assert.AreEqual(BallWaveStep.Complete, clock.NextStep());
            Assert.AreEqual(1, clock.Lost);
        }

        [TestMethod]
        public void AWaveWaitsForBallsThatHaveNotSpawnedYet_ARandomDelay()
        {
            var clock = new BallWaveClock(1);
            clock.StartWave(3);
            clock.OnSpawned(1);
            clock.OnSpawned(2);
            clock.OnLaunched(1);
            clock.OnLaunched(2);
            clock.OnLegEnd(1);
            clock.OnLegEnd(2);

            Assert.AreEqual(BallWaveStep.Wait, clock.NextStep(), "the third ball has a later random delay and has not spawned");

            clock.OnSpawned(3);
            clock.OnLaunched(3);
            clock.OnLegEnd(3);
            Assert.AreEqual(BallWaveStep.Complete, clock.NextStep());
        }

        [TestMethod]
        public void ForceResolve_WritesOffEverythingStillOutstanding()
        {
            var clock = new BallWaveClock(1);
            clock.StartWave(4);
            clock.OnSpawned(1);
            clock.OnLaunched(1);

            Assert.AreEqual(BallWaveStep.Wait, clock.NextStep());
            Assert.AreEqual(4, clock.ForceResolve(), "one rolling, three that never reported");
            Assert.AreEqual(BallWaveStep.Complete, clock.NextStep());
            Assert.AreEqual(4, clock.Lost);
        }

        [TestMethod]
        public void TickLength_IsDerivedFromConsecutiveNows_AsTheLongestGap()
        {
            var clock = new BallWaveClock(1);
            Assert.AreEqual(0.1, clock.TickSeconds, 1e-9, "the 100 ms default until two ticks are seen");

            foreach (var now in new[] { 0.0, 0.1, 0.2, 1.2, 1.3 })
                clock.ObserveNow(now);

            Assert.AreEqual(1.0, clock.TickSeconds, 1e-9);
        }

        // ---- F4: the wave ends on its balls, with a fallback that is never early -------------------------------------

        /// <summary>One simulated wave on a driver that ticks every <paramref name="tick"/> s: spawn on the first tick at/after the delay, launch (MoveLeg) on the first tick at/after spawn + lead, leg end found on the first tick at/after launch + leg.</summary>
        private static double SimulateLastLegEndFound(double tick, double lead, IEnumerable<(double Delay, double Leg)> balls)
        {
            double Tick(double t) => Math.Ceiling(t / tick - 1e-9) * tick;

            return balls.Max(b =>
            {
                var spawn = Tick(b.Delay);
                var launch = Tick(spawn + lead);
                return Tick(launch + b.Leg);
            });
        }

        [TestMethod]
        public void Fallback_OnA1sTickWithRandomDelays_NeverFiresBeforeTheLastLegEnd_TheOldFlatSecondDid()
        {
            const double lead = 2.01;
            const float speed = 3f;

            var wave = new BallWave();
            wave.Routes.Add(new BallRoute(new Vector2(0, 0), new Vector2(12.03f, 0), 0.0, "a"));
            wave.Routes.Add(new BallRoute(new Vector2(0, 0), new Vector2(12.03f, 0), 3.01, "b"));
            wave.Routes.Add(new BallRoute(new Vector2(0, 0), new Vector2(6f, 0), 5.5, "c"));

            var actual = SimulateLastLegEndFound(1.0, lead, wave.Routes.Select(r => (r.Delay, BallRules.LegSeconds(r, speed))));

            Assert.IsTrue(BallRules.FallbackSeconds(wave, speed, lead, 1.0) >= actual, $"fallback {BallRules.FallbackSeconds(wave, speed, lead, 1.0)} s vs last leg end found at {actual} s");
            Assert.IsTrue(BallRules.WaveSeconds(wave, speed, lead) < actual, "control: the old ideal + 1 s would have ended the wave while a ball still rolled");
            Assert.AreEqual(3.0, BallRules.Settle(1.0), 1e-9);
            Assert.AreEqual(1.0, BallRules.Settle(0.1), 1e-9, "never under a second");
        }

        [TestMethod]
        public void Clock_DoesNotEndTheWave_UntilTheLastBallActuallyEnds_OnASlowTick()
        {
            const double tick = 1.0;
            const double lead = 2.0;
            var delays = new[] { 0.0, 3.2, 5.5 };
            var clock = new BallWaveClock(1);
            clock.StartWave(3);

            var spawnAt = delays.Select(d => Math.Ceiling(d / tick) * tick).ToArray();
            var launchAt = spawnAt.Select(s => Math.Ceiling((s + lead) / tick) * tick).ToArray();
            var endAt = launchAt.Select(l => Math.Ceiling((l + 4.0) / tick) * tick).ToArray();
            var finishedAt = -1.0;

            for (var t = 0.0; t < 30.0; t += tick)
            {
                for (var i = 0; i < 3; i++)
                {
                    if (t == spawnAt[i]) clock.OnSpawned(i + 1);
                    if (t == launchAt[i]) clock.OnLaunched(i + 1);
                    if (t == endAt[i]) clock.OnLegEnd(i + 1);
                }

                if (clock.WaveOver && finishedAt < 0)
                    finishedAt = t;
            }

            Assert.AreEqual(endAt.Max(), finishedAt, 1e-9, "the wave ends on the tick the last leg ends, not earlier");
        }

        // ---- the lead the markers and the schedule share ----------------------------------------------------------------

        [TestMethod]
        public void LeadFor_AFanUsesTheRealDistance_OthersUseTheFloor_AndMarkersOutliveIt()
        {
            var region = Pinnacle();
            var fan = P("spread=60 lead=0.5", BallPatterns.Fan);

            var near = BallRules.LeadFor(region, BallPatterns.Fan, fan, 5.0);
            var far = BallRules.LeadFor(region, BallPatterns.Fan, fan, 30.0);
            Assert.IsTrue(far > near + 2.0, $"the farther target needs the longer sidestep ({near} vs {far})");
            Assert.AreEqual(BallRules.FanLead(60, 30.0, fan.Reach, fan.EscapeSpeed), far, 1e-9);
            Assert.AreEqual(BallRules.EffectiveLead(region, BallPatterns.Fan, fan), BallRules.LeadFor(region, BallPatterns.Fan, fan, null), 1e-9, "no distance yet: the worst-case floor");

            var row = P("", BallPatterns.Row);
            Assert.AreEqual(BallRules.EffectiveLead(region, BallPatterns.Row, row), BallRules.LeadFor(region, BallPatterns.Row, row, 999.0), 1e-9, "a distance means nothing to a row");

            Assert.AreEqual(far + 1.0, BallRules.MarkerSeconds(far), 1e-9);
            Assert.IsTrue(BallRules.MarkerSeconds(far) > far, "the pillars outlive the lead");
        }

        // ---- F5: marker honesty -------------------------------------------------------------------------------------------

        [TestMethod]
        public void Cross_ShowsExactlyOnePillar_TheIntersection_AndARowShowsOne()
        {
            foreach (var region in new[] { Pinnacle(), HazardRegion.Circle("c", 0x0069, 0, false, 15f, -20f, 14f, HazardGroundMode.Plane, -47.75f, null, 2f, null, 2f, 6f) })
            {
                for (var seed = 1; seed <= 10; seed++)
                {
                    var cross = BallPatterns.Plan(region, BallPatterns.Cross, P("", BallPatterns.Cross), new Random(seed));
                    Assert.AreEqual(1, cross.Markers.Count, $"seed {seed}");
                    Assert.AreEqual(cross.SafePoint.Value, cross.Markers[0]);

                    var row = BallPatterns.Plan(region, BallPatterns.Row, P("", BallPatterns.Row), new Random(seed));
                    Assert.AreEqual(1, row.Markers.Count);
                }
            }
        }

        [TestMethod]
        public void MarkerNote_DiffersPerPattern_AndAFanDoesNotClaimItMarksTheWayThrough()
        {
            var region = Pinnacle();
            string Note(string pattern) => BallPatterns.Plan(region, pattern, P("", pattern), new Random(3)).MarkerNote;

            var notes = new[] { Note(BallPatterns.Row), Note(BallPatterns.Cross), Note(BallPatterns.Fan) };

            Assert.AreEqual(3, notes.Distinct().Count());
            StringAssert.Contains(Note(BallPatterns.Row), "way through");
            StringAssert.Contains(Note(BallPatterns.Cross), "safe spot");
            StringAssert.Contains(Note(BallPatterns.Fan), "line of fire");
            Assert.IsFalse(Note(BallPatterns.Fan).Contains("way through"));
            // C11: a ring marks its rays only when ray_markers=1 is given; the default plan is unchanged
            Assert.AreEqual("", Note(BallPatterns.RingOut));
            Assert.AreEqual("", Note(BallPatterns.RingIn));
            Assert.AreEqual("", Note(BallPatterns.Random));
            Assert.AreEqual("pillars mark the rays", BallPatterns.Plan(region, BallPatterns.RingOut, P("ray_markers=1", BallPatterns.RingOut), new Random(3)).MarkerNote);
            Assert.AreEqual("pillars mark the rays", BallPatterns.Plan(region, BallPatterns.RingIn, P("ray_markers=1", BallPatterns.RingIn), new Random(3)).MarkerNote);
        }

        [TestMethod]
        public void Rings_MarkEveryRay_OnlyAlongTheRolledStretch_AtTheFanSpacing()
        {
            // C11: during the lead a ring shows its rays; every marker sits ON a route and on a placeable point
            var region = Pinnacle();

            foreach (var pattern in new[] { BallPatterns.RingOut, BallPatterns.RingIn })
            {
                var wave = BallPatterns.Plan(region, pattern, P("ray_markers=1", pattern), new Random(5));

                Assert.IsTrue(wave.Routes.Count > 0, pattern);
                Assert.IsTrue(wave.Markers.Count >= wave.Routes.Count, $"{pattern}: at least one marker per ray");

                foreach (var m in wave.Markers)
                {
                    Assert.IsTrue(region.IsPlaceable(m.X, m.Y), $"{pattern}: marker {m} is not placeable");
                    Assert.IsTrue(wave.Routes.Any(r => DistanceToSegment(m, r.From, r.To) < 1e-3f), $"{pattern}: marker {m} is on no ray's rolled stretch");
                }

                foreach (var route in wave.Routes)
                {
                    var on = wave.Markers.Where(m => DistanceToSegment(m, route.From, route.To) < 1e-3f).ToList();

                    Assert.IsTrue(on.Count > 0 || route.Length < HazardDefaults.BallsFanMarkerSpacing, $"{pattern}: a ray of {route.Length:0.#} m carries no marker");
                }
            }
        }

        [TestMethod]
        public void Rings_DefaultPlan_IsUnchanged_NoRayMarkers()
        {
            // C11 is opt-in: an admin /hazard ring run (no ray_markers) plans the same routes and no markers at all
            var region = Pinnacle();

            foreach (var pattern in new[] { BallPatterns.RingOut, BallPatterns.RingIn })
            {
                var plain = BallPatterns.Plan(region, pattern, P("", pattern), new Random(5));
                var off = BallPatterns.Plan(region, pattern, P("ray_markers=0", pattern), new Random(5));
                var on = BallPatterns.Plan(region, pattern, P("ray_markers=1", pattern), new Random(5));

                Assert.AreEqual(0, plain.Markers.Count, $"{pattern}: no markers by default");
                Assert.AreEqual(0, off.Markers.Count, pattern);
                Assert.IsTrue(on.Markers.Count > 0, $"{pattern}: the control places markers");
                CollectionAssert.AreEqual(plain.Routes.Select(r => (r.From, r.To)).ToList(), on.Routes.Select(r => (r.From, r.To)).ToList(), $"{pattern}: the key changes only the markers");
                Assert.IsFalse(P("", pattern).RayMarkers);
            }

            StringAssert.Contains(Validate(region, "ray_markers=maybe", BallPatterns.RingOut), "ray_markers=");
            Assert.IsNull(Validate(region, "ray_markers=1", BallPatterns.RingOut));
        }

        [TestMethod]
        public void RayMarkers_IsReadLiterally_AndDocumented()
        {
            var src = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/HazardMechanics/HazardBallPatterns.cs");
            StringAssert.Contains(src, "RayMarkers = args.GetBool(\"ray_markers\", false)");
            StringAssert.Contains(src, "RayMarkers = p.RayMarkers,");

            StringAssert.Contains(ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/Managers/PropertyManager.cs"), "ray_markers=<1|0");

            var usage = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/Command/Handlers/HazardCommands.cs");
            var line = usage.Split('\n').Single(l => l.Contains("args+: ray_markers=1|0"));
            Assert.IsTrue(line.Trim().TrimEnd('+').Trim().Trim('"').Length <= 250);
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared();

            if (len2 <= 0f)
                return Vector2.Distance(p, a);

            var t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
            return Vector2.Distance(p, a + ab * t);
        }

        // ---- F6: exclusions and safe spots ----------------------------------------------------------------------------------

        private static HazardRegion WithPad(float minX, float maxX, float minY, float maxY)
            => HazardRegion.Rect("padded", 0x0069, 0, false, -4.6f, 34.6f, -34.6f, -5.4f, HazardGroundMode.Plane, -47.75f, null, 0, null, 2, 6,
                new[] { HazardExclusion.Rect(minX, maxX, minY, maxY) });

        [TestMethod]
        public void ExclusionOnAStartOrEndLine_IsRefused_NamingTheSide_AMidRegionOneIsNot()
        {
            // y -9..-5 reaches the north start line (y = -7.4): those lanes would be slid inward and leave a strip uncovered
            var north = WithPad(10f, 20f, -9f, -5f);

            StringAssert.Contains(Validate(north, "dir=n", BallPatterns.Row), "exclusion touches the north start/end line");
            StringAssert.Contains(Validate(north, "dir=n", BallPatterns.Row), "safe spot");
            StringAssert.Contains(Validate(north, "dir=s", BallPatterns.Row), "exclusion touches the south start/end line", "it is the south row's END line");
            StringAssert.Contains(Validate(north, "dirs=ne", BallPatterns.Cross), "exclusion touches");

            // east/west rows cross the pad mid-lane and start and end on clear lines: no safe spot
            Assert.IsNull(Validate(north, "dir=e", BallPatterns.Row));

            // control: a pad in the middle of the room touches no start or end line
            var middle = WithPad(10f, 20f, -30f, -10f);

            foreach (var accepted in new[] { "dir=n", "dir=s", "dir=e", "dir=w", "dir=random" })
                Assert.IsNull(Validate(middle, accepted, BallPatterns.Row), accepted);

            Assert.IsNull(Validate(middle, "", BallPatterns.Cross));
        }

        // ---- F7: the route cap -----------------------------------------------------------------------------------------------

        [TestMethod]
        public void ARowOfTooManyBalls_IsRefused_AndTheStockBallIsNot()
        {
            var huge = HazardRegion.Rect("huge", 0xA9B4, 0, true, 1f, 191f, 1f, 191f, HazardGroundMode.Terrain, 0, null, 0, null, 2, 6);

            var refused = Validate(huge, "ball_radius=0.1 player_radius=0 gap=1 dir=n", BallPatterns.Row);
            StringAssert.Contains(refused, "over the cap of 96");

            Assert.IsNull(Validate(huge, "dir=n", BallPatterns.Row), "control: stock radii give about 45 balls");
            StringAssert.Contains(Validate(huge, "ball_radius=0.1 player_radius=0 gap=1", BallPatterns.Cross), "over the cap");

            // and the wall really is that long, so the cap is guarding a real number
            var p = P("ball_radius=0.1 player_radius=0 gap=1 dir=n", BallPatterns.Row);
            Assert.IsTrue(BallPatterns.Plan(huge, BallPatterns.Row, p, new Random(1)).Routes.Count > HazardDefaults.BallsMaxRoutes);
        }

        // ---- F8: the model matches the hit radius ---------------------------------------------------------------------------------

        [TestMethod]
        public void SpawnScale_FollowsBallRadiusOnlyWhenGiven()
        {
            Assert.AreEqual(1f, BallRules.SpawnScale(P("", BallPatterns.Row)), "as authored");
            Assert.AreEqual(0.5f, BallRules.SpawnScale(P("ball_radius=0.9", BallPatterns.Row)), 1e-5f);
            Assert.AreEqual(2f, BallRules.SpawnScale(P("ball_radius=3.6", BallPatterns.Row)), 1e-5f);
            Assert.AreEqual(1f, BallRules.SpawnScale(P("ball_radius=1.8", BallPatterns.Row)), 1e-3f, "the stock radius given explicitly changes nothing");
        }

        // ---- E: escape_speed ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void EscapeSpeed_DefaultsToThree_IsInTheSettingsString_AndIsClamped()
        {
            Assert.AreEqual(3.0, P("", BallPatterns.Row).EscapeSpeed);
            Assert.AreEqual("3.0", HazardArgs.Parse(HazardDefaults.Balls).GetString("escape_speed"));
            Assert.AreEqual(5.0, P("escape_speed=5", BallPatterns.Row).EscapeSpeed);
            Assert.AreEqual(HazardDefaults.MinEscapeSpeed, P("escape_speed=0.1", BallPatterns.Row).EscapeSpeed);
            Assert.AreEqual(HazardDefaults.MaxEscapeSpeed, P("escape_speed=99", BallPatterns.Row).EscapeSpeed);
            Assert.AreEqual(1.0, HazardDefaults.MinEscapeSpeed);
            Assert.AreEqual(12.0, HazardDefaults.MaxEscapeSpeed);
        }

        [TestMethod]
        public void HigherEscapeSpeed_GivesAShorterLead_InEveryFormula()
        {
            var region = Pinnacle();

            foreach (var pattern in new[] { BallPatterns.Row, BallPatterns.Cross, BallPatterns.RingOut, BallPatterns.Fan })
            {
                var slow = BallRules.FairLead(region, pattern, P("escape_speed=2", pattern));
                var fast = BallRules.FairLead(region, pattern, P("escape_speed=6", pattern));
                Assert.IsTrue(fast < slow, $"{pattern}: {fast} should be under {slow}");
            }

            // the row formula to the digit: (36.9 / 2 - 3) / escape_speed
            Assert.AreEqual(5.15, BallRules.FairLead(region, BallPatterns.Row, P("", BallPatterns.Row)), 0.01);
            Assert.AreEqual(3.09, BallRules.FairLead(region, BallPatterns.Row, P("escape_speed=5", BallPatterns.Row)), 0.01);

            // and it flows into the lead the run really uses
            Assert.AreEqual(3.09, BallRules.EffectiveLead(region, BallPatterns.Row, P("escape_speed=5 lead=0.5", BallPatterns.Row)), 0.01);

            // a fast flee cannot push the lead under the framework floor
            Assert.IsTrue(BallRules.FairLead(region, BallPatterns.Random, P("escape_speed=12", BallPatterns.Random)) >= HazardDefaults.BallsMinLead);
        }

        [TestMethod]
        public void Clamp_Holds_AnOutOfRangeEscapeSpeedChangesTheLeadLikeItsClampedValue()
        {
            var region = Pinnacle();

            Assert.AreEqual(BallRules.FairLead(region, BallPatterns.Row, P("escape_speed=1", BallPatterns.Row)),
                BallRules.FairLead(region, BallPatterns.Row, P("escape_speed=-4", BallPatterns.Row)), 1e-9);
            Assert.AreEqual(BallRules.FairLead(region, BallPatterns.Row, P("escape_speed=12", BallPatterns.Row)),
                BallRules.FairLead(region, BallPatterns.Row, P("escape_speed=500", BallPatterns.Row)), 1e-9);
        }

        // ---- F9, F11c ------------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void FanLead_UsesTheSecantFormula_SoAWideFanNeedsMoreThanTheOldEstimate()
        {
            // ((d sin t + reach) / cos t + 0.5) / speed, t = 60 degrees, d = 35.2 m: (30.48 + 2.3) / 0.5 + 0.5 = 66.06 / 3
            Assert.AreEqual(22.02, BallRules.FanLead(120, 35.2, 2.3, 3.0), 0.02);

            var old = (35.2 * Math.Sin(Math.PI / 3) + 2.3 + 0.5) / 3.0;
            Assert.IsTrue(BallRules.FanLead(120, 35.2, 2.3, 3.0) > old * 1.8, "the old numerator undercounted by half");

            Assert.IsNull(Validate(Pinnacle(), "count=7 spread=120", BallPatterns.Fan), "still valid on the Pinnacle");
            Assert.IsTrue(double.IsFinite(BallRules.FanLead(180, 10, 2.3, 3.0)), "the angle is capped under 90 degrees");
        }

        [TestMethod]
        public void CrossLead_IsTheStraightLineToTheFarthestIntersection()
        {
            var region = Pinnacle();
            var p = P("", BallPatterns.Cross);

            // box centre to its far corner: hypot((36.9 - 6) / 2, (26.9 - 6) / 2) = hypot(15.45, 10.45) = 18.65 m, / 3
            var expected = Math.Sqrt(15.45 * 15.45 + 10.45 * 10.45) / 3.0;
            Assert.AreEqual(expected, BallRules.FairLead(region, BallPatterns.Cross, p), 0.02);
            Assert.IsTrue(BallRules.FairLead(region, BallPatterns.Cross, p) > BallRules.FairLead(region, BallPatterns.Row, P("", BallPatterns.Row)), "longer than the one-axis row estimate");
        }

        // ---- invariant 10: nothing spawns in a solid --------------------------------------------------------------------------------

        [TestMethod]
        public void NudgeStart_SlidesAlongTheLaneToTheFirstCell_AndGivesUpPastOneReach()
        {
            var from = new Vector2(0f, 0f);
            var to = new Vector2(10f, 0f);
            Func<Vector2, bool> solidUntil2 = pt => pt.X >= 2f - 1e-4f;

            Assert.AreEqual(from, BallPatterns.NudgeStart(from, to, _ => true, 2.3f), "a start that has a cell is left alone");

            var nudged = BallPatterns.NudgeStart(from, to, solidUntil2, 2.3f);
            Assert.AreEqual(2f, nudged.Value.X, 1e-3f);
            Assert.AreEqual(0f, nudged.Value.Y, 1e-3f);

            Assert.IsNull(BallPatterns.NudgeStart(from, to, solidUntil2, 1.0f), "the first cell is 2 m along, past the 1 m allowed");
            Assert.IsNull(BallPatterns.NudgeStart(from, to, _ => false, 2.3f), "no cell anywhere: the caller logs the hole");
            Assert.IsNull(BallPatterns.NudgeStart(from, from, solidUntil2, 2.3f), "a zero-length lane has nowhere to slide");

            // never slides past the lane end
            Assert.IsNull(BallPatterns.NudgeStart(from, new Vector2(1f, 0f), pt => pt.X >= 1.5f, 5f));
        }

        // ---- the source scan, extended to the new files -------------------------------------------------------------------------------

        [TestMethod]
        public void MechanicRoutesItsDecisionsThroughThePureCore()
        {
            string dir = null;

            for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null && dir == null; d = d.Parent)
            {
                var candidate = System.IO.Path.Combine(d.FullName, "Source", "ACE.Server", "HazardMechanics");
                dir = System.IO.Directory.Exists(candidate) ? candidate : null;
            }

            Assert.IsNotNull(dir, "could not find Source/ACE.Server/HazardMechanics");

            var code = string.Join("\n", System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "RollingBallMechanic.cs")).Split('\n')
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            foreach (var needle in new[]
            {
                "BallRules.LeadFor(", "BallRules.MarkerSeconds(", "BallRules.FallbackSeconds(", "BallRules.SpawnScale(",
                "clock.Hittable()", "BallPatterns.NudgeStart(", "clock.Close()",
                // the per-ball glue (live path or none, the kept tail, the destroy decision) is BallHits', fed the handle as it is and the run's hit_lag
                "BallHits.Tick(clock, id, h.Alive, h.Path, b.Tail, ctx.Now, p.HitLag, ",
                "BallHits.LegEnded(waveClock, h.Id, path, ref b.Tail, p.HitLag, ",
            })
                StringAssert.Contains(code, needle);

            // every timeout fallback deadline grows by hit_lag (two schedules, two re-checks)
            var fallbacks = Regex.Matches(code, @"BallRules\.FallbackSeconds\((?<args>[^;]*?)\)(?=[,;])").Cast<Match>().ToList();
            Assert.AreEqual(4, Regex.Matches(code, @"BallRules\.FallbackSeconds\(").Count, "the mechanic has four fallback deadlines");
            Assert.AreEqual(4, fallbacks.Count(m => m.Groups["args"].Value.TrimEnd().EndsWith(", p.HitLag", StringComparison.Ordinal)), "each passes p.HitLag: " + string.Join(" | ", fallbacks.Select(m => m.Value)));

            // the dead-handle cleanup runs BEFORE the hit loop, in Tick and in TickCadence
            Assert.IsTrue(code.IndexOf("clock.Unresolved()", StringComparison.Ordinal) < code.IndexOf("SweepWave(ctx, clock, balls)", StringComparison.Ordinal), "Tick drops dead handles before it sweeps");

            var cadenceTick = code.Substring(code.IndexOf("private void TickCadence(", StringComparison.Ordinal));
            var dead = cadenceTick.IndexOf("w.Clock.Unresolved()", StringComparison.Ordinal);
            var sweep = cadenceTick.IndexOf("SweepWave(ctx, w.Clock, w.Balls)", StringComparison.Ordinal);
            Assert.IsTrue(dead >= 0 && sweep > dead, "TickCadence drops dead handles before it sweeps");
        }
    }
}
