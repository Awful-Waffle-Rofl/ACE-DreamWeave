using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Hunter's Echo targeting (2026-11-10 retarget fix): the pure pick, range and arrow-speed rules in HuntersEchoMath.
    /// The live tick needs a Player and a landblock, so its wiring is pinned by HuntersEchoWiringSourceScanTests; these
    /// pin the decisions that wiring hands its delegates to. No PropertyManager key is read here.
    /// </summary>
    [TestClass]
    public class HuntersEchoTargetingTests
    {
        private sealed class Mob
        {
            private static uint nextId = 1;

            public readonly uint Id = nextId++;
            public string Name;
            public float Distance;
            public bool Eligible = true;
            public bool InSight = true;
            public override string ToString() => Name;
        }

        private sealed class Probe
        {
            public readonly List<Mob> Measured = new List<Mob>();
            public readonly List<Mob> Sighted = new List<Mob>();

            public Mob Select(Mob current, IEnumerable<Mob> candidates, float maxDistance, bool hasBow = true, int maxSightTests = HuntersEchoMath.MaxSightTestsPerPick,
                HuntersEchoPickGate gate = null, double now = 0.0)
            {
                // the live wiring (CombatPet.HuntersEchoSightTest): a failed test is remembered on the gate
                return HuntersEchoMath.SelectTarget(current, candidates,
                    m => m.Eligible,
                    m => { Measured.Add(m); return m.Distance; },
                    maxDistance,
                    hasBow ? m => { Sighted.Add(m); if (!m.InSight) gate?.RecordSightFailure(m.Id, now); return m.InSight; } : (Func<Mob, bool>)null,
                    maxSightTests,
                    gate == null ? (Func<Mob, bool>)null : m => gate.SightRecentlyFailed(m.Id, now));
            }
        }

        // ------------------------------------------------------------ range

        [TestMethod]
        public void RadarRange_IsTheOutdoorRadarConstant_IndoorsToo()
        {
            // owner ruling 2026-11-11: indoors does not change the range; the sight test decides who is shot
            Assert.AreEqual(75.0f, HuntersEchoMath.RadarRange);
            Assert.AreEqual(ACE.Server.WorldObjects.Player.MaxRadarRange_Outdoors, HuntersEchoMath.RadarRange);
            Assert.AreNotEqual(ACE.Server.WorldObjects.Player.MaxRadarRange_Indoors, HuntersEchoMath.RadarRange, "the indoor radar constant is not used");
        }

        [TestMethod]
        public void EngageRange_IsTheRadar_NeverLessThanTheBow()
        {
            Assert.AreEqual(75.0f, HuntersEchoMath.EngageRange(75.0f, 58.8f), "a short bow shoots to the full radar");
            Assert.AreEqual(77.7f, HuntersEchoMath.EngageRange(75.0f, 77.7f), 1e-4f, "a bow that reaches past the radar keeps its own reach");
            Assert.AreEqual(58.8f, HuntersEchoMath.EngageRange(25.0f, 58.8f), 1e-4f, "indoors the old bow reach is never cut to the 25 m radar");
            Assert.AreEqual(75.0f, HuntersEchoMath.EngageRange(75.0f, float.NaN));
            Assert.AreEqual(75.0f, HuntersEchoMath.EngageRange(75.0f, -1.0f));
            Assert.AreEqual(75.0f, HuntersEchoMath.EngageRange(75.0f, float.PositiveInfinity));
        }

        // ------------------------------------------------------------ the pick

        [TestMethod]
        public void Select_KeepsALegalCurrentTarget_EvenWithACloserOne()
        {
            var current = new Mob { Name = "current", Distance = 30 };
            var closer = new Mob { Name = "closer", Distance = 5 };

            Assert.AreSame(current, new Probe().Select(current, new[] { closer, current }, 75));
        }

        [TestMethod]
        public void Select_DeadCurrent_TakesTheNearestLegalOne_OnTheSameCall()
        {
            var dead = new Mob { Name = "dead", Distance = 10, Eligible = false };
            var far = new Mob { Name = "far", Distance = 40 };
            var near = new Mob { Name = "near", Distance = 20 };

            Assert.AreSame(near, new Probe().Select(dead, new[] { far, dead, near }, 75));
        }

        [TestMethod]
        public void Select_NoCurrent_TakesTheNearestLegalOne()
        {
            var a = new Mob { Name = "a", Distance = 50 };
            var b = new Mob { Name = "b", Distance = 12 };

            Assert.AreSame(b, new Probe().Select(null, new[] { a, b }, 75));
        }

        [TestMethod]
        public void Select_NeverPicksAnIneligibleCandidate_AndNeverMeasuresOne()
        {
            var idle = new Mob { Name = "idle", Distance = 3, Eligible = false };
            var engaged = new Mob { Name = "engaged", Distance = 60 };

            var probe = new Probe();

            Assert.AreSame(engaged, probe.Select(null, new[] { idle, engaged }, 75));
            CollectionAssert.DoesNotContain(probe.Measured, idle, "distance is measured only for eligible candidates");
            CollectionAssert.DoesNotContain(probe.Sighted, idle);
        }

        [TestMethod]
        public void Select_FullRadar_TakesATargetPastTheOldBowRange()
        {
            // 70 m: past a 58.8 m bow, inside the 75 m radar
            var caster = new Mob { Name = "caster", Distance = 70 };

            Assert.AreSame(caster, new Probe().Select(null, new[] { caster }, HuntersEchoMath.EngageRange(75, 58.8f)));
        }

        [TestMethod]
        public void Select_OutOfRange_IsNeverPicked_AndNeverSightChecked()
        {
            var beyond = new Mob { Name = "beyond", Distance = 80 };

            var probe = new Probe();

            Assert.IsNull(probe.Select(null, new[] { beyond }, 75));
            Assert.AreEqual(0, probe.Sighted.Count, "the sight test is never run on a candidate out of range");
        }

        [TestMethod]
        public void Select_CurrentLeavesRange_IsDroppedForTheNextLegalOne()
        {
            var current = new Mob { Name = "current", Distance = 76 };
            var next = new Mob { Name = "next", Distance = 50 };

            Assert.AreSame(next, new Probe().Select(current, new[] { current, next }, 75));
        }

        [TestMethod]
        public void Select_CurrentLosesSight_IsDroppedForTheNextLegalOne()
        {
            var current = new Mob { Name = "current", Distance = 10, InSight = false };
            var next = new Mob { Name = "next", Distance = 30 };

            var probe = new Probe();

            Assert.AreSame(next, probe.Select(current, new[] { current, next }, 75));
            Assert.AreEqual(1, probe.Sighted.FindAll(m => ReferenceEquals(m, current)).Count, "the dropped current is sight-checked once, not again as a candidate");
        }

        [TestMethod]
        public void Select_NearestBehindAWall_IsSkippedForTheNextInSight()
        {
            var walled = new Mob { Name = "walled", Distance = 8, InSight = false };
            var clear = new Mob { Name = "clear", Distance = 25 };
            var farther = new Mob { Name = "farther", Distance = 40 };

            var probe = new Probe();

            Assert.AreSame(clear, probe.Select(null, new[] { farther, clear, walled }, 75));
            CollectionAssert.AreEqual(new[] { walled, clear }, probe.Sighted, "sight is tested nearest first and stops at the first clear one");
        }

        [TestMethod]
        public void Select_NothingInSight_ReturnsNull()
        {
            var a = new Mob { Name = "a", Distance = 8, InSight = false };
            var b = new Mob { Name = "b", Distance = 9, InSight = false };

            Assert.IsNull(new Probe().Select(null, new[] { a, b }, 75));
        }

        [TestMethod]
        public void Select_NoBow_SkipsSightAndRange_TheOldRule()
        {
            var walled = new Mob { Name = "walled", Distance = 500, InSight = false };

            var probe = new Probe();

            Assert.AreSame(walled, probe.Select(null, new[] { walled }, float.MaxValue, hasBow: false));
            Assert.AreEqual(0, probe.Sighted.Count);
        }

        [TestMethod]
        public void Select_NaNDistance_IsNeverPicked()
        {
            var broken = new Mob { Name = "broken", Distance = float.NaN };

            Assert.IsNull(new Probe().Select(null, new[] { broken }, float.MaxValue));
            Assert.IsNull(new Probe().Select(broken, Array.Empty<Mob>(), float.MaxValue));
        }

        [TestMethod]
        public void Select_NullCandidatesAndNullEntries_AreTolerated()
        {
            Assert.IsNull(new Probe().Select(null, null, 75));
            Assert.IsNull(new Probe().Select(null, new Mob[] { null }, 75));
        }

        // ------------------------------------------------------------ sight-test cost (review F3)

        private static Mob[] Walled(int count, float firstDistance)
        {
            var mobs = new Mob[count];
            for (var i = 0; i < count; i++)
                mobs[i] = new Mob { Name = "walled" + i, Distance = firstDistance + i, InSight = false };
            return mobs;
        }

        [TestMethod]
        public void Select_TenWalledCandidates_SightTestsNoMoreThanTheCap()
        {
            var probe = new Probe();

            Assert.IsNull(probe.Select(null, Walled(10, 5), 75));
            Assert.AreEqual(HuntersEchoMath.MaxSightTestsPerPick, probe.Sighted.Count, "one pick sight-tests at most the nearest six");
            Assert.AreEqual(6, HuntersEchoMath.MaxSightTestsPerPick);
        }

        /// <summary>
        /// Review D1: six walled monsters nearer than a clear seventh must not keep the Echo idle. The first pick spends
        /// its six tests on the walled ones and remembers them; the next pick (after the empty-pick backoff) skips them
        /// for free and tests the seventh.
        /// </summary>
        [TestMethod]
        public void Select_SixWalledNearer_TheClearSeventhIsChosenWithinTwoPicks()
        {
            var walled = Walled(6, 5);
            var clearSeventh = new Mob { Name = "clear", Distance = 40 };
            var candidates = new List<Mob>(walled) { clearSeventh };

            var gate = new HuntersEchoPickGate();
            Mob chosen = null;
            var picks = 0;
            var now = 100.0;

            while (chosen == null && picks < 2)
            {
                Assert.IsTrue(gate.ShouldPick(false, now));

                var probe = new Probe();
                chosen = probe.Select(null, candidates, 75, gate: gate, now: now);
                picks++;

                Assert.IsTrue(probe.Sighted.Count <= HuntersEchoMath.MaxSightTestsPerPick, $"pick {picks} ran {probe.Sighted.Count} sight tests");

                gate.OnPicked(chosen != null, now);
                now += HuntersEchoMath.EmptyPickBackoffSeconds;
            }

            Assert.AreSame(clearSeventh, chosen, "the clear seventh must be chosen within two picks");
            Assert.AreEqual(2, picks, "the first pick only reached the six walled ones");

            // control: with no failure memory (the pre-fix behaviour) every pick re-tests the same six and finds nothing
            Assert.IsNull(new Probe().Select(null, candidates, 75));
            Assert.IsNull(new Probe().Select(null, candidates, 75));
        }

        [TestMethod]
        public void Select_EighteenDeep_EveryCandidateIsTestedBeforeTheNearestSetIsRetried()
        {
            // 17 walled, the clear one 18th nearest: three picks, a backoff apart, test three fresh sets of six
            var walled = Walled(17, 5);
            var clear = new Mob { Name = "clear", Distance = 60 };
            var candidates = new List<Mob>(walled) { clear };

            var gate = new HuntersEchoPickGate();
            var now = 100.0;
            Mob chosen = null;

            for (var pick = 0; pick < 3 && chosen == null; pick++)
            {
                chosen = new Probe().Select(null, candidates, 75, gate: gate, now: now);
                gate.OnPicked(chosen != null, now);
                now += HuntersEchoMath.EmptyPickBackoffSeconds;
            }

            Assert.AreSame(clear, chosen);
            Assert.IsTrue(HuntersEchoMath.SightFailureTtlSeconds > 2 * HuntersEchoMath.EmptyPickBackoffSeconds,
                "the failure memory must outlast two backoffs for three picks to reach 18 deep");
        }

        /// <summary>
        /// The memory orders, it does not block (review D4): within the TTL a recent failure is tested AFTER fresh
        /// candidates, even nearer ones; once the TTL has passed it is back in plain distance order.
        /// </summary>
        [TestMethod]
        public void SightFailure_WithinTtl_TestedAfterFreshOnes_AfterTtl_BackInDistanceOrder()
        {
            var near = new Mob { Name = "near", Distance = 10, InSight = false };
            var far = new Mob { Name = "far", Distance = 30, InSight = false };
            var gate = new HuntersEchoPickGate();

            gate.RecordSightFailure(near.Id, 100.0);

            var withinTtl = new Probe();
            Assert.IsNull(withinTtl.Select(null, new[] { near, far }, 75, gate: gate, now: 100.0 + HuntersEchoMath.SightFailureTtlSeconds - 0.01));
            CollectionAssert.AreEqual(new[] { far, near }, withinTtl.Sighted, "a recent failure is re-tested only after the fresh candidate");

            // a separate gate: the probe above remembered both of its own failures
            var expiryGate = new HuntersEchoPickGate();
            expiryGate.RecordSightFailure(near.Id, 100.0);
            expiryGate.RecordSightFailure(far.Id, 100.0 + HuntersEchoMath.SightFailureTtlSeconds);

            var expired = new Probe();
            expired.Select(null, new[] { near, far }, 75, gate: expiryGate, now: 100.0 + HuntersEchoMath.SightFailureTtlSeconds);
            CollectionAssert.AreEqual(new[] { near, far }, expired.Sighted, "the expired failure is fresh again and tested first; the new one waits");
        }

        /// <summary>
        /// Review D4 (a): a lone engaged target that flickers behind cover for one tick is re-acquired by the first pick
        /// after the backoff, not shut out for the memory window.
        /// </summary>
        [TestMethod]
        public void SightFailure_LoneTargetFlickers_IsReacquiredOnTheFirstPickAfterTheBackoff()
        {
            var lone = new Mob { Name = "lone", Distance = 20, InSight = false };
            var gate = new HuntersEchoPickGate();

            // t = 100: behind cover, the pick finds nothing and the backoff starts
            Assert.IsNull(new Probe().Select(null, new[] { lone }, 75, gate: gate, now: 100.0));
            gate.OnPicked(false, 100.0);

            // t = 100.2: back in the open, but the backoff holds the pick
            lone.InSight = true;
            Assert.IsFalse(gate.ShouldPick(false, 100.2));

            // the first pick at or after 100.6 takes it, though its failure is still remembered
            var now = 100.0 + HuntersEchoMath.EmptyPickBackoffSeconds;
            Assert.IsTrue(gate.ShouldPick(false, now));
            Assert.IsTrue(gate.SightRecentlyFailed(lone.Id, now));
            Assert.AreSame(lone, new Probe().Select(null, new[] { lone }, 75, gate: gate, now: now));
        }

        /// <summary>Review D4 (c): both passes together never run more than the cap, whatever the memory holds.</summary>
        [TestMethod]
        public void SightFailure_EveryPick_RunsAtMostTheCap()
        {
            var mobs = Walled(20, 5);
            var gate = new HuntersEchoPickGate();

            // half the wall already remembered, interleaved, so both passes have work to do
            for (var i = 0; i < mobs.Length; i += 2)
                gate.RecordSightFailure(mobs[i].Id, 100.0);

            var now = 100.1;

            for (var pick = 0; pick < 8; pick++)
            {
                var probe = new Probe();
                Assert.IsNull(probe.Select(null, mobs, 75, gate: gate, now: now));
                Assert.IsTrue(probe.Sighted.Count <= HuntersEchoMath.MaxSightTestsPerPick, $"pick {pick} ran {probe.Sighted.Count} sight tests");
                now += HuntersEchoMath.EmptyPickBackoffSeconds;
            }

            // with fewer fresh candidates than the cap, pass 2 spends the rest on recent failures, nearest first
            var few = Walled(8, 5);
            var fewGate = new HuntersEchoPickGate();
            for (var i = 2; i < few.Length; i++)
                fewGate.RecordSightFailure(few[i].Id, 100.0);

            var mixed = new Probe();
            mixed.Select(null, few, 75, gate: fewGate, now: 100.1);
            CollectionAssert.AreEqual(new[] { few[0], few[1], few[2], few[3], few[4], few[5] }, mixed.Sighted);
        }

        [TestMethod]
        public void SightFailure_HeldTargetIsAlwaysRechecked_OutsideTheCap()
        {
            var held = new Mob { Name = "held", Distance = 4 };
            var gate = new HuntersEchoPickGate();
            gate.RecordSightFailure(held.Id, 100.0);

            var probe = new Probe();
            Assert.AreSame(held, probe.Select(held, Walled(10, 5), 75, gate: gate, now: 100.1), "a remembered failure never skips the held target's own re-check");
            Assert.AreEqual(1, probe.Sighted.Count);
        }

        [TestMethod]
        public void SightFailure_MemoryIsPrunedAndBounded_AndResetClearsIt()
        {
            var gate = new HuntersEchoPickGate();

            for (uint id = 1; id <= 10; id++)
                gate.RecordSightFailure(id, 100.0);

            gate.RecordSightFailure(99, 100.0 + HuntersEchoMath.SightFailureTtlSeconds);
            Assert.AreEqual(1, gate.RememberedFailures, "expired entries are pruned on the next record");

            for (uint id = 1000; id < 1000 + HuntersEchoPickGate.MaxRememberedFailures + 20; id++)
                gate.RecordSightFailure(id, 200.0 + id * 0.0001);

            Assert.AreEqual(HuntersEchoPickGate.MaxRememberedFailures, gate.RememberedFailures, "the memory never grows past its bound");

            gate.Reset();
            Assert.AreEqual(0, gate.RememberedFailures);
        }

        [TestMethod]
        public void Select_HeldTargetsOwnRecheck_IsOutsideTheCap()
        {
            var current = new Mob { Name = "current", Distance = 4, InSight = false };
            var probe = new Probe();

            Assert.IsNull(probe.Select(current, Walled(10, 5), 75));
            Assert.AreEqual(1 + HuntersEchoMath.MaxSightTestsPerPick, probe.Sighted.Count);
        }

        [TestMethod]
        public void Select_NoBow_IsNotCapped()
        {
            var probe = new Probe();

            Assert.IsNotNull(probe.Select(null, Walled(10, 5), float.MaxValue, hasBow: false, maxSightTests: 0));
        }

        [TestMethod]
        public void PickGate_EmptyPickWaitsTheBackoff_AHeldTargetNeverWaits()
        {
            var gate = new HuntersEchoPickGate();

            Assert.IsTrue(gate.ShouldPick(holdsTarget: false, now: 100.0), "a fresh Echo picks at once");

            gate.OnPicked(found: false, now: 100.0);

            Assert.IsFalse(gate.ShouldPick(false, 100.2));
            Assert.IsFalse(gate.ShouldPick(false, 100.0 + HuntersEchoMath.EmptyPickBackoffSeconds - 0.01));
            Assert.IsTrue(gate.ShouldPick(false, 100.0 + HuntersEchoMath.EmptyPickBackoffSeconds));
            Assert.IsTrue(gate.ShouldPick(holdsTarget: true, now: 100.2), "a held target is re-checked every tick");
            Assert.AreEqual(0.6, HuntersEchoMath.EmptyPickBackoffSeconds, 1e-9);
        }

        [TestMethod]
        public void PickGate_FoundTargetClearsTheBackoff_AndResetPicksAtOnce()
        {
            var gate = new HuntersEchoPickGate();

            gate.OnPicked(false, 100.0);
            gate.OnPicked(true, 100.1);
            Assert.IsTrue(gate.ShouldPick(false, 100.2), "a found target clears the backoff");

            gate.OnPicked(false, 200.0);
            gate.Reset();
            Assert.IsTrue(gate.ShouldPick(false, 200.1), "a refresh engages at once");
        }

        // ------------------------------------------------------------ offset split (review F2)

        [TestMethod]
        public void HorizontalAndHeight_TargetAbove_IsPositiveHeight()
        {
            var (horizontal, height) = HuntersEchoMath.HorizontalAndHeight(new System.Numerics.Vector3(3.0f, 4.0f, 12.0f));

            Assert.AreEqual(5.0f, horizontal, 1e-5f);
            Assert.AreEqual(12.0f, height, 1e-5f);
        }

        [TestMethod]
        public void HorizontalAndHeight_TargetBelow_IsNegativeHeight()
        {
            var (horizontal, height) = HuntersEchoMath.HorizontalAndHeight(new System.Numerics.Vector3(-6.0f, 8.0f, -7.5f));

            Assert.AreEqual(10.0f, horizontal, 1e-5f);
            Assert.AreEqual(-7.5f, height, 1e-5f);
        }

        [TestMethod]
        public void HorizontalAndHeight_FeedsArrowSpeed_UphillNeedsMoreThanDownhill()
        {
            var (dUp, hUp) = HuntersEchoMath.HorizontalAndHeight(new System.Numerics.Vector3(60, 0, 15));
            var (dDown, hDown) = HuntersEchoMath.HorizontalAndHeight(new System.Numerics.Vector3(60, 0, -15));

            Assert.IsTrue(HuntersEchoMath.ArrowSpeed(1.0f, dUp, hUp, -G, Cap) > HuntersEchoMath.ArrowSpeed(1.0f, dDown, hDown, -G, Cap));
        }

        // ------------------------------------------------------------ arrow speed

        private const float G = 9.8f;
        private const float Cap = 50.0f;

        [TestMethod]
        public void ArrowSpeed_InsideTheBowsReach_IsTheBowsSpeed()
        {
            // 30 m/s reaches 91.8 m on the flat; 50 m needs far less
            Assert.AreEqual(30.0f, HuntersEchoMath.ArrowSpeed(30.0f, 50.0f, 0.0f, -G, Cap));
        }

        [TestMethod]
        public void ArrowSpeed_PastTheBowsReach_IsRaisedEnoughToArrive()
        {
            // a 24 m/s bow reaches 58.8 m (v^2 / g); a 70 m target needs more
            var speed = HuntersEchoMath.ArrowSpeed(24.0f, 70.0f, 0.0f, -G, Cap);

            Assert.IsTrue(speed > 24.0f);
            Assert.IsTrue(speed * speed / G >= 70.0f, $"{speed} m/s cannot reach 70 m on the flat");
        }

        [TestMethod]
        public void ArrowSpeed_AccountsForAnUphillTarget()
        {
            var d = 60.0f;
            var h = 15.0f;

            var speed = HuntersEchoMath.ArrowSpeed(10.0f, d, h, -G, Cap);

            var minimum = Math.Sqrt(G * (h + Math.Sqrt(d * d + h * h)));
            Assert.IsTrue(speed >= minimum, $"{speed} m/s is below the {minimum} m/s needed to reach ({d}, {h})");
        }

        [TestMethod]
        public void ArrowSpeed_FullOutdoorRadar_IsReachableUnderThePhysicsCap()
        {
            // the farthest target the Echo is allowed (75 m, even 20 m up) never needs more than the physics cap
            var speed = HuntersEchoMath.ArrowSpeed(1.0f, HuntersEchoMath.RadarRange, 20.0f, -G, Cap);

            Assert.IsTrue(speed < Cap, $"{speed} m/s was clamped by the cap");
        }

        [TestMethod]
        public void ArrowSpeed_IsCappedAtThePhysicsMaximum()
        {
            Assert.AreEqual(Cap, HuntersEchoMath.ArrowSpeed(10.0f, 1000.0f, 0.0f, -G, Cap));
            Assert.AreEqual(Cap, HuntersEchoMath.ArrowSpeed(80.0f, 10.0f, 0.0f, -G, Cap), "a bow faster than the cap is held to it, as GetProjectileSpeed does");
        }

        [TestMethod]
        public void ArrowSpeed_BadInput_KeepsTheBowsSpeed()
        {
            Assert.AreEqual(24.0f, HuntersEchoMath.ArrowSpeed(24.0f, float.NaN, 0.0f, -G, Cap));
            Assert.AreEqual(24.0f, HuntersEchoMath.ArrowSpeed(24.0f, 70.0f, float.PositiveInfinity, -G, Cap));
            Assert.AreEqual(24.0f, HuntersEchoMath.ArrowSpeed(24.0f, 70.0f, 0.0f, 0.0f, Cap));
        }
    }
}
