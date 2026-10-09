using System;
using System.Collections.Generic;
using System.Linq;

using log4net;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Strips room-blanketing and named-drain spells from a run creature's spell book at spawn time (owner
    /// ruling 2026-09-08). Threads draws its roster from retail content, and some of those weenies carry
    /// spells that make sense on an open landblock but not inside a small run copy - 360-degree ring spells
    /// and multi-projectile waves/volleys that blanket a room regardless of where the player stands, plus two
    /// life-drain spells the owner ruled out by name (resolved to ids below the naming trap they sit in).
    ///
    /// Pure and side-effect free by design: no database, no dat files, no log4net configuration required to
    /// exercise the decision logic in a unit test. <see cref="ThreadDungeonSpawner"/> is the only caller and
    /// owns the log line, the PropertyManager reads, and the memoized NumProjectiles lookup.
    ///
    /// BAN BY ID, NEVER BY NAME. Spell names repeat across ids in ace_world - "Exsanguinating Wave" is the
    /// worked example (3940 on 19 weenies, 3999 on 1; both live), and "Incantation of Lightning Bolt" also
    /// appears under two ids - so a name match would be ambiguous and could silently hit the wrong spell.
    /// </summary>
    public static class DungeonSpellFilter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The projectile-count rule's threshold: a spell row with MORE than this many projectiles is the
        /// whole AoE class (owner ruling 2026-09-08) - measured against the local ace_world, this selects
        /// exactly 16 spells across the level-250-and-up roster: ten 360-degree rings at 9 projectiles, two
        /// waves at 8, and four volley/blast spells at 5.
        /// </summary>
        public const int MaxAllowedProjectiles = 1;

        /// <summary>
        /// The default banned-id list (owner ruling 2026-09-08): Exsanguinating Wave under BOTH of its live
        /// ids (3940 on 19 weenies, 3999 on 1), and Poisoned Vitality (6167). Held as a compile-time constant
        /// so <see cref="ACE.Server.Managers.PropertyManager"/>'s registration and this filter's own fallback
        /// can never drift apart, the same contract DungeonPopulationLimits.DefaultTrashHealthFloorRatio uses.
        /// </summary>
        public const string DefaultBannedSpellIds = "3940,3999,6167";

        /// <summary>
        /// The master switch's compile-time default: on. False reproduces today's behaviour exactly - no
        /// spell is ever removed from a run creature's book.
        /// </summary>
        public const bool DefaultStripAoeSpellsEnabled = true;

        /// <summary>
        /// Parses a comma-separated spell-id list, tolerantly. Follows the exact style of the shared landblock
        /// list parser (ACE.Server.Realms.LandblockRealmList, behind mule_landblocks and friends): a malformed entry
        /// is skipped with a logged warning, never thrown - a bad config string must never break a spawn.
        /// An empty or whitespace-only string returns an empty set, which the caller reads as "the id list is
        /// off; only the projectile rule applies".
        /// </summary>
        public static HashSet<int> ParseBannedIds(string csv)
        {
            var result = new HashSet<int>();

            if (string.IsNullOrWhiteSpace(csv))
                return result;

            foreach (var token in csv.Split(','))
            {
                var trimmed = token.Trim();

                if (trimmed.Length == 0)
                    continue;

                if (int.TryParse(trimmed, out var id) && id > 0)
                {
                    result.Add(id);
                }
                else
                {
                    log.Warn($"DungeonSpellFilter.ParseBannedIds: skipping malformed entry '{token}'.");
                }
            }

            return result;
        }

        /// <summary>
        /// Decides which spells to remove from a creature's spell book, removes them from
        /// <paramref name="book"/> in place, and returns their ids for logging. A spell is removed when either its NumProjectiles
        /// (via <paramref name="projectilesOf"/>) is greater than <see cref="MaxAllowedProjectiles"/>, or its
        /// id is in <paramref name="bannedIds"/>.
        ///
        /// SAFETY GUARD: if removing every flagged spell would leave the book EMPTY and
        /// <paramref name="hasDamagingBodyPart"/> is false, nothing is removed - an empty list is returned and
        /// the book is left exactly as authored. A monster with no melee and no spells stands there doing
        /// nothing, which is worse than the spell being cast. The guard is evaluated ONLY in that combination:
        /// a would-be-emptied book with at least one damaging body part is stripped normally, and a
        /// non-emptying strip never engages the guard at all.
        /// </summary>
        public static IReadOnlyList<int> Strip(IDictionary<int, float> book, ISet<int> bannedIds, Func<int, int> projectilesOf,
            bool hasDamagingBodyPart)
        {
            if (book == null || book.Count == 0)
                return Array.Empty<int>();

            bannedIds ??= new HashSet<int>();
            projectilesOf ??= (_ => 0);

            var toRemove = new List<int>();

            foreach (var id in book.Keys)
            {
                if (bannedIds.Contains(id) || projectilesOf(id) > MaxAllowedProjectiles)
                    toRemove.Add(id);
            }

            if (toRemove.Count == 0)
                return Array.Empty<int>();

            // The safety guard: would this empty the book, and does the creature have nothing else to fall
            // back on? Only then is the strip refused outright.
            if (toRemove.Count == book.Count && !hasDamagingBodyPart)
                return Array.Empty<int>();

            foreach (var id in toRemove)
                book.Remove(id);

            return toRemove;
        }
    }
}
