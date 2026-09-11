using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;

namespace ACE.Entity.Models
{
    /// <summary>
    /// A single item reward predicted along one forecasted emote-execution path.
    /// </summary>
    public class ForecastedReward
    {
        public uint WeenieClassId { get; }

        public int Amount { get; }

        public bool IsRandomTreasure { get; }

        public ForecastedReward(uint weenieClassId, int amount, bool isRandomTreasure)
        {
            WeenieClassId = weenieClassId;
            Amount = amount;
            IsRandomTreasure = isRandomTreasure;
        }
    }

    /// <summary>
    /// A pure, offline forecast of the item rewards an emote set (and everything it branches into)
    /// could hand out, without executing any emotes or touching a live WorldObject/Player.
    ///
    /// This deliberately does NOT model TakeItems (capacity freed by consuming the turn-in item) or
    /// any other capacity mutation - callers combine this forecast with their own capacity accounting.
    /// </summary>
    public class EmoteRewardForecast
    {
        /// <summary>
        /// Every distinct reward path discovered while walking the emote graph. Each path is the set
        /// of Give/CreateTreasure rewards encountered along one sequence of branch choices. Order of
        /// rewards within a path does not matter - only totals per path.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<ForecastedReward>> Paths { get; }

        /// <summary>
        /// True if the walk hit maxDepth, maxPaths, or a cycle before it could fully expand every
        /// path. This is a fail-open signal - a Truncated forecast should be treated as "there may be
        /// more/worse paths than reported", never as complete.
        /// </summary>
        public bool Truncated { get; }

        private EmoteRewardForecast(IReadOnlyList<IReadOnlyList<ForecastedReward>> paths, bool truncated)
        {
            Paths = paths;
            Truncated = truncated;
        }

