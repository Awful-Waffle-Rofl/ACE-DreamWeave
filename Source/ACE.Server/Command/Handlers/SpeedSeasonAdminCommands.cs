using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using log4net;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.Network;
using ACE.Server.WorldObjects;

namespace ACE.Server.Command.Handlers
{
    /// <summary>
    /// Admin tooling for Proving Grounds: Speed - the season registry and the speed board.
    ///
    /// THIS BOARD INVERTS THE OTHER THREE. The Attack, Defense and Wave boards live on the character biota and
    /// are served from PlayerManager's in-memory collections, so they are cleared through those biotas with
    /// /resetleaderboard (see ProvingGroundsAdminCommands). Speed's `character_speed_run` shard table is the
    /// RECORD OF TRUTH and SpeedBoardManager's board cache is DERIVED from it, so its tooling writes the TABLE
    /// and then refreshes the cache, and never touches a biota. See Docs/ProvingGroundsSpeed/DESIGN.md
    /// section 3.5.
    /// </summary>
    public static class SpeedSeasonAdminCommands
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const string ResetUsage = "Usage: resetspeedboard <season> [confirm]";

        /// <summary>
        /// Most entries listed in the chat/console output of a reset. A busy season would otherwise build one
        /// enormous message; the log line written on confirm is uncapped, so the full record of what was
        /// cleared always survives even when the on-screen list is truncated. Mirrors
        /// ProvingGroundsAdminCommands.MaxListedEntriesPerBoard.
        /// </summary>
        private const int MaxListedEntries = 20;

        /// <summary>
        /// Appends up to <see cref="MaxListedEntries"/> board lines, then a count of what was omitted.
        /// </summary>
        private static void AppendEntries(StringBuilder output, IReadOnlyList<SpeedBoardEntry> entries)
        {
            var listed = Math.Min(entries.Count, MaxListedEntries);

            for (var i = 0; i < listed; i++)
                output.AppendLine($"  {i + 1}. {entries[i].CharacterName} - {Player.FormatSpeedRunTime(entries[i].Centiseconds)}");

            if (entries.Count > listed)
                output.AppendLine($"  ... and {entries.Count - listed} more");
        }

        // ===========================================================================================
        // /reloadspeedseasons
        // ===========================================================================================

        [CommandHandler("reloadspeedseasons", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Re-reads the Proving Grounds: Speed season registry from the world database.",
            "")]
        public static void HandleReloadSpeedSeasons(Session session, params string[] parameters)
        {
            var (added, updated, missing) = SpeedSeasonManager.Reload();

            var output = new StringBuilder();
            output.AppendLine("=== Speed Season Registry Reload ===");
            output.AppendLine($"Added: {added}");
            output.AppendLine($"Updated: {updated}");
            output.AppendLine($"Missing from the database: {missing.Count}");

            if (missing.Count > 0)
                output.AppendLine($"  Missing season id(s): {string.Join(", ", missing)}");

            // Stated plainly because the count reads like a deletion and is not one: the reload is additive,
            // so a season no longer present in speed_season is KEPT in the registry rather than dropped. A run
            // that armed under it is still holding that season id and must still be able to resolve it.
            output.AppendLine("A season missing from the database is KEPT in the registry, not dropped - a live run may still reference it.");
            output.AppendLine("Content errors (malformed or overlapping rows) are written to the server log.");

            CommandHandlerHelper.WriteOutputInfo(session, output.ToString(), ChatMessageType.Broadcast);
        }

        // ===========================================================================================
        // /speedseason
        // ===========================================================================================

        [CommandHandler("speedseason", AccessLevel.Admin, CommandHandlerFlag.None, 0,
            "Shows the active Proving Grounds: Speed season and the next scheduled one.",
            "")]
        public static void HandleSpeedSeason(Session session, params string[] parameters)
        {
            var output = new StringBuilder();
            output.AppendLine("=== Speed Season ===");
            output.AppendLine($"Server time now: {DateTime.UtcNow:u} (UTC)");
            output.AppendLine();

            AppendSeason(output, "Active season", SpeedSeasonManager.GetActiveSeason(), "no active season (the rotation is in a gap - the season portal will refuse use)");
            output.AppendLine();
            AppendSeason(output, "Next scheduled season", SpeedSeasonManager.GetNextSeason(), "none scheduled");

            CommandHandlerHelper.WriteOutputInfo(session, output.ToString(), ChatMessageType.Broadcast);
        }

