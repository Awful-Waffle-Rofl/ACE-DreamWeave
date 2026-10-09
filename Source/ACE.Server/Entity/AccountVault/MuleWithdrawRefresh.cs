using System;
using System.Collections.Generic;

using ACE.Entity.Enum;

namespace ACE.Server.Entity.AccountVault
{
    /// <summary>
    /// Which kind of mule panel row one withdraw request named. Decides whether that request can ever
    /// defer the panel refresh - see <see cref="MuleWithdrawRefresh.Decide"/>.
    /// </summary>
    internal enum MuleWithdrawRowKind
    {
        /// <summary>A single stored biota (direct, or behind a one-member proxy). Its row always disappears.</summary>
        Individual,

        /// <summary>A GROUP proxy row: several equivalent stored biotas on one line.</summary>
        Group,

        /// <summary>A counted item-CLASS row (account_vault_class).</summary>
        Class,

        /// <summary>A collapsed stack LEDGER row (account_vault_stack).</summary>
        Ledger,
    }

    /// <summary>What one successful withdraw request did to the row it named.</summary>
    internal readonly record struct MuleWithdrawOutcome(MuleWithdrawRowKind Kind, bool RowEmptied);

    internal enum MuleRefreshTiming
    {
        /// <summary>Re-send the whole vendor list now (the pre-debounce behaviour).</summary>
        Immediate,

        /// <summary>Do not send it now; make sure one refresh is pending for this viewer.</summary>
        Defer,
    }

    /// <summary>
    /// The pure half of the mule panel's deferred ("debounced") refresh after a withdrawal.
    ///
    /// WHY IT EXISTS. Every successful withdrawal used to re-send the whole vendor list
    /// (GameEventApproachVendor). With a few hundred rows that is one very large fragmented message,
    /// and a player clicking a class/group row of salvage several times a second sent several of them a
    /// second - which crashed at least one beta client. A partial decrement of a class/group/ledger row
    /// changes nothing the client needs RIGHT NOW (the row is still there; only its count label is
    /// stale), so those refreshes are coalesced rather than sent on every click.
    ///
    /// NO TIMER-DRIVEN SEND, EVER (owner ruling, superseding the original 5 s ActionChain timer).
    /// GameEventApproachVendor OPENS the vendor panel on the client, and the client sends no
    /// "panel closed" message - so a timer that fires after the deferral window elapses cannot tell a
    /// player still watching the panel from one who closed it and walked off, and would pop the panel
    /// back open for the latter. Instead, a pending refresh is delivered only piggybacked on the
    /// player's OWN next withdraw click: <see cref="MuleRefreshDebouncer"/> just records when the
    /// window opened, and PersonalVendor upgrades that click to an immediate refresh once the window
    /// has elapsed. A click proves the panel is still open in a way a timer never can. Until that next
    /// click (or a reopen, or a refused buy - see Player_Commerce's HasPendingRefreshFor branch), the
    /// client keeps showing a stale count for the row(s) that deferred - an accepted trade-off, not a
    /// bug, since correcting it without a click would require the same unsafe timer-driven send.
    /// The one non-list send is GameEventInventoryServerSaveFailed, a direct reply to the player's own
    /// click rather than a timer send, made at the end of every deferred
    /// withdrawal: it carries no list and opens no panel, and it is what releases the client's
    /// vendor-buy lock, which a deferral (no vendor list sent) would otherwise leave held, because a
    /// locked client sends no next click (see PersonalVendor.RefreshAfterWithdraw).
    ///
    /// Everything here is free of Player/Session/network so it can be unit tested; PersonalVendor wires
    /// it to the real panel.
    /// </summary>
    internal static class MuleWithdrawRefresh
    {
        /// <summary>
        /// The FIXED deferral window, measured from the first deferred withdrawal. Further deferrable
        /// withdrawals inside it never push it back, and there is no timer to restart - see this class's
        /// remarks. A window is delivered only by the player's own next withdraw click landing at or
        /// after this many seconds past the window's open time.
        /// </summary>
        public const float DeferWindowSeconds = 5.0f;

        /// <summary>
        /// What the vendor does right after a successful withdrawal, for each timing. OWNER RULING: the
        /// mule's Buy line ("Here it is, exactly as you left it.") plays on EVERY successful withdrawal,
        /// exactly once, on every path - the immediate refresh here, a plain deferral, and an
        /// elapsed-window upgrade (PersonalVendor.RefreshAfterWithdraw reruns this with
        /// MuleRefreshTiming.Immediate once it detects the upgrade, so it plays the line the same way
        /// an ordinary immediate refresh does). An immediate refresh gets it from
        /// ApproachVendor(player, VendorType.Buy), as it always did; a deferred withdrawal plays the Buy
        /// emote on its own at once and sends no list.
        /// </summary>
        public static MuleRefreshActions ActionsAfterWithdraw(MuleRefreshTiming timing)
        {
            return timing == MuleRefreshTiming.Defer
                ? new MuleRefreshActions(SendListNow: false, ListVendorType: VendorType.Undef, PlayBuyEmoteNow: true, OpenDeferralWindow: true)
                : new MuleRefreshActions(SendListNow: true, ListVendorType: VendorType.Buy, PlayBuyEmoteNow: false, OpenDeferralWindow: false);
        }

