using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Content pins for the Drift Atlan infusion arc: the 26 Drift weapons (1007403-1007428) carry
    /// the OD+18 damage/defense stats, exact spellbooks, no imbue/underlay/tinker rows and the
    /// DriftAtlanWeapon bool (9080); the recipe unit has 78 recipes and 142 cook_book rows with the
    /// owner-ruled eligibility. Pure file + regex parsing of the committed SQL, same idiom as
    /// BluespireTinkerLockContentTests.
    /// </summary>
    [TestClass]
    public class DriftAtlanContentTests
    {
        private const int FirstWeapon = 1007403;
        private const int LastWeapon = 1007428;
        private const int ElementalInfusion = 1007400;
        private const int ArmorInfusion = 1007401;
        private const int LifeWand = 1007427;
        private const int NetherWand = 1007428;
        private const int RetailVoidWand = 46397;

        private static readonly int[] CommonSeven = { 6089, 6094, 6091, 4395, 4405, 4417, 4400 };
        private static readonly int[] WarWandBase = { 6098, 6091, 4414, 4400, 4418 };

        private static readonly int[] RetailSources =
        {
            46101, 46102, 46103, 46104, 46084, 46085, 46086, 46087, 71265, 71266, 71267, 71268,
            46118, 71262, 46120, 46121, 46067, 46068, 46069, 46070, 46135, 46136, 46137, 46138,
            46395, 46397,
        };

        private static readonly int[] WandWcids =
        {
            1007423, 1007424, 1007425, 1007426, 1007427, 1007428, 46135, 46136, 46137, 46138, 46395, 46397,
        };

        private sealed class Group
        {
            public string Name;
            public int First;
            public int Last;
            public int Damage;           // int 44 (melee), 0 if not applicable
            public double Defense;       // float 29
            public int[] ClassSpells;    // per-class pair for melee/bow
        }

        private const int BowElementalDamageBonus = 30;      // int 204
        private const double WandElementalDamageMod = 1.29;   // float 152

        private static readonly Group[] Groups =
        {
            new Group { Name = "Sword", First = 1007403, Last = 1007406, Damage = 79, Defense = 1.18, ClassSpells = new[] { 6072, 4624 } },
            new Group { Name = "Mace", First = 1007407, Last = 1007410, Damage = 64, Defense = 1.20, ClassSpells = new[] { 6043, 4518 } },
            new Group { Name = "Axe", First = 1007411, Last = 1007414, Damage = 69, Defense = 1.16, ClassSpells = new[] { 6047, 4538 } },
            new Group { Name = "Two Handed", First = 1007415, Last = 1007418, Damage = 53, Defense = 1.16, ClassSpells = new[] { 6073, 5032 } },
            new Group { Name = "Bow", First = 1007419, Last = 1007422, Damage = 0, Defense = 1.18, ClassSpells = new[] { 6044, 4522 } },
            new Group { Name = "Wand", First = 1007423, Last = 1007428, Damage = 0, Defense = 1.18, ClassSpells = null },
        };

        private static readonly Dictionary<string, int> ElementDamageTypes = new Dictionary<string, int>
        {
            { "Shivering", 8 }, { "Smoldering", 16 }, { "Sparking", 64 }, { "Stinging", 32 },
        };

        [TestMethod]
        public void DriftWeapons_AllTwentySixFilesExist()
        {
            var dir = GetWeeniesDir();

            for (var wcid = FirstWeapon; wcid <= LastWeapon; wcid++)
                Assert.IsNotNull(FindWeenieFile(dir, wcid), $"{wcid}: no weenie file found.");
        }

        [TestMethod]
        public void DriftWeapons_CarryDamageAndDefense()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var g in Groups)
            {
                for (var wcid = g.First; wcid <= g.Last; wcid++)
                {
                    var file = FindWeenieFile(dir, wcid);
                    if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                    var ints = ParseNumeric(file, wcid, "weenie_properties_int");
                    var floats = ParseNumeric(file, wcid, "weenie_properties_float");
                    var tag = $"{wcid} ({g.Name})";

                    if (g.Name == "Bow")
                    {
                        Expect(problems, tag, "int 204", ints, 204, BowElementalDamageBonus);
                        Expect(problems, tag, "float 63", floats, 63, 2.4);
                    }
                    else if (g.Name == "Wand")
                    {
                        Expect(problems, tag, "float 152", floats, 152, WandElementalDamageMod);
                    }
                    else
                    {
                        Expect(problems, tag, "int 44 Damage", ints, 44, g.Damage);
                    }

                    Expect(problems, tag, "float 29 WeaponDefense", floats, 29, g.Defense);
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void ExistingItemsMigration_MatchesTheWeenieExpectations()
        {
            var sql = StripSqlComments(File.ReadAllText(FindMigration()));
            var problems = new List<string>();

            // Melee int 44: every (range, old) pair must be a Group's range with old + 8 == its current Damage.
            var meleeRows = Regex.Matches(sql, @"b\.`weenie_Class_Id`\s+BETWEEN\s+(\d+)\s+AND\s+(\d+)\s+AND\s+bi\.`value`\s*=\s*(\d+)\)");
            Assert.AreEqual(4, meleeRows.Count, "melee branches in the int 44 update.");
            Assert.IsTrue(Regex.IsMatch(sql, @"SET\s+bi\.`value`\s*=\s*bi\.`value`\s*\+\s*8\s+WHERE\s+bi\.`type`\s*=\s*44"), "melee update must be value + 8 on type 44.");

            foreach (Match m in meleeRows)
            {
                var first = int.Parse(m.Groups[1].Value);
                var last = int.Parse(m.Groups[2].Value);
                var old = int.Parse(m.Groups[3].Value);
                var g = Groups.FirstOrDefault(x => x.Damage > 0 && x.First == first && x.Last == last);

                if (g == null) problems.Add($"melee range {first}-{last}: not a Drift melee group.");
                else if (old + 8 != g.Damage) problems.Add($"{g.Name}: migration old {old} + 8 != weenie Damage {g.Damage}.");
            }

            // Bow int 204.
            var bow = Regex.Match(sql, @"SET\s+bi\.`value`\s*=\s*(\d+)\s+WHERE\s+bi\.`type`\s*=\s*204\s+AND\s+b\.`weenie_Class_Id`\s+BETWEEN\s+(\d+)\s+AND\s+(\d+)\s+AND\s+bi\.`value`\s*=\s*(\d+)");
            var bowGroup = Groups.Single(x => x.Name == "Bow");
            if (!bow.Success) problems.Add("bow int 204 update not found.");
            else
            {
                if (int.Parse(bow.Groups[1].Value) != BowElementalDamageBonus) problems.Add($"bow new {bow.Groups[1].Value} != weenie {BowElementalDamageBonus}.");
                if (int.Parse(bow.Groups[2].Value) != bowGroup.First || int.Parse(bow.Groups[3].Value) != bowGroup.Last) problems.Add("bow wcid range mismatch.");
                if (int.Parse(bow.Groups[4].Value) != 22) problems.Add($"bow old {bow.Groups[4].Value} != 22 (the pre-OD+18 value).");
            }

            // Wand float 152.
            var wand = Regex.Match(sql, @"SET\s+bf\.`value`\s*=\s*([\d.]+)\s+WHERE\s+bf\.`type`\s*=\s*152\s+AND\s+b\.`weenie_Class_Id`\s+BETWEEN\s+(\d+)\s+AND\s+(\d+)\s+AND\s+ABS\(bf\.`value`\s*-\s*([\d.]+)\)");
            var wandGroup = Groups.Single(x => x.Name == "Wand");
            if (!wand.Success) problems.Add("wand float 152 update not found.");
            else
            {
                if (Math.Abs(double.Parse(wand.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - WandElementalDamageMod) > 1e-9) problems.Add($"wand new {wand.Groups[1].Value} != weenie {WandElementalDamageMod}.");
                if (int.Parse(wand.Groups[2].Value) != wandGroup.First || int.Parse(wand.Groups[3].Value) != wandGroup.Last) problems.Add("wand wcid range mismatch.");
                if (Math.Abs(double.Parse(wand.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) - 1.21) > 1e-9) problems.Add($"wand old {wand.Groups[4].Value} != 1.21 (the pre-OD+18 value).");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        private static string StripSqlComments(string s) => Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        private static string FindMigration()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var updates = Path.Combine(dir.FullName, "Database", "Updates", "Shard");

                if (Directory.Exists(updates))
                {
                    var file = Directory.EnumerateFiles(updates, "*Drift-Weapons-OD18-Existing-Items.sql").FirstOrDefault();
                    Assert.IsNotNull(file, $"Drift OD18 shard migration not found in {updates}.");
                    return file;
                }

                dir = dir.Parent;
            }

            Assert.Fail($"Could not locate Database/Updates/Shard by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
        [TestMethod]
        public void DriftWeapons_CarryBoolWorkmanshipBondedAndNoImbueRows()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();
            var forbiddenInts = new[] { 179, 303, 304, 305, 306, 171 };

            for (var wcid = FirstWeapon; wcid <= LastWeapon; wcid++)
            {
                var file = FindWeenieFile(dir, wcid);
                if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                var ints = ParseNumeric(file, wcid, "weenie_properties_int");
                var dids = ParseHex(file, wcid, "weenie_properties_d_i_d");
                var bools = ParseBools(file, wcid);

                Expect(problems, wcid.ToString(), "int 105 ItemWorkmanship", ints, 105, 10);
                Expect(problems, wcid.ToString(), "int 33 Bonded", ints, 33, 1);
                Expect(problems, wcid.ToString(), "int 114 Attuned", ints, 114, 1);

                if (!bools.TryGetValue(9080, out var drift) || !drift)
                    problems.Add($"{wcid}: PropertyBool 9080 missing or False.");

                foreach (var p in forbiddenInts)
                    if (ints.ContainsKey(p))
                        problems.Add($"{wcid}: forbidden PropertyInt {p} present.");

                if (dids.ContainsKey(52))
                    problems.Add($"{wcid}: forbidden DID 52 (IconUnderlay) present.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void DriftWeapons_CarryNoResistanceCleaving()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();

            for (var wcid = FirstWeapon; wcid <= LastWeapon; wcid++)
            {
                var file = FindWeenieFile(dir, wcid);
                if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                if (ParseNumeric(file, wcid, "weenie_properties_int").ContainsKey(263))
                    problems.Add($"{wcid}: PropertyInt 263 (ResistanceModifierType) present.");

                if (ParseNumeric(file, wcid, "weenie_properties_float").ContainsKey(157))
                    problems.Add($"{wcid}: PropertyFloat 157 (ResistanceModifier) present.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void Infusions_ElementalAndCripplingCarryStoneIcons()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();

            // Elemental = retail Major Prismatic Stone (20908) icon; Crippling = Major Smoldering Stone (6124) icon.
            foreach (var (wcid, icon) in new[] { (ElementalInfusion, "0x060026BB"), (1007402, "0x06001C20") })
            {
                var file = FindWeenieFile(dir, wcid);
                if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                var dids = ParseHex(file, wcid, "weenie_properties_d_i_d");
                if (!dids.TryGetValue(8, out var actual) || !string.Equals(actual, icon, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{wcid}: Icon (DID 8) expected {icon}, found {(dids.TryGetValue(8, out var a) ? a : "none")}.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }
        [TestMethod]
        public void DriftWeapons_DamageTypeMatchesElementWord()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();

            for (var wcid = FirstWeapon; wcid <= LastWeapon; wcid++)
            {
                var file = FindWeenieFile(dir, wcid);
                if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                var ints = ParseNumeric(file, wcid, "weenie_properties_int");
                int expected;

                if (wcid == LifeWand) expected = 128;
                else if (wcid == NetherWand) expected = 1024;
                else
                {
                    var name = ParseName(file, wcid);
                    var words = ElementDamageTypes.Keys.Where(k => name != null && name.Contains(k)).ToList();

                    if (words.Count != 1)
                    {
                        problems.Add($"{wcid}: name '{name}' does not contain exactly one element word.");
                        continue;
                    }

                    expected = ElementDamageTypes[words[0]];
                }

                Expect(problems, wcid.ToString(), "int 45 DamageType", ints, 45, expected);
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void DriftWeapons_SpellbooksAreExactSets()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var g in Groups)
            {
                for (var wcid = g.First; wcid <= g.Last; wcid++)
                {
                    var file = FindWeenieFile(dir, wcid);
                    if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                    int[] expected;
                    if (g.ClassSpells != null) expected = CommonSeven.Concat(g.ClassSpells).ToArray();
                    else if (wcid == LifeWand) expected = new[] { 6060 }.Concat(WarWandBase).Concat(new[] { 4582 }).ToArray();
                    else if (wcid == NetherWand) expected = new[] { 6074 }.Concat(WarWandBase).Concat(new[] { 5418 }).ToArray();
                    else expected = new[] { 6075 }.Concat(WarWandBase).Concat(new[] { 4638 }).ToArray();

                    var actualList = ParseSpellbook(file, wcid);
                    var actual = actualList.OrderBy(x => x).ToArray();
                    var want = expected.OrderBy(x => x).ToArray();

                    if (!actual.SequenceEqual(want))
                        problems.Add($"{wcid} ({g.Name}): spellbook [{string.Join(",", actual)}] != expected [{string.Join(",", want)}].");
                }
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void RecipeUnit_HasExpectedCounts()
        {
            var (recipes, cookbook) = LoadRecipeUnit();

            Assert.AreEqual(142, cookbook.Count, "cook_book row count");
            Assert.AreEqual(78, recipes.Count, "recipe count");
        }

        [TestMethod]
        public void RecipeUnit_TargetsAreDriftWeaponsOrRetailSources()
        {
            var (_, cookbook) = LoadRecipeUnit();
            var allowed = new HashSet<int>(RetailSources);
            for (var w = FirstWeapon; w <= LastWeapon; w++) allowed.Add(w);

            var bad = cookbook.Where(c => !allowed.Contains(c.Target)).Select(c => $"cook_book {c.Id}: target {c.Target}").ToList();
            Assert.AreEqual(0, bad.Count, string.Join(Environment.NewLine, bad));
        }

        [TestMethod]
        public void RecipeUnit_ConversionSuccessWcidsAreDriftWeapons()
        {
            var (recipes, cookbook) = LoadRecipeUnit();
            var referenced = new HashSet<int>(cookbook.Select(c => c.RecipeId));
            var bad = new List<string>();
            var conversions = 0;

            foreach (var r in recipes)
            {
                if (r.Value == 0) continue;
                conversions++;
                if (r.Value < FirstWeapon || r.Value > LastWeapon)
                    bad.Add($"recipe {r.Key}: success wcid {r.Value} outside {FirstWeapon}-{LastWeapon}");
            }

            foreach (var id in cookbook.Select(c => c.RecipeId).Distinct())
                if (!recipes.ContainsKey(id))
                    bad.Add($"cook_book references unknown recipe {id}");

            Assert.IsTrue(conversions > 0, "expected at least one conversion recipe.");
            Assert.AreEqual(0, bad.Count, string.Join(Environment.NewLine, bad));
        }

        [TestMethod]
        public void RecipeUnit_EligibilityRules()
        {
            var (_, cookbook) = LoadRecipeUnit();
            var bad = new List<string>();

            foreach (var c in cookbook)
            {
                var isElemOrArmor = c.Source == ElementalInfusion || c.Source == ArmorInfusion;

                if ((c.Target == NetherWand || c.Target == RetailVoidWand) && isElemOrArmor)
                    bad.Add($"cook_book {c.Id}: Void/Nether wand {c.Target} paired with source {c.Source}.");

                if (c.Source == ArmorInfusion && WandWcids.Contains(c.Target))
                    bad.Add($"cook_book {c.Id}: wand {c.Target} paired with Armor infusion.");
            }

            Assert.AreEqual(0, bad.Count, string.Join(Environment.NewLine, bad));
        }

        // ------------------------------------------------ pairing / imbue / infusion pins

        // Retail Enhanced source -> (Drift wcid, element). Mirrors ATLAN_TABLE in tools/drift-atlan/generate_recipes.py.
        private static readonly Dictionary<int, (int Drift, string Element)> RetailMap = BuildRetailMap();

        private static Dictionary<int, (int, string)> BuildRetailMap()
        {
            var elements = new[] { "Cold", "Fire", "Electric", "Acid" };
            var table = new[]
            {
                new[] { 46101, 46102, 46103, 46104 }, new[] { 46084, 46085, 46086, 46087 },
                new[] { 71265, 71266, 71267, 71268 }, new[] { 46118, 71262, 46120, 46121 },
                new[] { 46067, 46068, 46069, 46070 }, new[] { 46135, 46136, 46137, 46138 },
            };
            var map = new Dictionary<int, (int, string)>();
            var drift = FirstWeapon;

            foreach (var row in table)
                for (var i = 0; i < 4; i++)
                    map[row[i]] = (drift++, elements[i]);

            map[46395] = (1007427, "Health");
            map[RetailVoidWand] = (NetherWand, "Nether");
            return map;
        }

        private static (int Bit, long Underlay) ExpectedImbue(int infusion, string element)
        {
            switch (infusion)
            {
                case ArmorInfusion: return (0x4, 0x06003356);
                case 1007402: return (0x2, 0x06003357);
                default:
                    switch (element)
                    {
                        case "Cold": return (0x80, 0x06003353);
                        case "Fire": return (0x200, 0x06003359);
                        case "Electric": return (0x100, 0x06003354);
                        case "Acid": return (0x40, 0x06003355);
                        case "Health": return (0x8000, 0x0600678D);
                    }
                    throw new InvalidOperationException($"no Elemental imbue for element '{element}'");
            }
        }

        private sealed class RecipeRow
        {
            public int Id, SuccessWcid, DestroySourceChance, DestroySourceAmount, DestroyTargetChance, DestroyTargetAmount;
            public List<(int Index, int Stat, long Value)> Ints = new List<(int, int, long)>();
            public List<(int Index, int Stat, long Value)> Dids = new List<(int, int, long)>();
            public int ModCount;
            public int ExecutesOnSuccess = -1;
        }

        private static Dictionary<int, RecipeRow> LoadRecipeRows()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, "Content/ not found.");
            var text = StripComments(File.ReadAllText(Path.Combine(contentDir, "sql", "recipes", "DriftAtlanInfusions.sql")));
            var rows = new Dictionary<int, RecipeRow>();

            var recipeBlock = Regex.Match(text, @"INSERT\s+INTO\s+`recipe`\s*\(.*?\)\s*VALUES(.*?);", RegexOptions.Singleline);
            Assert.IsTrue(recipeBlock.Success, "recipe INSERT not found.");

            foreach (Match tuple in Regex.Matches(recipeBlock.Groups[1].Value, @"\((\d+),.*?'\d{4}-\d\d-\d\d \d\d:\d\d:\d\d'\)", RegexOptions.Singleline))
            {
                var inner = tuple.Value.Substring(1, tuple.Value.Length - 2);
                var f = SplitRowFields(inner);
                rows[int.Parse(f[0])] = new RecipeRow
                {
                    Id = int.Parse(f[0]),
                    SuccessWcid = int.Parse(f[5]),
                    DestroySourceChance = int.Parse(f[11]), DestroySourceAmount = int.Parse(f[12]),
                    DestroyTargetChance = int.Parse(f[14]), DestroyTargetAmount = int.Parse(f[15]),
                };
            }

            // One segment per recipe_mod INSERT; the int/did rows that follow belong to it.
            var segments = Regex.Split(text, @"INSERT\s+INTO\s+`recipe_mod`\s*\([^)]*\)\s*VALUES");

            for (var s = 1; s < segments.Length; s++)
            {
                var seg = segments[s];
                var head = Regex.Match(seg, @"\(\s*(\d+)\s*,\s*(\d+)\s*,");
                var recipeId = int.Parse(head.Groups[1].Value);
                Assert.IsTrue(rows.ContainsKey(recipeId), $"recipe_mod references unknown recipe {recipeId}");
                var r = rows[recipeId];
                r.ModCount++;
                r.ExecutesOnSuccess = int.Parse(head.Groups[2].Value);

                foreach (Match m in Regex.Matches(seg, @"INSERT\s+INTO\s+`recipe_mods_int`[^;]*?VALUES\s*\(@parent_id\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?\d+)", RegexOptions.Singleline))
                    r.Ints.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value)));

                foreach (Match m in Regex.Matches(seg, @"INSERT\s+INTO\s+`recipe_mods_d_i_d`[^;]*?VALUES\s*\(@parent_id\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?\d+)", RegexOptions.Singleline))
                    r.Dids.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), long.Parse(m.Groups[3].Value)));
            }

            return rows;
        }

        [TestMethod]
        public void RecipeUnit_EveryCookbookRowPairsTheRightRecipe()
        {
            var (_, cookbook) = LoadRecipeUnit();
            var rows = LoadRecipeRows();
            var driftToElement = RetailMap.Values.ToDictionary(v => v.Drift, v => v.Element);
            var bad = new List<string>();

            foreach (var c in cookbook)
            {
                var tag = $"cook_book {c.Id} (source {c.Source}, target {c.Target}, recipe {c.RecipeId})";

                if (!rows.TryGetValue(c.RecipeId, out var r)) { bad.Add($"{tag}: unknown recipe."); continue; }

                if (r.ModCount != 1 || r.ExecutesOnSuccess != 1) bad.Add($"{tag}: expected exactly one recipe_mod executing on success.");
                if (r.Ints.Count != 1 || r.Dids.Count != 1) bad.Add($"{tag}: expected exactly one mods_int and one mods_d_i_d row.");

                string element;
                int expectedIndex;

                if (RetailMap.TryGetValue(c.Target, out var retail))
                {
                    element = retail.Element;
                    expectedIndex = 3; // SuccessResult
                    if (r.SuccessWcid != retail.Drift) bad.Add($"{tag}: success wcid {r.SuccessWcid}, expected {retail.Drift}.");
                    if (r.DestroySourceChance != 1 || r.DestroySourceAmount != 1) bad.Add($"{tag}: must destroy the source (1,1).");
                    if (r.DestroyTargetChance != 1 || r.DestroyTargetAmount != 1) bad.Add($"{tag}: must destroy the target (1,1).");
                }
                else if (driftToElement.TryGetValue(c.Target, out element))
                {
                    expectedIndex = 0; // SuccessTarget
                    if (r.SuccessWcid != 0) bad.Add($"{tag}: re-imbue success wcid {r.SuccessWcid}, expected 0.");
                    if (r.DestroySourceChance != 1 || r.DestroySourceAmount != 1) bad.Add($"{tag}: must destroy the source (1,1).");
                    if (r.DestroyTargetChance != 0 || r.DestroyTargetAmount != 0) bad.Add($"{tag}: re-imbue must not destroy the target.");
                }
                else { bad.Add($"{tag}: target is neither a retail source nor a Drift weapon."); continue; }

                if (r.Ints.Count == 1 && r.Dids.Count == 1)
                {
                    var want = ExpectedImbue(c.Source, element);
                    var i = r.Ints[0];
                    var d = r.Dids[0];
                    if (i.Index != expectedIndex || d.Index != expectedIndex) bad.Add($"{tag}: mod index {i.Index}/{d.Index}, expected {expectedIndex}.");
                    if (i.Stat != 179 || i.Value != want.Bit) bad.Add($"{tag}: int {i.Stat}={i.Value}, expected 179={want.Bit}.");
                    if (d.Stat != 52 || d.Value != want.Underlay) bad.Add($"{tag}: did {d.Stat}={d.Value}, expected 52={want.Underlay}.");
                }
            }

            Assert.AreEqual(0, bad.Count, string.Join(Environment.NewLine, bad));
        }

        [TestMethod]
        public void RecipeUnit_PerInfusionPairCounts()
        {
            var (_, cookbook) = LoadRecipeUnit();
            // Each eligible pair exists twice (conversion + re-imbue), so retail-target rows count the pairs.
            Assert.AreEqual(25, cookbook.Count(c => c.Source == ElementalInfusion && RetailMap.ContainsKey(c.Target)), "Elemental");
            Assert.AreEqual(20, cookbook.Count(c => c.Source == ArmorInfusion && RetailMap.ContainsKey(c.Target)), "Armor");
            Assert.AreEqual(26, cookbook.Count(c => c.Source == 1007402 && RetailMap.ContainsKey(c.Target)), "Crippling");
            Assert.AreEqual(25, cookbook.Count(c => c.Source == ElementalInfusion && !RetailMap.ContainsKey(c.Target)), "Elemental re-imbue");
            Assert.AreEqual(20, cookbook.Count(c => c.Source == ArmorInfusion && !RetailMap.ContainsKey(c.Target)), "Armor re-imbue");
            Assert.AreEqual(26, cookbook.Count(c => c.Source == 1007402 && !RetailMap.ContainsKey(c.Target)), "Crippling re-imbue");
        }

        [TestMethod]
        public void Infusions_CarryUseTextBondedTradeableAndTargetType()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();

            foreach (var wcid in new[] { 1007400, 1007401, 1007402 })
            {
                var file = FindWeenieFile(dir, wcid);
                if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                var ints = ParseNumeric(file, wcid, "weenie_properties_int");
                var tag = wcid.ToString();

                Expect(problems, tag, "int 16 ItemUseable", ints, 16, 524296);
                Expect(problems, tag, "int 94 TargetType", ints, 94, 33025);
                Expect(problems, tag, "int 11 MaxStackSize", ints, 11, 100);
                Expect(problems, tag, "int 33 Bonded", ints, 33, 1);
                // Owner ruling 2026-10-31: infusions are tradeable, so no Attuned row (Bonded stays: no death drop).
                if (ints.ContainsKey(114)) problems.Add($"{tag}: carries int 114 Attuned; infusions are tradeable.");

                var useRow = false;
                foreach (var body in Blocks(file, "weenie_properties_string"))
                    if (Regex.IsMatch(body, $@"\(\s*{wcid}\s*,\s*14\s*,\s*'[^']+'"))
                        useRow = true;

                if (!useRow) problems.Add($"{tag}: no PropertyString 14 (Use) row.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        [TestMethod]
        public void AllDriftWeenies_HaveNoStringLiteralsInNumericBlocks()
        {
            var dir = GetWeeniesDir();
            var problems = new List<string>();
            var tables = new[] { "weenie_properties_int", "weenie_properties_float", "weenie_properties_bool", "weenie_properties_d_i_d" };

            for (var wcid = ElementalInfusion; wcid <= LastWeapon; wcid++)
            {
                var file = FindWeenieFile(dir, wcid);
                if (file == null) { problems.Add($"{wcid}: file missing."); continue; }

                foreach (var table in tables)
                    foreach (var body in Blocks(file, table))
                        if (body.Contains("'"))
                            problems.Add($"{wcid}: quoted string literal inside a {table} INSERT.");
            }

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        // ---------------------------------------------------------------- helpers

        private static void Expect(List<string> problems, string tag, string label, Dictionary<int, double> map, int key, double expected)
        {
            if (!map.TryGetValue(key, out var actual))
                problems.Add($"{tag}: missing {label}.");
            else if (Math.Abs(actual - expected) > 1e-9)
                problems.Add($"{tag}: {label} is {actual}, expected {expected}.");
        }

        private static (Dictionary<int, int> Recipes, List<(int Id, int RecipeId, int Source, int Target)> Cookbook) LoadRecipeUnit()
        {
            var contentDir = FindContentDir();
            Assert.IsNotNull(contentDir, $"Could not locate the repo's Content/ directory by walking up from {AppContext.BaseDirectory}.");

            var file = Path.Combine(contentDir, "sql", "recipes", "DriftAtlanInfusions.sql");
            Assert.IsTrue(File.Exists(file), $"Expected {file} to exist.");

            var text = File.ReadAllText(file);

            var recipes = new Dictionary<int, int>();
            var recipeBlock = Regex.Match(text, @"INSERT\s+INTO\s+`recipe`\s*\(.*?\)\s*VALUES(.*?);\s*(\r?\n|$)", RegexOptions.Singleline);
            Assert.IsTrue(recipeBlock.Success, "recipe INSERT not found.");

            // (id, unknown_1, skill, difficulty, salvage_Type,<newline> success_W_C_I_D, ...
            foreach (Match m in Regex.Matches(recipeBlock.Groups[1].Value,
                @"\(\s*(\d+)\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*(\d+)\s*,"))
            {
                recipes[int.Parse(m.Groups[1].Value)] = int.Parse(m.Groups[2].Value);
            }

            var cookbook = new List<(int, int, int, int)>();
            var cbBlock = Regex.Match(text, @"INSERT\s+INTO\s+`cook_book`\s*\([^)]*\)\s*VALUES(.*?);", RegexOptions.Singleline);
            Assert.IsTrue(cbBlock.Success, "cook_book INSERT not found.");

            foreach (Match m in Regex.Matches(cbBlock.Groups[1].Value,
                @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,"))
            {
                cookbook.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                    int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)));
            }

            return (recipes, cookbook);
        }

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

        private static List<string> SplitRowFields(string tuple)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuote = false;

            foreach (var c in tuple)
            {
                if (c == '\'') inQuote = !inQuote;

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

        private static string StripComments(string s) => Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        private static IEnumerable<string> Blocks(string file, string table)
        {
            var text = File.ReadAllText(file);
            foreach (Match b in Regex.Matches(text, $@"INSERT\s+INTO\s+`{table}`.*?;", RegexOptions.Singleline | RegexOptions.IgnoreCase))
                yield return StripComments(b.Value);
        }

        private static Dictionary<int, double> ParseNumeric(string file, int wcid, string table)
        {
            var result = new Dictionary<int, double>();

            foreach (var body in Blocks(file, table))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(-?[\d.]+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result[int.Parse(row.Groups[2].Value)] = double.Parse(row.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);

            return result;
        }

        private static Dictionary<int, string> ParseHex(string file, int wcid, string table)
        {
            var result = new Dictionary<int, string>();

            foreach (var body in Blocks(file, table))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(0x[0-9A-Fa-f]+)\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result[int.Parse(row.Groups[2].Value)] = row.Groups[3].Value;

            return result;
        }

        private static Dictionary<int, bool> ParseBools(string file, int wcid)
        {
            var result = new Dictionary<int, bool>();

            foreach (var body in Blocks(file, "weenie_properties_bool"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(True|False)\s*\)", RegexOptions.IgnoreCase))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result[int.Parse(row.Groups[2].Value)] = string.Equals(row.Groups[3].Value, "True", StringComparison.OrdinalIgnoreCase);

            return result;
        }

        private static string ParseName(string file, int wcid)
        {
            foreach (var body in Blocks(file, "weenie_properties_string"))
            {
                var m = Regex.Match(body, $@"\(\s*{wcid}\s*,\s*1\s*,\s*'([^']*)'");
                if (m.Success) return m.Groups[1].Value;
            }

            return null;
        }

        private static List<int> ParseSpellbook(string file, int wcid)
        {
            var result = new List<int>();

            foreach (var body in Blocks(file, "weenie_properties_spell_book"))
                foreach (Match row in Regex.Matches(body, @"\(\s*(\d+)\s*,\s*(\d+)\s*,\s*[-\d.]+\s*\)"))
                    if (int.Parse(row.Groups[1].Value) == wcid)
                        result.Add(int.Parse(row.Groups[2].Value));

            return result;
        }
    }
}
