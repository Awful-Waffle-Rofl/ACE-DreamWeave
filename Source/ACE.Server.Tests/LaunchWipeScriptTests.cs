using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Drift guard for Database/Optional/Shard/launch-wipe.sql, the operator-run launch wipe
    /// (Docs/LAUNCH-2026-10-02.md). The script deletes by complement, so a shard table it does not
    /// know about is a table whose rows silently survive the wipe - or, if it holds character ids,
    /// hands a wiped player's data to whoever the guid allocator reissues that id to. These tests
    /// fail the build the moment a migration adds a table the script has not classified, or a
    /// constant in the script stops matching the C# member it names.
    ///
    /// Pure file and reflection checks: no database, no PropertyManager reads.
    /// </summary>
    [TestClass]
    public class LaunchWipeScriptTests
    {
        private static readonly string[] Verdicts = { "WIPE", "CLEAN", "KEEP" };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Database", "Optional", "Shard", "launch-wipe.sql")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Optional/Shard/launch-wipe.sql by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string Script() => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Optional", "Shard", "launch-wipe.sql"));

        private static string StripComments(string sql)
        {
            sql = Regex.Replace(sql, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"(?m)--.*$", string.Empty);
            return sql;
        }

        /// <summary>table name -> verdict, from the frozen INVENTORY-BEGIN / INVENTORY-END header. Duplicates are reported.</summary>
        private static (List<(string Table, string Verdict)> Rows, List<string> Malformed) Inventory()
        {
            var lines = Script().Replace("\r\n", "\n").Split('\n');
            var begin = Array.FindIndex(lines, l => l.Trim() == "-- INVENTORY-BEGIN");
            var end = Array.FindIndex(lines, l => l.Trim() == "-- INVENTORY-END");

            Assert.IsTrue(begin >= 0 && end > begin, "launch-wipe.sql must carry a '-- INVENTORY-BEGIN' ... '-- INVENTORY-END' block");

            var rows = new List<(string, string)>();
            var malformed = new List<string>();

            for (var i = begin + 1; i < end; i++)
            {
                var m = Regex.Match(lines[i].Trim(), @"^--\s+([a-z0-9_]+)\s+([A-Z]+)$");
                if (m.Success)
                    rows.Add((m.Groups[1].Value, m.Groups[2].Value));
                else
                    malformed.Add(lines[i]);
            }

            return (rows, malformed);
        }

        /// <summary>
        /// Every shard table the schema can contain: ShardBase.sql, plus every CREATE TABLE under
        /// Database/Updates/Shard with RENAME TABLE applied, plus every table the EF model maps.
        /// RenameSources are the pre-rename names, which a shard can legitimately still carry.
        /// </summary>
        private static (SortedSet<string> Tables, SortedSet<string> RenameSources, Dictionary<string, string> Origin) DerivedTables()
        {
            var root = RepoRoot();
            var tables = new SortedSet<string>(StringComparer.Ordinal);
            var origin = new Dictionary<string, string>(StringComparer.Ordinal);

            var baseSql = StripComments(File.ReadAllText(Path.Combine(root, "Database", "Base", "ShardBase.sql")));
            foreach (Match m in Regex.Matches(baseSql, @"CREATE TABLE\s+`([A-Za-z0-9_]+)`"))
            {
                tables.Add(m.Groups[1].Value);
                origin.TryAdd(m.Groups[1].Value, "ShardBase.sql");
            }

            Assert.IsTrue(tables.Contains("biota") && tables.Contains("character"), "ShardBase.sql parse found no biota/character - the regex no longer matches the file");

            var renames = new List<(string From, string To, string File)>();

            foreach (var file in Directory.GetFiles(Path.Combine(root, "Database", "Updates", "Shard"), "*.sql").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                var sql = StripComments(File.ReadAllText(file));
                var name = Path.GetFileName(file);

                foreach (Match m in Regex.Matches(sql, @"CREATE TABLE\s+(?:IF NOT EXISTS\s+)?`([A-Za-z0-9_]+)`", RegexOptions.IgnoreCase))
                {
                    tables.Add(m.Groups[1].Value);
                    origin.TryAdd(m.Groups[1].Value, name);
                }

                foreach (Match m in Regex.Matches(sql, @"RENAME TABLE\s+`([A-Za-z0-9_]+)`\s+TO\s+`([A-Za-z0-9_]+)`", RegexOptions.IgnoreCase))
                    renames.Add((m.Groups[1].Value, m.Groups[2].Value, name));
            }

            var renameSources = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (from, to, file) in renames)
            {
                tables.Remove(from);
                tables.Add(to);
                origin[to] = file;
                renameSources.Add(from);
            }

            // Code-side source: a table the EF model maps but no migration creates is still a table
            // the server writes to. No connection is opened; the model is built from metadata only.
            var options = new DbContextOptionsBuilder<ShardDbContext>()
                .UseMySql("server=127.0.0.1;port=3306;user=none;password=none;database=none", new MySqlServerVersion(new Version(8, 0, 36)))
                .Options;
            using (var context = new ShardDbContext(options))
            {
                foreach (var entity in context.Model.GetEntityTypes())
                {
                    var table = entity.GetTableName();
                    if (string.IsNullOrEmpty(table) || renameSources.Contains(table))
                        continue;
                    tables.Add(table);
                    origin.TryAdd(table, $"EF model ({entity.ClrType.Name})");
                }
            }

            return (tables, renameSources, origin);
        }

        [TestMethod]
        public void Inventory_ClassifiesEveryShardTableExactlyOnce()
        {
            var (rows, malformed) = Inventory();
            var (derived, renameSources, origin) = DerivedTables();

            Assert.AreEqual(0, malformed.Count, "Malformed inventory line(s) - expected '-- <table> <WIPE|CLEAN|KEEP>':\n" + string.Join("\n", malformed));

            var badVerdicts = rows.Where(r => !Verdicts.Contains(r.Verdict)).Select(r => $"{r.Table} {r.Verdict}").ToList();
            Assert.AreEqual(0, badVerdicts.Count, "Invalid verdict(s):\n" + string.Join("\n", badVerdicts));

            var duplicates = rows.GroupBy(r => r.Table).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.AreEqual(0, duplicates.Count, "Table(s) listed more than once:\n" + string.Join("\n", duplicates));

            var listed = new HashSet<string>(rows.Select(r => r.Table), StringComparer.Ordinal);

            var missing = derived.Where(t => !listed.Contains(t)).Select(t => $"{t}  (from {origin[t]})").ToList();
            Assert.AreEqual(0, missing.Count,
                "Shard table(s) the launch wipe does not classify. Add each to the INVENTORY block of " +
                "Database/Optional/Shard/launch-wipe.sql with a verdict, and add its WIPE / CLEAN statements and verify check:\n" +
                string.Join("\n", missing));

            var unknown = listed.Where(t => !derived.Contains(t) && !renameSources.Contains(t)).OrderBy(t => t).ToList();
            Assert.AreEqual(0, unknown.Count, "Inventory lists table(s) no schema source defines (typo or dropped table):\n" + string.Join("\n", unknown));
        }

        [TestMethod]
        public void Inventory_EveryWipeAndExplicitCleanTableIsHandledInTheBody()
        {
            var (rows, _) = Inventory();
            var body = StripComments(Script());

            var unhandled = new List<string>();

            foreach (var (table, verdict) in rows)
            {
                if (verdict == "KEEP")
                    continue;

                // biota_properties_* and character_properties_* are cleaned by ON DELETE CASCADE from
                // biota / character (Database/Base/ShardBase.sql), not by a statement of their own.
                if (verdict == "CLEAN" && (table.StartsWith("biota_properties_") || table.StartsWith("character_properties_")))
                    continue;

                // A guarded table is handled twice through @lw_t (the delete and its verify check);
                // a base table is named directly.
                var guardedUses = Regex.Matches(body, $@"SET @lw_t = '{Regex.Escape(table)}';").Count;
                var directUses = Regex.Matches(body, $@"(?:FROM|JOIN)\s+`?{Regex.Escape(table)}`?\s").Count;

                if (verdict == "WIPE" && guardedUses < 2)
                    unhandled.Add($"{table} WIPE: needs a guarded delete and a guarded verify (found {guardedUses} SET @lw_t)");
                else if (verdict == "CLEAN" && guardedUses < 2 && directUses < 2)
                    unhandled.Add($"{table} CLEAN: needs a clean statement and a verify check (found {guardedUses} guarded, {directUses} direct)");
            }

            Assert.AreEqual(0, unhandled.Count, "Inventory verdict(s) with no code behind them:\n" + string.Join("\n", unhandled));
        }

        [TestMethod]
        public void Constants_MatchTheCSharpMembersTheyName()
        {
            var script = Script();
            var matches = Regex.Matches(script, @"(?m)^SET @c_([A-Za-z0-9]+)_([A-Za-z0-9_]+) = (\d+);\s*$");

            Assert.IsTrue(matches.Count >= 10, $"Expected the constants block; found {matches.Count} '@c_' lines");

            var entityEnums = typeof(ObjectGuid).Assembly.GetTypes().Where(t => t.IsEnum).ToList();
            var wrong = new List<string>();
            var defined = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in matches)
            {
                var type = m.Groups[1].Value;
                var member = m.Groups[2].Value;
                var sqlValue = long.Parse(m.Groups[3].Value);
                defined.Add($"@c_{type}_{member}");

                long? actual = null;

                if (type == "ObjectGuid")
                {
                    var prop = typeof(ObjectGuid).GetProperty(member, BindingFlags.Public | BindingFlags.Static);
                    if (prop != null)
                        actual = Convert.ToInt64(prop.GetValue(null));
                }
                else if (type == "DefaultLong")
                {
                    if (DefaultPropertyManager.DefaultLongProperties.TryGetValue(member, out var p))
                        actual = p.Item;
                }
                else
                {
                    var candidates = entityEnums.Where(t => t.Name == type).ToList();
                    if (candidates.Count != 1)
                    {
                        wrong.Add($"@c_{type}_{member}: {candidates.Count} enum types named {type} in ACE.Entity");
                        continue;
                    }
                    if (Enum.GetNames(candidates[0]).Contains(member))
                        actual = Convert.ToInt64(Enum.Parse(candidates[0], member));
                }

                if (actual == null)
                    wrong.Add($"@c_{type}_{member}: no such C# member");
                else if (actual.Value != sqlValue)
                    wrong.Add($"@c_{type}_{member}: script says {sqlValue}, C# says {actual.Value}");
            }

            Assert.AreEqual(0, wrong.Count, "launch-wipe.sql constant(s) out of step with C#:\n" + string.Join("\n", wrong));

            // An undefined user variable is NULL in MySQL, and `x IN (NULL)` / `BETWEEN NULL AND ...`
            // are silently false - a typo'd constant would quietly keep or delete the wrong rows.
            var body = StripComments(script);
            var used = new HashSet<string>(Regex.Matches(body, @"@c_[A-Za-z0-9_]+").Select(x => x.Value), StringComparer.Ordinal);
            var undefined = used.Where(u => !defined.Contains(u)).OrderBy(u => u).ToList();
            Assert.AreEqual(0, undefined.Count, "Constant(s) used but never SET:\n" + string.Join("\n", undefined));

            // Each constant occurs once in its own SET line; fewer than two occurrences means no use.
            var unused = defined.Where(d => Regex.Matches(body, Regex.Escape(d) + @"(?![A-Za-z0-9_])").Count < 2).OrderBy(d => d).ToList();
            Assert.AreEqual(0, unused.Count, "Constant(s) SET but never used (remove them, or the body is using a bare number instead):\n" + string.Join("\n", unused));
        }

        [TestMethod]
        public void Body_UsesNoBareGuidBoundsOrEnumHex()
        {
            var body = StripComments(Script());
            var codeLines = body.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("SET @c_")).ToList();
            var code = string.Join("\n", codeLines);

            var bounds = new[] { ObjectGuid.PlayerMin, ObjectGuid.PlayerMax, ObjectGuid.StaticObjectMin, ObjectGuid.StaticObjectMax, ObjectGuid.DynamicMin, ObjectGuid.DynamicMax };
            var hits = new List<string>();

            foreach (var b in bounds)
            {
                if (Regex.IsMatch(code, $@"(?<![0-9]){b}(?![0-9])"))
                    hits.Add($"decimal {b}");
                if (Regex.IsMatch(code, $@"0x0*{b:X}\b", RegexOptions.IgnoreCase))
                    hits.Add($"hex 0x{b:X}");
            }

            // Any 0x literal in code is suspect: MySQL reads it as a binary string in many contexts.
            foreach (Match m in Regex.Matches(code, @"\b0x[0-9A-Fa-f]+\b"))
                hits.Add($"hex literal {m.Value}");

            Assert.AreEqual(0, hits.Count, "Use the @c_ constants instead of bare values:\n" + string.Join("\n", hits.Distinct()));
        }

        [TestMethod]
        public void Body_HasNoDdlNoTransactionControlAndNoUse()
        {
            // DDL implicitly commits, which would end the wrapper's transaction halfway through the
            // wipe. The scratch schema's DDL lives in tools/launch-wipe.sh, before START TRANSACTION,
            // and the transaction itself is opened and closed by the wrapper.
            var body = StripComments(Script());

            var forbidden = new (string Pattern, string Why)[]
            {
                (@"\bDROP\b", "DROP"),
                (@"\bTRUNCATE\b", "TRUNCATE (implicit commit)"),
                (@"\bCREATE\s+DATABASE\b", "CREATE DATABASE"),
                (@"\bCREATE\b", "CREATE (any DDL implicitly commits)"),
                (@"\bALTER\b", "ALTER (implicit commit)"),
                (@"\bRENAME\b", "RENAME (implicit commit)"),
                (@"(?m)^\s*USE\b", "top-level USE"),
                (@"\bCOMMIT\b", "COMMIT (the wrapper decides)"),
                (@"\bROLLBACK\b", "ROLLBACK (the wrapper decides)"),
                (@"\bSTART\s+TRANSACTION\b", "START TRANSACTION (the wrapper opens it)"),
                (@"\bautocommit\b", "autocommit (the wrapper sets it)"),
                (@"\bLOCK\s+TABLES\b", "LOCK TABLES (implicit commit)"),
            };

            var found = forbidden.Where(f => Regex.IsMatch(body, f.Pattern, RegexOptions.IgnoreCase)).Select(f => f.Why).ToList();
            Assert.AreEqual(0, found.Count, "launch-wipe.sql must not contain:\n" + string.Join("\n", found));

            // The decision must be the script's last statement: nothing may run after the gate.
            var lastStatement = body.TrimEnd().Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).Last();
            Assert.AreEqual("CALL ace_wipe_scratch.lw_gate()", lastStatement, "The last statement of launch-wipe.sql must be the gate call");
        }
    }
}