        /// <summary>
        /// Owner-ruled refresh timing for one successful withdraw basket:
        ///  - IMMEDIATE if any request withdrew an individual item (its row disappears), or emptied the
        ///    class/group/ledger row it named, or if anything withdrawn had to be handed back to the vault
        ///    (the vault's shape moved in a way the deferred lookups do not track), or if there are no
        ///    outcomes at all (nothing to reason about - fail toward the old behaviour).
        ///  - DEFER otherwise: only partial decrements of class/group rows and ledger rows that still
        ///    have a balance.
        /// </summary>
        public static MuleRefreshTiming Decide(IReadOnlyList<MuleWithdrawOutcome> outcomes, bool anyReturnedToVault)
        {
            if (anyReturnedToVault || outcomes == null || outcomes.Count == 0)
                return MuleRefreshTiming.Immediate;

            foreach (var outcome in outcomes)
            {
                if (outcome.Kind == MuleWithdrawRowKind.Individual || outcome.RowEmptied)
                    return MuleRefreshTiming.Immediate;
            }

            return MuleRefreshTiming.Defer;
        }
    }

    /// <summary>
    /// The plan <see cref="MuleWithdrawRefresh.ActionsAfterWithdraw"/> returns: send the list now (and
    /// with which vendor type - a non-Undef type plays that vendor emote inside ApproachVendor), play the
    /// Buy emote on its own now, and open a deferral window.
    /// </summary>
    internal readonly record struct MuleRefreshActions(bool SendListNow, VendorType ListVendorType, bool PlayBuyEmoteNow, bool OpenDeferralWindow);

    /// <summary>
    /// Per-viewer pending-refresh bookkeeping for ONE mule window. Holds NO timer and schedules nothing -
    /// see <see cref="MuleWithdrawRefresh"/>'s class remarks for why a timer is unsafe here. This just
    /// records that a window is open and WHEN it opened, so the caller (PersonalVendor) can ask, on the
    /// player's own next withdraw click, whether that window has elapsed and should be upgraded to an
    /// immediate refresh.
    ///
    /// Keyed by viewer (character guid), so two grantees looking at the same mule each get their own
    /// window.
    ///
    /// Locked because the withdraw handler and an approach can run on different landblock groups'
    /// threads for the same window.
    /// </summary>
    internal sealed class MuleRefreshDebouncer
    {
        private readonly object sync = new object();
        private readonly Dictionary<uint, DateTime> pending = new Dictionary<uint, DateTime>();

        public int PendingCount
        {
            get { lock (sync) return pending.Count; }
        }

        public bool IsPending(uint viewer)
        {
            lock (sync)
                return pending.ContainsKey(viewer);
        }

        /// <summary>
        /// Opens a deferral window for <paramref name="viewer"/>, recording <paramref name="now"/> as
        /// when it opened, if none is pending. Returns true for a NEW window; false when one is already
        /// pending, in which case nothing is written - the window is fixed from its first deferral and
        /// is never pushed back by a later one.
        /// </summary>
        public bool TryOpenWindow(uint viewer, DateTime now)
        {
            lock (sync)
            {
                if (pending.ContainsKey(viewer))
                    return false;

                pending[viewer] = now;
                return true;
            }
        }

        /// <summary>An immediate refresh (or an upgrade) satisfies any pending one. Returns whether one was pending.</summary>
        public bool Cancel(uint viewer)
        {
            lock (sync)
                return pending.Remove(viewer);
        }

        /// <summary>
        /// True only when a window is pending for <paramref name="viewer"/> AND at least
        /// <paramref name="windowSeconds"/> have elapsed since it opened. False when nothing is pending -
        /// the caller must not open a window here, only read whether an already-open one has matured.
        /// </summary>
        public bool HasElapsed(uint viewer, DateTime now, float windowSeconds)
        {
            lock (sync)
            {
                if (!pending.TryGetValue(viewer, out var openedAt))
                    return false;

                return (now - openedAt).TotalSeconds >= windowSeconds;
            }
        }

        /// <summary>Drops every pending window - the vendor was destroyed or re-pointed at another store.</summary>
        public void Clear()
        {
            lock (sync)
                pending.Clear();
        }
    }
}