        /// <summary>
        /// One season block, or the given "nothing here" line. Every timestamp is labelled UTC explicitly:
        /// starts_at/ends_at are MySQL datetime and carry no timezone at all, and the whole system compares
        /// them against DateTime.UtcNow, so an admin reading these as local time would mis-schedule a
        /// rotation by their own offset (DESIGN section 4.2).
        /// </summary>
        private static void AppendSeason(StringBuilder output, string label, ACE.Database.Models.World.SpeedSeason season, string noneText)
        {
            if (season == null)
            {
                output.AppendLine($"{label}: {noneText}");
                return;
            }

            output.AppendLine($"{label}: id {season.Id} - {season.Name}");
            output.AppendLine($"  Dungeon: {season.DungeonName}");
            output.AppendLine($"  Window (UTC): {season.StartsAt:u} - {season.EndsAt:u}");
            output.AppendLine($"  Objective wcid: {season.ObjectiveWcid}");
            output.AppendLine($"  Level floor: {season.LevelFloor}");
            output.AppendLine($"  Realm id: {season.RealmId}");
            output.AppendLine($"  Entry cell: 0x{season.ObjCellId:X8} [{season.OriginX} {season.OriginY} {season.OriginZ}] [{season.AnglesW} {season.AnglesX} {season.AnglesY} {season.AnglesZ}]");
        }

        // ===========================================================================================
        // /resetspeedboard
        // ===========================================================================================

