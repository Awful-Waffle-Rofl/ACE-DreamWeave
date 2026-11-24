using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.BalanceTrials;
using ACE.Server.Managers.Analytics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.BalanceTrials
{
    /// <summary>
    /// Drift guard between the trial_run / trial_event CREATE TABLE column sets in ace_analytics.sql and the
    /// INSERT column lists AnalyticsDatabase.WriteTrialRuns / WriteTrialEvents write, plus the queue behaviour of
    /// AnalyticsManager.RecordTrialRun / RecordTrialEvent. A pure source-text check for the schema half, on the same
    /// footing as AnalyticsKnotEventSchemaTests: AnalyticsDatabase needs a live MySQL connection, which
    /// ACE.Server.Tests does not have. The tables freeze on first deploy (no ALTER path), so a column added to one
    /// side and forgotten on the other would only show up in production.
    /// </summary>
    [TestClass]
    public class AnalyticsTrialSchemaTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Managers", "Analytics", "AnalyticsManager.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Managers/Analytics/AnalyticsManager.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Source(string file)
            => File.ReadAllText(Path.Combine(RepoRoot(), "ACE.Server", "Managers", "Analytics", file));

        private static string TableBody(string table)
        {
            var m = Regex.Match(Source("ace_analytics.sql"),
                @"CREATE TABLE IF NOT EXISTS `" + table + @"` \((?<body>.*?)\n\) ENGINE=InnoDB", RegexOptions.Singleline);
            Assert.IsTrue(m.Success, $"expected a CREATE TABLE IF NOT EXISTS `{table}` block in ace_analytics.sql");
            return m.Groups["body"].Value;
        }

        private static string[] SchemaColumns(string table)
            => TableBody(table).Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("`"))
                .Select(line => Regex.Match(line, @"^`([a-z0-9_]+)`").Groups[1].Value)
                .Where(name => name.Length > 0 && name != "id")
                .ToArray();

        private static string[] InsertColumns(string table)
        {
            var m = Regex.Match(Source("AnalyticsDatabase.cs"), @"INSERT INTO `" + table + @"` \((?<cols>[^)]+)\)", RegexOptions.Singleline);
            Assert.IsTrue(m.Success, $"expected an INSERT INTO `{table}` (...) column list in AnalyticsDatabase.cs");
            return Regex.Matches(m.Groups["cols"].Value, @"`([a-z0-9_]+)`").Cast<Match>().Select(x => x.Groups[1].Value).ToArray();
        }

        private static int ColumnWidth(string table, string column)
        {
            var m = Regex.Match(TableBody(table), @"^\s*`" + column + @"`\s+(?:VAR)?CHAR\((\d+)\)", RegexOptions.Multiline);
            Assert.IsTrue(m.Success, $"{table}.{column} is not a CHAR/VARCHAR column in ace_analytics.sql");
            return int.Parse(m.Groups[1].Value);
        }

        private static void AssertSameColumns(string table, int expectedCount)
        {
            var schema = SchemaColumns(table);
            var insert = InsertColumns(table);

            Assert.AreEqual(expectedCount, schema.Length, $"{table} has {expectedCount} non-id columns in the schema");
            Assert.AreEqual(expectedCount, insert.Length, $"{table} INSERT must name {expectedCount} columns");

            var inSchemaOnly = schema.Except(insert).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var inInsertOnly = insert.Except(schema).OrderBy(s => s, StringComparer.Ordinal).ToArray();

            Assert.AreEqual(0, inSchemaOnly.Length, $"columns in ace_analytics.sql but never written: {string.Join(", ", inSchemaOnly)}");
            Assert.AreEqual(0, inInsertOnly.Length, $"columns written but not in ace_analytics.sql: {string.Join(", ", inInsertOnly)}");
            Assert.AreEqual(insert.Length, insert.Distinct().Count(), $"{table} INSERT names a column twice");
        }

        [TestMethod]
        public void Trial_run_insert_names_every_schema_column() => AssertSameColumns("trial_run", 57);

        [TestMethod]
        public void Trial_event_insert_names_every_schema_column() => AssertSameColumns("trial_event", 16);

        // The brief's column contract, one "name TYPE NULL|NOT NULL" per line, in schema order. The tables freeze on
        // first deploy, so a type, width or nullability slip cannot be fixed later.
        private const string RunContract = @"
