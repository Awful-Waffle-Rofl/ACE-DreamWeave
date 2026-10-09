using ACE.Entity;
using ACE.Server.WorldObjects;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Physics
{
    /// <summary>
    /// A landblock and its per-realm instance copies (e.g. the realm-0 "Town Network" and the
    /// realm-1 "Drift Network" views of landblock 0x0007) share 32-bit object guids AND client-side
    /// cell ids. When a player leaves one such instance, the client must be told to delete that
    /// instance's objects (GameMessageDeleteObject). If it is not, re-entering a *different* instance
    /// of the same landblock id later re-renders the old instance's objects as un-interactable ghosts:
    /// the client keeps object data keyed by guid+cell, and the server - which has no counterpart
    /// object in the new instance - never sends a delete.
    ///
    /// The delete is driven by <see cref="Player.FlushKnownObjectsForInstanceChange"/>, gated by
    /// <see cref="Player.TeleportRequiresClientObjectFlush"/>. The regression these tests pin: the old
    /// gate compared CurrentLandblock.Instance to the destination instance. For a command-initiated
    /// teleport (lifestone / Drift Network recall, /teleto) UpdatePlayerPosition relocates
    /// CurrentLandblock to the destination *before* the gate runs (Player.InUpdate == false), so both
    /// sides of that compare were the destination and the flush was skipped - leaving the ghosts.
    /// The fix decides from the pre-teleport origin instead.
    /// </summary>
    [TestClass]
    public class TeleportInstanceFlushTests
    {
        // [1 bit ephemeral][15 bits realmId][16 bits shortInstanceId]
        private static readonly uint Realm0 = Position.InstanceIDFromVars(0, 0, false); // base world
        private static readonly uint Realm1 = Position.InstanceIDFromVars(1, 0, false); // e.g. Drift / Weave

        // landblock 0x0007 = the Town Network hub; the realm-1 override rows place class trainers here
        private const uint TownNetworkPortalCell = 0x00070147; // base (realm-0) content
        private const uint DriftTrainerCell = 0x00070135;      // realm-1 override content
        // an unrelated landblock the player passes through (a lifestone)
        private const uint LifestoneCell = 0x00160142;

        private static ulong InstancedLandblock(uint cell, uint instance)
        {
            return new Position(cell, 0, 0, 0, 0, 0, 0, 1, instance).InstancedLandblock;
        }

        [TestMethod]
        public void LeavingRealmInstance_ToDifferentLandblock_RequiresFlush()
        {
            // exact leak repro: standing among the realm-1 trainers in Drift Network (0x0007),
            // the player /ls recalls to a lifestone in another landblock. The trainers must be
            // deleted on the client as the player leaves, or they ghost into the realm-0 Town
            // Network on the next visit. The intermediate landblock happens to be in the same
            // realm (home realm), so an "instance changed?" gate alone would miss this - the
            // landblock id is what changed.
            var origin = InstancedLandblock(DriftTrainerCell, Realm1);
            var destination = InstancedLandblock(LifestoneCell, Realm1);

            Assert.IsTrue(Player.TeleportRequiresClientObjectFlush(origin, destination));
        }

        [TestMethod]
        public void CrossInstance_SameLandblockId_RequiresFlush()
        {
            // the direct blend: realm-1 view of 0x0007 -> realm-0 view of the same landblock id.
            // Same client landblock/cell ids, different server instance => must flush.
            var origin = InstancedLandblock(DriftTrainerCell, Realm1);
            var destination = InstancedLandblock(TownNetworkPortalCell, Realm0);

            Assert.IsTrue(Player.TeleportRequiresClientObjectFlush(origin, destination));
        }

        [TestMethod]
        public void SameInstancedLandblock_DoesNotFlush()
        {
            // a teleport that stays within the same server-side landblock instance (e.g. a short
            // hop) must NOT flush - those objects remain visible and re-creating them would flicker.
            var location = InstancedLandblock(TownNetworkPortalCell, Realm0);

            Assert.IsFalse(Player.TeleportRequiresClientObjectFlush(location, location));
        }

        [TestMethod]
        public void CommandTeleport_PostRelocationCompare_DemonstratesTheOldSkip()
        {
            // Why the old gate failed for command teleports: by the time it ran, CurrentLandblock
            // had already been relocated to the destination, so it compared the destination instance
            // against the (destination) Location instance - equal - and skipped the flush.
            var origin = InstancedLandblock(DriftTrainerCell, Realm1);
            var destination = InstancedLandblock(LifestoneCell, Realm1);

            // the buggy, post-relocation comparison (destination vs destination) sees no change...
            Assert.IsFalse(Player.TeleportRequiresClientObjectFlush(destination, destination),
                "post-relocation compare is the bug: it never triggers the flush");

            // ...while the correct origin-vs-destination comparison does.
            Assert.IsTrue(Player.TeleportRequiresClientObjectFlush(origin, destination),
                "origin-vs-destination is the fix: it triggers the flush that deletes the ghosts");
        }
    }
}
