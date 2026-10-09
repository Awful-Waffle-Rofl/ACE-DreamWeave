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
    /// Which of the three things a <see cref="VaultEntry"/> stands for.
    ///
    /// THIS REPLACED A BOOL, AND THE REPLACEMENT WAS THE WHOLE POINT. The type used to carry
    /// <c>bool IsLedger</c>, and every consumer was written as a two-way split: "ledger row" versus
    /// "stored biota, so it has a guid and a WorldObject". A counted CLASS row is a third thing - no
    /// biota, no guid, an empty Members list - and under the old bool it would have answered false to
    /// IsLedger and been read as a stored item by every one of those sites, silently, with
    /// <see cref="ObjectGuid.Invalid"/> standing in for a real guid. Deleting the property is what
    /// made each of those sites fail to COMPILE and get a deliberate ruling instead.
    ///
    /// DO NOT ADD BOOLEAN HELPERS BACK. Not <c>IsLedger</c>, not <c>IsClass</c>, not "just for
    /// symmetry": a bool per kind restores exactly the two-way reading this enum exists to prevent,
    /// and the next kind would be invisible again. <see cref="VaultEntry.IsGroup"/> is the one
    /// exception and it is not a kind at all - it is orthogonal, it applies only to
    /// <see cref="StoredItem"/>, and it answers a different question (can this line be withdrawn in
    /// part).
    /// </summary>
    public enum VaultEntryKind
    {
        /// <summary>
        /// One or more real biotas sitting in a vault container. <see cref="VaultEntry.Guid"/> and
        /// <see cref="VaultEntry.WorldObject"/> are real, and <see cref="VaultEntry.Members"/> holds
        /// at least one object.
        /// </summary>
        StoredItem = 0,

        /// <summary>
        /// A collapsed-stackable ledger row (account_vault_stack). No biota: the count is units of a
        /// wcid, and a withdraw builds fresh stacks.
        /// </summary>
        Ledger = 1,

        /// <summary>
        /// A counted item-class row (account_vault_class). No biota either: the count is ITEMS of one
        /// class, the row carries a pooled Value, and a withdraw materializes one object per item from
        /// the stored canonical form.
        /// </summary>
        Class = 2,
    }

    /// <summary>
    /// One <c>account_vault_class</c> row standing behind a <see cref="VaultEntryKind.Class"/> entry.
    ///
    /// SEVERAL OF THESE CAN SHARE ONE DRAWN LINE, and that is why this type exists. The class key
    /// carries the RAW (ItemWorkmanship, NumItemsInMaterial) pair, so a withdraw hands back exactly
    /// what was deposited; the panel buckets their QUOTIENT, because that is the number the player
    /// sees. The class partition is therefore strictly finer than the display partition, and an entry
    /// that assumed one row per line counted - and charged - for rows nobody could see (the
    /// 2026-09-25 entry-count defect).
    ///
    /// Each member keeps its OWN canonical form, count and pooled total. Merging them would mean
    /// choosing one raw pair and discarding the other, which is the one genuinely lossy operation
    /// available here: a player would withdraw a bag carrying numbers they never deposited.
    /// </summary>
    public sealed class VaultClassMember
    {
        /// <summary>The account_vault_class row's key: a hash of exactly <see cref="CanonicalForm"/>.</summary>
        public string ClassKey { get; }

        /// <summary>The stored payload this member's items are rebuilt from.</summary>
        public string CanonicalForm { get; }

        /// <summary>Items on this row.</summary>
        public long Count { get; }

        /// <summary>PropertyInt.Value pooled across this row's <see cref="Count"/> items.</summary>
        public long TotalValue { get; }

        public VaultClassMember(string classKey, string canonicalForm, long count, long totalValue)
        {
            ClassKey = classKey;
            CanonicalForm = canonicalForm;
            Count = count;
            TotalValue = totalValue;
        }

        public override string ToString() => $"class {ClassKey} x{Count} worth {TotalValue}";
    }

    /// <summary>
    /// One displayable line in a store: a stored biota, a collapsed ledger row, or a counted item
    /// class. <see cref="Kind"/> says which, and it is an enum rather than a bool deliberately - see
    /// <see cref="VaultEntryKind"/>.
    ///
    /// This is a projection, not a handle. It is built fresh by every
    /// <see cref="AccountVaultStore.GetEntries(int, int)"/> call and holds no authority - a withdraw
    /// re-resolves the guid against the store's live vaults, so an entry captured before another
    /// window emptied the vault fails cleanly instead of transacting against a stale view.
    ///
    /// For both biota-less kinds there is no object behind the line at all: <see cref="Guid"/> is
    /// <see cref="ObjectGuid.Invalid"/>, <see cref="WorldObject"/> is null and <see cref="Members"/>
    /// is empty. Materializing a display object for those lines belongs to the vendor view, NOT to the
    /// store - the store must stay free of any vendor type so it remains unit-testable with no network
    /// layer.
    /// </summary>
    public class VaultEntry
    {
        /// <summary>Which of the three things this line stands for. Never inferred from Guid or Members.</summary>
        public VaultEntryKind Kind { get; }

        public uint Wcid { get; }

        /// <summary>
        /// How much this line holds. For a ledger row it is total UNITS, independent of MaxStackSize;
        /// for a class row it is a number of ITEMS; for a stored biota it is that object's StackSize,
        /// or 1 for a non-stackable; for a group it is the number of MEMBERS.
        /// </summary>
        public long Count { get; }

        /// <summary>
        /// The pooled PropertyInt.Value across all <see cref="Count"/> items, for a
        /// <see cref="VaultEntryKind.Class"/> row. Zero for every other kind, where it means nothing.
        /// </summary>
        public long TotalValue { get; }

        /// <summary>
        /// The REPRESENTATIVE class key this line stands for, for a <see cref="VaultEntryKind.Class"/>
        /// row: <see cref="ClassMembers"/>[0]'s. Null for every other kind.
        ///
        /// A line can stand for SEVERAL class rows (see <see cref="ClassMembers"/>), so a consumer that
        /// needs every row the line covers - a withdraw, a "which keys is the store serving" audit -
        /// must walk that list rather than read this. This stays the representative so that a consumer
        /// which only needs one key to name the line keeps working unchanged.
        /// </summary>
        public string ClassKey { get; }

        /// <summary>
        /// The REPRESENTATIVE stored canonical form for a <see cref="VaultEntryKind.Class"/> row:
        /// <see cref="ClassMembers"/>[0]'s. Null for every other kind.
        ///
        /// It is what the panel DRAWS the line from, which is correct: every member of a display group
        /// differs from the representative only on properties the player cannot tell apart on the row.
        /// It is NOT what a withdraw rebuilds from beyond the first member - see
        /// <see cref="ClassMembers"/>.
        /// </summary>
        public string CanonicalForm { get; }

        /// <summary>
        /// Every <c>account_vault_class</c> row this line stands for, in CLASS KEY ASCENDING order.
        /// Empty for every kind but <see cref="VaultEntryKind.Class"/>, where it holds at least one.
        ///
        /// The order is part of the contract, not incidental: a withdraw drains members from the front
        /// in this order, so the same panel action twice does the same thing, and it matches the DAO's
        /// own ORDER BY so the panel is stable across reads.
        ///
        /// ACCEPTED IMPRECISION, stated here because it is deliberate. <see cref="Count"/> is the sum
        /// of the members' counts and <see cref="TotalValue"/> the sum of their pooled totals, so the
        /// unit value the panel shows is the GROUP's average - while a withdrawn bag carries its own
        /// member row's share, which can differ slightly. This is not new: PropertyInt.Value is already
        /// a groupable key for stored items, so a display group of loose items likewise shows one value
        /// while its members differ. Do not "fix" it by merging rows; that is the lossy operation the
        /// class tier exists to avoid.
        /// </summary>
        public IReadOnlyList<VaultClassMember> ClassMembers { get; }

        /// <summary>
        /// The stable id of the DISPLAY group this <see cref="VaultEntryKind.Class"/> line draws, per
        /// <see cref="VaultCollapse.ClassDisplayId"/>: 32 lowercase hex characters. Null for every other
        /// kind.
        ///
        /// This, and not <see cref="ClassKey"/>, is what a market listing of a class line is anchored
        /// on. <see cref="ClassKey"/> is the REPRESENTATIVE member's key and moves as members are
        /// drained or added; this is derived from what the line IS and holds for as long as the line
        /// does.
        /// </summary>
        public string ClassDisplayId { get; }

        /// <summary>
        /// A <see cref="VaultEntryKind.Class"/> row's own PropertyString.Name override, or null when
        /// the class carries none and the template's name applies. Null for every other kind - a
        /// stored item has a <see cref="WorldObject"/> to read a name off, and a ledger row is a wcid
        /// with no overrides at all.
        ///
        /// It is here so that /mule search sees the name the PLAYER sees. A renamed bag forms its own
        /// class (Name is part of the identity key), and searching it against the template's name
        /// alone would silently fail to find exactly the items a player is most likely to look for.
        /// </summary>
        public string DisplayName { get; }

        /// <summary><see cref="ObjectGuid.Invalid"/> for both biota-less kinds.</summary>
        public ObjectGuid Guid { get; }

        /// <summary>Null for both biota-less kinds.</summary>
        public WorldObject WorldObject { get; }

        /// <summary>
        /// Every stored biota this line stands for, in the order <see cref="AccountVaultStore"/>
        /// enumerated them. Empty for a ledger row AND for a class row; exactly one element for an
        /// ordinary stored item; two or more for a group.
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
        ///
        /// It is ORTHOGONAL to <see cref="Kind"/> rather than a fourth kind, and it is the one boolean
        /// this type keeps. Both biota-less kinds have an empty <see cref="Members"/> list, so this is
        /// false for them by construction and no call site has to remember that.
        /// </summary>
        public bool IsGroup => Members.Count > 1;

        private VaultEntry(VaultEntryKind kind, uint wcid, long count, ObjectGuid guid, WorldObject worldObject, IReadOnlyList<WorldObject> members,
                           long totalValue, string classKey, string canonicalForm, string displayName,
                           IReadOnlyList<VaultClassMember> classMembers, string classDisplayId = null)
        {
            ClassDisplayId = classDisplayId;
            Kind = kind;
            Wcid = wcid;
            Count = count;
            Guid = guid;
            WorldObject = worldObject;
            Members = members;
            TotalValue = totalValue;
            ClassKey = classKey;
            CanonicalForm = canonicalForm;
            DisplayName = displayName;
            ClassMembers = classMembers;
        }

        public static VaultEntry ForLedger(uint wcid, long count)
        {
            return new VaultEntry(VaultEntryKind.Ledger, wcid, count, ObjectGuid.Invalid, null, Array.Empty<WorldObject>(), 0, null, null, null,
                                  Array.Empty<VaultClassMember>());
        }

        /// <summary>
        /// One counted item-CLASS line: every <c>account_vault_class</c> row that draws as this line,
        /// in class key ascending order. <see cref="Count"/> is the sum of their counts and
        /// <see cref="TotalValue"/> the sum of their pooled totals.
        ///
        /// SEVERAL ROWS PER LINE IS THE ORDINARY CASE, not an exception - a class key carries the raw
        /// workmanship pair while the panel buckets its quotient. A caller that needs every row must
        /// walk <see cref="ClassMembers"/>; <see cref="ClassKey"/> and <see cref="CanonicalForm"/>
        /// answer for the representative alone.
        ///
        /// There is no biota behind it, exactly as for a ledger row, so <see cref="Guid"/> is invalid,
        /// <see cref="WorldObject"/> is null and <see cref="Members"/> is empty. The canonical forms
        /// travel on the entry because the withdraw path rebuilds each item from them, and re-reading
        /// them from the shard at withdraw time would be a second source that could disagree with the
        /// rows this line was resolved against.
        ///
        /// The caller owns the list and must not mutate it afterwards; a line is rebuilt from scratch
        /// on the next read.
        ///
        /// <paramref name="classDisplayId"/> is REQUIRED rather than defaulted: it is the identity a
        /// market listing of this line is anchored on (see <see cref="ClassDisplayId"/>), and a line
        /// built without one could be listed by nobody and matched by every null.
        /// </summary>
        public static VaultEntry ForClass(uint wcid, IReadOnlyList<VaultClassMember> classMembers, string displayName, string classDisplayId)
        {
            if (string.IsNullOrEmpty(classDisplayId))
                throw new ArgumentException("a class line needs its display id", nameof(classDisplayId));

            if (classMembers == null || classMembers.Count == 0)
                return null;

            long count = 0;
            long totalValue = 0;

            foreach (var member in classMembers)
            {
                count += member.Count;
                totalValue += member.TotalValue;
            }

            return new VaultEntry(VaultEntryKind.Class, wcid, count, ObjectGuid.Invalid, null, Array.Empty<WorldObject>(), totalValue,
                                  classMembers[0].ClassKey, classMembers[0].CanonicalForm, displayName, classMembers, classDisplayId);
        }

        public static VaultEntry ForItem(WorldObject item)
        {
            if (item == null)
                return null;

            return new VaultEntry(VaultEntryKind.StoredItem, item.WeenieClassId, item.StackSize ?? 1, item.Guid, item, new[] { item }, 0, null, null, null,
                                  Array.Empty<VaultClassMember>());
        }

        /// <summary>
        /// One row standing for several equivalent stored biotas.
        ///
        /// <see cref="WorldObject"/> and <see cref="Guid"/> are the representative, members[0], so
        /// every consumer that only reads those keeps working unchanged and sees a real stored item.
        /// <see cref="Count"/> is the number of MEMBERS - deliberately not a sum of stack sizes, which
        /// a group has no way to express - and <see cref="Kind"/> stays
        /// <see cref="VaultEntryKind.StoredItem"/>, because every one of these biotas really is in a
        /// vault container.
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

            return new VaultEntry(VaultEntryKind.StoredItem, representative.WeenieClassId, members.Count, representative.Guid, representative, members, 0, null, null, null,
                                  Array.Empty<VaultClassMember>());
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case VaultEntryKind.Ledger:
                    return $"ledger wcid {Wcid} x{Count}";

                case VaultEntryKind.Class:
                    return ClassMembers.Count > 1
                        ? $"class group of {ClassMembers.Count} rows from {ClassKey} wcid {Wcid} x{Count} worth {TotalValue}"
                        : $"class {ClassKey} wcid {Wcid} x{Count} worth {TotalValue}";

                default:
                    return IsGroup
                        ? $"group of {Count} from 0x{Guid.Full:X8} wcid {Wcid}"
                        : $"item 0x{Guid.Full:X8} wcid {Wcid} x{Count}";
            }
        }
    }
}
