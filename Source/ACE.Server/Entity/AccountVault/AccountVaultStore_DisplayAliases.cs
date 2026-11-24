using System.Collections.Generic;
using System.Linq;

using ACE.Entity;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Display aliases: the guid a mule panel row lists a stored item under, instead of the item's own.
    ///
    /// WHY. A renderable stored item used to be listed in the vendor panel under its REAL guid, sent as
    /// game data only (GameEventApproachVendor). A client plugin (Decal, and VTank on top of it) then
    /// records that guid from the vendor blob, and the CreateObject that arrives when the item is
    /// withdrawn does not replace that record - so the withdrawn weapon could not be added to VTank until
    /// a relog (owner-reproduced; the plugin mechanism is inferred, Decal is closed source). The client
    /// must therefore never see a stored item's real guid until the CreateObject at withdraw. Every
    /// stored-item row gets an alias instead; PersonalVendor keys its storeItems lookup by it, writes it
    /// on the wire (Vendor.GetWireGuid), and answers appraisals under it.
    ///
    /// WHY HERE, NOT ON THE WINDOW. Owner ruling: an alias lives as long as the account vault, so it
    /// survives a re-summon. Plugins re-identify every guid they have not seen before, so a fresh set of
    /// aliases per PersonalVendor would turn every re-summon into a full-vault re-identify. Several
    /// windows over one store share one alias per item for the same reason.
    ///
    /// THREADING. RebuildView runs on whichever thread the approaching player's landblock ticks on, NOT
    /// on this store's mutation queue, and two windows over one store can sit in different landblock
    /// groups and rebuild concurrently. So the map has its own leaf lock, <see cref="aliasLock"/>, rather
    /// than reusing <see cref="stateLock"/> or the queue: nothing is ever acquired while it is held
    /// except the dynamic guid allocator's own leaf lock, so it cannot join a lock-order cycle, and a
    /// rebuild never has to wait on a deposit's database work.
    ///
    /// NEVER RECYCLED, AND BOUNDED. An alias is a dynamic guid naming no object; it is never handed
    /// back to GuidManager, because a recycled one would be reissued to a real item some client may
    /// still hold a stale vendor record for - the exact bug this exists to fix. An entry is dropped the
    /// moment its item crosses a vault boundary (<see cref="ForgetDisplayAlias"/>, by any route, window
    /// or not), and <see cref="RetainDisplayAliases"/> sweeps anything else a full rebuild no longer
    /// sees, so the map never holds more than the vault's current stored items.
    /// </summary>
    public partial class AccountVaultStore
    {
        /// <summary>Guards <see cref="displayAliases"/>. A leaf lock - see the class remarks.</summary>
        private readonly object aliasLock = new object();

        /// <summary>Stored item's real guid -> its display alias.</summary>
        private readonly Dictionary<uint, uint> displayAliases = new Dictionary<uint, uint>();

        /// <summary>
        /// The alias the panel lists the stored item <paramref name="realGuid"/> under, allocating one the
        /// first time this store is asked about that item. Stable for as long as the item stays in this
        /// store (see <see cref="RetainDisplayAliases"/>). Safe from any thread.
        /// </summary>
        internal ObjectGuid GetOrCreateDisplayAlias(ObjectGuid realGuid)
        {
            lock (aliasLock)
            {
                if (displayAliases.TryGetValue(realGuid.Full, out var alias))
                    return new ObjectGuid(alias);

                alias = world.NewDisplayAliasGuid();

                displayAliases[realGuid.Full] = alias;

                return new ObjectGuid(alias);
            }
        }

        /// <summary>
        /// Drops the alias of every item NOT in <paramref name="liveRealGuids"/> - the stored items a full
        /// GetEntries pass just saw. Skipped, leaving the map as it was, when <see cref="Version"/> has
        /// moved past <paramref name="builtFromVersion"/>: the set is then older than the store, and another
        /// window may already have aliased an item it does not contain. A race that slips past this check
        /// (a mutation landing between the check and the removal) costs only that item a fresh alias on
        /// the next rebuild - a row re-identify in the client, never a wrong withdraw, because every window
        /// re-reads its aliases on every rebuild.
        ///
        /// The caller must only pass a set from a LOADED store: an unloaded store's GetEntries is empty by
        /// design, and pruning against it would throw every alias away.
        /// </summary>
        internal void RetainDisplayAliases(HashSet<uint> liveRealGuids, long builtFromVersion)
        {
            lock (aliasLock)
            {
                if (Version != builtFromVersion)
                    return;

                foreach (var real in displayAliases.Keys.Where(real => !liveRealGuids.Contains(real)).ToList())
                    displayAliases.Remove(real);
            }
        }

        /// <summary>
        /// Drops the alias of one item that has crossed a vault boundary (withdraw by any route, barreling,
        /// the fold's take-out, a return). Called from ForgetFoldMemoryLocked, under stateLock; takes only
        /// the leaf aliasLock. The next rebuild that lists the item again gives it a fresh alias.
        /// </summary>
        private void ForgetDisplayAlias(uint realGuid)
        {
            lock (aliasLock)
                displayAliases.Remove(realGuid);
        }

        /// <summary>Test seam: whether this store holds an alias for the stored item <paramref name="realGuid"/>.</summary>
        internal bool HasDisplayAlias(ObjectGuid realGuid)
        {
            lock (aliasLock)
                return displayAliases.ContainsKey(realGuid.Full);
        }

        /// <summary>Test seam: how many aliases this store is holding.</summary>
        internal int DisplayAliasCount
        {
            get { lock (aliasLock) return displayAliases.Count; }
        }
    }
}
