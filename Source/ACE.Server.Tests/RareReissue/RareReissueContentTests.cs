using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Factories;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.RareReissue
{
    /// <summary>
    /// Content pins for the 28 upgraded retail rare armor and jewelry reissues, wcids 1008400-1008427
    /// (Docs/superpowers/specs/2026-11-16-rare-armor-jewelry-reintroduction-design.md). Pure file + regex parsing,
    /// same idiom as KnotLootContentTests.
    /// </summary>
    [TestClass]
    public class RareReissueContentTests
    {
        private static readonly (uint Clone, uint Source, string Name, bool IsArmor, int RetailArmorLevel, int[] Spells)[] Items =
        {
            (1008400, 30528, "Helm of Leikotha's Tears", true, 525, new[] { 6105, 4407, 6064 }),
            (1008401, 30521, "Pauldrons of Leikotha's Tears", true, 525, new[] { 4407, 6077, 6041 }),
            (1008402, 30520, "Breastplate of Leikotha's Tears", true, 525, new[] { 6104, 4407, 6054 }),
            (1008403, 30518, "Girth of Leikotha's Tears", true, 525, new[] { 4407, 6063, 6102 }),
            (1008404, 30523, "Bracers of Leikotha's Tears", true, 525, new[] { 6107, 4407, 6051 }),
            (1008405, 30525, "Gauntlets of Leikotha's Tears", true, 525, new[] { 6103, 4407, 6053 }),
            (1008406, 30516, "Tassets of Leikotha's Tears", true, 525, new[] { 4407, 6078, 6102 }),
            (1008407, 30513, "Greaves of Leikotha's Tears", true, 525, new[] { 4407, 6076, 6055 }),
            (1008408, 30511, "Gelidite Mitre", true, 525, new[] { 6105, 4407, 6064 }),
            (1008409, 30522, "Gelidite Pauldrons", true, 525, new[] { 4407, 6077, 6041 }),
            (1008410, 30519, "Gelidite Breastplate", true, 525, new[] { 6104, 4407, 6054 }),
            (1008411, 30517, "Gelidite Girth", true, 525, new[] { 4407, 6063, 6102 }),
            (1008412, 30524, "Gelidite Bracers", true, 525, new[] { 6107, 4407, 6051 }),
            (1008413, 30526, "Gelidite Gauntlets", true, 525, new[] { 6103, 4407, 6053 }),
            (1008414, 30515, "Gelidite Tassets", true, 525, new[] { 6101, 4407, 6078 }),
            (1008415, 30514, "Gelidite Greaves", true, 525, new[] { 4407, 6076, 6055 }),
            (1008416, 30529, "Gelidite Boots", true, 525, new[] { 6106, 4407, 6071 }),
            (1008417, 30532, "Dusk Coat", true, 500, new[] { 6107, 6104, 4407, 6051, 6102 }),
            (1008418, 30530, "Dusk Leggings", true, 500, new[] { 6101, 4407, 6078, 6076 }),
            (1008419, 30533, "Patriarch's Twilight Coat", true, 500, new[] { 6107, 6104, 4407, 6051, 6102 }),
            (1008420, 30531, "Patriarch's Twilight Tights", true, 500, new[] { 6101, 4407, 6078, 6076 }),
            (1008421, 30354, "Band of Elemental Harmony", false, 0, new[] { 6080, 6082, 6083, 6079 }),
            (1008422, 30355, "Love's Favor", false, 0, new[] { 4299, 4498, 4608, 6084, 6051 }),
            (1008423, 30356, "Circle of Pure Thought", false, 0, new[] { 6105, 6101, 4305, 4329, 4494, 4498 }),
            (1008424, 30358, "Necklace of Iniquity", false, 0, new[] { 4299, 4496, 6077, 5418, 6074 }),
            (1008425, 30359, "Ibriya's Choice", false, 0, new[] { 6106, 6104, 4297, 4299, 4319, 4325 }),
            (1008426, 30362, "Swift Strike Ring", false, 0, new[] { 6106, 4319, 4560, 4616, 6055, 6071 }),
            (1008427, 30365, "Winter's Heart Ring", false, 0, new[] { 6107, 6104, 4299, 4325, 4466 }),
        };

        // The 24 Epic ids the spec appendix maps to Legendary.
        private static readonly int[] EpicIds =
        {
            3963, 3964, 3965, 4019, 4226, 4227, 4673, 4675, 4676, 4677, 4679, 4680,
            4681, 4682, 4684, 4692, 4694, 4695, 4696, 4704, 4705, 4710, 4911, 5429,
        };

        private const int NameProp = 1;
        private const int RareIdProp = 17;
        private const int ArmorLevelProp = 28;
        private const int ItemWorkmanshipProp = 105;
        private const int WieldRequirementsProp = 158;
        private const int WieldSkillTypeProp = 159;
        private const int WieldDifficultyProp = 160;
        private const int EquipmentSetIdProp = 265;
        private const int ItemMaxLevelProp = 319;
        private const int ItemXpStyleProp = 320;
        private const int GearCritDamageProp = 374;
        private const int GearMaxHealthProp = 379;
        private const int ModSalvageAllowMaskProp = 9095;
        private const int TinkerLockedProp = 9068;
        private const int ObsidianAndTailoringMask = 12;

        [TestMethod]
        public void EachClone_SpellbookIsExactlyTheMappedSet()
        {
            foreach (var item in Items)
            {
                var spells = Rows(item.Clone, "weenie_properties_spell_book").Keys.OrderBy(i => i).ToArray();
                CollectionAssert.AreEqual(item.Spells.OrderBy(i => i).ToArray(), spells, $"{item.Clone} {item.Name} spellbook");
            }
        }

        [TestMethod]
        public void EachClone_SpellbookHasNoDuplicateRows()
        {
            foreach (var item in Items)
            {
                var text = Regex.Replace(Sql(item.Clone), @"/\*.*?\*/", " ", RegexOptions.Singleline);
                var block = Regex.Match(text, @"INSERT\s+INTO\s+`weenie_properties_spell_book`.*?;\s*$", RegexOptions.Singleline | RegexOptions.Multiline).Value;
                Assert.AreEqual(item.Spells.Length, Regex.Matches(block, @"\(\s*" + item.Clone + @"\s*,").Count, $"{item.Clone} spellbook row count");
            }
        }

        // Retail sets these physics/usability bools True on every one of the 28 sources that carries them
        // (Inscribable 22 on all 28); a clone must never carry them as False.
        private static readonly int[] RetailTrueBools = { 11, 13, 14, 19, 22 };

        [TestMethod]
        public void EachClone_RetailTrueBoolsStayTrue()
        {
            foreach (var item in Items)
            {
                var bools = Rows(item.Clone, "weenie_properties_bool");
                Assert.AreEqual("True", Get(bools, 22), $"{item.Clone} {item.Name} Inscribable");
                foreach (var prop in RetailTrueBools.Where(bools.ContainsKey))
                    Assert.AreEqual("True", bools[prop], $"{item.Clone} {item.Name} PropertyBool {prop}");
            }
        }

        [TestMethod]
        public void EachClone_CarriesNoEpicSpellIds()
        {
            foreach (var item in Items)
            {
                var spells = Rows(item.Clone, "weenie_properties_spell_book").Keys;
                Assert.IsFalse(spells.Any(s => EpicIds.Contains(s)), $"{item.Clone} {item.Name} still carries an Epic spell");
            }
        }

        [TestMethod]
        public void EachArmorClone_CritDamage2_Workmanship10_ObsidianAndTailoringMask()
        {
            foreach (var item in Items.Where(i => i.IsArmor))
            {
                var ints = Rows(item.Clone, "weenie_properties_int");
                Assert.AreEqual("2", Get(ints, GearCritDamageProp), $"{item.Clone} GearCritDamage");
                Assert.AreEqual("10", Get(ints, ItemWorkmanshipProp), $"{item.Clone} ItemWorkmanship");
                Assert.AreEqual(ObsidianAndTailoringMask.ToString(), Get(ints, ModSalvageAllowMaskProp), $"{item.Clone} ModSalvageAllowMask");
                Assert.IsFalse(ints.ContainsKey(GearMaxHealthProp), $"{item.Clone} armor must not carry GearMaxHealth");
            }
        }

        [TestMethod]
        public void EachJewelryClone_Vitality3_NoWorkmanship_NoMask()
        {
            foreach (var item in Items.Where(i => !i.IsArmor))
            {
                var ints = Rows(item.Clone, "weenie_properties_int");
                Assert.AreEqual("3", Get(ints, GearMaxHealthProp), $"{item.Clone} GearMaxHealth");
                Assert.IsFalse(ints.ContainsKey(ItemWorkmanshipProp), $"{item.Clone} jewelry must not carry ItemWorkmanship");
                Assert.IsFalse(ints.ContainsKey(ModSalvageAllowMaskProp), $"{item.Clone} jewelry must not carry ModSalvageAllowMask");
                Assert.IsFalse(ints.ContainsKey(GearCritDamageProp), $"{item.Clone} jewelry must not carry GearCritDamage");
            }
        }

        [TestMethod]
        public void EachClone_NoRareId_NoLock_NoWield()
        {
            var forbiddenInts = new[] { RareIdProp, WieldRequirementsProp, WieldSkillTypeProp, WieldDifficultyProp };

            foreach (var item in Items)
            {
                var ints = Rows(item.Clone, "weenie_properties_int");
                foreach (var prop in forbiddenInts)
                    Assert.IsFalse(ints.ContainsKey(prop), $"{item.Clone} {item.Name} carries forbidden PropertyInt {prop}");

                Assert.IsFalse(Rows(item.Clone, "weenie_properties_bool").ContainsKey(TinkerLockedProp), $"{item.Clone} carries TinkerLocked");
            }
        }

        // Owner ruling 2026-11-17: the armor keeps the retail Heroic Protector rare set (EquipmentSet 40,
        // RareDamageResistance) and the retail item leveling (max level 50, XP style 1, 2,000,000,000 base XP).
        private const int HeroicProtectorSet = 40;
        private const int ItemTotalXpProp = 4;
        private const int ItemBaseXpProp = 5;

        [TestMethod]
        public void EachArmorClone_KeepsHeroicProtectorSetAndItemLeveling()
        {
            foreach (var item in Items.Where(i => i.IsArmor))
            {
                var ints = Rows(item.Clone, "weenie_properties_int");
                Assert.AreEqual(HeroicProtectorSet.ToString(), Get(ints, EquipmentSetIdProp), $"{item.Clone} {item.Name} EquipmentSetId");
                Assert.AreEqual("50", Get(ints, ItemMaxLevelProp), $"{item.Clone} {item.Name} ItemMaxLevel");
                Assert.AreEqual("1", Get(ints, ItemXpStyleProp), $"{item.Clone} {item.Name} ItemXpStyle");

                var int64 = Rows(item.Clone, "weenie_properties_int64");
                Assert.AreEqual("0", Get(int64, ItemTotalXpProp), $"{item.Clone} {item.Name} ItemTotalXp");
                Assert.AreEqual("2000000000", Get(int64, ItemBaseXpProp), $"{item.Clone} {item.Name} ItemBaseXp");
                Assert.AreEqual(2, int64.Count, $"{item.Clone} {item.Name} int64 row count");
            }
        }

        // Retail jewelry carries no set and no leveling, except the int64 XP rows on 30356 and 30359, which are kept as retail.
        private static readonly uint[] JewelryWithRetailXpRows = { 1008423, 1008425 };

        [TestMethod]
        public void EachJewelryClone_SetAndLevelingRowsMatchRetail()
        {
            foreach (var item in Items.Where(i => !i.IsArmor))
            {
                var ints = Rows(item.Clone, "weenie_properties_int");
                Assert.IsFalse(ints.ContainsKey(EquipmentSetIdProp), $"{item.Clone} EquipmentSetId");
                Assert.IsFalse(ints.ContainsKey(ItemMaxLevelProp), $"{item.Clone} ItemMaxLevel");
                Assert.IsFalse(ints.ContainsKey(ItemXpStyleProp), $"{item.Clone} ItemXpStyle");

                var int64 = Rows(item.Clone, "weenie_properties_int64");
                if (JewelryWithRetailXpRows.Contains(item.Clone))
                {
                    Assert.AreEqual("0", Get(int64, ItemTotalXpProp), $"{item.Clone} ItemTotalXp");
                    Assert.AreEqual("2000000000", Get(int64, ItemBaseXpProp), $"{item.Clone} ItemBaseXp");
                }
                else
                {
                    Assert.AreEqual(0, int64.Count, $"{item.Clone} int64 rows");
                }
            }
        }

        // Owner requirement: every jewelry reissue takes the Jeweler's Aspect gem (1008000) and Green Jade (Hammer 1008001).
        private const uint JewelersAspectGem = 1008000;
        private const uint GreenJadeHammer = 1008001;
        private const int TargetTypeProp = 94;

        [TestMethod]
        public void EachJewelryClone_IsAspectablePiece_ForJewelersAspectAndGreenJade()
        {
            // Both tools target Jewelry; the client and Player_Use refuse a target whose ItemType misses the tool's TargetType.
            foreach (var tool in new[] { JewelersAspectGem, GreenJadeHammer })
                Assert.AreNotEqual(0, int.Parse(Get(Rows(tool, "weenie_properties_int"), TargetTypeProp)) & (int)ACE.Entity.Enum.ItemType.Jewelry, $"tool {tool} TargetType");

            foreach (var item in Items.Where(i => !i.IsArmor))
            {
                var ints = Rows(item.Clone, "weenie_properties_int");
                var itemType = (ACE.Entity.Enum.ItemType)int.Parse(Get(ints, 1));
                var locations = (ACE.Entity.Enum.EquipMask)int.Parse(Get(ints, 9));

                // The same predicate both JewelryAspectApplicator and GreenJadeManager gate on.
                var piece = JewelryAspectApplicatorTests.Jewelry(item.Name, locations, type: itemType);
                Assert.IsTrue(ACE.Server.Entity.JewelryAspectMath.IsAspectablePiece(piece), $"{item.Clone} {item.Name} is not an aspectable ring, bracelet or necklace");
                Assert.IsFalse(ACE.Server.Entity.JewelryAspectMath.IsAspected(piece), $"{item.Clone} {item.Name} must drop unaspected so the gem can add the first aspect");

                // Nothing on the piece that the TinkerLock gate (Player_Use, RecipeManager) would refuse with.
                var locked = Rows(item.Clone, "weenie_properties_bool").TryGetValue(TinkerLockedProp, out var lockText) && lockText == "True";
                int? mask = ints.TryGetValue(ModSalvageAllowMaskProp, out var maskText) ? int.Parse(maskText) : null;
                Assert.IsFalse(ACE.Server.Entity.TinkerLock.IsRefused(locked, sourceIsManaStone: false, hasCookbookRow: false), $"{item.Clone} TinkerLocked refuses the gem and the jade");
                Assert.IsFalse(ACE.Server.Entity.TinkerLock.ModSalvageMaskRefuses(mask, false, false, ACE.Server.Entity.TinkerLock.ModSalvageKind.Other, true, true, false), $"{item.Clone} salvage mask refuses the gem and the jade");
                Assert.IsFalse(ints.ContainsKey(9100) || ints.ContainsKey(9101) || ints.ContainsKey(9102), $"{item.Clone} carries aspect counts");
            }
        }

        [TestMethod]
        public void EachArmorClone_ArmorLevelIsRetail()
        {
            foreach (var item in Items.Where(i => i.IsArmor))
                Assert.AreEqual(item.RetailArmorLevel.ToString(), Get(Rows(item.Clone, "weenie_properties_int"), ArmorLevelProp), $"{item.Clone} {item.Name} ArmorLevel");
        }

        [TestMethod]
        public void EachClone_NameIsRetailName()
        {
            foreach (var item in Items)
                Assert.AreEqual(item.Name.Replace("'", "''"), Get(Rows(item.Clone, "weenie_properties_string"), NameProp), $"{item.Clone} Name");
        }

        [TestMethod]
        public void RareTier4_ContainsEveryClone()
        {
            foreach (var item in Items)
                Assert.AreEqual(4, LootGenerationFactory.GetRareTier(item.Clone), $"{item.Clone} {item.Name} is not in rare tier 4");
        }

        [TestMethod]
        public void NoRareTier_ContainsAnyReissuedSourceWcid()
        {
            foreach (var item in Items)
                Assert.AreEqual(0, LootGenerationFactory.GetRareTier(item.Source), $"retail {item.Source} {item.Name} is still in a rare tier");
        }

        [TestMethod]
        public void RegistryFragment_Owns1008400Block()
        {
            var file = Path.Combine(Directory.GetParent(WeeniesDir()).Parent.FullName, "wcid-registry.d", "wcid-1008400.tsv");
            Assert.IsTrue(File.Exists(file), $"missing {file}");

            var row = File.ReadAllLines(file).Select(l => l.Split('\t')).Single(c => c.Length > 1 && c[1] == "1008400");
            Assert.AreEqual("wcid", row[0]);
            Assert.AreEqual("1008449", row[2]);
            Assert.AreEqual("rare-reissue", row[4]);
            Assert.AreEqual("active", row[6]);
        }

        // ---- helpers (same idiom as KnotLootContentTests) ----

        private static string Get(Dictionary<int, string> rows, int prop)
        {
            Assert.IsTrue(rows.TryGetValue(prop, out var value), $"missing property {prop}");
            return value;
        }

        private static string Sql(uint wcid)
        {
            var file = Directory.EnumerateFiles(WeeniesDir(), wcid + " *.sql", SearchOption.TopDirectoryOnly).FirstOrDefault();
            Assert.IsNotNull(file, $"no weenie SQL starting '{wcid} ' under {WeeniesDir()}");
            return File.ReadAllText(file);
        }

        /// <summary>Rows of one table's INSERT blocks as type -> value text (strings unquoted; comments removed).</summary>
        private static Dictionary<int, string> Rows(uint wcid, string table)
        {
            var result = new Dictionary<int, string>();
            var text = Regex.Replace(Sql(wcid), @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"^\s*--.*$", " ", RegexOptions.Multiline);

            foreach (Match block in Regex.Matches(text, @"INSERT\s+INTO\s+`" + table + @"`.*?;\s*$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Multiline))
            {
                foreach (Match row in Regex.Matches(block.Value, @"\(\s*" + wcid + @"\s*,\s*(\d+)\s*,\s*('(?:[^']|'')*'|[^)]*?)\s*\)"))
                {
                    var value = row.Groups[2].Value.Trim();
                    if (value.StartsWith("'") && value.EndsWith("'") && value.Length >= 2)
                        value = value.Substring(1, value.Length - 2);
                    result[int.Parse(row.Groups[1].Value)] = value;
                }
            }

            return result;
        }

        private static string WeeniesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(candidate))
                    return candidate;

                dir = dir.Parent;
            }

            Assert.Fail($"Could not find Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