run_key CHAR(32) NOT NULL
trial_uuid CHAR(32) NOT NULL
rep INT NOT NULL
scenario_id VARCHAR(64) NOT NULL
scenario_hash CHAR(64) NOT NULL
archetype_id VARCHAR(64) NOT NULL
archetype_checksum CHAR(64) NOT NULL
state_digest CHAR(64) NULL
digest_drift TINYINT(1) NOT NULL
driver VARCHAR(64) NOT NULL
client_count INT NOT NULL
character_id INT UNSIGNED NOT NULL
char_level INT NOT NULL
gem_level INT NOT NULL
gem_level_pressed INT NULL
tier INT NOT NULL
dungeon_id VARCHAR(32) NOT NULL
family VARCHAR(32) NOT NULL
seed INT NOT NULL
mods_json VARCHAR(1024) NOT NULL
started_utc DATETIME(3) NOT NULL
ended_utc DATETIME(3) NOT NULL
end_state VARCHAR(8) NOT NULL
engaged_secs DOUBLE NOT NULL
idle_secs DOUBLE NOT NULL
ttk_trash_avg DOUBLE NULL
ttk_elite_avg DOUBLE NULL
ttk_boss DOUBLE NULL
kills_trash INT NOT NULL
kills_elite INT NOT NULL
boss_killed TINYINT(1) NOT NULL
deaths INT NOT NULL
dmg_dealt BIGINT NOT NULL
dmg_taken BIGINT NOT NULL
hits_out INT NOT NULL
misses_out INT NOT NULL
resisted_out INT NOT NULL
hits_in INT NOT NULL
evaded_in INT NOT NULL
resisted_in INT NOT NULL
xp_earned BIGINT NOT NULL
lum_earned BIGINT NOT NULL
xp_per_hour DOUBLE NULL
lum_per_hour DOUBLE NULL
xp_per_engaged_hour DOUBLE NULL
lum_per_engaged_hour DOUBLE NULL
xp_multiplier DOUBLE NOT NULL
boss_xp_multiplier DOUBLE NOT NULL
lum_multiplier DOUBLE NOT NULL
hp_low_pct DOUBLE NULL
stam_low_pct DOUBLE NULL
mana_low_pct DOUBLE NULL
consumables_used INT NOT NULL
ability_procs INT NOT NULL
pred_player_hits_to_kill_boss DOUBLE NULL
pred_boss_hits_to_kill_player DOUBLE NULL
pred_boss_hit_chance DOUBLE NULL";

        private const string EventContract = @"
