using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

using MySqlConnector;

using ACE.Common;

namespace ACE.Server.Managers.Analytics
{
    /// <summary>
    /// Thin MySqlConnector data layer for the ace_analytics database. Deliberately not EF/scaffolded:
    /// this DB is new and its writes are simple bulk inserts, so a self-contained connector keeps
    /// analytics fully isolated from the shard DB path. All work runs on the AnalyticsManager worker
    /// thread (never a sim thread).
    /// </summary>
    internal static class AnalyticsDatabase
    {
        private static string connectionString;

        public readonly struct RosterRow
        {
            public readonly uint CharacterId;
            public readonly string Name;
            public readonly int Level;
            public readonly int Landblock;

            public RosterRow(uint characterId, string name, int level, int landblock)
            {
                CharacterId = characterId;
                Name = name;
                Level = level;
                Landblock = landblock;
            }
        }

        public readonly struct RateRow
        {
            public readonly uint CharacterId;
            public readonly string Name;
            public readonly long Xp;
            public readonly long Lum;

            public RateRow(uint characterId, string name, long xp, long lum)
            {
                CharacterId = characterId;
                Name = name;
                Xp = xp;
                Lum = lum;
            }
        }

        public sealed class ItemFlowRow
        {
            public DateTime Ts;
            public string Kind;
            public uint FromId;
            public string FromName;
            public uint ToId;
            public string ToName;
            public bool ToIsPlayer;
            public uint ItemWcid;
            public string ItemName;
            public int StackSize;
            public long Value;
        }

        public sealed class CurrencyFlowRow
        {
            public DateTime Ts;
            public string Kind;
            public uint FromId;
            public string FromName;
            public uint ToId;
            public string ToName;
            public string Currency;
            public long Amount;
        }

