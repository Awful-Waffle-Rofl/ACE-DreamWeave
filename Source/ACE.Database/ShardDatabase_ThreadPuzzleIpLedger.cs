using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.EntityFrameworkCore;

using MySqlConnector;

using ACE.Database.Models.Shard;

namespace ACE.Database
{
    /// <summary>
    /// Thread puzzle-gate IP ledger DAO (the thread_puzzle_ip_ledger table). A ledger, not a biota
    /// operation, so like the reward_claim DAO it deliberately does NOT run through
    /// SerializedShardDatabase: every call opens its own ShardDbContext and runs exactly one statement.
    ///
    /// NOTHING here may open a user-initiated EF transaction. ShardDbContext.OnConfiguring enables
    /// EnableRetryOnFailure, and MySqlRetryingExecutionStrategy refuses one outright (see the repo
    /// CLAUDE.md). Each call is one INSERT, one SELECT or one DELETE, so none needs one.
    ///
    /// Unlike the reward_claim DAO these methods do NOT catch and log: they THROW. The only caller is
    /// ThreadPuzzleIpLedger, which owns error reporting so that a missing table or a database outage is
    /// logged once, not once per lever pull.
    /// </summary>
    public partial class ShardDatabase
    {
        private const string InsertThreadPuzzleIpLedgerSql =
            "INSERT INTO `thread_puzzle_ip_ledger` (`ip_Key`, `kind`, `at_Utc`, `until_Utc`, `account_Id`, `character_Id`, `run_Id`, `run_Start_Group`, `ip_Address`) " +
            "VALUES (@ipKey, @kind, @atUtc, @untilUtc, @accountId, @characterId, @runId, @runStartGroup, @ipAddress)";

        private const string PruneThreadPuzzleIpLedgerSql =
            "DELETE FROM `thread_puzzle_ip_ledger` " +
            "WHERE (`kind` = 1 AND `at_Utc` < @failCutoff) OR (`kind` = 2 AND (`until_Utc` IS NULL OR `until_Utc` <= @nowUtc))";

        private const string ClearThreadPuzzleIpFailsSql =
            "DELETE FROM `thread_puzzle_ip_ledger` WHERE `ip_Key` = @ipKey AND `kind` = 1 AND `at_Utc` <= @atUtc";

        /// <summary>
        /// Deletes <paramref name="ipKey"/>'s fail rows at or before <paramref name="atUtc"/>: a lockout has just
        /// consumed them, so they must not count again once it ends. One DELETE. Returns the number of rows deleted.
        /// Throws on failure.
        /// </summary>
        public int ClearThreadPuzzleIpFails(string ipKey, DateTime atUtc)
        {
            if (string.IsNullOrEmpty(ipKey))
                throw new ArgumentException("A ledger key must be non-empty.", nameof(ipKey));

            using (var context = new ShardDbContext())
            {
                return context.Database.ExecuteSqlRaw(ClearThreadPuzzleIpFailsSql,
                    new MySqlParameter("@ipKey", ipKey),
                    new MySqlParameter("@atUtc", atUtc));
            }
        }

        /// <summary>Inserts one fail row. <c>row.UntilUtc</c> is ignored and stored as NULL. Throws on failure.</summary>
        public void InsertThreadPuzzleIpFail(ThreadPuzzleIpLedgerRow row)
        {
            if (row == null)
                throw new ArgumentNullException(nameof(row));

            InsertThreadPuzzleIpLedgerRow(row, ThreadPuzzleIpLedgerKind.Fail, null);
        }

        /// <summary>Inserts one lockout row. <c>row.UntilUtc</c> is required. Throws on failure.</summary>
        public void InsertThreadPuzzleIpLockout(ThreadPuzzleIpLedgerRow row)
        {
            if (row == null)
                throw new ArgumentNullException(nameof(row));

            if (!row.UntilUtc.HasValue)
                throw new ArgumentException("A lockout row needs UntilUtc.", nameof(row));

            InsertThreadPuzzleIpLedgerRow(row, ThreadPuzzleIpLedgerKind.Lockout, row.UntilUtc);
        }

        private static void InsertThreadPuzzleIpLedgerRow(ThreadPuzzleIpLedgerRow row, ThreadPuzzleIpLedgerKind kind, DateTime? untilUtc)
        {
            if (string.IsNullOrEmpty(row.IpKey))
                throw new ArgumentException("A ledger row needs an ip key.", nameof(row));

            using (var context = new ShardDbContext())
            {
                // Explicit MySqlParameters, as TryInsertRewardClaim does, so every NULL audit column is bound
                // as DBNull.Value by construction.
                context.Database.ExecuteSqlRaw(InsertThreadPuzzleIpLedgerSql,
                    new MySqlParameter("@ipKey", row.IpKey),
                    new MySqlParameter("@kind", (byte)kind),
                    new MySqlParameter("@atUtc", row.AtUtc),
                    new MySqlParameter("@untilUtc", (object)untilUtc ?? DBNull.Value),
                    new MySqlParameter("@accountId", (object)row.AccountId ?? DBNull.Value),
                    new MySqlParameter("@characterId", (object)row.CharacterId ?? DBNull.Value),
                    new MySqlParameter("@runId", (object)row.RunId ?? DBNull.Value),
                    new MySqlParameter("@runStartGroup", (object)row.RunStartGroup ?? DBNull.Value),
                    new MySqlParameter("@ipAddress", (object)row.IpAddress ?? DBNull.Value));
            }
        }

        /// <summary>
        /// The rows that can still matter at <paramref name="nowUtc"/>: fail rows with at_Utc inside
        /// (<paramref name="nowUtc"/> - <paramref name="failWindow"/>, ...] and lockout rows whose
        /// until_Utc is after <paramref name="nowUtc"/>. One SELECT. Times come back as DateTimeKind.Utc.
        /// Throws on failure.
        /// </summary>
        public List<ThreadPuzzleIpLedgerRow> LoadActiveThreadPuzzleIpLedger(DateTime nowUtc, TimeSpan failWindow)
        {
            var failCutoff = nowUtc - failWindow;

            List<ThreadPuzzleIpLedgerRow> rows;

            using (var context = new ShardDbContext())
            {
                context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

                rows = context.ThreadPuzzleIpLedger
                    .Where(r => (r.Kind == ThreadPuzzleIpLedgerKind.Fail && r.AtUtc > failCutoff)
                             || (r.Kind == ThreadPuzzleIpLedgerKind.Lockout && r.UntilUtc > nowUtc))
                    .OrderBy(r => r.Id)
                    .ToList();
            }

            foreach (var row in rows)
            {
                row.AtUtc = DateTime.SpecifyKind(row.AtUtc, DateTimeKind.Utc);

                if (row.UntilUtc.HasValue)
                    row.UntilUtc = DateTime.SpecifyKind(row.UntilUtc.Value, DateTimeKind.Utc);
            }

            return rows;
        }

        /// <summary>
        /// Deletes fail rows older than <paramref name="nowUtc"/> - <paramref name="failWindow"/> and
        /// lockout rows that have ended. One DELETE. Returns the number of rows deleted. Throws on failure.
        /// </summary>
        public int PruneExpiredThreadPuzzleIpLedger(DateTime nowUtc, TimeSpan failWindow)
        {
            using (var context = new ShardDbContext())
            {
                return context.Database.ExecuteSqlRaw(PruneThreadPuzzleIpLedgerSql,
                    new MySqlParameter("@failCutoff", nowUtc - failWindow),
                    new MySqlParameter("@nowUtc", nowUtc));
            }
        }
    }
}
