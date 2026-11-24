using System.Collections.Generic;

using ACE.Server.KnotLoot;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

namespace ACE.Server.Pinnacles
{
    /// <summary>
    /// What a won Pinnacle puts in the cache: the roll of the pinnacle's reward table (PinnacleDef.RewardTable) at the
    /// Knot level. An unknown or missing table rolls EMPTY: the cache still forms (and the Knot is still consumed), it
    /// just holds nothing.
    /// </summary>
    public static class PinnacleRewards
    {
        /// <summary>
        /// Fresh, unplaced items for the cache, or an empty list. The caller owns them: anything it cannot place is
        /// destroyed by the caller.
        /// </summary>
        public static List<WorldObject> Roll(int knotLevel, Player owner, string rewardTable)
        {
            var rewards = KnotLootService.RollLive(rewardTable, knotLevel) ?? new List<WorldObject>();

            // An independent roll (tunable pinnacle_cache_foolproof_chance, 50%) for one random Foolproof salvage bag on top of
            // the table's rewards, placed FIRST so it shows in the cache's first slot. Rolled even when the table is empty.
            var foolproof = FoolproofSalvage.TryRoll(FoolproofSalvage.PinnacleCacheChance);

            if (foolproof != null)
                rewards.Insert(0, foolproof);

            return rewards;
        }
    }
}
