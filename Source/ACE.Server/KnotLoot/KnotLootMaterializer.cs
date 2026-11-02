using System;
using System.Collections.Generic;

using log4net;

using ACE.Common;
using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.KnotLoot
{
    /// <summary>
    /// Turns rolled drops into fresh, unplaced items. A trait drop gets exactly three stamps: its level, its table and
    /// its trait position u. A stack drop gets its stack size, clamped to the item's MaxStackSize. A drop carrying a
    /// mutation gets that mutation (UpgradedCloak), drawing from <c>next</c>. Nothing else is written.
    /// </summary>
    public static class KnotLootMaterializer
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static List<WorldObject> Create(IReadOnlyList<KnotLootDrop> drops, string tableKey, int knotLevel, Func<uint, WorldObject> create, Func<double> next = null)
        {
            var items = new List<WorldObject>();

            next ??= () => ThreadSafeRandom.Next(0.0f, 1.0f);

            foreach (var drop in drops)
            {
                var item = create(drop.Wcid);

                if (item == null)
                {
                    // most often a table entry whose weenie is not in the world database (content not applied yet)
                    log.Warn($"[KNOTLOOT] table '{tableKey}': wcid {drop.Wcid} could not be created; the drop is skipped");
                    continue;
                }

                if (drop.Trait != null && KnotTraitRegistry.TryGet(drop.Trait.Value, out var definition))
                {
                    item.SetProperty(PropertyInt.KnotLootLevel, knotLevel);
                    item.SetProperty(PropertyString.KnotLootTable, tableKey);
                    item.SetProperty(definition.Property, drop.U);
                }

                if (drop.Mutation == KnotLootMutation.UpgradedCloak)
                    UpgradedCloak.Mutate(item, knotLevel, next);

                if (drop.StackSize > 1)
                {
                    // a huge currency setting must never build a stack above the item's own cap
                    var stackSize = Math.Min(drop.StackSize, item.MaxStackSize ?? drop.StackSize);

                    if (stackSize < drop.StackSize)
                        log.Warn($"[KNOTLOOT] table '{tableKey}': a stack of {drop.StackSize} x wcid {drop.Wcid} was clamped to its MaxStackSize {stackSize}");

                    item.SetStackSize(stackSize);
                }

                items.Add(item);
            }

            return items;
        }
    }
}
