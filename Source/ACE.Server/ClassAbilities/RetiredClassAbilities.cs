using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// A static table of retired class abilities' quest-key suffixes (the value that follows
    /// <see cref="ClassAbilityRegistry.QuestKeyPrefix"/> in a CharacterPropertiesQuestRegistry row name) mapped
    /// to their historical CostPerRank array, display name, and max rank - so a character still holding an
    /// orphaned rank in a retired ability can be priced and refunded after its definition is gone from
    /// <see cref="ClassAbilityRegistry.Abilities"/>. Pure and static, no Player dependency, matching the
    /// ClassAbilityScaling / ClassAbilityLumCurve testability pattern in this folder.
    ///
    /// A row lands here only once retired: the id stays reserved in <see cref="ClassAbilityId"/> (never
    /// re-used), the handler is unregistered, and the quest-key suffix + historical cost/maxRank are recorded
    /// below from the definition's last committed state (verified against git history at the time the ability
    /// was retired, not recalled from memory).
    /// </summary>
    public static class RetiredClassAbilities
    {
        private class RetiredEntry
        {
            public string DisplayName;
            public int[] CostPerRank;
            public int MaxRank;
        }

        // Keyed by quest-key suffix (ClassAbilityDefinition.Name at the time of retirement - lowercase,
        // matches how ClassAbilityRegistry.QuestKey builds the row name). Case-insensitive lookup, same as
        // ClassAbilityRegistry.TryGetByName.
        private static readonly Dictionary<string, RetiredEntry> Retired = new(StringComparer.OrdinalIgnoreCase)
        {
            // Retired 2026-08-03 (skill redistribution): Heavy Weapons and Light Weapons split out into their
            // own homes; the bundle itself is gone. CostPerRank {1,1,1} / MaxRank 3, matching BundleStatAbility.
            ["advanced_weaponry"] = new RetiredEntry
            {
                DisplayName = "Advanced Weaponry",
                CostPerRank = new[] { 1, 1, 1 },
                MaxRank = 3,
            },

            // Retired 2026-08-03 (skill redistribution): Sneak Attack and Deception moved into Rogue Training;
            // Dirty Fighting is unhomed. CostPerRank {1,1,1} / MaxRank 3, matching BundleStatAbility.
            ["questionable_tactics"] = new RetiredEntry
            {
                DisplayName = "Questionable Tactics",
                CostPerRank = new[] { 1, 1, 1 },
                MaxRank = 3,
            },

            // Retired 2026-07-18 (Arc/Streak base ratio ~2.0x = +100% void damage - far too strong). Verified
            // against StreakToArcAbility.cs as it stood immediately before deletion (git log -S StreakToArc,
            // commit b9af96afd): Name = "streaktoarc" (no underscore), MaxRank = 1, CostPerRank = { 3 } - NOT
            // the {3,3,3}/MaxRank 3 shape a 3-tier skill would have; Streak-to-Arc was always a flat one-time
            // unlock.
            ["streaktoarc"] = new RetiredEntry
            {
                DisplayName = "Streak-to-Arc",
                CostPerRank = new[] { 3 },
                MaxRank = 1,
            },
        };

        /// <summary>
        /// Computes the class ability point refund owed for an orphaned rank in a retired ability. Clamps
        /// rank to the ability's historical MaxRank before summing cost, exactly like
        /// ClassAbilityDefinition.CumulativeCost. Returns FALSE (no refund, no display name) for a quest key
        /// suffix this table doesn't recognize - an unresolvable row that isn't retired-and-known must be
        /// left alone by the caller, never guessed at.
        /// </summary>
        public static bool TryGetRefund(string questKeySuffix, int rank, out int points, out string displayName)
        {
            points = 0;
            displayName = null;

            if (questKeySuffix == null || !Retired.TryGetValue(questKeySuffix, out var entry))
                return false;

            displayName = entry.DisplayName;
            points = entry.CostPerRank.Take(Math.Clamp(rank, 0, entry.MaxRank)).Sum();
            return true;
        }
    }
}
