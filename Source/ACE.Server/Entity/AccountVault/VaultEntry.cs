using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// How much of a store one actor may do (DESIGN section 10).
    ///
    /// Deliberately ordered so that a numeric comparison is never needed: every call site tests for an
    /// exact member. None is the default so that a failure to resolve authorization denies rather than
    /// grants.
    /// </summary>
    public enum VaultAccess
    {
        None = 0,
        Deposit = 1,
        DepositWithdraw = 2,
    }

    /// <summary>
    /// Which end of a group row's <see cref="VaultEntry.Members"/> list a partial withdraw takes from.
    ///
    /// <see cref="Front"/> is the default and is the panel's documented behaviour (see
    /// <see cref="VaultEntry.Members"/>): the seller's own withdraw takes members 1..k, so the same
    /// action twice does the same thing.
    ///
    /// <see cref="Back"/> exists for ONE caller, the market sale, and the reason is an anchor rather
    /// than a preference. A group listing pins market_listing.item_Guid to the group's representative,
    /// which is Members[0]; taking from the front would sell the anchor out from under a listing that
    /// still has members left, leaving an Active row naming a guid the seller no longer holds. Taking
    /// from the back removes the representative only on the sale that empties the group, and that sale
    /// drives the listing's remaining count to zero, where the listing closes and both active keys are
    /// cleared anyway.
    /// </summary>
    public enum GroupTakeOrder
    {
        Front = 0,
        Back = 1,
    }

    /// <summary>
    /// One displayable line in a store: either a stored biota or a collapsed ledger row.
    ///
    /// This is a projection, not a handle. It is built fresh by every
    /// <see cref="AccountVaultStore.GetEntries(int, int)"/> call and holds no authority - a withdraw
    /// re-resolves the guid against the store's live vaults, so an entry captured before another
    /// window emptied the vault fails cleanly instead of transacting against a stale view.
    ///
    /// When <see cref="IsLedger"/> is true there is no biota behind the line at all: <see cref="Guid"/>
    /// is <see cref="ObjectGuid.Invalid"/> and <see cref="WorldObject"/> is null. Materializing a
    /// display object for those lines belongs to the vendor view, NOT to the store - the store must
    /// stay free of any vendor type so it remains unit-testable with no network layer.
    /// </summary>
    public class VaultEntry
    {
        /// <summary>True when this line is a collapsed-stackable ledger row rather than a stored biota.</summary>
        public bool IsLedger { get; }

        public uint Wcid { get; }

        /// <summary>
        /// Units held. For a ledger row this is the total count, independent of MaxStackSize. For a
        /// stored biota it is that object's StackSize, or 1 for a non-stackable.
        /// </summary>
        public long Count { get; }

        /// <summary><see cref="ObjectGuid.Invalid"/> when <see cref="IsLedger"/> is true.</summary>
        public ObjectGuid Guid { get; }

        /// <summary>Null when <see cref="IsLedger"/> is true.</summary>
        public WorldObject WorldObject { get; }

        /// <summary>
        /// Every stored biota this line stands for, in the order <see cref="AccountVaultStore"/>
        /// enumerated them. Empty for a ledger row; exactly one element for an ordinary stored item;
        /// two or more for a group.
        ///
        /// The withdraw path reads it, and the ORDER is part of the contract rather than incidental:
        /// members are taken from the front, so withdrawing 2 of 5 twice takes members 1-2 and then
        /// what were members 3-4, and the same panel action twice does the same thing.
        ///
        /// One caller asks for the other end, and only one: a market sale passes
        /// <see cref="GroupTakeOrder.Back"/> so it never takes the representative while the listing
        /// that names it still has members left. See that enum for why.
        /// </summary>
        public IReadOnlyList<WorldObject> Members { get; }

        /// <summary>
        /// True when this line collapses several equivalent stored biotas into one row. A group is a
        /// READ-TIME projection and is never persisted - see the grouping design doc - so nothing here
        /// survives the call that built it.
        ///
        /// The distinction matters at exactly one place: a group accepts a partial withdraw (its
        /// <see cref="Count"/> is a number of MEMBERS), while a lone stored biota is indivisible and
        /// still refuses one.
        /// </summary>
        public bool IsGroup => Members.Count > 1;

        private VaultEntry(bool isLedger, uint wcid, long count, ObjectGuid guid, WorldObject worldObject, IReadOnlyList<WorldObject> members)
        {
            IsLedger = isLedger;
            Wcid = wcid;
            Count = count;
            Guid = guid;
            WorldObject = worldObject;
            Members = members;
        }

        public static VaultEntry ForLedger(uint wcid, long count)
        {
            return new VaultEntry(true, wcid, count, ObjectGuid.Invalid, null, Array.Empty<WorldObject>());
        }

        public static VaultEntry ForItem(WorldObject item)
        {
            if (item == null)
                return null;

            return new VaultEntry(false, item.WeenieClassId, item.StackSize ?? 1, item.Guid, item, new[] { item });
        }

        /// <summary>
        /// One row standing for several equivalent stored biotas.
        ///
        /// <see cref="WorldObject"/> and <see cref="Guid"/> are the representative, members[0], so
        /// every consumer that only reads those keeps working unchanged and sees a real stored item.
        /// <see cref="Count"/> is the number of MEMBERS - deliberately not a sum of stack sizes, which
        /// a group has no way to express - and <see cref="IsLedger"/> stays false, because every one of
        /// these biotas really is in a vault container.
        ///
        /// The caller owns the list and must not mutate it afterwards; a group is rebuilt from scratch
        /// on the next read.
        /// </summary>
        public static VaultEntry ForGroup(IReadOnlyList<WorldObject> members)
        {
            if (members == null || members.Count == 0)
                return null;

            var representative = members[0];

            if (representative == null)
                return null;

            return new VaultEntry(false, representative.WeenieClassId, members.Count, representative.Guid, representative, members);
        }

        public override string ToString()
        {
            if (IsLedger)
                return $"ledger wcid {Wcid} x{Count}";

            return IsGroup
                ? $"group of {Count} from 0x{Guid.Full:X8} wcid {Wcid}"
                : $"item 0x{Guid.Full:X8} wcid {Wcid} x{Count}";
        }
    }
}
