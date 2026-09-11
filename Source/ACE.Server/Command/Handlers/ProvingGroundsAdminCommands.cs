using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using log4net;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Network;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Admin tooling for the three BIOTA-BACKED Proving Grounds leaderboards (/top dps, /top defense,
    /// /top wave). Those three are served entirely from the in-memory PlayerManager collections (online Player +
    /// offline OfflinePlayer objects), never re-read from the shard database, so clearing their scores requires
    /// mutating those in-memory biotas through IPlayer and then persisting via SaveBiotaToDatabase. Deleting
    /// the rows directly in MySQL would do nothing until the next server restart, and would be silently
    /// overwritten by the next save of an already-loaded player.
    ///
    /// SPEED IS THE EXCEPTION, AND IT INVERTS ALL OF THE ABOVE. /top speed keeps no score on the biota at all:
    /// its `character_speed_run` shard table is the RECORD OF TRUTH and SpeedBoardManager's board cache is
    /// DERIVED from that table and refreshed on write. It is therefore reset with /resetspeedboard (see
    /// SpeedSeasonAdminCommands), which deletes the rows and then refreshes the cache - NOT with
    /// /resetleaderboard, which cannot reach it, because nothing about that board lives on a biota. See
    /// Docs/ProvingGroundsSpeed/DESIGN.md section 3.5.
    /// </summary>
    public static class ProvingGroundsAdminCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string Usage = "Usage: resetleaderboard <dps|defense|wave|all> [confirm]";

        /// <summary>
        /// Most entries per board listed in the chat/console output. A shard with hundreds of record holders would
        /// otherwise build one enormous message; the log line written on confirm is uncapped, so the full record of
        /// what was cleared always survives even when the on-screen list is truncated.
        /// </summary>
        private const int MaxListedEntriesPerBoard = 20;

        /// <summary>
        /// Appends up to <see cref="MaxListedEntriesPerBoard"/> score lines, then a count of what was omitted.
        /// </summary>
        private static void AppendEntries(StringBuilder output, IReadOnlyList<(string Name, long Score)> entries, bool numbered)
        {
            var listed = Math.Min(entries.Count, MaxListedEntriesPerBoard);

            for (var i = 0; i < listed; i++)
            {
                var prefix = numbered ? $"  {i + 1}. " : "  ";
                output.AppendLine($"{prefix}{entries[i].Name} - {entries[i].Score:N0}");
            }

            if (entries.Count > listed)
                output.AppendLine($"  ... and {entries.Count - listed} more");
        }

        /// <summary>
        /// Maps a /resetleaderboard board argument (case-insensitive) to the PropertyInt64/label pairs it covers.
        /// Factored out so the mapping can be unit tested without a live server.
        /// </summary>
        public static bool TryGetBoards(string arg, out IReadOnlyList<(PropertyInt64 Property, string Label)> boards)
        {
            switch (arg?.ToLowerInvariant())
            {
                case "dps":
                    boards = new List<(PropertyInt64, string)> { (PropertyInt64.BestDpsScore, "Attack (dps)") };
                    return true;

                case "defense":
                    boards = new List<(PropertyInt64, string)> { (PropertyInt64.BestSurvivalScore, "Defense (defense)") };
                    return true;

                case "wave":
                    boards = new List<(PropertyInt64, string)>
                    {
                        (PropertyInt64.BestWaveScoreCenti, "Wave (wave)"),
                        (PropertyInt64.BestWaveScore, "Wave legacy (wave)"),
                    };
                    return true;

                // "all" means all the BIOTA-BACKED boards, which is all this command can reach: every entry
                // here is a PropertyInt64 on a character biota, and Speed deliberately stores nothing on a
                // biota. Its board is the `character_speed_run` table plus a derived cache, so it is reset with
                // /resetspeedboard instead. The omission is deliberate, not an oversight.
                case "all":
                    boards = new List<(PropertyInt64, string)>
                    {
                        (PropertyInt64.BestDpsScore, "Attack (dps)"),
                        (PropertyInt64.BestSurvivalScore, "Defense (defense)"),
                        (PropertyInt64.BestWaveScoreCenti, "Wave (wave)"),
                        (PropertyInt64.BestWaveScore, "Wave legacy (wave)"),
                    };
                    return true;

                default:
                    boards = null;
                    return false;
            }
        }

        [CommandHandler("resetleaderboard", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Clears Proving Grounds leaderboard records for every character on the shard.",
            "<dps|defense|wave|all> [confirm]")]
        public static void HandleResetLeaderboard(Session session, params string[] parameters)
        {
            if (parameters.Length < 1 || !TryGetBoards(parameters[0], out var boards))
            {
                CommandHandlerHelper.WriteOutputInfo(session, Usage, ChatMessageType.Broadcast);
                return;
            }

            var boardArg = parameters[0].ToLowerInvariant();
            var confirmed = parameters.Length > 1 && string.Equals(parameters[1], "confirm", StringComparison.OrdinalIgnoreCase);

            // PlayerManager.GetAllPlayers() concatenates GetAllOffline() + GetAllOnline(). A character is moved
            // from the offline dictionary to the online dictionary on login (SwitchPlayerFromOfflineToOnline
            // removes the offline entry as part of the same locked operation), so the same guid can never appear
            // in both lists - no de-duplication is needed here.
            var allPlayers = PlayerManager.GetAllPlayers();

            var invoker = session?.Player?.Name ?? "CONSOLE";

            if (!confirmed)
            {
                var dryRunOutput = new StringBuilder();
                dryRunOutput.AppendLine("=== Proving Grounds Leaderboard Reset (DRY RUN - no changes made) ===");

                foreach (var (property, label) in boards)
                {
                    var entries = allPlayers
                        .Select(p => (Name: p.Name, Score: p.GetProperty(property) ?? 0))
                        .Where(e => e.Score > 0)
                        .OrderByDescending(e => e.Score)
                        .ToList();

                    dryRunOutput.AppendLine($"{label}: {entries.Count} record(s) would be cleared");

                    if (entries.Count == 0)
                    {
                        dryRunOutput.AppendLine("  (already empty)");
                        continue;
                    }

                    AppendEntries(dryRunOutput, entries, numbered: true);
                }

                dryRunOutput.AppendLine($"Re-run with confirm to apply: resetleaderboard {boardArg} confirm");

                CommandHandlerHelper.WriteOutputInfo(session, dryRunOutput.ToString(), ChatMessageType.Broadcast);
                return;
            }

            // Keyed per board so the summary/log below can report per-board counts, but SaveBiotaToDatabase is
            // called at most once per player (below), regardless of how many of the selected boards it cleared.
            var clearedByBoard = boards.ToDictionary(b => b.Property, b => new List<(string Name, long Score)>());

            foreach (var player in allPlayers)
            {
                var clearedAnyForThisPlayer = false;

                foreach (var (property, _) in boards)
                {
                    var score = player.GetProperty(property) ?? 0;
                    if (score <= 0)
                        continue;

                    clearedByBoard[property].Add((player.Name, score));
                    player.RemoveProperty(property);
                    clearedAnyForThisPlayer = true;
                }

                if (clearedAnyForThisPlayer)
                    player.SaveBiotaToDatabase();
            }

            var output = new StringBuilder();
            output.AppendLine("=== Proving Grounds Leaderboard Reset ===");

            foreach (var (property, label) in boards)
            {
                var clearedEntries = clearedByBoard[property];
                clearedEntries.Sort((a, b) => b.Score.CompareTo(a.Score));

                output.AppendLine($"{label}: cleared {clearedEntries.Count} record(s)");

                AppendEntries(output, clearedEntries, numbered: false);

                log.Warn($"[PROVING GROUNDS] {invoker} reset the {label} leaderboard ({boardArg}): "
                    + $"{clearedEntries.Count} record(s) cleared - "
                    + string.Join(", ", clearedEntries.Select(e => $"{e.Name}={e.Score:N0}")));
            }

            CommandHandlerHelper.WriteOutputInfo(session, output.ToString(), ChatMessageType.Broadcast);
        }
    }
}