        /// <summary>
        /// Clears one season's speed board by deleting its `character_speed_run` rows and then refreshing the
        /// derived board cache.
        ///
        /// THIS IS THE ONE PROVING GROUNDS BOARD WHOSE RESET WRITES THE DATABASE DIRECTLY. The other three -
        /// Attack, Defense and Wave - keep their scores on the character biota and are cleared with
        /// /resetleaderboard, where a raw SQL edit would be invisible until the next restart. Here it is the
        /// exact opposite: the table is authoritative and the in-memory board is derived from it, so the
        /// delete lands in MySQL and SpeedBoardManager.RefreshSeason re-reads the season from the table.
        /// Never clear this board through a biota.
        /// </summary>
        [CommandHandler("resetspeedboard", AccessLevel.Admin, CommandHandlerFlag.None, 1,
            "Clears one Proving Grounds: Speed season's recorded runs. Writes the shard table, then refreshes the board cache.",
            "<season> [confirm]")]
        public static void HandleResetSpeedBoard(Session session, params string[] parameters)
        {
            if (parameters.Length < 1 || !int.TryParse(parameters[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonId))
            {
                CommandHandlerHelper.WriteOutputInfo(session, ResetUsage, ChatMessageType.Broadcast);
                return;
            }

            var confirmed = parameters.Length > 1 && string.Equals(parameters[1], "confirm", StringComparison.OrdinalIgnoreCase);

            var invoker = session?.Player?.Name ?? "CONSOLE";

            // The dry run previews the CACHE; the confirmed delete acts on the TABLE. Those two can legitimately
            // disagree if someone has edited character_speed_run behind the server's back since the last refresh,
            // so the deleted-row count below is reported from the delete itself rather than reconciled against
            // this preview.
            var board = SpeedBoardManager.GetSeasonBoard(seasonId);

            if (!confirmed)
            {
                var dryRunOutput = new StringBuilder();
                dryRunOutput.AppendLine("=== Speed Board Reset (DRY RUN - no changes made) ===");
                dryRunOutput.AppendLine($"Season {seasonId}: {board.Count} board entr(ies) would be cleared");

                if (board.Count == 0)
                    dryRunOutput.AppendLine("  (already empty)");
                else
                    AppendEntries(dryRunOutput, board);

                dryRunOutput.AppendLine("This deletes the season's character_speed_run rows - the record of truth - and then refreshes the board cache.");
                dryRunOutput.AppendLine("Counts are read from the board cache; the delete acts on the table, so they can differ if the table was edited outside the server.");
                dryRunOutput.AppendLine($"Re-run with confirm to apply: resetspeedboard {seasonId} confirm");

                CommandHandlerHelper.WriteOutputInfo(session, dryRunOutput.ToString(), ChatMessageType.Broadcast);
                return;
            }

            // Uncapped, so the full record of what was cleared survives even when the on-screen list above is
            // truncated to MaxListedEntries. Written BEFORE the delete, because after it the board is gone.
            log.Warn($"[SPEED] {invoker} reset the speed board for season {seasonId}: "
                + $"{board.Count} cached board entr(ies) - "
                + string.Join(", ", board.Select(e => $"{e.CharacterName}(0x{e.CharacterId:X8})={e.Centiseconds}cs")));

            int deleted;

            try
            {
                // THE ORDER HERE IS THE WHOLE DESIGN (DESIGN section 3.5): write the TABLE, then refresh the
                // DERIVED cache. `character_speed_run` is the record of truth for this board; the cache exists
                // only so /top speed stays a memory read. Never reset this board through a biota - that is how
                // the other three boards work and it would do nothing at all here.
                deleted = DatabaseManager.Shard.BaseDatabase.DeleteSpeedRunsBySeason(seasonId);
                SpeedBoardManager.RefreshSeason(seasonId);
            }
            catch (Exception ex)
            {
                log.Error($"[SPEED] {invoker}'s reset of season {seasonId} FAILED - the character_speed_run rows may be partly or wholly intact and the board cache may now be stale.", ex);
                CommandHandlerHelper.WriteOutputInfo(session, $"Speed board reset for season {seasonId} FAILED - see the server log. Nothing has been reported as deleted.", ChatMessageType.Broadcast);
                return;
            }

            // SpeedChallengeSeasonId / BestSpeedRunCenti on the player biota are DELIBERATELY left alone. They
            // are a personal-best convenience cache feeding one chat line, never the authority (DESIGN section
            // 4.3), and chasing every character's biota here would reintroduce exactly the biota coupling this
            // board's design removed.
            //
            // Be precise about how far that goes, because the obvious phrasing overstates it: the cache clears
            // itself at the next ROTATION, since a cached best whose stored season id does not match the season
            // being scored reads as unset. It does NOT clear on this reset. A player who runs the SAME season
            // again after a reset still has their pre-reset time on the biota, so their next completion is
            // measured against it and may not announce a personal best even though the board is empty. That is
            // cosmetic - the board itself, which is the only thing anyone else sees, is genuinely cleared.

            var output = new StringBuilder();
            output.AppendLine("=== Speed Board Reset ===");
            output.AppendLine($"Season {seasonId}: deleted {deleted} character_speed_run row(s) and refreshed the board cache.");
            output.AppendLine($"Board entries cleared from the cache: {board.Count}.");
            output.AppendLine("Personal-best biota caches are left untouched by design. They clear themselves at the next rotation, not on this reset,");
            output.AppendLine("so a player re-running THIS season is still measured against their pre-reset time for the personal-best message only.");

            log.Warn($"[SPEED] {invoker} reset the speed board for season {seasonId}: {deleted} character_speed_run row(s) deleted, board cache refreshed.");

            CommandHandlerHelper.WriteOutputInfo(session, output.ToString(), ChatMessageType.Broadcast);
        }

        // ===========================================================================================
        // /removespeedrun
        // ===========================================================================================

        private const string RemoveUsage = "Usage: removespeedrun <season> <characterName> [<runId> confirm]";

        /// <summary>
        /// One parsed, validated invocation of /removespeedrun. Immutable and pure so
        /// <see cref="TryParseRemoveSpeedRunArgs"/> can be unit-tested with no session, no database and
        /// no live world.
        /// </summary>
        internal readonly struct RemoveSpeedRunArgs
        {
            public RemoveSpeedRunArgs(int seasonId, string characterName, uint? runId, bool confirmed)
            {
                SeasonId = seasonId;
                CharacterName = characterName;
                RunId = runId;
                Confirmed = confirmed;
            }

            public int SeasonId { get; }

            public string CharacterName { get; }

            /// <summary>Null on the dry-run form; set on the confirm form.</summary>
            public uint? RunId { get; }

            public bool Confirmed { get; }
        }

        /// <summary>
        /// Parses `removespeedrun &lt;season&gt; &lt;characterName&gt;` (dry run) and
        /// `removespeedrun &lt;season&gt; &lt;characterName&gt; &lt;runId&gt; confirm` (apply). A character
        /// name can itself contain spaces (AC allows it), so the confirm form is recognised from the
        /// TAIL of the parameter list - the last token must be "confirm" and the one before it a valid
        /// run id - rather than by a fixed parameter count; everything between the season and that tail
        /// is joined back into the name. Pure: no I/O, no database, no session.
        /// </summary>
        internal static bool TryParseRemoveSpeedRunArgs(string[] parameters, out RemoveSpeedRunArgs args, out string error)
        {
            args = default;
            error = null;

            if (parameters == null || parameters.Length < 2)
            {
                error = RemoveUsage;
                return false;
            }

            if (!int.TryParse(parameters[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonId))
            {
                error = RemoveUsage;
                return false;
            }

            var remaining = parameters.Skip(1).ToArray();

            if (remaining.Length >= 3 && string.Equals(remaining[remaining.Length - 1], "confirm", StringComparison.OrdinalIgnoreCase))
            {
                if (!uint.TryParse(remaining[remaining.Length - 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var runId))
                {
                    error = RemoveUsage;
                    return false;
                }

                var confirmName = string.Join(" ", remaining, 0, remaining.Length - 2).Trim();

                if (confirmName.Length == 0)
                {
                    error = RemoveUsage;
                    return false;
                }

                args = new RemoveSpeedRunArgs(seasonId, confirmName, runId, true);
                return true;
            }

            var dryRunName = string.Join(" ", remaining).Trim();

            if (dryRunName.Length == 0)
            {
                error = RemoveUsage;
                return false;
            }

            args = new RemoveSpeedRunArgs(seasonId, dryRunName, null, false);
            return true;
        }

        /// <summary>
        /// Wraps a raw table row as the immutable SpeedBoardEntry SpeedBoardManager's ranking helpers
        /// operate on, so this file can reuse SpeedBoardManager.CompareEntries instead of re-deriving the
        /// ranking rule (lower centiseconds is better, non-positive times rank last, ties break on
        /// CompletedAt then CharacterId).
        /// </summary>
        internal static SpeedBoardEntry ToBoardEntry(CharacterSpeedRun row)
        {
            return new SpeedBoardEntry(row.CharacterId, row.CharacterName, row.SeasonId, row.Centiseconds, row.CharacterLevel, row.CompletedAt);
        }

        /// <summary>
        /// Which of a character's own rows is the one currently backing their board entry - the row
        /// SpeedBoardManager.RefreshSeason would collapse to as this character's best line, computed the
        /// same way (SpeedBoardManager.CompareEntries) so this can never disagree with the live cache.
        /// All rows are expected to belong to the SAME character and season; callers filter first. Null
        /// or empty input returns null rather than throwing.
        /// </summary>
        internal static CharacterSpeedRun SelectCurrentBoardRow(IReadOnlyList<CharacterSpeedRun> rows)
        {
            if (rows == null || rows.Count == 0)
                return null;

            var best = rows[0];
            var bestEntry = ToBoardEntry(best);

            for (var i = 1; i < rows.Count; i++)
            {
                var candidate = rows[i];
                var candidateEntry = ToBoardEntry(candidate);

                if (SpeedBoardManager.CompareEntries(candidateEntry, bestEntry) < 0)
                {
                    best = candidate;
                    bestEntry = candidateEntry;
                }
            }

            return best;
        }

        /// <summary>
        /// One CharacterId's slice of a name match: every row that CharacterId recorded in the season,
        /// and which of those rows currently backs their board entry.
        /// </summary>
        internal sealed class SpeedRunNameMatch
        {
            public SpeedRunNameMatch(uint characterId, IReadOnlyList<CharacterSpeedRun> rows, CharacterSpeedRun currentBoardRow)
            {
                CharacterId = characterId;
                Rows = rows;
                CurrentBoardRow = currentBoardRow;
            }

            public uint CharacterId { get; }

            /// <summary>This CharacterId's rows only, ordered by Id.</summary>
            public IReadOnlyList<CharacterSpeedRun> Rows { get; }

            /// <summary>
            /// Never null when <see cref="Rows"/> is non-empty: the row SpeedBoardManager would collapse
            /// to as this CharacterId's board entry, computed the same way (SelectCurrentBoardRow).
            /// </summary>
            public CharacterSpeedRun CurrentBoardRow { get; }
        }

        /// <summary>
        /// Filters a season's rows to a character name (case-insensitive) and groups the matches by
        /// CharacterId - NOT by name. The board is keyed by CharacterId (SpeedBoardManager's per-season
        /// dictionary), and `character_speed_run` deliberately outlives a deleted or renamed character,
        /// so two different CharacterIds can legitimately share the same CharacterName snapshot (an
        /// account that deleted and recreated a character under the same name, or two different accounts
        /// that happened to pick it). Grouping by name alone would merge their rows into one "current
        /// board entry" pick, which is wrong: each CharacterId has its own line on the board. Returns one
        /// entry per distinct CharacterId, ordered by CharacterId, each with its own current-board-entry
        /// row computed independently via SelectCurrentBoardRow. Pure: no I/O, no database.
        /// </summary>
        internal static List<SpeedRunNameMatch> GroupMatchingRowsByCharacter(IEnumerable<CharacterSpeedRun> rows, string characterName)
        {
            var matches = (rows ?? Enumerable.Empty<CharacterSpeedRun>())
                .Where(r => r != null && string.Equals(r.CharacterName, characterName, StringComparison.OrdinalIgnoreCase));

            var groups = new List<SpeedRunNameMatch>();

            foreach (var group in matches.GroupBy(r => r.CharacterId).OrderBy(g => g.Key))
            {
                var groupRows = group.OrderBy(r => r.Id).ToList();
                groups.Add(new SpeedRunNameMatch(group.Key, groupRows, SelectCurrentBoardRow(groupRows)));
            }

            return groups;
        }

        /// <summary>
        /// Removes exactly one recorded Proving Grounds: Speed run.
        ///
        /// Same table-then-cache order as /resetspeedboard (DESIGN section 3.5), scoped to one row: the
        /// dry run lists every `character_speed_run` row for the named character in the given season -
        /// read from the TABLE, not the board cache, since the whole point is picking a row the cache
        /// may have already collapsed away - and the confirmed form deletes exactly that row (refusing,
        /// with the table left untouched, unless it belongs to both the named character and the season),
        /// then refreshes the cache. The character's next-best run in that season, if any, becomes their
        /// board entry automatically because the refresh rebuilds straight from the table.
        /// </summary>
        [CommandHandler("removespeedrun", AccessLevel.Admin, CommandHandlerFlag.None, 2,
            "Removes one recorded Proving Grounds: Speed run for a character in a season. Writes the shard table, then refreshes the board cache.",
            "<season> <characterName> [<runId> confirm]")]
        public static void HandleRemoveSpeedRun(Session session, params string[] parameters)
        {
            if (!TryParseRemoveSpeedRunArgs(parameters, out var args, out var error))
            {
                CommandHandlerHelper.WriteOutputInfo(session, error, ChatMessageType.Broadcast);
                return;
            }

            var invoker = session?.Player?.Name ?? "CONSOLE";

            List<CharacterSpeedRun> seasonRows;

            try
            {
                seasonRows = DatabaseManager.Shard.BaseDatabase.GetSpeedRunsBySeason(args.SeasonId);
            }
            catch (Exception ex)
            {
                log.Error($"[SPEED] {invoker}'s removespeedrun lookup for season {args.SeasonId}, character '{args.CharacterName}' FAILED reading character_speed_run.", ex);
                CommandHandlerHelper.WriteOutputInfo(session, $"Could not read season {args.SeasonId}'s runs - see the server log. Nothing has changed.", ChatMessageType.Broadcast);
                return;
            }

            var groups = GroupMatchingRowsByCharacter(seasonRows, args.CharacterName);
            var totalRuns = groups.Sum(g => g.Rows.Count);

            if (!args.Confirmed)
            {
                var dryRunOutput = new StringBuilder();
                dryRunOutput.AppendLine("=== Remove Speed Run (DRY RUN - no changes made) ===");
                dryRunOutput.AppendLine($"Season {args.SeasonId}, character '{args.CharacterName}': {totalRuns} recorded run(s)");

                if (groups.Count == 0)
                    dryRunOutput.AppendLine("  (no runs recorded for this character in this season)");
                else
                {
                    // More than one CharacterId sharing this name is a distinct-character collision, not
                    // a duplicate row: character_speed_run outlives a deleted/renamed character, so a
                    // recreated character or a different account can legitimately hold the same name.
                    // Each CharacterId gets its own labelled section and its own current-board-entry pick.
                    if (groups.Count > 1)
                        dryRunOutput.AppendLine($"NOTE: {groups.Count} distinct character ids share the name '{args.CharacterName}' in this season - each has its own board entry.");

                    foreach (var group in groups)
                    {
                        if (groups.Count > 1)
                            dryRunOutput.AppendLine($"  -- CharacterId 0x{group.CharacterId:X8} --");

                        foreach (var row in group.Rows)
                        {
                            var marker = row.Id == group.CurrentBoardRow.Id ? " [current board entry]" : "";
                            dryRunOutput.AppendLine($"  id {row.Id} (character 0x{row.CharacterId:X8}): {Player.FormatSpeedRunTime(row.Centiseconds)} (recorded {row.CompletedAt:u} UTC){marker}");
                        }
                    }
                }

                dryRunOutput.AppendLine("This is a single-row delete - it removes exactly one character_speed_run row, then refreshes the board cache.");
                dryRunOutput.AppendLine("The personal-best biota cache (BestSpeedRunCenti) is left untouched, as with /resetspeedboard.");

                if (groups.Count > 0)
                    dryRunOutput.AppendLine($"Re-run with confirm to delete one: removespeedrun {args.SeasonId} {args.CharacterName} <runId> confirm");

                CommandHandlerHelper.WriteOutputInfo(session, dryRunOutput.ToString(), ChatMessageType.Broadcast);
                return;
            }

            // The target row is resolved from the flattened group rows, so its CharacterId comes from
            // THAT ROW, never from args.CharacterName - the delete below scopes on target.CharacterId,
            // which is what keeps a name collision between two CharacterIds from ever being ambiguous.
            var target = groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id == args.RunId.Value);

            if (target == null)
            {
                CommandHandlerHelper.WriteOutputInfo(session,
                    $"Run id {args.RunId.Value} does not belong to character '{args.CharacterName}' in season {args.SeasonId} (or does not exist). Nothing has changed.",
                    ChatMessageType.Broadcast);
                return;
            }

            // Written BEFORE the delete, same reason as /resetspeedboard: after the delete the row is gone.
            log.Warn($"[SPEED] {invoker} removed a speed run: season {args.SeasonId}, character {target.CharacterName}(0x{target.CharacterId:X8}), "
                + $"run id {target.Id}, {target.Centiseconds}cs ({Player.FormatSpeedRunTime(target.Centiseconds)}).");

            int deleted;
            List<SpeedBoardEntry> refreshedBoard = null;

            try
            {
                // Table, then cache - the delete is scoped to id + season + characterId in the same
                // statement, so there is no window in which this could remove a row that has moved to a
                // different character or season since the read above. RefreshSeason and the board read
                // that follows it live in THIS try, same as HandleResetSpeedBoard: if the delete succeeds
                // but the refresh throws, the caller must hear that the cache may now be stale rather than
                // being told the operation fully succeeded.
                deleted = DatabaseManager.Shard.BaseDatabase.DeleteSpeedRunById(target.Id, args.SeasonId, target.CharacterId);

                if (deleted > 0)
                {
                    SpeedBoardManager.RefreshSeason(args.SeasonId);
                    refreshedBoard = SpeedBoardManager.GetSeasonBoard(args.SeasonId);
                }
            }
            catch (Exception ex)
            {
                log.Error($"[SPEED] {invoker}'s removal of run id {target.Id} (season {args.SeasonId}, character {target.CharacterName}) FAILED - the row may or may not be deleted and the board cache may now be stale.", ex);
                CommandHandlerHelper.WriteOutputInfo(session, $"Removing run id {target.Id} FAILED - see the server log. The row may or may not be deleted and the board cache may now be stale.", ChatMessageType.Broadcast);
                return;
            }

            if (deleted == 0)
            {
                CommandHandlerHelper.WriteOutputInfo(session,
                    $"Run id {target.Id} was no longer present for character '{args.CharacterName}' in season {args.SeasonId} by the time the delete ran. Nothing has changed.",
                    ChatMessageType.Broadcast);
                return;
            }

            var remaining = refreshedBoard?.FirstOrDefault(e => e.CharacterId == target.CharacterId);

            var output = new StringBuilder();
            output.AppendLine("=== Remove Speed Run ===");
            output.AppendLine($"Season {args.SeasonId}: deleted run id {target.Id} for {target.CharacterName} ({Player.FormatSpeedRunTime(target.Centiseconds)}) and refreshed the board cache.");

            output.AppendLine(remaining != null
                ? $"{target.CharacterName}'s board entry is now their next-best run: {Player.FormatSpeedRunTime(remaining.Centiseconds)}."
                : $"{target.CharacterName} has no other recorded runs in season {args.SeasonId} - they no longer have a board entry for it.");

            output.AppendLine("The personal-best biota cache (BestSpeedRunCenti) is left untouched by design, same as /resetspeedboard - see that command's notes on how that cache clears at the next rotation, not on a row removal.");

            log.Warn($"[SPEED] {invoker} removed a speed run: season {args.SeasonId}, character {target.CharacterName}(0x{target.CharacterId:X8}), run id {target.Id} deleted, board cache refreshed.");

            CommandHandlerHelper.WriteOutputInfo(session, output.ToString(), ChatMessageType.Broadcast);
        }
    }
}
