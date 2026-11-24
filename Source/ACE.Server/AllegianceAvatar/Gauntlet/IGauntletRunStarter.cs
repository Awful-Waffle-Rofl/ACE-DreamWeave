using System;

using ACE.Entity;

namespace ACE.Server.Avatars.Gauntlet
{
    /// <summary>
    /// The seam between the Gauntlet purchase (plan Task 10, GauntletPurchase) and the run manager
    /// (plan Task 7, GauntletManager). The purchase owns rights, level and Favor checks and the Favor
    /// reservation; the manager owns every check that needs run state, and the run itself.
    /// GauntletManager sets <see cref="GauntletRunStarter.Current"/> at startup; tests install a fake.
    ///
    /// THREADING: both methods are THREAD-SAFE and are called synchronously from the vendor buy path (a landblock
    /// thread). GauntletManager takes its registry lock in each and does no world work in TryStart: it only registers
    /// the run in Purchased, and the copy is loaded by the next world tick.
    ///
    /// EXCEPTIONS: TryStart never throws. A fault inside it undoes its own registration and returns false, so the
    /// reservation is released exactly once, by the caller (a throw is still treated as false by GauntletPurchase).
    /// </summary>
    public interface IGauntletRunStarter
    {
        /// <summary>
        /// Checks that depend on manager state, made before any Favor moves: a run already live for this
        /// Avatar, the server-wide concurrent cap, the per-Avatar cooldown, no arena site configured.
        /// Returns null when a run may start, otherwise the ASCII refusal line to show the buyer.
        /// </summary>
        string CheckCanStart(uint avatarId, DateTime nowUtc);

        /// <summary>
        /// Starts a run. The purchase has ALREADY reserved <see cref="GauntletStartRequest.Price"/> Favor
        /// (AvatarManager.TryReserveFavor). From a true return the run owns that reservation: it calls
        /// AvatarManager.CommitDebit when wave 1 spawns and AvatarManager.ReleaseReservation on any end before
        /// that. On a false return nothing was started and the CALLER releases the reservation.
        /// </summary>
        bool TryStart(GauntletStartRequest request, out string refusal);
    }

    /// <summary>What the purchase hands the manager to start a run.</summary>
    public sealed class GauntletStartRequest
    {
        public uint AvatarId { get; init; }
        public uint? MonarchGuid { get; init; }
        public uint BuyerGuid { get; init; }
        /// <summary>Where the gate is placed: the selling NPC's location (summoned or seated).</summary>
        public Position NpcLocation { get; init; }
        public ulong Price { get; init; }
        public DateTime NowUtc { get; init; }
    }

    /// <summary>Holder for the live starter. Null until GauntletManager initializes; the purchase refuses while null.</summary>
    public static class GauntletRunStarter
    {
        public static IGauntletRunStarter Current { get; set; }
    }
}
