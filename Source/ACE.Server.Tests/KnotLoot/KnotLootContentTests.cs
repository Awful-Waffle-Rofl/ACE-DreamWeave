using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Server.Entity;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// Content pins for the four Grael rare items (Docs/Knots/KNOT-LOOT-DESIGN.md section 5): wcids 1007450-1007453. Pure
    /// file + regex parsing, same idiom as BluespireTinkerLockContentTests. The tinker-lock refusal text for a Knot item
    /// is pinned here too, through the pure RefusalMessage overload.
    /// </summary>
    [TestClass]
    public class KnotLootContentTests
    {
        private const uint Orb = 1007450;
        private const uint Shield = 1007451;
        private const uint Gauntlets = 1007452;
        private const uint Bracelet = 1007453;

        private static readonly uint[] AllWcids = { Orb, Shield, Gauntlets, Bracelet };

        private const int ValidLocationsProp = 9;
        private const int ItemUseableProp = 16;
        private const int RareIdProp = 17;
        private const int ArmorLevelProp = 28;
        private const int BondedProp = 33;
        private const int AttunedProp = 114;
        private const int WieldRequirementsProp = 158;
        private const int WieldSkillTypeProp = 159;
        private const int WieldDifficultyProp = 160;
        private const int EquipmentSetIdProp = 265;
        private const int TinkerLockedProp = 9068;
        private const int KnotLootLevelProp = 9093;
        private const int SpellProp = 28;

        // Owner ruling 2026-10-23: a flat 500 (+200 on the 2026-10-21 ruling of 300).
        private const int GauntletArmorLevel = 500;
        private const int WristWear = 0x30000;

        [TestMethod]
        public void EachItem_TinkerLocked_Level100_NoBondNoRareSet()
        {
            foreach (var wcid in AllWcids)
            {
                var ints = Rows(wcid, "weenie_properties_int");
                var bools = Rows(wcid, "weenie_properties_bool");
                var floats = Rows(wcid, "weenie_properties_float");

                Assert.AreEqual("True", bools[TinkerLockedProp], $"{wcid} TinkerLocked");
                Assert.AreEqual("7", ints[WieldRequirementsProp], $"{wcid} WieldRequirements = Level");
                Assert.AreEqual("1", ints[WieldSkillTypeProp], $"{wcid} WieldSkillType");
                Assert.AreEqual("100", ints[WieldDifficultyProp], $"{wcid} WieldDifficulty");

                Assert.IsFalse(ints.ContainsKey(BondedProp), $"{wcid} must not be Bonded");
                Assert.IsFalse(ints.ContainsKey(AttunedProp), $"{wcid} must not be Attuned");
                Assert.IsFalse(ints.ContainsKey(RareIdProp), $"{wcid} must not carry RareId");
                Assert.IsFalse(bools.ContainsKey(108), $"{wcid} must not carry RareUsesTimer");
                Assert.IsFalse(floats.ContainsKey(154), $"{wcid} must not carry LastRareUsedTimestamp");

                if (wcid == Gauntlets)
                    Assert.AreEqual("30", ints[EquipmentSetIdProp]);
                else
                    Assert.IsFalse(ints.ContainsKey(EquipmentSetIdProp), $"{wcid} must carry no EquipmentSetId");
            }
        }

        [TestMethod]
        public void MatronSpines_TradeableButBonded()
        {
            // Owner ruling 2026-10-31: Drift Matron Spines are tradeable (no Attuned row); Bonded stays so they never drop on death.
            var ints = Rows(1007602, "weenie_properties_int");
            Assert.IsFalse(ints.ContainsKey(AttunedProp), "1007602 must not be Attuned");
            Assert.AreEqual("1", ints[BondedProp], "1007602 Bonded");
        }

        [TestMethod]
        public void ShardMigration_ClearsAttunedFromExistingInfusionsAndSpinesOnly()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string sql = null;
            while (dir != null && sql == null)
            {
                var candidate = Path.Combine(dir.FullName, "Database", "Updates", "Shard", "2026-10-31-00-Infusions-And-Matron-Spines-Tradeable-Existing-Items.sql");
                if (File.Exists(candidate)) sql = File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            Assert.IsNotNull(sql, "the infusions/spines shard migration was not found by walking up from the test assembly");

            var statements = Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline).Split(';')
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            Assert.AreEqual(1, statements.Count, "one statement");
            StringAssert.StartsWith(statements[0], "DELETE bi FROM `biota_properties_int`");
            StringAssert.Contains(statements[0], "bi.`type` = 114");
            StringAssert.Contains(statements[0], "`weenie_Class_Id` IN (1007400, 1007401, 1007402, 1007602)");
        }

        [TestMethod]
        public void EachItem_SpellbookIsExactly()
        {
            var expected = new Dictionary<uint, int[]>
            {
                [Orb] = new[] { 2347, 4564, 4530, 4582, 4305, 4329, 6056, 6046, 6060, 6091 },
                [Shield] = new[] { 6095, 4407, 4393, 4412, 4397, 4401, 4403, 4391, 4409 },
                [Gauntlets] = new[] { 6101, 4299, 4329, 5149, 5152, 4906, 4068 },
                [Bracelet] = new[] { 6055, 6054, 6063 },
            };

            foreach (var pair in expected)
            {
                var actual = Rows(pair.Key, "weenie_properties_spell_book").Keys.OrderBy(k => k).ToArray();
                CollectionAssert.AreEqual(pair.Value.OrderBy(k => k).ToArray(), actual, $"{pair.Key} spellbook");
            }
        }

        [TestMethod]
        public void Shield_ArmorLevel350()
        {
            Assert.AreEqual("350", Rows(Shield, "weenie_properties_int")[ArmorLevelProp]);
        }

        [TestMethod]
        public void Gauntlets_ArmorLevel500_Set30()
        {
            var s = Sql(Gauntlets);
            StringAssert.Matches(s, new Regex(@"\(1007452,\s*28,\s*" + GauntletArmorLevel + @"\)"));
            StringAssert.Matches(s, new Regex(@"\(1007452,\s*265,\s*30\)"));
        }

        [TestMethod]
        public void Bracelet_BothWrists()
        {
            Assert.AreEqual(WristWear.ToString(), Rows(Bracelet, "weenie_properties_int")[ValidLocationsProp]);
        }

        [TestMethod]
        public void Orb_Brilliance2348_Defense118()
        {
            Assert.AreEqual(2348, Convert.ToInt32(Rows(Orb, "weenie_properties_d_i_d")[SpellProp], 16));
            Assert.AreEqual("1.18", Rows(Orb, "weenie_properties_float")[29]);
            Assert.AreEqual("6291464", Rows(Orb, "weenie_properties_int")[ItemUseableProp]);
        }

        [TestMethod]
        public void Orb_NoRetailRareBonuses_FullMana()
        {
            var ints = Rows(Orb, "weenie_properties_int");

            Assert.IsFalse(ints.ContainsKey(166), "orb must not carry SlayerCreatureType");
            Assert.IsFalse(ints.ContainsKey(179), "orb must not carry ImbuedEffect");
            Assert.IsFalse(Rows(Orb, "weenie_properties_float").ContainsKey(138), "orb must not carry SlayerDamageBonus");
            Assert.IsFalse(Rows(Orb, "weenie_properties_float").ContainsKey(147), "orb must not carry CriticalFrequency (Biting Strike)");
            Assert.IsFalse(Rows(Orb, "weenie_properties_float").ContainsKey(152), "orb must not carry ElementalDamageMod (piercing bonus)");
            Assert.IsFalse(ints.ContainsKey(45), "orb must not carry DamageType (non-elemental)");
            Assert.AreEqual("13131", ints[107], "ItemCurMana");
            Assert.AreEqual("13131", ints[108], "ItemMaxMana");
            Assert.AreEqual("0.2", Rows(Orb, "weenie_properties_float")[144], "ManaConversionMod kept");
        }

        [TestMethod]
        public void EachItem_NoLevelingRows()
        {
            foreach (var wcid in AllWcids)
            {
                var ints = Rows(wcid, "weenie_properties_int");
                Assert.IsFalse(ints.ContainsKey(319), $"{wcid} must not carry ItemMaxLevel");
                Assert.IsFalse(ints.ContainsKey(320), $"{wcid} must not carry ItemXpStyle");
            }
        }

        [TestMethod]
        public void EachItem_LookMatchesSpec()
        {
            var look = new Dictionary<uint, (string Setup, string Icon, string Name)>
            {
                [Orb] = ("0x02001380", "0x06005C03", "Mirrorglass Orb"),
                [Shield] = ("0x0200137B", "0x06005BF6", "Shield of the Rose"),
                [Gauntlets] = ("0x02001383", "0x06005C0D", "Gauntlets of the Frugal Hand"),
                [Bracelet] = ("0x020000FB", "0x06005BDE", "Glass Turtle Bracelet"),
            };

            foreach (var pair in look)
            {
                var dids = Rows(pair.Key, "weenie_properties_d_i_d");
                Assert.IsTrue(string.Equals(pair.Value.Setup, dids[1], StringComparison.OrdinalIgnoreCase), $"{pair.Key} Setup {dids[1]}");
                Assert.IsTrue(string.Equals(pair.Value.Icon, dids[8], StringComparison.OrdinalIgnoreCase), $"{pair.Key} Icon {dids[8]}");
                Assert.AreEqual(pair.Value.Name, Rows(pair.Key, "weenie_properties_string")[1], $"{pair.Key} Name");
            }
        }

        [TestMethod]
        public void NoItem_CarriesRollStamps()
        {
            foreach (var wcid in AllWcids)
            {
                var floats = Rows(wcid, "weenie_properties_float");

                foreach (var id in new[] { 9015, 9016, 9017, 9018 })
                    Assert.IsFalse(floats.ContainsKey(id), $"{wcid} must not carry trait float {id}");

                Assert.IsFalse(Rows(wcid, "weenie_properties_int").ContainsKey(KnotLootLevelProp), $"{wcid} KnotLootLevel");
                Assert.IsFalse(Rows(wcid, "weenie_properties_string").ContainsKey(9029), $"{wcid} KnotLootTable");
            }
        }

        [TestMethod]
        public void KnotItem_TinkerRefusal_UsesKnotText()
        {
            const string expected = "The glass and thread of this piece will not take salvage.";

            Assert.AreEqual(expected, TinkerLock.KnotRefusalText);
            Assert.AreEqual(expected, TinkerLock.RefusalMessage(5u, false, "Foo", targetIsKnotItem: true));
            Assert.AreEqual(expected, TinkerLock.RefusalMessage(null, false, "Foo", targetIsKnotItem: true));

            // Not a Knot item: the Siraluun line is unchanged.
            Assert.AreEqual("The Foo is bound by Siraluun craft and cannot be altered by that.", TinkerLock.RefusalMessage(5u, false, "Foo"));
        }

        // ---- helpers ----

        private static string Sql(uint wcid)
        {
            var file = Directory.EnumerateFiles(WeeniesDir(), wcid + " *.sql", SearchOption.TopDirectoryOnly).FirstOrDefault();
            Assert.IsNotNull(file, $"no weenie SQL starting '{wcid} ' under {WeeniesDir()}");
            return File.ReadAllText(file);
        }

        /// <summary>Rows of one INSERT block as type -> value text (strings collapse to their text; comments removed).</summary>
        private static Dictionary<int, string> Rows(uint wcid, string table)
        {
            var result = new Dictionary<int, string>();
            var text = Regex.Replace(Sql(wcid), @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"^\s*--.*$", " ", RegexOptions.Multiline);

            foreach (Match block in Regex.Matches(text, @"INSERT\s+INTO\s+`" + table + @"`.*?;", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var body = block.Value;

                foreach (Match row in Regex.Matches(body, @"\(\s*" + wcid + @"\s*,\s*(\d+)\s*,\s*('(?:[^']|'')*'|[^)]*?)\s*\)"))
                    result[int.Parse(row.Groups[1].Value)] = row.Groups[2].Value.Trim().Trim('\'');
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
