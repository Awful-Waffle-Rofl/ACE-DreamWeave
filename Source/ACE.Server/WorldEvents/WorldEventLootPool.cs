using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>Where one seat's share is in its delivery life.</summary>
    public enum WorldEventShareState
    {
        /// <summary>Dealt, not yet delivered. A claim may start delivering it.</summary>
        Pending,

        /// <summary>A delivery chain owns it. Nothing else may touch its contents.</summary>
        Delivering,

        /// <summary>Its chest is in the world. Terminal.</summary>
        Delivered
    }

    /// <summary>
    /// One seat's share of a run's pooled loot (world_events_pooled_loot_enabled). Built by
    /// <see cref="WorldEventLootPool.CloseAndDeal"/>; consumed by WorldEventShareDelivery.
    ///
    /// OWNERSHIP. While Pending the pool owns it (and destroys its held objects on dispose); while Delivering the
    /// delivery chain owns it exclusively and the pool never touches it. The chain works directly on these lists, so a
    /// failed delivery that hands the share back leaves it exactly as far along as it got: unrolled entries stay in
    /// <see cref="Entries"/>, rolled items in <see cref="Built"/>, rares in <see cref="Rares"/>.
    /// </summary>
    public sealed class WorldEventLootShare
    {
        public WorldEventLootShare(uint seat)
        {
            Seat = seat;
        }

        /// <summary>The character guid this share belongs to.</summary>
        public uint Seat { get; }

        /// <summary>Banked kills not yet rolled, in kill order. Never carries a held rare (see <see cref="Rares"/>).</summary>
        public List<ThreadLootLedgerEntry> Entries { get; } = new List<ThreadLootLedgerEntry>();

        /// <summary>Rares rolled at kill time for this seat, with the finder name the "has discovered" line uses.</summary>
        public List<(WorldObject Rare, string FinderName)> Rares { get; } = new List<(WorldObject Rare, string FinderName)>();

        /// <summary>Items already rolled that a failed delivery handed back. Placed before any new roll.</summary>
        public List<WorldObject> Built { get; } = new List<WorldObject>();

        public WorldEventShareState State { get; internal set; } = WorldEventShareState.Pending;

        /// <summary>Delivery attempts started on this share, for the log line.</summary>
        public int Attempts { get; internal set; }

        public bool IsEmpty => Entries.Count == 0 && Rares.Count == 0 && Built.Count == 0;

        /// <summary>Destroys every held object (rares and built items) and forgets the unrolled entries. Returns the count destroyed.</summary>
        internal int DestroyContents()
        {
            var destroyed = 0;

            foreach (var (rare, _) in Rares)
            {
                if (rare != null && !rare.IsDestroyed)
                {
                    rare.Destroy();
                    destroyed++;
                }
            }

            foreach (var item in Built)
            {
                if (item != null && !item.IsDestroyed)
                {
                    item.Destroy();
                    destroyed++;
                }
            }

            Rares.Clear();
            Built.Clear();
            Entries.Clear();

            return destroyed;
        }
    }

    /// <summary>
    /// A live delivery chain, as the pool sees it: something holding a Delivering share's objects that a Dispose must be
    /// able to destroy even if the landblock queue that would have run it is gone.
    /// </summary>
    internal interface IWorldEventShareChain
    {
        /// <summary>Destroys the chain's chest and every object it or its share holds; returns the count. Idempotent.</summary>
        int DestroyHeld();
    }

    /// <summary>What <see cref="WorldEventLootPool.CloseAndDeal"/> did, for the one log line.</summary>
    public readonly struct WorldEventDealSummary
    {
        public readonly int Records;
        public readonly int Seats;
        public readonly int Rares;
        public readonly int RaresDestroyed;
        public readonly bool AlreadyClosed;

        public WorldEventDealSummary(int records, int seats, int rares, int raresDestroyed, bool alreadyClosed)
        {
            Records = records;
            Seats = seats;
            Rares = rares;
            RaresDestroyed = raresDestroyed;
            AlreadyClosed = alreadyClosed;
        }
    }

    /// <summary>
    /// World Events pooled loot (world_events_pooled_loot_enabled). Prod run 3 showed every event kill making a corpse
    /// and its rolled loot MID-FIGHT, each one a create broadcast to everyone in view; per-player outbound traffic
    /// tripled and clients crashed. Under this model a non-boss event creature leaves no corpse: its kill is BANKED
    /// here (the treasure profile to roll later, plus any rare, which needs the killer and so is rolled at kill time and
    /// held out of world), and nothing is rolled until a participant claims the Weave Cache, at which point their share
    /// is built into their own chest a budgeted step at a time (WorldEventShareDelivery).
    ///
    /// LIFE. Created when the run goes Active and the switch is on (<see cref="WorldEvent.LatchPooledLoot"/>). Banked into
    /// from Creature.Die on landblock-group threads, concurrently. Closed and dealt once at Finish, on the world thread,
    /// as the run enters Rewarding: the seats are every character with a participation record (the crate-versus-coal
    /// rule), and the kills are dealt round-robin in kill order so per-seat counts differ by at most one; a rare goes to
    /// the seat it was rolled for. Disposed in CompleteCleanup (window end or abort), which destroys every rare and
    /// built item still held, so an unclaimed share simply vanishes.
    ///
    /// LOCKING. <see cref="sync"/> guards every field. Nothing that can block, re-enter or take another lock runs under
    /// it; object destruction happens after the lock is released.
    ///
    /// Reuses ThreadLootLedgerEntry unchanged as the banked record and ThreadCacheFiller.MaterializeEntry to roll it, so a
    /// share holds exactly what the corpses would have held from their treasure profile.
    /// </summary>
    public sealed class WorldEventLootPool
    {
        private readonly object sync = new object();

        private readonly List<ThreadLootLedgerEntry> records = new List<ThreadLootLedgerEntry>();

        private Dictionary<uint, WorldEventLootShare> shares = new Dictionary<uint, WorldEventLootShare>();

        private readonly List<IWorldEventShareChain> liveChains = new List<IWorldEventShareChain>();

        private bool closed;
        private bool disposed;

        /// <summary>Banked records not yet dealt.</summary>
        public int Count
        {
            get { lock (sync) return records.Count; }
        }

        /// <summary>True once the deal has run (or the pool was disposed); every later bank is refused.</summary>
        public bool IsClosed
        {
            get { lock (sync) return closed; }
        }

        public bool IsDisposed
        {
            get { lock (sync) return disposed; }
        }

        /// <summary>
        /// Appends one banked kill in kill order. False once the pool is closed or disposed: the CALLER then owns the
        /// entry's held rare and must destroy it (<see cref="WorldEventLootBank.BankKill"/> does).
        /// </summary>
        public bool TryBank(ThreadLootLedgerEntry entry)
        {
            if (entry == null)
                return false;

            lock (sync)
            {
                if (closed || disposed)
                    return false;

                records.Add(entry);
                return true;
            }
        }

        /// <summary>
        /// Finish, entering Rewarding: closes the bank and deals every record. <paramref name="seats"/> are the
        /// credit holders (duplicates and 0 ignored, order kept); <paramref name="startOffset"/> picks which seat takes
        /// the first record, so the seat that gets the odd one out is not always the same. A rare with no seat to go to
        /// (no seats at all) is destroyed. Idempotent: a second call deals nothing.
        /// </summary>
        public WorldEventDealSummary CloseAndDeal(IEnumerable<uint> seats, int startOffset)
        {
            var orphans = new List<WorldObject>();
            int recordCount;
            int seatCount;
            int rareCount;

            lock (sync)
            {
                if (closed || disposed)
                    return new WorldEventDealSummary(0, 0, 0, 0, true);

                closed = true;

                var seatList = NormalizeSeats(seats);

                recordCount = records.Count;
                seatCount = seatList.Count;
                rareCount = records.Count(r => r?.HeldRare != null);

                shares = Deal(records, seatList, startOffset, orphans);
                records.Clear();
            }

            foreach (var rare in orphans)
                rare?.Destroy();

            return new WorldEventDealSummary(recordCount, seatCount, rareCount, orphans.Count, false);
        }

        /// <summary>The seat list the deal uses: distinct, non-zero, in the order given.</summary>
        internal static List<uint> NormalizeSeats(IEnumerable<uint> seats)
        {
            var list = new List<uint>();
            var seen = new HashSet<uint>();

            foreach (var seat in seats ?? Enumerable.Empty<uint>())
            {
                if (seat != 0 && seen.Add(seat))
                    list.Add(seat);
            }

            return list;
        }

        /// <summary>
        /// The deal itself, pure. Record i (kill order) goes to seats[(startOffset + i) mod n], so per-seat record
        /// counts differ by at most one and each seat's entries stay in kill order. The record's held rare is split off
        /// and goes to its RareRecipientGuid (the seat it was rolled for) when that is a seat, else it follows the
        /// record. No seats: every rare goes to <paramref name="orphans"/> and nothing is dealt. Every seat gets a share,
        /// empty or not.
        /// </summary>
        internal static Dictionary<uint, WorldEventLootShare> Deal(IReadOnlyList<ThreadLootLedgerEntry> records, IReadOnlyList<uint> seats,
            int startOffset, List<WorldObject> orphans)
        {
            var result = new Dictionary<uint, WorldEventLootShare>();

            if (seats == null || seats.Count == 0)
            {
                foreach (var record in records ?? Array.Empty<ThreadLootLedgerEntry>())
                {
                    if (record?.HeldRare != null)
                        orphans?.Add(record.HeldRare);
                }

                return result;
            }

            foreach (var seat in seats)
                result[seat] = new WorldEventLootShare(seat);

            var n = seats.Count;
            var offset = ((startOffset % n) + n) % n;
            var index = 0;

            foreach (var record in records ?? Array.Empty<ThreadLootLedgerEntry>())
            {
                if (record == null)
                    continue;

                var share = result[seats[(offset + index) % n]];
                index++;

                if (record.HeldRare == null)
                {
                    share.Entries.Add(record);
                    continue;
                }

                // The profile half follows the round-robin; the rare goes to the player it was rolled for.
                share.Entries.Add(new ThreadLootLedgerEntry(record.IsBoss, record.KillerIsOlthoiPlayer, record.Profile, record.SalvageAffinities,
                    null, null, record.Rolls, 0));

                var rareShare = record.RareRecipientGuid != 0 && result.TryGetValue(record.RareRecipientGuid, out var recipient) ? recipient : share;
                rareShare.Rares.Add((record.HeldRare, record.HeldRareFinderName));
            }

            return result;
        }

        /// <summary>
        /// Starts delivering <paramref name="seat"/>'s share: true only for a dealt, Pending, non-empty share, which is
        /// then Delivering and owned by the caller until <see cref="EndDelivery"/>. An empty share is marked Delivered
        /// here so it is never asked about again.
        /// </summary>
        public bool TryBeginDelivery(uint seat, out WorldEventLootShare share)
        {
            share = null;

            lock (sync)
            {
                if (!closed || disposed || !shares.TryGetValue(seat, out var found))
                    return false;

                if (found.State != WorldEventShareState.Pending)
                    return false;

                if (found.IsEmpty)
                {
                    found.State = WorldEventShareState.Delivered;
                    return false;
                }

                found.State = WorldEventShareState.Delivering;
                found.Attempts++;
                share = found;
                return true;
            }
        }

        /// <summary>
        /// Ends a delivery the caller began. <paramref name="delivered"/> true: the chest is in the world, the share is
        /// done. False: the share goes back to Pending with whatever the chain left in it, so a later claim click retries
        /// it. Returns false when the pool was disposed meanwhile; the caller then still owns the share's contents and
        /// must destroy them.
        /// </summary>
        public bool EndDelivery(uint seat, bool delivered)
        {
            lock (sync)
            {
                if (!shares.TryGetValue(seat, out var share) || share.State != WorldEventShareState.Delivering)
                    return false;

                share.State = delivered ? WorldEventShareState.Delivered : WorldEventShareState.Pending;
                return !disposed;
            }
        }

        /// <summary>
        /// Registers a delivery chain for the life of its delivery, so <see cref="Dispose"/> can destroy what it holds.
        /// False once disposed: the caller then owns the share's contents and must destroy them.
        /// </summary>
        internal bool TryRegisterChain(IWorldEventShareChain chain)
        {
            if (chain == null)
                return false;

            lock (sync)
            {
                if (disposed)
                    return false;

                liveChains.Add(chain);
                return true;
            }
        }

        /// <summary>A chain that reached any end leaves the live list.</summary>
        internal void UnregisterChain(IWorldEventShareChain chain)
        {
            lock (sync)
                liveChains.Remove(chain);
        }

        /// <summary>Live chains, for tests.</summary>
        internal int LiveChainCount
        {
            get { lock (sync) return liveChains.Count; }
        }

        /// <summary>The share's state, or null when the seat has none.</summary>
        public WorldEventShareState? ShareState(uint seat)
        {
            lock (sync)
                return shares.TryGetValue(seat, out var share) ? share.State : (WorldEventShareState?)null;
        }

        /// <summary>
        /// Window end or abort: refuses everything from now on and destroys every rare of an undealt record, every held
        /// object of a Pending share, and - through <see cref="IWorldEventShareChain.DestroyHeld"/> - the chest and
        /// contents of every live delivery chain. That last part does not wait for the chain's next turn, because a
        /// landblock that unloads clears its queue and the turn may never come. A Delivering share with no registered
        /// chain (a delivery begun and not yet handed to one) is left to its caller. Returns the count destroyed.
        /// Idempotent.
        /// </summary>
        public int Dispose()
        {
            var doomed = new List<WorldObject>();
            var pendingShares = new List<WorldEventLootShare>();
            List<IWorldEventShareChain> chainsToKill;

            lock (sync)
            {
                if (disposed)
                    return 0;

                disposed = true;
                closed = true;

                foreach (var record in records)
                {
                    if (record?.HeldRare != null)
                        doomed.Add(record.HeldRare);
                }

                records.Clear();

                foreach (var share in shares.Values)
                {
                    if (share.State == WorldEventShareState.Pending)
                        pendingShares.Add(share);
                }

                chainsToKill = new List<IWorldEventShareChain>(liveChains);
                liveChains.Clear();
            }

            var destroyed = 0;

            foreach (var item in doomed)
            {
                if (item != null && !item.IsDestroyed)
                {
                    item.Destroy();
                    destroyed++;
                }
            }

            // Safe outside the lock: a Pending share can only leave Pending through TryBeginDelivery, which refuses once
            // disposed is set.
            foreach (var share in pendingShares)
                destroyed += share.DestroyContents();

            // Outside the lock: a chain takes its own lock, and its unregister call takes this one.
            foreach (var chain in chainsToKill)
                destroyed += chain.DestroyHeld();

            return destroyed;
        }

        /// <summary>Test seam: a snapshot of the dealt shares.</summary>
        internal Dictionary<uint, WorldEventLootShare> SharesForTest()
        {
            lock (sync)
                return new Dictionary<uint, WorldEventLootShare>(shares);
        }

        public override string ToString() => $"WorldEventLootPool(records={Count}, closed={IsClosed}, disposed={IsDisposed})";
    }

    /// <summary>
    /// The kill-time half of World Events pooled loot: called from Creature.Die for a death
    /// <see cref="Creature.IsWorldEventPooledDeath(WorldEvent, int?, uint)"/> accepted, on the landblock thread that
    /// killed it.
    /// </summary>
    public static class WorldEventLootBank
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Test seam for the kill-time rare roll. Null (production) is Corpse.RollRareFor, with the killer's found-rare
        /// bookkeeping applied only after the bank accepts the record. A seam roller books nothing.
        /// </summary>
        internal static Func<Creature, DamageHistoryInfo, WorldObject> HeldRareRoller;

        /// <summary>
        /// CreateCorpse's rare eligibility, read against the creature's GATE level. The World Events band uplift raises
        /// Level and records the authored level in Creature.LootGateLevel, and CreateCorpse reads
        /// LootGateLevelFor(LootGateLevel, Level); ThreadLootPool.BankKill reads plain Level (Threads never sets a gate
        /// level), so it is NOT reused here.
        /// </summary>
        internal static bool ResolveRareEligible(bool current, int? lootGateLevel, int? level, DamageHistoryInfo killer)
            => ResolveRareEligible(current, lootGateLevel, level, killer != null, killer != null && killer.IsPlayer,
                killer != null && killer.IsOlthoiPlayer, () => killer?.TryGetAttacker()?.Level);

        /// <summary>The same rule over plain values (a test cannot build the Player a player killer needs).</summary>
        internal static bool ResolveRareEligible(bool current, int? lootGateLevel, int? level, bool killerPresent, bool killerIsPlayer,
            bool killerIsOlthoiPlayer, Func<int?> killerPlayerLevel)
            => Creature.ResolveCanGenerateRare(current, Creature.LootGateLevelFor(lootGateLevel, level), killerPresent, killerIsPlayer,
                killerIsOlthoiPlayer, killerPlayerLevel);

        /// <summary>
        /// Banks one kill on the run's pool. True when the pool accepted it (or there was nothing to bank); false when the
        /// pool refused it (closed or disposed), in which case any rare was destroyed and nothing was booked. Either way
        /// the caller leaves no corpse. A throw propagates (Die then falls back to a normal corpse) after destroying any
        /// rare already rolled, so a fallback corpse can never mean two rares.
        /// </summary>
        public static bool BankKill(Creature creature, DamageHistoryInfo killer)
        {
            var pool = creature?.P_WorldEvent?.LootPool;

            if (pool == null)
                return false;

            // CreateCorpse's lootKiller substitution: a CombatPet's kill is its owner's.
            killer = DamageHistoryInfo.ResolvePetOwnerAsKiller(killer);

            var canRare = ResolveRareEligible(creature.CanGenerateRare, creature.LootGateLevel, creature.Level, killer);

            var seam = HeldRareRoller;
            Player rareKiller = null;
            var rareTimestamp = 0;
            var rareRealTime = false;
            var rareTier = 0;

            // The rare is rolled FOR the killer and is the killer's: rareRecipientGuid is the killer's guid (a player's,
            // after the pet substitution), which is the seat the deal hands the rare to.
            var entry = ThreadLootLedgerEntry.FromKill(false, killer, canRare, creature.DeathTreasure, null,
                k => seam != null
                    ? seam(creature, k)
                    : Corpse.RollRareFor(k, creature.Name, creature.Guid, out rareKiller, out rareTimestamp, out rareRealTime, out rareTier),
                1, null, killer?.Guid.Full ?? 0);

            // Nothing to roll and no rare: there is nothing to bank, and an empty record would only dilute the split.
            if (entry.Profile == null && entry.HeldRare == null)
                return true;

            bool accepted;

            try
            {
                accepted = pool.TryBank(entry);
            }
            catch
            {
                entry.HeldRare?.Destroy();
                throw;
            }

            if (accepted)
            {
                if (seam == null && entry.HeldRare != null)
                    Corpse.ApplyRareFoundBookkeeping(rareKiller, rareRealTime, rareTimestamp, rareTier);

                return true;
            }

            entry.HeldRare?.Destroy();

            log.Warn($"[WORLDEVENT] run={creature.P_WorldEvent?.RunId} pool refused a kill from {creature.Name} (0x{creature.Guid.Full:X8}); the bank is closed, no corpse");
            return false;
        }
    }

    /// <summary>
    /// One budgeted fill step of a share chest, with no world object of its own so it can be driven by tests. Generic
    /// over the item type for that reason only; production is WorldObject.
    ///
    /// Order: handed-back items first (rares, then anything a failed attempt had already rolled), which cost no roll;
    /// then banked entries in kill order, one at a time, each charged its Rolls against the step's
    /// <see cref="ThreadLootRollBudget"/>. The budget's first claim is always admitted, so every step makes progress.
    /// An item the chest refuses (full) goes to <paramref name="refused"/>; once the chest is full the step stops.
    /// </summary>
    internal static class WorldEventShareFill
    {
        internal enum StepStop
        {
            /// <summary>The step's time or roll budget ran out with entries left: re-enqueue.</summary>
            Exhausted,

            /// <summary>Nothing left to place.</summary>
            Drained,

            /// <summary>The step took its maxEntries with entries left and budget to spare: its turn is over.</summary>
            Yielded,

            /// <summary>The chest reached its capacity; whatever is left is remainder.</summary>
            Full
        }

        internal static StepStop RunStep<T>(List<T> prebuilt, List<ThreadLootLedgerEntry> entries, Func<ThreadLootLedgerEntry, List<T>> materialize,
            Func<T, bool> tryPlace, Func<bool> isFull, List<T> refused, ThreadLootRollBudget budget, int maxEntries = int.MaxValue) where T : class
        {
            var taken = 0;

            while (prebuilt.Count > 0)
            {
                if (isFull())
                    return StepStop.Full;

                var item = prebuilt[0];
                prebuilt.RemoveAt(0);

                if (item != null && !tryPlace(item))
                    refused.Add(item);
            }

            while (true)
            {
                if (isFull())
                    return entries.Count > 0 ? StepStop.Full : StepStop.Drained;

                if (entries.Count == 0)
                    return StepStop.Drained;

                if (budget.Exhausted)
                    return StepStop.Exhausted;

                if (taken >= maxEntries)
                    return StepStop.Yielded;

                taken++;

                var entry = entries[0];
                entries.RemoveAt(0);

                budget.Spend(entry?.Rolls ?? 1);

                var items = materialize(entry) ?? new List<T>();

                foreach (var item in items)
                {
                    if (item == null)
                        continue;

                    if (isFull() || !tryPlace(item))
                        refused.Add(item);
                }
            }
        }
    }
}
