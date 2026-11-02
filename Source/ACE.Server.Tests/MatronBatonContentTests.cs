using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pins for the two Drift Matron rare batons (Hive Stinger Baton 1007603, Hive Nether Baton
    /// 1007604). The two are meant to be identical except for DamageType, WieldSkillType, the extra
    /// mastery spell and the look/description, so the shared rows are pinned for both and the
    /// differing rows per baton. Also pins the ABSENCE of Bonded/Attuned (tradeable), the PvE-only and
    /// self-ivory style bools, and the drop-time stamp rows (9020/9093/9029) that code writes later.
    /// Pure file + regex parsing of the committed SQL, same idiom as VoidClawContentTests.
    /// </summary>
    [TestClass]
    public class MatronBatonContentTests
    {
        private const int Stinger = 1007603;
        private const int Nether = 1007604;
        private const int Voidclaw = 1007600;

        private const string ExpectedSpec = "v1|f29=1.15:1.25:0.01";

        // Rows identical on both batons (everything except 3 PaletteTemplate, 18 UiEffects, 45 DamageType, 159 WieldSkillType).
        // 179 ImbuedEffect = 2 is Crippling Blow; the shard migration 2026-11-01-00 back-fills existing batons.
        private static readonly Dictionary<int, double> SharedInts = new Dictionary<int, double>
        {
            { 1, 32768 }, { 5, 150 }, { 8, 10 }, { 9, 16777216 }, { 16, 1 }, { 19, 8000 },
            { 46, 512 }, { 93, 1044 }, { 94, 16 }, { 105, 10 }, { 106, 350 }, { 107, 2000 },
            { 108, 2000 }, { 109, 250 }, { 151, 2 }, { 158, 2 }, { 160, 355 }, { 179, 2 }, { 9095, 3 },
        };

        private static readonly Dictionary<int, double> SharedFloats = new Dictionary<int, double>
        {
            { 5, -0.025 }, { 39, 1.5 }, { 144, 0.08 }, { 152, 1.29 },
        };

        private static readonly int[] SharedSpells = { 6098, 6091, 6087, 4305, 4329 };

        private static readonly (int Wcid, string ClassName, string Name, int DamageType, int WieldSkill, int ExtraSpell, int Palette, int UiEffects, string Setup, string Icon)[] Batons =
        {
            (Stinger, "ace1007603-hivestingerbaton", "Hive Stinger Baton", 2, 34, 4638, 4, 2048, "0x02001492", "0x06006090"),
            (Nether, "ace1007604-hivenetherbaton", "Hive Nether Baton", 1024, 43, 5418, 39, 4096, "0x02001A30", "0x0600608D"),
        };

        [TestMethod]
        public void Batons_FilesExistWithUnitHeadersAndClassNames()
        {
            foreach (var b in Batons)
            {
                var text = File.ReadAllText(GetFile(b.Wcid));

                StringAssert.Contains(text, $"-- @unit: weenies/{b.Wcid}");
                StringAssert.Contains(text, "-- @owner: void-claw");
                StringAssert.Contains(text, $"-- @owns: wcid:{b.Wcid}");
                StringAssert.Contains(text, $"'{b.ClassName}', 35,");
            }
        }

        [TestMethod]
        public void Batons_IntRowsAreSharedPlusPerBatonRows()
        {
            foreach (var b in Batons)
            {
                var expected = new Dictionary<int, double>(SharedInts)
                {
                    { 3, b.Palette }, { 18, b.UiEffects }, { 45, b.DamageType }, { 159, b.WieldSkill },
                };

                AssertExactly(expected, ParseNumeric(b.Wcid, "weenie_properties_int"), $"{b.Wcid} int");
            }
        }

        [TestMethod]
        public void Batons_SalvageMaskIsThreeAndWorkmanshipIsTen()
        {
            foreach (var b in Batons)
            {
                var ints = ParseNumeric(b.Wcid, "weenie_properties_int");

                Assert.AreEqual(3, (int)ints[9095], $"{b.Wcid}: ModSalvageAllowMask (Tourmaline | Amethyst).");
                Assert.AreEqual(10, (int)ints[105], $"{b.Wcid}: ItemWorkmanship (Tourmaline refuses casters without it).");
            }
        }

        [TestMethod]
        public void Batons_FloatRowsAreExactAndCarryNoFixedWeaponDefense()
        {
            foreach (var b in Batons)
            {
                var floats = ParseNumeric(b.Wcid, "weenie_properties_float");

                AssertExactly(SharedFloats, floats, $"{b.Wcid} float");
                Assert.AreEqual(1.29, floats[152], 1e-9, $"{b.Wcid}: ElementalDamageMod.");
                Assert.AreEqual(1.5, floats[39], 1e-9, $"{b.Wcid}: DefaultScale (matches the retail baton).");
                Assert.IsFalse(floats.ContainsKey(29), $"{b.Wcid}: WeaponDefense (29) is rolled via 9031, not fixed.");
                Assert.IsFalse(floats.ContainsKey(9020), $"{b.Wcid}: PropertyFloat 9020 is stamped at drop time.");
            }
        }

        [TestMethod]
        public void Batons_CarryNoBindingOrDropStampRows()
        {
            foreach (var b in Batons)
            {
                var ints = ParseNumeric(b.Wcid, "weenie_properties_int");
                var bools = ParseBools(b.Wcid);
                var strings = ParseStrings(b.Wcid);

                foreach (var forbidden in new[] { 33, 114, 9093 })
                    Assert.IsFalse(ints.ContainsKey(forbidden), $"{b.Wcid}: forbidden PropertyInt {forbidden} present.");

                foreach (var forbidden in new[] { 9081, 9080, 9068 })
                    Assert.IsFalse(bools.ContainsKey(forbidden), $"{b.Wcid}: forbidden PropertyBool {forbidden} present.");

                Assert.IsFalse(strings.ContainsKey(9029), $"{b.Wcid}: PropertyString 9029 is stamped at drop time.");
            }
        }

        [TestMethod]
        public void Batons_BoolsAreInscribableOnly()
        {
            foreach (var b in Batons)
            {
                var bools = ParseBools(b.Wcid);

                Assert.AreEqual(1, bools.Count, $"{b.Wcid} bool rows: " + string.Join(",", bools.Keys));
                Assert.IsTrue(bools.TryGetValue(22, out var insc) && insc, $"{b.Wcid}: Inscribable (22) must be True.");
            }
        }

        [TestMethod]
        public void Batons_StringsCarryNameDescAndExactCreationRollSpec()
        {
            foreach (var b in Batons)
            {
                var strings = ParseStrings(b.Wcid);

                Assert.AreEqual(b.Name, strings[1]);
                Assert.IsTrue(strings[16].EndsWith("Spells loosed through it strike with a will that is hard to refuse."), $"{b.Wcid}: LongDesc tail.");
                Assert.AreEqual(ExpectedSpec, strings[9031], $"{b.Wcid}: CreationRollSpec.");
                Assert.AreEqual(3, strings.Count, $"{b.Wcid} string rows: " + string.Join(",", strings.Keys));
            }
        }

        [TestMethod]
        public void Batons_CreationRollSpecParsesToMeleeDefenseGrid()
        {
            foreach (var b in Batons)
            {
                var spec = ParseStrings(b.Wcid)[9031];
                var entries = CreationRoll.Parse(spec);

                Assert.IsNotNull(entries, $"{b.Wcid}: CreationRollSpec failed to parse.");
                Assert.AreEqual(1, entries.Count, $"{b.Wcid}: entry count.");
                Assert.IsTrue(entries[0].IsFloat, $"{b.Wcid}: entry must be a PropertyFloat.");
                Assert.AreEqual(29, entries[0].Id, $"{b.Wcid}: WeaponDefense id.");
                Assert.AreEqual(11, entries[0].GridCount, $"{b.Wcid}: grid points.");
                Assert.AreEqual(1.15m, entries[0].Min, $"{b.Wcid}: min.");
                Assert.AreEqual(1.25m, entries[0].Max, $"{b.Wcid}: max.");
            }
        }
        [TestMethod]
        public void Batons_DidRowsAreRetailLookPlusRareUnderlay()
        {
            foreach (var b in Batons)
            {
                var expected = new Dictionary<int, string>
                {
                    { 1, b.Setup }, { 3, "0x20000014" }, { 6, "0x04001E9C" }, { 7, "0x1000062A" },
                    { 8, b.Icon }, { 22, "0x3400002B" }, { 52, "0x06005B0C" },
                };
                var dids = ParseHex(b.Wcid);
                var problems = new List<string>();

                foreach (var kv in expected)
                    if (!dids.TryGetValue(kv.Key, out var actual) || !string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{b.Wcid} did {kv.Key}: expected {kv.Value}, found {(dids.TryGetValue(kv.Key, out var a) ? a : "none")}.");

                foreach (var key in dids.Keys.Where(k => !expected.ContainsKey(k)))
                    problems.Add($"{b.Wcid} did {key}: unexpected row (retail DID 27/46 must be dropped).");

                Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            }
        }

        [TestMethod]
        public void Batons_SpellbookIsSharedSetPlusOneMasterySelfAtProbabilityTwo()
        {
            foreach (var b in Batons)
            {
                var rows = new List<(int Spell, double Prob)>();

                foreach (var body in Blocks(b.Wcid, "weenie_properties_spell_book"))
                    foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*([-\d.]+)\s*\)"))
                        if (int.Parse(row.Groups[1].Value) == b.Wcid)
                            rows.Add((int.Parse(row.Groups[2].Value), double.Parse(row.Groups[3].Value, CultureInfo.InvariantCulture)));

                var expected = SharedSpells.Concat(new[] { b.ExtraSpell }).ToArray();

                CollectionAssert.AreEquivalent(expected, rows.Select(r => r.Spell).ToArray(), $"{b.Wcid} spellbook.");
                Assert.IsTrue(rows.All(r => Math.Abs(r.Prob - 2) < 1e-9), $"{b.Wcid}: every spell_book probability must be 2.");
            }
        }

        [TestMethod]
        public void Batons_DamageTypeAndWieldSkillDifferPerBaton()
        {
            var stinger = ParseNumeric(Stinger, "weenie_properties_int");
            var nether = ParseNumeric(Nether, "weenie_properties_int");

            Assert.AreEqual(2, (int)stinger[45], "Stinger DamageType Pierce.");
            Assert.AreEqual(34, (int)stinger[159], "Stinger WieldSkillType War Magic.");
            Assert.AreEqual(1024, (int)nether[45], "Nether DamageType Nether.");
            Assert.AreEqual(43, (int)nether[159], "Nether WieldSkillType Void Magic.");
        }

        [TestMethod]
        public void CripplingBlowMigration_MatchesTheThreeWeenieTemplates()
        {
            const string name = "2026-11-01-00-Matron-Rewards-Crippling-Blow-Existing-Items.sql";
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string sql = null;

            while (dir != null && sql == null)
            {
                var candidate = Path.Combine(dir.FullName, "Database", "Updates", "Shard", name);
                if (File.Exists(candidate)) sql = File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            Assert.IsNotNull(sql, "the Crippling Blow shard migration was not found by walking up from the test assembly");

            // a stray `*/` inside the header would close it early and leave prose as SQL, which the comment-stripped
            // checks below could otherwise hide
            Assert.AreEqual(1, Regex.Matches(sql, @"\*/").Count, "the migration header comment must close exactly once");

            var body = StripComments(sql).Trim();

            StringAssert.StartsWith(body, "INSERT INTO `biota_properties_int`", "the migration is a single INSERT statement");
            Assert.AreEqual(1, body.Count(c => c == ';'), "exactly one statement");
            Assert.IsTrue(body.EndsWith(";"), "the statement ends the file");

            // the wcid list is exactly the Voidclaw and the two batons
            var inList = Regex.Match(body, @"`weenie_Class_Id`\s+IN\s*\(([^)]*)\)");
            Assert.IsTrue(inList.Success, "the migration scopes by weenie_Class_Id IN (...)");
            var wcids = inList.Groups[1].Value.Split(',').Select(s => int.Parse(s.Trim())).OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(new[] { Voidclaw, Stinger, Nether }, wcids, "migration wcid list");

            // it writes PropertyInt 179 and ORs bit 2 into an existing row (an upsert, so other imbue bits survive)
            StringAssert.Contains(body, "INSERT INTO `biota_properties_int`");
            StringAssert.Matches(body, new Regex(@"SELECT\s+b\.`id`,\s*179,\s*2\s+FROM\s+`biota`"));
            StringAssert.Matches(body, new Regex(@"ON DUPLICATE KEY UPDATE\s+`value`\s*=\s*`value`\s*\|\s*2\s*;"));

            // and every wcid it touches has the same imbue in its weenie template, so new and existing items agree
            foreach (var wcid in wcids)
            {
                if (!ParseNumeric(wcid, "weenie_properties_int").TryGetValue(179, out var imbue))
                    Assert.Fail($"{wcid}: no PropertyInt 179 row in template");

                Assert.AreEqual(2, (int)imbue, $"{wcid}: template ImbuedEffect must be Crippling Blow (2).");
            }
        }

        // ---------------------------------------------------------------- helpers
        private static void AssertExactly(Dictionary<int, double> expected, Dictionary<int, double> actual, string label)
        {
            var problems = new List<string>();

            foreach (var kv in expected)
            {
                if (!actual.TryGetValue(kv.Key, out var v)) problems.Add($"{label} {kv.Key}: missing.");
                else if (Math.Abs(v - kv.Value) > 1e-9) problems.Add($"{label} {kv.Key}: is {v}, expected {kv.Value}.");
            }

            foreach (var key in actual.Keys.Where(k => !expected.ContainsKey(k)))
                problems.Add($"{label} {key}: unexpected row.");

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        private static string GetFile(int wcid)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(weenies))
                {
                    var file = Directory.EnumerateFiles(weenies, wcid + " *.sql", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                    Assert.IsNotNull(file, $"{wcid}: no weenie file found in {weenies}.");
                    return file;
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static string StripComments(string s) => Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        private static IEnumerable<string> Blocks(int wcid, string table)
        {
            var text = File.ReadAllText(GetFile(wcid));
            foreach (Match b in Regex.Matches(text, $@"INSERT\s+INTO\s+`{table}`.*?;\s*(\r?\n|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                yield return StripComments(b.Value);
        }

        private static Dictionary<int, double> ParseNumeric(int wcid, string table)
        {
            var result = new Dictionary<int, double>();

            foreach (var body in Blocks(wcid, table))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?[\d.]+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result[int.Parse(row.Groups[2].Value)] = double.Parse(row.Groups[3].Value, CultureInfo.InvariantCulture);

            return result;
        }

        private static Dictionary<int, string> ParseHex(int wcid)
        {
            var result = new Dictionary<int, string>();

            foreach (var body in Blocks(wcid, "weenie_properties_d_i_d"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(0x[0-9A-Fa-f]+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result[int.Parse(row.Groups[2].Value)] = row.Groups[3].Value;

            return result;
        }

        private static Dictionary<int, bool> ParseBools(int wcid)
        {
            var result = new Dictionary<int, bool>();

            foreach (var body in Blocks(wcid, "weenie_properties_bool"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(True|False)\s*\)", RegexOptions.IgnoreCase))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result[int.Parse(row.Groups[2].Value)] = string.Equals(row.Groups[3].Value, "True", StringComparison.OrdinalIgnoreCase);

            return result;
        }

        private static Dictionary<int, string> ParseStrings(int wcid)
        {
            var result = new Dictionary<int, string>();

            foreach (var body in Blocks(wcid, "weenie_properties_string"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*'((?:[^']|'')*)'\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result[int.Parse(row.Groups[2].Value)] = row.Groups[3].Value.Replace("''", "'");

            return result;
        }
    }
}
