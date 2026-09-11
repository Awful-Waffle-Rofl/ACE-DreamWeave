using System.Collections.Generic;

namespace ACE.Server.Entity.Facets
{
    /// <summary>Where a remembered worn item was found.</summary>
    public enum FacetGearSource
    {
        /// <summary>Nothing the character or account holds matches this entry. Report it by name.</summary>
        NotFound,

        /// <summary>Already in the character's own packs. No movement needed beyond equipping.</summary>
        Inventory,

        /// <summary>Parked by reference in the account vault, still carrying its original guid.</summary>
        VaultItem,

        /// <summary>
        /// The original biota is gone - the vault collapsed a pristine item into a ledger stack and
        /// destroyed it. Withdrawing from the stack rebuilds an equivalent object with a NEW guid.
        /// </summary>
        VaultLedger,
    }

    /// <summary>One remembered entry, resolved to where it can actually be obtained from.</summary>
    public readonly struct FacetGearResolution
    {
        public FacetGearResolution(FacetGearSource source, uint guid, uint wcid, int slot)
        {
            Source = source;
            Guid = guid;
            Wcid = wcid;
            Slot = slot;
        }

        public FacetGearSource Source { get; }

        /// <summary>Meaningless when Source is VaultLedger or NotFound - the object does not exist yet.</summary>
        public uint Guid { get; }

        public uint Wcid { get; }

        /// <summary>The EquipMask the item was worn in, as its raw integer value.</summary>
        public int Slot { get; }
    }

    /// <summary>
    /// Resolution ordering for a facet's remembered worn set. Pure - it takes plain sets and counts
    /// rather than a Player or a vault store, so it is unit-testable and so the caller keeps ownership
    /// of the vault's serialized mutation queue.
    ///
    /// The caller MUST still re-verify ownership before equipping anything: a remembered guid may
    /// belong to an object that has since been traded, sold or given away, in which case it is now
    /// someone else's item. Resolution answers "where would this be", never "may I have it".
    /// </summary>
    public static class FacetGear
    {
        public static FacetGearResolution Resolve(
            FacetEquipEntry entry,
            ISet<uint> inventoryGuids,
            ISet<uint> vaultItemGuids,
            IReadOnlyDictionary<uint, int> vaultLedgerCounts)
        {
            if (entry == null)
                return new FacetGearResolution(FacetGearSource.NotFound, 0, 0, 0);

            if (inventoryGuids != null && inventoryGuids.Contains(entry.Guid))
                return new FacetGearResolution(FacetGearSource.Inventory, entry.Guid, entry.Wcid, entry.Slot);

            if (vaultItemGuids != null && vaultItemGuids.Contains(entry.Guid))
                return new FacetGearResolution(FacetGearSource.VaultItem, entry.Guid, entry.Wcid, entry.Slot);

            if (vaultLedgerCounts != null && vaultLedgerCounts.TryGetValue(entry.Wcid, out var count) && count > 0)
                return new FacetGearResolution(FacetGearSource.VaultLedger, 0, entry.Wcid, entry.Slot);

            return new FacetGearResolution(FacetGearSource.NotFound, entry.Guid, entry.Wcid, entry.Slot);
        }

        /// <summary>
        /// Resolves a whole set, decrementing a working copy of the ledger counts as it goes so two
        /// remembered entries cannot both claim the last stack of the same wcid.
        /// </summary>
        public static List<FacetGearResolution> ResolveAll(
            IEnumerable<FacetEquipEntry> entries,
            ISet<uint> inventoryGuids,
            ISet<uint> vaultItemGuids,
            IReadOnlyDictionary<uint, int> vaultLedgerCounts)
        {
            var results = new List<FacetGearResolution>();

            if (entries == null)
                return results;

            var remaining = new Dictionary<uint, int>();

            if (vaultLedgerCounts != null)
            {
                foreach (var pair in vaultLedgerCounts)
                    remaining[pair.Key] = pair.Value;
            }

            foreach (var entry in entries)
            {
                var result = Resolve(entry, inventoryGuids, vaultItemGuids, remaining);

                if (result.Source == FacetGearSource.VaultLedger)
                    remaining[result.Wcid] = remaining[result.Wcid] - 1;

                results.Add(result);
            }

            return results;
        }
    }
}
