using ACE.Server.Entity;
using ACE.Server.Physics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Physics
{
    /// <summary>
    /// One-way player visibility (stage, PvP battleground bg_003c, 2026-10-16): dungeon VisibleCells are not always
    /// symmetric, so one player (Kestrel) can lose sight of another (Roz) while Roz's cell still sees Kestrel's.
    /// Kestrel's destruction queue then expires Roz. Before the fix that expiry ran RemoveObject, which dropped the
    /// pair from BOTH KnownPlayers tables while Roz still knew Kestrel: Kestrel's broadcasts (attacks, death,
    /// position) stopped reaching a client that still drew him, and nothing on Roz's side ever repaired it.
    ///
    /// Bare PhysicsObjs (no CurLandblock/CurCell) resolve CurInstance to KnownInstance, the same seam
    /// ObjectMaintInstanceTests uses, so no dat data or live server is needed. The destruction queue is keyed on
    /// PhysicsTimer.CurrentTime, which reads Timers.PortalYearTicks; every test that moves it restores it.
    /// </summary>
    [TestClass]
    public class ObjectMaintOneWayVisibilityTests
    {
        private const uint MatchInstance = 0x800165AE;
        private const uint OtherInstance = 0x800165AF;

        // both in the player guid range 0x50000001 - 0x5FFFFFFF (PhysicsObj.IsPlayer)
        private const uint RozId = 0x50000045;
        private const uint KestrelId = 0x5A000100;

        private static PhysicsObj MakePlayer(uint id, uint instance)
        {
            return new PhysicsObj { ID = id, KnownInstance = instance };
        }

        private static bool KnowsPlayer(PhysicsObj owner, PhysicsObj player)
        {
            return owner.ObjMaint.GetKnownPlayersWhere(kvp => kvp.Key == player.ID).Count == 1;
        }

        /// <summary>Both players see each other, as the arrival pass leaves them.</summary>
        private static void MakeMutuallyVisible(PhysicsObj a, PhysicsObj b)
        {
            a.ObjMaint.AddVisibleObject(b);
            a.ObjMaint.AddKnownObject(b);
            b.ObjMaint.AddVisibleObject(a);
            b.ObjMaint.AddKnownObject(a);

            Assert.IsTrue(KnowsPlayer(a, b) && KnowsPlayer(b, a), "precondition: the pair starts fully linked");
        }

        /// <summary>Runs the real 25 s expiry of <paramref name="target"/> from <paramref name="owner"/>'s queue.</summary>
        private static void ExpireFromQueue(PhysicsObj owner, PhysicsObj target)
        {
            var savedTicks = Timers.PortalYearTicks;
            try
            {
                Timers.PortalYearTicks = 1000.0;
                owner.ObjMaint.AddObjectToBeDestroyed(target);

                Timers.PortalYearTicks = 1000.0 + 26.0;
                var expired = owner.ObjMaint.DestroyObjects();

                CollectionAssert.Contains(expired, target, "the queue entry must actually expire");
            }
            finally
            {
                Timers.PortalYearTicks = savedTicks;
            }
        }

        [TestMethod]
        public void Expiry_OfAPlayerThatStillKnowsTheOwner_KeepsBothBroadcastLinks()
        {
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            MakeMutuallyVisible(roz, kestrel);

            // Kestrel's cell stops seeing Roz's; Roz's still sees Kestrel's
            ExpireFromQueue(kestrel, roz);

            // Kestrel forgets Roz...
            Assert.IsFalse(kestrel.ObjMaint.KnownObjectsContainsKey(roz.ID));
            Assert.IsFalse(kestrel.ObjMaint.VisibleObjectsContainsKey(roz.ID));
            Assert.AreEqual(0, kestrel.ObjMaint.GetDestructionQueueCount());

            // ...but Roz still knows (and draws) Kestrel, so the broadcast links survive both ways
            Assert.IsTrue(roz.ObjMaint.KnownObjectsContainsKey(kestrel.ID));
            Assert.IsTrue(KnowsPlayer(kestrel, roz), "Kestrel's broadcasts must still reach Roz");
            Assert.IsTrue(KnowsPlayer(roz, kestrel), "Roz's broadcasts must still reach Kestrel");
        }

        [TestMethod]
        public void Expiry_OfAPlayerThatNoLongerKnowsTheOwner_RemovesItFully()
        {
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            MakeMutuallyVisible(roz, kestrel);

            // Roz has already forgotten Kestrel (her own expiry): nothing keeps the pair
            ExpireFromQueue(roz, kestrel);
            Assert.IsFalse(roz.ObjMaint.KnownObjectsContainsKey(kestrel.ID));

            ExpireFromQueue(kestrel, roz);

            Assert.IsFalse(kestrel.ObjMaint.KnownObjectsContainsKey(roz.ID));
            Assert.IsFalse(kestrel.ObjMaint.VisibleObjectsContainsKey(roz.ID));
            Assert.IsFalse(KnowsPlayer(kestrel, roz));
            Assert.IsFalse(KnowsPlayer(roz, kestrel));
        }

        [TestMethod]
        public void DestroyObject_AfterAOneWayExpiry_LeavesNoStaleBroadcastLink_DestroyingTheExpiredSide()
        {
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            MakeMutuallyVisible(roz, kestrel);
            ExpireFromQueue(kestrel, roz);

            roz.ObjMaint.DestroyObject();

            Assert.IsFalse(KnowsPlayer(kestrel, roz));
            Assert.IsFalse(kestrel.ObjMaint.KnownObjectsContainsKey(roz.ID));
            Assert.AreEqual(0, roz.ObjMaint.GetKnownPlayersCount());
            Assert.AreEqual(0, roz.ObjMaint.GetKnownObjectsCount());
        }

        [TestMethod]
        public void DestroyObject_AfterAOneWayExpiry_LeavesNoStaleBroadcastLink_DestroyingTheExpiringSide()
        {
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            MakeMutuallyVisible(roz, kestrel);
            ExpireFromQueue(kestrel, roz);

            kestrel.ObjMaint.DestroyObject();

            Assert.IsFalse(KnowsPlayer(roz, kestrel));
            Assert.IsFalse(roz.ObjMaint.KnownObjectsContainsKey(kestrel.ID));
            Assert.IsFalse(roz.ObjMaint.VisibleObjectsContainsKey(kestrel.ID));
            Assert.AreEqual(0, kestrel.ObjMaint.GetKnownPlayersCount());
        }

        [TestMethod]
        public void SelfHeal_RepairsAPreBrokenPair()
        {
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrel = MakePlayer(KestrelId, MatchInstance);
            MakeMutuallyVisible(roz, kestrel);

            // the pre-fix state: Kestrel's RemoveObject(Roz) cut both links while Roz still sees Kestrel
            kestrel.ObjMaint.RemoveObject(roz);
            Assert.IsTrue(roz.ObjMaint.VisibleObjectsContainsKey(kestrel.ID), "precondition: Roz still sees Kestrel");
            Assert.IsFalse(KnowsPlayer(kestrel, roz), "precondition: Kestrel's broadcasts no longer reach Roz");

            var repaired = roz.ObjMaint.RepairInverseKnownPlayers();

            Assert.AreEqual(1, repaired);
            Assert.IsTrue(KnowsPlayer(kestrel, roz), "Roz's heartbeat must put her back on Kestrel's broadcast list");

            // idempotent: a second heartbeat repairs nothing
            Assert.AreEqual(0, roz.ObjMaint.RepairInverseKnownPlayers());
        }

        [TestMethod]
        public void EnsureKnownPlayer_AcrossDifferentInstances_IsRefused()
        {
            var roz = MakePlayer(RozId, MatchInstance);
            var kestrelElsewhere = MakePlayer(KestrelId, OtherInstance);

            Assert.IsFalse(kestrelElsewhere.ObjMaint.EnsureKnownPlayer(roz));
            Assert.IsFalse(KnowsPlayer(kestrelElsewhere, roz));

            // and the same-instance control, so the refusal above is the instance gate and not a dead method
            var kestrelHere = MakePlayer(KestrelId, MatchInstance);
            Assert.IsTrue(kestrelHere.ObjMaint.EnsureKnownPlayer(roz));
            Assert.IsTrue(KnowsPlayer(kestrelHere, roz));
        }
    }
}
