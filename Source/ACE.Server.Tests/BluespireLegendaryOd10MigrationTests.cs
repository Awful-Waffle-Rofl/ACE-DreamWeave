using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Owner correction 2026-11-21: the eight Bluespire base weapons (1005450-1005457) go Epic -> Legendary and take
    /// OD+10 plus ten damage tinkers baked in, and the three binding stones (1005460, 1005461, 1005493) bind the
    /// Legendary tier. Database/Updates/Shard/2026-11-21-04-Bluespire-Weapons-Legendary-OD10-Existing-Items.sql carries
    /// the change to items that already exist. These tests parse the migration's literals and pin them to the weenie
    /// templates and recipes, so an edit to one side without the other fails here instead of leaving old and new items
    /// disagreeing. The dat-backed check (spell pairs share category, power matches) skips when the dats are absent;
    /// every other test here is dat-free.
    /// </summary>
    [TestClass]
    public class BluespireLegendaryOd10MigrationTests
    {
        private const string MigrationFile = "2026-11-21-04-Bluespire-Weapons-Legendary-OD10-Existing-Items.sql";

        /// <summary>Old Epic id -> new Legendary id, the owner-ruled map.</summary>
        private static readonly Dictionary<int, int> SpellMap = new Dictionary<int, int>
        {
            { 3963, 6103 }, // Coordination
            { 3964, 6105 }, // Focus
            { 3965, 6107 }, // Strength
            { 4019, 6106 }, // Quickness
            { 4227, 6101 }, // Willpower
            { 4661, 6089 }, // Blood Thirst (stone)
            { 4670, 6098 }, // Spirit Thirst (stone)
            { 4663, 6091 }, // Defender (stone)
        };

        /// <summary>Each weapon's two template cantrips after the change.</summary>
        private static readonly Dictionary<int, int[]> WeaponCantrips = new Dictionary<int, int[]>
        {
            { 1005450, new[] { 6103, 6106 } }, // Elari Bow: Coordination, Quickness
            { 1005451, new[] { 6105, 6101 } }, // Implant (Red): Focus, Willpower
            { 1005452, new[] { 6105, 6101 } }, // Implant (Purple)
            { 1005453, new[] { 6105, 6101 } }, // Awakener
            { 1005454, new[] { 6103, 6107 } }, // Waaika: Coordination, Strength
            { 1005455, new[] { 6103, 6107 } }, // Tewhate
            { 1005456, new[] { 6103, 6106 } }, // Hoeroa: Coordination, Quickness
            { 1005457, new[] { 6103, 6107 } }, // Taiaha
        };

        private static readonly Regex CaseMap = new Regex(@"WHEN\s+(\d+)\s+THEN\s+([\d.]+)");

        // ---------- spell book ----------

        [TestMethod]
        public void ShardMigration_SpellIdMap_IsTheOwnerMap_InEveryStatementThatRenames()
        {
            var statements = Statements();

            var renames = statements.Where(s => s.Contains("CASE sb.`spell`") || s.Contains("er.`spell_Id` = CASE er.`spell_Id`") || s.Contains("keep.`spell_Id` = CASE")).ToList();
            Assert.AreEqual(4, renames.Count, "two spell-book statements and two registry statements carry the id map");

            foreach (var s in renames)
            {
                var idCase = IdCase(s);
                CollectionAssert.AreEquivalent(SpellMap.ToList(), idCase.ToList(), $"id map in: {Head(s)}");
            }

            // Every rename is scoped to the old ids only, through one IN list identical to the map's keys.
            foreach (var s in renames)
            {
                var inList = Regex.Match(s, @"`(?:spell|spell_Id)` IN \(([\d,\s]+)\)");
                Assert.IsTrue(inList.Success, $"old-id IN list in: {Head(s)}");
                CollectionAssert.AreEquivalent(SpellMap.Keys.ToList(), Ints(inList.Groups[1].Value), $"IN list in: {Head(s)}");
            }
        }

        [TestMethod]
        public void ShardMigration_OldIds_AppearOnlyOnTheFromSide()
        {
            var body = string.Join(";", Statements());

            foreach (var old in SpellMap.Keys)
                Assert.IsFalse(Regex.IsMatch(body, $@"THEN\s+{old}\b"), $"old id {old} is never a THEN target");

            foreach (var neu in SpellMap.Values)
                Assert.IsFalse(Regex.IsMatch(body, $@"IN \([^)]*\b{neu}\b"), $"new id {neu} is never in an old-id IN list");
        }

        [TestMethod]
        public void WeaponTemplates_CarryTheLegendaryCantrips_AndNoEpicOnes()
        {
            foreach (var kv in WeaponCantrips)
            {
                var spells = SpellBook(WeenieSql(kv.Key), kv.Key);

                foreach (var id in kv.Value)
                    Assert.IsTrue(spells.ContainsKey(id), $"{kv.Key} spell book has {id}");

                foreach (var old in SpellMap.Keys)
                    Assert.IsFalse(spells.ContainsKey(old), $"{kv.Key} spell book no longer has Epic {old}");

                // The Hoeroa reference's probability-0 anomaly is kept on the renamed rows.
                var expectedProbability = kv.Key == 1005456 ? 0 : 2;
                foreach (var id in kv.Value)
                    Assert.AreEqual(expectedProbability, spells[id], $"{kv.Key} spell {id} probability");
            }
        }

        [TestMethod]
        public void WeaponTemplates_SpellBookSets_AreLegendaryCantripsPlusTheirOriginalAurasAndAptitude()
        {
            // The non-cantrip ids are each weapon's spell book on origin/master before this change (ddffc4eca), so a
            // stray rename of an Aura of Incantation or Legendary Aptitude row fails here.
            var kept = new Dictionary<int, int[]>
            {
                { 1005450, new[] { 4395, 4400, 6044 } },
                { 1005451, new[] { 4400, 4414, 6075 } },
                { 1005452, new[] { 4400, 4414, 6074 } },
                { 1005453, new[] { 4400, 4414, 6060 } },
                { 1005454, new[] { 4395, 4400, 4405, 4417, 6043 } },
                { 1005455, new[] { 4395, 4400, 4405, 4417, 6072 } },
                { 1005456, new[] { 4395, 4400, 4405, 4417, 6047 } },
                { 1005457, new[] { 4395, 4400, 4405, 4417, 6073 } },
            };

            foreach (var kv in WeaponCantrips)
            {
                var expected = kv.Value.Concat(kept[kv.Key]).OrderBy(x => x).ToList();
                var actual = SpellBook(WeenieSql(kv.Key), kv.Key).Keys.OrderBy(x => x).ToList();
                CollectionAssert.AreEqual(expected, actual, $"{kv.Key} spell book set: expected [{string.Join(",", expected)}], actual [{string.Join(",", actual)}]");
            }
        }

        [TestMethod]
        public void StoneRecipes_AddTheLegendarySpell_AndTheMigrationMapsTheirEpicSpell()
        {
            foreach (var (file, epic, legendary) in new[] {
                ("BluespireUpgradeGemsRecipes.sql", 4661, 6089),
                ("BluespireUpgradeGemsRecipes.sql", 4670, 6098),
                ("BluespireDefenderStoneRecipe.sql", 4663, 6091) })
            {
                var sql = StripComments(File.ReadAllText(Path.Combine(ContentDir(), "sql", "recipes", file)));

                // recipe_mods_int AddSpell row: (@parent_id, 0, <spell>, -1, 7, 0)
                Assert.IsTrue(Regex.IsMatch(sql, $@"\(@parent_id,\s*0,\s*{legendary},\s*-1,\s*7,\s*0\)"), $"{file} adds {legendary}");
                Assert.IsFalse(Regex.IsMatch(sql, $@"\(@parent_id,\s*0,\s*{epic},"), $"{file} no longer adds {epic}");
                Assert.AreEqual(legendary, SpellMap[epic], $"migration maps {epic} -> {legendary}");
            }
        }

        [TestMethod]
        public void StoneRecipes_SuccessMessages_NameTheLegendarySpell()
        {
            foreach (var (file, recipe, spell, target) in new[] {
                ("BluespireUpgradeGemsRecipes.sql", 1005468, "Blood Thirst", "weapon"),
                ("BluespireUpgradeGemsRecipes.sql", 1005469, "Spirit Thirst", "caster implement"),
                ("BluespireDefenderStoneRecipe.sql", 1005490, "Defender", "weapon") })
            {
                var sql = StripComments(File.ReadAllText(Path.Combine(ContentDir(), "sql", "recipes", file)));

                // success_Message is the first string literal after the recipe id in its VALUES tuple.
                var m = Regex.Match(sql, $@"\(\s*{recipe},\s*0,\s*0,\s*0,\s*0,\s*0,\s*1,\s*'((?:[^']|'')*)'");
                Assert.IsTrue(m.Success, $"{file} recipe {recipe} success message found");

                var message = m.Groups[1].Value;
                StringAssert.Matches(message, new Regex(@"^You bind the Legendary [A-Za-z ]+ into your (weapon|caster implement)\.$"), $"recipe {recipe} message shape");
                Assert.AreEqual($"You bind the Legendary {spell} into your {target}.", message, $"recipe {recipe} message");
                Assert.IsFalse(message.Contains("Epic"), $"recipe {recipe} message no longer says Epic");
            }
        }

        // ---------- statement scope ----------

        [TestMethod]
        public void ShardMigration_SpellBookStatements_AreScopedToTheWeaponsByObjectId()
        {
            var statements = Statements();
            const string scope = "`weenie_Class_Id` BETWEEN 1005450 AND 1005457";

            var delete = statements.Single(s => s.StartsWith("DELETE sb FROM `biota_properties_spell_book` sb"));
            StringAssert.Contains(delete, "INNER JOIN `biota` b ON b.`id` = sb.`object_Id`", "statement 1 joins the owning biota on object_Id");
            StringAssert.Contains(delete, $"WHERE b.{scope} ", "statement 1 is scoped to the eight weapons");
            StringAssert.Contains(delete, "keep.`object_Id` = sb.`object_Id`", "statement 1 collision join stays on the same biota");

            var update = statements.Single(s => s.StartsWith("UPDATE `biota_properties_spell_book` sb"));
            StringAssert.Contains(update, $"sb.`object_Id` IN (SELECT b.`id` FROM `biota` b WHERE b.{scope})", "statement 2 is scoped to the eight weapons by object_Id");
        }

        [TestMethod]
        public void ShardMigration_RegistryStatements_AreScopedToTheWeaponsByCaster()
        {
            var statements = Statements();
            const string scope = "`weenie_Class_Id` BETWEEN 1005450 AND 1005457";

            var delete = statements.Single(s => s.StartsWith("DELETE er FROM `biota_properties_enchantment_registry` er"));
            StringAssert.Contains(delete, "INNER JOIN `biota` c ON c.`id` = er.`caster_Object_Id`", "statement 3 joins the CASTER biota");
            StringAssert.Contains(delete, $"WHERE c.{scope} ", "statement 3 is scoped to the eight weapons as caster");
            StringAssert.Contains(delete, "keep.`object_Id` = er.`object_Id`", "statement 3 collision join stays on the same object");
            StringAssert.Contains(delete, "keep.`layer_Id` = er.`layer_Id`", "statement 3 collision join stays on the same layer");

            var update = statements.Single(s => s.StartsWith("UPDATE `biota_properties_enchantment_registry` er"));
            StringAssert.Contains(update, $"er.`caster_Object_Id` IN (SELECT c.`id` FROM `biota` c WHERE c.{scope})", "statement 4 is scoped to the eight weapons as caster");
        }

        // ---------- enchantment registry ----------

        [TestMethod]
        public void ShardMigration_Registry_RewritesPowerAndMagnitude_ForOldAndNewIds()
        {
            var update = Statements().Single(s => s.StartsWith("UPDATE `biota_properties_enchantment_registry`"));

            // Expected values: power from client_portal.dat (SpellBase.Power), magnitude from the world spell table
            // (stat_Mod_Val), both read 2026-11-21. The dat half is re-checked by the RequiresDatFiles test below.
            var expected = new Dictionary<int, (int Power, double Value)>
            {
                { 6103, (35, 35) }, { 6105, (35, 35) }, { 6107, (35, 35) }, { 6106, (35, 35) }, { 6101, (35, 35) },
                { 6089, (10, 10) }, { 6098, (7, 0.07) }, { 6091, (9, 0.09) },
            };

            var power = CaseValues(update, "er.`power_Level` = CASE");
            var value = CaseValues(update, "er.`stat_Mod_Value` = CASE");

            foreach (var kv in SpellMap)
            {
                // The old id is the branch that runs (the WHERE selects only old-id rows, and spell_Id is assigned last);
                // the new-id branches are never reached and only keep the two lists symmetric.
                foreach (var id in new[] { kv.Key, kv.Value })
                {
                    Assert.AreEqual(expected[kv.Value].Power, (int)power[id], $"power_Level for {id}");
                    Assert.AreEqual(expected[kv.Value].Value, value[id], 1e-9, $"stat_Mod_Value for {id}");
                }
            }

            Assert.AreEqual(SpellMap.Count * 2, power.Count, "power CASE covers exactly the old and new ids");
            Assert.AreEqual(SpellMap.Count * 2, value.Count, "value CASE covers exactly the old and new ids");

            // spell_Id is assigned LAST in a single-table UPDATE (left-to-right), and the scope is the caster.
            var idAssign = update.IndexOf("er.`spell_Id` = CASE", StringComparison.Ordinal);
            Assert.IsTrue(idAssign > update.IndexOf("er.`stat_Mod_Value` = CASE", StringComparison.Ordinal), "spell_Id assigned after the magnitude");
            Assert.IsTrue(idAssign > update.IndexOf("er.`power_Level` = CASE", StringComparison.Ordinal), "spell_Id assigned after the power");
            StringAssert.Contains(update, "er.`caster_Object_Id` IN (SELECT c.`id` FROM `biota` c WHERE c.`weenie_Class_Id` BETWEEN 1005450 AND 1005457)");
        }

        [TestMethod]
        [TestCategory("RequiresDatFiles")]
        public void SpellPairs_ShareCategoryAndShape_AndTheNewPowerIsTheMigrations()
        {
            RequireDats();

            var update = Statements().Single(s => s.StartsWith("UPDATE `biota_properties_enchantment_registry`"));
            var power = CaseValues(update, "er.`power_Level` = CASE");
            var table = DatManager.PortalDat.SpellTable.Spells;

            foreach (var kv in SpellMap)
            {
                var oldSpell = table[(uint)kv.Key];
                var newSpell = table[(uint)kv.Value];

                StringAssert.StartsWith(oldSpell.Name, "Epic ", $"{kv.Key}");
                StringAssert.StartsWith(newSpell.Name, "Legendary ", $"{kv.Value}");
                Assert.AreEqual(oldSpell.Name.Substring(5), newSpell.Name.Substring(10), $"{kv.Key} -> {kv.Value} is the same spell family");
                Assert.AreEqual(oldSpell.Category, newSpell.Category, $"{kv.Key} -> {kv.Value} category");
                Assert.AreEqual(oldSpell.School, newSpell.School, $"{kv.Key} -> {kv.Value} school");
                Assert.AreEqual(oldSpell.MetaSpellType, newSpell.MetaSpellType, $"{kv.Key} -> {kv.Value} meta type");
                Assert.AreEqual(oldSpell.DegradeModifier, newSpell.DegradeModifier, $"{kv.Key} -> {kv.Value} degrade modifier");
                Assert.AreEqual(oldSpell.DegradeLimit, newSpell.DegradeLimit, $"{kv.Key} -> {kv.Value} degrade limit");
                Assert.AreEqual(newSpell.Power, (uint)power[kv.Value], $"{kv.Value} power in the migration");
            }
        }

        // ---------- numeric values ----------

        [TestMethod]
        public void ShardMigration_NumericValues_MatchTheWeenieTemplates()
        {
            var statements = Statements();

            // (wcid range, table, property type, template value) - every row the migration raises.
            var cases = new (int Lo, int Hi, string Table, int Type, double Value)[]
            {
                (1005454, 1005454, "int", 44, 66),
                (1005455, 1005455, "int", 44, 84),
                (1005456, 1005456, "int", 44, 67),
                (1005457, 1005457, "int", 44, 55),
                (1005450, 1005450, "int", 204, 22),
                (1005450, 1005450, "float", 63, 2.8),
                (1005451, 1005453, "float", 152, 1.31),
            };

            foreach (var c in cases)
            {
                var scope = c.Lo == c.Hi ? $"= {c.Lo}" : $"BETWEEN {c.Lo} AND {c.Hi}";
                var alias = c.Table == "int" ? "bi" : "bf";
                var stmt = statements.SingleOrDefault(s => s.StartsWith($"UPDATE `biota_properties_{c.Table}`") && s.Contains($"{alias}.`type` = {c.Type} ") && s.Contains($"`weenie_Class_Id` {scope} "));
                Assert.IsNotNull(stmt, $"one statement raises {c.Table} {c.Type} on {scope}");

                var set = Regex.Match(stmt, $@"SET {alias}\.`value` = ([\d.]+)");
                Assert.AreEqual(c.Value, double.Parse(set.Groups[1].Value, CultureInfo.InvariantCulture), 1e-9, $"{c.Table} {c.Type} on {scope} SET value");

                // Guarded on "below the new value", never on an exact old value.
                var guard = c.Table == "int" ? $"{alias}.`value` < {set.Groups[1].Value};" : $"{alias}.`value` < {set.Groups[1].Value} - 1e-6;";
                Assert.IsTrue((stmt + ";").EndsWith(guard), $"{c.Table} {c.Type} on {scope} guard is '{guard}'");

                for (var wcid = c.Lo; wcid <= c.Hi; wcid++)
                {
                    var template = c.Table == "int" ? TemplateInt(wcid, c.Type) : TemplateFloat(wcid, c.Type);
                    Assert.AreEqual(c.Value, template, 1e-9, $"weenie {wcid} {c.Table} {c.Type} template equals the migration");
                }
            }

            // Awakener DamageType: Fire 16 -> Health 128, only where it is still 16.
            var damageType = statements.Single(s => s.Contains("bi.`type` = 45 "));
            StringAssert.Contains(damageType, "SET bi.`value` = 128");
            StringAssert.Contains(damageType, "`weenie_Class_Id` = 1005453 AND bi.`value` = 16");
            Assert.AreEqual(128, TemplateInt(1005453, 45));

            // The bow's own Damage (44) is untouched, and no statement reaches outside the Bluespire block.
            Assert.AreEqual(22, TemplateInt(1005450, 44));
            foreach (var s in statements)
                foreach (Match m in Regex.Matches(s, @"\b100\d{4}\b"))
                {
                    var id = int.Parse(m.Value);
                    Assert.IsTrue((id >= 1005450 && id <= 1005461) || id == 1005493, $"{id} is a Bluespire weapon or stone, in: {Head(s)}");
                }
        }

        [TestMethod]
        public void MeleeDamage_IsTheTier8LootMaximumPlusTenIron()
        {
            // OD+10 = the tier-8 loot maximum Damage of the weapon's own type: base Mutation #0 "Damage = N" plus the
            // largest "Damage += N" in a mutation whose tier-8 chance is non-zero. Iron adds +1 per tinker, ten tinkers.
            foreach (var (wcid, file) in new[] {
                (1005454, "light_finesse_mace.txt"), (1005455, "heavy_axe.txt"),
                (1005456, "light_finesse_staff.txt"), (1005457, "two_handed_cleaver.txt") })
            {
                var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Entity", "Mutations", "MeleeWeapons", "Damage_WieldDifficulty_DamageVariance", file);
                var lines = File.ReadAllLines(path);

                var baseDamage = int.Parse(Regex.Match(string.Join("\n", lines), @"^\s*Damage = (\d+)", RegexOptions.Multiline).Groups[1].Value);
                var max = 0;
                var tier8 = false;
                foreach (var line in lines)
                {
                    var tiers = Regex.Match(line, @"^Tier chances: (.*)$");
                    if (tiers.Success)
                        tier8 = tiers.Groups[1].Value.Split(',')[7].Trim() != "0";

                    var add = Regex.Match(line, @"Damage \+= (\d+)");
                    if (tier8 && add.Success)
                        max = Math.Max(max, int.Parse(add.Groups[1].Value));
                }

                Assert.AreEqual(baseDamage + max + 10, TemplateInt(wcid, 44), $"{wcid} Damage = {file} T8 max ({baseDamage}+{max}) + 10 Iron");
            }
        }

        // ---------- stone text ----------

        [TestMethod]
        public void ShardMigration_StoneText_MatchesTheTemplates_AndNamesTheLegendarySpell()
        {
            var sql = MigrationSql();

            foreach (var (wcid, spell) in new[] { (1005460, "Legendary Blood Thirst"), (1005461, "Legendary Spirit Thirst"), (1005493, "Legendary Defender") })
            {
                foreach (var type in new[] { 14, 16 })
                {
                    var m = Regex.Match(WeenieSql(wcid), $@"\(\s*{wcid},\s*{type},\s*'((?:[^']|'')*)'\)");
                    Assert.IsTrue(m.Success, $"{wcid} has a string {type} row");
                    StringAssert.Contains(m.Groups[1].Value, spell, $"{wcid} string {type} names {spell}");
                    Assert.IsFalse(m.Groups[1].Value.Contains("Epic"), $"{wcid} string {type} no longer says Epic");

                    var stmt = Statements().SingleOrDefault(s => s.Contains($"bs.`type` = {type} AND b.`weenie_Class_Id` = {wcid}"));
                    Assert.IsNotNull(stmt, $"migration sets {wcid} string {type}");
                    StringAssert.Contains(stmt, $"'{m.Groups[1].Value}'", $"{wcid} string {type} migration text equals the template");
                }
            }

            Assert.IsFalse(Regex.IsMatch(StripComments(sql), @"'[^']*Epic[^']*'"), "no string literal in the migration says Epic");
        }

        // ---------- helpers ----------

        private static string MigrationSql()
        {
            var path = Path.Combine(RepoRoot(), "Database", "Updates", "Shard", MigrationFile);
            Assert.IsTrue(File.Exists(path), $"{path} exists");
            return File.ReadAllText(path);
        }

        private static string StripComments(string sql) => Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline);

        private static List<string> Statements()
            => StripComments(MigrationSql()).Split(';').Select(s => Regex.Replace(s.Trim(), @"\s+", " ")).Where(s => s.Length > 0).ToList();

        private static string Head(string s) => s.Length > 90 ? s.Substring(0, 90) : s;

        private static List<int> Ints(string csv) => csv.Split(',').Select(x => int.Parse(x.Trim())).ToList();

        /// <summary>The first id-to-id CASE in a statement (the spell rename).</summary>
        private static Dictionary<int, int> IdCase(string statement)
        {
            var start = statement.Contains("keep.`spell") ? statement.IndexOf("= CASE", StringComparison.Ordinal) : statement.LastIndexOf("= CASE", StringComparison.Ordinal);
            var end = statement.IndexOf("END", start, StringComparison.Ordinal);
            return CaseMap.Matches(statement.Substring(start, end - start)).Cast<Match>()
                .ToDictionary(m => int.Parse(m.Groups[1].Value), m => int.Parse(m.Groups[2].Value));
        }

        private static Dictionary<int, double> CaseValues(string statement, string marker)
        {
            var start = statement.IndexOf(marker, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"'{marker}' present");
            var end = statement.IndexOf("END", start, StringComparison.Ordinal);
            return CaseMap.Matches(statement.Substring(start, end - start)).Cast<Match>()
                .ToDictionary(m => int.Parse(m.Groups[1].Value), m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        private static Dictionary<int, int> SpellBook(string sql, int wcid)
            => Regex.Matches(Section(sql, "weenie_properties_spell_book"), $@"\(\s*{wcid},\s*(\d+),\s*(\d+)\)").Cast<Match>()
                .ToDictionary(m => int.Parse(m.Groups[1].Value), m => int.Parse(m.Groups[2].Value));

        private static int TemplateInt(int wcid, int type)
        {
            var sql = WeenieSql(wcid);
            var section = Section(sql, "weenie_properties_int");
            var m = Regex.Match(section, $@"\(\s*{wcid},\s*{type},\s*(-?\d+)\)");
            Assert.IsTrue(m.Success, $"{wcid} has int {type}");
            return int.Parse(m.Groups[1].Value);
        }

        private static double TemplateFloat(int wcid, int type)
        {
            var sql = WeenieSql(wcid);
            var section = Section(sql, "weenie_properties_float");
            var m = Regex.Match(section, $@"\(\s*{wcid},\s*{type},\s*(-?[\d.]+)\)");
            Assert.IsTrue(m.Success, $"{wcid} has float {type}");
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static string Section(string sql, string table)
        {
            var start = sql.IndexOf($"INSERT INTO `{table}`", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"{table} insert present");
            var end = sql.IndexOf("INSERT INTO `", start + 1, StringComparison.Ordinal);
            return end < 0 ? sql.Substring(start) : sql.Substring(start, end - start);
        }

        private static string WeenieSql(int wcid)
        {
            var dir = Path.Combine(ContentDir(), "sql", "weenies");
            var file = Directory.GetFiles(dir, $"{wcid} *.sql").SingleOrDefault();
            Assert.IsNotNull(file, $"one weenie file for {wcid} in {dir}");
            return File.ReadAllText(file);
        }

        private static string ContentDir() => Path.Combine(RepoRoot(), "Content");

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Database", "Updates", "Shard", MigrationFile)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Database/Updates/Shard/{MigrationFile} by walking up from {AppContext.BaseDirectory}.");
            return dir.FullName;
        }

        private static readonly object initLock = new object();
        private static bool initialized;
        private static string skipReason;

        private static void RequireDats()
        {
            lock (initLock)
            {
                if (!initialized)
                {
                    initialized = true;
                    skipReason = InitializeDats();
                }
            }

            if (skipReason != null)
                Assert.Inconclusive(skipReason);
        }

        private static string InitializeDats()
        {
            if (DatManager.PortalDat != null)
                return null;

            var candidates = new List<string>();

            var fromEnv = Environment.GetEnvironmentVariable("ACE_DAT_PATH");
            if (!string.IsNullOrWhiteSpace(fromEnv))
                candidates.Add(fromEnv);

            candidates.Add(@"C:\ACE\Dats\");
            candidates.Add(@"C:\Turbine\Asheron's Call\");

            var datDir = candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "client_portal.dat")));
            if (datDir == null)
                return $"Skipped: client_portal.dat not found in any of [{string.Join(", ", candidates)}]. Set ACE_DAT_PATH to a directory containing the client .dat files.";

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                DatManager.Initialize(datDir, true, false);
            }
            catch (Exception ex)
            {
                return $"Skipped: loading the dats from {datDir} failed: {ex.Message}";
            }

            return DatManager.PortalDat == null ? $"Skipped: {datDir} did not produce a PortalDat." : null;
        }
    }
}
