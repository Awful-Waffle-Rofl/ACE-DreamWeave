using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pins for the ML tinker lock arc (WaffleACE, 2026-09-27): only the 8 Bluespire quest
    /// weapons carry PropertyBool.TinkerLocked (9068) - their cook_book allow-list is exactly the 6
    /// tinker stones (4 Ward-Stones + 2 Thirst-Stones). The 3 Siraluun crests do NOT carry the lock
    /// (owner ruling reversed an earlier draft, 2026-09-27) and tinker normally, so they carry
    /// ItemWorkmanship = 10 like the weapons but no TinkerLocked row; a Defenders EquipmentSetId row
    /// briefly added for the crests in this same arc was removed by a later owner ruling the same
    /// day, so the crests must carry no EquipmentSetId at all. Each remaining numeric tunable (bow
    /// MaximumVelocity, caster ImbuedEffect/CriticalFrequency) lands as specified. Pure file + regex
    /// parsing, same idiom as BluespireBaseWeaponTinkerContentTests.
    /// </summary>
    [TestClass]
    public class BluespireTinkerLockContentTests
    {
        private const int TinkerLockedProperty = 9068;
        private const int MaximumVelocityProperty = 26;
        private const int ImbuedEffectProperty = 179;
        private const int CriticalFrequencyProperty = 147;
        private const int EquipmentSetIdProperty = 265;
        private const int ItemWorkmanshipProperty = 105;

        private static readonly int[] WeaponWcids =
        {
            1005450, 1005451, 1005452, 1005453, 1005454, 1005455, 1005456, 1005457,
        };

        private static readonly int[] CasterWcids = { 1005451, 1005452, 1005453 };

        private static readonly int[] CrestWcids = { 1005480, 1005481, 1005482 };

        // The exact allow-list: the 4 Ward-Stones (target all 8 weapons) + 2 Thirst-Stones (each
        // targets only its own weapon category - Blood-Thirst the 5 melee/missile weapons,
        // Spirit-Thirst the 3 casters - via the existing rows in BluespireUpgradeGemsCookbook.sql).
        private static readonly HashSet<int> WardStoneWcids = new HashSet<int>
        {
            1004110, // Aun Ward-Stone
            1004111, // Carenzi Ward-Stone
            1004112, // Siraluun Ward-Stone
            1006300, // Gromnie Ward-Stone
        };

        private static readonly HashSet<int> ThirstStoneWcids = new HashSet<int> { 1005460, 1005461 };

        private static readonly HashSet<int> AllowedStoneWcids =
            new HashSet<int>(WardStoneWcids.Concat(ThirstStoneWcids));

        [TestMethod]
        public void AllEightWeapons_CarryTinkerLocked()
        {
            var weeniesDir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var wcid in WeaponWcids)
            {
                var file = FindWeenieFile(weeniesDir, wcid);

                if (file == null)
                {
                    problems.Add($"{wcid}: no file under {weeniesDir} starts with '{wcid} '.");
                    continue;
                }

                var bools = ParseBoolProperties(file, wcid);

                if (!bools.TryGetValue(TinkerLockedProperty, out var locked))
                {
                    problems.Add($"{wcid} ({Path.GetFileName(file)}): missing PropertyBool {TinkerLockedProperty} (TinkerLocked).");
                }
                else if (!locked)
                {
                    problems.Add($"{wcid} ({Path.GetFileName(file)}): TinkerLocked is False, expected True.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// Owner ruling reversed the initial lock on the 3 Siraluun crests (2026-09-27): they must
        /// tinker normally, so they carry ItemWorkmanship = 10 but must NOT carry TinkerLocked at
        /// all. A later owner ruling the same day also removed the Defenders EquipmentSetId row
        /// that had briefly been added in this arc, so EquipmentSetId (265) must be ABSENT too.
        /// </summary>
        [TestMethod]
        public void Crests_CarryWorkmanshipButNotTheLockOrAnEquipmentSet()
        {
            var weeniesDir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var wcid in CrestWcids)
            {
                var file = FindWeenieFile(weeniesDir, wcid);

                if (file == null)
                {
                    problems.Add($"{wcid}: no file under {weeniesDir} starts with '{wcid} '.");
                    continue;
                }

                var name = Path.GetFileName(file);
                var ints = ParseIntProperties(file, wcid);
                var bools = ParseBoolProperties(file, wcid);

                if (!ints.TryGetValue(ItemWorkmanshipProperty, out var workmanship))
                    problems.Add($"{wcid} ({name}): missing PropertyInt {ItemWorkmanshipProperty} (ItemWorkmanship).");
                else if (workmanship != 10)
                    problems.Add($"{wcid} ({name}): ItemWorkmanship is {workmanship}, expected 10.");

                if (ints.ContainsKey(EquipmentSetIdProperty))
                    problems.Add($"{wcid} ({name}): carries PropertyInt {EquipmentSetIdProperty} (EquipmentSetId) - the Defenders set was removed by owner ruling, this must be absent.");

                if (bools.ContainsKey(TinkerLockedProperty))
                    problems.Add($"{wcid} ({name}): carries PropertyBool {TinkerLockedProperty} (TinkerLocked) - crests must tinker normally, not be locked.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void BluespireQuestWeapons_CookbookSourcesAreExactlyTheSixTinkerStones()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            var recipesDir = Path.Combine(contentDir, "sql", "recipes");
            Assert.IsTrue(Directory.Exists(recipesDir), $"Expected {recipesDir} to exist.");

            var sourcesByTarget = new Dictionary<int, HashSet<int>>();
            foreach (var wcid in WeaponWcids)
                sourcesByTarget[wcid] = new HashSet<int>();

            foreach (var file in Directory.EnumerateFiles(recipesDir, "*.sql", SearchOption.AllDirectories))
            {
                foreach (var (sourceWcid, targetWcid) in ParseCookbookSourceTargetPairs(file))
                {
                    if (sourcesByTarget.TryGetValue(targetWcid, out var sources))
                        sources.Add(sourceWcid);
                }
            }

            var problems = new List<string>();

            foreach (var wcid in WeaponWcids)
            {
                var sources = sourcesByTarget[wcid];

                // Every weapon must accept all 4 Ward-Stones (they target all 8 weapons alike).
                var missingWardStones = WardStoneWcids.Except(sources).ToList();
                if (missingWardStones.Count > 0)
                    problems.Add($"{wcid}: missing Ward-Stone cook_book source(s) {string.Join(",", missingWardStones)}.");

                // Every weapon must accept at least one Thirst-Stone (its own category's).
                if (!sources.Overlaps(ThirstStoneWcids))
                    problems.Add($"{wcid}: has no Thirst-Stone ({string.Join(",", ThirstStoneWcids)}) cook_book source at all.");

                // Nothing outside the 6-stone allow-list may reach this target.
                var extra = sources.Except(AllowedStoneWcids).ToList();
                if (extra.Count > 0)
                    problems.Add($"{wcid}: has UNEXPECTED cook_book source(s) {string.Join(",", extra)} - the allow-list must be exactly the 6 tinker stones.");
            }

            // Across the 8 weapons, BOTH Thirst-Stones must actually be used somewhere (Blood-Thirst
            // on the 5 melee/missile weapons, Spirit-Thirst on the 3 casters) - otherwise the
            // "at least one" check above could pass with only one Thirst-Stone ever wired at all.
            var allSources = sourcesByTarget.Values.SelectMany(s => s).ToHashSet();
            foreach (var thirstStone in ThirstStoneWcids)
            {
                if (!allSources.Contains(thirstStone))
                    problems.Add($"Thirst-Stone {thirstStone} has no cook_book row targeting any of the 8 weapons at all.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Bow1005450_HasMaximumVelocityTwentySevenPointThree()
        {
            var weeniesDir = GetWeeniesDir();
            var file = FindWeenieFile(weeniesDir, 1005450);
            Assert.IsNotNull(file, $"no file under {weeniesDir} starts with '1005450 '.");

            var floats = ParseFloatProperties(file, 1005450);

            Assert.IsTrue(floats.TryGetValue(MaximumVelocityProperty, out var velocity),
                $"1005450 ({Path.GetFileName(file)}): missing PropertyFloat {MaximumVelocityProperty} (MaximumVelocity).");
            Assert.AreEqual(27.3, velocity, 0.0001);
        }

        [TestMethod]
        public void Casters_HaveImbuedEffectTwoAndCriticalFrequencyPointThree()
        {
            var weeniesDir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var wcid in CasterWcids)
            {
                var file = FindWeenieFile(weeniesDir, wcid);
                if (file == null)
                {
                    problems.Add($"{wcid}: no file under {weeniesDir} starts with '{wcid} '.");
                    continue;
                }

                var ints = ParseIntProperties(file, wcid);
                var floats = ParseFloatProperties(file, wcid);
                var name = Path.GetFileName(file);

                if (!ints.TryGetValue(ImbuedEffectProperty, out var imbued))
                    problems.Add($"{wcid} ({name}): missing PropertyInt {ImbuedEffectProperty} (ImbuedEffect).");
                else if (imbued != 2)
                    problems.Add($"{wcid} ({name}): ImbuedEffect is {imbued}, expected 2 (CripplingBlow only).");

                if (!floats.TryGetValue(CriticalFrequencyProperty, out var freq))
                    problems.Add($"{wcid} ({name}): missing PropertyFloat {CriticalFrequencyProperty} (CriticalFrequency).");
                else if (Math.Abs(freq - 0.3) > 0.0001)
                    problems.Add($"{wcid} ({name}): CriticalFrequency is {freq}, expected 0.3 (Biting Strike).");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        // ---- helpers ------------------------------------------------------------------

        private static string GetWeeniesDir()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            var weeniesDir = Path.Combine(contentDir, "sql", "weenies");
            Assert.IsTrue(Directory.Exists(weeniesDir), $"Expected {weeniesDir} to exist.");

            return weeniesDir;
        }

        private static string FindContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content");

                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.sql", SearchOption.AllDirectories).Any())
                    return candidate;

                dir = dir.Parent;
            }

            return null;
        }

        private static string FindWeenieFile(string weeniesDir, int wcid)
        {
            return Directory.EnumerateFiles(weeniesDir, wcid + " *.sql", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static Dictionary<int, int> ParseIntProperties(string file, int wcid) =>
            ParseNumericProperties(file, wcid, "weenie_properties_int", v => (int)double.Parse(v));

        private static Dictionary<int, bool> ParseBoolProperties(string file, int wcid)
        {
            var result = new Dictionary<int, bool>();
            var text = File.ReadAllText(file);

            var blocks = Regex.Matches(
                text,
                @"INSERT\s+INTO\s+`weenie_properties_bool`.*?;",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                var body = Regex.Replace(block.Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);

                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(True|False)\s*\)", RegexOptions.IgnoreCase))
                {
                    if (int.Parse(row.Groups[1].Value) != wcid)
                        continue;

                    result[int.Parse(row.Groups[2].Value)] = string.Equals(row.Groups[3].Value, "True", StringComparison.OrdinalIgnoreCase);
                }
            }

            return result;
        }

        private static Dictionary<int, double> ParseFloatProperties(string file, int wcid) =>
            ParseNumericProperties(file, wcid, "weenie_properties_float", double.Parse)
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        private static Dictionary<int, T> ParseNumericProperties<T>(string file, int wcid, string table, Func<string, T> parse)
        {
            var text = File.ReadAllText(file);
            var result = new Dictionary<int, T>();

            var blocks = Regex.Matches(
                text,
                $@"INSERT\s+INTO\s+`{table}`.*?;",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                var body = Regex.Replace(block.Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);

                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?[\d.]+)\s*\)"))
                {
                    if (int.Parse(row.Groups[1].Value) != wcid)
                        continue;

                    result[int.Parse(row.Groups[2].Value)] = parse(row.Groups[3].Value);
                }
            }

            return result;
        }

        /// <summary>
        /// Reads every cook_book INSERT row as (source_W_C_I_D, target_W_C_I_D), column order read
        /// from each INSERT's own header. Files with no cook_book INSERT are skipped cheaply.
        /// </summary>
        private static IEnumerable<(int SourceWcid, int TargetWcid)> ParseCookbookSourceTargetPairs(string file)
        {
            var text = File.ReadAllText(file);

            if (text.IndexOf("cook_book", StringComparison.OrdinalIgnoreCase) < 0)
                yield break;

            var blocks = Regex.Matches(
                text,
                @"INSERT\s+INTO\s+`cook_book`\s*\(([^)]*)\)\s*VALUES(.*?);",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                var columns = block.Groups[1].Value
                    .Split(',')
                    .Select(c => c.Trim().Trim('`'))
                    .ToList();

                var sourceIndex = columns.FindIndex(c => string.Equals(c, "source_W_C_I_D", StringComparison.OrdinalIgnoreCase));
                var targetIndex = columns.FindIndex(c => string.Equals(c, "target_W_C_I_D", StringComparison.OrdinalIgnoreCase));

                if (sourceIndex < 0 || targetIndex < 0)
                    continue;

                var body = Regex.Replace(block.Groups[2].Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);

                foreach (Match row in Regex.Matches(body, @"\(([^()]*)\)", RegexOptions.Singleline))
                {
                    var fields = SplitRowFields(row.Groups[1].Value);

                    if (fields.Count != columns.Count)
                        continue;

                    if (!int.TryParse(fields[sourceIndex], out var sourceWcid)) continue;
                    if (!int.TryParse(fields[targetIndex], out var targetWcid)) continue;

                    yield return (sourceWcid, targetWcid);
                }
            }
        }

        /// <summary>Splits one VALUES tuple on commas that sit outside single-quoted literals.</summary>
        private static List<string> SplitRowFields(string tuple)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuote = false;

            for (var i = 0; i < tuple.Length; i++)
            {
                var c = tuple[i];

                if (c == '\'')
                {
                    if (inQuote && i + 1 < tuple.Length && tuple[i + 1] == '\'')
                    {
                        current.Append("''");
                        i++;
                        continue;
                    }

                    inQuote = !inQuote;
                    current.Append(c);
                    continue;
                }

                if (c == ',' && !inQuote)
                {
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            fields.Add(current.ToString().Trim());

            return fields;
        }
    }
}
