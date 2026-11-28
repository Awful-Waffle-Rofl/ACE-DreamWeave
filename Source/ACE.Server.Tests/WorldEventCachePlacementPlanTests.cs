using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using ACE.Entity;
using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using Attempt = ACE.Server.WorldEvents.WorldEventRewardDelivery.CachePlacementAttempt;
using Result = ACE.Server.WorldEvents.WorldEventRewardDelivery.CachePlaceResult;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Weave Cache placement plan (WorldEventRewardDelivery.BuildPlacementPlan / PlaceFirstAccepted), dat-free.
    ///
    /// The prod failure it covers (2026-11-23, run 4): a single-cache run asks for the anchor itself, and the old
    /// code's only retry was "the anchor" - the same point again - so one crowded centre refused the cache twice and
    /// the run paid nobody. The snap delegate is faked (it writes a sentinel Z) so these tests need no landscape;
    /// WorldEventCachePlacementDatTests drives the same plan through the real physics placement.
    /// </summary>
    [TestClass]
    public class WorldEventCachePlacementPlanTests
    {
        // The prod run's anchor: 0x934B001A [76.300003 38.099998 14.000000].
        private const uint Cell = 0x934B001A;
        private const float AX = 76.300003f, AY = 38.099998f, AZ = 14.0f;
        private const float SnappedZ = 50f;

        private static Position Anchor(float x = AX, float y = AY, uint cell = Cell) => new Position(cell, x, y, AZ, 0f, 0f, 0f, 1f, 0);

        private static void FakeSnap(Position p) => p.PositionZ = SnappedZ;

        private static int RingCount => WorldEventRewardDelivery.FallbackRingRadii.Count * WorldEventRewardDelivery.FallbackPointsPerRing;

        [TestMethod]
        public void SingleCache_SecondAttemptIsNeverTheSamePointAsTheFirst()
        {
            var anchor = Anchor();
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, FakeSnap);

            Assert.IsTrue(plan.Count > 2, "a plan of one or two points is the bug");

            for (var i = 1; i < plan.Count; i++)
            {
                var same = plan[i].Ethereal == plan[i - 1].Ethereal
                    && plan[i].Position.Cell == plan[i - 1].Position.Cell
                    && Vector3.Distance(plan[i].Position.Pos, plan[i - 1].Position.Pos) < 0.01f;

                Assert.IsFalse(same, $"attempt {i} repeats attempt {i - 1} exactly - the old 'retry at the anchor' for a single cache");
            }

            Assert.AreEqual("requested", plan[0].Kind);
            Assert.IsFalse(plan[0].Ethereal);
            Assert.AreEqual(AZ, plan[0].Position.PositionZ, "the requested point is tried exactly as asked");
        }

        [TestMethod]
        public void SingleCache_PlanIsRequestedThenRingsThenEtherealTail()
        {
            var anchor = Anchor();
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, FakeSnap);

            // requested (== anchor, so no separate anchor entry), every ring point, then ethereal requested + snapped anchor
            Assert.AreEqual(1 + RingCount + 2, plan.Count, string.Join("\n", plan.Select(Describe)));

            var firstEthereal = plan.FindIndex(a => a.Ethereal);
            Assert.AreEqual(1 + RingCount, firstEthereal, "every solid attempt comes before the first ethereal one");
            Assert.IsTrue(plan.Skip(firstEthereal).All(a => a.Ethereal), "nothing solid after the ethereal tail starts");

            Assert.AreEqual("requested", plan[firstEthereal].Kind);
            Assert.AreEqual(AZ, plan[firstEthereal].Position.PositionZ);
            Assert.AreEqual("anchor-ground", plan[firstEthereal + 1].Kind);
            Assert.AreEqual(SnappedZ, plan[firstEthereal + 1].Position.PositionZ, "the last resort includes the anchor on the ground under it");
        }

        [TestMethod]
        public void Rings_AreAtTheDeclaredRadii_Snapped_AndSolid()
        {
            var anchor = Anchor();
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, FakeSnap);
            var rings = plan.Where(a => a.Kind == "ring").ToList();

            Assert.AreEqual(RingCount, rings.Count);
            Assert.IsTrue(rings.All(a => !a.Ethereal));
            Assert.IsTrue(rings.All(a => a.Position.PositionZ == SnappedZ), "every ring point is terrain-snapped");

            var offset = 0;

            foreach (var radius in WorldEventRewardDelivery.FallbackRingRadii)
            {
                for (var i = 0; i < WorldEventRewardDelivery.FallbackPointsPerRing; i++)
                {
                    var p = rings[offset + i].Position;
                    var d = Vector2.Distance(new Vector2(p.PositionX, p.PositionY), new Vector2(AX, AY));
                    Assert.AreEqual(radius, d, 0.01f, $"ring point {offset + i}");
                }

                offset += WorldEventRewardDelivery.FallbackPointsPerRing;
            }

            var radii = WorldEventRewardDelivery.FallbackRingRadii;
            for (var i = 1; i < radii.Count; i++)
                Assert.IsTrue(radii[i] > radii[i - 1], "rings grow outward");

            // past the engine's own 4 m placement slide, or the first ring adds nothing the slide did not already try
            Assert.IsTrue(radii[0] > 4f);
        }

        [TestMethod]
        public void MultiCache_RingPointIsFollowedByTheAnchor()
        {
            var anchor = Anchor();
            var requested = new Position(anchor);
            requested.SetPosition(anchor.Pos + new Vector3(3f, 0f, 0f));

            var plan = WorldEventRewardDelivery.BuildPlacementPlan(requested, anchor, FakeSnap);

            Assert.AreEqual("requested", plan[0].Kind);
            Assert.AreEqual("anchor", plan[1].Kind);
            Assert.AreEqual(AX, plan[1].Position.PositionX, 1e-4f);
            Assert.AreEqual(AZ, plan[1].Position.PositionZ, "the composed anchor is tried exactly as composed, as before");
            Assert.AreEqual(2 + RingCount + 3, plan.Count, "ethereal tail: requested, anchor, anchor on the ground");
        }

        [TestMethod]
        public void NearALandblockEdge_PointsOffTheBlockAreDropped()
        {
            var anchor = Anchor(x: 2f);
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, FakeSnap);

            Assert.IsTrue(plan.Count(a => a.Kind == "ring") < RingCount, "some ring points fall west of the block edge");
            Assert.IsTrue(plan.All(a => a.Position.Cell >> 16 == anchor.Cell >> 16 && a.Position.InstancedLandblock == anchor.InstancedLandblock),
                "every candidate stays on the anchor's landblock, whose action queue this runs on");
        }

        [TestMethod]
        public void IndoorAnchor_HasNoRingsAndIsNeverSnapped()
        {
            var anchor = Anchor(cell: 0x934B0105);
            var snapped = 0;

            var plan = WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, p => snapped++);

            Assert.AreEqual(0, snapped, "indoors the terrain has no meaning");
            Assert.AreEqual(0, plan.Count(a => a.Kind == "ring"), "a 5-12 m offset indoors is a wall or another room");
            Assert.AreEqual(2, plan.Count, "solid requested, then ethereal requested");
            Assert.IsFalse(plan[0].Ethereal);
            Assert.IsTrue(plan[1].Ethereal);
        }

        [TestMethod]
        public void ASnapThatThrows_KeepsTheRawZ()
        {
            var anchor = Anchor();
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, p => throw new InvalidOperationException("no landcell"));

            Assert.IsTrue(plan.Count(a => a.Kind == "ring") == RingCount);
            Assert.IsTrue(plan.All(a => a.Position.PositionZ == AZ));
        }

        [TestMethod]
        public void PlanPositions_AreCopies_NotTheAnchorItself()
        {
            var anchor = Anchor();
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(anchor, anchor, FakeSnap);

            Assert.IsTrue(plan.All(a => !ReferenceEquals(a.Position, anchor)), "a physics placement mutates Location; the composition's anchor must not move");
            Assert.AreEqual(AZ, anchor.PositionZ);
        }

        // ================= PlaceFirstAccepted =================

        private static Result Accept(bool ok) => ok ? Result.Placed : Result.Refused;

        [TestMethod]
        public void PlaceFirstAccepted_ACrowdOnlyRefusal_IsPlacedByTheEtherealTail()
        {
            var anchor = Anchor();
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, FakeSnap);
            var tried = 0;

            // a crowd: every solid attempt refused, an ethereal one accepted
            var outcome = WorldEventRewardDelivery.PlaceFirstAccepted(plan, a => { tried++; return Accept(a.Ethereal); }, () => false);

            Assert.AreEqual(plan.FindIndex(a => a.Ethereal), outcome.Index);
            Assert.AreEqual(outcome.Index + 1, tried, "nothing is tried after the accepted attempt");
            Assert.AreEqual(tried, outcome.Tried);
            Assert.IsTrue(outcome.Used.Ethereal);
            Assert.IsFalse(outcome.Permanent);
        }

        [TestMethod]
        public void PlaceFirstAccepted_ReturnsTheFirstAccepted()
        {
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(Anchor(), Anchor(), FakeSnap);
            var calls = 0;

            Assert.AreEqual(0, WorldEventRewardDelivery.PlaceFirstAccepted(plan, a => { calls++; return Result.Placed; }, () => false).Index);
            Assert.AreEqual(1, calls);

            calls = 0;
            Assert.AreEqual(3, WorldEventRewardDelivery.PlaceFirstAccepted(plan, a => Accept(calls++ == 3), () => false).Index);
        }

        [TestMethod]
        public void PlaceFirstAccepted_StopsAsSoonAsTheHeldListCloses()
        {
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(Anchor(), Anchor(), FakeSnap);
            var calls = 0;

            var outcome = WorldEventRewardDelivery.PlaceFirstAccepted(plan, a => { calls++; return Result.Refused; }, () => calls >= 2);

            Assert.AreEqual(-1, outcome.Index);
            Assert.AreEqual(2, calls, "no further objects are built once the run is cleaning up");
            Assert.IsFalse(outcome.Permanent);
        }

        [TestMethod]
        public void PlaceFirstAccepted_EveryRefusal_IsMinusOne()
        {
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(Anchor(), Anchor(), FakeSnap);
            var calls = 0;

            var outcome = WorldEventRewardDelivery.PlaceFirstAccepted(plan, a => { calls++; return Result.Refused; }, () => false);

            Assert.AreEqual(-1, outcome.Index);
            Assert.AreEqual(plan.Count, calls, "every candidate is tried before giving up");
            Assert.AreEqual(plan.Count, outcome.Tried);
            Assert.AreEqual(-1, WorldEventRewardDelivery.PlaceFirstAccepted(null, a => Result.Placed, null).Index);
            Assert.AreEqual(-1, WorldEventRewardDelivery.PlaceFirstAccepted(new List<Attempt>(), a => Result.Placed, null).Index);
        }

        /// <summary>
        /// Review finding 1: a cache weenie that does not build fails identically at every candidate, so the plan
        /// stops at the first Permanent instead of creating and destroying ~30 objects.
        /// </summary>
        [TestMethod]
        public void PlaceFirstAccepted_APermanentFailure_IsTriedExactlyOnce()
        {
            var plan = WorldEventRewardDelivery.BuildPlacementPlan(Anchor(), Anchor(), FakeSnap);
            var calls = 0;

            var outcome = WorldEventRewardDelivery.PlaceFirstAccepted(plan, a => { calls++; return Result.Permanent; }, () => false);

            Assert.AreEqual(1, calls, "a permanent failure is not retried at the next candidate");
            Assert.IsTrue(outcome.Permanent);
            Assert.AreEqual(-1, outcome.Index);
            Assert.AreEqual(1, outcome.Tried);

            // control: the same plan with a plain refusal walks every candidate
            calls = 0;
            WorldEventRewardDelivery.PlaceFirstAccepted(plan, a => { calls++; return Result.Refused; }, () => false);
            Assert.AreEqual(plan.Count, calls);
        }

        /// <summary>
        /// Review finding 2: the plan is lazy, so a cache accepted at its requested point snaps nothing; and each
        /// further attempt costs at most one snap.
        /// </summary>
        [TestMethod]
        public void LazyPlan_AcceptedAtAttemptZero_NeverSnaps()
        {
            var anchor = Anchor();
            var snaps = 0;

            var outcome = WorldEventRewardDelivery.PlaceFirstAccepted(
                WorldEventRewardDelivery.EnumeratePlacementPlan(new Position(anchor), anchor, 0, p => snaps++),
                a => Result.Placed, () => false);

            Assert.AreEqual(0, outcome.Index);
            Assert.AreEqual(0, snaps, "no ring point is built or snapped when the first attempt is accepted");

            // control: refusing the first two (requested, then the first ring point) snaps exactly that one ring point
            snaps = 0;
            var calls = 0;
            WorldEventRewardDelivery.PlaceFirstAccepted(
                WorldEventRewardDelivery.EnumeratePlacementPlan(new Position(anchor), anchor, 0, p => snaps++),
                a => Accept(calls++ == 1), () => false);
            Assert.AreEqual(1, snaps);

            // and the materialised plan snaps every ring point plus the anchor's ground
            snaps = 0;
            WorldEventRewardDelivery.BuildPlacementPlan(new Position(anchor), anchor, p => snaps++);
            Assert.AreEqual(RingCount + 1, snaps);
        }

        /// <summary>
        /// Review finding 4: two caches of one run that both reach the ethereal tail never share a point, even though
        /// their solid plans fall back to the same anchor and rings.
        /// </summary>
        [TestMethod]
        public void EtherealTail_TwoCacheIndices_ShareNoPoint()
        {
            var anchor = Anchor();
            var requested = new[] { 0, 1, 2, 9 }.Select(i =>
            {
                var p = new Position(anchor);
                p.SetPosition(anchor.Pos + new Vector3(3f * MathF.Cos(i), 3f * MathF.Sin(i), 0f));
                return p;
            }).ToList();

            var tails = new List<List<Attempt>>();

            foreach (var (index, ask) in new[] { (0, requested[0]), (1, requested[1]), (2, requested[2]), (9, requested[3]) })
                tails.Add(WorldEventRewardDelivery.BuildPlacementPlan(ask, anchor, FakeSnap, index).Where(a => a.Ethereal).ToList());

            Assert.IsTrue(tails.All(t => t.Count > 0));

            for (var a = 0; a < tails.Count; a++)
                for (var b = a + 1; b < tails.Count; b++)
                    foreach (var pa in tails[a])
                        foreach (var pb in tails[b])
                            Assert.IsTrue(Vector3.Distance(pa.Position.Pos, pb.Position.Pos) > 0.5f,
                                $"caches {a} and {b} share an ethereal point: {Describe(pa)} vs {Describe(pb)}");

            // cache 0 keeps the exact points (single-cache behaviour unchanged); later ones are offset and snapped
            Assert.AreEqual(AX, tails[0].Single(t => t.Kind == "anchor").Position.PositionX, 1e-4f);
            Assert.IsTrue(tails[1].All(t => t.Position.PositionZ == SnappedZ), "offset ethereal points are snapped outdoors");
            Assert.IsTrue(tails.SelectMany(t => t).All(t => t.Position.Cell >> 16 == anchor.Cell >> 16), "and stay on the anchor's landblock");
        }

        // ================= PrepareCache / StampPlacedCache (review finding 5) =================

        [TestMethod]
        public void PrepareCache_SetsEtherealAndACopiedLocation()
        {
            var at = Anchor();

            var solid = MakeCache();
            WorldEventRewardDelivery.PrepareCache(solid, at, false);
            Assert.AreNotEqual(true, solid.Ethereal);
            Assert.IsFalse(ReferenceEquals(at, solid.Location), "a copy: physics placement mutates Location");
            Assert.AreEqual(AX, solid.Location.PositionX, 1e-4f);

            var ethereal = MakeCache();
            WorldEventRewardDelivery.PrepareCache(ethereal, at, true);
            Assert.AreEqual(true, ethereal.Ethereal);
            Assert.AreEqual(true, ethereal.GetProperty(ACE.Entity.Enum.Properties.PropertyBool.Ethereal),
                "the property InitPhysicsObj builds the physics state from");
        }

        [TestMethod]
        public void TryEnterCache_PreparesBeforeItEnters()
        {
            var wo = MakeCache();
            bool? etherealAtEnter = null;
            Position locationAtEnter = null;

            var ok = WorldEventRewardDelivery.TryEnterCache(wo, Anchor(), true, w => { etherealAtEnter = w.Ethereal; locationAtEnter = w.Location; return true; });

            Assert.IsTrue(ok);
            Assert.AreEqual(true, etherealAtEnter, "ethereal must already be set when the enter step runs");
            Assert.IsNotNull(locationAtEnter);
        }

        [TestMethod]
        public void StampPlacedCache_SetsTimeToRotAndTheRunStamp()
        {
            var wo = MakeCache();

            WorldEventRewardDelivery.StampPlacedCache(wo, 77);

            Assert.AreEqual(-1.0, wo.TimeToRot ?? 0);
            Assert.AreEqual(77, wo.GetProperty(ACE.Entity.Enum.Properties.PropertyInt.WorldEventId));
        }

        private static uint nextGuid = 0x7D2E0001;

        private static ACE.Server.WorldObjects.GenericObject MakeCache()
        {
            var weenie = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 999849,
                ClassName = "cacheplacementplanshape",
                WeenieType = ACE.Entity.Enum.WeenieType.Generic,
                PropertiesString = new Dictionary<ACE.Entity.Enum.Properties.PropertyString, string> { { ACE.Entity.Enum.Properties.PropertyString.Name, "Plan Test Cache" } },
                PropertiesInt = new Dictionary<ACE.Entity.Enum.Properties.PropertyInt, int> { { ACE.Entity.Enum.Properties.PropertyInt.PhysicsState, 1048 } },
            };

            return new ACE.Server.WorldObjects.GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static string Describe(Attempt a) => $"{a.Kind} eth={a.Ethereal} {a.Position.ToLOCString()}";
    }
}