        /// <summary>
        /// The emote types EmoteManager.EmoteIsBranchingType treats as branching, mapped to the
        /// EmoteCategory (or, for Goto, the single category) their ExecuteEmoteSet call site can
        /// redirect execution to. Copied whole from EmoteManager.EmoteIsBranchingType
        /// (Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs:1762-1808) so this table cannot
        /// silently drift out of sync with the server's real branching set - if that switch statement
        /// changes, this table must change with it.
        /// </summary>
        private static readonly Dictionary<EmoteType, EmoteCategory[]> BranchTargets = new Dictionary<EmoteType, EmoteCategory[]>
        {
            [EmoteType.UpdateQuest] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqQuest] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqQuestSolves] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            // The 13 stat-inquiry types below all fall back to EmoteCategory.TestNoQuality when the
            // inquired stat/attribute/skill is null (e.g. EmoteManager.cs:480-497 InqBoolStat,
            // :617-632 InqIntStat) - a Give reward living only in a TestNoQuality set was previously
            // invisible to every forecast path.
            [EmoteType.InqBoolStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqIntStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqFloatStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqStringStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqAttributeStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqRawAttributeStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqSecondaryAttributeStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqRawSecondaryAttributeStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqSkillStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqRawSkillStat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqSkillTrained] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqSkillSpecialized] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqInt64Stat] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure, EmoteCategory.TestNoQuality },
            [EmoteType.InqEvent] = new[] { EmoteCategory.EventSuccess, EmoteCategory.EventFailure },
            // InqFellowQuest/UpdateFellowQuest branch to QuestNoFellow when the player has no fellowship
            // (EmoteManager.cs:527-544, :1490-1524).
            [EmoteType.InqFellowQuest] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure, EmoteCategory.QuestNoFellow },
            // InqFellowNum can branch to TestNoFellow (EmoteManager.cs:510-524).
            [EmoteType.InqFellowNum] = new[] { EmoteCategory.NumFellowsSuccess, EmoteCategory.NumFellowsFailure, EmoteCategory.TestNoFellow },
            [EmoteType.UpdateFellowQuest] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure, EmoteCategory.QuestNoFellow },
            [EmoteType.Goto] = new[] { EmoteCategory.GotoSet },
            [EmoteType.InqNumCharacterTitles] = new[] { EmoteCategory.NumCharacterTitlesSuccess, EmoteCategory.NumCharacterTitlesFailure },
            [EmoteType.InqYesNo] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure },
            [EmoteType.InqOwnsItems] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure },
            [EmoteType.UpdateMyQuest] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqMyQuest] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqMyQuestSolves] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqPackSpace] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure },
            [EmoteType.InqQuestBitsOn] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqQuestBitsOff] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqMyQuestBitsOn] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqMyQuestBitsOff] = new[] { EmoteCategory.QuestSuccess, EmoteCategory.QuestFailure },
            [EmoteType.InqContractsFull] = new[] { EmoteCategory.TestSuccess, EmoteCategory.TestFailure },
        };

        /// <summary>
        /// Walks every possible execution path starting at <paramref name="root"/>, following branch
        /// rows into candidate sets drawn from <paramref name="allSets"/>, and totals up the item
        /// rewards (Give / CreateTreasure) along each path.
        /// </summary>
        public static EmoteRewardForecast Build(PropertiesEmote root, IEnumerable<PropertiesEmote> allSets, int maxDepth = 8, int maxPaths = 64)
        {
            var paths = new List<IReadOnlyList<ForecastedReward>>();

            if (root == null)
                return new EmoteRewardForecast(paths, false);

            var allSetsList = allSets as IList<PropertiesEmote> ?? allSets?.ToList() ?? new List<PropertiesEmote>();

            var truncated = false;

            var visited = new HashSet<(EmoteCategory Category, string Quest)>
            {
                (root.Category, (root.Quest ?? string.Empty).ToLowerInvariant())
            };

            Walk(root, 0, null, allSetsList, new List<ForecastedReward>(), visited, 1, maxDepth, maxPaths, paths, ref truncated);

            return new EmoteRewardForecast(paths, truncated);
        }

        /// <summary>
        /// One link in the "what to resume after this set finishes" chain. Mirrors EmoteManager.DoEnqueue
        /// (Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs:1730-1736): ExecuteEmote runs a
        /// branching row's nested target set, and then the OUTER set's next row still runs unconditionally
        /// - this holds for every branching type, Goto included ("redirect" describes the dispatch, not
        /// the control flow). So a branch does not truncate the outer set's flow; it is a persistent,
        /// immutable stack frame each candidate walk resumes into once its own rows run out.
        /// </summary>
        private sealed class ContinuationFrame
        {
            public readonly PropertiesEmote Set;
            public readonly int ResumeIndex;
            public readonly ContinuationFrame Next;

            public ContinuationFrame(PropertiesEmote set, int resumeIndex, ContinuationFrame next)
            {
                Set = set;
                ResumeIndex = resumeIndex;
                Next = next;
            }
        }

        private static void Walk(
            PropertiesEmote set,
            int startIndex,
            ContinuationFrame continuation,
            IList<PropertiesEmote> allSets,
            List<ForecastedReward> rewards,
            HashSet<(EmoteCategory Category, string Quest)> visited,
            int depth,
            int maxDepth,
            int maxPaths,
            List<IReadOnlyList<ForecastedReward>> paths,
            ref bool truncated)
        {
            if (set?.PropertiesEmoteAction != null)
            {
                for (var i = startIndex; i < set.PropertiesEmoteAction.Count; i++)
                {
                    var row = set.PropertiesEmoteAction[i];
                    var type = (EmoteType)row.Type;

                    if (type == EmoteType.Give)
                    {
                        if (row.WeenieClassId.HasValue && row.WeenieClassId.Value != 0)
                        {
                            var amount = row.StackSize ?? 1;
                            if (amount <= 0)
                                amount = 1;

                            rewards.Add(new ForecastedReward(row.WeenieClassId.Value, amount, false));
                        }
                        continue;
                    }

                    if (type == EmoteType.CreateTreasure)
                    {
                        rewards.Add(new ForecastedReward(0, 1, true));
                        continue;
                    }

                    if (BranchTargets.TryGetValue(type, out var targetCategories))
                    {
                        // Mirrors EmoteManager.GetEmoteSet (EmoteManager.cs:1590-1591): when questName
                        // (here, the branching row's Message) is null, NO quest filter is applied at all
                        // - every set in the target categories is a candidate, regardless of its own
                        // Quest value. Only a non-null Message narrows to an exact (case-insensitive)
                        // Quest match.
                        var candidates = allSets.Where(candidateSet =>
                            Array.IndexOf(targetCategories, candidateSet.Category) >= 0 &&
                            (row.Message == null || string.Equals(candidateSet.Quest ?? string.Empty, row.Message, StringComparison.OrdinalIgnoreCase)) &&
                            candidateSet.Probability > 0).ToList();

                        if (candidates.Count == 0)
                        {
                            // branch contributes nothing; path continues with remaining rows in this set
                            continue;
                        }

                        // every candidate resumes HERE (this set, right after the branch row) once its
                        // own rows are exhausted - the branch never truncates this set's own flow.
                        var resumeHere = new ContinuationFrame(set, i + 1, continuation);

                        foreach (var candidate in candidates)
                        {
                            if (paths.Count >= maxPaths)
                            {
                                truncated = true;
                                return;
                            }

                            if (depth >= maxDepth)
                            {
                                truncated = true;
                                continue;
                            }

                            var key = (candidate.Category, (candidate.Quest ?? string.Empty).ToLowerInvariant());

                            if (visited.Contains(key))
                            {
                                truncated = true;
                                continue;
                            }

                            var branchRewards = new List<ForecastedReward>(rewards);
                            var branchVisited = new HashSet<(EmoteCategory, string)>(visited) { key };

                            Walk(candidate, 0, resumeHere, allSets, branchRewards, branchVisited, depth + 1, maxDepth, maxPaths, paths, ref truncated);

                            if (truncated && paths.Count >= maxPaths)
                                return;
                        }

                        // this frame's remaining flow is now owned by each candidate's own walk (which
                        // resumes into resumeHere once it finishes) - nothing more to do at this level
                        return;
                    }
                }
            }

            // reached the end of this set's rows without hitting another branch - resume whatever this
            // set's own entry (a branch row somewhere further up) was itself continuing into, or - if
            // there is nothing left to resume - this is a complete path
            if (continuation == null)
                AddPath(rewards, paths, maxPaths, ref truncated);
            else
                Walk(continuation.Set, continuation.ResumeIndex, continuation.Next, allSets, rewards, visited, depth, maxDepth, maxPaths, paths, ref truncated);
        }

        private static void AddPath(List<ForecastedReward> rewards, List<IReadOnlyList<ForecastedReward>> paths, int maxPaths, ref bool truncated)
        {
            if (paths.Count >= maxPaths)
            {
                truncated = true;
                return;
            }

            paths.Add(rewards);
        }
    }
}
