using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities.Abilities;

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

            // Retired 2026-08-17 (Berserker/Rogue balance pass): the low-health melee ramp is replaced in the
            // Berserker T2 slot by Break Armor. Verified against BloodFuryAbility.cs as it stood immediately
            // before deletion: Name = "bloodfury" (no underscore), MaxRank = 3, CostPerRank = { 3, 3, 3 }.
            ["bloodfury"] = new RetiredEntry
            {
                DisplayName = "Blood Fury",
                CostPerRank = new[] { 3, 3, 3 },
                MaxRank = 3,
            },

            // Retired 2026-09-12 (class ability overhaul): Archer Tier 2 was a wall of silent damage riders
            // and the stamina surcharge played badly; Pinning Shot takes the slot. Verified against
            // HeavyDrawAbility.cs as it stood immediately before deletion (lines 24/29/30): Name =
            // "heavydraw" (no underscore), MaxRank = 3, CostPerRank = { 1, 1, 1 } - NOT the 2/2/2 several of
            // its Tier 2 neighbours carried.
            ["heavydraw"] = new RetiredEntry
            {
                DisplayName = "Heavy Draw",
                CostPerRank = new[] { 1, 1, 1 },
                MaxRank = 3,
            },

            // Retired 2026-09-12 (class ability overhaul): it only did anything for a character who had also
            // bought Rogue's Parry, which made it a dead entry for most Vanguards; Kinetic Charge takes the
            // slot. Verified against ShieldCheckAbility.cs as it stood immediately before deletion (lines
            // 22/26/27): Name = "shieldcheck" (no underscore), MaxRank = 3, CostPerRank = { 2, 2, 2 }.
            ["shieldcheck"] = new RetiredEntry
            {
                DisplayName = "Shield Check",
                CostPerRank = new[] { 2, 2, 2 },
                MaxRank = 3,
            },

            // Retired 2026-09-12 (class ability overhaul): its stacking avoidance overlapped Parry inside the
            // same pooled cap. Killer Instinct replaces it in the class but sits at Rogue TIER 3, not the
            // Tier 2 an earlier version of this comment claimed: T3 is the signed-off placement and the only
            // one that reproduces Rogue's 44 CAP total (ClassAbilityDefinition's KillerInstinct entry
            // carries the arithmetic - T3 would otherwise be 9 and T2 18, against 15/15). Verified against
            // SurefootedAbility.cs as it stood immediately before deletion (lines 38/43/44): Name =
            // "surefooted" (no underscore), MaxRank = 3, CostPerRank = { 2, 2, 2 }.
            ["surefooted"] = new RetiredEntry
            {
                DisplayName = "Surefooted",
                CostPerRank = new[] { 2, 2, 2 },
                MaxRank = 3,
            },

            // Retired 2026-09-16 (Missile Defense ownership fix): Missile Defense is owned solely by Archer
            // Training, and the standalone Enhanced Missile Defense entry double-dipped with it - a player
            // buying both got two additive bonuses to the same skill from two different purchases. Verified
            // against EnhancedStatAbility.cs as it stood immediately before the exclusion (SkillCost line 56,
            // MaxTier line 46): Name = "enhanced_missiledefense" ("enhanced_" + Skill.MissileDefense.ToString()
            // .ToLowerInvariant()), MaxRank = 3, CostPerRank = { 1, 1, 1 } - the flat Enhanced-skill rate, not
            // a bespoke one.
            ["enhanced_missiledefense"] = new RetiredEntry
            {
                DisplayName = "Enhanced Missile Defense",
                CostPerRank = new[] { 1, 1, 1 },
                MaxRank = 3,
            },
        };

        /// <summary>
        /// The retired abilities' NUMERIC ids, mapped to the quest-key suffix that keys <see cref="Retired"/>.
        ///
        /// Ownership persists by NAME, so the table above is the authority and this is only a second way in.
        /// It exists because one thing does key off the numeric id: a class ability TOKEN carries
        /// <see cref="ACE.Entity.Enum.Properties.PropertyInt.ClassAbilityTokenId"/>, and an unused token for a
        /// since-retired ability is still sitting in someone's pack. Without this map,
        /// Player.RefundUnusedVoucher can't resolve it (the id is deliberately no longer in
        /// <see cref="ClassAbilityRegistry.Abilities"/>) and the prepaid points are stranded.
        ///
        /// Every id here MUST also stay reserved in <see cref="ClassAbilityId"/> and keep its
        /// <see cref="ClassAbilityTokenCatalog.TokenAbilities"/> slot, or the wcid a live token was minted
        /// from stops meaning what it meant.
        /// </summary>
        private static readonly Dictionary<ClassAbilityId, string> RetiredIds = new()
        {
            [ClassAbilityId.AdvancedWeaponry] = "advanced_weaponry",
            [ClassAbilityId.QuestionableTactics] = "questionable_tactics",
            [ClassAbilityId.StreakToArc] = "streaktoarc",
            [ClassAbilityId.BloodFury] = "bloodfury",
            [ClassAbilityId.HeavyDraw] = "heavydraw",
            [ClassAbilityId.ShieldCheck] = "shieldcheck",
            [ClassAbilityId.Surefooted] = "surefooted",
            [EnhancedStatAbility.ClassIdForSkill(Skill.MissileDefense)] = "enhanced_missiledefense",
        };

        /// <summary>
        /// Every retired ability as a flat read-only row - numeric id, quest-key suffix, display name, max
        /// rank and historical cost. Exists for tooling that must SHOW a retired entry rather than silently
        /// drop it: the planner catalog under tools/ca-planner emits these with "retired": true so a saved
        /// build referencing one can be explained instead of appearing corrupt. Ordered by numeric id, so
        /// the enumeration is stable across runs.
        /// </summary>
        public static IEnumerable<(ClassAbilityId Id, string Name, string DisplayName, int MaxRank, IReadOnlyList<int> CostPerRank)> All()
        {
            foreach (var kvp in RetiredIds.OrderBy(kvp => (int)kvp.Key))
            {
                var entry = Retired[kvp.Value];
                yield return (kvp.Key, kvp.Value, entry.DisplayName, entry.MaxRank, entry.CostPerRank);
            }
        }

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

        /// <summary>
        /// The id-keyed form, for the one caller that only has a numeric id: an unused prepaid TOKEN whose
        /// <see cref="ACE.Entity.Enum.Properties.PropertyInt.ClassAbilityTokenId"/> names a retired ability.
        ///
        /// <paramref name="tier"/> is the token's rank (1-based), and the refund is that ONE rank's historical
        /// cost - NOT the cumulative cost the string overload returns. That is the difference between the two:
        /// the string overload prices ranks a character actually holds and has paid for cumulatively, while a
        /// token is a single prepaid rank. Returns FALSE for an id this table doesn't recognize, or a tier
        /// outside the ability's historical CostPerRank.
        /// </summary>
        public static bool TryGetRefund(ClassAbilityId id, int tier, out int points, out string displayName)
        {
            points = 0;
            displayName = null;

            if (!RetiredIds.TryGetValue(id, out var suffix) || !Retired.TryGetValue(suffix, out var entry))
                return false;

            if (tier < 1 || tier > entry.CostPerRank.Length)
                return false;

            displayName = entry.DisplayName;
            points = entry.CostPerRank[tier - 1];
            return true;
        }
    }
}
