using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// One peddler purchase: what a listing sells, at what price. Data only, never a WorldObject; the buy builds the
    /// objects from it (ThreadPeddlerPayment.BuildPurchase) and hands them over at once. <see cref="Pattern"/> is set only
    /// for a Pattern entry, and fixes which pinnacle the Pattern is for.
    /// </summary>
    public sealed class PeddlerPurchase
    {
        public PeddlerPurchase(string entryId, int price, Knots.PinnacleDef pattern = null)
        {
            EntryId = entryId;
            Price = price;
            Pattern = pattern;
        }

        /// <summary>The prize-table entry id the purchase delivers.</summary>
        public string EntryId { get; }

        /// <summary>A Pattern purchase's pinnacle; null for every other entry.</summary>
        public Knots.PinnacleDef Pattern { get; }

        /// <summary>Peddler units: items taken at random from the buyer's own held pile at the buy.</summary>
        public int Price { get; }
    }

    public enum PeddlerChargeOutcome
    {
        /// <summary>The payment was taken from the pile and the purchase recorded.</summary>
        Charged,

        /// <summary>The basket costs more than the buyer's balance. Nothing was taken.</summary>
        Insufficient,

        /// <summary>The buyer's shop is not open: the run is not Cleared, the peddler never presented to them, or they are done.</summary>
        ShopNotOpen,

        /// <summary>The buyer held no deal seat at the clear (a non-member, a keyless member, or one the puzzle policy removed).</summary>
        NoSeat,

        /// <summary>The basket is empty, holds a null purchase, a purchase without an entry id, or a price below 1.</summary>
        Invalid,

        /// <summary>
        /// The basket would leave the buyer holding more than one of an entry the plan's config caps at one per player
        /// (PeddlerConfig.OnePerPlayer), counting what they have already bought. Nothing was taken.
        /// </summary>
        LimitReached,
    }

    /// <summary>What <see cref="ThreadDungeonRun.TryChargePeddlerPurchase"/> did, and the buyer's balance after it.</summary>
    public readonly struct PeddlerChargeResult
    {
        public PeddlerChargeResult(PeddlerChargeOutcome outcome, long balance)
        {
            Outcome = outcome;
            Balance = balance;
        }

        public PeddlerChargeOutcome Outcome { get; }
        public long Balance { get; }
        public bool IsCharged => Outcome == PeddlerChargeOutcome.Charged;
    }

    /// <summary>
    /// The one-per-player rule (PeddlerConfig.OnePerPlayer, owner ruling 2026-11-20), shared by the authoritative locked
    /// check in ThreadDungeonRun.TryChargePeddlerPurchase and the vendor's pre-check in ThreadPeddlerVendor.TryBuy. Pure.
    /// </summary>
    internal static class PeddlerOnePerPlayer
    {
        /// <summary>
        /// True when, for some capped entry id, the entry ids in <paramref name="bought"/> plus the amounts in
        /// <paramref name="basket"/> exceed 1. Counted by entry id alone. Long sums, so a huge amount cannot overflow.
        /// False when nothing is capped.
        /// </summary>
        public static bool Exceeds(IReadOnlySet<string> capped, IEnumerable<string> bought, IEnumerable<(string EntryId, long Amount)> basket)
        {
            if (capped == null || capped.Count == 0 || basket == null)
                return false;

            var wanted = new Dictionary<string, long>(StringComparer.Ordinal);

            foreach (var (entryId, amount) in basket)
            {
                if (entryId != null && capped.Contains(entryId))
                    wanted[entryId] = (wanted.TryGetValue(entryId, out var n) ? n : 0) + amount;
            }

            if (wanted.Count == 0)
                return false;

            foreach (var held in bought ?? Enumerable.Empty<string>())
            {
                if (held != null && wanted.ContainsKey(held))
                    wanted[held]++;
            }

            return wanted.Values.Any(n => n > 1);
        }
    }

    /// <summary>
    /// The run's Wandering Peddler as the encounter roll left it (owner rulings 2026-11-19, clear only): the parsed config and
    /// the rolled stall. Nothing exists in the world until the clear, when the first seated member who can afford something
    /// reaches delivery (ThreadPeddlerShop.Present). Immutable.
    /// </summary>
    public sealed class PeddlerPlan
    {
        public PeddlerPlan(ThreadEncounterInstance instance, PeddlerConfig config, IReadOnlyList<PeddlerOffer> offers, double spawnMultiplier)
        {
            Instance = instance;
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Offers = offers ?? throw new ArgumentNullException(nameof(offers));
            SpawnMultiplier = spawnMultiplier;

            if (Offers.Count == 0)
                throw new ArgumentException("a peddler plan needs at least one offer", nameof(offers));
        }

        /// <summary>The encounter instance the NPC is tracked under. Null only in tests.</summary>
        public ThreadEncounterInstance Instance { get; }

        public PeddlerConfig Config { get; }

        public IReadOnlyList<PeddlerOffer> Offers { get; }

        public double SpawnMultiplier { get; }

        /// <summary>The cheapest offer's FLAT price (the floor of every member's price).</summary>
        public int MinPrice => Offers.Min(o => o.Price);

        /// <summary>Does the config price offers as a percent of the buyer's pile (PeddlerConfig.PricePercent)?</summary>
        public bool HasPercentPricing => Config.PricePercent.Count > 0;

        /// <summary>One offer's price for a member whose pile basis is <paramref name="basis"/> (PeddlerEncounterType.MemberPrice). Pure.</summary>
        public int PriceFor(PeddlerOffer offer, long basis)
            => PeddlerEncounterType.MemberPrice(offer.Price, Config.PricePercent.TryGetValue(offer.EntryId, out var pct) ? pct : (double?)null, offer.JitterFactor, basis);

        /// <summary>The cheapest offer for a member with this basis: one whose balance is below it can afford nothing.</summary>
        public int MinPriceFor(long basis) => Offers.Min(o => PriceFor(o, basis));
    }

    /// <summary>What a delivery seam does for one member, from <see cref="ThreadDungeonRun.PeddlerGateFor"/>.</summary>
    internal enum PeddlerGate
    {
        /// <summary>No shop for this member (no peddler, no seat at the clear, done, or the peddler has gone): deliver as usual.</summary>
        None,

        /// <summary>The run has a peddler and this member is undecided, but the clear-time pile is not complete. Touch nothing.</summary>
        WaitPile,

        /// <summary>The pile is complete and this member has not been presented yet: present the peddler (or skip them).</summary>
        Present,

        /// <summary>This member's shop is open. Touch nothing; the release requests placement.</summary>
        Held,
    }

    /// <summary>
    /// The Wandering Peddler's per-player accounting on the pooled-loot pipeline (owner rulings 2026-11-16, made clear-only
    /// 2026-11-19). A separate partial so the account state and its invariants read in one place. Everything here is guarded
    /// by the run's private stateLock.
    ///
    /// The currency is PER PLAYER and REAL: one unit is one item in that player's own held pile that
    /// ThreadPeddlerPayment.ItemEligibleForPayment accepts (<see cref="PeddlerBalance"/>). Nothing is traded before the
    /// clear. Payment is taken AT THE BUY (owner ruling 2026-11-20, purchases hit the inventory at purchase time):
    /// <see cref="TryChargePeddlerPurchase"/> removes exactly the price in RANDOM eligible items from the pile, under the
    /// lock, and the vendor (ThreadPeddlerVendor.TryBuy) puts the purchase in the buyer's pack the same moment.
    ///
    /// Invariants this file owns:
    ///   1. <see cref="BankedLootRolls"/> only grows. The pre-clear trickle and every delivery claim drain the LEDGER,
    ///      never this counter.
    ///   2. A purchase is charged whole or not at all, only while the run is Cleared and the buyer's shop is OPEN, only by
    ///      a member seated at the clear, and only when the pile holds at least its total in eligible items; exactly the
    ///      total is removed, so the balance is never negative.
    ///   3. A member's shop goes undecided -> Open -> Done, or undecided -> Done, never back. Open -> Done happens once
    ///      (<see cref="TryMarkShopDone"/>), and only its winner requests the placement that delivers the rest of the pile.
    ///   4. The purchase record is data (entry ids). Nothing here creates or holds a WorldObject but the reference to the
    ///      one live NPC; the payment items a charge removes are handed back for the caller to destroy outside the lock.
    ///   5. At most one peddler per run: the claim latch, then one plan, then at most one NPC, ever.
    ///   6. A member never buys more than one of an entry id the plan's config caps (PeddlerConfig.OnePerPlayer), checked
    ///      under the lock with the rest of the charge.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        private enum PeddlerShopState
        {
            Open,
            Done,
        }

        // Per member: the entry ids bought, in buy order. Guarded by stateLock.
        private readonly Dictionary<uint, List<string>> peddlerBought = new Dictionary<uint, List<string>>();

        // Per member: the pile basis for percent pricing, snapshot once when their shop opens (TryOpenPeddlerShop) and
        // fixed for the visit, so prices do not fall as they spend. Guarded by stateLock.
        private readonly Dictionary<uint, long> peddlerBasis = new Dictionary<uint, long>();

        // Per member: absent = undecided, then Open, then Done (or straight to Done when skipped).
        private readonly Dictionary<uint, PeddlerShopState> peddlerShops = new Dictionary<uint, PeddlerShopState>();

        private PeddlerPlan peddlerPlan;
        private ThreadPeddlerVendor peddlerNpc;
        private bool peddlerNpcEverSet;

        // Set when the peddler leaves, or cannot appear: every undecided member is then skipped.
        private bool peddlerClosed;

        // Every ledger roll ever banked (TryAppendLootEntry), and every ledger roll ever handed out (TryClaimLootRolls,
        // the trickle's claims included). Both only grow.
        private long bankedLootRolls;
        private long claimedLootRolls;

        // Stamped once, at the Active -> Cleared transition (CheckClearedLocked): how many rolls were banked by then, and
        // who held a deal seat. The pile gate waits for exactly those rolls, so a kill banked after the clear never holds a
        // shop back; and only a member seated at the clear is offered the shop.
        private long bankedLootRollsAtClear = -1;
        private HashSet<uint> dealSeatsAtClear;

        /// <summary>Every ledger roll ever banked in this run (invariant 1). Never lowered by a claim, the trickle or a drain.</summary>
        public long BankedLootRolls { get { lock (stateLock) return bankedLootRolls; } }

        // Owner rule: at most ONE Wandering Peddler ever exists in a run. The picker's per-row cap (maxPerRun) only reads the
        // row being placed, so the type claims this latch itself before it records anything. Guarded by stateLock.
        private bool peddlerClaimed;

        /// <summary>True once a peddler has claimed this run's single peddler slot and not released it.</summary>
        public bool PeddlerClaimed { get { lock (stateLock) return peddlerClaimed; } }

        /// <summary>Claims the run's one peddler slot. False when it is already taken.</summary>
        internal bool TryClaimPeddler()
        {
            lock (stateLock)
            {
                if (peddlerClaimed)
                    return false;

                peddlerClaimed = true;
                return true;
            }
        }

        /// <summary>Gives the slot back after a Place that failed before a plan was recorded. Refused once a plan exists.</summary>
        internal void ReleasePeddlerClaim()
        {
            lock (stateLock)
            {
                if (peddlerPlan == null)
                    peddlerClaimed = false;
            }
        }

        /// <summary>Every ledger roll ever handed out by a claim (delivery or trickle).</summary>
        internal long ClaimedLootRolls { get { lock (stateLock) return claimedLootRolls; } }

        // Callers hold stateLock.
        private void CountBankedRollsLocked(ThreadLootLedgerEntry entry) => bankedLootRolls += entry.Rolls;

        private void CountClaimedRollsLocked(int rolls) => claimedLootRolls += Math.Max(0, rolls);

        /// <summary>Called from CheckClearedLocked, inside the lock, at the one Active -> Cleared transition.</summary>
        private void StampPeddlerClearLocked()
        {
            bankedLootRollsAtClear = bankedLootRolls;
            dealSeatsAtClear = new HashSet<uint>(DealSeats());
        }

        // ------------------------------------------------------------------------------------------------------
        // The plan and the one NPC
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Records the run's peddler (PeddlerEncounterType.Place). Only with the claim held, only once, and only while the run
        /// is Active. False changes nothing.
        /// </summary>
        internal bool TrySetPeddlerPlan(PeddlerPlan plan)
        {
            if (plan == null)
                return false;

            lock (stateLock)
            {
                if (!peddlerClaimed || peddlerPlan != null || state != ThreadDungeonRunState.Active)
                    return false;

                peddlerPlan = plan;
                return true;
            }
        }

        /// <summary>The run's peddler plan, or null when the run has no peddler.</summary>
        public PeddlerPlan PeddlerPlan { get { lock (stateLock) return peddlerPlan; } }

        /// <summary>The live peddler NPC, or null (not yet created, gone, or never coming).</summary>
        internal ThreadPeddlerVendor PeddlerNpc { get { lock (stateLock) return peddlerNpc; } }

        /// <summary>Has the peddler left, or been found unable to appear? No member is presented after this.</summary>
        public bool PeddlerClosed { get { lock (stateLock) return peddlerClosed; } }

        /// <summary>
        /// Records the one NPC. False when one was ever recorded before, the plan is closed, or there is no plan: the caller
        /// then destroys what it built. Invariant 5: true at most once per run.
        /// </summary>
        internal bool TrySetPeddlerNpc(ThreadPeddlerVendor npc)
        {
            if (npc == null)
                return false;

            lock (stateLock)
            {
                if (peddlerPlan == null || peddlerClosed || peddlerNpcEverSet)
                    return false;

                peddlerNpc = npc;
                peddlerNpcEverSet = true;
                return true;
            }
        }

        /// <summary>
        /// Closes the peddler (it left, or cannot appear): no member is presented after this, and every member whose shop
        /// was still open is marked Done here and returned, so the caller requests their placement. Idempotent.
        /// </summary>
        internal List<uint> ClosePeddler()
        {
            var released = new List<uint>();

            lock (stateLock)
            {
                peddlerClosed = true;
                peddlerNpc = null;

                foreach (var guid in peddlerShops.Where(kv => kv.Value == PeddlerShopState.Open).Select(kv => kv.Key).ToList())
                {
                    peddlerShops[guid] = PeddlerShopState.Done;
                    released.Add(guid);
                }
            }

            return released;
        }

        // ------------------------------------------------------------------------------------------------------
        // Per-member shop
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The hold gate for one member, read by both delivery seams after the clear pile is complete and before the pile
        /// is first delivered. A member skipped here (closed peddler, no seat at the clear) is latched Done.
        /// </summary>
        internal PeddlerGate PeddlerGateFor(uint memberGuid)
        {
            lock (stateLock)
            {
                if (peddlerPlan == null)
                    return PeddlerGate.None;

                if (peddlerShops.TryGetValue(memberGuid, out var shop))
                    return shop == PeddlerShopState.Open ? PeddlerGate.Held : PeddlerGate.None;

                if (state != ThreadDungeonRunState.Cleared)
                    return PeddlerGate.None;

                if (peddlerClosed || dealSeatsAtClear == null || !dealSeatsAtClear.Contains(memberGuid))
                {
                    peddlerShops[memberGuid] = PeddlerShopState.Done;
                    return PeddlerGate.None;
                }

                return PeddlerClearPileCompleteLocked ? PeddlerGate.Present : PeddlerGate.WaitPile;
            }
        }

        /// <summary>Undecided -> Open: only for a member seated at the clear, while the run is Cleared, the clear pile is complete and the peddler is not closed.</summary>
        internal bool TryOpenPeddlerShop(uint memberGuid)
        {
            lock (stateLock)
            {
                if (peddlerPlan == null || peddlerClosed || state != ThreadDungeonRunState.Cleared || peddlerShops.ContainsKey(memberGuid)
                    || dealSeatsAtClear == null || !dealSeatsAtClear.Contains(memberGuid) || !PeddlerClearPileCompleteLocked)
                    return false;

                peddlerShops[memberGuid] = PeddlerShopState.Open;

                // The basis: the payment-eligible items in their pile, before any buy (nothing can be charged before Open).
                members.TryGetValue(memberGuid, out var ms);
                peddlerBasis[memberGuid] = BalanceLocked(ms);
                return true;
            }
        }

        /// <summary>The member's pile basis, snapshot when their shop opened; null before that.</summary>
        internal long? PeddlerBasis(uint memberGuid)
        {
            lock (stateLock)
                return peddlerBasis.TryGetValue(memberGuid, out var basis) ? basis : (long?)null;
        }

        /// <summary>
        /// The member's price for the plan's offer of <paramref name="entryId"/>: PeddlerPlan.PriceFor at their basis snapshot
        /// (0 before their shop opened, which is the flat price). Null without a plan or such an offer. Server-side only:
        /// this is what a buy charges and what their displays show.
        /// </summary>
        internal int? PeddlerPriceFor(uint memberGuid, string entryId)
        {
            lock (stateLock)
            {
                var offer = peddlerPlan?.Offers.FirstOrDefault(o => string.Equals(o.EntryId, entryId, StringComparison.Ordinal));

                if (offer == null)
                    return null;

                peddlerBasis.TryGetValue(memberGuid, out var basis);
                return peddlerPlan.PriceFor(offer, basis);
            }
        }

        /// <summary>Undecided -> Done without a shop (nothing affordable, or the peddler cannot appear). True when it changed.</summary>
        internal bool MarkPeddlerShopSkipped(uint memberGuid)
        {
            lock (stateLock)
            {
                if (peddlerShops.ContainsKey(memberGuid))
                    return false;

                peddlerShops[memberGuid] = PeddlerShopState.Done;
                return true;
            }
        }

        /// <summary>
        /// Invariant 3: Open -> Done, exactly once. Every done source (walk-away, Yes, /spawncache, logout or leaving, idle,
        /// the peddler leaving) goes through here, and only the caller that gets true requests the placement.
        /// </summary>
        public bool TryMarkShopDone(uint memberGuid)
        {
            lock (stateLock)
            {
                if (!peddlerShops.TryGetValue(memberGuid, out var shop) || shop != PeddlerShopState.Open)
                    return false;

                peddlerShops[memberGuid] = PeddlerShopState.Done;
                return true;
            }
        }

        /// <summary>Is this member's shop open (presented, not yet done)?</summary>
        public bool IsPeddlerShopOpen(uint memberGuid)
        {
            lock (stateLock)
                return peddlerShops.TryGetValue(memberGuid, out var shop) && shop == PeddlerShopState.Open;
        }

        /// <summary>Is any member's shop open right now?</summary>
        internal bool AnyPeddlerShopOpen
        {
            get
            {
                lock (stateLock)
                    return peddlerShops.Values.Any(v => v == PeddlerShopState.Open);
            }
        }

        /// <summary>Has this member's shop been decided as done (shopped and finished, or skipped)?</summary>
        public bool IsPeddlerShopDone(uint memberGuid)
        {
            lock (stateLock)
                return peddlerShops.TryGetValue(memberGuid, out var shop) && shop == PeddlerShopState.Done;
        }

        /// <summary>The members seated at the clear (empty before the clear).</summary>
        internal IReadOnlyList<uint> PeddlerSeatsAtClear()
        {
            lock (stateLock)
                return dealSeatsAtClear?.ToList() ?? new List<uint>();
        }

        // ------------------------------------------------------------------------------------------------------
        // Balance and charge
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The buyer's balance in peddler units, the ONLY balance read: the eligible items in their held pile
        /// (ThreadPeddlerPayment.ItemEligibleForPayment). A buy removes its payment from the pile, so this is already net of
        /// every purchase. 0 for a run without pooled loot, or a guid that is not a member.
        /// </summary>
        public long PeddlerBalance(uint memberGuid)
        {
            if (members == null || !members.TryGetValue(memberGuid, out var ms))
                return 0;

            lock (stateLock)
                return BalanceLocked(ms);
        }

        private long BalanceLocked(MemberState ms)
        {
            if (!pooledLoot || ms == null)
                return 0;

            return ms.HeldPile.Count(ThreadPeddlerPayment.ItemEligibleForPayment);
        }

        /// <summary>
        /// Invariant 2. Charges the whole basket or none of it, under one lock: the run must be Cleared, the buyer's shop
        /// open, the buyer seated at the clear, the one-per-player cap respected (invariant 6: bought plus basket at most 1,
        /// by entry id, else <see cref="PeddlerChargeOutcome.LimitReached"/>), and the pile must hold at least the basket's
        /// total in eligible items. Then exactly that many items, chosen by <paramref name="choosePayment"/> from the
        /// CURRENT eligible pile items, are removed from the pile and handed back in <paramref name="removed"/> for the
        /// caller to destroy outside the lock, and each purchase's entry id is recorded as bought. A purchase may repeat an
        /// entry any number of times (stock is never consumed) except a capped one. Returns the outcome and the balance
        /// after it; on any refusal nothing changed and <paramref name="removed"/> is empty.
        /// </summary>
        public PeddlerChargeResult TryChargePeddlerPurchase(uint memberGuid, IReadOnlyList<PeddlerPurchase> basket,
            Func<IReadOnlyList<HeldPileItem>, int, IReadOnlyList<HeldPileItem>> choosePayment, out List<HeldPileItem> removed)
        {
            removed = new List<HeldPileItem>();

            MemberState ms = null;
            members?.TryGetValue(memberGuid, out ms);

            lock (stateLock)
            {
                if (state != ThreadDungeonRunState.Cleared || !peddlerShops.TryGetValue(memberGuid, out var shop) || shop != PeddlerShopState.Open)
                    return new PeddlerChargeResult(PeddlerChargeOutcome.ShopNotOpen, BalanceLocked(ms));

                if (ms == null || dealSeatsAtClear == null || !dealSeatsAtClear.Contains(memberGuid))
                    return new PeddlerChargeResult(PeddlerChargeOutcome.NoSeat, BalanceLocked(ms));

                if (basket == null || basket.Count == 0 || basket.Any(p => p == null || string.IsNullOrEmpty(p.EntryId) || p.Price < 1) || choosePayment == null)
                    return new PeddlerChargeResult(PeddlerChargeOutcome.Invalid, BalanceLocked(ms));

                peddlerBought.TryGetValue(memberGuid, out var bought);

                if (PeddlerOnePerPlayer.Exceeds(peddlerPlan?.Config.OnePerPlayer, bought, basket.Select(p => (p.EntryId, 1L))))
                    return new PeddlerChargeResult(PeddlerChargeOutcome.LimitReached, BalanceLocked(ms));

                var candidates = ms.HeldPile.Where(ThreadPeddlerPayment.ItemEligibleForPayment).ToList();
                var total = basket.Sum(p => (long)p.Price);

                if (total > candidates.Count)
                    return new PeddlerChargeResult(PeddlerChargeOutcome.Insufficient, candidates.Count);

                var chosen = choosePayment(candidates, (int)total) ?? Array.Empty<HeldPileItem>();

                // Exactly the price, every one a distinct current eligible pile item, or nothing at all.
                var eligible = new HashSet<HeldPileItem>(candidates);

                if (chosen.Count != total || chosen.Distinct().Count() != chosen.Count || chosen.Any(h => h == null || !eligible.Contains(h)))
                    return new PeddlerChargeResult(PeddlerChargeOutcome.Invalid, candidates.Count);

                foreach (var held in chosen)
                {
                    ms.HeldPile.Remove(held);
                    removed.Add(held);
                }

                if (bought == null)
                    peddlerBought[memberGuid] = bought = new List<string>();

                bought.AddRange(basket.Select(p => p.EntryId));

                return new PeddlerChargeResult(PeddlerChargeOutcome.Charged, BalanceLocked(ms));
            }
        }

        /// <summary>The entry ids this buyer has bought, in buy order (empty before their first buy).</summary>
        public IReadOnlyList<string> PeddlerBought(uint memberGuid)
        {
            lock (stateLock)
                return peddlerBought.TryGetValue(memberGuid, out var bought) ? bought.ToList() : new List<string>();
        }

        // ------------------------------------------------------------------------------------------------------
        // The clear pile
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Is every roll banked by the clear already handed out of the ledger, and nothing built-but-unpiled left (the
        /// pending boss bonus, pooled bonus rolls, overflow, the deal buffers and the prebuilt store)? This is the group
        /// form of the solo placer's snapshot gate (ThreadCachePlacer.Execute, ledgerClaimed &lt; ledgerSnapshot ||
        /// HasUndealtNonLedgerLoot): the ledger is FIFO, so the at-clear rolls are the first ones claimed, and a kill
        /// banked after the clear never re-arms it. False before the clear.
        /// </summary>
        internal bool PeddlerClearPileComplete
        {
            get
            {
                lock (stateLock)
                    return PeddlerClearPileCompleteLocked;
            }
        }

        // Callers hold stateLock (the lock is re-entrant, and HasUndealtNonLedgerLoot takes it too).
        private bool PeddlerClearPileCompleteLocked
            => bankedLootRollsAtClear >= 0 && claimedLootRolls >= bankedLootRollsAtClear && !HasUndealtNonLedgerLoot;

        /// <summary>
        /// Is a seated member's presentation waiting only on the deal buffer? True when every roll banked by the clear has
        /// already been claimed from the ledger and a member seated at the clear is still undecided while the peddler can
        /// appear. ThreadGroupCacheDelivery.BufferAndFlush then hands the ledger buffer out without waiting for its floor,
        /// so a stream of kills after the clear (which keeps the ledger non-empty) cannot hold a shop back indefinitely.
        /// </summary>
        internal bool PeddlerDebtorWaiting
        {
            get
            {
                lock (stateLock)
                {
                    if (bankedLootRollsAtClear < 0 || claimedLootRolls < bankedLootRollsAtClear)
                        return false;

                    return peddlerPlan != null && !peddlerClosed && dealSeatsAtClear != null && dealSeatsAtClear.Any(g => !peddlerShops.ContainsKey(g));
                }
            }
        }
    }
}
