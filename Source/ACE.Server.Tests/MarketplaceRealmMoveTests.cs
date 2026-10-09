using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Realms;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure pieces of the Marketplace move into realm 1's copy of Aerfalle Keep (landblock 0x01F5):
    /// the same-landblock realm hop predicate (InstanceRouting), the realm-aware mule_bind_position parser
    /// (RealmLocString) and the fail-closed Marketplace recall resolver (MarketplaceRecall). The realm registry
    /// is injected everywhere, so no RealmManager, world database or PropertyManager state is read.
    /// </summary>
    [TestClass]
    public class MarketplaceRealmMoveTests
    {
        private static readonly WorldRealm Realm1 = new WorldRealm(1, "Weave Content 1", RealmType.Realm);

        private static WorldRealm OnlyRealm1(ushort id) => id == 1 ? Realm1 : (id == 0 ? new WorldRealm(0, "Base World", RealmType.Realm) : null);

        private static WorldRealm NoRealms(ushort id) => id == 0 ? new WorldRealm(0, "Base World", RealmType.Realm) : null;

        private static uint Persistent(ushort realm) => Position.InstanceIDFromVars(realm, 0, false);

        private static uint Ephemeral(ushort realm, ushort shortId = 5) => Position.InstanceIDFromVars(realm, shortId, true);

        private static Position At(uint cell, uint instance) => new Position(cell, 10f, 10f, 0f, 0f, 0f, 0f, 1f, instance);

        // ================= same-landblock realm hop predicate =================

        /// <summary>The case the guard exists for: realm 0's Aerfalle Keep to realm 1's Marketplace, same landblock.</summary>
        [TestMethod]
        public void Hop_PersistentToPersistent_SameLandblock_DifferentRealm_IsHop()
        {
            Assert.IsTrue(InstanceRouting.IsPersistentSameLandblockRealmHop(At(0x01F50229, Persistent(0)), At(0x01F50262, Persistent(1))));
        }

        [TestMethod]
        public void Hop_SameInstance_IsNotHop()
        {
            Assert.IsFalse(InstanceRouting.IsPersistentSameLandblockRealmHop(At(0x01F50229, Persistent(1)), At(0x01F50262, Persistent(1))));
        }

        [TestMethod]
        public void Hop_DifferentLandblock_IsNotHop()
        {
            Assert.IsFalse(InstanceRouting.IsPersistentSameLandblockRealmHop(At(0x00070145, Persistent(0)), At(0x01F50262, Persistent(1))));
        }

        /// <summary>
        /// An ephemeral end is not a persistent hop: instanced portals legitimately move a player between the
        /// hub and a private copy of the landblock they stand in. The any-instance form (used by portal gems)
        /// still reports it.
        /// </summary>
        [TestMethod]
        public void Hop_EphemeralEnd_NotPersistentHop_ButAnyInstanceHop()
        {
            var hub = At(0x01F50229, Persistent(1));
            var privateCopy = At(0x01F50229, Ephemeral(1));

            Assert.IsFalse(InstanceRouting.IsPersistentSameLandblockRealmHop(hub, privateCopy));
            Assert.IsFalse(InstanceRouting.IsPersistentSameLandblockRealmHop(privateCopy, hub));
            Assert.IsTrue(InstanceRouting.IsSameLandblockCrossInstanceHop(hub, privateCopy));
        }

        [TestMethod]
        public void Hop_NullEnds_AreNotHops()
        {
            Assert.IsFalse(InstanceRouting.IsPersistentSameLandblockRealmHop(null, At(0x01F50262, Persistent(1))));
            Assert.IsFalse(InstanceRouting.IsSameLandblockCrossInstanceHop(At(0x01F50262, Persistent(1)), null));
        }

        // ================= mule_bind_position (RealmLocString) =================

        private const string NewDefault = "0x01F50262 39.971748 -118.261452 6.005000 0.663589 0.000000 0.000000 0.748097 @1";
        private const string OldDefault = "0x016C019E 28.850000 -40.738000 0.005000 -0.394134 0.000000 0.000000 -0.919053";

        /// <summary>
        /// With @1 the bind lands in realm 1's default instance whatever instance the converting player stands
        /// in (here realm 0). Before the token existed the player's own instance was used, which is the realm-0
        /// dungeon - so this fails against the old parse (which also rejected nothing and produced instance 0).
        /// </summary>
        [TestMethod]
        public void MuleBind_WithRealmToken_BindsToThatRealmsDefaultInstance()
        {
            Assert.IsTrue(RealmLocString.TryParse(NewDefault, Persistent(0), OnlyRealm1, out var pos, out var error, out var notRegistered), error);

            Assert.IsFalse(notRegistered);
            Assert.AreEqual(0x01F50262u, pos.Cell);
            Assert.AreEqual(Realm1.DefaultInstanceID, pos.Instance);
            Assert.AreEqual(39.971748f, pos.PositionX, 1e-5f);
            Assert.AreEqual(6.005f, pos.PositionZ, 1e-5f);
            Assert.AreEqual(0.663589f, pos.RotationW, 1e-5f);
            Assert.AreEqual(0.748097f, pos.RotationZ, 1e-5f);
        }

        /// <summary>Back-compat: no token keeps today's behaviour, the caller-supplied (current) instance.</summary>
        [TestMethod]
        public void MuleBind_WithoutRealmToken_KeepsCallerInstance()
        {
            var current = Persistent(3);

            Assert.IsTrue(RealmLocString.TryParse(OldDefault, current, NoRealms, out var pos, out var error, out _), error);
            Assert.AreEqual(current, pos.Instance);
            Assert.AreEqual(0x016C019Eu, pos.Cell);
        }

        /// <summary>An unregistered realm skips the bind (false) and says why, rather than binding elsewhere.</summary>
        [TestMethod]
        public void MuleBind_UnregisteredRealm_Refused()
        {
            Assert.IsFalse(RealmLocString.TryParse(NewDefault, Persistent(0), NoRealms, out var pos, out var error, out var notRegistered));
            Assert.IsNull(pos);
            Assert.IsTrue(notRegistered);
            StringAssert.Contains(error, "realm 1");
        }

        [TestMethod]
        public void MuleBind_MalformedRealmToken_Refused()
        {
            Assert.IsFalse(RealmLocString.TryParse("0x01F50262 1 2 3 @x", 0, OnlyRealm1, out _, out _, out var notRegistered));
            Assert.IsFalse(notRegistered);
            Assert.IsFalse(RealmLocString.TryParse("@1", 0, OnlyRealm1, out _, out _, out _));
            Assert.IsFalse(RealmLocString.TryParse("", 0, OnlyRealm1, out _, out _, out _));
        }

        /// <summary>
        /// Control: the shipped default of mule_bind_position is the new realm-1 Marketplace lifestone. Read from
        /// DefaultStringProperties, not GetString, so no PropertyManager cache state is involved.
        /// </summary>
        [TestMethod]
        public void MuleBind_ShippedDefault_IsTheRealm1MarketplaceLifestone()
        {
            Assert.AreEqual(NewDefault, ACE.Server.Managers.DefaultPropertyManager.DefaultStringProperties["mule_bind_position"].Item);
        }

        // ================= Marketplace recall resolver =================

        /// <summary>
        /// Portal weenie routed to realm 1: the drop is its Destination in realm 1's default instance. The old
        /// static always produced instance 0 (Weenie.GetPosition hardcodes it), i.e. realm 0's 0x01F5 - the
        /// monster dungeon - so this fails against the unfixed behaviour.
        /// </summary>
        [TestMethod]
        public void Recall_WeenieWithPortalRealm_BindsToThatRealm()
        {
            var dest = At(0x01F50229, 0);

            Assert.IsTrue(MarketplaceRecall.TryResolve(true, dest, 1, OnlyRealm1, out var drop, out var failure), failure);
            Assert.AreEqual(Realm1.DefaultInstanceID, drop.Instance);
            Assert.AreEqual(0x01F50229u, drop.Cell);
            Assert.AreEqual(0u, dest.Instance, "the resolver must not mutate the weenie's position");
        }

        /// <summary>No portal weenie at all: the realm-1 fallback constant, never realm 0.</summary>
        [TestMethod]
        public void Recall_NoWeenie_UsesRealm1Fallback()
        {
            Assert.IsTrue(MarketplaceRecall.TryResolve(false, null, null, OnlyRealm1, out var drop, out var failure), failure);
            AssertIsMarketplaceLanding(drop);
            Assert.AreEqual(Realm1.DefaultInstanceID, drop.Instance);
        }

        /// <summary>
        /// Fails closed: a weenie with no PortalRealm (the pre-move content, still pointing at realm 0) is refused
        /// rather than guessed. The old code teleported to it in realm 0.
        /// </summary>
        [TestMethod]
        public void Recall_WeenieWithoutPortalRealm_Refused()
        {
            Assert.IsFalse(MarketplaceRecall.TryResolve(true, At(0x016C01BC, 0), null, OnlyRealm1, out var drop, out var failure));
            Assert.IsNull(drop);
            StringAssert.Contains(failure, "PortalRealm");
        }

        /// <summary>Fails closed: realm 1 not registered on this world - refused, never realm 0.</summary>
        [TestMethod]
        public void Recall_UnregisteredRealm_Refused()
        {
            Assert.IsFalse(MarketplaceRecall.TryResolve(true, At(0x01F50229, 0), 1, NoRealms, out var drop, out _));
            Assert.IsNull(drop);

            Assert.IsFalse(MarketplaceRecall.TryResolve(false, null, null, NoRealms, out drop, out _), "the fallback is realm 1 too, so it must fail closed as well");
            Assert.IsNull(drop);
        }

        /// <summary>
        /// Fails closed: an explicit PortalRealm = 0 on the portal weenie is refused. Realm 0 is always
        /// registered, so without this the recall would resolve into realm 0's 0x01F5 - the hostile retail
        /// Aerfalle Keep dungeon, never the Marketplace.
        /// </summary>
        [TestMethod]
        public void Recall_WeenieWithPortalRealmZero_Refused()
        {
            Assert.IsFalse(MarketplaceRecall.TryResolve(true, At(0x01F50229, 0), 0, OnlyRealm1, out var drop, out var failure));
            Assert.IsNull(drop);
            StringAssert.Contains(failure, "realm 0");
        }

        [TestMethod]
        public void Recall_InvalidRealmId_Refused()
        {
            Assert.IsFalse(MarketplaceRecall.TryResolve(true, At(0x01F50229, 0), -1, OnlyRealm1, out _, out _));
            Assert.IsFalse(MarketplaceRecall.TryResolve(true, At(0x01F50229, 0), 0x8000, OnlyRealm1, out _, out _));
        }

        /// <summary>A weenie with PortalRealm but no Destination row uses the fallback coordinates in the weenie's realm.</summary>
        [TestMethod]
        public void Recall_WeenieWithRealmButNoDestination_UsesFallbackCoordinates()
        {
            Assert.IsTrue(MarketplaceRecall.TryResolve(true, null, 1, OnlyRealm1, out var drop, out var failure), failure);
            AssertIsMarketplaceLanding(drop);
            Assert.AreEqual(Realm1.DefaultInstanceID, drop.Instance);
        }

        /// <summary>
        /// The fallback drop is the owner-chosen Marketplace landing, 0x01F50224 [50 -132.618988 0.005] facing 1 0 0 0
        /// (read with /loc in realm 1, 2026-09-28), not the old Aerfalle Keep entry point 0x01F50229 [50 -180].
        /// </summary>
        private static void AssertIsMarketplaceLanding(Position drop)
        {
            Assert.AreEqual(0x01F50224u, drop.Cell);
            Assert.AreEqual(50f, drop.PositionX, 1e-4f);
            Assert.AreEqual(-132.618988f, drop.PositionY, 1e-4f);
            Assert.AreEqual(0.005f, drop.PositionZ, 1e-4f);
            Assert.AreEqual(1f, drop.RotationW, 1e-6f);
            Assert.AreEqual(0f, drop.RotationZ, 1e-6f);
        }
    }
}
