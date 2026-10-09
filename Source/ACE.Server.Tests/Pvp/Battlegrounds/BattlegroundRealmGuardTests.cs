using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The realm guard (Docs/Pvp/ATTACK-DEFEND.md "Stripped copy"): a battleground match plays in a private realm-1 copy of its landblock,
    /// and that copy is empty of retail objects ONLY because realm 1 strips statics and encounters by default and the landblock has no
    /// realm-1 override. A realm-1 `realm_landblock_rule` row or `landblock_instance_realm` row on a battleground landblock would put
    /// content (a door, a portal, a generator) into every match, so any such row in the committed content SQL fails this test.
    ///
    /// <para/>
    /// This is a SOURCE scan of Content/**/*.sql. It carries its own positive control (the scan must see realm-1 rows of both tables
    /// elsewhere in the tree, or it proves nothing) and its parser is pinned against synthetic SQL below.
    /// </summary>
    [TestClass]
    public class BattlegroundRealmGuardTests
    {
        private const string RuleTable = "realm_landblock_rule";
        private const string InstanceTable = "landblock_instance_realm";

        /// <summary>One parsed row: the table, the realm id, the 16-bit landblock it touches and where it was found.</summary>
        internal sealed record RealmRow(string Table, long Realm, uint Landblock, string Where);

        // ---------------- the repo scan ----------------

        [TestMethod]
        public void NoBattlegroundLandblock_HasARealmOneRuleOrInstanceRow()
        {
            var content = ContentDir();
            var files = Directory.EnumerateFiles(content, "*.sql", SearchOption.AllDirectories).ToList();
            var rows = new List<RealmRow>();

            foreach (var file in files)
                rows.AddRange(ParseRows(File.ReadAllText(file), Path.GetRelativePath(content, file)));

            // Positive control: the scan must see realm-1 rows of BOTH tables on other landblocks, or a parser that matches nothing would pass.
            Assert.IsTrue(files.Count > 100, $"scanned only {files.Count} SQL files under {content}");
            Assert.IsTrue(rows.Any(r => r.Table == RuleTable && r.Realm == 1), "control: no realm-1 realm_landblock_rule row was parsed anywhere in Content");
            Assert.IsTrue(rows.Any(r => r.Table == InstanceTable && r.Realm == 1), "control: no realm-1 landblock_instance_realm row was parsed anywhere in Content");

            var offenders = rows.Where(r => r.Realm == 1 && BattlegroundMapCatalog.Landblocks.Contains(r.Landblock)).ToList();

            Assert.AreEqual(0, offenders.Count,
                "a battleground landblock must stay empty in realm 1; found: " + string.Join("; ", offenders.Select(o => $"{o.Table} realm 1 landblock 0x{o.Landblock:X4} in {o.Where}")));
        }

        // ---------------- the parser, pinned ----------------

        [TestMethod]
        public void Parser_FindsARuleRow()
        {
            var rows = ParseRows("INSERT INTO `realm_landblock_rule` (`realm_id`, `landblock`, `strip_statics`, `strip_encounters`)\nVALUES (1, 0x003C, 0, 0);", "t.sql");

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(new RealmRow(RuleTable, 1, 0x003C, "t.sql"), rows[0]);
        }

        [TestMethod]
        public void Parser_FindsInstanceRows_ByTheCellsLandblock_AcrossAMultiRowInsert()
        {
            var sql = "INSERT INTO `landblock_instance_realm` (`realm_id`, `guid`, `weenie_Class_Id`, `obj_Cell_Id`, `origin_X`)\n" +
                      "VALUES (1, 0x70007EDE, 1001088, 0x003C0223, 1.0), /* a comment, with (parens) */\n" +
                      "       (1, 0x70007EDF, 1001088, 0x016C0134, 2.0),\n" +
                      "       (2, 0x70007EE0, 1001088, 0x003C0224, 3.0);";

            var rows = ParseRows(sql, "t.sql");

            CollectionAssert.AreEqual(new long[] { 1, 1, 2 }, rows.Select(r => r.Realm).ToArray());
            CollectionAssert.AreEqual(new uint[] { 0x003C, 0x016C, 0x003C }, rows.Select(r => r.Landblock).ToArray());
        }

        [TestMethod]
        public void Parser_UsesTheColumnListOrder_AndFallsBackToTheTableOrder()
        {
            var reordered = ParseRows("INSERT INTO landblock_instance_realm (obj_Cell_Id, realm_id) VALUES (0x003C0223, 1);", "t.sql");
            Assert.AreEqual(new RealmRow(InstanceTable, 1, 0x003C, "t.sql"), reordered.Single());

            var positional = ParseRows("INSERT INTO `realm_landblock_rule` VALUES (1, 60, 1, 1);", "t.sql");
            Assert.AreEqual(new RealmRow(RuleTable, 1, 60, "t.sql"), positional.Single(), "decimal landblock 60 is 0x003C");
        }

        [TestMethod]
        public void Parser_IgnoresCommentedOutRows_AndOtherTables()
        {
            var sql = "-- INSERT INTO `realm_landblock_rule` (`realm_id`, `landblock`) VALUES (1, 0x003C);\n" +
                      "/* INSERT INTO `landblock_instance_realm` (`realm_id`, `obj_Cell_Id`) VALUES (1, 0x003C0223); */\n" +
                      "INSERT INTO `landblock_instance` (`guid`, `obj_Cell_Id`) VALUES (1, 0x003C0223);\n" +
                      "DELETE FROM `realm_landblock_rule` WHERE `realm_id` = 1 AND `landblock` = 0x003C;";

            Assert.AreEqual(0, ParseRows(sql, "t.sql").Count);
        }

        [TestMethod]
        public void Parser_ASemicolonOrCommentMarkerInsideAString_DoesNotEndTheStatement()
        {
            var sql = "INSERT INTO `landblock_instance_realm` (`realm_id`, `guid`, `note`, `obj_Cell_Id`) VALUES (1, 5, 'a; b -- c /* d', 0x003C0223);";

            Assert.AreEqual(0x003Cu, ParseRows(sql, "t.sql").Single().Landblock);
        }

        // ---------------- discrimination: the guard DOES fail on a battleground landblock ----------------

        [TestMethod]
        public void TheGuardsPredicate_FlagsABattlegroundRow_AndNoneOnAnOrdinaryLandblock()
        {
            var bad = ParseRows("INSERT INTO `realm_landblock_rule` (`realm_id`, `landblock`, `strip_statics`, `strip_encounters`) VALUES (1, 0x003C, 0, 0);", "fake.sql");
            var badKoth = ParseRows("INSERT INTO `landblock_instance_realm` (`realm_id`, `guid`, `weenie_Class_Id`, `obj_Cell_Id`) VALUES (1, 1, 1001088, 0x016C0134);", "fake.sql");
            var fine = ParseRows("INSERT INTO `realm_landblock_rule` (`realm_id`, `landblock`, `strip_statics`, `strip_encounters`) VALUES (1, 0x21B0, 0, 0);", "fake.sql");
            var otherRealm = ParseRows("INSERT INTO `realm_landblock_rule` (`realm_id`, `landblock`, `strip_statics`, `strip_encounters`) VALUES (2, 0x003C, 0, 0);", "fake.sql");

            Assert.IsTrue(Offends(bad.Single()), "a realm-1 rule on 0x003C");
            Assert.IsTrue(Offends(badKoth.Single()), "a realm-1 instance on 0x016C");
            Assert.IsFalse(Offends(fine.Single()), "0x21B0 is not a battleground landblock");
            Assert.IsFalse(Offends(otherRealm.Single()), "realm 2 is not the private copy's realm");
        }

        private static bool Offends(RealmRow r) => r.Realm == 1 && BattlegroundMapCatalog.Landblocks.Contains(r.Landblock);

        // ---------------- the scanner ----------------

        private static string ContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "realms")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/realms by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Content");
        }

        private static readonly Regex InsertStatement = new(
            @"INSERT\s+(?:IGNORE\s+)?INTO\s+`?(?<table>landblock_instance_realm|realm_landblock_rule)`?\s*(?:\((?<cols>[^)]*)\))?\s*VALUES\s*(?<rows>(?:'(?:[^'\\]|\\.|'')*'|[^;'])*)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Every row of every INSERT into the two realm tables in <paramref name="sql"/>, comments removed first.</summary>
        internal static List<RealmRow> ParseRows(string sql, string where)
        {
            var result = new List<RealmRow>();

            foreach (Match m in InsertStatement.Matches(StripComments(sql)))
            {
                var table = m.Groups["table"].Value.ToLowerInvariant();
                var cols = m.Groups["cols"].Success
                    ? m.Groups["cols"].Value.Split(',').Select(c => c.Trim().Trim('`').ToLowerInvariant()).ToList()
                    : (table == RuleTable ? new List<string> { "realm_id", "landblock" } : new List<string> { "realm_id", "guid", "weenie_class_id", "obj_cell_id" });

                var realmAt = cols.IndexOf("realm_id");
                var blockAt = cols.IndexOf(table == RuleTable ? "landblock" : "obj_cell_id");

                if (realmAt < 0 || blockAt < 0)
                    continue;

                foreach (var tuple in Tuples(m.Groups["rows"].Value))
                {
                    if (tuple.Count <= Math.Max(realmAt, blockAt) || !TryNumber(tuple[realmAt], out var realm) || !TryNumber(tuple[blockAt], out var value))
                        continue;

                    var landblock = table == RuleTable ? (uint)value : (uint)(value >> 16);

                    result.Add(new RealmRow(table, realm, landblock, where));
                }
            }

            return result;
        }

        private static bool TryNumber(string token, out long value)
        {
            token = token.Trim();

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(token.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);

            return long.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Removes `-- ...` and `/* ... */` comments, leaving quoted strings untouched.</summary>
        private static string StripComments(string sql)
        {
            var sb = new StringBuilder(sql.Length);
            var i = 0;

            while (i < sql.Length)
            {
                var c = sql[i];

                if (c == '\'')
                {
                    var start = i++;

                    while (i < sql.Length)
                    {
                        if (sql[i] == '\\')
                            i += 2;
                        else if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                            i += 2;
                        else if (sql[i] == '\'')
                        {
                            i++;
                            break;
                        }
                        else
                            i++;
                    }

                    i = Math.Min(sql.Length, i);
                    sb.Append(sql, start, i - start);
                }
                else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    while (i < sql.Length && sql[i] != '\n')
                        i++;
                }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? sql.Length : end + 2;
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            return sb.ToString();
        }

        /// <summary>The parenthesised tuples of a VALUES list, each split on top-level commas outside quoted strings.</summary>
        private static IEnumerable<List<string>> Tuples(string values)
        {
            var depth = 0;
            var inString = false;
            var field = new StringBuilder();
            List<string> row = null;

            for (var i = 0; i < values.Length; i++)
            {
                var c = values[i];

                if (inString)
                {
                    field.Append(c);

                    if (c == '\\' && i + 1 < values.Length)
                        field.Append(values[++i]);
                    else if (c == '\'')
                        inString = false;

                    continue;
                }

                if (c == '\'')
                {
                    inString = true;
                    field.Append(c);
                }
                else if (c == '(')
                {
                    if (depth++ == 0)
                    {
                        row = new List<string>();
                        field.Clear();
                    }
                    else
                        field.Append(c);
                }
                else if (c == ')')
                {
                    if (--depth == 0)
                    {
                        row.Add(field.ToString());
                        yield return row;
                        row = null;
                    }
                    else
                        field.Append(c);
                }
                else if (c == ',' && depth == 1)
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (depth >= 1)
                    field.Append(c);
            }
        }
    }
}
