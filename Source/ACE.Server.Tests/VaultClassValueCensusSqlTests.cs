using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Database/Optional/Shard/vault-class-value-census.sql: the per-account value census the owner
    /// runs on stage before and after the fold.
    ///
    /// WHY THIS TEST EXISTS AT ALL, when the SQL is never executed by the server. The census has two
    /// failure modes and they are not equally loud. A wrong TABLE name fails immediately and visibly
    /// the first time it runs, so it costs a round trip and nothing else. A wrong PROPERTY ID does not
    /// fail at all - it silently sums the wrong column and produces a number that looks like a census
    /// and is not one, and the whole point of the exercise is that this number is trusted. So the two
    /// magic integers in that file are pinned here against the enums they came from, in the same way
    /// any numeric id written into doctrine has to be.
    ///
    /// It also pins the READ-ONLY contract. The file is handed to an operator to run against a shard
    /// that is a copy of prod; a write statement appearing in it later, for any reason, must break the
    /// build rather than be discovered on that shard.
    /// </summary>
    [TestClass]
    public class VaultClassValueCensusSqlTests
    {
        private static string CensusSql()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Database", "Optional", "Shard", "vault-class-value-census.sql")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find vault-class-value-census.sql by walking up from {AppContext.BaseDirectory}");

            return File.ReadAllText(Path.Combine(dir.FullName, "Database", "Optional", "Shard", "vault-class-value-census.sql")).Replace("\r\n", "\n");
        }

        /// <summary>The file with every -- comment line stripped, which is the SQL that actually runs.</summary>
        private static string CensusStatements()
        {
            return string.Join("\n", CensusSql().Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        }

        /// <summary>
        /// The silent one. `cont.type = 2` must be PropertyInstanceId.Container and `bpi.type = 19`
        /// must be PropertyInt.Value; either being wrong yields a plausible-looking total that is a sum
        /// over the wrong property, and nothing anywhere would say so.
        /// </summary>
        [TestMethod]
        public void TheTwoPropertyIds_MatchTheEnumsTheyCameFrom()
        {
            Assert.AreEqual(2, (int)PropertyInstanceId.Container,
                "the census joins stored items to their vault on InstanceId 2; if Container is not 2 the file is wrong");

            Assert.AreEqual(19, (int)PropertyInt.Value,
                "the census sums Int 19; if Value is not 19 the census silently totals a different property");

            var sql = CensusStatements();

            StringAssert.Contains(sql, "cont.`type`  = 2", "the container join must use PropertyInstanceId.Container");
            StringAssert.Contains(sql, "bpi.`type`      = 19", "the value sum must use PropertyInt.Value");
        }

        /// <summary>
        /// Every table and column the census names, against the scaffolded model. The int-property
        /// table in this schema is biota_properties_i_i_d for InstanceIds and biota_properties_int for
        /// ints, which is easy to get wrong in either direction.
        /// </summary>
        [TestMethod]
        public void EveryTableItNames_ExistsInTheScaffoldedModel()
        {
            var sql = CensusStatements();

            foreach (var table in new[] { "account_vault", "account_vault_class", "biota_properties_i_i_d", "biota_properties_int" })
                StringAssert.Contains(sql, $"`{table}`", $"the census must name {table}");

            // The spelling that does NOT exist. A census written against it would fail on stage rather
            // than here, which is a wasted round trip on a box that has to be quiesced to use at all.
            Assert.IsFalse(sql.Contains("biota_properties_iid"),
                "biota_properties_iid does not exist in this schema; the InstanceId table is biota_properties_i_i_d");

            var context = File.ReadAllText(ShardDbContextPath());

            foreach (var table in new[] { "biota_properties_i_i_d", "biota_properties_int" })
                StringAssert.Contains(context, $"ToTable(\"{table}\"",
                    $"{table} is not mapped by ShardDbContext, so the census is reading a table that does not exist");
        }

        private static string ShardDbContextPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database", "Models", "Shard", "ShardDbContext.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ShardDbContext.cs by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, "Source", "ACE.Database", "Models", "Shard", "ShardDbContext.cs");
        }

        /// <summary>
        /// READ ONLY, enforced rather than promised. This file is run by hand against a copy of prod.
        /// </summary>
        [TestMethod]
        public void ItContainsNothingButSelects()
        {
            var sql = CensusStatements();

            foreach (var forbidden in new[] { "INSERT", "UPDATE", "DELETE", "DROP", "CREATE", "ALTER", "TRUNCATE", "REPLACE", "GRANT", "SET " })
                Assert.IsFalse(Regex.IsMatch(sql, $@"\b{forbidden.Trim()}\b", RegexOptions.IgnoreCase),
                    $"the census must be read only, and it contains {forbidden.Trim()}");

            // Control: it really does contain the SELECT it is supposed to, so the sweep above is not
            // passing over an empty string.
            StringAssert.Contains(sql, "SELECT");
            Assert.AreEqual(1, Regex.Matches(sql, @";").Count, "the census is exactly one statement");
        }

        /// <summary>
        /// The barrel exclusion, on BOTH halves of the census by being on the only half that reads
        /// account_vault at all, plus the LEFT JOIN that keeps a valueless item counted.
        ///
        /// The LEFT JOIN is the one that would be silently wrong: an INNER join to
        /// biota_properties_int drops every biota with no Value row from stored_Biotas as well as from
        /// stored_Value, so an item losing its Value would look like an item disappearing.
        /// </summary>
        [TestMethod]
        public void ItScopesToOrdinaryVaults_AndKeepsValuelessItemsInTheCount()
        {
            var sql = CensusStatements();

            StringAssert.Contains(sql, "WHERE av.`kind` = 0",
                "the census must exclude the barrel, which the fold cannot touch and the reaper purges on its own schedule");

            StringAssert.Contains(sql, "LEFT JOIN `biota_properties_int`",
                "an INNER join here would drop valueless biotas from the count as well as from the sum");

            Assert.IsFalse(sql.Contains("account_vault_stack"),
                "the pristine ledger carries no stored Value and the fold does not touch it, so it belongs on neither side");
        }

        /// <summary>
        /// One row PER ACCOUNT, and the invariant column is the SUM of the two halves. A fleet total
        /// hides value leaking off one account and being created on another.
        /// </summary>
        [TestMethod]
        public void ItReportsPerAccount_WithTheInvariantAsItsOwnColumn()
        {
            var sql = CensusStatements();

            StringAssert.Contains(sql, "GROUP BY t.`account_Id`");
            StringAssert.Contains(sql, "ORDER BY t.`account_Id`");
            StringAssert.Contains(sql, "SUM(t.`stored_Value`) + SUM(t.`class_Value`)        AS total_Value");
        }

        /// <summary>
        /// The header has to be self-contained, because the person running it will not have this
        /// conversation. Three things it must say in as many words: the two commands, what a pass and
        /// a fail look like, and that the answer is only exact on a quiesced shard.
        /// </summary>
        [TestMethod]
        public void TheHeaderIsSelfContained()
        {
            var header = CensusSql();

            StringAssert.Contains(header, "census-pre.tsv");
            StringAssert.Contains(header, "census-post.tsv");
            StringAssert.Contains(header, "PASS  =");
            StringAssert.Contains(header, "FAIL  =");
            StringAssert.Contains(header, "QUIESCED SHARD, WITH NOBODY CONNECTED");
        }
    }
}
