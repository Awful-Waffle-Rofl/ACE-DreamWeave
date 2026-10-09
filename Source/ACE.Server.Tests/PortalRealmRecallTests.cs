using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Realms;
using ACE.Server.WorldObjects;

using RuntimeBiota = ACE.Entity.Models.Biota;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the standing rule that realm-routed and instanced portals are never recallable, tie-able or
    /// summonable from a tie, whatever their PortalBitmask says. The regression: the D3 entry portal
    /// (1001109 Held Door) carries PortalRealm = 1 with PortalBitmask Unrestricted, so players could tie
    /// and recall straight into it. Portal Tie, Portal Recall and Summon Portal all read NoTie / NoRecall /
    /// NoSummon off a Portal rebuilt from the weenie, so pinning the getters pins all three spell paths.
    ///
    /// Plus the one OPT-IN exception (owner decision 2026-09-26, the Marketplace move into realm 1): a wcid in
    /// realm_portal_recall_allowlist that is routed ONLY by a persistent PortalRealm is not locked.
    ///
    /// No PropertyManager key is read: RealmPortalRecallTunables.AllowlistSource is pinned to the shipped
    /// Defaults (23032, 1001007) for every test and restored afterwards.
    /// </summary>
    [TestClass]
    public class PortalRealmRecallTests
    {
        private const uint MarketplacePortal = 23032;
        private const uint DriftMarketplaceReturn = 1001007;
        private const uint HeldDoor = 1001109;

        private static uint nextId = 0x7F300000;

        private Func<IReadOnlySet<uint>> savedAllowlistSource;

        [TestInitialize]
        public void PinShippedAllowlist()
        {
            savedAllowlistSource = RealmPortalRecallTunables.AllowlistSource;
            RealmPortalRecallTunables.AllowlistSource = () => RealmPortalRecallTunables.Defaults;
        }

        [TestCleanup]
        public void RestoreAllowlistSource()
        {
            RealmPortalRecallTunables.AllowlistSource = savedAllowlistSource;
        }

        private static Portal CreatePortal(PortalBitmask bitmask, uint wcid = 1)
        {
            var biota = new RuntimeBiota
            {
                Id = ++nextId,
                WeenieClassId = wcid,
                WeenieType = WeenieType.Portal,
            };

            var portal = new Portal(biota);
            portal.SetProperty(PropertyInt.PortalBitmask, (int)bitmask);
            return portal;
        }

        private static void AssertLocked(Portal portal, string what)
        {
            Assert.IsTrue(portal.IsRealmRouted, $"{what}: not treated as realm-routed.");
            Assert.IsTrue(portal.IsRecallLockedByRealmRouting, $"{what}: not lock-listed.");
            Assert.IsTrue(portal.NoRecall, $"{what}: Portal Recall would accept it.");
            Assert.IsTrue(portal.NoTie, $"{what}: Portal Tie would accept it.");
            Assert.IsTrue(portal.NoSummon, $"{what}: a tie stored before the rule could still be summoned.");
        }

        private static void AssertRecallable(Portal portal, string what)
        {
            Assert.IsTrue(portal.IsRealmRouted, $"{what}: should still be DESCRIBED as realm-routed.");
            Assert.IsFalse(portal.IsRecallLockedByRealmRouting, $"{what}: an allowlisted persistent-realm portal must not be lock-listed.");
            Assert.IsFalse(portal.NoRecall, $"{what}: Portal Recall should accept it.");
            Assert.IsFalse(portal.NoTie, $"{what}: Portal Tie should accept it.");
            Assert.IsFalse(portal.NoSummon, $"{what}: Summon Portal should accept it.");
        }

        [TestMethod]
        public void UnrestrictedPortal_WithoutRealmRouting_StaysRecallable()
        {
            // control: the ordinary case must still be accepted, or the locked cases below prove nothing
            var portal = CreatePortal(PortalBitmask.Unrestricted);

            Assert.IsFalse(portal.IsRealmRouted);
            Assert.IsFalse(portal.NoRecall);
            Assert.IsFalse(portal.NoTie);
            Assert.IsFalse(portal.NoSummon);
        }

        [TestMethod]
        public void PortalRealm_LocksAnUnrestrictedPortal()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted);
            portal.SetProperty(PropertyInt.PortalRealm, 1);

            AssertLocked(portal, "PortalRealm = 1");
        }

        [TestMethod]
        public void PortalRealmZero_StillCountsAsRealmRouted()
        {
            // an explicit realm override is routing even when it names the base realm
            var portal = CreatePortal(PortalBitmask.Unrestricted);
            portal.SetProperty(PropertyInt.PortalRealm, 0);

            AssertLocked(portal, "PortalRealm = 0");
        }

        // ================= realm_portal_recall_allowlist (opt-in, 2026-09-26) =================

        /// <summary>
        /// The Marketplace portal, routed into realm 1 by PortalRealm alone, is recallable / tie-able /
        /// summonable because its wcid is in the shipped allowlist. Fails against the blanket lock.
        /// </summary>
        [TestMethod]
        public void AllowlistedPersistentRealmPortal_IsRecallable()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted, MarketplacePortal);
            portal.SetProperty(PropertyInt.PortalRealm, 1);

            AssertRecallable(portal, "23032 + PortalRealm = 1");

            var driftReturn = CreatePortal(PortalBitmask.Unrestricted, DriftMarketplaceReturn);
            driftReturn.SetProperty(PropertyInt.PortalRealm, 0);

            AssertRecallable(driftReturn, "1001007 + PortalRealm = 0");
        }

        /// <summary>
        /// The Held Door shape (1001109: PortalRealm = 1, Unrestricted) is NOT allowlisted and stays locked.
        /// Fails against the blanket persistent-realm exemption this replaced.
        /// </summary>
        [TestMethod]
        public void NonAllowlistedPersistentRealmPortal_HeldDoorShape_StaysLocked()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted, HeldDoor);
            portal.SetProperty(PropertyInt.PortalRealm, 1);

            AssertLocked(portal, "1001109 + PortalRealm = 1");
        }

        /// <summary>An allowlisted wcid that is instance-routed is still locked: the exemption is persistent-only.</summary>
        [TestMethod]
        public void AllowlistedButInstanced_StaysLocked()
        {
            var instanced = CreatePortal(PortalBitmask.Unrestricted, MarketplacePortal);
            instanced.SetProperty(PropertyInt.PortalRealm, 1);
            instanced.SetProperty(PropertyInt.PortalInstancing, 1);

            AssertLocked(instanced, "23032 + PortalRealm = 1 + PortalInstancing = 1");

            var exit = CreatePortal(PortalBitmask.Unrestricted, MarketplacePortal);
            exit.SetProperty(PropertyInt.PortalRealm, 1);
            exit.SetProperty(PropertyInt.PortalExitInstance, 1);

            AssertLocked(exit, "23032 + PortalRealm = 1 + PortalExitInstance = 1");
        }

        /// <summary>An allowlisted wcid with instancing but NO PortalRealm is locked too (9022 is required).</summary>
        [TestMethod]
        public void AllowlistedInstancedWithoutPortalRealm_StaysLocked()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted, MarketplacePortal);
            portal.SetProperty(PropertyInt.PortalInstancing, 1);

            AssertLocked(portal, "23032 + PortalInstancing = 1");
        }

        /// <summary>A summoned gateway is judged by its OriginalPortal, not by the gateway weenie's own wcid.</summary>
        [TestMethod]
        public void SummonedGateway_UsesOriginalPortalWcid()
        {
            var gateway = CreatePortal(PortalBitmask.Unrestricted, wcid: 2);
            gateway.SetProperty(PropertyInt.PortalRealm, 1);
            gateway.OriginalPortal = MarketplacePortal;

            Assert.IsFalse(gateway.IsRecallLockedByRealmRouting);

            gateway.OriginalPortal = HeldDoor;
            Assert.IsTrue(gateway.IsRecallLockedByRealmRouting);
        }

        /// <summary>An emptied allowlist restores the full lock for the Marketplace portal too.</summary>
        [TestMethod]
        public void EmptyAllowlist_LocksEverything()
        {
            RealmPortalRecallTunables.AllowlistSource = () => RealmPortalRecallTunables.Parse("");

            var portal = CreatePortal(PortalBitmask.Unrestricted, MarketplacePortal);
            portal.SetProperty(PropertyInt.PortalRealm, 1);

            AssertLocked(portal, "23032 with an empty allowlist");
        }

        [TestMethod]
        public void Allowlist_ShippedDefault_AndLenientParse()
        {
            Assert.AreEqual(RealmPortalRecallTunables.DefaultAllowlist,
                ACE.Server.Managers.DefaultPropertyManager.DefaultStringProperties[RealmPortalRecallTunables.AllowlistConfigKey].Item);

            CollectionAssert.AreEquivalent(new uint[] { 23032, 1001007 }, new List<uint>(RealmPortalRecallTunables.Defaults));

            var rejected = new List<string>();
            var parsed = RealmPortalRecallTunables.Parse(" 23032, ,0x59F8,abc,0,-5,", rejected);

            CollectionAssert.AreEquivalent(new uint[] { 23032 }, new List<uint>(parsed), "0x59F8 is 23032 in hex");
            CollectionAssert.AreEquivalent(new[] { "abc", "0", "-5" }, rejected);
        }

        /// <summary>A persistent-realm portal that must stay unrecallable still can, with the ordinary bitmask bits.</summary>
        [TestMethod]
        public void AllowlistedWithNoRecallBit_StaysLocked()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted | PortalBitmask.NoRecall | PortalBitmask.NoSummon, MarketplacePortal);
            portal.SetProperty(PropertyInt.PortalRealm, 1);

            Assert.IsTrue(portal.NoRecall);
            Assert.IsTrue(portal.NoTie);
            Assert.IsTrue(portal.NoSummon);
        }

        /// <summary>
        /// Tie / recall re-resolve from a portal rebuilt from the weenie, so a live-only PortalRealm (the
        /// /portal-realm admin override) must be detected as a mismatch and not remembered - otherwise the
        /// recall would land in a different realm copy than the portal did.
        /// </summary>
        [TestMethod]
        public void SamePortalRealm_DetectsALiveOnlyOverride()
        {
            var weenieBuilt = CreatePortal(PortalBitmask.Unrestricted);
            var live = CreatePortal(PortalBitmask.Unrestricted);

            Assert.IsTrue(Portal.SamePortalRealm(live, weenieBuilt), "neither carries PortalRealm");

            live.SetProperty(PropertyInt.PortalRealm, 1);
            Assert.IsFalse(Portal.SamePortalRealm(live, weenieBuilt), "live-only override");

            weenieBuilt.SetProperty(PropertyInt.PortalRealm, 1);
            Assert.IsTrue(Portal.SamePortalRealm(live, weenieBuilt), "both from the weenie");
        }

        [TestMethod]
        public void PortalInstancing_LocksAnUnrestrictedPortal()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted);
            portal.SetProperty(PropertyInt.PortalInstancing, 1);

            AssertLocked(portal, "PortalInstancing = 1");
        }

        [TestMethod]
        public void PortalExitInstance_LocksAnUnrestrictedPortal()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted);
            portal.SetProperty(PropertyInt.PortalExitInstance, 1);

            AssertLocked(portal, "PortalExitInstance = 1");
        }

        [TestMethod]
        public void PortalInstancingZero_DoesNotLock()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted);
            portal.SetProperty(PropertyInt.PortalInstancing, 0);

            Assert.IsFalse(portal.NoRecall);
        }

        [TestMethod]
        public void BitmaskNoRecall_StillLocksWithoutRealmRouting()
        {
            var portal = CreatePortal(PortalBitmask.Unrestricted | PortalBitmask.NoRecall);

            Assert.IsFalse(portal.IsRealmRouted);
            Assert.IsTrue(portal.NoRecall);
            Assert.IsTrue(portal.NoTie);
        }
    }
}
