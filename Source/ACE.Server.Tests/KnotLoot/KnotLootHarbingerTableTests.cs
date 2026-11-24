using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.KnotLoot;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// The Drift Harbinger loot table (round 2 U3): table shape, draw order, reachability of every rare, and the
    /// committed weenie SQL each rare resolves to. The roller part is pure (it reads only the settings handed to it);
    /// the content part is file + regex parsing, same idiom as KnotLootContentTests.
    /// </summary>
    [TestClass]
    public class KnotLootHarbingerTableTests
    {
        private sealed class FakeDefaults : IKnotLootSettings
        {
            public bool TableEnabled(string t) => true;

            public long Weight(string t, string entry, long fb) => fb;

            public double Chance(string t, string group, double fb) => fb;

            public (long Min, long Max) Currency(string t, string key, (long, long) fb) => fb;

            public KnotWindowSettings Window(string t) => KnotWindowSettings.Default;
        }

        private int calls;

        private Func<double> Script(params double[] values)
        {
            calls = 0;

            return () => values[calls++];
        }

        private sealed record Expected(string Key, uint Wcid, long Weight, string Name, int? DamageType, int? WeaponSkill, int? WieldSkill, int Wield, string Kind);

        // The orchestrator-ruled stat table (HARBINGER-ROUND2-PLAN.md section 4 "Loot power targets", section 8).
        private static readonly Expected[] Rares =
        {
            new("arm_guard", 1008268, 4, "Arm Guard of the Harbinger", null, null, 1, 180, "armor"),
            new("caustic_wings", 1008269, 1, "Harbinger's Caustic Wings", 32, null, 34, 385, "caster"),
            new("storm_eye", 1008270, 1, "Harbinger's Storm Eye", 64, null, 34, 385, "caster"),
            new("frore_crystal", 1008271, 1, "Harbinger's Frore Crystal", 8, null, 34, 385, "caster"),
            new("burning_limb", 1008272, 1, "Harbinger's Burning Limb", 16, null, 34, 385, "caster"),
            new("corroding_scythe", 1008273, 1, "Harbinger's Corroding Scythe", 32, 41, 41, 430, "scythe"),
            new("thundering_scythe", 1008278, 1, "Harbinger's Thundering Scythe", 64, 41, 41, 430, "scythe"),
            new("freezing_scythe", 1008279, 1, "Harbinger's Freezing Scythe", 8, 41, 41, 430, "scythe"),
            new("burning_scythe", 1008280, 1, "Harbinger's Burning Scythe", 16, 41, 41, 430, "scythe"),
            new("caustic_eye", 1008274, 1, "Harbinger's Caustic Eye", 32, 44, 44, 430, "eye"),
            new("jolting_eye", 1008275, 1, "Harbinger's Jolting Eye", 64, 44, 44, 430, "eye"),
            new("frozen_eye", 1008276, 1, "Harbinger's Frozen Eye", 8, 44, 44, 430, "eye"),
            new("burning_eye", 1008277, 1, "Harbinger's Burning Eye", 16, 44, 44, 430, "eye"),
        };

        [TestMethod]
        public void Table_HarbingerShape_MatchesTheSpec()
        {
            var t = KnotLootTables.Harbinger;

            Assert.AreEqual("harbinger", t.Key);
            Assert.AreEqual(2, t.Picks.Count, "infusion + rare only: no spines-style gated group");
            Assert.IsFalse(t.Picks.Any(p => p.Gated), "no group is gated");

            Assert.AreEqual("infusion", t.Picks[0].Key);
            CollectionAssert.AreEqual(KnotLootTables.Matron.Picks[0].Entries.Select(e => (e.Key, e.Wcid, e.DefaultWeight)).ToArray(), t.Picks[0].Entries.Select(e => (e.Key, e.Wcid, e.DefaultWeight)).ToArray(), "the infusion group is Matron's");

            Assert.AreEqual("rare", t.Picks[1].Key);
            CollectionAssert.AreEqual(Rares.Select(r => r.Key).ToArray(), t.Picks[1].Entries.Select(e => e.Key).ToArray());
            CollectionAssert.AreEqual(Rares.Select(r => r.Wcid).ToArray(), t.Picks[1].Entries.Select(e => e.Wcid).ToArray());
            CollectionAssert.AreEqual(Rares.Select(r => r.Weight).ToArray(), t.Picks[1].Entries.Select(e => e.DefaultWeight).ToArray());
            Assert.AreEqual(1, t.Picks[1].Entries.Count(e => e.Wcid == 1008277), "the fire heavy 1008277 is in the table once");

            // R3 rewards: which drops carry which Knot trait / mutation
            foreach (var e in t.Picks[1].Entries)
            {
                var kind = Rares.Single(r => r.Wcid == e.Wcid).Kind;

                switch (kind)
                {
                    case "caster":
                        Assert.AreEqual(KnotTraitId.OverpoweringIntent, e.Trait, $"{e.Key} wand: Overpowering Intent");
                        Assert.AreEqual(KnotLootMutation.HarbingerGuard, e.Mutation, $"{e.Key} wand: Harbinger's Guard");
                        break;
                    case "eye":
                        Assert.IsNull(e.Trait, $"{e.Key} heavy: no roller trait");
                        Assert.AreEqual(KnotLootMutation.HarbingerGuard, e.Mutation, $"{e.Key} heavy: Harbinger's Guard");
                        break;
                    case "armor":
                        Assert.AreEqual(KnotTraitId.HarbingerCelerity, e.Trait, $"{e.Key} arm guard: Celerity");
                        Assert.AreEqual(KnotLootMutation.None, e.Mutation);
                        break;
                    default:
                        Assert.IsNull(e.Trait, $"{e.Key} scythe: no trait");
                        Assert.AreEqual(KnotLootMutation.None, e.Mutation, $"{e.Key} scythe: no mutation");
                        break;
                }
            }

            CollectionAssert.AreEqual(KnotLootTables.Matron.Currency.Select(c => (c.Key, c.Wcid, c.DefaultMin, c.DefaultMax)).ToArray(), t.Currency.Select(c => (c.Key, c.Wcid, c.DefaultMin, c.DefaultMax)).ToArray(), "currencies match Matron");
        }

        [TestMethod]
        public void Table_LookupFindsHarbinger_CaseInsensitive()
        {
            Assert.IsTrue(KnotLootTables.TryGet("harbinger", out var t));
            Assert.AreSame(KnotLootTables.Harbinger, t);
            Assert.IsTrue(KnotLootTables.TryGet("HARBINGER", out var u));
            Assert.AreSame(KnotLootTables.Harbinger, u);
        }

        [TestMethod]
        public void Roll_Harbinger_DrawOrder_IsInfusionRareTraitUMmdNote()
        {
            // rare draw 0.0 picks the Arm Guard, which carries the Celerity trait, so it draws one trait u
            var d = KnotLootRoller.Roll(KnotLootTables.Harbinger, 300, new FakeDefaults(), Script(0.5, 0.0, 0.6, 0.25, 0.75));

            Assert.AreEqual(4, d.Count);
            Assert.AreEqual(5, calls, "five draws: infusion, rare, the Arm Guard's trait u, mmd, note (no gate)");
            Assert.AreEqual(1007401u, d[0].Wcid);
            Assert.AreEqual(1008268u, d[1].Wcid);
            Assert.AreEqual(KnotTraitId.HarbingerCelerity, d[1].Trait);
            Assert.AreEqual(0.6, d[1].U, 1e-9);
            Assert.AreEqual(20630u, d[2].Wcid);
            Assert.AreEqual(43901u, d[3].Wcid);
            Assert.AreEqual(0.25, d[2].U, 1e-9);
            Assert.AreEqual(0.75, d[3].U, 1e-9);

            // a scythe has no trait, so it draws nothing extra
            var scythe = KnotLootRoller.Roll(KnotLootTables.Harbinger, 300, new FakeDefaults(), Script(0.5, 8.5 / 16.0, 0.25, 0.75));
            Assert.AreEqual(1008273u, scythe[1].Wcid, "control: the sixth rare (cumulative weight 8 of 16) is the Corroding Scythe");
            Assert.AreEqual(4, calls, "a scythe draws no trait u");
        }

        [TestMethod]
        public void Roll_Harbinger_EveryRareIsReachable_ByItsWeightShare_AndAlwaysExactlyOne()
        {
            var total = (double)Rares.Sum(r => r.Weight);
            var cumulative = 0.0;

            foreach (var rare in Rares)
            {
                var mid = (cumulative + rare.Weight / 2.0) / total;
                cumulative += rare.Weight;

                var entry = KnotLootTables.Harbinger.Picks[1].Entries.Single(e => e.Wcid == rare.Wcid);
                var script = entry.Trait != null ? new[] { 0.0, mid, 0.5, 0.5, 0.5 } : new[] { 0.0, mid, 0.5, 0.5 };
                var d = KnotLootRoller.Roll(KnotLootTables.Harbinger, 300, new FakeDefaults(), Script(script));

                Assert.AreEqual(rare.Wcid, d[1].Wcid, rare.Key);
                Assert.AreEqual(1, d.Count(x => Rares.Any(r => r.Wcid == x.Wcid)), "exactly one rare per roll");
                Assert.AreEqual(script.Length, calls, rare.Key);
            }
        }

        // ---- committed weenie SQL ----

        [TestMethod]
        public void EveryRare_ResolvesToACommittedSql_WithTheRuledElementSkillAndPower()
        {
            foreach (var r in Rares)
            {
                var ints = Rows(r.Wcid, "weenie_properties_int");
                var floats = Rows(r.Wcid, "weenie_properties_float");
                var strings = Rows(r.Wcid, "weenie_properties_string");

                Assert.AreEqual(r.Name, strings[1], $"{r.Wcid} Name");
                Assert.AreEqual("10", ints[171], $"{r.Wcid} NumTimesTinkered (cannot be tinkered further)");
                Assert.AreEqual(r.Wield.ToString(), ints[160], $"{r.Wcid} WieldDifficulty");
                Assert.AreEqual(r.WieldSkill.ToString(), ints[159], $"{r.Wcid} WieldSkillType");
                Assert.AreEqual(r.Kind == "armor" ? "7" : "2", ints[158], $"{r.Wcid} WieldRequirements");

                if (r.DamageType != null)
                    Assert.AreEqual(r.DamageType.ToString(), ints[45], $"{r.Wcid} DamageType");

                if (r.WeaponSkill != null)
                    Assert.AreEqual(r.WeaponSkill.ToString(), ints[48], $"{r.Wcid} WeaponSkill");

                switch (r.Kind)
                {
                    case "armor":
                        Assert.AreEqual("555", ints[28], "ArmorLevel");
                        Assert.AreEqual("32", ints[9], "HandWear");
                        break;
                    case "caster":
                        Assert.AreEqual("35", WeenieType(r.Wcid));
                        Assert.AreEqual("1.28", floats[152], $"{r.Wcid} ElementalDamageMod");
                        Assert.AreEqual("0.1", floats[144], $"{r.Wcid} ManaConversionMod");
                        Assert.IsFalse(floats.ContainsKey(29), $"{r.Wcid} WeaponDefense is rolled via 9031, not fixed");
                        Assert.AreEqual(UiEffect(r.DamageType.Value), ints[18], $"{r.Wcid} UiEffects follows the element");
                        break;
                    case "scythe":
                        Assert.AreEqual("6", WeenieType(r.Wcid));
                        Assert.AreEqual("59", ints[44], "Damage");
                        Assert.AreEqual("0.3", floats[22], "DamageVariance");
                        Assert.IsFalse(floats.ContainsKey(29) || floats.ContainsKey(62), $"{r.Wcid} defense and offense are rolled via 9031, not fixed");
                        Assert.AreEqual("8", ints[46], "DefaultCombatStyle TwoHanded");
                        break;
                    case "eye":
                        Assert.AreEqual("81", ints[44], "Damage");
                        Assert.AreEqual("0.47", floats[22], "DamageVariance");
                        Assert.IsFalse(floats.ContainsKey(29) || floats.ContainsKey(62), $"{r.Wcid} defense and offense are rolled via 9031, not fixed");
                        break;
                }

                if (r.Kind != "armor" && r.Kind != "caster")
                    Assert.AreEqual(UiEffect(r.DamageType.Value), ints[18], $"{r.Wcid} UiEffects follows the element");
            }
        }

        [TestMethod]
        public void EveryRare_CarriesNoRareIdAndNoAuthoredTrait_AndOwnsItsWcid()
        {
            foreach (var r in Rares)
            {
                var ints = Rows(r.Wcid, "weenie_properties_int");
                var sql = Sql(r.Wcid);

                Assert.IsFalse(ints.ContainsKey(17), $"{r.Wcid} RareId");
                Assert.IsFalse(ints.ContainsKey(169), $"{r.Wcid} TsysMutationData");
                Assert.IsFalse(ints.ContainsKey(319), $"{r.Wcid} ItemMaxLevel (rare levelling)");
                Assert.AreEqual(r.Kind == "armor", ints.ContainsKey(265), $"{r.Wcid} EquipmentSetId only on the Arm Guard");
                Assert.IsTrue(Regex.IsMatch(sql, "weenie_properties_spell_book", RegexOptions.IgnoreCase), $"{r.Wcid} carries spells");
                var floats = Rows(r.Wcid, "weenie_properties_float");
                Assert.IsFalse(floats.ContainsKey(9020) || floats.ContainsKey(9023) || floats.ContainsKey(9024), $"{r.Wcid} Knot trait positions are stamped at drop, never authored");
                Assert.IsFalse(Rows(r.Wcid, "weenie_properties_int").ContainsKey(9093), $"{r.Wcid} KnotLootLevel is stamped at drop, never authored");
                StringAssert.Contains(sql, "-- @owner: pinnacle-harbinger");
                StringAssert.Contains(sql, $"-- @owns: wcid:{r.Wcid}");
            }
        }

        // ---- review hardening: plumbing, forbidden keys, offense/defense, mod layer, visual, text ----

        private static readonly Dictionary<string, (string Name, string Value)[]> Plumbing = new()
        {
            ["armor"] = new[] { ("ItemType", "2"), ("ValidLocations", "32"), ("ClothingPriority", "32768"), ("WieldRequirements", "7") },
            ["caster"] = new[] { ("ItemType", "32768"), ("ValidLocations", "16777216"), ("DefaultCombatStyle", "512"), ("ItemUseable", "1"), ("TargetType", "16"), ("WeaponType", "0"), ("WieldRequirements", "2") },
            ["scythe"] = new[] { ("ItemType", "1"), ("ValidLocations", "33554432"), ("DefaultCombatStyle", "8"), ("AttackType", "4"), ("WeaponType", "11"), ("CombatUse", "5"), ("ItemUseable", "1"), ("WieldRequirements", "2") },
            ["eye"] = new[] { ("ItemType", "1"), ("ValidLocations", "1048576"), ("DefaultCombatStyle", "2"), ("AttackType", "6"), ("WeaponType", "2"), ("CombatUse", "1"), ("ItemUseable", "1"), ("WieldRequirements", "2") },
        };

        private static readonly Dictionary<string, int> IntKey = new()
        {
            ["ItemType"] = 1, ["ValidLocations"] = 9, ["ClothingPriority"] = 4, ["WieldRequirements"] = 158, ["DefaultCombatStyle"] = 46,
            ["ItemUseable"] = 16, ["TargetType"] = 94, ["WeaponType"] = 353, ["AttackType"] = 47, ["CombatUse"] = 51,
        };

        [TestMethod]
        public void EveryRare_HasItsKindsPlumbing()
        {
            foreach (var r in Rares)
            {
                var ints = Rows(r.Wcid, "weenie_properties_int");

                foreach (var (name, value) in Plumbing[r.Kind])
                    Assert.AreEqual(value, ints[IntKey[name]], $"{r.Wcid} {name}");
            }
        }

        [TestMethod]
        public void EveryRare_CarriesNoForbiddenKey_AndCleavingOnlyOnScythes()
        {
            foreach (var r in Rares)
            {
                var ints = Rows(r.Wcid, "weenie_properties_int");
                var floats = Rows(r.Wcid, "weenie_properties_float");

                foreach (var k in new[] { 17, 166, 169, 263, 319, 320 })
                    Assert.IsFalse(ints.ContainsKey(k), $"{r.Wcid} forbidden int {k}");

                // ImbuedEffect (179): Crippling Blow (2) on every weapon (owner ask 2026-11-22); the Arm Guard carries none.
                if (r.Kind == "armor")
                    Assert.IsFalse(ints.ContainsKey(179), $"{r.Wcid} armor carries no ImbuedEffect");
                else
                    Assert.AreEqual("2", ints[179], $"{r.Wcid} ImbuedEffect = CripplingBlow");

                foreach (var k in new[] { 136, 138, 155, 157 })
                    Assert.IsFalse(floats.ContainsKey(k), $"{r.Wcid} forbidden float {k}");

                if (r.Kind == "scythe")
                    Assert.AreEqual("3", ints[292], $"{r.Wcid} Cleaving pinned at 3 (R3 owner ask)");
                else
                    Assert.IsFalse(ints.ContainsKey(292), $"{r.Wcid} Cleaving only on scythes");
            }
        }

        private static string RollSpec(string kind) => kind == "caster" ? "v1|f29=1.15:1.25:0.01" : "v1|f29=1.15:1.25:0.01;f62=1.15:1.25:0.01";

        [TestMethod]
        public void EveryWeapon_RollsDefenseAndOffenseOnEveryDrop_AndOnlyTheArmGuardAlsoTakesObsidianAndTailoring()
        {
            foreach (var r in Rares)
            {
                var ints = Rows(r.Wcid, "weenie_properties_int");
                var floats = Rows(r.Wcid, "weenie_properties_float");
                var strings = Rows(r.Wcid, "weenie_properties_string");

                if (r.Kind == "armor")
                {
                    // Owner 2026-11-20: the Arm Guard is Obsidian friendly (Tourmaline 1 | Amethyst 2 | Obsidian 4).
                    // Owner 2026-11-21: and tailorable (Tailoring 8).
                    Assert.AreEqual("15", ints[9095], $"{r.Wcid} ModSalvageAllowMask (Tourmaline, Amethyst, Obsidian, Tailoring; refuses other salvage)");
                    Assert.IsFalse(strings.ContainsKey(9031), $"{r.Wcid} the Arm Guard rolls nothing");
                    continue;
                }

                Assert.AreEqual("3", ints[9095], $"{r.Wcid} ModSalvageAllowMask (Tourmaline, Amethyst; refuses Obsidian and other salvage)");

                Assert.AreEqual("1.04", floats[149], $"{r.Wcid} WeaponMissileDefense");
                Assert.AreEqual("1.04", floats[150], $"{r.Wcid} WeaponMagicDefense");
                var parsed = ACE.Server.Entity.CreationRoll.Parse(strings[9031]);
                Assert.IsNotNull(parsed, $"{r.Wcid} CreationRollSpec parses (a malformed spec applies nothing)");
                CollectionAssert.AreEqual(r.Kind == "caster" ? new[] { 29 } : new[] { 29, 62 }, parsed.ConvertAll(e => e.Id), $"{r.Wcid} rolled property ids");

                foreach (var e in parsed)
                {
                    Assert.IsTrue(e.IsFloat, $"{r.Wcid} f{e.Id} is a float entry");
                    Assert.AreEqual(1.15m, e.Min, $"{r.Wcid} f{e.Id} min");
                    Assert.AreEqual(1.25m, e.Max, $"{r.Wcid} f{e.Id} max");
                    Assert.AreEqual(0.01m, e.Step, $"{r.Wcid} f{e.Id} step");
                }

                Assert.AreEqual(RollSpec(r.Kind), strings[9031], $"{r.Wcid} CreationRollSpec: melee defense 1.15-1.25, plus attack 1.15-1.25 on melee weapons");
                Assert.IsFalse(floats.ContainsKey(29), $"{r.Wcid} fixed WeaponDefense would be overwritten by nothing; the roll owns it");
                Assert.IsFalse(floats.ContainsKey(62), $"{r.Wcid} fixed WeaponOffense");
            }
        }

        [TestMethod]
        public void VisualEffectScripts_ArePinnedPerItem_AndOnlyFroreAndFrozenRelyOnTheirSetupDefault()
        {
            var expected = new Dictionary<uint, string>
            {
                [1008269] = "0x33000022", [1008270] = "0x330003C1", [1008272] = "0x3300033E",
                // scythes: the Setup bakes no default, so the element script is the only effect
                [1008273] = "0x33000F24", [1008278] = "0x33000F1E", [1008279] = "0x33000F9A", [1008280] = "0x33000F23",
                // the three non-frost Eyes use Setup 0x02001361, which bakes no default, so the element script is their only effect
                [1008274] = "0x33000F24", [1008275] = "0x33000F1E", [1008277] = "0x33000F23",
            };

            foreach (var r in Rares)
            {
                var dids = Rows(r.Wcid, "weenie_properties_d_i_d");

                if (expected.TryGetValue(r.Wcid, out var script))
                    Assert.AreEqual(script, dids[9000], $"{r.Wcid} VisualEffectScript");
                else
                    Assert.IsFalse(dids.ContainsKey(9000), $"{r.Wcid} carries no VisualEffectScript (its Setup bakes the effect, or it is armor)");
            }

            Assert.AreEqual("0x0200137F", Rows(1008271, "weenie_properties_d_i_d")[1]);
            Assert.AreEqual("0x02001A3D", Rows(1008273, "weenie_properties_d_i_d")[1], "scythe Setup");
        }

        [TestMethod]
        public void EveryRare_LongDesc_IsTheApprovedText()
        {
            var text = new Dictionary<uint, string>
            {
                [1008268] = "The hollowed out Arm of the Harbinger.",
                [1008269] = "Pinions torn from the Harbinger's acid alcove, still weeping a corrosive dew.",
                [1008270] = "A lens of the Harbinger's lightning. The storm still turns behind the glass.",
                [1008271] = "A shard of the Harbinger's frost crystal. It draws the warmth from the hand that holds it.",
                [1008272] = "A limb from the Harbinger's burning reach. The wood never finishes burning.",
                [1008273] = "The Harbinger's acid, given a reaper's edge. The blade eats a little of whatever it cuts.",
                [1008278] = "The Harbinger's storm, given a reaper's edge. It hums before it strikes.",
                [1008279] = "The Harbinger's frost, given a reaper's edge. Rime gathers along the blade at rest.",
                [1008280] = "The Harbinger's fire, given a reaper's edge. The haft is warm to the touch, and never cools.",
                [1008274] = "An eye of acid from the Harbinger's caustic alcove. It weeps, and what it weeps on does not last.",
                [1008275] = "An eye of lightning from the Harbinger's storm alcove. It never stops crackling.",
                [1008276] = "An eye of ice from the Harbinger's frost alcove. It watches, and it does not blink.",
                [1008277] = "An eye of flame from the Harbinger's burning alcove. Look into it too long and you will see nothing else.",
            };

            Assert.AreEqual(Rares.Length, text.Count);

            foreach (var r in Rares)
                Assert.AreEqual(text[r.Wcid], Rows(r.Wcid, "weenie_properties_string")[16], $"{r.Wcid} LongDesc");
        }

        private static readonly uint[] Eyes = { 1008274, 1008275, 1008276, 1008277 };

        [TestMethod]
        public void AllFourHeavies_AreTheFrozenEyeModel_AndDifferOnlyInTheElementFields()
        {
            Assert.IsFalse(Directory.EnumerateFiles(WeeniesDir(), "1008274 Harbinger's Dripping*.sql").Any(), "old Dripping Ruin file is gone");
            Assert.IsFalse(Directory.EnumerateFiles(WeeniesDir(), "1008275 Harbinger's Thunderhead*.sql").Any(), "old Thunderhead file is gone");
            Assert.IsFalse(Directory.EnumerateFiles(WeeniesDir(), "1008277 Harbinger's Burning Star*.sql").Any(), "old Burning Star file is gone");

            var elementInts = new HashSet<int> { 18, 45 };
            var elementStrings = new HashSet<int> { 1, 16 };

            foreach (var table in new[] { "weenie_properties_int", "weenie_properties_float", "weenie_properties_d_i_d", "weenie_properties_string", "weenie_properties_spell_book" })
            {
                var reference = Rows(1008276, table);

                foreach (var wcid in Eyes.Where(w => w != 1008276))
                {
                    var rows = Rows(wcid, table);

                    foreach (var key in reference.Keys.Union(rows.Keys))
                    {
                        if (table == "weenie_properties_int" && elementInts.Contains(key))
                            continue;
                        if (table == "weenie_properties_string" && elementStrings.Contains(key))
                            continue;
                        if (table == "weenie_properties_d_i_d" && (key == 9000 || key == 1))
                            continue;

                        Assert.AreEqual(reference.TryGetValue(key, out var a) ? a : "<absent>", rows.TryGetValue(key, out var b) ? b : "<absent>", $"{wcid} {table} {key} must equal the Frozen Eye's");
                    }
                }
            }

            foreach (var wcid in Eyes)
            {
                var dids = Rows(wcid, "weenie_properties_d_i_d");

                // The Frozen Eye's own Setup 0x02001362 bakes the blue frost script 0x33000F9A, so only the frost
                // Eye uses it. The other three take sibling rare Setup 0x02001361 (no default script, same holding
                // frames) and graft the Eye mesh 0x010039CB onto part 0, so they show only their own element.
                var animParts = Rows(wcid, "weenie_properties_anim_part");
                if (wcid == 1008276)
                {
                    Assert.AreEqual("0x02001362", dids[1], "the Frozen Eye keeps the frost-baked Eye Setup");
                    Assert.AreEqual(0, animParts.Count, "the Frozen Eye needs no AnimPart graft");
                }
                else
                {
                    Assert.AreEqual("0x02001361", dids[1], $"{wcid} uses the script-free sibling Setup, not the frost-baked 0x02001362");
                    Assert.AreEqual(1, animParts.Count, $"{wcid} carries exactly one AnimPart row");
                    Assert.AreEqual("16792011", animParts[0], $"{wcid} part 0 is the Frozen Eye mesh 0x010039CB");
                }

                Assert.AreEqual("0x06005BB3", dids[8], $"{wcid} Icon");
                Assert.AreEqual("0x04000BEF", dids[6], $"{wcid} PaletteBase");
                Assert.AreEqual("0x10000860", dids[7], $"{wcid} ClothingBase");
            }

            Assert.AreEqual("ace1008274-harbingerscausticeye", ClassName(1008274));
            Assert.AreEqual("ace1008275-harbingersjoltingeye", ClassName(1008275));
            Assert.AreEqual("ace1008277-harbingersburningeye", ClassName(1008277));
        }

        /// <summary>Owner 2026-11-19: every Harbinger weapon and wand drains mana at -0.033 (Storm Eye's retail -0.05 brought in line).</summary>
        [TestMethod]
        public void EveryWeaponAndWand_ManaRate_IsTheFamilyRate()
        {
            foreach (var wcid in new uint[] { 1008269, 1008270, 1008271, 1008272 }.Concat(Eyes).Concat(Scythes))
                Assert.AreEqual("-0.033", Rows(wcid, "weenie_properties_float")[5], "ManaRate of " + wcid);
        }

        private static readonly uint[] Scythes = { 1008273, 1008278, 1008279, 1008280 };

        [TestMethod]
        public void AllFourScythes_DifferOnlyInTheElementFields()
        {
            var elementInts = new HashSet<int> { 18, 45 };
            var elementStrings = new HashSet<int> { 1, 16 };

            foreach (var table in new[] { "weenie_properties_int", "weenie_properties_float", "weenie_properties_d_i_d", "weenie_properties_string", "weenie_properties_spell_book" })
            {
                var reference = Rows(Scythes[0], table);

                foreach (var wcid in Scythes.Skip(1))
                {
                    var rows = Rows(wcid, table);

                    foreach (var key in reference.Keys.Union(rows.Keys))
                    {
                        if (table == "weenie_properties_int" && elementInts.Contains(key))
                            continue;
                        if (table == "weenie_properties_string" && elementStrings.Contains(key))
                            continue;
                        if (table == "weenie_properties_d_i_d" && key == 9000)
                            continue;

                        Assert.AreEqual(reference.TryGetValue(key, out var x) ? x : "<absent>", rows.TryGetValue(key, out var y) ? y : "<absent>", $"{wcid} {table} {key} must equal the first scythe's");
                    }
                }
            }
        }

        [TestMethod]
        public void SpellBooks_AreTheApprovedSetPerFamily_AllAtProbabilityTwo()
        {
            var cantrips = new[] { 6089, 6094, 6091, 6100 };
            var auras = new[] { 4395, 4405, 4400, 4417 };

            var expected = new Dictionary<string, int[]>
            {
                ["eye"] = cantrips.Concat(auras).Concat(new[] { 6072, 4624 }).ToArray(),
                ["scythe"] = cantrips.Concat(auras).Concat(new[] { 6073, 5032 }).ToArray(),
                ["caster"] = new[] { 6098, 6091, 6087, 4414, 4400, 4418, 6075, 4638 },
                ["armor"] = new[] { 6101, 4329, 4468, 4466, 4460, 4470 },
            };

            foreach (var r in Rares)
            {
                var book = Rows(r.Wcid, "weenie_properties_spell_book");

                CollectionAssert.AreEquivalent(expected[r.Kind], book.Keys.ToArray(), $"{r.Wcid} spell set");
                Assert.IsTrue(book.Values.All(v => v == "2"), $"{r.Wcid} every spell at probability 2");
            }
        }

        [TestMethod]
        public void ArmGuard_CarriesTheDefenderSetCritDamageAndItemMana()
        {
            var ints = Rows(1008268, "weenie_properties_int");

            Assert.AreEqual("16", ints[265], "EquipmentSetId = Defenders");
            Assert.AreEqual("2", ints[374], "GearCritDamage");
            Assert.AreEqual("350", ints[106], "ItemSpellcraft");
            Assert.AreEqual("3000", ints[107], "ItemCurMana");
            Assert.AreEqual("3000", ints[108], "ItemMaxMana");
            Assert.AreEqual("0", ints[109], "ItemDifficulty");
            Assert.AreEqual("-0.033", Rows(1008268, "weenie_properties_float")[5], "ManaRate");
        }

        [TestMethod]
        public void EveryWeaponAndWand_TinkerLog_LetsTourmalineReroll()
        {
            foreach (var r in Rares.Where(x => x.Kind != "armor"))
            {
                var weapon = BuildFromSql(r);

                var log = weapon.GetProperty(PropertyString.TinkerLog);
                Assert.IsNotNull(log, $"{r.Wcid} carries a TinkerLog (9007)");
                Assert.IsTrue(WeaponModTinkerSet.TryParseLog(log, out var entries), $"{r.Wcid} log parses");
                Assert.AreEqual(weapon.GetProperty(PropertyInt.NumTimesTinkered), entries.Count, $"{r.Wcid} log entries == NumTimesTinkered (the retail integrity gate)");
                Assert.AreEqual(10, entries.Count, $"{r.Wcid} ten entries");
                Assert.IsTrue(entries.All(WeaponTinkerTable.IsKnown), $"{r.Wcid} every entry is a material the weapon-mod table can reverse");
                Assert.AreEqual(1, entries.Distinct().Count(), $"{r.Wcid} one material per item");
                Assert.AreEqual(r.Kind == "caster" ? MaterialType.GreenGarnet : MaterialType.Iron, entries[0], $"{r.Wcid} material per family");

                Assert.IsTrue(WeaponModTinkerSet.PassesIntegrityGate(weapon), $"{r.Wcid} integrity gate");
                // Crippling Blow (owner ask 2026-11-22): one imbue bit reserves one of the ten slots (Tourmaline stays allowed: 9 available).
                Assert.AreEqual(1, WeaponModTinkerSet.ReadReservedSlots(weapon), $"{r.Wcid} only the Crippling Blow imbue is reserved");
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Reroll, weapon), $"{r.Wcid} Tourmaline reroll is not refused");
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, WeaponModManager.ResolveRefusal(WeaponModManager.WeaponModAction.Swap, weapon), $"{r.Wcid} Amethyst is not refused");
            }
        }

        private static uint nextGuid = 0x7F2D0000;

        /// <summary>A real WorldObject built from the committed SQL's own int and string rows, so the weapon-mod rules read what the shipped item carries.</summary>
        private static WorldObject BuildFromSql(Expected r)
        {
            var weenie = new Weenie
            {
                WeenieClassId = r.Wcid,
                WeenieType = r.Kind == "caster" ? ACE.Entity.Enum.WeenieType.Caster : ACE.Entity.Enum.WeenieType.MeleeWeapon,
                PropertiesInt = Rows(r.Wcid, "weenie_properties_int").ToDictionary(kv => (PropertyInt)kv.Key, kv => int.Parse(kv.Value)),
                PropertiesString = Rows(r.Wcid, "weenie_properties_string").Where(kv => kv.Key == 9007).ToDictionary(kv => (PropertyString)kv.Key, kv => kv.Value),
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static string ClassName(uint wcid)
        {
            var m = Regex.Match(Sql(wcid), @"VALUES \(" + wcid + @", '([^']*)'");
            Assert.IsTrue(m.Success, $"{wcid} weenie row");
            return m.Groups[1].Value;
        }

        [TestMethod]
        public void Pinnacles_HarbingerRowPointsAtTheHarbingerTable_AndShipsEnabled()
        {
            var text = File.ReadAllText(Path.Combine(ContentDir(), "dungeons", "dynamic", "pinnacles.json"));
            var line = text.Split('\n').Single(l => l.Contains("\"key\":\"harbinger\""));

            StringAssert.Contains(line, "\"rewardTable\":\"harbinger\"");
            StringAssert.Contains(line, "\"enabled\":true");
        }

        private static string UiEffect(int damageType) => damageType switch
        {
            32 => "256",
            64 => "64",
            8 => "128",
            16 => "32",
            _ => throw new ArgumentOutOfRangeException(nameof(damageType)),
        };

        // ---- helpers ----

        private static string WeenieType(uint wcid)
        {
            var m = Regex.Match(Sql(wcid), @"VALUES \(" + wcid + @", '[^']*', (\d+),");
            Assert.IsTrue(m.Success, $"{wcid} weenie row");
            return m.Groups[1].Value;
        }

        private static string Sql(uint wcid)
        {
            var file = Directory.EnumerateFiles(WeeniesDir(), wcid + " *.sql", SearchOption.TopDirectoryOnly).FirstOrDefault();
            Assert.IsNotNull(file, $"no weenie SQL starting '{wcid} ' under {WeeniesDir()}");
            return File.ReadAllText(file);
        }

        private static Dictionary<int, string> Rows(uint wcid, string table)
        {
            var result = new Dictionary<int, string>();
            var text = Regex.Replace(Sql(wcid), @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"^\s*--.*$", " ", RegexOptions.Multiline);

            foreach (Match block in Regex.Matches(text, @"INSERT\s+INTO\s+`" + table + @"`.*?;[ \t]*(?:\r?\n|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                foreach (Match row in Regex.Matches(block.Value, @"\(\s*" + wcid + @"\s*,\s*(\d+)\s*,\s*('(?:[^']|'')*'|[^)]*?)\s*\)"))
                    result[int.Parse(row.Groups[1].Value)] = row.Groups[2].Value.Trim().Trim('\'').Replace("''", "'");
            }

            return result;
        }

        /// <summary>
        /// Existing Arm Guards keep their own biota copy of ModSalvageAllowMask, so the shard update must OR the Obsidian
        /// bit into exactly the Arm Guard's rows, and the value it inserts must match the weenie template.
        /// </summary>
        [TestMethod]
        public void ArmGuardObsidianMigration_OrsTheObsidianBitIntoExistingArmGuardsOnly()
        {
            const string name = "2026-11-20-01-Harbinger-Arm-Guard-Obsidian-Existing-Items.sql";
            var path = Path.Combine(Path.GetDirectoryName(ContentDir()), "Database", "Updates", "Shard", name);
            Assert.IsTrue(File.Exists(path), $"shard update {name} not found at {path}");
            var sql = File.ReadAllText(path);

            Assert.AreEqual(1, Regex.Matches(sql, @"\*/").Count, "the migration header comment must close exactly once");

            var body = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline).Trim();

            StringAssert.StartsWith(body, "INSERT INTO `biota_properties_int`", "the migration is a single INSERT statement");
            Assert.AreEqual(1, body.Count(c => c == ';'), "exactly one statement");
            Assert.IsTrue(body.EndsWith(";"), "the statement ends the file");
            StringAssert.Matches(body, new Regex(@"WHERE\s+b\.`weenie_Class_Id`\s+IN\s*\(\s*1008268\s*\)"), "scoped to the Arm Guard only");
            StringAssert.Matches(body, new Regex(@"SELECT\s+b\.`id`,\s*9095,\s*7\s+FROM\s+`biota`"), "inserts the template mask where no row exists");
            StringAssert.Matches(body, new Regex(@"ON DUPLICATE KEY UPDATE\s+`value`\s*=\s*`value`\s*\|\s*4\s*;"), "ORs in the Obsidian bit, keeping the others");

            // The template has since moved on to 15 (Tailoring, 2026-11-21); the later migration below carries it there.
            Assert.AreEqual(7, int.Parse(Rows(1008268, "weenie_properties_int")[9095]) & 7, "the inserted value is the template's non-tailoring bits");
        }

        /// <summary>
        /// Owner 2026-11-21: the Arm Guard takes tailoring kits. Existing Arm Guards keep their own biota copy of
        /// ModSalvageAllowMask, so this shard update must OR the Tailoring bit into exactly the Arm Guard's rows, run after
        /// the Obsidian update, and insert the current weenie template value.
        /// </summary>
        [TestMethod]
        public void ArmGuardTailoringMigration_OrsTheTailoringBitIntoExistingArmGuardsOnly()
        {
            const string name = "2026-11-21-03-Harbinger-Arm-Guard-Tailoring-Existing-Items.sql";
            const string obsidian = "2026-11-20-01-Harbinger-Arm-Guard-Obsidian-Existing-Items.sql";
            var path = Path.Combine(Path.GetDirectoryName(ContentDir()), "Database", "Updates", "Shard", name);
            Assert.IsTrue(File.Exists(path), $"shard update {name} not found at {path}");
            Assert.IsTrue(string.CompareOrdinal(obsidian, name) < 0, "the tailoring update sorts after the Obsidian update");
            var sql = File.ReadAllText(path);

            Assert.AreEqual(1, Regex.Matches(sql, @"\*/").Count, "the migration header comment must close exactly once");

            var body = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline).Trim();

            StringAssert.StartsWith(body, "INSERT INTO `biota_properties_int`", "the migration is a single INSERT statement");
            Assert.AreEqual(1, body.Count(c => c == ';'), "exactly one statement");
            Assert.IsTrue(body.EndsWith(";"), "the statement ends the file");
            StringAssert.Matches(body, new Regex(@"WHERE\s+b\.`weenie_Class_Id`\s+IN\s*\(\s*1008268\s*\)"), "scoped to the Arm Guard only");
            StringAssert.Matches(body, new Regex(@"SELECT\s+b\.`id`,\s*9095,\s*15\s+FROM\s+`biota`"), "inserts the template mask where no row exists");
            StringAssert.Matches(body, new Regex(@"ON DUPLICATE KEY UPDATE\s+`value`\s*=\s*`value`\s*\|\s*8\s*;"), "ORs in the Tailoring bit, keeping the others");

            Assert.AreEqual("15", Rows(1008268, "weenie_properties_int")[9095], "the inserted value matches the weenie template");
            Assert.AreEqual(7 | 8, int.Parse(Rows(1008268, "weenie_properties_int")[9095]), "the 11-20 insert (7) plus this update's bit (8) lands on the template");
        }

        /// <summary>
        /// Owner 2026-11-22: all 12 Harbinger weapons carry the Crippling Blow imbue. Existing drops keep their own biota
        /// copy of the property rows, so this shard update must OR ImbuedEffect bit 2 into exactly the 12 weapons (never the
        /// Arm Guard, 1008268) and insert the template value where no row exists.
        /// </summary>
        [TestMethod]
        public void WeaponCripplingBlowMigration_OrsBitTwoIntoTheTwelveWeaponsOnly()
        {
            const string name = "2026-11-22-01-Harbinger-Weapons-Crippling-Blow-Existing-Items.sql";
            const string tailoring = "2026-11-21-03-Harbinger-Arm-Guard-Tailoring-Existing-Items.sql";
            var path = Path.Combine(Path.GetDirectoryName(ContentDir()), "Database", "Updates", "Shard", name);
            Assert.IsTrue(File.Exists(path), $"shard update {name} not found at {path}");
            Assert.IsTrue(string.CompareOrdinal(tailoring, name) < 0, "the crippling update sorts after the tailoring update");
            var sql = File.ReadAllText(path);

            Assert.AreEqual(1, Regex.Matches(sql, @"\*/").Count, "the migration header comment must close exactly once");

            var body = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline).Trim();

            StringAssert.StartsWith(body, "INSERT INTO `biota_properties_int`", "the migration is a single INSERT statement");
            Assert.AreEqual(1, body.Count(c => c == ';'), "exactly one statement");
            Assert.IsTrue(body.EndsWith(";"), "the statement ends the file");
            StringAssert.Matches(body, new Regex(@"\(\s*`object_Id`\s*,\s*`type`\s*,\s*`value`\s*\)"), "column list is object_Id, type, value in order");
            StringAssert.Matches(body, new Regex(@"SELECT\s+b\.`id`,\s*179,\s*2\s+FROM\s+`biota`"), "inserts CripplingBlow where no row exists");
            StringAssert.Matches(body, new Regex(@"ON DUPLICATE KEY UPDATE\s+`value`\s*=\s*`value`\s*\|\s*2\s*;"), "ORs in bit 2, keeping any other imbue");

            var inList = Regex.Match(body, @"WHERE\s+b\.`weenie_Class_Id`\s+IN\s*\(([^)]*)\)");
            Assert.IsTrue(inList.Success, "scoped by weenie_Class_Id IN (...)");
            var wcids = inList.Groups[1].Value.Split(',').Select(s => uint.Parse(s.Trim())).OrderBy(x => x).ToArray();
            var weapons = Rares.Where(r => r.Kind != "armor").Select(r => r.Wcid).OrderBy(x => x).ToArray();
            Assert.AreEqual(12, weapons.Length, "the table has 12 weapons");
            CollectionAssert.AreEqual(weapons, wcids, "scoped to exactly the 12 weapons");
            Assert.IsFalse(wcids.Contains(1008268u), "the Arm Guard is excluded");
        }

        private static string WeeniesDir() => Path.Combine(ContentDir(), "sql", "weenies");

        private static string ContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content", "sql", "weenies");

                if (Directory.Exists(candidate))
                    return Path.Combine(dir.FullName, "Content");

                dir = dir.Parent;
            }

            Assert.Fail($"Could not find Content/sql/weenies by walking up from {AppContext.BaseDirectory}.");
            return null;
        }
    }
}