        /// <summary>
        /// Creates the database if needed and applies the (idempotent) schema. Throws on failure so
        /// the caller can disable analytics rather than run half-initialized.
        /// </summary>
        public static void Initialize(MySqlConfiguration cfg)
        {
            // Connect without a database first so we can create it if it doesn't exist.
            var serverConn = $"server={cfg.Host};port={cfg.Port};user={cfg.Username};password={cfg.Password};{cfg.ConnectionOptions}";
            using (var conn = new MySqlConnection(serverConn))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{cfg.Database}` DEFAULT CHARACTER SET utf8mb4;";
                cmd.ExecuteNonQuery();
            }

            connectionString = $"server={cfg.Host};port={cfg.Port};database={cfg.Database};user={cfg.Username};password={cfg.Password};{cfg.ConnectionOptions}";

            using (var conn = new MySqlConnection(connectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = LoadSchemaSql();
                cmd.ExecuteNonQuery();
            }
        }

        private static string LoadSchemaSql()
        {
            const string resource = "ACE.Server.Managers.Analytics.ace_analytics.sql";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded analytics schema '{resource}' not found.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>
        /// One transactional flush: overwrite the live snapshot tables and append the interval rate rows.
        /// </summary>
        public static void WriteFlush(IReadOnlyList<RosterRow> roster, IReadOnlyDictionary<int, int> blockPops,
            IReadOnlyList<RateRow> rates, DateTime tsUtc, double secs)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            ExecNonQuery(conn, tx, "DELETE FROM `live_roster`;");
            if (roster.Count > 0)
            {
                var sb = new StringBuilder("INSERT INTO `live_roster` (`character_id`,`name`,`level`,`landblock`,`updated_utc`) VALUES ");
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                for (int i = 0; i < roster.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"(@c{i},@n{i},@l{i},@b{i},@t)");
                    cmd.Parameters.AddWithValue($"@c{i}", roster[i].CharacterId);
                    cmd.Parameters.AddWithValue($"@n{i}", roster[i].Name);
                    cmd.Parameters.AddWithValue($"@l{i}", roster[i].Level);
                    cmd.Parameters.AddWithValue($"@b{i}", roster[i].Landblock);
                }
                cmd.Parameters.AddWithValue("@t", tsUtc);
                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }

            ExecNonQuery(conn, tx, "DELETE FROM `live_landblock_pop`;");
            if (blockPops.Count > 0)
            {
                var sb = new StringBuilder("INSERT INTO `live_landblock_pop` (`landblock`,`players`,`updated_utc`) VALUES ");
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                int i = 0;
                foreach (var kvp in blockPops)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"(@b{i},@p{i},@t)");
                    cmd.Parameters.AddWithValue($"@b{i}", kvp.Key);
                    cmd.Parameters.AddWithValue($"@p{i}", kvp.Value);
                    i++;
                }
                cmd.Parameters.AddWithValue("@t", tsUtc);
                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }

            if (rates.Count > 0)
            {
                var sb = new StringBuilder("INSERT INTO `char_rate_interval` (`character_id`,`name`,`ts_utc`,`secs`,`xp_gained`,`lum_gained`) VALUES ");
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                for (int i = 0; i < rates.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"(@c{i},@n{i},@t,@s,@x{i},@m{i})");
                    cmd.Parameters.AddWithValue($"@c{i}", rates[i].CharacterId);
                    cmd.Parameters.AddWithValue($"@n{i}", rates[i].Name);
                    cmd.Parameters.AddWithValue($"@x{i}", rates[i].Xp);
                    cmd.Parameters.AddWithValue($"@m{i}", rates[i].Lum);
                }
                cmd.Parameters.AddWithValue("@t", tsUtc);
                cmd.Parameters.AddWithValue("@s", secs);
                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        /// <summary>Appends a batch of Tier-2 audit events (item + currency flows) in one transaction.</summary>
        public static void WriteTier2(IReadOnlyList<ItemFlowRow> items, IReadOnlyList<CurrencyFlowRow> currencies)
        {
            if (items.Count == 0 && currencies.Count == 0)
                return;

            using var conn = new MySqlConnection(connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            if (items.Count > 0)
            {
                var sb = new StringBuilder("INSERT INTO `item_flow_event` (`ts_utc`,`kind`,`from_id`,`from_name`,`to_id`,`to_name`,`to_is_player`,`item_wcid`,`item_name`,`stack_size`,`value`) VALUES ");
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                for (int i = 0; i < items.Count; i++)
                {
                    var r = items[i];
                    if (i > 0) sb.Append(',');
                    sb.Append($"(@ts{i},@k{i},@f{i},@fn{i},@t{i},@tn{i},@ip{i},@w{i},@in{i},@ss{i},@v{i})");
                    cmd.Parameters.AddWithValue($"@ts{i}", r.Ts);
                    cmd.Parameters.AddWithValue($"@k{i}", r.Kind);
                    cmd.Parameters.AddWithValue($"@f{i}", r.FromId);
                    cmd.Parameters.AddWithValue($"@fn{i}", r.FromName);
                    cmd.Parameters.AddWithValue($"@t{i}", r.ToId);
                    cmd.Parameters.AddWithValue($"@tn{i}", r.ToName);
                    cmd.Parameters.AddWithValue($"@ip{i}", r.ToIsPlayer ? 1 : 0);
                    cmd.Parameters.AddWithValue($"@w{i}", r.ItemWcid);
                    cmd.Parameters.AddWithValue($"@in{i}", r.ItemName);
                    cmd.Parameters.AddWithValue($"@ss{i}", r.StackSize);
                    cmd.Parameters.AddWithValue($"@v{i}", r.Value);
                }
                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }

            if (currencies.Count > 0)
            {
                var sb = new StringBuilder("INSERT INTO `currency_flow_event` (`ts_utc`,`kind`,`from_id`,`from_name`,`to_id`,`to_name`,`currency`,`amount`) VALUES ");
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                for (int i = 0; i < currencies.Count; i++)
                {
                    var r = currencies[i];
                    if (i > 0) sb.Append(',');
                    sb.Append($"(@ts{i},@k{i},@f{i},@fn{i},@t{i},@tn{i},@cu{i},@am{i})");
                    cmd.Parameters.AddWithValue($"@ts{i}", r.Ts);
                    cmd.Parameters.AddWithValue($"@k{i}", r.Kind);
                    cmd.Parameters.AddWithValue($"@f{i}", r.FromId);
                    cmd.Parameters.AddWithValue($"@fn{i}", r.FromName);
                    cmd.Parameters.AddWithValue($"@t{i}", r.ToId);
                    cmd.Parameters.AddWithValue($"@tn{i}", r.ToName);
                    cmd.Parameters.AddWithValue($"@cu{i}", r.Currency);
                    cmd.Parameters.AddWithValue($"@am{i}", r.Amount);
                }
                cmd.CommandText = sb.ToString();
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        /// <summary>Prunes raw interval rows older than the retention window.</summary>
        public static void PruneRates(int retentionDays)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM `char_rate_interval` WHERE `ts_utc` < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-retentionDays));
            cmd.ExecuteNonQuery();
        }

        private static void ExecNonQuery(MySqlConnection conn, MySqlTransaction tx, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
