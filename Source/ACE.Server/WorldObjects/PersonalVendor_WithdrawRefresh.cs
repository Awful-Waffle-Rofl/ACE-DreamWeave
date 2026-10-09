using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Network.GameEvent.Events;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The deferred ("debounced") panel refresh after a mule withdrawal. The rules themselves are pure
    /// and live in <see cref="MuleWithdrawRefresh"/>; this half wires them to the live window.
    ///
    /// NO TIMER. A deferred window is delivered only by piggybacking on the player's own next withdraw
    /// click once the window has elapsed (see <see cref="RefreshAfterWithdraw"/> and
    /// <see cref="MuleWithdrawRefresh"/>'s class remarks) - never by a server-initiated send, because
    /// GameEventApproachVendor would pop the panel back open for a player who already closed it, and the
    /// client reports no panel-closed message to gate on. The one send a deferral does make is
    /// GameEventInventoryServerSaveFailed (no list, no panel), which releases the client's vendor-buy lock.
    ///
    /// WHAT DEFERRING COSTS, AND HOW IT IS PAID FOR. The panel's lookups (<see cref="proxiedItems"/>,
    /// <see cref="classDisplays"/>) are normally refreshed by <see cref="RebuildView"/>, which only runs
    /// on an approach. Skipping the approach would leave them describing the PRE-withdrawal row: a group
    /// row would still list the members just handed out (so the next partial withdraw of it would try
    /// to take them again and be refused as gone), and a class row would still carry its old count.
    /// RebuildView cannot simply be run without sending, either: a row whose count changed gets a NEW
    /// display guid there (stable display guids, #1449), and the client, not having been sent the list,
    /// only knows the old one. So a deferred withdrawal instead PATCHES the lookups of exactly the rows
    /// it touched, in place, under their existing display guids - a group's member list loses the
    /// members actually withdrawn, a class row is re-read live from the store by its ClassDisplayId.
    /// The next real RebuildView overwrites all of it from the store as it always did.
    /// </summary>
    public partial class PersonalVendor
    {
        /// <summary>Per-viewer pending deferred refreshes for this window. Cleared whenever the Store is rebound or unbound (which WorldObject.Destroy does).</summary>
        private readonly MuleRefreshDebouncer refreshDebouncer = new MuleRefreshDebouncer();

        /// <summary>
        /// True while a deferred refresh is pending for <paramref name="player"/> - that is, while their
        /// client is KNOWN to be showing counts older than the store's. Player_Commerce's refused-buy
        /// branch reads this: a refusal must always correct a client that is known to be stale, even
        /// when another viewer's approach has just rebuilt this window and <see cref="ViewIsStale"/> is
        /// therefore false.
        /// </summary>
        internal bool HasPendingRefreshFor(Player player)
        {
            return player != null && refreshDebouncer.IsPending(player.Guid.Full);
        }

        /// <summary>Test seam: this window's pending-refresh bookkeeping.</summary>
        internal MuleRefreshDebouncer RefreshDebouncer => refreshDebouncer;

        /// <summary>
        /// Test seam: the entry a withdraw request naming <paramref name="displayGuid"/> would be built
        /// from, resolved exactly as <see cref="TryWithdrawTransaction"/> resolves group and class rows
        /// (a group's entry wraps the lookup's own member list, which is what the deferred patch checks
        /// by reference). False for any other guid.
        /// </summary>
        internal bool TryGetLookupEntry(ObjectGuid displayGuid, out VaultEntry entry, out MuleWithdrawRowKind kind)
        {
            if (proxiedItems.TryGetValue(displayGuid, out var members) && members.Count > 1)
            {
                entry = VaultEntry.ForGroup(members);
                kind = MuleWithdrawRowKind.Group;
                return true;
            }

            if (classDisplays.TryGetValue(displayGuid, out var classRow))
            {
                entry = classRow;
                kind = MuleWithdrawRowKind.Class;
                return true;
            }

            entry = null;
            kind = MuleWithdrawRowKind.Individual;
            return false;
        }

        /// <summary>
        /// Test seam: what a withdraw request naming <paramref name="displayGuid"/> would currently be
        /// validated against - a group row's live member count or a class row's count - or null when that
        /// guid names neither.
        /// </summary>
        internal long? WithdrawableCountFor(ObjectGuid displayGuid)
        {
            if (proxiedItems.TryGetValue(displayGuid, out var members))
                return members.Count;

            if (classDisplays.TryGetValue(displayGuid, out var classRow))
                return classRow.Count;

            return null;
        }

        /// <summary>
        /// The tail of a successful <see cref="TryWithdrawTransaction"/>: refresh the panel now, or defer
        /// it. A null player gets nothing, exactly as the unconditional ApproachVendor this replaced
        /// (its override returns at once for a null player).
        ///
        /// THE UPGRADE (no timer-driven send, ever; the lock-release InventoryServerSaveFailed below is a reply to this click - see this file's and MuleWithdrawRefresh's class
        /// remarks). When PrepareWithdrawRefresh says Defer, this checks whether a refresh is ALREADY
        /// pending for this player and its window has elapsed. If so, this very click - proof the panel
        /// is still open, which a timer could never have - upgrades the refresh to Immediate instead of
        /// opening/extending anything. ApproachVendor(player, VendorType.Buy) then rebuilds the view from
        /// the store (RebuildView) and sends it, which harmlessly discards whatever PrepareWithdrawRefresh
        /// just patched into proxiedItems/classDisplays above (the patch was written for the Defer case,
        /// which no longer applies once RebuildView clears and repopulates both dictionaries from the
        /// live store, per PersonalVendor.RebuildView), and its ApproachVendor override's
        /// CancelPendingRefresh call clears the pending window. If nothing is pending, or it has not yet
        /// elapsed, behavior is unchanged: a plain deferral (which does not extend an already-open window).
        /// </summary>
        private void RefreshAfterWithdraw(Player player, AccountVaultStore activeStore,
                                          List<(VaultEntry entry, int amount)> requests,
                                          List<(ObjectGuid displayGuid, MuleWithdrawRowKind kind)> requestRows,
                                          List<IReadOnlyList<WorldObject>> withdrawnPerRequest,
                                          bool anyReturnedToVault)
        {
            if (player == null)
                return;

            var timing = PrepareWithdrawRefresh(activeStore, requests, requestRows, withdrawnPerRequest, anyReturnedToVault);
            timing = ApplyElapsedUpgrade(player.Guid.Full, timing, DateTime.UtcNow);

            var actions = MuleWithdrawRefresh.ActionsAfterWithdraw(timing);

            if (actions.SendListNow)
            {
                // The override cancels any pending deferred refresh for this player: this one satisfies it
                // (whether it is an ordinary immediate refresh or the elapsed-window upgrade above).
                // ListVendorType is Buy here, so ApproachVendor plays this withdrawal's one Buy line.
                ApproachVendor(player, actions.ListVendorType);
                return;
            }

            // Owner ruling: the Buy line plays on EVERY successful withdrawal. Only the emote - no
            // GameEventApproachVendor, no rebuild - so the list itself stays deferred. The same call
            // Vendor.ApproachVendor makes for a non-Undef type (Vendor.DoVendorEmote).
            if (actions.PlayBuyEmoteNow)
                DoVendorEmote(VendorType.Buy, player);

            if (actions.OpenDeferralWindow)
                DeferRefresh(player);

            // The deferred branch sends no vendor list, and the client holds its vendor-buy lock ("you
            // can only move or use one item at a time") until a vendor list OR an
            // InventoryServerSaveFailed arrives. UseDone alone (already sent on success) does not
            // release it, and the "deliver on the next click" design cannot rescue it: a locked client
            // sends no next click. So release it here. This is not a list and opens no panel, so it
            // does not break the no-timer-driven-send rule. WeenieError.None, default. Live-confirmed
            // 2026-09-30: the lock reproduces without this send and clears with it. Never on the
            // immediate path above, where ApproachVendor's list already releases it.
            var session = player.Session;
            session?.Network.EnqueueSend(new GameEventInventoryServerSaveFailed(session, player.Guid.Full));
        }

        /// <summary>
        /// Test seam + the actual logic RefreshAfterWithdraw runs: upgrades a Defer to Immediate when a
        /// refresh is already pending for <paramref name="viewer"/> and its window has elapsed by
        /// <paramref name="now"/>. Anything else (nothing pending, not yet elapsed, or an already-Immediate
        /// timing) passes <paramref name="timing"/> through unchanged. Split out from RefreshAfterWithdraw
        /// so it is reachable without a live Player/Session - see this file's class remarks for why the
        /// upgrade exists at all.
        /// </summary>
        internal MuleRefreshTiming ApplyElapsedUpgrade(uint viewer, MuleRefreshTiming timing, DateTime now)
        {
            if (timing == MuleRefreshTiming.Defer && refreshDebouncer.HasElapsed(viewer, now, MuleWithdrawRefresh.DeferWindowSeconds))
                return MuleRefreshTiming.Immediate;

            return timing;
        }

        /// <summary>
        /// Works out what each request did to its row, decides the refresh timing, and - only when the
        /// answer is Defer - patches this window's lookups so the display guids the client already holds
        /// keep resolving to the rows' LIVE contents. See this file's class remarks.
        ///
        /// No Player, Session or network here, so a test can drive it against a fake-backed store after
        /// withdrawing through the store directly (the live TryWithdrawTransaction path cannot defer in
        /// the test project: with no player every withdrawn item is handed back to the vault, which is
        /// itself an Immediate case).
        /// </summary>
        internal MuleRefreshTiming PrepareWithdrawRefresh(AccountVaultStore activeStore,
                                                          IReadOnlyList<(VaultEntry entry, int amount)> requests,
                                                          IReadOnlyList<(ObjectGuid displayGuid, MuleWithdrawRowKind kind)> requestRows,
                                                          IReadOnlyList<IReadOnlyList<WorldObject>> withdrawnPerRequest,
                                                          bool anyReturnedToVault)
        {
            if (requests == null || requestRows == null || withdrawnPerRequest == null
                || requests.Count != requestRows.Count || requests.Count != withdrawnPerRequest.Count)
            {
                return MuleRefreshTiming.Immediate;
            }

            // Cheap pre-pass: an individual item or a hand-back decides Immediate without reading the store.
            if (anyReturnedToVault || requestRows.Any(r => r.kind == MuleWithdrawRowKind.Individual))
                return MuleRefreshTiming.Immediate;

            // One live read of the store, shared by every class and ledger request in the basket. An
            // unloaded store returns an empty list, which reads as "row gone" below and so falls back to
            // an immediate refresh - the safe direction.
            IReadOnlyList<VaultEntry> live = null;

            IReadOnlyList<VaultEntry> Live() => live ??= activeStore?.GetEntries(0, -1) ?? new List<VaultEntry>();

            var outcomes = new List<MuleWithdrawOutcome>(requests.Count);
            var groupPatches = new List<(ObjectGuid guid, IReadOnlyList<WorldObject> before, List<WorldObject> after)>();
            var classPatches = new List<(ObjectGuid guid, VaultEntry before, VaultEntry after)>();

            for (var i = 0; i < requests.Count; i++)
            {
                var (entry, _) = requests[i];
                var (displayGuid, kind) = requestRows[i];
                var withdrawn = withdrawnPerRequest[i] ?? new List<WorldObject>();

                switch (kind)
                {
                    case MuleWithdrawRowKind.Group:
                    {
                        // The members still on this row are the ones this window listed minus the ones
                        // just handed out. Matched by guid, so it does not depend on the take order.
                        var taken = new HashSet<uint>(withdrawn.Where(w => w != null).Select(w => w.Guid.Full));
                        var remaining = entry.Members.Where(m => m != null && !taken.Contains(m.Guid.Full)).ToList();

                        outcomes.Add(new MuleWithdrawOutcome(kind, remaining.Count == 0));
                        groupPatches.Add((displayGuid, entry.Members, remaining));
                        break;
                    }

                    case MuleWithdrawRowKind.Class:
                    {
                        // ClassDisplayId is the line's stable identity for as long as the line exists
                        // (VaultEntry.ClassDisplayId), so its absence from a live read means the withdraw
                        // emptied it.
                        var now = Live().FirstOrDefault(e => e.Kind == VaultEntryKind.Class && e.ClassDisplayId == entry.ClassDisplayId);

                        outcomes.Add(new MuleWithdrawOutcome(kind, now == null));

                        if (now != null)
                            classPatches.Add((displayGuid, entry, now));

                        break;
                    }

                    case MuleWithdrawRowKind.Ledger:
                    {
                        // A ledger request is re-resolved by wcid on every withdraw (VaultEntry.ForLedger)
                        // and adjudicated by the database, so there is no lookup to patch - only whether a
                        // balance is left.
                        var stillHeld = Live().Any(e => e.Kind == VaultEntryKind.Ledger && e.Wcid == entry.Wcid);

                        outcomes.Add(new MuleWithdrawOutcome(kind, !stillHeld));
                        break;
                    }

                    default:
                        outcomes.Add(new MuleWithdrawOutcome(kind, true));
                        break;
                }
            }

            var timing = MuleWithdrawRefresh.Decide(outcomes, anyReturnedToVault);

            if (timing != MuleRefreshTiming.Defer)
                return timing;

            // Patched only while the lookup still holds the very object this request was resolved from.
            // If ANY target no longer does, the lookups moved under this basket - a RebuildView refilled
            // them, or the Store setter cleared them (the I8 store re-fetch in TryWithdrawTransaction
            // rebinds the window mid-basket and the setter empties every lookup). The client's display
            // guids can then no longer be trusted to resolve, so the refresh is NOT deferred: fall back to
            // Immediate, which rebuilds and re-sends. Checked for every target before any is written, so
            // a basket is never left half-patched.
            foreach (var (guid, before, _) in groupPatches)
            {
                if (!proxiedItems.TryGetValue(guid, out var current) || !ReferenceEquals(current, before))
                    return MuleRefreshTiming.Immediate;
            }

            foreach (var (guid, before, _) in classPatches)
            {
                if (!classDisplays.TryGetValue(guid, out var current) || !ReferenceEquals(current, before))
                    return MuleRefreshTiming.Immediate;
            }

            foreach (var (guid, _, after) in groupPatches)
                proxiedItems[guid] = after;

            foreach (var (guid, _, after) in classPatches)
                classDisplays[guid] = after;

            return timing;
        }

        /// <summary>
        /// Opens (or joins) this viewer's fixed deferral window. There is no timer to schedule - only the
        /// first deferral of a window records when it opened; a deferral inside an already-open window is
        /// a no-op, so the window is never pushed back. The window is later delivered, if at all, only by
        /// <see cref="RefreshAfterWithdraw"/> upgrading a subsequent click of this player's own once the
        /// window has elapsed, or satisfied outright by any approach (<see cref="CancelPendingRefresh"/>).
        /// </summary>
        private void DeferRefresh(Player player)
        {
            var viewer = player.Guid.Full;

            if (refreshDebouncer.TryOpenWindow(viewer, DateTime.UtcNow))
            {
                if (log.IsDebugEnabled)
                    log.Debug($"[MULE VENDOR] {Name}: deferred the panel refresh for {player.Name} after a partial withdraw; it becomes eligible to piggyback on their next withdraw click after {MuleWithdrawRefresh.DeferWindowSeconds} s (account {StoreAccountId}).");
            }
            else if (log.IsDebugEnabled)
            {
                log.Debug($"[MULE VENDOR] {Name}: deferred the panel refresh for {player.Name} after a partial withdraw (a refresh is already pending for account {StoreAccountId}; the window is not extended).");
            }
        }

        /// <summary>
        /// Every pending deferred refresh is dropped when this window is rebound or unbound - the
        /// viewers' panels described the old binding, and WorldObject.Destroy unbinds (Store = null), so
        /// this is also the destroy-time cleanup.
        /// </summary>
        private void ClearPendingRefreshes()
        {
            refreshDebouncer.Clear();
        }

        /// <summary>An approach sends the whole list, which satisfies any deferred refresh pending for that player.</summary>
        private void CancelPendingRefresh(Player player)
        {
            if (player != null && refreshDebouncer.Cancel(player.Guid.Full) && log.IsDebugEnabled)
                log.Debug($"[MULE VENDOR] {Name}: a panel refresh for {player.Name} satisfied the deferred one pending for them.");
        }
    }
}
