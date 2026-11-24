using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content guards for the lugian dungeon placements (lugian-dungeons PR E). Reads the generator
    /// weenies, the exit portal and the clusterplan-generated realm-1 unit straight from Content/, so no
    /// database, dat or world is needed. One data row per dungeon (<see cref="Dungeons"/>) over shared
    /// assertions: a divergence between the Citadel and Excavations units fails a test.
    ///
    /// The row counts are the PLACED counts the planner reached, not the requested 55 / 80: the planner
    /// never loosens its drop-clearance, disc-pass or flat-join rules to hit a number, so a short count is
    /// recorded here as the shipped truth and re-derived whenever the unit is regenerated.
    /// </summary>
    [TestClass]
    public class LugianDungeonPlacementContentTests
    {
        private sealed class Dungeon
        {
            public string Name;
            public int ClusterGen, BossGen, Brute, Hurler, Shaman, Boss;
            public string Unit;       // path under Content/, no extension
            public string Block;      // 4 hex digits, e.g. "02F0"
            public long GuidLow, GuidHigh; // GuidLow is the clusterplan --guid-floor (the retail base ends just below it); guids must sit strictly above it
            public int Clusters;      // placed cluster generators (excludes escorts)
            public int Escorts;       // boss escort clusters
            public int ExitWcid;
            public int ExitCount;
        }

        private static readonly Dungeon[] Dungeons =
        {
            new Dungeon
            {
                Name = "Citadel",
                ClusterGen = 1008462, BossGen = 1008463,
                Brute = 1008450, Hurler = 1008451, Shaman = 1008452, Boss = 1008453,
                Unit = "realms/lugian_citadel", Block = "02F0",
                GuidLow = 0x702F006D, GuidHigh = 0x702F0FFF,
                Clusters = 48, Escorts = 2,
                ExitWcid = 1008468, ExitCount = 1,
            },
            new Dungeon
            {
                Name = "Excavations",
                ClusterGen = 1008464, BossGen = 1008465,
                Brute = 1008454, Hurler = 1008455, Shaman = 1008456, Boss = 1008457,
                Unit = "realms/lugian_excavations", Block = "02E7",
                GuidLow = 0x702E7057, GuidHigh = 0x702E7FFF,
                Clusters = 80, Escorts = 2,
                ExitWcid = 1008469, ExitCount = 2,
            },
        };

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Content/sql/weenies by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string WeenieText(int wcid)
        {
            var dir = Path.Combine(RepoRoot(), "Content", "sql", "weenies");
            var files = Directory.GetFiles(dir, wcid + " *.sql");
            Assert.AreEqual(1, files.Length, $"expected exactly one weenie file for wcid {wcid} in {dir}");
            return File.ReadAllText(files[0]);
        }

        private static string UnitText(Dungeon d) => File.ReadAllText(Path.Combine(RepoRoot(), "Content", d.Unit + ".sql"));

        /// <summary>The text of one INSERT block (from its table name to its terminating semicolon).</summary>
        private static string Block(string sql, string table)
        {
            var m = Regex.Match(sql, "INSERT INTO `" + table + "`.*?;\\s*(?:\\r?\\n|$)", RegexOptions.Singleline);
            return m.Success ? m.Value : "";
        }

        /// <summary>(type, value) pairs of a weenie_properties_{int,float,bool,...} block.</summary>
        private static Dictionary<int, string> Props(string sql, string table)
        {
            var map = new Dictionary<int, string>();
            foreach (Match m in Regex.Matches(Block(sql, table), @"\(\d+,\s*(\d+),\s*([^\s)]+(?: [^)\s]+)?)\s*\)"))
                map[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.Trim();
            return map;
        }

        private sealed class Profile
        {
            public int Probability, Wcid, Delay, Init, Max, When, Where, Stack;
        }

        private static List<Profile> Profiles(string sql)
        {
            var rows = new List<Profile>();
            foreach (Match m in Regex.Matches(Block(sql, "weenie_properties_generator"),
                @"\(\d+,\s*(-?\d+),\s*(\d+),\s*(\d+),\s*(\d+),\s*(\d+),\s*(\d+),\s*(\d+),\s*(-?\d+),"))
            {
                rows.Add(new Profile
                {
                    Probability = int.Parse(m.Groups[1].Value),
                    Wcid = int.Parse(m.Groups[2].Value),
                    Delay = int.Parse(m.Groups[3].Value),
                    Init = int.Parse(m.Groups[4].Value),
                    Max = int.Parse(m.Groups[5].Value),
                    When = int.Parse(m.Groups[6].Value),
                    Where = int.Parse(m.Groups[7].Value),
                    Stack = int.Parse(m.Groups[8].Value),
                });
            }
            return rows;
        }

        private sealed class Row
        {
            public int Realm, Wcid;
            public long Guid, Cell;
            public double X, Y;
        }

        private static List<Row> UnitRows(string sql)
        {
            var rows = new List<Row>();
            foreach (Match m in Regex.Matches(sql,
                @"^\s*(?:VALUES\s*)?\((\d+),\s*0x([0-9A-Fa-f]{8}),\s*(\d+),\s*0x([0-9A-Fa-f]{8}),\s*(-?[\d.]+),\s*(-?[\d.]+),", RegexOptions.Multiline))
            {
                rows.Add(new Row
                {
                    Realm = int.Parse(m.Groups[1].Value),
                    Guid = long.Parse(m.Groups[2].Value, NumberStyles.HexNumber),
                    Wcid = int.Parse(m.Groups[3].Value),
                    Cell = long.Parse(m.Groups[4].Value, NumberStyles.HexNumber),
                    X = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                    Y = double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture),
                });
            }
            return rows;
        }

        [TestMethod]
        public void ClusterGenerator_IsTheNoBackfill421Shape()
        {
            foreach (var d in Dungeons)
            {
                var sql = WeenieText(d.ClusterGen);
                var ints = Props(sql, "weenie_properties_int");
                var floats = Props(sql, "weenie_properties_float");

                Assert.AreEqual("6", ints.GetValueOrDefault(81), d.Name + ": MaxGeneratedObjects (81) must be 6");
                Assert.AreEqual("6", ints.GetValueOrDefault(82), d.Name + ": InitGeneratedObjects (82) must be 6");
                Assert.IsFalse(ints.ContainsKey(142), d.Name + ": GeneratorTimeType (142) must be absent");
                Assert.AreEqual("60", floats.GetValueOrDefault(41), d.Name + ": RegenerationInterval (41)");
                Assert.AreEqual("30", floats.GetValueOrDefault(121), d.Name + ": GeneratorInitialDelay (121)");
                Assert.AreEqual("3", floats.GetValueOrDefault(43), d.Name + ": GeneratorRadius (43)");
                Assert.IsFalse(Props(sql, "weenie_properties_string").ContainsKey(34), d.Name + ": GeneratorEvent (34) must be absent");
                Assert.IsTrue(Props(sql, "weenie_properties_d_i_d").ContainsKey(1), d.Name + ": a generator with no Setup never enters the world");

                var profiles = Profiles(sql);
                Assert.IsFalse(profiles.Any(p => p.Wcid == d.Shaman),
                    d.Name + ": the Chanter (" + d.Shaman + ") was removed from the cluster by owner ruling 2026-11-23 and must not be profiled");
                CollectionAssert.AreEqual(
                    new[] { d.Brute, d.Hurler },
                    profiles.Select(p => p.Wcid).ToArray(), d.Name + ": profile order Brute, Hurler");
                CollectionAssert.AreEqual(new[] { 4, 2 }, profiles.Select(p => p.Init).ToArray(), d.Name + ": initCreate 4/2");
                CollectionAssert.AreEqual(new[] { 4, 2 }, profiles.Select(p => p.Max).ToArray(), d.Name + ": maxCreate 4/2");

                foreach (var p in profiles)
                {
                    Assert.AreEqual(-1, p.Probability, d.Name + ": every profile probability -1 (all spawn)");
                    Assert.AreEqual(300, p.Delay, d.Name + ": delay 300");
                    Assert.AreEqual(1, p.When, d.Name + ": whenCreate 1 (Destruction)");
                    Assert.AreEqual(2, p.Where, d.Name + ": whereCreate 2 (Scatter)");
                }

                Assert.AreEqual(int.Parse(ints[81]), profiles.Sum(p => p.Init), d.Name + ": profile counts sum to MaxGeneratedObjects");
            }
        }

        [TestMethod]
        public void BossGenerator_IsOneSlotFifteenMinutes()
        {
            foreach (var d in Dungeons)
            {
                var sql = WeenieText(d.BossGen);
                var ints = Props(sql, "weenie_properties_int");
                var floats = Props(sql, "weenie_properties_float");

                Assert.AreEqual("1", ints.GetValueOrDefault(81), d.Name + ": boss MaxGeneratedObjects (81)");
                Assert.IsFalse(ints.ContainsKey(142), d.Name + ": GeneratorTimeType (142) must be absent");
                Assert.AreEqual("60", floats.GetValueOrDefault(41), d.Name + ": boss RegenerationInterval (41)");
                Assert.AreEqual("30", floats.GetValueOrDefault(121), d.Name + ": boss GeneratorInitialDelay (121)");
                Assert.IsTrue(Props(sql, "weenie_properties_d_i_d").ContainsKey(1), d.Name + ": boss generator needs a Setup");

                var profiles = Profiles(sql);
                Assert.AreEqual(1, profiles.Count, d.Name + ": one boss profile");
                var p = profiles[0];
                Assert.AreEqual(d.Boss, p.Wcid);
                Assert.AreEqual(-1, p.Probability);
                Assert.AreEqual(1, p.Max);
                Assert.AreEqual(1, p.Init);
                Assert.AreEqual(900, p.Delay, d.Name + ": 15 minute respawn");
                Assert.AreEqual(1, p.When);
                Assert.AreEqual(2, p.Where);
            }
        }

        [TestMethod]
        public void Unit_IsRealmOneBlockScopedWithGuidsInTheReservation()
        {
            foreach (var d in Dungeons)
            {
                var sql = UnitText(d);

                StringAssert.Contains(sql, "-- @owns: block:1:0x" + d.Block, d.Name + ": unit owns the realm-1 block");
                StringAssert.Contains(sql, "DELETE FROM `landblock_instance_link_realm` WHERE `realm_id` = 1", d.Name + ": link wipe first");
                StringAssert.Contains(sql, "DELETE FROM `landblock_instance_realm` WHERE `realm_id` = 1 AND (`obj_Cell_Id` >> 16) = 0x" + d.Block, d.Name + ": instance wipe");
                Assert.IsTrue(sql.IndexOf("DELETE FROM `landblock_instance_link_realm`", StringComparison.Ordinal)
                    < sql.IndexOf("DELETE FROM `landblock_instance_realm`", StringComparison.Ordinal), d.Name + ": link rows wiped before instance rows");
                Assert.IsFalse(Regex.IsMatch(sql, "INSERT INTO `landblock_instance`\\s"), d.Name + ": never writes realm-0 instance rows");

                var rows = UnitRows(sql);
                Assert.IsTrue(rows.Count > 0, d.Name + ": parsed no rows");

                foreach (var r in rows)
                {
                    Assert.AreEqual(1, r.Realm, d.Name + ": realm 1 only (guid " + r.Guid.ToString("X8") + ")");
                    Assert.IsTrue(r.Guid > d.GuidLow && r.Guid <= d.GuidHigh, d.Name + ": guid " + r.Guid.ToString("X8") + " outside (floor, top]: the floor is where the retail base ends");
                    Assert.AreEqual(Convert.ToInt64(d.Block, 16), r.Cell >> 16, d.Name + ": cell " + r.Cell.ToString("X8") + " outside the block");
                }

                Assert.AreEqual(rows.Count, rows.Select(r => r.Guid).Distinct().Count(), d.Name + ": guids unique");
                Assert.AreEqual(rows.Max(r => r.Guid), rows[0].Guid, d.Name + ": allocated top-down, first row is the highest");
            }
        }

        [TestMethod]
        public void Unit_PlacesTheRightCounts()
        {
            foreach (var d in Dungeons)
            {
                var rows = UnitRows(UnitText(d));

                Assert.AreEqual(d.Clusters + d.Escorts, rows.Count(r => r.Wcid == d.ClusterGen), d.Name + ": cluster rows (clusters + escorts)");
                Assert.AreEqual(1, rows.Count(r => r.Wcid == d.BossGen), d.Name + ": boss rows");
                Assert.AreEqual(d.ExitCount, rows.Count(r => r.Wcid == d.ExitWcid), d.Name + ": exit rows");
                Assert.AreEqual(d.Clusters + d.Escorts + 1 + d.ExitCount, rows.Count, d.Name + ": no other rows in the unit");
            }
        }

        [TestMethod]
        public void Unit_PutsExactlyTheEscortsNextToTheBoss()
        {
            foreach (var d in Dungeons)
            {
                var rows = UnitRows(UnitText(d));
                var boss = rows.Single(r => r.Wcid == d.BossGen);
                var near = rows.Where(r => r.Wcid == d.ClusterGen && (r.Cell >> 16) == (boss.Cell >> 16)
                    && Math.Sqrt((r.X - boss.X) * (r.X - boss.X) + (r.Y - boss.Y) * (r.Y - boss.Y)) <= 10.0).ToList();
                Assert.AreEqual(d.Escorts, near.Count, d.Name + ": cluster rows within 10 m of the boss row are exactly the escorts");
            }
        }

        [TestMethod]
        public void ExitPortal_ReturnsToTheDriftInRealmOne()
        {
            foreach (var d in Dungeons)
            {
                var sql = WeenieText(d.ExitWcid);
                var ints = Props(sql, "weenie_properties_int");

                Assert.IsTrue(Regex.IsMatch(sql, @"VALUES \(\d+, 'ace[^']*', 7,"), d.Name + ": exit is a Portal weenie (type 7)");
                Assert.AreEqual("1", ints.GetValueOrDefault(9022), d.Name + ": PortalRealm (9022) 1 resolves the destination into realm 1");
                Assert.IsFalse(ints.ContainsKey(86), d.Name + ": exit has no MinLevel gate");
                Assert.IsFalse(ints.ContainsKey(87), d.Name + ": exit has no MaxLevel gate");

                var m = Regex.Match(Block(sql, "weenie_properties_position"), @"\(\d+,\s*2,\s*0x([0-9A-Fa-f]{8}),");
                Assert.IsTrue(m.Success, d.Name + ": exit has a Destination (position type 2)");
                var cell = long.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
                Assert.AreEqual(0x0007L, cell >> 16, d.Name + ": destination is on the Drift landblock 0x0007");
            }
        }
    }
}
