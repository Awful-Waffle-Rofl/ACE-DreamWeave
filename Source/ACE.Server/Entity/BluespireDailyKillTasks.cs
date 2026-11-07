using System;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Bluespire daily kill tasks (Drift Marae Lassel, OpenSpec change bluespire-daily-kill-tasks): ten Hea
    /// Tumerok quest-givers, each handing out a random kill task from its own monster category and paying on
    /// NPC turn-in, after which that category is on a rolling 20-hour cooldown.
    ///
    /// Everything about the feature is content (weenies, emotes, quest rows, realm placements) and lives in
    /// the private content repository. The ONE engine requirement is cooldown isolation: the cooldown is a
    /// world quest row's MinDelta (72,000 seconds), stamped by the NPC on a successful claim, and
    /// QuestManager.GetNextSolveTime multiplies every MinDelta by quest_mindelta_rate unless
    /// QuestManager.CanScaleQuestMinDelta says otherwise. The spec requires exactly 72,000 seconds whatever
    /// that multiplier is set to, so this quest family is exempted from scaling - the same mechanism, and the
    /// same kind of check, as the existing ColoArena exemption, which is left exactly as it was.
    ///
    /// Scope is a name prefix and nothing else: a quest is in the family if and only if its name (after the
    /// usual @comment strip) starts with <see cref="QuestPrefix"/>. No existing quest uses that prefix, so
    /// every other quest on the server keeps its current scaling behaviour. Content must name the family's
    /// rows accordingly - BluespireDailyKill{Category}Wait (the per-category cooldown timer),
    /// BluespireDailyKill{Category}Task (the active-assignment marker) and
    /// BluespireDailyKill{Category}{Target} (the per-target kill counter). Only the Wait rows carry a
    /// MinDelta; the others have MinDelta 0, so exempting them from scaling changes nothing for them.
    /// </summary>
    public static class BluespireDailyKillTasks
    {
        /// <summary>The quest-name prefix that identifies the Bluespire daily kill-task family.</summary>
        public const string QuestPrefix = "BluespireDailyKill";

        /// <summary>
        /// The approved cooldown, in seconds (20 hours), measured from the successful NPC reward claim.
        /// Engine code never applies this value itself - it is the MinDelta content is expected to author on
        /// each category's Wait row - but it is the single named constant tests and tooling check against.
        /// </summary>
        public const uint CooldownSeconds = 72000;

        /// <summary>
        /// True when <paramref name="questName"/> belongs to the Bluespire daily kill-task family. Accepts a
        /// raw quest format with an optional @comment (stripped the same way QuestManager.GetQuestName
        /// does) and compares the prefix case-insensitively, because quest names reach the engine from
        /// hand-authored emote text. Null or empty is never in the family.
        /// </summary>
        public static bool IsQuestFamily(string questName)
        {
            if (string.IsNullOrEmpty(questName))
                return false;

            var idx = questName.IndexOf('@');
            var name = idx == -1 ? questName : questName.Substring(0, idx);

            return name.StartsWith(QuestPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
