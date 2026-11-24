using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// The Salvage Roller's escrow (Docs/SalvageRoller/DESIGN.md 5.8): account_vault containers of
    /// <see cref="EscrowContainerKind"/> that hold a batch's target item and Hammers, biota-intact and
    /// guid-preserved, while the batch is open.
    ///
    /// THE ESCROW IS NOT A VAULT. Its containers are partitioned out at load (next to the barrel, in
    /// TryEnsureLoadedLocked) and are never members of <see cref="vaults"/>, so every path that walks the
    /// vault list - GetEntries, EntryCount and the cap, the fold, grouping, the market's vault reads,
    /// reaping - is correct by construction rather than by a filter someone has to remember. Nothing
    /// here bumps <see cref="Version"/> or touches the grouping memo, because nothing a vault window can
    /// see changes.
    ///
    /// The save rules are the vault's own, copied rather than reinvented:
    ///
    ///   - INTO escrow (<see cref="TryEscrow"/>, <see cref="TryReturnToEscrow"/>): the ITEM's save is
    ///     the only thing that records its new home, so it is saved immediately and a failed save is
    ///     queued in pendingDepositSaves for retry, exactly as a deposit is. The container's own
    ///     save only carries derived aggregates.
    ///   - OUT of escrow (<see cref="TryReleaseEscrow"/>): the item is NOT re-saved. Its row keeps
    ///     pointing at the escrow container until the caller delivers it and saves it, so a crash in
    ///     between leaves it in escrow - the recoverable direction, as for a vault withdraw.
    ///   - DESTROYED in escrow (<see cref="TryDestroyEscrowed"/>): a consumed Hammer.
    ///
    /// Every mutation asserts the store's mutation queue, like every other mutation in this class.
    ///
    /// AN UNREADABLE ESCROW CONTAINER DEGRADES THE ESCROW, NEVER THE VAULT: <see cref="escrowDegraded"/>
    /// is set and every operation here refuses with <see cref="EscrowDegradedMessage"/> until a
    /// later load reads every escrow container.
    /// </summary>
    public partial class AccountVaultStore
    {
        private const string EscrowContainerName = "Account Escrow";

        /// <summary>The refusal when the escrow cannot be used right now. Distinct from the vault being unavailable.</summary>
        public const string EscrowUnavailableMessage = "The Salvage Roller cannot reach your account's escrow right now. Try again in a moment.";

        /// <summary>
        /// The refusal while the escrow is degraded (a container the load could not read). Not a transient state, so
        /// it does not invite a retry.
        /// </summary>
        public const string EscrowDegradedMessage = "Part of your account's Salvage Roller escrow could not be loaded, so the Salvage Roller is closed to your account for now. It is retried after about ten minutes in which nobody on your account uses the vault or the Salvage Roller; if it is still closed after that, please report it to staff.";

        /// <summary>The refusal when an escrow operation names an item the escrow does not hold.</summary>
        public const string EscrowItemGoneMessage = "That item is not in the Salvage Roller's escrow.";

        /// <summary>
        /// The account's escrow containers, oldest-first like <see cref="vaults"/>. Guarded by
        /// <see cref="stateLock"/>; assigned only by the load and by <see cref="FindOrCreateEscrowWithFreeSlot"/>.
        /// </summary>
        private readonly List<Container> escrows = new List<Container>();

        /// <summary>escrow container guid -> account_vault.id. Kept for symmetry with vaultRowIds and for diagnostics; escrow containers are never reaped.</summary>
        private readonly Dictionary<uint, uint> escrowRowIds = new Dictionary<uint, uint>();

        /// <summary>
        /// True when the last load met an escrow container it could not READ. Every escrow operation
        /// refuses while it is set: a batch's items may be in the unreadable container.
        /// </summary>
        private bool escrowDegraded;

        /// <summary>How many escrow containers are loaded. For the exclusion tests only.</summary>
        internal int EscrowContainerCountForTest
        {
            get { lock (stateLock) return escrows.Count; }
        }

        /// <summary>Whether the last load degraded the escrow. For the exclusion tests only.</summary>
        internal bool EscrowDegradedForTest
        {
            get { lock (stateLock) return escrowDegraded; }
        }

        /// <summary>
        /// True when every escrow operation could run right now: the store's index is loaded (which
        /// may start a load, like <see cref="IsLoaded"/>), no escrow container failed to read, and every
        /// escrow container has finished its async inventory load (R4 - an unloaded container reports an
        /// empty inventory, which is indistinguishable from an empty one).
        ///
        /// Deliberately independent of the VAULT containers' inventory load: the Salvage Roller does not
        /// need the vault panel to be ready, and the vault does not need the escrow.
        /// </summary>
        public bool IsEscrowReady(out string failReason)
        {
            lock (stateLock)
                return EscrowReadyLocked(out failReason);
        }

        private bool EscrowReadyLocked(out string failReason)
        {
            failReason = null;

            if (!TryEnsureLoadedLocked())
            {
                failReason = EscrowUnavailableMessage;
                return false;
            }

            // Its own line, and deliberately NOT "try again in a moment": an unreadable escrow container stays
            // unreadable until someone repairs it, so a retry hint would only send the player round in circles.
            if (escrowDegraded)
            {
                failReason = EscrowDegradedMessage;
                return false;
            }

            foreach (var escrow in escrows)
            {
                if (!escrow.InventoryLoaded)
                {
                    failReason = EscrowUnavailableMessage;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The escrowed object with this guid, or null when no escrow container holds it (or the escrow
        /// is not ready). A read; never moves anything.
        ///
        /// CALLER CONTRACT: null means EITHER "not held" OR "not ready", and the two call for opposite
        /// actions (one is a finished or lost item, the other a retry). Check <see cref="IsEscrowReady"/>
        /// first; only a null from a ready escrow means the escrow does not hold the item.
        /// </summary>
        internal WorldObject FindEscrowed(uint itemGuid)
        {
            var guid = new ObjectGuid(itemGuid);

            lock (stateLock)
            {
                if (!EscrowReadyLocked(out _))
                    return null;

                foreach (var escrow in escrows)
                {
                    if (escrow.Inventory.TryGetValue(guid, out var item))
                        return item;
                }
            }

            return null;
        }

        /// <summary>
        /// True when <paramref name="containerGuid"/> is one of THIS account's escrow containers. A read; never moves
        /// anything. False when the escrow is not ready, so a caller acting on a true always has a loaded escrow set.
        /// </summary>
        internal bool IsEscrowContainer(uint containerGuid)
        {
            lock (stateLock)
            {
                if (!EscrowReadyLocked(out _))
                    return false;

                return escrows.Any(e => e.Guid.Full == containerGuid);
            }
        }

        /// <summary>
        /// Moves an UNPARENTED object into escrow. The caller (the Salvage Roller sell pipeline) has
        /// already detached it from the player and saved that detach, so until the save below lands the
        /// item's row points at nothing - the window the batch-item purge exemption covers.
        ///
        /// Refuses, having moved nothing, when the escrow is not ready, the item is still parented, or no
        /// escrow container with a free slot exists or can be created. On a refusal the CALLER still holds
        /// the object and must not destroy it.
        ///
        /// <paramref name="landed"/>, when given, is told whether the ITEM's save reached the shard. It runs
        /// only when this returns true (a refusal never reaches the save), possibly later and on the database
        /// thread. A failed save is still queued for retry exactly as before; the callback only reports it.
        /// </summary>
        internal bool TryEscrow(WorldObject item, out uint escrowContainerGuid, out string failReason, Action<bool> landed = null)
        {
            AssertOnMutationQueue(nameof(TryEscrow));

            Touch();

            escrowContainerGuid = 0;
            failReason = null;

            if (item == null)
            {
                failReason = EscrowItemGoneMessage;
                return false;
            }

            if (!IsEscrowReady(out failReason))
                return false;

            if (item.ContainerId != null || item.WielderId != null)
            {
                log.Error($"[VAULT] account {AccountId}: refused to escrow 0x{item.Guid.Full:X8} because it is still parented (container {item.ContainerId}, wielder {item.WielderId}).");
                failReason = EscrowUnavailableMessage;
                return false;
            }

            if (!TryAddToEscrowAndSave(item, out escrowContainerGuid, landed))
            {
                failReason = EscrowUnavailableMessage;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Takes an object OUT of escrow and hands it to the caller, parented to nothing.
        ///
        /// The same contract as <see cref="TryWithdraw(VaultEntry, int, Player, out List{WorldObject}, out string)"/>:
        /// the item is NOT re-saved here, so its row keeps pointing at the escrow container. The caller
        /// must either deliver it (add it to the player and save it immediately) or give it back with
        /// <see cref="TryReturnToEscrow"/>. It must never be Destroy()ed as an error path.
        /// </summary>
        internal bool TryReleaseEscrow(uint itemGuid, out WorldObject item, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryReleaseEscrow));

            Touch();

            item = null;
            failReason = null;

            if (!TryRemoveFromEscrow(itemGuid, out item, out var escrow, out failReason))
                return false;

            // The withdraw rule: the CONTAINER is saved, the item is not. See the class remarks.
            world.SaveBiota(escrow);

            return true;
        }

        /// <summary>
        /// Puts back an object that <see cref="TryReleaseEscrow"/> handed out and the caller could not
        /// deliver - the escrow's counterpart of <see cref="TryReturnWithdrawn"/>. Also the path that
        /// puts a target back with a result already applied, when a take's delivery fails after the
        /// choice was committed (DESIGN 5.4 step 7.9).
        ///
        /// Same save discipline as <see cref="TryEscrow"/>: the item's own save records its home.
        /// On a refusal the CALLER still holds the object and must not destroy it.
        /// </summary>
        internal bool TryReturnToEscrow(WorldObject item, out string failReason)
        {
            AssertOnMutationQueue(nameof(TryReturnToEscrow));

            Touch();

            failReason = null;

            if (item == null)
            {
                failReason = EscrowItemGoneMessage;
                return false;
            }

            if (!IsEscrowReady(out failReason))
            {
                log.Error($"[VAULT] account {AccountId}: could not return 0x{item.Guid.Full:X8} to escrow because the escrow is not ready. The CALLER still holds it and must not destroy it.");
                return false;
            }

            if (item.ContainerId != null || item.WielderId != null)
            {
                log.Error($"[VAULT] account {AccountId}: refused a return to escrow of 0x{item.Guid.Full:X8} because it is still parented (container {item.ContainerId}, wielder {item.WielderId}). A delivered item is not a returnable one.");
                failReason = EscrowUnavailableMessage;
                return false;
            }

            if (!TryAddToEscrowAndSave(item, out _, null))
            {
                log.Error($"[VAULT] account {AccountId}: could not return 0x{item.Guid.Full:X8} ({item.Name}) to escrow. The CALLER still holds it and must not destroy it.");
                failReason = EscrowUnavailableMessage;
                return false;
            }

            log.Warn($"[VAULT] account {AccountId}: item 0x{item.Guid.Full:X8} ({item.Name}) was returned to escrow after a release could not be delivered.");

            return true;
        }

        /// <summary>
        /// Destroys an escrowed object: a Hammer consumed when its batch commits. Refuses, having
        /// destroyed nothing, when the escrow does not hold it.
        ///
        /// CALLER CONTRACT: that refusal is also what an ALREADY-destroyed item returns (it is simply no
        /// longer held). Reconcile rolling a Pending batch forward re-runs this for every Hammer row still
        /// present, so it must check <see cref="IsEscrowReady"/> first and then treat "not held" as success -
        /// the Hammer was consumed before the crash - and delete that Hammer's row, never retry forever.
        ///
        /// <paramref name="landed"/>, when given, is told whether the biota's REMOVAL reached the shard (it
        /// runs only when this returns true). A caller deleting the Hammer's batch row does it there, so the
        /// row never goes before the biota does.
        /// </summary>
        internal bool TryDestroyEscrowed(uint itemGuid, out string failReason, Action<bool> landed = null)
        {
            AssertOnMutationQueue(nameof(TryDestroyEscrowed));

            Touch();

            if (!TryRemoveFromEscrow(itemGuid, out var item, out var escrow, out failReason))
                return false;

            if (landed == null)
                world.DestroyItem(item);
            else
                world.DestroyItem(item, landed);
            world.SaveBiota(escrow);

            return true;
        }

        /// <summary>
        /// Removes an object from whichever escrow container holds it. Also drops any pending retry of
        /// its escrow save: the retry would otherwise run on a later drain against an object that has
        /// since been delivered (harmless) or destroyed (which would write the destroyed biota back).
        /// </summary>
        private bool TryRemoveFromEscrow(uint itemGuid, out WorldObject item, out Container escrow, out string failReason)
        {
            item = null;
            escrow = null;
            failReason = null;

            var guid = new ObjectGuid(itemGuid);

            lock (stateLock)
            {
                if (!EscrowReadyLocked(out failReason))
                    return false;

                foreach (var candidate in escrows)
                {
                    if (candidate.Inventory.ContainsKey(guid))
                    {
                        escrow = candidate;
                        break;
                    }
                }

                if (escrow == null || !escrow.TryRemoveFromInventory(guid, out item))
                {
                    escrow = null;
                    item = null;
                    failReason = EscrowItemGoneMessage;
                    return false;
                }

                pendingDepositSaves.Remove(item);
            }

            return true;
        }

        /// <summary>
        /// Adds an unparented object to the first escrow container with a free slot (creating one when
        /// all are full) and saves the ITEM, queuing a failed save for retry. The container save is
        /// deferred to the end of the drain, exactly as a deposit's is. <paramref name="landed"/> hears the
        /// item save's outcome once it is known; it is never called when this returns false.
        /// </summary>
        private bool TryAddToEscrowAndSave(WorldObject item, out uint escrowContainerGuid, Action<bool> landed)
        {
            escrowContainerGuid = 0;

            var escrow = FindOrCreateEscrowWithFreeSlot();

            if (escrow == null)
                return false;

            lock (stateLock)
            {
                // Appended at the end for the reason DepositToVault gives: placement 0 renumbers every
                // existing item in the container.
                if (!escrow.TryAddToInventory(item, placementPosition: escrow.Inventory.Count))
                {
                    log.Error($"[VAULT] account {AccountId}: TryAddToInventory refused 0x{item.Guid.Full:X8} on escrow 0x{escrow.Guid.Full:X8} ({escrow.Inventory.Count} of {escrow.ItemCapacity} slots used).");
                    return false;
                }

                dirtyVaults.Add(escrow);
            }

            var savedGuid = item.Guid.Full;
            var savedWcid = item.WeenieClassId;

            world.SaveBiota(item, saved =>
            {
                if (!saved)
                {
                    log.Error($"[VAULT] ESCROW SAVE FAILED for account {AccountId}: 0x{savedGuid:X8} (wcid {savedWcid}) is in escrow in memory but its biota did not persist. Queued for retry.");

                    lock (stateLock)
                    {
                        if (!pendingDepositSaves.ContainsKey(item))
                            pendingDepositSaves[item] = 0;
                    }
                }

                landed?.Invoke(saved);
            });

            escrowContainerGuid = escrow.Guid.Full;

            return true;
        }

        /// <summary>
        /// The oldest escrow container with a free slot, creating a new one when every one is full (or
        /// none exists yet). Runs on the mutation queue, which is what makes check-then-create atomic.
        ///
        /// Creation goes through <see cref="CreateIndexedContainer"/>, so it inherits the save-then-index
        /// ordering, the reserved Location and the spawn-filter registration - and its blocking wait on
        /// the world thread (accepted risk, DESIGN section 11), once per 255 escrowed items per account.
        /// </summary>
        private Container FindOrCreateEscrowWithFreeSlot()
        {
            lock (stateLock)
            {
                var existing = escrows.FirstOrDefault(e => e.Inventory.Count < (e.ItemCapacity ?? 0));

                if (existing != null)
                    return existing;
            }

            var container = CreateIndexedContainer(EscrowContainerKind, EscrowContainerName, out var rowId);

            if (container == null)
                return null;

            lock (stateLock)
            {
                escrows.Add(container);
                escrowRowIds[container.Guid.Full] = rowId;
            }

            return container;
        }
    }
}
