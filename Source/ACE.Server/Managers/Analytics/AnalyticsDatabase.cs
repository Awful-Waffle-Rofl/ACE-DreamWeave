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

        /// <summary>
        /// One banked-pyreal balance for one character at one flush. Rows are CHANGE-ONLY - the producer
        /// emits one only when the balance moved since that character's previous snapshot - so a reader
        /// carries the last value forward across the gaps. See ace_analytics.sql.
        /// </summary>
        public readonly struct BankSnapshotRow
        {
            public readonly uint CharacterId;
            public readonly string Name;
            public readonly uint AccountId;
            public readonly int Level;
            public readonly long BankedPyreals;

            public BankSnapshotRow(uint characterId, string name, uint accountId, int level, long bankedPyreals)
            {
                CharacterId = characterId;
                Name = name;
                AccountId = accountId;
                Level = level;
                BankedPyreals = bankedPyreals;
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

        public sealed class ChatRow
        {
            public DateTime Ts;
            public uint CharacterId;
            public string Name;
            public string Channel;
            public int Landblock;
            public string Message;
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

        /// <summary>One gem modifier on one finished run (child of <see cref="DungeonRunRow"/>).</summary>
        public sealed class DungeonRunModifierRow
        {
            public string ModifierId;
            public double Magnitude;
        }

        /// <summary>
        /// One aggregated placement outcome for one finished run (child of <see cref="DungeonRunRow"/>).
        /// Aggregated on the (Reason, Wcid, Role) triple by ThreadDungeonRun's ledger, so
        /// <see cref="Attempts"/> is a count of ATTEMPTS and can exceed the number of plan entries involved.
        /// </summary>
        public sealed class DungeonRunPlacementRow
        {
            /// <summary>"plan" or "place" - part of the aggregation key, see ace_analytics.sql.</summary>
            public string Phase;

            public string Reason;
            public bool IsFailure;
            public bool EntryPlaced;
            public bool Credited;
            public uint Wcid;
            public string Role;
            public int Attempts;
        }

        /// <summary>
        /// One finished Thread run, carrying its own child rows. Every value is captured by the
        /// producer (ThreadDungeonManager.EndRun) before this object is queued: it holds no run, no
        /// WorldObject and no live collection, so the writer thread can serialise it long after the run is
        /// gone. See ace_analytics.sql for what each column means.
        /// </summary>
        public sealed class DungeonRunRow
        {
            public uint RunId;

            /// <summary>
            /// Groups the runs one gem USE produced; 32 hex characters. COUNT(DISTINCT start_group) is what
            /// players did, COUNT(*) is what the server tried. See ace_analytics.sql.
            /// </summary>
            public string StartGroup;

            public DateTime StartedUtc;
            public DateTime EndedUtc;
            public double DurationSecs;
            public string EndState;
            public string EndReason;
            public bool Entered;
            public uint CharacterId;
            public string Name;

            /// <summary>The owner's level when the run OPENED. The unbiased one; see ace_analytics.sql.</summary>
            public int CharLevelStart;

            /// <summary>The owner's level at end time.</summary>
            public int CharLevel;

            public string DungeonId;
            public int GemLevel;
            public int Tier;
            public string Family;
            public int Instability;
            public int Presses;

            /// <summary>The gem's rng seed: the reproduction key for this exact population. See ace_analytics.sql.</summary>
            public int Seed;

            /// <summary>RESERVED for the gem-reroll design, no writer, always 0. See ace_analytics.sql.</summary>
            public int StartAttempts;

            /// <summary>One-shot latch: did the run ever get as far as building a plan and enqueueing placement.</summary>
            public bool PopulateReached;

            /// <summary>
            /// Milliseconds population took, or NULL when it never completed. Written as DBNull, never as a
            /// sentinel, so it drops out of AVG and percentile queries by itself.
            /// </summary>
            public int? PopulateMs;

            public int Planned;
            public int Spawned;
            public int Killed;
            public int ClearTarget;
            public uint BossWcidIntended;
            public uint BossWcidPlaced;
            public bool BossKilled;
            public double BossHealthRatio;
            public bool BossHealthClamped;
            public bool SurveyFiled;
            public int CreditedKills;
            public long XpGained;
            public long LumGained;

            public IReadOnlyList<DungeonRunModifierRow> Modifiers = new List<DungeonRunModifierRow>();
            public IReadOnlyList<DungeonRunPlacementRow> Placements = new List<DungeonRunPlacementRow>();
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
        /// One transactional flush: overwrite the live snapshot tables and append the interval rate rows
        /// plus any changed banked-pyreal balances.
        ///
        /// The bank snapshots ride in the SAME transaction as the roster and rate writes on purpose. The
        /// producer marks a balance as "already recorded" in its in-memory map as soon as it builds the row,
        /// so a bank row that committed while the rest of the flush rolled back would leave a balance change
        /// the producer believes was written and will never emit again.
        /// </summary>
        public static void WriteFlush(IReadOnlyList<RosterRow> roster, IReadOnlyDictionary<int, int> blockPops,
            IReadOnlyList<RateRow> rates, IReadOnlyList<BankSnapshotRow> bankSnapshots, DateTime tsUtc, double secs)
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

            if (bankSnapshots != null && bankSnapshots.Count > 0)
            {
                var sb = new StringBuilder("INSERT INTO `char_bank_snapshot` (`character_id`,`name`,`account_id`,`level`,`ts_utc`,`banked_pyreals`) VALUES ");
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                for (int i = 0; i < bankSnapshots.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"(@c{i},@n{i},@a{i},@l{i},@t,@bp{i})");
                    cmd.Parameters.AddWithValue($"@c{i}", bankSnapshots[i].CharacterId);
                    cmd.Parameters.AddWithValue($"@n{i}", bankSnapshots[i].Name);
                    cmd.Parameters.AddWithValue($"@a{i}", bankSnapshots[i].AccountId);
                    cmd.Parameters.AddWithValue($"@l{i}", bankSnapshots[i].Level);
                    cmd.Parameters.AddWithValue($"@bp{i}", bankSnapshots[i].BankedPyreals);
                }
                cmd.Parameters.AddWithValue("@t", tsUtc);
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

        /// <summary>
        /// Appends public chat rows. Kept out of WriteTier2's transaction on purpose: chat volume is
        /// unbounded by online count, and a large chat batch must never delay or roll back the
        /// item/currency audit, which is the more valuable of the two.
        /// </summary>
        public static void WriteChat(IReadOnlyList<ChatRow> rows)
        {
            if (rows.Count == 0)
                return;

            using var conn = new MySqlConnection(connectionString);
            conn.Open();

            var sb = new StringBuilder("INSERT INTO `chat_event` (`ts_utc`,`character_id`,`name`,`channel`,`landblock`,`message`) VALUES ");
            using var cmd = conn.CreateCommand();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (i > 0) sb.Append(',');
                sb.Append($"(@ts{i},@c{i},@n{i},@ch{i},@lb{i},@m{i})");
                cmd.Parameters.AddWithValue($"@ts{i}", r.Ts);
                cmd.Parameters.AddWithValue($"@c{i}", r.CharacterId);
                cmd.Parameters.AddWithValue($"@n{i}", r.Name);
                cmd.Parameters.AddWithValue($"@ch{i}", r.Channel);
                cmd.Parameters.AddWithValue($"@lb{i}", r.Landblock);
                cmd.Parameters.AddWithValue($"@m{i}", r.Message);
            }
            cmd.CommandText = sb.ToString();
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// Appends finished Thread runs and their children in ONE transaction.
        ///
        /// The parents go in one at a time rather than as a single multi-row INSERT, and that is not an
        /// oversight: MySQL's LAST_INSERT_ID() returns the id of the FIRST row of a multi-row insert, so a
        /// batched parent insert would give every run's children the same run_fk. One INSERT per run, then
        /// its own LAST_INSERT_ID(), is the only shape that keeps the foreign key honest. The batch is small
        /// by construction - it is bounded by how many dungeon runs END in one drain tick, not by player
        /// activity - so the extra round trips cost nothing worth optimising.
        ///
        /// Children are still batched per parent: one INSERT for that run's modifiers and one for its
        /// placements. A rollback loses the whole batch, which is correct - a parent row with no children is
        /// indistinguishable from a run that had none.
        /// </summary>
        public static void WriteDungeonRuns(IReadOnlyList<DungeonRunRow> rows)
        {
            if (rows.Count == 0)
                return;

            using var conn = new MySqlConnection(connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();

            foreach (var r in rows)
            {
                long runFk;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText =
                        "INSERT INTO `dungeon_run` (`run_id`,`start_group`,`started_utc`,`ended_utc`,`duration_secs`,`end_state`,`end_reason`,`entered`," +
                        "`character_id`,`name`,`char_level_start`,`char_level`,`dungeon_id`,`gem_level`,`tier`,`family`,`instability`,`presses`,`seed`," +
                        "`start_attempts`,`populate_reached`,`populate_ms`," +
                        "`planned`,`spawned`,`killed`,`clear_target`,`boss_wcid_intended`,`boss_wcid_placed`,`boss_killed`," +
                        "`boss_health_ratio`,`boss_health_clamped`,`survey_filed`,`credited_kills`,`xp_gained`,`lum_gained`) VALUES " +
                        "(@run,@sg,@start,@end,@dur,@state,@reason,@entered,@char,@name,@lvl0,@lvl,@dg,@gem,@tier,@fam,@inst,@press,@seed," +
                        "@sa,@pr,@pms," +
                        "@planned,@spawned,@killed,@ct,@bwi,@bwp,@bk,@bhr,@bhc,@sf,@ck,@xp,@lum);";

                    cmd.Parameters.AddWithValue("@run", r.RunId);
                    cmd.Parameters.AddWithValue("@sg", r.StartGroup);
                    cmd.Parameters.AddWithValue("@start", r.StartedUtc);
                    cmd.Parameters.AddWithValue("@end", r.EndedUtc);
                    cmd.Parameters.AddWithValue("@dur", r.DurationSecs);
                    cmd.Parameters.AddWithValue("@state", r.EndState);
                    cmd.Parameters.AddWithValue("@reason", r.EndReason);
                    cmd.Parameters.AddWithValue("@entered", r.Entered ? 1 : 0);
                    cmd.Parameters.AddWithValue("@char", r.CharacterId);
                    cmd.Parameters.AddWithValue("@name", r.Name);
                    cmd.Parameters.AddWithValue("@lvl0", r.CharLevelStart);
                    cmd.Parameters.AddWithValue("@lvl", r.CharLevel);
                    cmd.Parameters.AddWithValue("@dg", r.DungeonId);
                    cmd.Parameters.AddWithValue("@gem", r.GemLevel);
                    cmd.Parameters.AddWithValue("@tier", r.Tier);
                    cmd.Parameters.AddWithValue("@fam", r.Family);
                    cmd.Parameters.AddWithValue("@inst", r.Instability);
                    cmd.Parameters.AddWithValue("@press", r.Presses);
                    cmd.Parameters.AddWithValue("@seed", r.Seed);
                    cmd.Parameters.AddWithValue("@sa", r.StartAttempts);
                    cmd.Parameters.AddWithValue("@pr", r.PopulateReached ? 1 : 0);
                    // DBNull, not a sentinel: a run that never populated has no duration, and NULL is what
                    // keeps it out of AVG and percentile queries instead of dragging them toward zero.
                    cmd.Parameters.AddWithValue("@pms", r.PopulateMs.HasValue ? (object)r.PopulateMs.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@planned", r.Planned);
                    cmd.Parameters.AddWithValue("@spawned", r.Spawned);
                    cmd.Parameters.AddWithValue("@killed", r.Killed);
                    cmd.Parameters.AddWithValue("@ct", r.ClearTarget);
                    cmd.Parameters.AddWithValue("@bwi", r.BossWcidIntended);
                    cmd.Parameters.AddWithValue("@bwp", r.BossWcidPlaced);
                    cmd.Parameters.AddWithValue("@bk", r.BossKilled ? 1 : 0);
                    cmd.Parameters.AddWithValue("@bhr", r.BossHealthRatio);
                    cmd.Parameters.AddWithValue("@bhc", r.BossHealthClamped ? 1 : 0);
                    cmd.Parameters.AddWithValue("@sf", r.SurveyFiled ? 1 : 0);
                    cmd.Parameters.AddWithValue("@ck", r.CreditedKills);
                    cmd.Parameters.AddWithValue("@xp", r.XpGained);
                    cmd.Parameters.AddWithValue("@lum", r.LumGained);

                    cmd.ExecuteNonQuery();
                }

                // A SEPARATE command, not a second statement appended to the INSERT: multi-statement
                // behaviour is a connection-string option and ExecuteScalar over an INSERT-then-SELECT would
                // depend on it. LAST_INSERT_ID() is per-connection state, and this is the same connection
                // inside the same transaction, so nothing else can move it between the two calls.
                using (var idCmd = conn.CreateCommand())
                {
                    idCmd.Transaction = tx;
                    idCmd.CommandText = "SELECT LAST_INSERT_ID();";
                    runFk = Convert.ToInt64(idCmd.ExecuteScalar());
                }

                var modifiers = r.Modifiers;

                if (modifiers != null && modifiers.Count > 0)
                {
                    var sb = new StringBuilder("INSERT INTO `dungeon_run_modifier` (`run_fk`,`modifier_id`,`magnitude`) VALUES ");
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    for (int i = 0; i < modifiers.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append($"(@fk,@mid{i},@mag{i})");
                        cmd.Parameters.AddWithValue($"@mid{i}", modifiers[i].ModifierId);
                        cmd.Parameters.AddWithValue($"@mag{i}", modifiers[i].Magnitude);
                    }
                    cmd.Parameters.AddWithValue("@fk", runFk);
                    cmd.CommandText = sb.ToString();
                    cmd.ExecuteNonQuery();
                }

                var placements = r.Placements;

                if (placements != null && placements.Count > 0)
                {
                    var sb = new StringBuilder("INSERT INTO `dungeon_run_placement` (`run_fk`,`phase`,`reason`,`is_failure`,`entry_placed`,`credited`,`wcid`,`role`,`attempts`) VALUES ");
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    for (int i = 0; i < placements.Count; i++)
                    {
                        var p = placements[i];
                        if (i > 0) sb.Append(',');
                        sb.Append($"(@fk,@ph{i},@rs{i},@if{i},@ep{i},@cr{i},@w{i},@ro{i},@at{i})");
                        cmd.Parameters.AddWithValue($"@ph{i}", p.Phase);
                        cmd.Parameters.AddWithValue($"@rs{i}", p.Reason);
                        cmd.Parameters.AddWithValue($"@if{i}", p.IsFailure ? 1 : 0);
                        cmd.Parameters.AddWithValue($"@ep{i}", p.EntryPlaced ? 1 : 0);
                        cmd.Parameters.AddWithValue($"@cr{i}", p.Credited ? 1 : 0);
                        cmd.Parameters.AddWithValue($"@w{i}", p.Wcid);
                        cmd.Parameters.AddWithValue($"@ro{i}", p.Role);
                        cmd.Parameters.AddWithValue($"@at{i}", p.Attempts);
                    }
                    cmd.Parameters.AddWithValue("@fk", runFk);
                    cmd.CommandText = sb.ToString();
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        /// <summary>Prunes chat rows older than their own retention window.</summary>
        public static void PruneChat(int retentionDays)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM `chat_event` WHERE `ts_utc` < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-retentionDays));
            cmd.ExecuteNonQuery();
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

        /// <summary>
        /// Prunes banked-pyreal snapshot history older than the retention window, EXCEPT the newest row per
        /// character, which is kept forever.
        ///
        /// That exception is the invariant the table is read through: every character that has ever been
        /// snapshotted always has its most recent balance on file, so "what is this character's balance"
        /// is answerable at any time, and only the intermediate history ages out. The dashboard queries
        /// depend on exactly this - they read the latest row per character with NO time filter (an
        /// ORDER BY ts_utc DESC LIMIT 1, and a MAX(ts_utc) per-character join for the top-banked table) -
        /// so a flat cutoff would delete every row for anyone whose balance had not moved inside the
        /// window and render a wealthy inactive account as no money rather than as no change. Do NOT
        /// "simplify" this back into a plain DELETE ... WHERE ts_utc < cutoff: it looks equivalent and
        /// silently empties the leaderboard of everyone who is not actively trading.
        ///
        /// What is retained is bounded by character count - one row each - so there is no growth concern.
        ///
        /// The keep row is chosen by MAX(`id`), not by MAX(`ts_utc`): `id` is the autoincrement primary
        /// key, so it is unique and cannot tie, while two rows could in principle share a timestamp and
        /// leave the "newest" ambiguous.
        ///
        /// Shares the rate rows' AnalyticsRetentionDays rather than taking a key of its own: both are
        /// per-flush interval history of the same characters, and splitting the window would only make the
        /// two disagree about how far back "recently" goes.
        /// </summary>
        public static void PruneBankSnapshots(int retentionDays)
        {
            using var conn = new MySqlConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "DELETE `s` FROM `char_bank_snapshot` `s` " +
                "JOIN (SELECT `character_id`, MAX(`id`) AS `keep_id` FROM `char_bank_snapshot` GROUP BY `character_id`) `m` " +
                "ON `m`.`character_id` = `s`.`character_id` " +
                "WHERE `s`.`ts_utc` < @cutoff AND `s`.`id` < `m`.`keep_id`;";
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
