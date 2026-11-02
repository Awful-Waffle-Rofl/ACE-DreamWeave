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
    /// Content pins for the Olthoi Voidclaw (wcid 1007600): every stat row, the creation-roll spec,
    /// the salvage allow mask, PveOnly, the spellbook, the texture swaps, and the ABSENCE of the rows
    /// that would break it (PaletteTemplate/ClothingBase make the olthoi texture render as noise;
    /// Bonded/Attuned are deliberately not set; ImbuedEffect is Crippling Blow, bit 2). Pure file + regex parsing of the
    /// committed SQL, same idiom as DriftAtlanContentTests.
    /// </summary>
    [TestClass]
    public class VoidClawContentTests
    {
        private const int Wcid = 1007600;

        private static readonly Dictionary<int, double> ExpectedInts = new Dictionary<int, double>
        {
            { 1, 1 }, { 5, 135 }, { 9, 1048576 }, { 16, 1 }, { 18, 4096 }, { 19, 50000 },
            { 44, 53 }, { 45, 1024 }, { 46, 1 }, { 47, 1 }, { 48, 45 }, { 49, 20 }, { 51, 1 },
            { 93, 1044 }, { 105, 10 }, { 106, 350 }, { 107, 2000 }, { 108, 2000 }, { 109, 250 },
            { 151, 2 }, { 158, 2 }, { 159, 45 }, { 160, 430 }, { 179, 2 }, { 263, 1024 }, { 353, 1 },
            { 9095, 3 },
        };

        private static readonly Dictionary<int, double> ExpectedFloats = new Dictionary<int, double>
        {
            { 5, -0.033 }, { 21, 0.52 }, { 22, 0.43 }, { 29, 1.2 }, { 62, 1.2 }, { 157, 0.75 },
        };

        private static readonly Dictionary<int, string> ExpectedDids = new Dictionary<int, string>
        {
            { 1, "0x0200061C" }, { 3, "0x20000014" }, { 6, "0x04000BEF" },
            { 8, "0x060073DA" }, { 22, "0x3400002B" }, { 52, "0x06005B0C" },
            { 9000, "0x33001154" },   // VisualEffectScript: the Paradox-touched Olthoi Katar 35914 black smoke (its Setup 0x02001712 DefaultScript)
        };

        private static readonly int[] ExpectedSpells = { 6089, 6094, 6091, 4395, 4405, 4417, 4400 };

        private const string ExpectedSpec = "v1|f29=1.13:1.27:0.01;f62=1.13:1.27:0.01";

        [TestMethod]
        public void VoidClaw_FileExistsAndHasUnitHeaders()
        {
            var file = GetFile();
            var text = File.ReadAllText(file);

            StringAssert.Contains(text, "-- @unit: weenies/1007600");
            StringAssert.Contains(text, "-- @owner: void-claw");
            StringAssert.Contains(text, "-- @owns: wcid:1007600");
            StringAssert.Contains(text, "'ace1007600-olthoivoidclaw', 6,");
        }

        [TestMethod]
        public void VoidClaw_IntRowsAreExactlyTheSpec()
        {
            var ints = ParseNumeric(GetFile(), "weenie_properties_int");
            AssertExactly(ExpectedInts, ints, "int");
        }

        [TestMethod]
        public void VoidClaw_FloatRowsAreExactlyTheSpec()
        {
            var floats = ParseNumeric(GetFile(), "weenie_properties_float");
            AssertExactly(ExpectedFloats, floats, "float");
        }

        [TestMethod]
        public void VoidClaw_DidRowsAreExactlyTheSpec()
        {
            var dids = ParseHex(GetFile());
            var problems = new List<string>();

            foreach (var kv in ExpectedDids)
                if (!dids.TryGetValue(kv.Key, out var actual) || !string.Equals(actual, kv.Value, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"did {kv.Key}: expected {kv.Value}, found {(dids.TryGetValue(kv.Key, out var a) ? a : "none")}.");

            foreach (var key in dids.Keys.Where(k => !ExpectedDids.ContainsKey(k)))
                problems.Add($"did {key}: unexpected row.");

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void VoidClaw_BoolsArePveOnlyAndInscribableOnly()
        {
            var bools = ParseBools(GetFile());

            Assert.AreEqual(2, bools.Count, "bool row count: " + string.Join(",", bools.Keys));
            Assert.IsTrue(bools.TryGetValue(22, out var insc) && insc, "Inscribable (22) must be True.");
            Assert.IsTrue(bools.TryGetValue(9081, out var pve) && pve, "PveOnly (9081) must be True.");
        }

        [TestMethod]
        public void VoidClaw_ModSalvageAllowMaskIsThree()
        {
            var ints = ParseNumeric(GetFile(), "weenie_properties_int");
            Assert.IsTrue(ints.TryGetValue(9095, out var mask), "int 9095 ModSalvageAllowMask missing.");
            Assert.AreEqual(3, (int)mask, "Tourmaline (1) | Amethyst (2).");
        }

        [TestMethod]
        public void VoidClaw_StringsCarryNameDescAndExactCreationRollSpec()
        {
            var strings = ParseStrings(GetFile());

            Assert.AreEqual("Olthoi Voidclaw", strings[1]);
            StringAssert.StartsWith(strings[16], "A nekode whose blades were ground from the black carapace of an olthoi soldier");
            Assert.AreEqual(ExpectedSpec, strings[9031]);
            Assert.AreEqual(3, strings.Count, "string row count: " + string.Join(",", strings.Keys));
        }

        [TestMethod]
        public void VoidClaw_SpellbookIsExactSetAtProbabilityTwo()
        {
            var rows = new List<(int Spell, double Prob)>();

            foreach (var body in Blocks(GetFile(), "weenie_properties_spell_book"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*([-\d.]+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == Wcid)
                        rows.Add((int.Parse(row.Groups[2].Value), double.Parse(row.Groups[3].Value, CultureInfo.InvariantCulture)));

            CollectionAssert.AreEquivalent(ExpectedSpells, rows.Select(r => r.Spell).ToArray());
            Assert.IsTrue(rows.All(r => Math.Abs(r.Prob - 2) < 1e-9), "every spell_book probability must be 2.");
        }

        [TestMethod]
        public void VoidClaw_TextureMapSwapsOnPartZero()
        {
            var expected = new[]
            {
                (0, 0x050002ABL, 0x05001C91L),
                (0, 0x05000C54L, 0x05000377L),
                (0, 0x05000C55L, 0x05003313L),
            };
            var actual = new List<(int, long, long)>();

            foreach (var body in Blocks(GetFile(), "weenie_properties_texture_map"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == Wcid)
                        actual.Add((int.Parse(row.Groups[2].Value), long.Parse(row.Groups[3].Value), long.Parse(row.Groups[4].Value)));

            CollectionAssert.AreEqual(expected, actual.ToArray());
        }

        [TestMethod]
        public void VoidClaw_CarriesNoRowsThatBreakTheTextureOrBindIt()
        {
            var file = GetFile();
            var ints = ParseNumeric(file, "weenie_properties_int");
            var dids = ParseHex(file);

            foreach (var forbidden in new[] { 3, 33, 114 })
                Assert.IsFalse(ints.ContainsKey(forbidden), $"forbidden PropertyInt {forbidden} present.");

            Assert.IsFalse(dids.ContainsKey(7), "forbidden DID 7 (ClothingBase) present.");
            Assert.AreEqual(0, Blocks(file, "weenie_properties_palette").Count(), "no palette rows expected.");
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

        private static string GetFile()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var weenies = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(weenies))
                {
                    var file = Directory.EnumerateFiles(weenies, Wcid + " *.sql", SearchOption.TopDirectoryOnly)
                        .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                    Assert.IsNotNull(file, $"{Wcid}: no weenie file found in {weenies}.");
                    return file;
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static string StripComments(string s) => Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        private static IEnumerable<string> Blocks(string file, string table)
        {
            var text = File.ReadAllText(file);
            foreach (Match b in Regex.Matches(text, $@"INSERT\s+INTO\s+`{table}`.*?;\s*(\r?\n|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                yield return StripComments(b.Value);
        }

        private static Dictionary<int, double> ParseNumeric(string file, string table)
        {
            var result = new Dictionary<int, double>();

            foreach (var body in Blocks(file, table))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?[\d.]+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == Wcid)
                        result[int.Parse(row.Groups[2].Value)] = double.Parse(row.Groups[3].Value, CultureInfo.InvariantCulture);

            return result;
        }

        private static Dictionary<int, string> ParseHex(string file)
        {
            var result = new Dictionary<int, string>();

            foreach (var body in Blocks(file, "weenie_properties_d_i_d"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(0x[0-9A-Fa-f]+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == Wcid)
                        result[int.Parse(row.Groups[2].Value)] = row.Groups[3].Value;

            return result;
        }

        private static Dictionary<int, bool> ParseBools(string file)
        {
            var result = new Dictionary<int, bool>();

            foreach (var body in Blocks(file, "weenie_properties_bool"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(True|False)\s*\)", RegexOptions.IgnoreCase))
                    if (int.Parse(row.Groups[1].Value) == Wcid)
                        result[int.Parse(row.Groups[2].Value)] = string.Equals(row.Groups[3].Value, "True", StringComparison.OrdinalIgnoreCase);

            return result;
        }

        private static Dictionary<int, string> ParseStrings(string file)
        {
            var result = new Dictionary<int, string>();

            foreach (var body in Blocks(file, "weenie_properties_string"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*'((?:[^']|'')*)'\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == Wcid)
                        result[int.Parse(row.Groups[2].Value)] = row.Groups[3].Value.Replace("''", "'");

            return result;
        }
    }
}
