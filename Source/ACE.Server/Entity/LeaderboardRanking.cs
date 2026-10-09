using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Describes one of the per-character leaderboards /top and the character sheet share.
    /// </summary>
    public sealed class LeaderboardBoard
    {
        public string Key { get; init; }                 // "dps", "defense", "wave", "level", "cap", "stamp", "thread"
        public string Title { get; init; }               // exactly the /top title
        public Func<IPlayer, long> Score { get; init; }
        public Func<long, string> Format { get; init; }
        public Func<IPlayer, long> TieBreaker { get; init; }   // null = none
        public Func<IPlayer, string> Detail { get; init; }     // extra per-player line text, appended after Format; null = none
        public string FeatureGate { get; init; }         // PropertyManager bool key or null
    }

    /// <summary>
    /// The shared ranking pipeline behind /top: which candidates make a board, in what order, and the
    /// definitions of the per-character boards themselves. A later task (the web character sheet) reports
    /// a character's ranks through this same code rather than reimplementing it.
    /// </summary>
    public static class LeaderboardRanking
    {
        // how many places every /top board lists before it cuts off
        public const int LeaderboardSize = 20;

        // DPS-challenge duration all scores are reported against (the content portal uses 60s).
        private const double DpsLeaderboardDuration = 60.0;

        /// <summary>
        /// The per-character boards /top and the sheet share. Bank is NOT here: its score closes over a
        /// per-render balance snapshot and it is account-collapsed; /top bank calls Rank directly.
        /// </summary>
        public static IReadOnlyList<LeaderboardBoard> Boards { get; } = new List<LeaderboardBoard>
        {
            new LeaderboardBoard
            {
                Key = "dps",
                Title = "DPS Challenge",
                Score = p => p.GetProperty(PropertyInt64.BestDpsScore) ?? 0,
                Format = score => $"{score:N0} damage ({score / DpsLeaderboardDuration:N0} DPS)",
                TieBreaker = null,
                FeatureGate = null,
            },
            new LeaderboardBoard
            {
                Key = "defense",
                Title = "Defense Challenge",
                Score = p => p.GetProperty(PropertyInt64.BestSurvivalScore) ?? 0,
                Format = score => $"{score:N0}s",
                TieBreaker = null,
                FeatureGate = null,
            },
            new LeaderboardBoard
            {
                Key = "wave",
                Title = "Wave Challenge",
                Score = Player.GetBestWaveScoreCenti,
                Format = Player.FormatWaveScore,
                // Faster clear times sort first; a tied player with no recorded time (every full clear before
                // this shipped) sorts LAST among the tie, never mixed in among the timed entries - long.MinValue
                // is smaller than every negated positive time.
                TieBreaker = p =>
                {
                    var t = p.GetProperty(PropertyInt64.BestWaveClearTimeCenti) ?? 0;
                    return t > 0 ? -t : long.MinValue;
                },
                Detail = p =>
                {
                    var t = p.GetProperty(PropertyInt64.BestWaveClearTimeCenti);
                    return t.HasValue && t.Value > 0 ? $" - {Player.FormatWaveClearTime(t.Value)}" : null;
                },
                FeatureGate = null,
            },
            new LeaderboardBoard
            {
                Key = "level",
                Title = "Level",
                Score = p => p.Level ?? 0,
                Format = level => $"Level {level:N0}",
                TieBreaker = p => p.GetProperty(PropertyInt64.TotalExperience) ?? 0,
                FeatureGate = null,
            },
            new LeaderboardBoard
            {
                Key = "cap",
                Title = "Class Ability Points",
                Score = p => p.GetProperty(PropertyInt.TotalClassAbilityPointsEarned) ?? 0,
                Format = points => $"{points:N0} earned",
                TieBreaker = null,
                FeatureGate = "class_abilities_enabled",
            },
            new LeaderboardBoard
            {
                Key = "stamp",
                Title = "Quest Stamps",
                Score = p => p.GetProperty(PropertyInt64.QuestStampCount) ?? 0,
                Format = stamps => $"{stamps:N0} stamps",
                TieBreaker = null,
                FeatureGate = "quest_stamps_enabled",
            },
            new LeaderboardBoard
            {
                Key = "thread",
                Title = "Thread-Guide",
                Score = ThreadBoardScore,
                Format = ThreadBoardFormat,
                // Rung first, lifetime clears break a tie on the same rung.
                TieBreaker = p => p.GetProperty(PropertyInt.ThreadClearsLifetime) ?? 0,
                Detail = p => $" ({p.GetProperty(PropertyInt.ThreadClearsLifetime) ?? 0:N0} clears)",
                FeatureGate = "dynamic_dungeons_enabled",
            },
        };

        /// <summary>
        /// The "thread" board's score: the highest Thread-Guide rung won PLUS ONE, or 0 when the character
        /// has neither a rung nor a single Thread clear. Rank drops every score of 0 or less, so a plain
        /// rung score (0 = no rung) would also drop a character who has cleared Threads but never won a
        /// rung; the +1 keeps that character listed (below every rung holder, ordered by clears) while a
        /// character with both values at 0 stays off the board. The offset is internal: ThreadBoardFormat
        /// undoes it, so nothing player-visible ever shows the shifted number.
        /// </summary>
        private static long ThreadBoardScore(IPlayer p)
        {
            var rung = Math.Max(0, p.GetProperty(PropertyInt.ThreadGuideLevel) ?? 0);
            var clears = p.GetProperty(PropertyInt.ThreadClearsLifetime) ?? 0;
            return rung > 0 || clears > 0 ? rung + 1L : 0;
        }

        private static string ThreadBoardFormat(long score) =>
            score <= 1 ? "no rung" : $"rung {score - 1:N0}";

        /// <summary>
        /// Pure core. Exemption first, optional account collapse, score > 0, score desc then tie-break desc.
        /// </summary>
        public static List<T> Rank<T>(
            IEnumerable<T> candidates,
            Func<T, bool> isExempt,
            Func<T, long> score,
            Func<T, long> tieBreaker,
            Func<T, uint> accountKey,
            Func<T, int> level,
            Func<T, long> totalXp,
            Func<T, uint> ordinal)
        {
            tieBreaker ??= _ => 0;

            var filtered = candidates.Where(c => !isExempt(c));

            if (accountKey != null)
            {
                filtered = AccountLeaderboard.CollapseToAccountRepresentatives(filtered, accountKey, level, totalXp, ordinal);
            }

            return filtered
                .Where(c => score(c) > 0)
                .OrderByDescending(score)
                .ThenByDescending(tieBreaker)
                .ToList();
        }

        /// <summary>
        /// "Season Name - Dungeon Name" for a header or a winners line, degrading to just the season name
        /// when a row carries no dungeon name (only Name is guarded by SpeedSeasonManager.IsWellFormed, so a
        /// blank dungeon_name is a survivable content error rather than a reason to render a dangling dash).
        /// The one title rule for a speed season: /top speed, /top speed winners and the character sheet's
        /// speed rank all read it.
        /// </summary>
        public static string SpeedSeasonTitle(ACE.Database.Models.World.SpeedSeason season)
        {
            if (season == null)
                return "Speed Trial";

            return string.IsNullOrWhiteSpace(season.DungeonName) ? season.Name : $"{season.Name} - {season.DungeonName}";
        }

        /// <summary>
        /// Same filter /top speed applies: Centiseconds > 0, and an entry is dropped only when its character
        /// resolves AND is exempt. Order preserved.
        /// </summary>
        public static List<TEntry> RankSpeed<TEntry>(
            IEnumerable<TEntry> board,
            Func<TEntry, long> centiseconds,
            Func<TEntry, bool> resolvesToExemptCharacter)
        {
            return board
                .Where(e => centiseconds(e) > 0)
                .Where(e => !resolvesToExemptCharacter(e))
                .ToList();
        }
    }
}
