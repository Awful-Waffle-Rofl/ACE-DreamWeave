using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Factories.Tables.Wcids;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pin for the Magic Defense / Missile Defense Weapon Augmentations (retail 36631 recipe 9127,
    /// 36633 recipe 9128) on fork loot weapons.
    ///
    /// Both augmentations are reachable ONLY through cook_book: RecipeManager.GetRecipe consults
    /// GetCachedCookbook(source wcid, target wcid) first, and RecipeManager_New.GetNewRecipe has no case
    /// label for either source. Retail keys the rows on the TARGET wcid (560 retail loot weapon/caster
    /// wcids per recipe), so a fork wcid added to a loot table gets no row and the augmentation silently
    /// refuses it - the Sanguine caster bug (player report, xSoot, 2026-10). The rows that fix it live in
    /// Content/sql/recipes/SanguineCasterCookbook.sql.
    ///
    /// The guard is deliberately driven by the LOOT TABLES rather than a hard-coded wcid list: every
    /// fork wcid (&gt;= 1000000) any caster or missile loot table can roll must carry both rows, so the
    /// next fork element or weapon added to a table fails here instead of in a player's hands.
    ///
    /// Pure file + regex parsing - no database, no world.
    /// </summary>
    [TestClass]
    public class ForkLootWeaponAugmentationCookbookTests
    {
        private const int ForkWcidFloor = 1000000;

        // (source wcid, recipe id), verified against ace_world cook_book/recipe 2026-10-26.
        private static readonly (int SourceWcid, int RecipeId, string Name)[] Augmentations =
        {
            (36631, 9127, "Magic Defense Weapon Augmentation"),
            (36633, 9128, "Missile Defense Weapon Augmentation"),
        };

        // Fork casters that hold loot-generated instances without being in a loot table today: the
        // Sanguine Orb rolled as loot from 2026-08-02 to 2026-08-25 before CasterWcids dropped it.
        private static readonly int[] RetiredLootWcids = { 1000243 };

        // Fork loot wcids that deliberately do NOT take the defense augmentations. Each entry must carry the
        // reason (an owner ruling, a design note) so an exemption is a decision someone can find, never a way to
        // silence the test. Empty today: every fork loot caster/missile wcid is meant to accept both.
        private static readonly Dictionary<int, string> NotAugmentable = new Dictionary<int, string>
        {
        };

        [TestMethod]
        public void NotAugmentable_EveryExemptionCarriesAReasonAndNamesACheckedWcid()
        {
            var checkedWcids = ForkLootWeaponWcids().Concat(RetiredLootWcids).ToHashSet();

            foreach (var (wcid, reason) in NotAugmentable)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(reason), $"NotAugmentable[{wcid}] needs a reason.");
                Assert.IsTrue(checkedWcids.Contains(wcid), $"NotAugmentable[{wcid}] is not a fork loot wcid this test checks - stale exemption.");
            }
        }

        [TestMethod]
        public void EveryForkLootCasterAndMissileWcid_AcceptsBothDefenseAugmentations()
        {
            var forkLootWcids = ForkLootWeaponWcids();

            // Positive control: the table walk must actually see the Sanguine family, or an empty list
            // would make the assertion below pass vacuously.
            CollectionAssert.IsSubsetOf(new[] { 1000242, 1000244, 1000247 }, forkLootWcids,
                "Expected CasterWcids to roll the Sanguine sceptre/staff/baton (1000242/1000244/1000247).");

            var rows = LoadCookbookRows();

            // Positive control for the row scan itself (retail rows live in the base world DB, not Content/, so only
            // fork-authored rows can be seen): an empty scan means the parser or the path walk broke.
            Assert.IsTrue(rows.Count > 0, "The Content/sql cook_book scan returned no rows at all - the parser or the path walk is broken.");

            var problems = new List<string>();

            foreach (var wcid in forkLootWcids.Concat(RetiredLootWcids).Where(w => !NotAugmentable.ContainsKey(w)).Distinct().OrderBy(w => w))
            {
                foreach (var (source, recipe, name) in Augmentations)
                {
                    if (!rows.Contains((source, recipe, wcid)))
                        problems.Add($"{wcid}: no cook_book row (source {source}, recipe {recipe}) for {name}.");
                }
            }

            Assert.AreEqual(0, problems.Count,
                "Fork loot weapons the defense augmentations cannot reach (add rows to Content/sql/recipes/SanguineCasterCookbook.sql or a sibling unit):\n" +
                string.Join("\n", problems));
        }

        private static List<int> ForkLootWeaponWcids()
        {
            var tables = new IEnumerable<object>[]
            {
                CasterWcids.AllWcids().Cast<object>(),
                AtlatlWcids.AllWcids().Cast<object>(),
                BowWcids_Aluvian.AllWcids().Cast<object>(),
                BowWcids_Gharundim.AllWcids().Cast<object>(),
                BowWcids_Sho.AllWcids().Cast<object>(),
                CrossbowWcids.AllWcids().Cast<object>(),
            };

            return tables
                .SelectMany(t => t)
                .Select(w => Convert.ToInt64(w))
                .Where(w => w >= ForkWcidFloor)
                .Select(w => (int)w)
                .Distinct()
                .OrderBy(w => w)
                .ToList();
        }

        private static HashSet<(int Source, int Recipe, int Target)> LoadCookbookRows()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            // The whole of Content/sql, not just recipes/: a cook_book row is valid wherever a unit puts it (a
            // weenie unit, a patch), and the rule is "some unit grants it", not "the recipes folder grants it".
            var sqlDir = Path.Combine(contentDir, "sql");
            Assert.IsTrue(Directory.Exists(sqlDir), $"Expected {sqlDir} to exist.");

            var rows = new HashSet<(int, int, int)>();

            foreach (var file in Directory.EnumerateFiles(sqlDir, "*.sql", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sqlDir, file).Replace('\\', '/');

                if (relative.Split('/').Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase) || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                    continue;

                foreach (var row in ParseCookbookRows(File.ReadAllText(file)))
                    rows.Add(row);
            }

            return rows;
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

        /// <summary>
        /// Reads cook_book INSERT rows as (source_W_C_I_D, recipe_Id, target_W_C_I_D), taking column order
        /// from the INSERT header. Every committed cook_book unit uses numeric-only tuples plus a trailing
        /// timestamp literal with no embedded comma, so a plain comma split is sufficient here.
        /// </summary>
        private static IEnumerable<(int, int, int)> ParseCookbookRows(string text)
        {
            if (text.IndexOf("cook_book", StringComparison.OrdinalIgnoreCase) < 0)
                yield break;

            var blocks = Regex.Matches(
                text,
                @"INSERT\s+INTO\s+`cook_book`\s*\(([^)]*)\)\s*VALUES(.*?);",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                var columns = block.Groups[1].Value.Split(',').Select(c => c.Trim().Trim('`')).ToList();

                var recipeIndex = columns.FindIndex(c => c.Equals("recipe_Id", StringComparison.OrdinalIgnoreCase));
                var sourceIndex = columns.FindIndex(c => c.Equals("source_W_C_I_D", StringComparison.OrdinalIgnoreCase));
                var targetIndex = columns.FindIndex(c => c.Equals("target_W_C_I_D", StringComparison.OrdinalIgnoreCase));

                if (recipeIndex < 0 || sourceIndex < 0 || targetIndex < 0)
                    continue;

                var body = Regex.Replace(block.Groups[2].Value, @"/\*.*?\*/", " ", RegexOptions.Singleline);

                foreach (Match row in Regex.Matches(body, @"\(([^()]*)\)", RegexOptions.Singleline))
                {
                    var fields = row.Groups[1].Value.Split(',').Select(f => f.Trim()).ToList();

                    if (fields.Count != columns.Count)
                        continue;

                    if (int.TryParse(fields[sourceIndex], out var source)
                        && int.TryParse(fields[recipeIndex], out var recipe)
                        && int.TryParse(fields[targetIndex], out var target))
                    {
                        yield return (source, recipe, target);
                    }
                }
            }
        }
    }
}
