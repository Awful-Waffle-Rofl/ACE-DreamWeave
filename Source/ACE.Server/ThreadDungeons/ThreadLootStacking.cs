using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.Network.GameMessages.Messages;
using ACE.Server.WorldObjects;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Pooled-loot stack consolidation (Threads item 8, 2026-09-18; round 2, 2026-09-22). Stackable items that are
    /// genuinely identical are merged into as few stacks as their MaxStackSize allows. It is a pure object-count
    /// reduction: the same total quantity and the same total Value reach the same places, and nothing about what was
    /// rolled changes. On the solo path merging happens before placement, since there is one recipient. In a group
    /// run merging happens AFTER the snake deal, separately within each seat's own dealt share - never before the
    /// deal - so a whole set's worth of one dense-value stackable (Pyreal Peas and the like) can no longer land on a
    /// single seat as one merged pick; it still spreads across seats by the deal, and only what one seat already
    /// received gets consolidated into fewer stacks.
    ///
    /// WHAT COUNTS AS IDENTICAL. Stricter than ACE's own inventory merge, which only compares the wcid
    /// (Player_Inventory's stack merge): two items merge only when all of these hold -
    ///   - both are <see cref="Stackable"/> (SetStackSize does nothing on anything else) of the same runtime type and
    ///     wcid, with MaxStackSize above 1;
    ///   - every scalar property (int, int64, bool, float, string, data id, instance id, biota and ephemeral alike)
    ///     is equal, except the three a stack size owns - StackSize, Value and EncumbranceVal - and CreationTimestamp,
    ///     which WorldObject's constructor stamps per instance from the wall clock (WorldObject.cs:149) and so differs
    ///     between two otherwise identical rolls a second apart;
    ///   - the spell book, palettes, texture maps and anim parts are equal;
    ///   - neither carries anything a stack could not hold for both (emotes, a create list, a generator, a book,
    ///     skills, attributes, body parts, enchantments, positions, allegiance or house data) - such an item is never
    ///     merged;
    ///   - each item's Value and EncumbranceVal are exactly its per-unit value times its StackSize, so the merged
    ///     stack's SetStackSize reproduces the same totals; an item that is not self-consistent is left alone.
    /// Items carrying a rare finder name are never touched: they are announced individually.
    ///
    /// ORDER. Each merged stack takes the position of its group's FIRST item; unmerged items keep their order. The
    /// objects merged away are destroyed (they are out of world and never delivered).
    ///
    /// ROUND 2 (owner correction, 2026-09-22): merging and sorting happen on the WHOLE list of loot bound for one
    /// recipient BEFORE any of it is split across chests, not per delivery step. <see cref="ConsolidatePile"/>
    /// merges a group member's entire held pile - every round currently sitting in it - so each of that member's
    /// chests holds a contiguous, sorted segment of one larger list rather than per-round fragments; a solo run
    /// reaches the same result per fill step via the ordinary tuple <see cref="Consolidate(List{ValueTuple{WorldObject,string}},out int)"/>
    /// plus <see cref="TopUpExisting"/> against whatever a PRIOR step already placed (see ThreadCacheFiller.PlacePending
    /// for why a step-spanning buffer was not built - the smaller, budget-preserving fix). <see cref="SortForPlacement"/>
    /// / <see cref="SortPileForPlacement"/> then order a batch: stackables first, grouped by wcid, each group's own
    /// stacks adjacent and groups ordered by total Value descending, then everything else by Value descending.
    /// <see cref="TopUpExisting"/> covers the case this round 2 exists for: identical stacks landing in the SAME
    /// cache across separate fill passes (different deal rounds, or different time-budgeted steps) must top up the
    /// stack already there instead of sitting beside it as a second one.
    /// </summary>
    public static class ThreadLootStacking
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>Consolidates a list of (item, rare finder) pairs. <paramref name="removed"/> is how many objects were merged away.</summary>
        public static List<(WorldObject Item, string RareFinderName)> Consolidate(List<(WorldObject Item, string RareFinderName)> items, out int removed)
        {
            removed = 0;

            if (items == null || items.Count < 2)
                return items;

            var keep = new List<(WorldObject Item, string RareFinderName)>(items.Count);
            var groups = new Dictionary<string, List<WorldObject>>();
            var groupAt = new Dictionary<string, int>();

            foreach (var entry in items)
            {
                var key = entry.RareFinderName == null ? SignatureOf(entry.Item) : null;

                if (key == null)
                {
                    keep.Add(entry);
                    continue;
                }

                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = new List<WorldObject>();
                    groupAt[key] = keep.Count;
                    keep.Add(entry);
                }

                group.Add(entry.Item);
            }

            if (groups.Values.All(g => g.Count < 2))
                return items;

            // Rebuild in order: each group expands, at its first item's position, into the stacks it merged into.
            var result = new List<(WorldObject Item, string RareFinderName)>(keep.Count);
            var firstOf = groupAt.ToDictionary(kv => kv.Value, kv => kv.Key);

            for (var i = 0; i < keep.Count; i++)
            {
                if (!firstOf.TryGetValue(i, out var key))
                {
                    result.Add(keep[i]);
                    continue;
                }

                var group = groups[key];

                if (group.Count < 2)
                {
                    result.Add(keep[i]);
                    continue;
                }

                foreach (var stack in MergeGroup(group, ref removed))
                    result.Add((stack, null));
            }

            return result;
        }

        /// <summary>Consolidates a plain item list (no rare finders).</summary>
        public static List<WorldObject> Consolidate(List<WorldObject> items, out int removed)
        {
            var merged = Consolidate(items?.Select(i => (i, (string)null)).ToList(), out removed);
            return merged?.Select(m => m.Item).ToList();
        }

        /// <summary>
        /// Whole-pile consolidation (Threads item 8 round 2, 2026-09-22, owner correction): merges identical
        /// stackables across the member's WHOLE held pile - every deal round currently sitting in it, not just what
        /// one delivery pass is about to place - since the pile IS the pre-chest list a member's caches are split
        /// from. Behaves like <see cref="Consolidate(List{ValueTuple{WorldObject,string}},out int)"/>, except a
        /// merged survivor's <see cref="HeldPileItem.MergedRounds"/> carries every round DESTROYED into it - never a
        /// co-survivor's own round, even when MaxStackSize forces more than one surviving stack (fix, code review of
        /// PR #1284, finding 1) - so the caller can still mark every round received (ruling R21) once the survivor
        /// lands, even though the rounds it swallowed never place an item of their own. Rares (a non-null
        /// RareFinderName) are never merged.
        /// </summary>
        public static List<HeldPileItem> ConsolidatePile(List<HeldPileItem> pile, out int removed)
        {
            removed = 0;

            if (pile == null || pile.Count < 2)
                return pile;

            var keep = new List<HeldPileItem>(pile.Count);
            var groups = new Dictionary<string, List<HeldPileItem>>();
            var groupAt = new Dictionary<string, int>();

            foreach (var held in pile)
            {
                var key = held.RareFinderName == null ? SignatureOf(held.Item) : null;

                if (key == null)
                {
                    keep.Add(held);
                    continue;
                }

                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = new List<HeldPileItem>();
                    groupAt[key] = keep.Count;
                    keep.Add(held);
                }

                group.Add(held);
            }

            if (groups.Values.All(g => g.Count < 2))
                return pile;

            var result = new List<HeldPileItem>(keep.Count);
            var firstOf = groupAt.ToDictionary(kv => kv.Value, kv => kv.Key);

            for (var i = 0; i < keep.Count; i++)
            {
                if (!firstOf.TryGetValue(i, out var key))
                {
                    result.Add(keep[i]);
                    continue;
                }

                var group = groups[key];

                if (group.Count < 2)
                {
                    result.Add(keep[i]);
                    continue;
                }

                var items = group.Select(h => h.Item).ToList();
                var beforeCount = items.Count;
                var mergedItems = MergeGroup(items, ref removed);

                if (mergedItems.Count == beforeCount)
                {
                    // MergeGroup declined (a conservation check failed, or the group did not actually need merging):
                    // keep every item exactly as it was, with its own round.
                    foreach (var held in group)
                        result.Add(held);
                    continue;
                }

                // Code review of PR #1284, finding 1: MergeGroup returns group.Take(survivorCount) - the survivors
                // are always the group's own first `survivorCount` items, index for index, and every item AFTER
                // that (group.Skip(survivorCount)) is the one it actually destroyed. A survivor's MergedRounds must
                // carry only THOSE destroyed rounds, never a CO-survivor's own round: when MaxStackSize forces more
                // than one surviving stack (e.g. 3x60 into a 100 cap merges to two stacks, one destroyed), one
                // survivor landing must not falsely mark received a round that belongs to a co-survivor still
                // sitting unplaced (or refused) in the pile.
                var survivorCount = mergedItems.Count;
                var destroyedRounds = group.Skip(survivorCount).Select(h => h.Round).Distinct().ToList();

                for (var s = 0; s < survivorCount; s++)
                {
                    var originalHeld = group[s];
                    result.Add(new HeldPileItem(mergedItems[s], null, originalHeld.Round, destroyedRounds.Count > 0 ? destroyedRounds : null));
                }
            }

            return result;
        }

        /// <summary>
        /// Placement order (owner ruling, 2026-09-22): stackables first, grouped by wcid (a group's stacks stay
        /// adjacent, groups ordered by the group's total Value descending), then everything else by Value
        /// descending. Stable throughout - GroupBy and OrderBy/OrderByDescending preserve input order among ties -
        /// so a batch that needed no reordering comes back unchanged.
        /// </summary>
        public static List<(WorldObject Item, string RareFinderName)> SortForPlacement(List<(WorldObject Item, string RareFinderName)> items)
        {
            if (items == null || items.Count < 2)
                return items;

            var stackables = items.Where(i => i.Item is Stackable).ToList();
            var others = items.Where(i => !(i.Item is Stackable)).ToList();

            var result = new List<(WorldObject Item, string RareFinderName)>(items.Count);

            foreach (var group in stackables.GroupBy(i => i.Item.WeenieClassId)
                         .Select(g => (Total: g.Sum(x => (long)(x.Item.Value ?? 0)), Items: g.ToList()))
                         .OrderByDescending(g => g.Total))
            {
                result.AddRange(group.Items);
            }

            result.AddRange(others.OrderByDescending(i => (long)(i.Item.Value ?? 0)));

            return result;
        }

        /// <summary>The <see cref="HeldPileItem"/> shape of <see cref="SortForPlacement"/>, for the group pile.</summary>
        public static List<HeldPileItem> SortPileForPlacement(List<HeldPileItem> pile)
        {
            if (pile == null || pile.Count < 2)
                return pile;

            var stackables = pile.Where(h => h.Item is Stackable).ToList();
            var others = pile.Where(h => !(h.Item is Stackable)).ToList();

            var result = new List<HeldPileItem>(pile.Count);

            foreach (var group in stackables.GroupBy(h => h.Item.WeenieClassId)
                         .Select(g => (Total: g.Sum(x => (long)(x.Item.Value ?? 0)), Items: g.ToList()))
                         .OrderByDescending(g => g.Total))
            {
                result.AddRange(group.Items);
            }

            result.AddRange(others.OrderByDescending(h => (long)(h.Item.Value ?? 0)));

            return result;
        }

        /// <summary>
        /// Tops up a stack already sitting in <paramref name="cache"/> that matches <paramref name="item"/>'s
        /// signature, via SetStackSize, before <paramref name="item"/> ever takes a fresh cache slot (Threads item 8
        /// round 2, 2026-09-22): a member refilling the same chest across deal rounds, or a solo fill step landing
        /// after an earlier one already placed a matching stack, must not grow a second stack beside one already
        /// there. Quantity/value/burden are conserved and asserted per pair exactly like <see cref="MergeGroup"/>;
        /// any mismatch rolls that pair back and leaves both stacks as they were. A successful move also updates the
        /// CACHE's own aggregate EncumbranceVal/Value (mirrors Player_Inventory.cs:894-895's AdjustStack, which does
        /// the same for a player's root container after a stack-size change) and invalidates the run's recorded
        /// ThreadCacheSort signature for this cache (code review of PR #1284, finding 4: that signature is GUID-only
        /// and would otherwise not notice a stack it never saw added or removed). A viewer of an open cache is sent
        /// GameMessageSetStackSize for the topped-up stack, the same idiom Player_Inventory.cs:212 uses right after
        /// a stack-size-changing merge in a player's own inventory. Returns true when <paramref name="item"/> was
        /// fully absorbed and destroyed (nothing left needing a slot of its own); false when it does not match
        /// anything in the cache, or still holds a remainder after topping up what room existed.
        /// </summary>
        public static bool TopUpExisting(ThreadDungeonRun run, Container cache, WorldObject item)
        {
            if (cache == null || item == null)
                return false;

            var sig = SignatureOf(item);

            if (sig == null)
                return false;

            var max = (int)(item.MaxStackSize ?? 1);

            foreach (var existing in cache.Inventory.Values.Where(v => !ReferenceEquals(v, item) && SignatureOf(v) == sig).ToList())
            {
                if ((item.StackSize ?? 0) <= 0)
                    break;

                var room = max - (existing.StackSize ?? 1);

                if (room <= 0)
                    continue;

                var move = Math.Min(room, item.StackSize ?? 1);

                if (move <= 0)
                    continue;

                var existingSizeBefore = existing.StackSize ?? 1;
                var existingValueBefore = existing.Value ?? 0;
                var existingBurdenBefore = existing.EncumbranceVal ?? 0;
                var itemSizeBefore = item.StackSize ?? 1;
                var itemValueBefore = item.Value ?? 0;
                var itemBurdenBefore = item.EncumbranceVal ?? 0;

                existing.SetStackSize(existingSizeBefore + move);
                item.SetStackSize(itemSizeBefore - move);

                var valueAfter = (existing.Value ?? 0) + (item.Value ?? 0);
                var burdenAfter = (existing.EncumbranceVal ?? 0) + (item.EncumbranceVal ?? 0);
                var sizeAfter = (existing.StackSize ?? 0) + (item.StackSize ?? 0);

                if (valueAfter != existingValueBefore + itemValueBefore || burdenAfter != existingBurdenBefore + itemBurdenBefore
                    || sizeAfter != existingSizeBefore + itemSizeBefore)
                {
                    existing.StackSize = existingSizeBefore;
                    existing.Value = existingValueBefore;
                    existing.EncumbranceVal = existingBurdenBefore;
                    item.StackSize = itemSizeBefore;
                    item.Value = itemValueBefore;
                    item.EncumbranceVal = itemBurdenBefore;

                    log.Warn($"[DYNDUNGEON] stack top-up of wcid {item.WeenieClassId} into cache 0x{cache.Guid.Full:X8} did not conserve value/burden/size; left unmerged");
                    continue;
                }

                // The cache's own aggregates track its contents' totals (Container.EncumbranceVal/Value), the same
                // way Player_Inventory.AdjustStack updates the ROOT container after resizing a stack inside it. The
                // per-unit figures came out equal on both sides (the signature match already requires it), so
                // either side's StackUnit* would give the same increment; existing's is the one that is now IN the
                // cache, so it reads naturally as "what grew".
                cache.EncumbranceVal = (cache.EncumbranceVal ?? 0) + (existing.StackUnitEncumbrance ?? 0) * move;
                cache.Value = (cache.Value ?? 0) + (existing.StackUnitValue ?? 0) * move;

                // No longer invalidates a display-sort signature here (CLEANUP item 3, code review of PR #1284,
                // 2026-09-22): ThreadCacheSort.SortRunCaches stopped reading ThreadDungeonRun's signature store when
                // its per-cache loop was deleted in round 4, so the call this used to make was dead bookkeeping.

                if (cache.IsOpen)
                {
                    var viewer = PlayerManager.GetOnlinePlayer(cache.Viewer);
                    viewer?.Session?.Network.EnqueueSend(new GameMessageSetStackSize(existing));
                }
            }

            if ((item.StackSize ?? 0) > 0)
                return false;

            item.Destroy();
            return true;
        }

        /// <summary>
        /// Merges one group of identical stackables into ceil(total / MaxStackSize) stacks, reusing the group's first
        /// objects and destroying the rest. The totals are asserted before anything is destroyed; on any mismatch the
        /// group is left exactly as it was.
        /// </summary>
        private static List<WorldObject> MergeGroup(List<WorldObject> group, ref int removed)
        {
            var max = (int)(group[0].MaxStackSize ?? 1);
            long totalSize = group.Sum(i => (long)(i.StackSize ?? 1));
            long totalValue = group.Sum(i => (long)(i.Value ?? 0));
            long totalBurden = group.Sum(i => (long)(i.EncumbranceVal ?? 0));

            var stacks = (int)((totalSize + max - 1) / max);

            if (max < 2 || stacks >= group.Count)
                return group;

            // Snapshot so a failed check can restore every stack size it touched.
            var sizes = group.Select(i => i.StackSize).ToList();
            var values = group.Select(i => i.Value).ToList();
            var burdens = group.Select(i => i.EncumbranceVal).ToList();

            var left = totalSize;

            for (var s = 0; s < stacks; s++)
            {
                var size = (int)Math.Min(max, left);
                group[s].SetStackSize(size);
                left -= size;
            }

            var keptValue = group.Take(stacks).Sum(i => (long)(i.Value ?? 0));
            var keptBurden = group.Take(stacks).Sum(i => (long)(i.EncumbranceVal ?? 0));
            var keptSize = group.Take(stacks).Sum(i => (long)(i.StackSize ?? 1));

            if (keptValue != totalValue || keptBurden != totalBurden || keptSize != totalSize)
            {
                for (var s = 0; s < stacks; s++)
                {
                    group[s].StackSize = sizes[s];
                    group[s].Value = values[s];
                    group[s].EncumbranceVal = burdens[s];
                }

                log.Warn($"[DYNDUNGEON] stack consolidation of {group.Count} x wcid {group[0].WeenieClassId} did not conserve value/burden/size; left unmerged");
                return group;
            }

            foreach (var gone in group.Skip(stacks))
            {
                gone.Destroy();
                removed++;
            }

            return group.Take(stacks).ToList();
        }

        private static readonly HashSet<PropertyInt> StackOwnedInts = new HashSet<PropertyInt>
        {
            PropertyInt.StackSize,
            PropertyInt.Value,
            PropertyInt.EncumbranceVal,
            PropertyInt.CreationTimestamp,

            // Placement-context, not identity: an item Consolidate compares is always free-floating (never yet in a
            // container), but TopUpExisting (round 2, 2026-09-22) compares a free incoming item against one ALREADY
            // sitting in the cache, which Container.TryAddToInventory stamps with its slot and resting state
            // (Container.cs:559, :562, :564). Without these exclusions, two otherwise-identical stacks would never
            // match once one of them was placed.
            PropertyInt.PlacementPosition,
            PropertyInt.Placement,
        };

        /// <summary>The PropertyInstanceId counterpart of <see cref="StackOwnedInts"/>: Container.cs:561-562 stamps these on add.</summary>
        private static readonly HashSet<PropertyInstanceId> StackOwnedInstanceIds = new HashSet<PropertyInstanceId>
        {
            PropertyInstanceId.Container,
            PropertyInstanceId.Owner,
        };

        /// <summary>
        /// The merge key: null for anything that must never merge (see the class comment), else a canonical string of
        /// every property that must be equal. Two items merge exactly when their keys are equal.
        /// </summary>
        internal static string SignatureOf(WorldObject item)
        {
            if (!(item is Stackable) || item.IsDestroyed)
                return null;

            if ((item.MaxStackSize ?? 1) < 2)
                return null;

            var biota = item.Biota;

            if (biota == null || Any(biota.PropertiesCreateList) || Any(biota.PropertiesEmote) || Any(biota.PropertiesGenerator)
                || biota.PropertiesBook != null || Any(biota.PropertiesBookPageData) || Any(biota.PropertiesSkill) || Any(biota.PropertiesAttribute)
                || Any(biota.PropertiesAttribute2nd) || Any(biota.PropertiesBodyPart) || Any(biota.PropertiesEnchantmentRegistry)
                || Any(biota.PropertiesPosition) || Any(biota.PropertiesAllegiance) || Any(biota.HousePermissions) || Any(biota.PropertiesEventFilter))
            {
                return null;
            }

            // Self-consistent stacks only: SetStackSize recomputes Value and EncumbranceVal from the per-unit values.
            var size = Math.Max(1, item.StackSize ?? 1);
            var unitValue = item.StackUnitValue ?? (item.Value ?? 0) / size;
            var unitBurden = item.StackUnitEncumbrance ?? (item.EncumbranceVal ?? 0) / size;

            if ((item.Value ?? 0) != unitValue * size || (item.EncumbranceVal ?? 0) != unitBurden * size)
                return null;

            var sb = new StringBuilder();
            sb.Append(item.GetType().FullName).Append('|').Append(item.WeenieClassId).Append('|');

            foreach (var kv in item.GetAllPropertyInt().Where(kv => !StackOwnedInts.Contains(kv.Key)).OrderBy(kv => kv.Key))
                sb.Append("i").Append((int)kv.Key).Append('=').Append(kv.Value).Append(';');
            foreach (var kv in item.GetAllPropertyInt64().OrderBy(kv => kv.Key))
                sb.Append("l").Append((int)kv.Key).Append('=').Append(kv.Value).Append(';');
            foreach (var kv in item.GetAllPropertyBools().OrderBy(kv => kv.Key))
                sb.Append("b").Append((int)kv.Key).Append('=').Append(kv.Value).Append(';');
            foreach (var kv in item.GetAllPropertyFloat().OrderBy(kv => kv.Key))
                sb.Append("f").Append((int)kv.Key).Append('=').Append(kv.Value.ToString("R")).Append(';');
            foreach (var kv in item.GetAllPropertyString().OrderBy(kv => kv.Key))
                sb.Append("s").Append((int)kv.Key).Append('=').Append(kv.Value?.Length ?? -1).Append(':').Append(kv.Value).Append(';');
            foreach (var kv in item.GetAllPropertyDataId().OrderBy(kv => kv.Key))
                sb.Append("d").Append((int)kv.Key).Append('=').Append(kv.Value).Append(';');
            foreach (var kv in item.GetAllPropertyInstanceId().Where(kv => !StackOwnedInstanceIds.Contains(kv.Key)).OrderBy(kv => kv.Key))
                sb.Append("n").Append((int)kv.Key).Append('=').Append(kv.Value).Append(';');

            if (biota.PropertiesSpellBook != null)
                foreach (var kv in biota.PropertiesSpellBook.OrderBy(kv => kv.Key))
                    sb.Append("sp").Append(kv.Key).Append('=').Append(kv.Value.ToString("R")).Append(';');

            if (biota.PropertiesPalette != null)
                foreach (var p in biota.PropertiesPalette)
                    sb.Append("pa").Append(p.SubPaletteId).Append(',').Append(p.Offset).Append(',').Append(p.Length).Append(';');

            if (biota.PropertiesTextureMap != null)
                foreach (var tm in biota.PropertiesTextureMap)
                    sb.Append("tm").Append(tm.PartIndex).Append(',').Append(tm.OldTexture).Append(',').Append(tm.NewTexture).Append(';');

            if (biota.PropertiesAnimPart != null)
                foreach (var ap in biota.PropertiesAnimPart)
                    sb.Append("ap").Append(ap.Index).Append(',').Append(ap.AnimationId).Append(';');

            return sb.ToString();
        }

        private static bool Any<T>(ICollection<T> c) => c != null && c.Count > 0;
    }
}
