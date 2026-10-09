using System;
using System.Linq;
using System.Numerics;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Command;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pre-existing tracker tests were written against the fixed 5 s stuck timeout; the timeout is now the
    /// pet_follow_stuck_seconds tunable, passed in per call. This keeps those tests at the default.
    /// </summary>
    internal static class PetFollowProgressTrackerTestExtensions
    {
        public static bool Update(this PetFollowProgressTracker tracker, double now, Vector3 pet, Vector3 owner)
        {
            return tracker.Update(now, pet, owner, PetFollowSettings.DefaultStuckSeconds);
        }
    }

    /// <summary>
    /// Combat pet idle follow (CombatPet_Follow) and summon assist (/summonattackontarget).
    ///
    /// SCOPE NOTE: no test in this project can construct a live Player (both constructors reach
    /// DatabaseManager.Authentication - see MuleCommerceTests.cs), and a CombatPet needs a live owner, a
    /// landblock and a physics object. So these cover the pieces deliberately split out to be pure: the stuck
    /// tracker, the follow and teleport rules, and the assist target decision. The wiring around them - the
    /// MoveTo, the physics teleport, the landblock relocation - needs an in-game check.
    /// </summary>
    [TestClass]
    public class SummonFollowAssistTests
    {
        // ---------------------------------------------------------------- stuck tracker

        // an owner 20 m down the +X axis from a pet at the origin, unless a test says otherwise
        private static readonly Vector3 Origin = Vector3.Zero;
        private static readonly Vector3 DistantOwner = new Vector3(20.0f, 0.0f, 0.0f);

        [TestMethod]
        public void Tracker_FirstSample_OnlyOpensTheWindow()
        {
            var tracker = new PetFollowProgressTracker();

            Assert.IsFalse(tracker.IsTracking);
            Assert.IsFalse(tracker.Update(100.0, Origin, DistantOwner));
            Assert.IsTrue(tracker.IsTracking);
        }

        [TestMethod]
        public void Tracker_StationaryPet_StationaryDistantOwner_IsStuckAtFiveSeconds()
        {
            var tracker = new PetFollowProgressTracker();

            tracker.Update(0.0, Origin, DistantOwner);

            Assert.IsFalse(tracker.Update(1.0, Origin, DistantOwner));
            Assert.IsFalse(tracker.Update(4.9, Origin, DistantOwner), "stuck before the 5 s timeout");
            Assert.IsTrue(tracker.Update(5.0, Origin, DistantOwner), "not stuck at the 5 s timeout");
        }

        [TestMethod]
        public void Tracker_PetRunningTowardAFasterRecedingOwner_IsNeverStuck()
        {
            var tracker = new PetFollowProgressTracker();

            // pet at 4 m/s, owner at 6 m/s the same way: the gap GROWS the whole time, and the old
            // owner-distance metric teleported this pet every 5 s. Sampled every 0.2 s for 20 s.
            tracker.Update(0.0, Origin, DistantOwner);

            for (var i = 1; i <= 100; i++)
            {
                var t = i * 0.2;
                var pet = new Vector3((float)(4.0 * t), 0.0f, 0.0f);
                var owner = new Vector3((float)(20.0 + 6.0 * t), 0.0f, 0.0f);

                Assert.IsFalse(tracker.Update(t, pet, owner), $"reported stuck at t={t:0.0} while running toward the owner");
            }
        }

        [TestMethod]
        public void Tracker_PetMovingPerpendicularToTheOwnerDirection_IsStuck()
        {
            var tracker = new PetFollowProgressTracker();

            // owner straight up +Y, pet running along +X at 4 m/s: moving, but not toward the owner
            var owner = new Vector3(0.0f, 50.0f, 0.0f);

            tracker.Update(0.0, Origin, owner);

            for (var i = 1; i <= 24; i++)
            {
                var t = i * 0.2;
                Assert.IsFalse(tracker.Update(t, new Vector3((float)(4.0 * t), 0.0f, 0.0f), owner), $"stuck early at t={t:0.0}");
            }

            Assert.IsTrue(tracker.Update(5.0, new Vector3(20.0f, 0.0f, 0.0f), owner));
        }

        [TestMethod]
        public void Tracker_SlidingAlongGeometryAtASteepAngle_IsStuck()
        {
            var tracker = new PetFollowProgressTracker();

            // sliding along a wall almost at right angles to the owner direction: 80 cm sideways and only 1 cm
            // toward the owner's side per 0.2 s step. The sideways motion carries the pet off the owner line,
            // so the net distance closed never reaches the 0.5 m epsilon (the pet ends up FARTHER away)
            tracker.Update(0.0, Origin, DistantOwner);

            for (var i = 1; i <= 24; i++)
            {
                var t = i * 0.2;
                tracker.Update(t, new Vector3(0.01f * i, (float)(4.0 * t), 0.0f), DistantOwner);
            }

            Assert.IsTrue(tracker.Update(5.0, new Vector3(0.25f, 20.0f, 0.0f), DistantOwner));
        }

        [TestMethod]
        public void Tracker_JitterAgainstAWall_IsStuck()
        {
            var tracker = new PetFollowProgressTracker();

            // grinding against a wall: 0.3 m forward, 0.3 m back, over and over - the steps cancel
            tracker.Update(0.0, Origin, DistantOwner);

            var forward = new Vector3(0.3f, 0.0f, 0.0f);

            for (var i = 1; i <= 24; i++)
                tracker.Update(i * 0.2, i % 2 == 1 ? forward : Origin, DistantOwner);

            Assert.IsTrue(tracker.Update(5.0, forward, DistantOwner));
        }

        [DataTestMethod]
        [DataRow(3.0f)]
        [DataRow(5.0f)]
        [DataRow(10.0f)]
        public void Tracker_SidewaysJitterAgainstAWall_IsStuck(float ownerDistance)
        {
            var tracker = new PetFollowProgressTracker();

            // grinding sideways along a wall between the pet and its owner: 1 m left, 1 m right, over and over,
            // never getting any closer. Both jitter points are exactly as far from the owner as each other, so
            // the true progress of every step is zero. The old projected-displacement sum credited each step
            // with 2 / sqrt(r^2 + 1) metres toward the owner (the direction was taken from the side the pet
            // had just left), which crossed the 0.5 m epsilon within a few steps and reset the clock forever.
            var owner = new Vector3(ownerDistance, 0.0f, 0.0f);
            var left = new Vector3(0.0f, 1.0f, 0.0f);
            var right = new Vector3(0.0f, -1.0f, 0.0f);

            tracker.Update(0.0, Origin, owner);

            for (var i = 1; i <= 24; i++)
            {
                var t = i * 0.2;
                Assert.IsFalse(tracker.Update(t, i % 2 == 1 ? left : right, owner), $"stuck early at t={t:0.0}");
            }

            Assert.IsTrue(tracker.Update(5.0, left, owner), $"sideways jitter at r={ownerDistance} m was counted as progress");
        }

        [TestMethod]
        public void Tracker_SlowCrawl_CountsOnceItAddsUpPastTheEpsilon()
        {
            var tracker = new PetFollowProgressTracker();

            // 0.2 m steps toward the owner: 0.6 m accumulated on the third step is progress at 3 s
            tracker.Update(0.0, Origin, DistantOwner);
            tracker.Update(1.0, new Vector3(0.2f, 0.0f, 0.0f), DistantOwner);
            tracker.Update(2.0, new Vector3(0.4f, 0.0f, 0.0f), DistantOwner);
            Assert.IsFalse(tracker.Update(3.0, new Vector3(0.6f, 0.0f, 0.0f), DistantOwner));

            Assert.IsFalse(tracker.Update(7.9, new Vector3(0.6f, 0.0f, 0.0f), DistantOwner));
            Assert.IsTrue(tracker.Update(8.0, new Vector3(0.6f, 0.0f, 0.0f), DistantOwner));
        }

        [TestMethod]
        public void Tracker_Progress_RestartsTheClockAndTheAccumulator()
        {
            var tracker = new PetFollowProgressTracker();

            tracker.Update(0.0, Origin, DistantOwner);

            // 0.6 m toward the owner at 4 s: progress, the clock restarts from there
            Assert.IsFalse(tracker.Update(4.0, new Vector3(0.6f, 0.0f, 0.0f), DistantOwner));

            // 0.4 m more is below the epsilon ONLY if the 0.6 was cleared - carried over it would total 1.0 and
            // restart the clock again, so this also proves the accumulator reset
            Assert.IsFalse(tracker.Update(6.0, new Vector3(1.0f, 0.0f, 0.0f), DistantOwner));
            Assert.IsFalse(tracker.Update(8.9, new Vector3(1.0f, 0.0f, 0.0f), DistantOwner), "stuck less than 5 s after the last progress");
            Assert.IsTrue(tracker.Update(9.0, new Vector3(1.0f, 0.0f, 0.0f), DistantOwner));
        }

        [TestMethod]
        public void Tracker_ClimbingTowardAnOwnerAbove_CountsIn3D()
        {
            var tracker = new PetFollowProgressTracker();

            // owner directly overhead (top of a stair well): a 2D projection would see no progress at all
            var owner = new Vector3(0.0f, 0.0f, 10.0f);

            tracker.Update(0.0, Origin, owner);
            Assert.IsFalse(tracker.Update(4.0, new Vector3(0.0f, 0.0f, 1.0f), owner));
            Assert.IsFalse(tracker.Update(8.9, new Vector3(0.0f, 0.0f, 1.0f), owner), "the climb at 4 s was not counted");
        }

        [TestMethod]
        public void Tracker_PetOnTopOfItsOwner_AddsNothingAndStaysFinite()
        {
            var tracker = new PetFollowProgressTracker();

            // the owner stands exactly on the pet's previous position, and the pet steps 0.3 m off them: that is
            // -0.3 m closed, never NaN. The sum must stay finite so later progress still registers.
            tracker.Update(0.0, Origin, Origin);
            Assert.IsFalse(tracker.Update(1.0, new Vector3(0.3f, 0.0f, 0.0f), Origin));

            // the owner walks off; real progress toward them must still register (a NaN accumulator never would)
            tracker.Update(2.0, new Vector3(0.3f, 0.0f, 0.0f), DistantOwner);
            Assert.IsFalse(tracker.Update(4.0, new Vector3(1.3f, 0.0f, 0.0f), DistantOwner));
            Assert.IsFalse(tracker.Update(8.9, new Vector3(1.3f, 0.0f, 0.0f), DistantOwner), "the 1 m step at 4 s was not counted");
            Assert.IsTrue(tracker.Update(9.0, new Vector3(1.3f, 0.0f, 0.0f), DistantOwner));
        }

        [TestMethod]
        public void Tracker_Reset_OnArrivalOrTarget_OpensAFreshWindow()
        {
            var tracker = new PetFollowProgressTracker();

            tracker.Update(0.0, Origin, DistantOwner);
            tracker.Update(4.0, Origin, DistantOwner);

            // arrival, a target acquired, or a teleport attempt
            tracker.Reset();
            Assert.IsFalse(tracker.IsTracking);

            Assert.IsFalse(tracker.Update(6.0, Origin, DistantOwner), "the first sample after a reset must not report stuck");
            Assert.IsFalse(tracker.Update(10.9, Origin, DistantOwner));
            Assert.IsTrue(tracker.Update(11.0, Origin, DistantOwner));
        }

        [TestMethod]
        public void Tracker_BackAndForthAlongTheOwnerLine_NetsToZero()
        {
            var tracker = new PetFollowProgressTracker();

            // 0.4 m forward then 0.4 m back while the owner walks away down the same line: each forward step is
            // credited 0.4, each back step -0.4, so the sum never reaches the epsilon
            tracker.Update(0.0, Origin, DistantOwner, 5.0);

            for (var i = 1; i <= 24; i++)
            {
                var owner = DistantOwner + new Vector3(0.5f * i, 0.0f, 0.0f);
                tracker.Update(i * 0.2, i % 2 == 1 ? new Vector3(0.4f, 0.0f, 0.0f) : Origin, owner, 5.0);
            }

            Assert.IsTrue(tracker.Update(5.0, new Vector3(0.4f, 0.0f, 0.0f), DistantOwner + new Vector3(12.5f, 0.0f, 0.0f), 5.0));
        }

        [TestMethod]
        public void Tracker_HonoursTheTimeoutItIsGiven()
        {
            var tracker = new PetFollowProgressTracker();

            // pet_follow_stuck_seconds replaces the old 5 s constant
            tracker.Update(0.0, Origin, DistantOwner, 3.0);
            Assert.IsFalse(tracker.Update(2.9, Origin, DistantOwner, 3.0));
            Assert.IsTrue(tracker.Update(3.0, Origin, DistantOwner, 3.0));

            // 0 or less disables the stuck trigger outright
            var disabled = new PetFollowProgressTracker();
            disabled.Update(0.0, Origin, DistantOwner, 0.0);
            Assert.IsFalse(disabled.Update(1000.0, Origin, DistantOwner, 0.0));
        }

        [TestMethod]
        public void DistanceClosed_IsExact_AndIgnoresWhereTheGoalCameFrom()
        {
            var goal = new Vector3(10.0f, 0.0f, 0.0f);

            Assert.AreEqual(1.0f, PetFollowProgressTracker.DistanceClosed(Origin, new Vector3(1.0f, 0.0f, 0.0f), goal), 1e-5f);
            Assert.AreEqual(-1.0f, PetFollowProgressTracker.DistanceClosed(new Vector3(1.0f, 0.0f, 0.0f), Origin, goal), 1e-5f);

            // a move around the goal at constant distance closes nothing
            Assert.AreEqual(0.0f, PetFollowProgressTracker.DistanceClosed(new Vector3(0.0f, 1.0f, 0.0f), new Vector3(0.0f, -1.0f, 0.0f), goal), 1e-5f);
        }

        // ---------------------------------------------------------------- teleport triggers

        [TestMethod]
        public void DistanceTrigger_FiresOnlyBeyondTheConfiguredDistance()
        {
            Assert.IsFalse(PetFollowRules.ShouldTeleportForDistance(39.9f, 40.0));
            Assert.IsFalse(PetFollowRules.ShouldTeleportForDistance(40.0f, 40.0), "exactly at the distance is not beyond it");
            Assert.IsTrue(PetFollowRules.ShouldTeleportForDistance(40.1f, 40.0));

            Assert.IsFalse(PetFollowRules.ShouldTeleportForDistance(500.0f, 0.0), "0 disables the distance trigger");
            Assert.IsFalse(PetFollowRules.ShouldTeleportForDistance(500.0f, -1.0), "a negative value disables it too");
        }

        [TestMethod]
        public void MaxTimeTrigger_FiresOnceTheFollowHasRunTooLong()
        {
            // a follow that began at t=10 with the default 15 s limit
            Assert.IsFalse(PetFollowRules.FollowTimedOut(10.0, 24.9, 15.0));
            Assert.IsTrue(PetFollowRules.FollowTimedOut(10.0, 25.0, 15.0));

            Assert.IsFalse(PetFollowRules.FollowTimedOut(10.0, 10000.0, 0.0), "0 disables the max-time trigger");
        }

        [TestMethod]
        public void MaxTimeTrigger_FiresEvenWhileTheTrackerSeesProgress()
        {
            var tracker = new PetFollowProgressTracker();

            // a pet chasing an owner it can never catch: it earns progress on every sample, so the stuck rule
            // never fires, and only the max-time rule ends the follow
            tracker.Update(0.0, Origin, DistantOwner, 5.0);

            var timedOutAt = -1.0;

            for (var i = 1; i <= 100; i++)
            {
                var t = i * 0.2;
                var pet = new Vector3((float)(4.0 * t), 0.0f, 0.0f);
                var owner = new Vector3((float)(20.0 + 6.0 * t), 0.0f, 0.0f);

                Assert.IsFalse(tracker.Update(t, pet, owner, 5.0));

                if (timedOutAt < 0 && PetFollowRules.FollowTimedOut(0.0, t, 15.0))
                    timedOutAt = t;
            }

            Assert.AreEqual(15.0, timedOutAt, 0.1, "the follow must time out on the first sample at or past 15 s");
        }

        // ---------------------------------------------------------------- teleport placement

        private const uint OutdoorCell = 0xA9B40017;

        [TestMethod]
        public void AcceptPlacement_WhereItWasSent_IsAccepted()
        {
            var requested = new Vector3(100.0f, 50.0f, 20.0f);

            Assert.IsTrue(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell, requested, true));
            Assert.IsTrue(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell, requested + new Vector3(0.6f, 0.6f, 0.0f), true), "0.85 m off is within tolerance");
            Assert.IsTrue(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell, requested + new Vector3(0.0f, 0.0f, 1.5f), true), "lifted onto a slope ahead");
        }

        [TestMethod]
        public void AcceptPlacement_SlidAwayOrOutOfTheCell_IsRejected()
        {
            var requested = new Vector3(100.0f, 50.0f, 20.0f);

            Assert.IsFalse(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell + 1, requested, true), "ended in a different cell");
            Assert.IsFalse(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell, requested, false), "no physics cell");
            Assert.IsFalse(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell, requested + new Vector3(1.2f, 0.0f, 0.0f), true), "slid 1.2 m sideways");
            Assert.IsFalse(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell, requested + new Vector3(0.0f, 0.0f, -2.5f), true), "dropped 2.5 m");
            Assert.IsFalse(PetFollowRules.AcceptPlacement(OutdoorCell, requested, OutdoorCell, new Vector3(float.NaN, 50.0f, 20.0f), true), "NaN origin");
        }

        [TestMethod]
        public void InFrontCandidate_IndoorsOrOffTheBlockOrInABuilding_IsNotTried()
        {
            Assert.IsTrue(PetFollowRules.InFrontCandidateUsable(OutdoorCell, 0xA9B40018), "outdoor owner, outdoor spot in the same landblock");

            Assert.IsFalse(PetFollowRules.InFrontCandidateUsable(0x01D90105, 0x01D90106), "owner indoors: the next EnvCell may be the next room");
            Assert.IsFalse(PetFollowRules.InFrontCandidateUsable(0x01D90105, 0x01D90105), "owner indoors, even the same cell");
            Assert.IsFalse(PetFollowRules.InFrontCandidateUsable(OutdoorCell, 0xA9B40105), "outdoor owner, spot inside a building");
            Assert.IsFalse(PetFollowRules.InFrontCandidateUsable(OutdoorCell, 0), "cell lookup failed");
            Assert.IsFalse(PetFollowRules.InFrontCandidateUsable(OutdoorCell, 0xA9B50017), "a different landblock");
        }

        [TestMethod]
        public void InFrontPlacement_BlockedLineOfSight_FallsBackToTheOwnersSpot()
        {
            // a clean outdoor cell on the far side of a fence: the cell rules pass, the owner cannot see it
            Assert.IsFalse(PetFollowRules.InFrontPlacementAllowed(OutdoorCell, 0xA9B40018, () => false), "fence between owner and spot");
            Assert.IsTrue(PetFollowRules.InFrontPlacementAllowed(OutdoorCell, 0xA9B40018, () => true), "clear line of sight");
            Assert.IsFalse(PetFollowRules.InFrontPlacementAllowed(OutdoorCell, 0xA9B40018, null), "no sight test available");
        }

        [TestMethod]
        public void InFrontPlacement_SightTestRunsOnlyAfterTheCellRulesPass()
        {
            var calls = 0;
            Func<bool> sight = () => { calls++; return true; };

            // the sight test is a physics transition: never paid for a spot the cell rules already refuse
            Assert.IsFalse(PetFollowRules.InFrontPlacementAllowed(0x01D90105, 0x01D90106, sight), "owner indoors");
            Assert.IsFalse(PetFollowRules.InFrontPlacementAllowed(OutdoorCell, 0xA9B40105, sight), "building interior");
            Assert.IsFalse(PetFollowRules.InFrontPlacementAllowed(OutdoorCell, 0, sight), "failed lookup");
            Assert.AreEqual(0, calls);

            Assert.IsTrue(PetFollowRules.InFrontPlacementAllowed(OutdoorCell, 0xA9B40018, sight));
            Assert.AreEqual(1, calls);
        }

        // ---------------------------------------------------------------- unreachable target

        private static readonly Vector3 TargetBehindAWall = new Vector3(10.0f, 0.0f, 0.0f);

        [TestMethod]
        public void Reach_NoProgressAndNoAttack_IsUnreachableAtTheTimeout()
        {
            var reach = new PetTargetReachTracker();
            var target = new object();

            Assert.IsFalse(reach.Update(target, 0.0, Origin, TargetBehindAWall, 8.0), "the first sample only opens the window");
            Assert.IsFalse(reach.Update(target, 7.9, Origin, TargetBehindAWall, 8.0));
            Assert.IsTrue(reach.Update(target, 8.0, Origin, TargetBehindAWall, 8.0));
        }

        [TestMethod]
        public void Reach_AnAttackRestartsTheClock()
        {
            var reach = new PetTargetReachTracker();
            var target = new object();

            reach.Update(target, 0.0, Origin, TargetBehindAWall, 8.0);
            reach.Update(target, 5.0, Origin, TargetBehindAWall, 8.0);

            // in melee range and swinging: the pet does not move closer, but it plainly reached the target
            reach.NotifyAttack(target, 5.0);

            Assert.IsFalse(reach.Update(target, 12.9, Origin, TargetBehindAWall, 8.0));
            Assert.IsTrue(reach.Update(target, 13.0, Origin, TargetBehindAWall, 8.0));
        }

        [TestMethod]
        public void Reach_AnAttackOnSomethingElse_DoesNotCount()
        {
            var reach = new PetTargetReachTracker();
            var target = new object();

            reach.Update(target, 0.0, Origin, TargetBehindAWall, 8.0);
            reach.NotifyAttack(new object(), 5.0);

            Assert.IsTrue(reach.Update(target, 8.0, Origin, TargetBehindAWall, 8.0));
        }

        [TestMethod]
        public void Reach_ClosingOnTheTarget_IsNeverUnreachable()
        {
            var reach = new PetTargetReachTracker();
            var target = new object();

            // a target running away faster than the pet: the gap grows, the pet still earns every metre it runs
            reach.Update(target, 0.0, Origin, TargetBehindAWall, 8.0);

            for (var i = 1; i <= 100; i++)
            {
                var t = i * 0.2;
                var pet = new Vector3((float)(4.0 * t), 0.0f, 0.0f);
                var fleeing = new Vector3((float)(10.0 + 6.0 * t), 0.0f, 0.0f);

                Assert.IsFalse(reach.Update(target, t, pet, fleeing, 8.0), $"unreachable at t={t:0.0} while closing on the target");
            }
        }

        [TestMethod]
        public void Reach_SidewaysJitterAtAWall_IsUnreachable()
        {
            var reach = new PetTargetReachTracker();
            var target = new object();
            var left = new Vector3(0.0f, 1.0f, 0.0f);
            var right = new Vector3(0.0f, -1.0f, 0.0f);

            reach.Update(target, 0.0, Origin, TargetBehindAWall, 8.0);

            for (var i = 1; i < 40; i++)
                Assert.IsFalse(reach.Update(target, i * 0.2, i % 2 == 1 ? left : right, TargetBehindAWall, 8.0));

            Assert.IsTrue(reach.Update(target, 8.0, left, TargetBehindAWall, 8.0));
        }

        [TestMethod]
        public void Reach_ANewTargetOpensAFreshWindow()
        {
            var reach = new PetTargetReachTracker();
            var first = new object();
            var second = new object();

            reach.Update(first, 0.0, Origin, TargetBehindAWall, 8.0);
            reach.Update(first, 7.0, Origin, TargetBehindAWall, 8.0);

            Assert.IsFalse(reach.Update(second, 7.5, Origin, TargetBehindAWall, 8.0), "switching target must not inherit the old clock");
            Assert.IsFalse(reach.Update(second, 15.4, Origin, TargetBehindAWall, 8.0));
            Assert.IsTrue(reach.Update(second, 15.5, Origin, TargetBehindAWall, 8.0));
        }

        [TestMethod]
        public void Reach_ZeroSecondsDisablesTheRule()
        {
            var reach = new PetTargetReachTracker();
            var target = new object();

            reach.Update(target, 0.0, Origin, TargetBehindAWall, 0.0);
            Assert.IsFalse(reach.Update(target, 10000.0, Origin, TargetBehindAWall, 0.0));
        }

        [TestMethod]
        public void IgnoreList_SkipsADroppedTargetOnlyForItsWindow()
        {
            var ignored = new PetTargetIgnoreList();

            ignored.Ignore(0x80001234, 100.0, 10.0);

            Assert.IsTrue(ignored.IsIgnored(0x80001234, 100.0));
            Assert.IsTrue(ignored.IsIgnored(0x80001234, 109.9));
            Assert.IsFalse(ignored.IsIgnored(0x80005678, 105.0), "only the dropped target is skipped");

            Assert.IsFalse(ignored.IsIgnored(0x80001234, 110.0), "the window has ended");
            Assert.AreEqual(0, ignored.Count, "an expired entry is pruned on lookup");
        }

        [TestMethod]
        public void IgnoreList_ZeroSecondsIgnoresNothing_AndReIgnoringExtends()
        {
            var ignored = new PetTargetIgnoreList();

            ignored.Ignore(0x80001234, 100.0, 0.0);
            Assert.IsFalse(ignored.IsIgnored(0x80001234, 100.0));

            ignored.Ignore(0x80001234, 100.0, 10.0);
            ignored.Ignore(0x80001234, 108.0, 10.0);
            Assert.IsTrue(ignored.IsIgnored(0x80001234, 117.9));
        }

        // ---------------------------------------------------------------- tunables

        private static readonly string[] SettingKeys =
        {
            PetFollowSettings.TeleportDistanceKey,
            PetFollowSettings.MaxFollowSecondsKey,
            PetFollowSettings.StuckSecondsKey,
            PetFollowSettings.UnreachableSecondsKey,
            PetFollowSettings.UnreachableIgnoreSecondsKey,
        };

        [TestMethod]
        public void Settings_CompiledDefaults_MatchThePropertyManagerDefaults()
        {
            var defaults = PetFollowSettings.Defaults;

            Assert.AreEqual(40.0, defaults.TeleportDistance);
            Assert.AreEqual(15.0, defaults.MaxFollowSeconds);
            Assert.AreEqual(5.0, defaults.StuckSeconds);
            Assert.AreEqual(8.0, defaults.UnreachableSeconds);
            Assert.AreEqual(10.0, defaults.UnreachableIgnoreSeconds);

            var table = DefaultPropertyManager.DefaultDoubleProperties;

            Assert.AreEqual(defaults.TeleportDistance, table[PetFollowSettings.TeleportDistanceKey].Item);
            Assert.AreEqual(defaults.MaxFollowSeconds, table[PetFollowSettings.MaxFollowSecondsKey].Item);
            Assert.AreEqual(defaults.StuckSeconds, table[PetFollowSettings.StuckSecondsKey].Item);
            Assert.AreEqual(defaults.UnreachableSeconds, table[PetFollowSettings.UnreachableSecondsKey].Item);
            Assert.AreEqual(defaults.UnreachableIgnoreSeconds, table[PetFollowSettings.UnreachableIgnoreSecondsKey].Item);
        }

        [TestMethod]
        public void Settings_Read_ReturnsTheLiveValues()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(PetFollowSettings.TeleportDistanceKey, 55.0, true));
                Assert.IsTrue(PropertyManager.ModifyDouble(PetFollowSettings.MaxFollowSecondsKey, 21.0, true));
                Assert.IsTrue(PropertyManager.ModifyDouble(PetFollowSettings.StuckSecondsKey, 3.5, true));
                Assert.IsTrue(PropertyManager.ModifyDouble(PetFollowSettings.UnreachableSecondsKey, 6.0, true));
                Assert.IsTrue(PropertyManager.ModifyDouble(PetFollowSettings.UnreachableIgnoreSecondsKey, -4.0, true));

                var settings = PetFollowSettings.Read();

                Assert.AreEqual(55.0, settings.TeleportDistance);
                Assert.AreEqual(21.0, settings.MaxFollowSeconds);
                Assert.AreEqual(3.5, settings.StuckSeconds);
                Assert.AreEqual(6.0, settings.UnreachableSeconds);
                Assert.AreEqual(0.0, settings.UnreachableIgnoreSeconds, "a negative ignore window is treated as 0");
            }
            finally
            {
                foreach (var key in SettingKeys)
                    PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item, true);
            }
        }

        // ---------------------------------------------------------------- follow and teleport rules

        [TestMethod]
        public void FollowBand_ArrivalPointIsInsideTheStartBand()
        {
            // the hysteresis: a pet that has just arrived must not immediately qualify to start again
            Assert.IsFalse(PetFollowRules.ShouldStartFollow(PetFollowRules.FollowArriveDistance));
            Assert.IsFalse(PetFollowRules.ShouldStartFollow(PetFollowRules.FollowStartDistance));
            Assert.IsTrue(PetFollowRules.ShouldStartFollow(PetFollowRules.FollowStartDistance + 0.01f));
        }

        [TestMethod]
        public void CanTeleportToOwner_OnlyWhenNothingBlocksIt()
        {
            Assert.IsTrue(PetFollowRules.CanTeleportToOwner(ownerTeleporting: false, ownerDead: false, sameInstance: true, sharesTickThread: true));

            Assert.IsFalse(PetFollowRules.CanTeleportToOwner(ownerTeleporting: true, ownerDead: false, sameInstance: true, sharesTickThread: true), "owner teleporting");
            Assert.IsFalse(PetFollowRules.CanTeleportToOwner(ownerTeleporting: false, ownerDead: true, sameInstance: true, sharesTickThread: true), "owner dead");
            Assert.IsFalse(PetFollowRules.CanTeleportToOwner(ownerTeleporting: false, ownerDead: false, sameInstance: false, sharesTickThread: true), "different instance");
            Assert.IsFalse(PetFollowRules.CanTeleportToOwner(ownerTeleporting: false, ownerDead: false, sameInstance: true, sharesTickThread: false), "different tick thread");
        }

        [TestMethod]
        public void SharesTickThread_SameLandblockOrSameNonNullGroup()
        {
            var groupA = new object();
            var groupB = new object();

            Assert.IsTrue(PetFollowRules.SharesTickThread(true, groupA, groupB), "same landblock is always the same thread");
            Assert.IsTrue(PetFollowRules.SharesTickThread(false, groupA, groupA));

            Assert.IsFalse(PetFollowRules.SharesTickThread(false, groupA, groupB), "different groups tick in parallel");
            Assert.IsFalse(PetFollowRules.SharesTickThread<object>(false, null, null), "a landblock not yet grouped proves nothing");
            Assert.IsFalse(PetFollowRules.SharesTickThread(false, groupA, null));
        }

        // ---------------------------------------------------------------- summon assist decision

        [TestMethod]
        public void Assist_Off_NeverSwitches()
        {
            Assert.AreEqual(PetTargetAction.Keep, PetAssistTargeting.Decide(false, true, false, currentTargetValid: true, attackAnimating: false));
            Assert.AreEqual(PetTargetAction.FindNearest, PetAssistTargeting.Decide(false, true, false, currentTargetValid: false, attackAnimating: false));
        }

        [TestMethod]
        public void Assist_On_SwitchesToAValidTarget()
        {
            Assert.AreEqual(PetTargetAction.SwitchToAssist, PetAssistTargeting.Decide(true, true, false, currentTargetValid: true, attackAnimating: false), "mid fight");
            Assert.AreEqual(PetTargetAction.SwitchToAssist, PetAssistTargeting.Decide(true, true, false, currentTargetValid: false, attackAnimating: false), "idle or following");
        }

        [TestMethod]
        public void Assist_AlreadyOnTheTarget_DoesNotReswitch()
        {
            Assert.AreEqual(PetTargetAction.Keep, PetAssistTargeting.Decide(true, true, true, currentTargetValid: true, attackAnimating: false));
        }

        [TestMethod]
        public void Assist_InvalidTarget_FallsBackToTheDefaultPick()
        {
            // the owner's monster died or became invalid: keep a live current target, else nearest-monster pick
            Assert.AreEqual(PetTargetAction.Keep, PetAssistTargeting.Decide(true, false, false, currentTargetValid: true, attackAnimating: false));
            Assert.AreEqual(PetTargetAction.FindNearest, PetAssistTargeting.Decide(true, false, false, currentTargetValid: false, attackAnimating: false));
        }

        [TestMethod]
        public void Assist_WaitsForASwingInProgress_ButNotOnADeadTarget()
        {
            Assert.AreEqual(PetTargetAction.Keep, PetAssistTargeting.Decide(true, true, false, currentTargetValid: true, attackAnimating: true));
            Assert.AreEqual(PetTargetAction.SwitchToAssist, PetAssistTargeting.Decide(true, true, false, currentTargetValid: false, attackAnimating: true));
        }

        // ---------------------------------------------------------------- property and command

        [TestMethod]
        public void SummonAttackOnTarget_IsTheRegisteredPropertyId()
        {
            // reserved in Source/property-registry.tsv; PropertyRegistryTests is the collision gate
            Assert.AreEqual(9062, (int)PropertyBool.SummonAttackOnTarget);
        }

        [TestMethod]
        public void SummonAttackOnTargetCommand_IsRegisteredForOrdinaryPlayers()
        {
            var handler = typeof(PlayerCommands)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SelectMany(m => m.GetCustomAttributes<CommandHandlerAttribute>(), (m, a) => new { Method = m, Attribute = a })
                .SingleOrDefault(x => string.Equals(x.Attribute.Command, "summonattackontarget", StringComparison.OrdinalIgnoreCase));

            Assert.IsNotNull(handler, "No /summonattackontarget command handler is declared on PlayerCommands.");

            Assert.AreEqual(AccessLevel.Player, handler.Attribute.Access,
                "/summonattackontarget is a player preference and must not require elevated access.");

            Assert.IsTrue(handler.Attribute.Flags.HasFlag(CommandHandlerFlag.RequiresWorld),
                "/summonattackontarget reads and writes character state, so it needs a player in the world.");

            Assert.AreEqual(0, handler.Attribute.ParameterCount,
                "/summonattackontarget with no argument reports the current setting, so it must not require one.");
        }
    }
}
