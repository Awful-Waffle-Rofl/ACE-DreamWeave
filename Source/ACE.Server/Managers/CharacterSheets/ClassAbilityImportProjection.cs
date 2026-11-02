using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.ClassAbilities;

namespace ACE.Server.Managers.CharacterSheets
{
    /// <summary>One quest registry row as the import projection reads it: the quest name and its completion count.</summary>
    public readonly record struct ImportQuestRow(string Name, int Completions);

    /// <summary>One ability rank on the wire: the canonical definition name (never the stored row spelling) and the rank.</summary>
    public sealed class ImportRank
    {
        public string Id { get; set; }
        public int Rank { get; set; }
    }

    /// <summary>
    /// What the class ability planner import reads off a character: ranks, bought-point counts, the point
    /// balances, and the character's full set of completed (non-ability) quest names. The quest set is held
    /// internally and filtered per request by <see cref="ClassAbilityImportProjection.FilterCompleted"/>; it is
    /// internal so it can never reach a serializer.
    /// </summary>
    public sealed class ClassAbilityImportBlock
    {
        public int LumPointsBought { get; set; }
        public int XpPointsBought { get; set; }
        public List<ImportRank> Ranks { get; set; } = new();
        public List<ImportRank> RetiredRanks { get; set; } = new();
        public int PointsEarned { get; set; }
        public int PointsAvailable { get; set; }
        public bool RespecPending { get; set; }

        internal HashSet<string> CompletedQuests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PURE projection behind GET /v1/accounts/me/characters/{guid}/class-abilities: quest rows plus PropertyInt
    /// values in, the import block out. No Player, no database, no PropertyManager - the glue in
    /// CharacterSheetProjector.ProjectOwner only gathers the inputs, so this is testable without a Player.
    /// </summary>
    public static class ClassAbilityImportProjection
    {
        /// <summary>The quest query parameter's per-value rule. Identical in the ACE route, the market proxy and ClassAbilityCapQuestTests.</summary>
        public const string QuestParamPattern = "^[A-Za-z0-9_]{1,64}$";

        /// <summary>At most this many quest values are read from one request; the rest are ignored.</summary>
        public const int MaxQuestParams = 32;

        // The documented pattern with its end anchor made strict: in .NET a bare `$` also matches before a
        // trailing "\n", which would let "name%0A" through as valid. \z anchors at the true end of the string.
        private static readonly Regex QuestParamRegex = new(QuestParamPattern.Substring(0, QuestParamPattern.Length - 1) + @"\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsValidQuestParam(string value) => value != null && QuestParamRegex.IsMatch(value);

        public static ClassAbilityImportBlock Project(
            IEnumerable<ImportQuestRow> rows, int lumPointsBought, int xpPointsBought, int pointsEarned, int pointsAvailable, bool respecPending)
        {
            var block = new ClassAbilityImportBlock
            {
                LumPointsBought = lumPointsBought,
                XpPointsBought = xpPointsBought,
                PointsEarned = pointsEarned,
                PointsAvailable = pointsAvailable,
                RespecPending = respecPending,
            };

            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            var retired = new Dictionary<string, int>(StringComparer.Ordinal);

            Dictionary<string, (string Name, int MaxRank)> retiredByKey = null;

            foreach (var row in rows ?? Enumerable.Empty<ImportQuestRow>())
            {
                if (string.IsNullOrEmpty(row.Name))
                    continue;

                if (row.Name.StartsWith(ClassAbilityRegistry.QuestKeyPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // An ability row, never a CAP quest stamp.
                    if (ClassAbilityRegistry.TryGetByQuestKey(row.Name, out var def))
                    {
                        var rank = Math.Clamp(row.Completions, 0, def.MaxRank);
                        if (rank > 0 && (!ranks.TryGetValue(def.Name, out var held) || rank > held))
                            ranks[def.Name] = rank;
                    }
                    else
                    {
                        retiredByKey ??= RetiredClassAbilities.All().ToDictionary(r => r.Name, r => (r.Name, r.MaxRank), StringComparer.OrdinalIgnoreCase);

                        var suffix = row.Name.Substring(ClassAbilityRegistry.QuestKeyPrefix.Length);
                        if (retiredByKey.TryGetValue(suffix, out var old))
                        {
                            var rank = Math.Clamp(row.Completions, 0, old.MaxRank);
                            if (rank > 0 && (!retired.TryGetValue(old.Name, out var held) || rank > held))
                                retired[old.Name] = rank;
                        }
                        // else: unknown and not retired - neither list.
                    }

                    continue;
                }

                if (row.Completions >= 1)
                    block.CompletedQuests.Add(row.Name);
            }

            block.Ranks = ranks.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new ImportRank { Id = kv.Key, Rank = kv.Value }).ToList();
            block.RetiredRanks = retired.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new ImportRank { Id = kv.Key, Rank = kv.Value }).ToList();

            return block;
        }

        /// <summary>
        /// The requested quest names the character has completed, echoed in the REQUESTED spelling and order.
        /// Only the first <see cref="MaxQuestParams"/> values are read; a value that fails
        /// <see cref="QuestParamPattern"/> is ignored (no error); a repeat (case-insensitive) is echoed once.
        /// </summary>
        public static List<string> FilterCompleted(ClassAbilityImportBlock block, IEnumerable<string> requested)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in (requested ?? Enumerable.Empty<string>()).Take(MaxQuestParams))
            {
                if (!IsValidQuestParam(name) || !seen.Add(name))
                    continue;

                if (block.CompletedQuests.Contains(name))
                    result.Add(name);
            }

            return result;
        }
    }
}