run_key CHAR(32) NOT NULL
t_ms INT NOT NULL
kind VARCHAR(12) NOT NULL
dir VARCHAR(3) NULL
actor_wcid INT UNSIGNED NULL
target_wcid INT UNSIGNED NULL
actor_role VARCHAR(8) NULL
damage_type VARCHAR(16) NULL
amount BIGINT NULL
crit TINYINT(1) NULL
combat_type VARCHAR(8) NULL
spell_id INT UNSIGNED NULL
item_wcid INT UNSIGNED NULL
pred_json VARCHAR(1024) NULL
xp BIGINT NULL
lum BIGINT NULL";

        /// <summary>The schema's "name TYPE NULL|NOT NULL" per column, comments and defaults stripped, in order.</summary>
        private static string[] SchemaDefinitions(string table)
            => TableBody(table).Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("`") && !line.StartsWith("`id`"))
                .Select(line =>
                {
                    var m = Regex.Match(line, @"^`(?<n>[a-z0-9_]+)`\s+(?<t>[A-Z]+(?:\(\d+\))?(?: UNSIGNED)?)\s+(?<nn>NOT NULL|NULL)\b");
                    Assert.IsTrue(m.Success, $"{table}: cannot parse column line '{line}'");
                    return $"{m.Groups["n"].Value} {m.Groups["t"].Value} {m.Groups["nn"].Value}";
                })
                .ToArray();

        private static string[] Contract(string text)
            => text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        [TestMethod]
        public void Trial_run_types_widths_and_nullability_match_the_brief()
        {
            CollectionAssert.AreEqual(Contract(RunContract), SchemaDefinitions("trial_run"));

            var nullable = SchemaDefinitions("trial_run").Where(d => d.EndsWith(" NULL") && !d.EndsWith(" NOT NULL")).Select(d => d.Split(' ')[0]).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var expected = new[]
            {
                "state_digest", "gem_level_pressed", "ttk_trash_avg", "ttk_elite_avg", "ttk_boss", "xp_per_hour", "lum_per_hour",
                "xp_per_engaged_hour", "lum_per_engaged_hour", "hp_low_pct", "stam_low_pct", "mana_low_pct",
                "pred_player_hits_to_kill_boss", "pred_boss_hits_to_kill_player", "pred_boss_hit_chance",
            }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expected, nullable);
        }

        [TestMethod]
        public void Trial_event_types_widths_and_nullability_match_the_brief()
        {
            CollectionAssert.AreEqual(Contract(EventContract), SchemaDefinitions("trial_event"));

            var notNull = new[] { "run_key", "t_ms", "kind" };
            foreach (var def in SchemaDefinitions("trial_event"))
            {
                var name = def.Split(' ')[0];
                if (notNull.Contains(name))
                    Assert.IsTrue(def.EndsWith(" NOT NULL"), $"{name} must be NOT NULL");
                else
                    Assert.IsTrue(def.EndsWith(" NULL") && !def.EndsWith(" NOT NULL"), $"{name} must be NULL");
            }
        }

        /// <summary>
        /// The first row field named on each line of a writer's values[] initializer, as a snake_case column name
        /// (TMs becomes t_ms). One element per line, so element N must name the column at INSERT position N.
        /// </summary>
        private static string[] ValueOrder(string method)
        {
            var source = Source("AnalyticsDatabase.cs");
            var start = source.IndexOf("public static void " + method, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{method} not found in AnalyticsDatabase.cs");
            var open = source.IndexOf("new object[]", start, StringComparison.Ordinal);
            var close = source.IndexOf("};", open, StringComparison.Ordinal);
            Assert.IsTrue(open > 0 && close > open, $"{method}: values initializer not found");

            return source.Substring(open, close - open).Split('\n')
                .Select(l => Regex.Match(l, @"\br\.([A-Za-z]+)"))
                .Where(m => m.Success)
                .Select(m => Regex.Replace(m.Groups[1].Value, "(?<!^)([A-Z])", "_$1").ToLowerInvariant())
                .ToArray();
        }

        [TestMethod]
        public void Trial_run_values_are_in_insert_column_order()
            => CollectionAssert.AreEqual(InsertColumns("trial_run"), ValueOrder("WriteTrialRuns"));

        [TestMethod]
        public void Trial_event_values_are_in_insert_column_order()
            => CollectionAssert.AreEqual(InsertColumns("trial_event"), ValueOrder("WriteTrialEvents"));

        [TestMethod]
        public void Every_kind_and_end_state_fits_its_column()
        {
            var kindWidth = ColumnWidth("trial_event", "kind");
            var dirWidth = ColumnWidth("trial_event", "dir");
            var endWidth = ColumnWidth("trial_run", "end_state");

            Assert.AreEqual(12, kindWidth);
            Assert.AreEqual(3, dirWidth);
            Assert.AreEqual(8, endWidth);

            Assert.AreEqual(10, TrialEventKinds.All.Count);
            Assert.AreEqual(4, TrialEventKinds.AllEndStates.Count);
            Assert.AreEqual(TrialEventKinds.All.Count, TrialEventKinds.All.Distinct().Count());

            foreach (var kind in TrialEventKinds.All)
                Assert.IsTrue(kind.Length <= kindWidth, $"kind '{kind}' ({kind.Length}) overflows VARCHAR({kindWidth})");
            foreach (var dir in TrialEventKinds.AllDirs)
                Assert.IsTrue(dir.Length <= dirWidth, $"dir '{dir}' overflows VARCHAR({dirWidth})");
            foreach (var end in TrialEventKinds.AllEndStates)
                Assert.IsTrue(end.Length <= endWidth, $"end_state '{end}' ({end.Length}) overflows VARCHAR({endWidth})");

            // The writer's clamp constants are the same widths as the schema.
            Assert.AreEqual(kindWidth, AnalyticsDatabase.TrialEventKindMaxLength);
            Assert.AreEqual(dirWidth, AnalyticsDatabase.TrialEventDirMaxLength);
            Assert.AreEqual(endWidth, AnalyticsDatabase.TrialEndStateMaxLength);
        }

        /// <summary>Every KEY line of a table body as "name(col, col)", in schema order (UNIQUE and PRIMARY spelled out).</summary>
        private static string[] SchemaKeys(string table)
            => TableBody(table).Split('\n')
                .Select(line => line.Trim().TrimEnd(','))
                .Where(line => Regex.IsMatch(line, @"^(PRIMARY |UNIQUE )?KEY\b"))
                .Select(line => Regex.Replace(line.Replace("`", ""), @"\s+", " "))
                .ToArray();

        [TestMethod]
        public void Trial_run_and_event_keys_match_the_brief()
        {
            // The keys freeze with the table: a missing or reshaped index cannot be added on a deployed database.
            CollectionAssert.AreEqual(new[]
            {
                "PRIMARY KEY (id)",
                "UNIQUE KEY ux_run_key (run_key)",
                "KEY ix_trial (trial_uuid, rep)",
                "KEY ix_scen_arch_ts (scenario_id, archetype_id, started_utc)",
                "KEY ix_started (started_utc)",
            }, SchemaKeys("trial_run"));

            CollectionAssert.AreEqual(new[]
            {
                "PRIMARY KEY (id)",
                "KEY ix_run_t (run_key, t_ms)",
                "KEY ix_kind (kind)",
            }, SchemaKeys("trial_event"));
        }

        [TestMethod]
        public void Every_dungeon_index_id_fits_trial_run_dungeon_id()
        {
            var width = ColumnWidth("trial_run", "dungeon_id");
            Assert.AreEqual(32, width);
            Assert.AreEqual(width, AnalyticsDatabase.TrialDungeonIdMaxLength, "the writer's clamp matches the schema");

            // Content/dungeons/dynamic/index.json, two levels above Source/.
            var index = Path.Combine(Directory.GetParent(RepoRoot()).FullName, "Content", "dungeons", "dynamic", "index.json");
            if (!File.Exists(index))
                Assert.Inconclusive($"{index} is not present (Content/ is not published to the public mirror) - skipping.");

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(index));
            var ids = doc.RootElement.GetProperty("dungeons").EnumerateArray().Select(d => d.GetProperty("id").GetString()).ToList();
            Assert.IsTrue(ids.Count > 50, $"fixture: the index lists {ids.Count} dungeons");
            Assert.IsTrue(ids.Any(id => id.Length > 16), "control: some index id is longer than the old VARCHAR(16)");

            foreach (var id in ids)
                Assert.IsTrue(id.Length <= width, $"dungeon id '{id}' ({id.Length}) overflows VARCHAR({width})");
        }

        [TestMethod]
        public void Record_methods_drop_when_disabled_and_queue_when_enabled()
        {
            var wasEnabled = AnalyticsManager.IsEnabled;
            try
            {
                AnalyticsManager.ResetTrialQueuesForTests();

                AnalyticsManager.SetEnabledForTests(false);
                AnalyticsManager.RecordTrialRun(new AnalyticsDatabase.TrialRunRow { RunKey = "a" });
                AnalyticsManager.RecordTrialEvent(new AnalyticsDatabase.TrialEventRow { RunKey = "a" });
                Assert.AreEqual(0, AnalyticsManager.DrainTrialRunQueueForTests().Count);
                Assert.AreEqual(0, AnalyticsManager.DrainTrialEventQueueForTests().Count);

                AnalyticsManager.SetEnabledForTests(true);
                AnalyticsManager.RecordTrialRun(new AnalyticsDatabase.TrialRunRow { RunKey = "b" });
                AnalyticsManager.RecordTrialRun(null);
                AnalyticsManager.RecordTrialEvent(new AnalyticsDatabase.TrialEventRow { RunKey = "b", Kind = TrialEventKinds.Hit });
                AnalyticsManager.RecordTrialEvent(null);

                var runs = AnalyticsManager.DrainTrialRunQueueForTests();
                var events = AnalyticsManager.DrainTrialEventQueueForTests();
                Assert.AreEqual(1, runs.Count);
                Assert.AreEqual("b", runs[0].RunKey);
                Assert.AreEqual(1, events.Count);
                Assert.AreEqual(TrialEventKinds.Hit, events[0].Kind);
            }
            finally
            {
                AnalyticsManager.SetEnabledForTests(wasEnabled);
                AnalyticsManager.ResetTrialQueuesForTests();
            }
        }

        [TestMethod]
        public void Event_queue_drops_and_counts_at_the_cap()
        {
            var wasEnabled = AnalyticsManager.IsEnabled;
            try
            {
                AnalyticsManager.ResetTrialQueuesForTests();
                AnalyticsManager.SetEnabledForTests(true);

                Assert.AreEqual(50000, AnalyticsManager.TrialEventQueueCap);

                var extra = 3;
                for (int i = 0; i < AnalyticsManager.TrialEventQueueCap + extra; i++)
                    AnalyticsManager.RecordTrialEvent(new AnalyticsDatabase.TrialEventRow { RunKey = "c", TMs = i });

                Assert.AreEqual(extra, AnalyticsManager.TrialEventsDropped);

                var events = AnalyticsManager.DrainTrialEventQueueForTests();
                Assert.AreEqual(AnalyticsManager.TrialEventQueueCap, events.Count);
                Assert.AreEqual(0, events[0].TMs, "the oldest events are kept, the overflow is what is dropped");
            }
            finally
            {
                AnalyticsManager.SetEnabledForTests(wasEnabled);
                AnalyticsManager.ResetTrialQueuesForTests();
            }
        }
    }
}
