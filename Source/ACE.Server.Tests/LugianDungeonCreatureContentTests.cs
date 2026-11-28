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
    /// Content pins for the lugian dungeon creatures (Citadel 300 and Excavations 400), their
    /// rock clone, quest item and tally quest rows. One data row per dungeon over shared assertions, so a
    /// divergence between the two dungeons fails a test. Pure file + regex parsing of the committed SQL and JSON,
    /// same idiom as MatronBatonContentTests.
    /// </summary>
    [TestClass]
    public class LugianDungeonCreatureContentTests
    {
        private sealed class Dungeon
        {
            public string Name;
            public int Brute, Hurler, Shaman, Boss, Rock, Item;
            public int BruteMace, BossMace;
            public int TrashLevel, BossLevel;
            public int TrashXp, BossXp;
            public double MaxBossScale;
            public string Prefix;
            public int RockDamage, BruteMaceDamage, BossMaceDamage;
        }

        private static readonly Dungeon[] Dungeons =
        {
            new Dungeon
            {
                Name = "Citadel", Brute = 1008450, Hurler = 1008451, Shaman = 1008452, Boss = 1008453, Rock = 1008458, Item = 1008460,
                BruteMace = 1008475, BossMace = 1008476, TrashLevel = 300, BossLevel = 320, TrashXp = 4000000, BossXp = 100000000,
                MaxBossScale = 1.3, Prefix = "LugianCitadel", RockDamage = 540, BruteMaceDamage = 599, BossMaceDamage = 700,
            },
            new Dungeon
            {
                Name = "Excavations", Brute = 1008454, Hurler = 1008455, Shaman = 1008456, Boss = 1008457, Rock = 1008459, Item = 1008461,
                BruteMace = 1008477, BossMace = 1008478, TrashLevel = 400, BossLevel = 420, TrashXp = 15000000, BossXp = 375000000,
                MaxBossScale = 1.8, Prefix = "LugianExcavations", RockDamage = 900, BruteMaceDamage = 1480, BossMaceDamage = 1723,
            },
        };

        private static IEnumerable<(Dungeon D, string Arch, int Wcid)> Creatures(Dungeon d)
        {
            yield return (d, "Brute", d.Brute);
            yield return (d, "Hurler", d.Hurler);
            yield return (d, "Shaman", d.Shaman);
            yield return (d, "Boss", d.Boss);
        }

        private static IEnumerable<int> Trash(Dungeon d) => new[] { d.Brute, d.Hurler, d.Shaman };

        [TestMethod]
        public void Creatures_HaveHeadersLevelsXpAndLugianType()
        {
            foreach (var d in Dungeons)
            {
                foreach (var c in Creatures(d))
                {
                    var text = File.ReadAllText(GetFile(c.Wcid));
                    StringAssert.Contains(text, $"-- @unit: weenies/{c.Wcid}");
                    StringAssert.Contains(text, "-- @owner: lugian-dungeons");
                    StringAssert.Contains(text, $"-- @owns: wcid:{c.Wcid}");

                    var ints = ParseNumeric(c.Wcid, "weenie_properties_int");
                    var boss = c.Arch == "Boss";
                    Assert.AreEqual(boss ? d.BossLevel : d.TrashLevel, (int)ints[25], $"{c.Wcid} Level");
                    Assert.AreEqual(boss ? d.BossXp : d.TrashXp, (int)ints[146], $"{c.Wcid} XpOverride");
                    Assert.IsTrue(ints[2] == 5 || ints[2] == 70, $"{c.Wcid} CreatureType {ints[2]}");
                    Assert.AreEqual(boss ? d.BossXp / 4000 : d.TrashXp / 4000, (int)ints[332], $"{c.Wcid} LuminanceAward (XP/4000, Marae standard)");
                }
            }
        }

        [TestMethod]
        public void Creatures_CarryNoRandomWeaponNoEmotesAndATreasureProfile()
        {
            foreach (var d in Dungeons)
            {
                foreach (var c in Creatures(d))
                {
                    var text = File.ReadAllText(GetFile(c.Wcid));
                    var dids = ParseNumeric(c.Wcid, "weenie_properties_d_i_d");
                    Assert.IsFalse(dids.ContainsKey(32), $"{c.Wcid}: no DID 32 (random weapon).");
                    Assert.IsTrue(dids.ContainsKey(35), $"{c.Wcid}: DeathTreasureType (35) present.");
                    Assert.AreEqual(2000, (int)dids[35], $"{c.Wcid}: the shared tier-8 profile.");
                    // No emotes at all: the Chanter's Taunt (14) and the pack-mates' ReceiveLocalSignal (37) pack buffs were removed 2026-11-19.
                    Assert.AreEqual(0, Emotes(c.Wcid).Count, $"{c.Wcid}: no emote rows (pack buffs removed).");
                }
            }
        }

        [TestMethod]
        public void Trash_HaveAwareness15And10_AndDropTheirItemAtFivePercent()
        {
            foreach (var d in Dungeons)
            {
                foreach (var w in Trash(d))
                {
                    var floats = ParseNumeric(w, "weenie_properties_float");
                    Assert.AreEqual(15, floats[31], 1e-9, $"{w} VisualAwarenessRange");
                    Assert.AreEqual(10, floats[32], 1e-9, $"{w} AuralAwarenessRange");

                    var drops = CreateRows(w).Where(r => r.Dest == 9).ToList();
                    CollectionAssert.AreEqual(new[] { (d.Item, 0.075), (0, 0.925) }, drops.Select(r => (r.Wcid, r.Shade)).ToArray(), $"{w} drops");
                }
            }
        }

        [TestMethod]
        public void Hurler_WieldsTheBandRockClone()
        {
            foreach (var d in Dungeons)
            {
                var wield = CreateRows(d.Hurler).Where(r => r.Dest == 2).ToList();
                Assert.AreEqual(1, wield.Count, "one wielded item");
                Assert.AreEqual(d.Rock, wield[0].Wcid);

                var rock = File.ReadAllText(GetFile(d.Rock));
                StringAssert.Contains(rock, $"-- @owns: wcid:{d.Rock}");
                Assert.AreEqual(d.RockDamage, (int)ParseNumeric(d.Rock, "weenie_properties_int")[44], "Rock Damage = the value static-scale printed (Excavations hand-lowered 1615 -> 900, 2026-11-21)");
            }
        }

        [TestMethod]
        public void Brute_AndBoss_WieldTheirOwnMaceClones()
        {
            foreach (var d in Dungeons)
            {
                Assert.AreEqual(d.BruteMace, CreateRows(d.Brute).Single(r => r.Dest == 2).Wcid);
                Assert.AreEqual(d.BossMace, CreateRows(d.Boss).Single(r => r.Dest == 2).Wcid);
                Assert.IsTrue(File.ReadAllText(GetFile(d.BruteMace)).Contains($"-- @owns: wcid:{d.BruteMace}"));
                Assert.IsTrue(File.ReadAllText(GetFile(d.BossMace)).Contains($"-- @owns: wcid:{d.BossMace}"));
                Assert.AreEqual(d.BruteMaceDamage, (int)ParseNumeric(d.BruteMace, "weenie_properties_int")[44], "Brute mace Damage = the value static-scale printed");
                Assert.AreEqual(d.BossMaceDamage, (int)ParseNumeric(d.BossMace, "weenie_properties_int")[44], "Boss mace Damage = the value static-scale printed");
            }
        }

        [TestMethod]
        public void Shaman_HasPowerup35AiUsesManaAndTheSupportSpellBook()
        {
            foreach (var d in Dungeons)
            {
                Assert.AreEqual(3.5, ParseNumeric(d.Shaman, "weenie_properties_float")[34], 1e-9, "PowerupTime");
                Assert.IsTrue(ParseBools(d.Shaman)[6], "AiUsesMana");

                var rows = Regex.Matches(Section(d.Shaman, "weenie_properties_spell_book"), @"\(\s*" + d.Shaman + @",\s*(\d+),\s*([\d.]+)\)")
                    .Select(m => (int.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToArray();

                // Ids resolved by level: the 8th entry of SpellLevelProgression.BludgeonVulnerabilityOther / ImperilOther / ShockArc.
                var expected = new[] { (4477, 2.30), (4312, 2.30), (4427, 2.30) };
                CollectionAssert.AreEqual(expected, rows, "Bludgeoning Vulnerability VIII, Imperil VIII and Shock Arc VIII, in that order, at 2.30 each (P(cast) = 1 - 0.7^3)");
            }
        }

        private const int ArmorSelf8 = 4291;
        private const int LightningProtectionSelf8 = 4470;

        private static string ChantSignal(Dungeon d) => d.Name == "Citadel" ? "lugian_chant_citadel" : "lugian_chant_excavations";

        private static IEnumerable<int> PackMates(Dungeon d) => new[] { d.Brute, d.Hurler, d.Boss };

        // Emote rows of one weenie: (category, probability, quest string, [(action type, message, spell id)]).
        private static List<(int Category, double Prob, string Quest, List<(int Type, string Message, int Spell)> Actions)> Emotes(int wcid)
        {
            var text = File.ReadAllText(GetFile(wcid));
            var result = new List<(int, double, string, List<(int, string, int)>)>();
            var heads = Regex.Matches(text, @"INSERT INTO `weenie_properties_emote` [^;]*?VALUES \(" + wcid + @",\s*(\d+)[^,]*,\s*([\d.]+),\s*NULL,\s*NULL,\s*NULL,\s*(NULL|'[^']*'),[^;]*;", RegexOptions.Singleline);
            foreach (Match h in heads)
            {
                var acts = new List<(int, string, int)>();
                var after = text.Substring(h.Index + h.Length);
                var m = Regex.Match(after, @"INSERT INTO `weenie_properties_emote_action`[^;]*?VALUES(.*?);", RegexOptions.Singleline);
                Assert.IsTrue(m.Success, $"{wcid}: emote action rows");
                foreach (var line in m.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var cols = Regex.Match(line, @"\(@parent_id,\s*(\d+),\s*(\d+)\s*/\*[^*]*\*/,\s*[^,]+,\s*[^,]+,\s*[^,]+,\s*(NULL|'[^']*'),(.*)\)");
                    Assert.IsTrue(cols.Success, $"{wcid}: action row '{line}'");
                    var rest = cols.Groups[4].Value.Split(',').Select(s => s.Trim()).ToArray();
                    // Columns after message: test_String min max min_64 max_64 min_Dbl max_Dbl stat display amount amount_64 hero_X_P_64 percent spell_Id
                    var spell = rest[13] == "NULL" ? 0 : int.Parse(rest[13]);
                    var msg = cols.Groups[3].Value == "NULL" ? null : cols.Groups[3].Value.Trim('\'');
                    acts.Add((int.Parse(cols.Groups[2].Value), msg, spell));
                }
                var quest = h.Groups[3].Value == "NULL" ? null : h.Groups[3].Value.Trim('\'');
                result.Add((int.Parse(h.Groups[1].Value), double.Parse(h.Groups[2].Value, CultureInfo.InvariantCulture), quest, acts));
            }
            return result;
        }

        [TestMethod]
        public void Chanters_CarryNoTauntSignalEmote_PackBuffsRemoved()
        {
            // Owner feedback 2026-11-19: the Chanter's Taunt -> LocalSignal pack buff made the resists too strong, so it is gone.
            foreach (var d in Dungeons)
            {
                var text = File.ReadAllText(GetFile(d.Shaman));
                Assert.AreEqual(0, Emotes(d.Shaman).Count, $"{d.Name} Chanter emotes");
                Assert.IsFalse(text.Contains("weenie_properties_emote"), $"{d.Name} Chanter has no emote tables");
                Assert.IsFalse(text.Contains(ChantSignal(d)), $"{d.Name} Chanter no longer emits '{ChantSignal(d)}'");
                Assert.IsFalse(ParseNumeric(d.Shaman, "weenie_properties_int").ContainsKey(290), "the Chanter does not listen either");
            }
        }

        [TestMethod]
        public void PackMates_NoLongerListenOrSelfBuff()
        {
            foreach (var d in Dungeons)
                foreach (var w in PackMates(d))
                {
                    var text = File.ReadAllText(GetFile(w));
                    var ints = ParseNumeric(w, "weenie_properties_int");
                    Assert.IsFalse(ints.ContainsKey(290), $"{w} HearLocalSignals removed");
                    Assert.IsFalse(ints.ContainsKey(291), $"{w} HearLocalSignalsRadius removed");
                    Assert.AreEqual(0, Emotes(w).Count, $"{w} ReceiveLocalSignal emote removed");
                    Assert.IsFalse(text.Contains("weenie_properties_emote"), $"{w} has no emote tables");
                    Assert.IsFalse(text.Contains(ChantSignal(d)), $"{w} no longer references '{ChantSignal(d)}'");
                    CollectionAssert.DoesNotContain(KeySet(w, "spell_book"), ArmorSelf8);
                    CollectionAssert.DoesNotContain(KeySet(w, "spell_book"), LightningProtectionSelf8);
                }
            Assert.AreNotEqual(ChantSignal(Dungeons[0]), ChantSignal(Dungeons[1]));
        }

        [TestMethod]
        public void NoLugianCarriesAnOtherBuff()
        {
            // A hostile monster's rolled Other buff lands on the player it fights, so every lugian spell must be a hostile one:
            // the Chanter book is the three pinned ids, and the pack-mates carry no spell book at all.
            foreach (var d in Dungeons)
            {
                foreach (var w in Creatures(d).Select(c => c.Wcid))
                {
                    var book = KeySet(w, "spell_book");
                    if (w == d.Shaman)
                        CollectionAssert.AreEquivalent(new[] { 4477, 4312, 4427 }, book, $"{w} spell book");
                    else
                        Assert.AreEqual(0, book.Length, $"{w} has no spell book");
                }
            }
        }

        [TestMethod]
        public void Boss_DropsExactlyOneItem_FitsItsScaleCap_AndOutHealthsItsBrute()
        {
            foreach (var d in Dungeons)
            {
                var drops = CreateRows(d.Boss).Where(r => r.Dest == 9).ToList();
                Assert.AreEqual(1, drops.Count);
                Assert.AreEqual(d.Item, drops[0].Wcid);
                Assert.AreEqual(1.0, drops[0].Shade, 1e-9);

                var scale = ParseNumeric(d.Boss, "weenie_properties_float")[39];
                Assert.IsTrue(scale <= d.MaxBossScale + 1e-9, $"boss scale {scale}");

                var ratio = (double)MaxHealth(d.Boss) / MaxHealth(d.Brute);
                Assert.IsTrue(ratio >= 14 && ratio <= 16, $"boss/brute MaxHealth ratio {ratio}");
            }
        }

        [TestMethod]
        public void KillQuests_NameTheirOwnKillsRows_AndEveryRowPairExists()
        {
            var solves = new Dictionary<string, int> { { "Brute", 500 }, { "Hurler", 250 }, { "Boss", 1 } };

            foreach (var d in Dungeons)
            {
                // Owner ruling 2026-11-23: the Chanter kill-tally quest is retired - no KillQuest tag, no quest units.
                Assert.IsFalse(ParseStrings(d.Shaman).ContainsKey(45), $"{d.Name} Chanter ({d.Shaman}) must carry no KillQuest");
                foreach (var key in new[] { d.Prefix + "Shaman", d.Prefix + "ShamanKills" })
                {
                    Assert.IsFalse(File.Exists(Path.Combine(RepoRoot(), "Content", "json", "quests", key + ".json")), key + " json unit must stay deleted");
                    Assert.IsFalse(File.Exists(Path.Combine(RepoRoot(), "Content", "sql", "quests", key + ".sql")), key + " sql unit must stay deleted");
                }

                foreach (var c in Creatures(d).Where(c => c.Arch != "Shaman"))
                {
                    Assert.AreEqual($"{d.Prefix}{c.Arch}Kills", ParseStrings(c.Wcid)[45], $"{c.Wcid} KillQuest");

                    AssertQuest(d.Prefix + c.Arch + "Kills", solves[c.Arch], 0);
                    AssertQuest(d.Prefix + c.Arch, -1, 72000);
                }
            }
        }

        [TestMethod]
        public void QuestItem_IsBondedStackableAndWorthless()
        {
            foreach (var d in Dungeons)
            {
                var ints = ParseNumeric(d.Item, "weenie_properties_int");
                StringAssert.Contains(File.ReadAllText(GetFile(d.Item)), $"-- @owns: wcid:{d.Item}");
                Assert.AreEqual(1, (int)ints[33], "Bonded");
                Assert.AreEqual(0, (int)ints[19], "Value");
                Assert.IsTrue(ints[11] > 1, "MaxStackSize");
                Assert.AreEqual(128, (int)ints[1], "ItemType Misc");
                StringAssert.Contains(File.ReadAllText(GetFile(d.Item)), "', 51, '", "WeenieType Stackable (51)");
            }
        }

        [TestMethod]
        public void NoPlaceholderMarkersRemainAndNamesAreFinal()
        {
            foreach (var d in Dungeons)
            {
                foreach (var w in new[] { d.Brute, d.Hurler, d.Shaman, d.Boss, d.Item })
                {
                    var text = File.ReadAllText(GetFile(w));
                    Assert.IsFalse(text.Contains("COPY-" + "PLACEHOLDER"), $"{w}: placeholder marker left in the file.");
                    StringAssert.Contains(text, "') /* Name */", $"{w}: Name line.");
                }
            }
        }

        [TestMethod]
        public void BothDungeons_ShareTheSamePropertyShapePerArchetype()
        {
            var tables = new[] { "int", "bool", "float", "d_i_d", "skill", "attribute", "attribute_2nd", "spell_book" };
            var first = Dungeons[0];

            foreach (var d in Dungeons.Skip(1))
            {
                foreach (var (a, b, label) in new[] { (first.Brute, d.Brute, "Brute"), (first.Hurler, d.Hurler, "Hurler"), (first.Shaman, d.Shaman, "Shaman"), (first.Boss, d.Boss, "Boss") })
                {
                    foreach (var t in tables)
                    {
                        // The Hurlers come from two different retail templates (Lackey vs Launcher): the skill set is
                        // template-owned, everything behavioural must still be identical. The d_i_d type set is compared:
                        // every lugian carries PhysicsEffectTable 0x3400001E, without which spell effects show no visual.
                        if (label == "Hurler" && t == "skill")
                            continue;

                        CollectionAssert.AreEqual(KeySet(a, t), KeySet(b, t), $"{label} {first.Name} vs {d.Name}: weenie_properties_{t} type set");
                    }
                }

                foreach (var key in new[] { 93, 101, 133 })
                    Assert.AreEqual(ParseNumeric(first.Hurler, "weenie_properties_int")[key], ParseNumeric(d.Hurler, "weenie_properties_int")[key], $"Hurler int {key}");

                Assert.AreEqual(80, ParseNumeric(d.Hurler, "weenie_properties_float")[55], 1e-9, "Hurler HomeRadius");
                Assert.IsTrue(ParseBools(d.Hurler)[52], "Hurler AiImmobile");
                Assert.IsFalse(ParseNumeric(d.Hurler, "weenie_properties_int").ContainsKey(308), "Hurler carries no DamageResistRating");
            }
        }

        [TestMethod]
        public void Shaman_MagicOutgrowsTheEarlierDungeonsShaman()
        {
            // Effective magic skill = init_Level + (Focus + Self) / 4, matching the values static-scale prints.
            int Magic(int shaman, int type) => (int)ParseNumeric(shaman, "weenie_properties_skill", 4)[type]
                + (int)(ParseNumeric(shaman, "weenie_properties_attribute", 1)[5] + ParseNumeric(shaman, "weenie_properties_attribute", 1)[6]) / 4;

            // Heavy Weapons = init + (Strength + Coordination) / 3 reproduces the printed 764 / 829 for the Brutes.
            int Heavy(int brute) => (int)ParseNumeric(brute, "weenie_properties_skill", 4)[44]
                + (int)(ParseNumeric(brute, "weenie_properties_attribute", 1)[1] + ParseNumeric(brute, "weenie_properties_attribute", 1)[4]) / 3;

            for (var i = 1; i < Dungeons.Length; i++)
            {
                var prev = Dungeons[i - 1];
                var d = Dungeons[i];
                var ratio = (double)Heavy(d.Brute) / Heavy(prev.Brute);

                foreach (var type in new[] { 31, 33, 34 })
                {
                    Assert.IsTrue(Magic(d.Shaman, type) > Magic(prev.Shaman, type), $"{d.Name} magic skill {type} must exceed {prev.Name}'s.");
                    Assert.AreEqual(Magic(prev.Shaman, type) * ratio, Magic(d.Shaman, type), 1.0, $"{d.Name} magic skill {type} tracks the Brute melee ratio.");
                }
            }
        }

        [TestMethod]
        public void Chanters_CarryTheLightningCasterMarker()
        {
            // The lugian motion table has no cast wind-up, so the caster is marked with a VisualEffectScript (DID 9000).
            foreach (var d in Dungeons)
                Assert.AreEqual((double)0x33000F1E, ParseNumeric(d.Shaman, "weenie_properties_d_i_d")[9000], $"{d.Name} Chanter VisualEffectScript");
        }

        [TestMethod]
        public void Gigas_HaveAPluralName()
        {
            foreach (var d in Dungeons)
            {
                var strings = ParseStrings(d.Brute);
                Assert.AreEqual(strings[1], strings[20], $"{d.Name} Gigas PluralName (string 20) must equal its Name.");
            }
        }

        [TestMethod]
        public void Boss_AttackSkillsAreAtLeastTheBrutes()
        {
            // Effective = init_Level + (attr1 + attr2) / divisor. Formulas read from client_portal.dat SkillTable
            // (attribute ids 1 Str, 3 Quick, 4 Coord): Two-handed / Heavy / Light / Dirty = (Str + Coord) / 3,
            // Finesse = (Quick + Coord) / 3, Missile = Coord / 2.
            double Eff(int wcid, int skill)
            {
                var attr = ParseNumeric(wcid, "weenie_properties_attribute", 1);
                var init = ParseNumeric(wcid, "weenie_properties_skill", 4)[skill];
                switch (skill)
                {
                    case 46: return init + (attr[3] + attr[4]) / 3.0;
                    case 47: return init + attr[4] / 2.0;
                    default: return init + (attr[1] + attr[4]) / 3.0;
                }
            }

            foreach (var d in Dungeons)
                foreach (var skill in new[] { 41, 44, 45, 46, 47, 52 })
                    Assert.IsTrue(Eff(d.Boss, skill) >= Eff(d.Brute, skill), $"{d.Name}: boss skill {skill} ({Eff(d.Boss, skill)}) must be >= the Brute's ({Eff(d.Brute, skill)}).");
        }

        // ---- helpers ----

        private static void AssertQuest(string name, int maxSolves, int minDelta)
        {
            var root = RepoRoot();
            var json = Path.Combine(root, "Content", "json", "quests", name + ".json");
            var sql = Path.Combine(root, "Content", "sql", "quests", name + ".sql");
            Assert.IsTrue(File.Exists(json), json);
            Assert.IsTrue(File.Exists(sql), sql);

            var j = File.ReadAllText(json);
            StringAssert.Matches(j, new Regex(@"""maxsolves"":\s*" + maxSolves + @"\b"), name);
            StringAssert.Matches(j, new Regex(@"""mindelta"":\s*" + minDelta + @"\b"), name);
            StringAssert.Contains(File.ReadAllText(sql), $"VALUES ('{name}', {minDelta}, {maxSolves},");
        }

        private static int MaxHealth(int wcid)
        {
            var endurance = Regex.Match(Section(wcid, "weenie_properties_attribute"), @"\(\s*" + wcid + @",\s*2,\s*(\d+),");
            var health = Regex.Match(Section(wcid, "weenie_properties_attribute_2nd"), @"\(\s*" + wcid + @",\s*1,\s*(\d+),");
            Assert.IsTrue(endurance.Success && health.Success, $"{wcid} endurance/health rows");
            return int.Parse(endurance.Groups[1].Value) / 2 + int.Parse(health.Groups[1].Value);
        }

        private static List<(int Dest, int Wcid, double Shade)> CreateRows(int wcid)
        {
            var rows = new List<(int, int, double)>();
            foreach (Match m in Regex.Matches(StripComments(Section(wcid, "weenie_properties_create_list")),
                @"\(\s*" + wcid + @",\s*(\d+),\s*(\d+),\s*\d+,\s*\d+,\s*([\d.]+),"))
                rows.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)));
            return rows;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Content", "sql", "weenies")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            Assert.Fail($"Could not locate Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static string GetFile(int wcid)
        {
            var weenies = Path.Combine(RepoRoot(), "Content", "sql", "weenies");
            var file = Directory.EnumerateFiles(weenies, wcid + " *.sql", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            Assert.IsNotNull(file, $"{wcid}: no weenie file found in {weenies}.");
            return file;
        }

        private static string StripComments(string s) => Regex.Replace(s, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        private static string Section(int wcid, string table)
        {
            var text = File.ReadAllText(GetFile(wcid));
            var m = Regex.Match(text, @"INSERT INTO `" + table + @"`[^;]*?VALUES(.*?);", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : string.Empty;
        }

        private static int[] KeySet(int wcid, string suffix)
        {
            var table = "weenie_properties_" + suffix;
            if (suffix == "spell_book" || suffix == "attribute" || suffix == "attribute_2nd" || suffix == "skill")
                return Regex.Matches(StripComments(Section(wcid, table)), @"\(\s*" + wcid + @",\s*(-?\d+)").Select(m => int.Parse(m.Groups[1].Value)).OrderBy(x => x).ToArray();
            return ParseNumeric(wcid, table).Keys.Concat(ParseBools(wcid).Keys.Where(_ => suffix == "bool")).OrderBy(x => x).ToArray();
        }

        // Numeric value taken from column index valueColumn (0 = the type column) of each row; used for skill init_Level.
        private static Dictionary<int, double> ParseNumeric(int wcid, string table, int valueColumn)
        {
            var d = new Dictionary<int, double>();
            foreach (Match m in Regex.Matches(StripComments(Section(wcid, table)), @"\(\s*" + wcid + @",\s*(\d+)((?:,\s*-?[\d.]+)+)\s*\)"))
            {
                var cols = m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).ToArray();
                d[int.Parse(m.Groups[1].Value)] = double.Parse(cols[valueColumn - 1], CultureInfo.InvariantCulture);
            }
            return d;
        }

        private static Dictionary<int, double> ParseNumeric(int wcid, string table)
        {
            var d = new Dictionary<int, double>();
            var body = StripComments(Section(wcid, table));
            foreach (Match m in Regex.Matches(body, @"\(\s*" + wcid + @",\s*(\d+),\s*(0x[0-9A-Fa-f]+|-?[\d.]+)\s*\)"))
            {
                var v = m.Groups[2].Value;
                d[int.Parse(m.Groups[1].Value)] = v.StartsWith("0x", StringComparison.Ordinal)
                    ? Convert.ToInt64(v, 16) : double.Parse(v, CultureInfo.InvariantCulture);
            }
            return d;
        }

        private static Dictionary<int, bool> ParseBools(int wcid)
        {
            var d = new Dictionary<int, bool>();
            foreach (Match m in Regex.Matches(StripComments(Section(wcid, "weenie_properties_bool")), @"\(\s*" + wcid + @",\s*(\d+),\s*(True|False)\s*\)"))
                d[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value == "True";
            return d;
        }

        private static Dictionary<int, string> ParseStrings(int wcid)
        {
            var d = new Dictionary<int, string>();
            foreach (Match m in Regex.Matches(Section(wcid, "weenie_properties_string"), @"\(\s*" + wcid + @",\s*(\d+),\s*'((?:[^']|'')*)'\)"))
                d[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value.Replace("''", "'");
            return d;
        }
    }
}
