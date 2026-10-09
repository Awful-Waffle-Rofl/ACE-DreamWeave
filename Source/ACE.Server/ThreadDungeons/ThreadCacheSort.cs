using System;
using System.Diagnostics;
using System.Linq;

using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// A cheap fingerprint of WHICH objects a cache holds: item count, and the XOR and the SUM of their GUIDs.
    /// O(n) GUID reads, no property lookups and no allocation, so checking it costs a small fraction of the sort
    /// it lets a pass skip. Any item arriving changes the count, and any swap of one object for another changes
    /// both XOR and SUM; the two together make an accidental match between different contents implausible.
    ///
    /// It deliberately does NOT cover positions. A player rearranging items inside an open chest, or taking one
    /// out (Container.TryRemoveFromInventory shifts the rest down, which preserves their order), should not
    /// summon a re-sort over what they did. A take-out does change the signature, which costs one re-sort that
    /// writes nothing.
    /// </summary>
    internal readonly struct CacheSortSignature : IEquatable<CacheSortSignature>
    {
        public CacheSortSignature(int count, uint xor, ulong sum)
        {
            Count = count;
            Xor = xor;
            Sum = sum;
        }

        public int Count { get; }
        public uint Xor { get; }
        public ulong Sum { get; }

        public static CacheSortSignature Of(Container cache)
        {
            var count = 0;
            var xor = 0u;
            var sum = 0ul;

            foreach (var guid in cache.Inventory.Keys)
            {
                count++;
                xor ^= guid.Full;
                sum += guid.Full;
            }

            return new CacheSortSignature(count, xor, sum);
        }

        public bool Equals(CacheSortSignature other) => Count == other.Count && Xor == other.Xor && Sum == other.Sum;

        public override bool Equals(object obj) => obj is CacheSortSignature other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Count, Xor, Sum);
    }

    /// <summary>What one <see cref="ThreadCacheSort.SortRunCaches"/> pass did. Every field is logged.</summary>
    internal readonly struct CacheSortPass
    {
        public CacheSortPass(int caches, int items, int deferred, int capped, double elapsedMs)
        {
            Caches = caches;
            Items = items;
            Deferred = deferred;
            Capped = capped;
            ElapsedMs = elapsedMs;
        }

        /// <summary>Caches whose contents were renumbered.</summary>
        public int Caches { get; }

        /// <summary>Items renumbered across those caches.</summary>
        public int Items { get; }

        /// <summary>Caches that still NEED a sort but were skipped because a player had them OPEN - see the class doc comment.</summary>
        public int Deferred { get; }

        /// <summary>
        /// Caches that still NEED a sort but were left for a later pass because this one hit
        /// <see cref="ThreadCacheSort.MaxCachesPerSortPass"/>. A cache already ordered and untouched since is never
        /// counted here, which is what lets a run with more caches than one pass may sort go quiet.
        /// </summary>
        public int Capped { get; }

        /// <summary>Wall-clock milliseconds the pass spent, measured on the landblock thread that ran it.</summary>
        public double ElapsedMs { get; }

        /// <summary>True when this pass left work for the deferred retry, whatever the reason.</summary>
        public bool LeftWork => Deferred > 0 || Capped > 0;
    }

    /// <summary>
    /// Orders a Thread Cache's contents by the /mule vault display order (user ruling 2026-09-21).
    ///
    /// NEITHER run kind is touched here as of the owner ruling 2026-09-22: a solo run now fills through the
    /// exact same merge-sort-before-placement path a group run always has (ThreadGroupCacheDelivery.FillFromPile,
    /// reused unchanged - see ThreadCacheFiller.BuildPile and ThreadCachePlacer.Execute), keyed by
    /// ThreadLootStacking.SortPileForPlacement's OWNER key: stackables grouped by wcid (by the group's total
    /// Value descending), then everything else by Value descending. <see cref="VaultDisplaySort.Order"/> is a
    /// DIFFERENT key entirely (item-type category, then weapon class or equip slot, then material, name,
    /// workmanship, and only THEN Value descending as a late tiebreaker - VaultDisplaySort.cs:111-146), so
    /// running it here would silently reorder a cache out of the very order the merge-sort feature exists to
    /// produce. Both run kinds are excluded for the same reason group already was: "the ordering its own
    /// delivery path already produced" now applies to solo too, not just group.
    ///
    /// The class stays (rather than being deleted) because the kill switch (<see cref="TunableKey"/>), the
    /// resume cursor and the signature caching are all still exercised in isolation by ThreadCacheSortTests, and
    /// because <see cref="Reorder"/>/<see cref="Reorderer"/> remain a correct, independently useful "order this
    /// one container by the vault key" primitive should a future caller want it outside the delivery chain.
    ///
    /// THE KEY IS NOT REIMPLEMENTED. <see cref="VaultDisplaySort.Order"/> is the one composite key, shared
    /// with PersonalVendor.forEachItem, so a future change to the vault ordering reaches Thread Caches
    /// with no second edit. The only thing this file owns is WHEN the order is applied and HOW it is
    /// written down.
    ///
    /// HOW: by PlacementPosition. What the player sees is what GameEventViewContents writes, and that
    /// event orders the container's inventory by PlacementPosition
    /// (Network/GameEvent/Events/GameEventViewContents.cs:15) - so "sorting the cache" means renumbering,
    /// not sorting a list in memory. One ascending sequence is assigned across the WHOLE inventory,
    /// main-slot and pack-slot items alike, which is exactly the numbering ThreadCacheFiller already
    /// produces: its `next` counter runs across both groups (ThreadCacheFiller.NextPlacementPosition
    /// takes the max over all of Inventory). A position is written only when it actually changes, so a
    /// re-sort writes nothing to a cache that is already in order.
    ///
    /// HISTORICAL (the mechanics below describe the per-cache loop <see cref="SortRunCaches"/> deleted in round 4,
    /// 2026-09-22 - CLEANUP item 2, code review of PR #1284: kept as a record of WHY the surrounding fields
    /// (<see cref="MaxCachesPerSortPass"/>, ThreadDungeonRun.CacheSortResume, ThreadDungeonRun.MarkCacheSortWanted)
    /// still exist, not as a description of anything <see cref="SortRunCaches"/> does today - it returns `default`
    /// unconditionally, for every run, and none of the below runs any more):
    ///
    /// WHEN: at the TERMINAL step of a delivery chain, never per batch - every chain's terminal step sorted, which
    /// also covered a retry chain and a late kill's new chain.
    ///
    /// HOW MUCH, per pass: at most <see cref="MaxCachesPerSortPass"/> caches, the rest deferred to the SAME retry
    /// an open cache used, with a resume cursor on the run (ThreadDungeonRun.CacheSortResume, keyed on cache GUID
    /// since caches are registered and destroyed between passes) covering a run holding more caches than one pass
    /// could sort.
    ///
    /// AND IT STOPPED: only a cache that still needed a sort counted against the cap or kept the retry armed - a
    /// per-cache <see cref="CacheSortSignature"/> let a later pass skip an already-ordered cache for free.
    ///
    /// AN OPEN CACHE WAS SKIPPED, NOT RESENT: the server has no way to prove what the client does with a reordered
    /// container it is already displaying, so a cache with a viewer was left in arrival order and the run marked
    /// (ThreadDungeonRun.MarkCacheSortWanted) for ThreadDungeonManager.Tick to retry once the viewer closed it.
    /// </summary>
    internal static class ThreadCacheSort
    {
        /// <summary>
        /// NOT CONSULTED (CLEANUP item 3, code review of PR #1284, 2026-09-22): <see cref="SortRunCaches"/> returns
        /// `default` unconditionally and never reads <see cref="Enabled"/>, so this key currently controls nothing.
        /// Left defined, and its PropertyManager config row left in place, on purpose - deleting either would be a
        /// silent behaviour change for an operator who has it set, and <see cref="Reorder"/>/<see cref="Reorderer"/>
        /// remain a correct primitive a future caller could gate on it again.
        /// </summary>
        public const string TunableKey = "dynamic_dungeons_cache_display_sort";

        /// <summary>Default ON, per the ruling that asked for the sort. See <see cref="TunableKey"/>: not consulted today.</summary>
        public const bool DefaultEnabled = true;

        /// <summary>
        /// The most caches one pass may renumber, so a terminal step's sort cost is bounded by a constant
        /// rather than by how many caches the run has accumulated. MEASURED on a Debug build, a whole pass
        /// against 8 FULL 120-item caches (the worst case this cap allows) runs 1.451 ms median / 1.737 ms
        /// max, rising to 2.601 ms max when a further 8 caches are counted past the cap - against the 8 ms
        /// default step budget (dynamic_dungeons_cache_step_budget_ms). Whatever a pass does not reach is
        /// deferred, not dropped.
        /// </summary>
        public const int MaxCachesPerSortPass = 8;

        /// <summary>The live tunable read. Not consulted by <see cref="SortRunCaches"/> today - see <see cref="TunableKey"/>.</summary>
        internal static bool Enabled => PropertyManager.GetBool(TunableKey, DefaultEnabled).Item;

        /// <summary>
        /// Test seam for the per-cache renumbering. Production is <see cref="Reorder"/>; a test replaces it
        /// to prove the caller's guard holds when the sort throws. Restore it in a finally.
        /// </summary>
        internal static Func<Container, int> Reorderer = Reorder;

        /// <summary>
        /// Renumbers one container's contents into <see cref="VaultDisplaySort.Order"/>'s order and returns
        /// how many items it covers. Reads the tunable NOT at all - the caller has already decided - so a
        /// test can drive the ordering itself without a PropertyManager cache entry.
        /// </summary>
        internal static int Reorder(Container cache)
        {
            if (cache == null)
                return 0;

            // Snapshotted before ordering: VaultDisplaySort.Order is lazy, and the enumeration below writes
            // to the objects it is walking.
            var items = cache.Inventory.Values.ToList();

            if (items.Count < 2)
                return items.Count;

            var position = 0;

            foreach (var item in VaultDisplaySort.Order(items))
            {
                // Only on a real change: PlacementPosition's setter is SetProperty, which marks the biota
                // changed, and a re-sort of an already-ordered cache should cost nothing.
                if (item.PlacementPosition != position)
                    item.PlacementPosition = position;

                position++;
            }

            return position;
        }

        /// <summary>
        /// Owner ruling 2026-09-22 (round 4): returns an empty pass for EVERY run, unconditionally, and touches
        /// nothing. Before this it ordered a solo run's placed caches by <see cref="VaultDisplaySort"/>'s key,
        /// bounded per pass at <see cref="MaxCachesPerSortPass"/> with a resume cursor across passes
        /// (<see cref="ThreadDungeonRun.CacheSortResume"/>) and a per-cache dirty check
        /// (<see cref="CacheSortSignature"/>) - group runs were already excluded, for the same reason solo is
        /// now: a solo run fills through the same merge-sort-before-placement path a group run always has
        /// (`ThreadGroupCacheDelivery.FillFromPile`, keyed by `ThreadLootStacking.SortPileForPlacement`'s owner
        /// key), and that key is NOT `VaultDisplaySort.Order`'s (see the class doc comment), so running this
        /// pass on a solo cache would only fight the order the merge already produced.
        ///
        /// The old per-cache loop (bounded pass, resume cursor, dirty check, `Reorderer` call) is removed rather
        /// than left dead beneath an early return: with every run excluded, nothing can reach it, so nothing can
        /// prove it still works. `Reorder`/`Reorderer` and `CacheSortSignature.Of` remain as independent,
        /// correct primitives (order one container by the vault key; fingerprint one container's contents) for
        /// whichever future caller wants them outside this now-inert entry point.
        /// </summary>
        internal static CacheSortPass SortRunCaches(ThreadDungeonRun run) => default;

        /// <summary>
        /// The " sortCaches=... sortItems=... sortDeferred=... sortCapped=... sortMs=..." tail of a delivery
        /// chain's finish line, or "" when the pass did nothing at all (the tunable off, a group run, a run
        /// with no placed cache). Kept here so the numbers that answer "what does the sort cost" are emitted
        /// in exactly one shape.
        /// </summary>
        internal static string LogSuffix(CacheSortPass pass)
        {
            if (pass.Caches == 0 && !pass.LeftWork)
                return "";

            return $" sortCaches={pass.Caches} sortItems={pass.Items} sortDeferred={pass.Deferred} sortCapped={pass.Capped} sortMs={pass.ElapsedMs:F3}";
        }

        /// <summary>
        /// The deferred pass ThreadDungeonManager.Tick runs for a run that had an open cache, or more caches
        /// than one pass may sort, when its delivery finished. Claims the run's flag first, so a run whose
        /// caches are all closed and sorted stops costing anything; anything still outstanding re-marks the
        /// flag through <see cref="SortRunCaches"/> and the next Tick continues from the resume cursor.
        /// </summary>
        internal static CacheSortPass SortDeferred(ThreadDungeonRun run)
        {
            if (run == null || !run.TryClaimCacheSortWanted())
                return default;

            return SortRunCaches(run);
        }
    }
}
