using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using log4net;

using ACE.Database;
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
    }
}
