using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// The pooled-loot half of a run (Docs/Threads/POOLED-LOOT-CACHE-DESIGN.md sections 1, 3 and 4). A
    /// separate partial file so the pool's state and its invariants read in one place. Every member here is
    /// guarded by the same private stateLock as the rest of the run: kills bank from landblock threads,
    /// delivery runs on the run landblock's queue, and EndRun can arrive from the world thread or from another
    /// landblock's thread.
    ///
    /// Invariants this file owns:
    ///   1. Every ledger roll is handed out once: TryClaimLootRolls hands out the head entry's rolls under the
    ///      lock (whole, or a slice at a time), and nothing here can put a roll back.
    ///   3. One delivery at a time: TryBeginCachePlacement hands out a token; a stale token (its landblock
    ///      action was dropped by an unload) is superseded after CachePlacementStaleAfter and can neither run
    ///      nor release its successor.
    ///   5. Once Ended, nothing is accepted: appends, overflow adds, deal-buffer adds, cache registrations and latches are
    ///      refused, so the caller destroys the object instead of stranding it. DrainLootPool hands the
    ///      out-of-world objects to ThreadLootPool.DisposeForRunEnd.
    /// </summary>
    public sealed partial class ThreadDungeonRun
    {
        public static readonly TimeSpan CachePlacementStaleAfter = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan SpawnCacheCooldown = TimeSpan.FromSeconds(5);

        private bool pooledLootStamped;
        private bool pooledLoot;

        private readonly Queue<ThreadLootLedgerEntry> lootLedger = new Queue<ThreadLootLedgerEntry>();
        private readonly List<(WorldObject Item, string RareFinderName)> lootOverflow = new List<(WorldObject Item, string RareFinderName)>();
        private readonly List<Container> placedCaches = new List<Container>();
        private bool lootBonusPending;

        private long placementToken;
        private long placementTokenSeq;
        private DateTime placementStartedUtc;
        private bool placementWanted;
        private bool cacheSortWanted;
        private uint cacheSortResume;
        private DateTime lastSpawnCacheUtc = DateTime.MinValue;

        /// <summary>
        /// True when this run delivers loot through Thread Caches instead of corpses. False until
        /// <see cref="MarkPooledLoot"/> has run, so a run that never reached populate reads as the corpse model.
        /// </summary>
        public bool PooledLoot { get { lock (stateLock) return pooledLoot; } }

        /// <summary>
        /// Write-once. Stamped by ThreadDungeonSpawner.TryPopulate before the first creature is placed, so no
        /// kill in this run can land under the other model. A mid-run flip of the tunable reaches the next run.
        /// </summary>
        public void MarkPooledLoot(bool enabled)
        {
            lock (stateLock)
            {
                if (pooledLootStamped) return;
                pooledLootStamped = true;
                pooledLoot = enabled;
            }
        }

        /// <summary>
        /// Banks one kill. Accepted in Starting, Active and Cleared (late kills pool into the cache, ruling
        /// 2026-09-14) - deliberately NOT RecordKill's state gate, which closes at Cleared. False for a run
        /// that is not pooled or has Ended; the caller then owns the entry's held rare.
        /// </summary>
        public bool TryAppendLootEntry(ThreadLootLedgerEntry entry)
        {
            lock (stateLock)
            {
                if (entry == null || !pooledLoot || state == ThreadDungeonRunState.Ended) return false;
                lootLedger.Enqueue(entry);
                return true;
            }
        }

        public int LedgerCount { get { lock (stateLock) return lootLedger.Count; } }

        /// <summary>
        /// The boss bonus, pooled. Shares the corpse model's bossChestClaimed latch so a run can never hold
        /// both a boss-death cache and a pooled bonus, and a second boss death adds nothing.
        /// </summary>
        public bool TryMarkLootBonusPending()
        {
            lock (stateLock)
            {
                if (!pooledLoot || state == ThreadDungeonRunState.Ended || bossChestClaimed) return false;
                bossChestClaimed = true;
                lootBonusPending = true;
                return true;
            }
        }

        public bool IsLootBonusPending { get { lock (stateLock) return lootBonusPending; } }

        /// <summary>Hands the pending bonus to exactly one filler.</summary>
        public bool TryClaimLootBonus()
        {
            lock (stateLock)
            {
                if (!lootBonusPending) return false;
                lootBonusPending = false;
                return true;
            }
        }

        /// <summary>Invariant 1: removes the oldest entry (kill order) and hands out all of its remaining rolls; it is never returned.</summary>
        public bool TryClaimNextLootEntry(out ThreadLootLedgerEntry entry) => TryClaimLootRolls(int.MaxValue, false, out entry);

        /// <summary>
        /// Invariant 1 with a roll budget (Group Threads final review F3): removes and hands out the oldest entry only
        /// when its remaining rolls are at most <paramref name="maxRolls"/>. An entry over the budget stays at the head,
        /// in kill order, for the next deal. Checked and dequeued under one lock.
        /// </summary>
        public bool TryClaimNextLootEntry(int maxRolls, out ThreadLootLedgerEntry entry) => TryClaimLootRolls(maxRolls, false, out entry);

        /// <summary>
        /// Invariant 1, per ROLL rather than per entry (the fat-entry fix, 2026-09-18). Hands out up to
        /// <paramref name="maxRolls"/> rolls of the oldest ledger entry, in kill order, under one lock:
        ///   - the whole rest of the entry when it fits: the entry is dequeued, and what is returned is the entry
        ///     itself when none of it had been handed out before, else its final slice (which carries the held rare);
        ///   - otherwise, only when <paramref name="allowPartial"/>, a slice of exactly <paramref name="maxRolls"/>
        ///     rolls, with the entry left at the head holding the rest (and its rare) for the next claim;
        ///   - otherwise nothing.
        /// Every roll is therefore handed out exactly once, and a held rare exactly once, with the last slice. A
        /// part-claimed entry still counts in <see cref="LedgerCount"/> and <see cref="HasUnclaimedLoot"/>, and its
        /// rare is destroyed by <see cref="DrainLootPool"/> like any unclaimed entry's.
        /// </summary>
        internal bool TryClaimLootRolls(int maxRolls, bool allowPartial, out ThreadLootLedgerEntry piece)
        {
            lock (stateLock)
            {
                piece = null;

                if (lootLedger.Count == 0 || maxRolls < 1)
                    return false;

                var head = lootLedger.Peek();
                var remaining = head.RollsRemaining;

                if (remaining <= maxRolls)
                {
                    lootLedger.Dequeue();
                    piece = remaining == head.Rolls ? head : head.Slice(remaining, final: true);
                    return true;
                }

                if (!allowPartial)
                    return false;

                head.RollsRemaining = remaining - maxRolls;
                piece = head.Slice(maxRolls, final: false);
                return true;
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Group Threads: the pooled half of the boss bonus, built across delivery steps
        // ------------------------------------------------------------------------------------------------------

        private int pooledBonusRollsPending;
        private double pooledBonusFactor;

        /// <summary>
        /// Records the pooled (Legendary-table) half of a group boss bonus as <paramref name="rolls"/> rolls still to
        /// build, at <paramref name="factor"/>. Called once, by the delivery step that won TryClaimLootBonus, so the
        /// latch stays one-shot while the rolls themselves are built a few at a time by later steps. The rolls are not
        /// objects yet, so nothing can be stranded: a run that ends first simply never builds them (DrainLootPool).
        /// False once Ended, or for a non-positive count.
        /// </summary>
        internal bool TrySetPooledBonusRolls(int rolls, double factor)
        {
            lock (stateLock)
            {
                if (rolls <= 0 || state == ThreadDungeonRunState.Ended) return false;
                pooledBonusRollsPending += rolls;
                pooledBonusFactor = factor;
                return true;
            }
        }

        /// <summary>Takes up to <paramref name="maxRolls"/> of the pending pooled bonus rolls, and the factor they were counted at.</summary>
        internal bool TryClaimPooledBonusRolls(int maxRolls, out int rolls, out double factor)
        {
            lock (stateLock)
            {
                rolls = 0;
                factor = pooledBonusFactor;

                if (pooledBonusRollsPending <= 0 || maxRolls < 1)
                    return false;

                rolls = Math.Min(maxRolls, pooledBonusRollsPending);
                pooledBonusRollsPending -= rolls;
                return true;
            }
        }

        /// <summary>Boss-bonus Legendary rolls still to build: a group run's pooled half, or (since 2026-09-19) a solo run's.</summary>
        public int PooledBonusRollsPending { get { lock (stateLock) return pooledBonusRollsPending; } }

        // ------------------------------------------------------------------------------------------------------
        // Group Threads: the deal buffer (code review of #1212, finding 2)
        // ------------------------------------------------------------------------------------------------------

        // Materialised group loot that no deal has handed out yet. A timed delivery step builds only a few rolls, and
        // the value-sorted snake deal balances only WITHIN the set it deals, so dealing every step would shrink that
        // set to 1-3 items and all but lose the balancing. ThreadGroupCacheDelivery.Deal buffers what a step builds
        // here and hands it out as one set once enough has accumulated (see ThreadGroupCacheDelivery.DealFloor).
        //
        // Conservation follows the held-rare pattern: an item is here or in a pile, never both. It is counted in
        // HasUndealtLoot (so GroupLootOwed, HasUnclaimedLoot and the Tick retry all see it), refused once Ended (the
        // caller destroys it), taken out under the lock exactly once, and destroyed by the run-end drain.
        private readonly List<(WorldObject Item, string RareFinderName)> dealBuffer = new List<(WorldObject Item, string RareFinderName)>();
        private int dealBufferRolls;

        // The pooled boss half, buffered apart from the ledger so it is dealt as ONE set when its build completes,
        // as it was before its build became resumable.
        private readonly List<(WorldObject Item, string RareFinderName)> pooledDealBuffer = new List<(WorldObject Item, string RareFinderName)>();

        /// <summary>
        /// Buffers built-but-undealt items for a later deal: <paramref name="pooled"/> picks the pooled boss-half buffer,
        /// else the ledger buffer, which also records <paramref name="rolls"/> claimed ledger rolls toward the deal
        /// floor. False once Ended, with nothing buffered: the caller then destroys every item.
        /// </summary>
        internal bool TryBufferForDeal(IReadOnlyCollection<(WorldObject Item, string RareFinderName)> items, int rolls, bool pooled)
        {
            lock (stateLock)
            {
                if (state == ThreadDungeonRunState.Ended) return false;

                var into = pooled ? pooledDealBuffer : dealBuffer;

                foreach (var item in items)
                {
                    if (item.Item != null)
                        into.Add(item);
                }

                if (!pooled)
                    dealBufferRolls += Math.Max(0, rolls);

                return true;
            }
        }

        /// <summary>Takes the chosen buffers, ledger first then pooled, and resets them. Each item is handed out exactly once.</summary>
        internal List<(WorldObject Item, string RareFinderName)> TakeDealBuffer(bool ledger, bool pooled)
        {
            lock (stateLock)
            {
                var taken = new List<(WorldObject Item, string RareFinderName)>();

                if (ledger)
                {
                    taken.AddRange(dealBuffer);
                    dealBuffer.Clear();
                    dealBufferRolls = 0;
                }

                if (pooled)
                {
                    taken.AddRange(pooledDealBuffer);
                    pooledDealBuffer.Clear();
                }

                return taken;
            }
        }

        /// <summary>Ledger rolls whose items sit in the deal buffer, toward ThreadGroupCacheDelivery.DealFloor.</summary>
        internal int DealBufferRolls { get { lock (stateLock) return dealBufferRolls; } }

        /// <summary>Items in both deal buffers.</summary>
        internal int DealBufferCount { get { lock (stateLock) return dealBuffer.Count + pooledDealBuffer.Count; } }

        // ------------------------------------------------------------------------------------------------------
        // Prebuilt loot: ledger rolls materialised ahead of the clear by the trickle (ThreadLootTrickle)
        // ------------------------------------------------------------------------------------------------------

        // Claimed ledger pieces whose items the trickle has already built, in CLAIM ORDER, which is kill order: the
        // trickle claims the head of the ledger exactly as delivery does (ThreadLootRollBudget.TryClaimNext, one roll
        // at a time, the held rare riding only the final slice). Delivery takes these BEFORE the ledger, so kill order
        // across the two stores is unchanged, and hands each piece to the same code a fresh claim reaches - only the
        // materialisation has moved earlier.
        //
        // Conservation follows the held-rare / deal-buffer pattern: a built object is here or downstream, never both.
        // It counts as owed loot (AnyBufferedForDeal, so HasUnclaimedLoot, HasUndealtLoot, GroupLootOwed and every
        // retry see it), is refused once Ended (the caller destroys it), is taken out under the lock exactly once,
        // and is destroyed and reported by the run-end drain.
        private readonly Queue<PrebuiltLoot> prebuiltLoot = new Queue<PrebuiltLoot>();
        private int prebuiltObjects;

        /// <summary>
        /// Stores one trickle-built piece. False once Ended, with nothing stored: the caller destroys the items and the
        /// piece's held rare.
        /// </summary>
        internal bool TryAddPrebuilt(ThreadLootLedgerEntry piece, List<WorldObject> items)
        {
            if (piece == null)
                return false;

            var built = new PrebuiltLoot(piece, items);

            lock (stateLock)
            {
                if (state == ThreadDungeonRunState.Ended) return false;

                prebuiltLoot.Enqueue(built);
                prebuiltObjects += built.ObjectCount;
                return true;
            }
        }

        /// <summary>Takes the oldest prebuilt piece (kill order). Each piece is handed out exactly once.</summary>
        internal bool TryTakePrebuilt(out PrebuiltLoot built)
        {
            lock (stateLock)
            {
                built = null;

                if (prebuiltLoot.Count == 0)
                    return false;

                built = prebuiltLoot.Dequeue();
                prebuiltObjects -= built.ObjectCount;
                return true;
            }
        }

        /// <summary>Prebuilt pieces waiting for delivery.</summary>
        internal int PrebuiltCount { get { lock (stateLock) return prebuiltLoot.Count; } }

        /// <summary>Out-of-world objects the prebuilt store holds (items plus any held rare not among them): what the trickle's cap counts.</summary>
        internal int PrebuiltObjects { get { lock (stateLock) return prebuiltObjects; } }

        /// <summary>Appends a materialised item no cache took. False once Ended: the caller destroys it.</summary>
        public bool TryAddOverflow(WorldObject item, string rareFinderName)
        {
            lock (stateLock)
            {
                if (item == null || state == ThreadDungeonRunState.Ended) return false;
                lootOverflow.Add((item, rareFinderName));
                return true;
            }
        }

        /// <summary>Removes and returns every overflow item in order. The caller must place or re-add each one.</summary>
        public List<(WorldObject Item, string RareFinderName)> TakeAllOverflow()
        {
            lock (stateLock)
            {
                var taken = new List<(WorldObject Item, string RareFinderName)>(lootOverflow);
                lootOverflow.Clear();
                return taken;
            }
        }

        public int OverflowCount { get { lock (stateLock) return lootOverflow.Count; } }

        /// <summary>
        /// Spec section 4: ledger non-empty OR bonus pending OR overflow non-empty. Group Threads (Task 10): OR any
        /// roster member holds an undelivered pile. A solo run never deals a pile, so its answer is unchanged; a group
        /// run whose ledger is fully dealt still has loot to deliver, and TriggerPooledLoot's gate must see it (the
        /// arrival and Tick retries would otherwise return NotApplicable with a pile still held).
        ///
        /// Also OR any member flagged delivery-wanted. A flag normally comes with a pile, but the arrival hook flags a
        /// member when its request finds the latch Busy, and the pass holding the latch can deliver that member's pile
        /// first. Without this clause the leftover flag would make the Tick's group retry ask every second and get
        /// NotApplicable forever; with it, the retry runs one pass, which clears the flag. Solo runs never flag a member.
        /// </summary>
        public bool HasUnclaimedLoot { get { lock (stateLock) return lootLedger.Count > 0 || lootBonusPending || pooledBonusRollsPending > 0 || lootOverflow.Count > 0 || AnyBufferedForDeal || AnyHeldPile || AnyMemberDeliveryWanted; } }

        /// <summary>Is any roster member flagged delivery-wanted (ruling R20)?</summary>
        internal bool AnyMemberDeliveryWanted
        {
            get
            {
                lock (stateLock)
                    return members != null && members.Values.Any(s => s.DeliveryWanted);
            }
        }

        /// <summary>
        /// Group Threads (Task 10): is there anything this member could still receive? The shared pool (ledger, pending
        /// bonus, overflow), which the next deal hands out, OR this member's own held pile. Another member's pile does
        /// not count.
        /// </summary>
        public bool HasUnclaimedLootFor(uint memberGuid)
        {
            lock (stateLock)
            {
                if (lootLedger.Count > 0 || lootBonusPending || pooledBonusRollsPending > 0 || lootOverflow.Count > 0 || AnyBufferedForDeal)
                    return true;
            }

            return HasHeldPile(memberGuid);
        }

        // ------------------------------------------------------------------------------------------------------
        // Group Threads: who picks first in each dealt set (user ruling on #1212, 2026-09-18)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The range a run's snake offset is drawn from: 720720 = lcm(1..16), so the offset modulo ANY seat count up to
        /// 16 is exactly uniform (a range that is not a multiple of the seat count would favour the low seats).
        /// </summary>
        internal const int SnakeRunOffsetRange = 720720;

        /// <summary>
        /// Test seam: the random source a run's snake offset is drawn from, returning a value in [0, SnakeRunOffsetRange).
        /// Production is ThreadSafeRandom. Tests that pin seats either set one run's offset (<see cref="SetSnakeRunOffset"/>)
        /// or swap this for a seeded source and restore it.
        /// </summary>
        internal static Func<int> SnakeRunOffsetSource = () => ACE.Common.ThreadSafeRandom.Next(0, SnakeRunOffsetRange - 1);

        private int snakeRunOffset = -1;
        private int snakeSetIndex;

        /// <summary>
        /// Group Threads: reserves the next dealt set and returns the deal-seat index that takes its FIRST pick (the top
        /// value item): (runOffset + setIndex) mod <paramref name="seats"/>. Every set is dealt as a fresh snake from that
        /// seat, so the first pick rotates through the seats one set at a time.
        ///
        /// This replaced a cursor that advanced by the set's SIZE modulo 2 x seats (Task 10). With the 12-item sets the
        /// deal buffer produces, 12 is a multiple of that period at 2, 3 and 6 seats, so every set started at the same
        /// position and one seat - seat 0, the owner, since the cursor started at 0 - took the top item of every set.
        ///
        /// runOffset is drawn once per run, on the first reservation, from <see cref="SnakeRunOffsetSource"/>, so the
        /// owner is not systematically first in a short clear. setIndex counts sets, not items, and a seat count that
        /// changes mid-run (a key released) is simply the modulus of later sets. Fewer than one seat returns 0 and
        /// reserves nothing.
        /// </summary>
        internal int ReserveSnakeSet(int seats)
        {
            lock (stateLock)
            {
                if (seats < 1)
                    return 0;

                if (snakeRunOffset < 0)
                    snakeRunOffset = NormalizeSnakeOffset(SnakeRunOffsetSource?.Invoke() ?? 0);

                var first = (int)((snakeRunOffset + (long)snakeSetIndex) % seats);
                snakeSetIndex++;
                return first;
            }
        }

        /// <summary>Fixes this run's snake offset before its first set is reserved (tests, and any caller that must be deterministic). Ignored once drawn.</summary>
        internal void SetSnakeRunOffset(int offset)
        {
            lock (stateLock)
            {
                if (snakeRunOffset < 0)
                    snakeRunOffset = NormalizeSnakeOffset(offset);
            }
        }

        /// <summary>This run's snake offset, or -1 before its first set was reserved.</summary>
        internal int SnakeRunOffset { get { lock (stateLock) return snakeRunOffset; } }

        private static int NormalizeSnakeOffset(int value)
        {
            var offset = value % SnakeRunOffsetRange;
            return offset < 0 ? offset + SnakeRunOffsetRange : offset;
        }
        /// <summary>The shared pool only (ledger, pending bonus, overflow, the deal buffers), without any member's held pile.</summary>
        internal bool HasUndealtLoot { get { lock (stateLock) return lootLedger.Count > 0 || lootBonusPending || pooledBonusRollsPending > 0 || lootOverflow.Count > 0 || AnyBufferedForDeal; } }

        /// <summary>
        /// HasUndealtLoot minus the ledger clause (item 1a fix, 2026-09-22). ThreadCachePlacer.Execute's solo build
        /// snapshots run.LedgerCount before it starts building, so a late kill's entry - appended to the BACK of the
        /// FIFO ledger after that snapshot was taken - never re-arms the "still waiting" gate for a segment that has
        /// already been fully built; it waits for the run's next placement pass instead. The pending boss bonus,
        /// pooled bonus rolls, shared overflow and the deal buffers (prebuilt trickle loot included) are never fed by
        /// a late KILL the way the ledger is, so they still gate on the WHOLE pending amount, not a snapshot of it.
        /// </summary>
        internal bool HasUndealtNonLedgerLoot { get { lock (stateLock) return lootBonusPending || pooledBonusRollsPending > 0 || lootOverflow.Count > 0 || AnyBufferedForDeal; } }

        // Callers hold stateLock. Built but not yet handed out: both deal buffers and the prebuilt store (solo runs use
        // only the latter).
        private bool AnyBufferedForDeal => dealBuffer.Count > 0 || pooledDealBuffer.Count > 0 || prebuiltLoot.Count > 0;

        /// <summary>
        /// Invariant 3. Returns a non-zero token to the one caller admitted, 0 otherwise. A held latch older
        /// than <see cref="CachePlacementStaleAfter"/> is taken over: Landblock.Unload clears the action queue,
        /// so the action that would have released it may never run.
        /// </summary>
        public long TryBeginCachePlacement(DateTime nowUtc)
        {
            lock (stateLock)
            {
                if (state == ThreadDungeonRunState.Ended) return 0;
                if (placementToken != 0 && nowUtc - placementStartedUtc < CachePlacementStaleAfter) return 0;
                placementToken = ++placementTokenSeq;
                placementStartedUtc = nowUtc;
                return placementToken;
            }
        }

        public bool IsCachePlacementCurrent(long token) { lock (stateLock) return token != 0 && placementToken == token; }

        /// <summary>
        /// Invariant 3, for a delivery that spans several landblock actions. ThreadCachePlacer holds ONE token
        /// across a whole chain of batch steps rather than re-latching per step, so that nothing can interleave
        /// with it and break the ledger's kill order; re-stamping the start time each step is what stops the
        /// <see cref="CachePlacementStaleAfter"/> takeover from firing on a chain that is still running. Only the
        /// token that holds the latch may re-stamp it, so a superseded token cannot revive itself.
        /// </summary>
        public void RefreshCachePlacement(long token, DateTime nowUtc)
        {
            lock (stateLock)
            {
                if (token != 0 && placementToken == token)
                    placementStartedUtc = nowUtc;
            }
        }

        /// <summary>Releases the latch only for the token that holds it.</summary>
        public void EndCachePlacement(long token)
        {
            lock (stateLock)
            {
                if (token != 0 && placementToken == token)
                    placementToken = 0;
            }
        }

        public bool IsCachePlacementInProgress(DateTime nowUtc)
        {
            lock (stateLock) return placementToken != 0 && nowUtc - placementStartedUtc < CachePlacementStaleAfter;
        }

        /// <summary>Set when delivery found loot but the owner was not inside; ThreadDungeonManager.Tick retries on sight.</summary>
        public void MarkPlacementWanted() { lock (stateLock) placementWanted = true; }

        public bool IsPlacementWanted { get { lock (stateLock) return placementWanted; } }

        public bool TryClaimPlacementWanted()
        {
            lock (stateLock)
            {
                if (!placementWanted) return false;
                placementWanted = false;
                return true;
            }
        }

        /// <summary>
        /// Set when a display sort (ThreadCacheSort) left work behind: a cache still needing a sort that a player
        /// had OPEN, or one past the pass's own cache cap. ThreadDungeonManager.Tick retries on the flag. Which
        /// caches still NEED work is not this flag's job - that is the per-cache signature record below - so the
        /// flag clears on the first pass that finds nothing left, and the Tick stops enqueuing.
        /// </summary>
        public void MarkCacheSortWanted() { lock (stateLock) cacheSortWanted = true; }

        public bool IsCacheSortWanted { get { lock (stateLock) return cacheSortWanted; } }

        /// <summary>
        /// Claims the flag, as <see cref="TryClaimPlacementWanted"/> does. A pass that still finds an open cache
        /// marks it again itself, so nothing is lost by claiming first.
        /// </summary>
        public bool TryClaimCacheSortWanted()
        {
            lock (stateLock)
            {
                if (!cacheSortWanted) return false;
                cacheSortWanted = false;
                return true;
            }
        }

        /// <summary>
        /// The GUID of the last cache a display-sort pass renumbered, or 0 for "start at the head". A pass is
        /// capped at ThreadCacheSort.MaxCachesPerSortPass caches, so this is what makes successive passes
        /// cover DIFFERENT caches instead of re-sorting the same head of the list forever. Keyed on the GUID
        /// rather than an index into placedCaches because caches are registered and destroyed between passes;
        /// a GUID that is no longer placed simply falls back to the head.
        /// </summary>
        public uint CacheSortResume { get { lock (stateLock) return cacheSortResume; } }

        public void SetCacheSortResume(uint cacheGuid) { lock (stateLock) cacheSortResume = cacheGuid; }

        public bool SpawnCacheCooldownElapsed(DateTime nowUtc) { lock (stateLock) return nowUtc - lastSpawnCacheUtc >= SpawnCacheCooldown; }

        public void StampSpawnCacheCooldown(DateTime nowUtc) { lock (stateLock) lastSpawnCacheUtc = nowUtc; }

        /// <summary>Records a cache that is in the world. False for null, a duplicate, or an Ended run.</summary>
        public bool TryRegisterCache(Container cache)
        {
            lock (stateLock)
            {
                if (cache == null || state == ThreadDungeonRunState.Ended || placedCaches.Contains(cache)) return false;
                placedCaches.Add(cache);
                return true;
            }
        }

        public void UnregisterCache(Container cache)
        {
            lock (stateLock)
                placedCaches.Remove(cache);
        }

        /// <summary>The live placed caches, oldest first. Destroyed ones are dropped from the run as a side effect.</summary>
        public List<Container> PlacedCachesSnapshot()
        {
            lock (stateLock)
            {
                placedCaches.RemoveAll(c => c.IsDestroyed);
                return placedCaches.ToList();
            }
        }

        /// <summary>
        /// Invariant 5, the state half. Empties the pool and hands back the out-of-world objects (overflow and
        /// the held rares of unclaimed entries) for the caller to destroy. Placed caches are forgotten, not
        /// returned: they are in the copy and die with it.
        /// </summary>
        public LootPoolDrain DrainLootPool()
        {
            lock (stateLock)
            {
                var drain = new LootPoolDrain(lootLedger.Count, lootBonusPending, placedCaches.Count) { PooledBonusRolls = pooledBonusRollsPending };

                foreach (var entry in lootLedger)
                    if (entry.HeldRare != null)
                        drain.HeldRares.Add(entry.HeldRare);

                foreach (var overflow in lootOverflow)
                    drain.Overflow.Add(overflow.Item);

                foreach (var buffered in dealBuffer.Concat(pooledDealBuffer))
                    drain.Buffered.Add(buffered.Item);

                foreach (var built in prebuiltLoot)
                    drain.Prebuilt.AddRange(built.Objects());

                lootLedger.Clear();
                lootOverflow.Clear();
                dealBuffer.Clear();
                pooledDealBuffer.Clear();
                dealBufferRolls = 0;
                prebuiltLoot.Clear();
                prebuiltObjects = 0;
                placedCaches.Clear();
                lootBonusPending = false;
                pooledBonusRollsPending = 0;

                return drain;
            }
        }
    }

    /// <summary>What a run's pool held when it was drained at run end.</summary>
    public sealed class LootPoolDrain
    {
        public LootPoolDrain(int ledgerEntries, bool bonusPending, int placedCaches)
        {
            LedgerEntries = ledgerEntries;
            BonusPending = bonusPending;
            PlacedCaches = placedCaches;
        }

        public int LedgerEntries { get; }
        public bool BonusPending { get; }
        public int PlacedCaches { get; }
        public List<WorldObject> Overflow { get; } = new List<WorldObject>();
        public List<WorldObject> HeldRares { get; } = new List<WorldObject>();

        /// <summary>Group Threads: built items still in the deal buffers (never handed out), destroyed by ThreadLootPool.DisposeForRunEnd.</summary>
        public List<WorldObject> Buffered { get; } = new List<WorldObject>();

        /// <summary>Group Threads: pooled boss-bonus rolls never built. Not objects, so nothing to destroy; reported only.</summary>
        public int PooledBonusRolls { get; internal set; }

        /// <summary>Objects the trickle built ahead of the clear that were never delivered (items and held rares), destroyed by ThreadLootPool.DisposeForRunEnd.</summary>
        public List<WorldObject> Prebuilt { get; } = new List<WorldObject>();

        /// <summary>Group Threads: items still in members' held piles at run end, destroyed by ThreadLootPool.DisposeForRunEnd.</summary>
        public int HeldPileItems { get; internal set; }
    }

    /// <summary>
    /// One ledger piece the trickle claimed and built ahead of the clear (ThreadDungeonRun's prebuilt store). The piece
    /// keeps the rare/recipient metadata of its slice; <see cref="Items"/> is what the materialiser returned, which
    /// normally includes the held rare last (ThreadCacheFiller.MaterializeEntry's shape) but is not relied on to.
    /// </summary>
    internal sealed class PrebuiltLoot
    {
        public PrebuiltLoot(ThreadLootLedgerEntry piece, List<WorldObject> items)
        {
            Piece = piece;
            Items = items ?? new List<WorldObject>();
        }

        public ThreadLootLedgerEntry Piece { get; }
        public List<WorldObject> Items { get; }

        /// <summary>Every distinct out-of-world object this piece holds: its items, plus the held rare if the items left it out.</summary>
        public IEnumerable<WorldObject> Objects()
        {
            foreach (var item in Items)
                if (item != null)
                    yield return item;

            if (Piece.HeldRare != null && !Items.Contains(Piece.HeldRare))
                yield return Piece.HeldRare;
        }

        public int ObjectCount => Objects().Count();
    }
}
