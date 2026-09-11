using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the invariant that every World update script can be run against a database that ALREADY has
    /// its change.
    ///
    /// The applied-updates ledger (DatabaseSetupScripts/Updates/&lt;db&gt;/applied_updates.txt) lives in the
    /// build output directory, while the database it describes is shared. A second checkout, a wiped bin,
    /// or a restored backup therefore all present a database that already carries changes the local ledger
    /// has never seen. Since #830 the boot patcher (correctly) does not record a script that threw and stops
    /// the run, so an unguarded `ALTER TABLE ... ADD COLUMN` in that situation fails on EVERY boot and blocks
    /// every later script in the directory - the exact failure this test exists to prevent recurring.
    ///
    /// MySQL 8.0 has no ADD COLUMN IF NOT EXISTS, and neither of the two runners that execute these files
    /// (the boot patcher's single MySqlCommand, and CI's `mysql &lt; file`) supports DELIMITER, so a stored
    /// procedure is out. The remaining shape both accept is a SET / PREPARE / EXECUTE guard around an
    /// information_schema check, which is why PatchDatabase's connection string sets AllowUserVariables=true.
    /// </summary>
    [TestClass]
    public class DbUpdateScriptIdempotencyTests
    {
        private const string WorldUpdatesRelativePath = "Database/Updates/World";
        private const string PatcherSourceRelativePath = "Source/ACE.Server/Program_DbUpdates.cs";

        // Unconditional DDL that fails on a re-run. CREATE TABLE / DROP TABLE have IF [NOT] EXISTS forms
        // in MySQL 8.0 and are only flagged when they omit them; ALTER TABLE (ADD COLUMN / ADD INDEX /
        // ADD CONSTRAINT / MODIFY / CHANGE / RENAME) and CREATE INDEX have no such form, so they always
        // need the SET/PREPARE/EXECUTE guard.
        private static readonly Regex UnguardableDdl =
            new Regex(
                @"\bALTER\s+TABLE\b"
                + @"|\bCREATE\s+(UNIQUE\s+|FULLTEXT\s+|SPATIAL\s+)?INDEX\b"
                + @"|\bCREATE\s+(TEMPORARY\s+)?TABLE\b(?!\s+IF\s+NOT\s+EXISTS)"
                + @"|\bDROP\s+(TEMPORARY\s+)?TABLE\b(?!\s+IF\s+EXISTS)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Guard =
            new Regex(@"\bPREPARE\b\s+\w+\s+\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [TestMethod]
        public void EveryWorldUpdateScriptIsRerunnable()
        {
            var dir = FindInSourceTree(WorldUpdatesRelativePath);
            Assert.IsNotNull(dir, $"Could not find {WorldUpdatesRelativePath} by walking up from {AppContext.BaseDirectory}.");

            var scripts = Directory.GetFiles(dir, "*.sql").OrderBy(f => f).ToList();
            Assert.AreNotEqual(0, scripts.Count, $"Found no *.sql files in {dir} - the test would pass vacuously.");

            var unguarded = new List<string>();

            foreach (var script in scripts)
            {
                var sql = StripComments(File.ReadAllText(script));

                // The DDL text inside a guard lives in a quoted string handed to PREPARE, so a file that has
                // a guard at all is only counted as unguarded when bare DDL survives outside those literals.
                if (Guard.IsMatch(sql))
                    sql = StripSingleQuotedLiterals(sql);

                if (UnguardableDdl.IsMatch(sql))
                    unguarded.Add(Path.GetFileName(script));
            }

            Assert.AreEqual(
                0,
                unguarded.Count,
                "These World update scripts run unconditional DDL (ALTER TABLE, CREATE INDEX, or a CREATE / "
                + "DROP TABLE missing its IF [NOT] EXISTS), so re-running them against a database that already "
                + "has the change fails and blocks every later script on every boot. Add IF [NOT] EXISTS where "
                + "MySQL 8.0 has one, otherwise wrap the DDL in the SET / PREPARE / EXECUTE information_schema "
                + "guard used by 2026-08-27-00-Add-Speed-Season-Start-Wcid.sql: " + string.Join(", ", unguarded));
        }

        [TestMethod]
        public void PatcherConnectionStringAllowsUserVariables()
        {
            var path = FindInSourceTree(PatcherSourceRelativePath);
            Assert.IsNotNull(path, $"Could not find {PatcherSourceRelativePath} by walking up from {AppContext.BaseDirectory}.");

            var source = File.ReadAllText(path);

            Assert.IsTrue(
                source.Contains("AllowUserVariables=true"),
                "PatchDatabase's connection string must set AllowUserVariables=true. Without it MySqlConnector "
                + "reads the `@` variables in an update script's idempotency guard as command parameters and "
                + "throws \"Parameter '@x' must be defined\" before the statement reaches the server, which would "
                + "make every guarded script fail on every boot.");
        }

        private static string StripComments(string sql)
        {
            sql = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            sql = Regex.Replace(sql, @"--[^\r\n]*", " ");
            sql = Regex.Replace(sql, @"^\s*#[^\r\n]*$", " ", RegexOptions.Multiline);
            return sql;
        }

        /// <summary>
        /// Removes '...' literals, honouring MySQL's doubled-quote escape ('') so a COMMENT '...it''s...'
        /// inside a guarded DDL string does not leave the rest of the file looking like literal text.
        /// </summary>
        private static string StripSingleQuotedLiterals(string sql)
        {
            return Regex.Replace(sql, @"'(?:[^']|'')*'", " ");
        }

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works regardless
        /// of the bin\Debug vs bin\x64\Debug output layout (same idiom as PropertyRegistryTests).
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate) || Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
