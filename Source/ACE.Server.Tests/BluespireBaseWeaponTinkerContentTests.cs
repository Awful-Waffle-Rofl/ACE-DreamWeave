using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pin for the two gates that make the eight Bluespire dungeon-ladder base weapons
    /// (wcids 1005450-1005457) slayer-tinkerable.
    ///
    /// Gate 1 - ItemWorkmanship. Every slayer imbue recipe, retail (8752/8753/8754/8755) and the three
    /// ML clones (1000200/1000201/1000202), carries a recipe_requirements_int row
    /// "stat 105 (ItemWorkmanship), enum 2 (CompareType.LessThan), value 1" at index 0
    /// (RequirementType.Target). RecipeManager.VerifyRequirement coalesces a MISSING property to 0 for
    /// that comparison (RecipeManager.cs, "case CompareType.LessThan: if ((prop ?? 0) &lt; val)"), so a
    /// weapon with no PropertyInt 105 row scores 0, which is &lt; 1, and the requirement FAILS. Dropping
    /// row 105 from any one of the eight silently un-tinkers it - nothing else in the codebase would
    /// notice, which is what this test is for.
    ///
    /// Gate 2 - cook_book reachability. RecipeManager.GetRecipe consults cook_book FIRST and only then
    /// falls back to RecipeManager_New.GetNewRecipe. The retail slayer stones are case labels in that
    /// fallback switch with no target filter, so they need no row; the three fork gems have no case
    /// label and are reachable ONLY through cook_book. recipes/BluespireBaseWeaponSlayerCookbook.sql
    /// supplies those 24 rows.
    ///
    /// Pure file + regex parsing - no database, no world, same idiom as RewardClaimContentPinTests.
    /// </summary>
    [TestClass]
    public class BluespireBaseWeaponTinkerContentTests
    {
        // PropertyInt ids, verified against Source/ACE.Entity/Enum/Properties/PropertyInt.cs.
        private const int ItemWorkmanshipProperty = 105;
        private const int MaxStructureProperty = 91;
        private const int NumTimesTinkeredProperty = 171;

        // The authored workmanship value: 10, the best roll in the game, fixed rather than rolled.
        private const int ExpectedWorkmanship = 10;

        private static readonly int[] BaseWeaponWcids =
        {
            1005450, // Tourmaline-Bound Elari Bow
            1005451, // Tourmaline-Bound Virindi Implant (Red)
            1005452, // Tourmaline-Bound Virindi Implant (Purple)
            1005453, // The Tourmaline-Bound Awakener
            1005454, // Palenqual's Tourmaline-Bound Waaika
            1005455, // Palenqual's Tourmaline-Bound Tewhate
            1005456, // Palenqual's Tourmaline-Bound Hoeroa
            1005457, // Palenqual's Tourmaline-Bound Taiaha
        };

        // (gem wcid, recipe id) for the three ML island slayer gems, verified against
        // Content/sql/recipes/MLSlayerGem*Recipe.sql and their generated cookbook units.
        private static readonly (int GemWcid, int RecipeId)[] MlSlayerGems =
        {
            (1004110, 1000200), // Aun Ward-Stone
            (1004111, 1000201), // Carenzi Ward-Stone
            (1004112, 1000202), // Siraluun Ward-Stone
            (1006300, 1000203), // Gromnie Ward-Stone, added 2026-09-27
        };

        [TestMethod]
        public void BluespireBaseWeapons_EachCarryItemWorkmanshipTen()
        {
            var weeniesDir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var wcid in BaseWeaponWcids)
            {
                var file = FindWeenieFile(weeniesDir, wcid);

                if (file == null)
                {
                    problems.Add($"{wcid}: no file under {weeniesDir} starts with '{wcid} '.");
                    continue;
                }

                var name = Path.GetFileName(file);
                var ints = ParseIntProperties(file, wcid);

                if (ints.Count == 0)
                {
                    problems.Add($"{wcid} ({name}): no weenie_properties_int rows parsed.");
                    continue;
                }

                if (!ints.TryGetValue(ItemWorkmanshipProperty, out var workmanship))
                {
                    problems.Add(
                        $"{wcid} ({name}): missing PropertyInt {ItemWorkmanshipProperty} (ItemWorkmanship). " +
                        "Without it RecipeManager.VerifyRequirement reads the property as 0, which is < 1, " +
                        "so every slayer imbue recipe refuses this weapon with 'The target item cannot be enchanted!'.");
                }
                else if (workmanship != ExpectedWorkmanship)
                {
                    problems.Add($"{wcid} ({name}): ItemWorkmanship is {workmanship}, expected {ExpectedWorkmanship}.");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// Pins the other half of decision D2: only PropertyInt 105 was added. MaxStructure (91) is read
        /// by nothing on the tinker path, and NumTimesTinkered (171) must stay ABSENT so it defaults to 0,
        /// which is what the retail 10-tinker cap wants.
        /// </summary>
        [TestMethod]
        public void BluespireBaseWeapons_CarryNoMaxStructureOrNumTimesTinkered()
        {
            var weeniesDir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var wcid in BaseWeaponWcids)
            {
                var file = FindWeenieFile(weeniesDir, wcid);

                if (file == null)
                {
                    problems.Add($"{wcid}: no file under {weeniesDir} starts with '{wcid} '.");
                    continue;
                }

                var name = Path.GetFileName(file);
                var ints = ParseIntProperties(file, wcid);

                if (ints.ContainsKey(MaxStructureProperty))
                    problems.Add($"{wcid} ({name}): carries PropertyInt {MaxStructureProperty} (MaxStructure), which this arc deliberately omits.");

                if (ints.ContainsKey(NumTimesTinkeredProperty))
                    problems.Add($"{wcid} ({name}): carries PropertyInt {NumTimesTinkeredProperty} (NumTimesTinkered), which must stay absent so it defaults to 0.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// Every one of the eight weapons must be a cook_book target for every one of the three ML slayer
        /// gems - 24 (gem, weapon) pairs. Without the row the gem never reaches its recipe at all, because
        /// a fork wcid has no case label in RecipeManager_New.GetNewRecipe's switch.
        /// </summary>
        [TestMethod]
        public void MlSlayerGems_HaveACookbookRowForEveryBluespireBaseWeapon()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            var recipesDir = Path.Combine(contentDir, "sql", "recipes");
            Assert.IsTrue(Directory.Exists(recipesDir), $"Expected {recipesDir} to exist.");

            // Scan every recipe unit, not just the one that supplies these rows today: the pairing is what
            // matters, not which file happens to carry it.
            var pairs = new HashSet<(int GemWcid, int RecipeId, int TargetWcid)>();

            foreach (var file in Directory.EnumerateFiles(recipesDir, "*.sql", SearchOption.AllDirectories))
            {
                foreach (var row in ParseCookbookRows(file))
                    pairs.Add(row);
            }

            var problems = new List<string>();

            foreach (var (gemWcid, recipeId) in MlSlayerGems)
            {
                foreach (var targetWcid in BaseWeaponWcids)
                {
                    if (!pairs.Contains((gemWcid, recipeId, targetWcid)))
                    {
                        problems.Add(
                            $"no cook_book row under {recipesDir} maps gem {gemWcid} / recipe {recipeId} to target {targetWcid}; " +
                            "that gem cannot reach its recipe for that weapon at all.");
                    }
                }
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

        /// <summary>
        /// Reads the file's weenie_properties_int INSERT statement(s) for the given object id and returns
        /// property id =&gt; value. Scoped to that one table on purpose: weenie_properties_bool and
        /// weenie_properties_float use the same (object_Id, type, value) row shape, so a file-wide regex
        /// would mix the three id spaces together.
        /// </summary>
        private static Dictionary<int, int> ParseIntProperties(string file, int wcid)
        {
            var text = File.ReadAllText(file);
            var result = new Dictionary<int, int>();

            var blocks = Regex.Matches(
                text,
                @"INSERT\s+INTO\s+`weenie_properties_int`.*?;",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                // Comments carry text like "/* ItemWorkmanship - ... */", never a row, so strip them before
                // matching rather than trusting that no comment happens to look like a tuple.
                var body = Regex.Replace(block.Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);

                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?\d+)\s*\)"))
                {
                    if (int.Parse(row.Groups[1].Value) != wcid)
                        continue;

                    result[int.Parse(row.Groups[2].Value)] = int.Parse(row.Groups[3].Value);
                }
            }

            return result;
        }

        /// <summary>
        /// Reads cook_book INSERT rows as (source_W_C_I_D, recipe_Id, target_W_C_I_D). Column ORDER is read
        /// from the INSERT header rather than assumed, so a unit that lists the columns differently still
        /// parses correctly.
        /// </summary>
        private static IEnumerable<(int GemWcid, int RecipeId, int TargetWcid)> ParseCookbookRows(string file)
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

                var recipeIndex = columns.FindIndex(c => string.Equals(c, "recipe_Id", StringComparison.OrdinalIgnoreCase));
                var sourceIndex = columns.FindIndex(c => string.Equals(c, "source_W_C_I_D", StringComparison.OrdinalIgnoreCase));
                var targetIndex = columns.FindIndex(c => string.Equals(c, "target_W_C_I_D", StringComparison.OrdinalIgnoreCase));

                if (recipeIndex < 0 || sourceIndex < 0 || targetIndex < 0)
                    continue;

                var body = Regex.Replace(block.Groups[2].Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);

                foreach (Match row in Regex.Matches(body, @"\(([^()]*)\)", RegexOptions.Singleline))
                {
                    var fields = SplitRowFields(row.Groups[1].Value);

                    if (fields.Count != columns.Count)
                        continue;

                    if (!int.TryParse(fields[recipeIndex], out var recipeId)) continue;
                    if (!int.TryParse(fields[sourceIndex], out var sourceWcid)) continue;
                    if (!int.TryParse(fields[targetIndex], out var targetWcid)) continue;

                    yield return (sourceWcid, recipeId, targetWcid);
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
                    // '' inside a literal is an escaped quote, not a close.
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
