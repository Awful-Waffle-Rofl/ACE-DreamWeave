using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.CombatSimulator;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.CharacterDetails.CharacterDetailsSourcePins;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// The pure Character Details projection: the contract shape, player-values-only, the baseline tooltip, the 99%
    /// accuracy-bar search, and the class ability point sentences shared with /abilities points.
    /// </summary>
    [TestClass]
    public class CharacterDetailsProjectorTests
    {
        public TestContext TestContext { get; set; }

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static ThreadBaselineCreature Baseline(uint meleeDefense = 400, uint missileDefense = 420, uint magicDefense = 380,
            uint meleeAttack = 410, uint missileAttack = 350, uint magicAttack = 450, int critResist = 0)
            => ThreadBaselineCreature.FromStandard(300, DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 100, 100,
                effectiveSkillMedians: new Dictionary<Skill, uint>
                {
                    [Skill.MeleeDefense] = meleeDefense, [Skill.MissileDefense] = missileDefense, [Skill.MagicDefense] = magicDefense,
                    [Skill.HeavyWeapons] = meleeAttack, [Skill.MissileWeapons] = missileAttack, [Skill.WarMagic] = magicAttack,
                },
                critResistRatingMedian: (uint)critResist), new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5));

        private static DefenderProfile Profile()
        {
            var armor = new Dictionary<ArmorKey, ArmorRow>();
            var resistance = new Dictionary<DamageType, ResistancePair>();
            var shield = new Dictionary<ShieldKey, ShieldRow>();

            foreach (var type in new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Fire, DamageType.Cold, DamageType.Acid, DamageType.Electric, DamageType.Nether })
            {
                foreach (BodyPart part in Enum.GetValues(typeof(BodyPart)))
                    foreach (var ima in new[] { false, true })
                        foreach (var imr in new[] { false, true })
                            armor[new ArmorKey(part, type, ima, imr)] = new ArmorRow(part == BodyPart.Head ? 200f / 3 : 400f);

                resistance[type] = new ResistancePair(type == DamageType.Nether ? 0.5f : 0.8f, 1.0f);
                shield[new ShieldKey(type, false)] = new ShieldRow(0f);
                shield[new ShieldKey(type, true)] = new ShieldRow(0f);
            }

            return new DefenderProfile
            {
                Armor = armor,
                Resistance = resistance,
                Shield = shield,
                DamageResistRatingBase = new Dictionary<CombatType, float> { [CombatType.Melee] = 0.8f, [CombatType.Missile] = 0.8f, [CombatType.Magic] = 0.9f },
                BattleHardenedMultiplier = 1.0f,
                RearGuardArmorLevel = 100.0,
            };
        }

        private static CharacterDetailsInputs Inputs(CombatMode stance = CombatMode.Melee) => new CharacterDetailsInputs
        {
            CharacterGuid = 0x50000A01,
            Level = 300,
            Stance = stance,
            WeaponAttackSkill = stance == CombatMode.Magic ? 0u : 500u,
            MissileAttackSkillByBarPercent = stance == CombatMode.Missile ? Enumerable.Range(0, 101).Select(p => (uint)(300 + 3 * p)).ToArray() : null,
            MagicSkills = new Dictionary<Skill, uint> { [Skill.WarMagic] = 480, [Skill.LifeMagic] = 450 },
            MeleeDefense = 520,
            MissileDefense = 480,
            MagicDefense = 430,
            WeaponSkill = stance == CombatMode.Magic ? Skill.None : Skill.HeavyWeapons,
            HeldCritPreResist = 0.15f,
            HeldCritDamageMod = 2.0f,
            KillerInstinctCritBonus = 0.04f,
            Profile = Profile(),
            SpellResistanceMod = new Dictionary<DamageType, float>
            {
                [DamageType.Slash] = 0.8f, [DamageType.Pierce] = 0.8f, [DamageType.Bludgeon] = 0.8f, [DamageType.Fire] = 0.8f,
                [DamageType.Cold] = 0.8f, [DamageType.Acid] = 0.8f, [DamageType.Electric] = 0.8f, [DamageType.Nether] = 0.5f,
                [DamageType.Health] = 0.9f,
            },
            SpellAbsorbFront = 1.0f,
            SpellAbsorbBack = 1.0f,
            HarmResistanceMod = 0.9f,
            DrainResistanceMod = 0.9f,
            DotNetherResistanceMod = 0.5f,
            DotUntypedResistanceMod = 1.0f,
            DotResistRating = 10,
            Fortitude = 0.05,
            DamageRating = 30,
            CritDamageRating = 15,
            CritRating = 5,
            BurdenMod = 1.0f,
        };

        private static Dictionary<string, CharacterDetailsRow> Rows(ACE.Server.Managers.Market.CharacterDetails d)
            => d.Sections.Where(s => s.Kind == "list").SelectMany(s => s.Rows).ToDictionary(r => r.Key);

        [TestMethod]
        public void SampleResponse_HasTheContractShape()
        {
            var details = CharacterDetailsProjector.Build(Inputs(), Baseline());
            var json = JsonSerializer.Serialize(details, MarketApiHost.Json);

            // the sample the report quotes
            TestContext.WriteLine("SAMPLE_JSON " + json);
            Console.WriteLine("SAMPLE_JSON " + json);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            CollectionAssert.AreEquivalent(new[] { "character_guid", "level", "baseline_available", "baseline_note", "sections" },
                root.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.IsTrue(root.GetProperty("baseline_available").GetBoolean());
            Assert.AreEqual(CharacterDetailsProjector.BaselineTooltip, root.GetProperty("baseline_note").GetString());

            foreach (var section in root.GetProperty("sections").EnumerateArray())
            {
                var kind = section.GetProperty("kind").GetString();
                Assert.IsTrue(kind == "list" || kind == "table" || kind == "tables");
                Assert.AreEqual(kind == "table", section.TryGetProperty("columns", out var columns), "columns only on a table");
                Assert.AreEqual(kind == "tables", section.TryGetProperty("tables", out var tables), "tables only on a tables section");

                if (kind == "tables")
                {
                    Assert.AreEqual(0, section.GetProperty("rows").GetArrayLength(), "rows is required and empty here");

                    foreach (var table in tables.EnumerateArray())
                    {
                        var tableColumns = table.GetProperty("columns").GetArrayLength();

                        foreach (var row in table.GetProperty("rows").EnumerateArray())
                        {
                            Assert.AreEqual(tableColumns, row.GetProperty("cells").GetArrayLength(), "cells.length == columns.length");

                            if (row.TryGetProperty("children", out var rowChildren))
                            {
                                foreach (var child in rowChildren.EnumerateArray())
                                    Assert.AreEqual(tableColumns, child.GetProperty("cells").GetArrayLength(), "children share the columns");
                            }
                        }
                    }

                    continue;
                }

                foreach (var row in section.GetProperty("rows").EnumerateArray())
                {
                    Assert.AreEqual(JsonValueKind.String, row.GetProperty("key").ValueKind);
                    Assert.AreEqual(JsonValueKind.String, row.GetProperty("label").ValueKind);

                    if (kind == "table")
                    {
                        Assert.AreEqual(columns.GetArrayLength(), row.GetProperty("cells").GetArrayLength(), "cells.length == columns.length");
                        Assert.AreNotEqual(row.GetProperty("label").GetString(), row.GetProperty("cells")[0].GetString(), "the label is never cells[0]");
                        Assert.IsFalse(row.TryGetProperty("value", out _));

                        if (row.TryGetProperty("children", out var children))
                        {
                            foreach (var child in children.EnumerateArray())
                            {
                                Assert.AreEqual(columns.GetArrayLength(), child.GetProperty("cells").GetArrayLength(), "children share the columns");
                                Assert.IsFalse(child.TryGetProperty("children", out _), "no nesting");
                            }
                        }
                    }
                    else
                    {
                        Assert.AreEqual(JsonValueKind.String, row.GetProperty("value").ValueKind);
                        Assert.IsFalse(row.TryGetProperty("cells", out _));
                    }
                }
            }

            var armor = details.Sections.Single(s => s.Key == "damage_taken").Tables.Single(t => t.Key == "physical");
            CollectionAssert.AreEqual(new[] { "Head", "Chest", "Abdomen", "U Arm", "L Arm", "Hands", "U Leg", "L Leg", "Feet" }, armor.Columns);
            Assert.AreEqual("30.4%", armor.Rows.Single(r => r.Key == "armor_front").Cells[0], "AL 66.7 halves damage, times 0.8 protection, 0.8 damage resist rating and 0.95 Fortitude: the worst type");
            Assert.AreEqual("30.4%", armor.Rows.Single(r => r.Key == "armor_front").Children.Single(c => c.Key == "armor_front_slash").Cells[0]);
        }

        private const string Tail = " Specialized monsters may be stronger.";

        [TestMethod]
        public void BaselineRows_TooltipsAreTheCalculation_WithTheSkillNumbersAndTheLevel()
        {
            var rows = Rows(CharacterDetailsProjector.Build(Inputs(), Baseline()));

            Assert.AreEqual(CharacterDetailsProjector.Pct(SkillCheck.GetSkillChance(500u, 400u)), rows["hit_melee"].Value);
            Assert.AreEqual(CharacterDetailsProjector.Pct(1.0f - (float)SkillCheck.GetSkillChance(410u, 520u)), rows["evade_melee"].Value);
            Assert.AreEqual(CharacterDetailsProjector.Pct(1.0f - (float)SkillCheck.GetSkillChance(450u, 430u)), rows["resist_spells"].Value);

            Assert.AreEqual("500 effective Heavy Weapons (buffed skill with weapon and stance modifiers) vs 400 average Melee Defense of level 300 Thread monsters." + Tail, rows["hit_melee"].Tooltip);
            Assert.AreEqual("520 effective Melee Defense (buffed skill with weapon and stance modifiers) vs 410 average melee attack of level 300 Thread monsters." + Tail, rows["evade_melee"].Tooltip);
            Assert.AreEqual("480 effective Missile Defense (buffed skill with weapon and stance modifiers) vs 350 average missile attack of level 300 Thread monsters." + Tail, rows["evade_missile"].Tooltip);
            Assert.AreEqual("430 effective Magic Defense (buffed skill with weapon and stance modifiers) vs 450 average magic attack of level 300 Thread monsters." + Tail, rows["resist_spells"].Tooltip);

            foreach (var row in rows.Values.Where(r => r.Tooltip != null && r.Tooltip.Contains("average")))
                StringAssert.EndsWith(row.Tooltip, Tail, row.Key);

            Assert.IsFalse(typeof(CharacterDetailsRow).GetProperties().Any(x => x.Name == "Note"), "the row has no note field");
        }

        [TestMethod]
        public void Burden_AppearsInTheEvadeTooltips_OnlyWhenItReducesDefense()
        {
            var burdened = Inputs();
            burdened.BurdenMod = 0.9f;
            var rows = Rows(CharacterDetailsProjector.Build(burdened, Baseline()));

            StringAssert.Contains(rows["evade_melee"].Tooltip, "Overburdened: melee and missile defense x0.90.");
            StringAssert.Contains(rows["evade_missile"].Tooltip, "Overburdened: melee and missile defense x0.90.");
            Assert.IsFalse(Rows(CharacterDetailsProjector.Build(Inputs(), Baseline()))["evade_melee"].Tooltip.Contains("Overburdened"));
        }

        [TestMethod]
        public void NoBaseline_OmitsTheBaselineRows_AndSaysWhy_ButKeepsTheCritRows()
        {
            var details = CharacterDetailsProjector.Build(Inputs(), null);
            var rows = Rows(details);

            Assert.IsFalse(details.BaselineAvailable);
            Assert.AreEqual(CharacterDetailsProjector.BaselineUnavailableNote(300), details.BaselineNote);

            foreach (var key in new[] { "hit_melee", "evade_melee", "evade_missile", "resist_spells" })
                Assert.IsFalse(rows.ContainsKey(key), key);

            Assert.AreEqual("15.0%", rows["crit_chance"].Value, "pre-resist, no monster term");
            Assert.IsFalse(rows["crit_chance"].Tooltip.Contains("Thread monsters"));
            Assert.IsTrue(rows.ContainsKey("crit_damage"));
        }

        // ---- gating: only what the held gear uses ----

        [TestMethod]
        public void MeleeHeld_ShowsMeleeHit_NoMissileNoSpells()
        {
            var keys = Rows(CharacterDetailsProjector.Build(Inputs(), Baseline())).Keys.ToList();

            CollectionAssert.IsSubsetOf(new[] { "hit_melee", "crit_chance", "crit_damage" }, keys);
            Assert.IsFalse(keys.Any(k => k == "hit_missile" || k == "accuracy_bar_99" || k.StartsWith("spell_")));

            CollectionAssert.AreEqual(new[] { "offense", "defense", "damage_taken" }, CharacterDetailsProjector.Build(Inputs(), Baseline()).Sections.Select(x => x.Key).ToArray(),
                "ratings, character and class points sections are gone");
        }

        [TestMethod]
        public void EmptyHands_ShowTheMeleeRow_NamedForTheSkillTheReaderReports()
        {
            // with nothing wielded the engine reads the character's highest melee skill (Player.GetCurrentWeaponSkill's null-weapon branch)
            var input = Inputs();
            input.WeaponSkill = Skill.LightWeapons;

            var rows = Rows(CharacterDetailsProjector.Build(input, Baseline()));
            StringAssert.StartsWith(rows["hit_melee"].Tooltip, "500 effective Light Weapons (buffed skill with weapon and stance modifiers) vs 400");
        }

        [TestMethod]
        public void MissileHeld_ShowsMissileHitAndTheBar_NoMelee()
        {
            var input = Inputs(CombatMode.Missile);
            input.WeaponSkill = Skill.Bow;
            var rows = Rows(CharacterDetailsProjector.Build(input, Baseline()));

            Assert.IsTrue(rows.ContainsKey("hit_missile") && rows.ContainsKey("accuracy_bar_99"));
            Assert.IsFalse(rows.ContainsKey("hit_melee") || rows.Keys.Any(k => k.StartsWith("spell_")));
            Assert.AreEqual("500 effective Bow at a full accuracy bar (buffed skill with weapon, stance and accuracy modifiers) vs 420 average Missile Defense of level 300 Thread monsters." + Tail, rows["hit_missile"].Tooltip);
            StringAssert.StartsWith(rows["accuracy_bar_99"].Tooltip, "The lowest accuracy bar at which your effective Bow reaches a 99% hit chance vs 420 average Missile Defense of level 300 Thread monsters.");
            StringAssert.EndsWith(rows["accuracy_bar_99"].Tooltip, Tail);
        }

        [TestMethod]
        public void CasterHeld_ShowsOnlyTheTrainedSchools_NoWeaponHit()
        {
            var input = Inputs(CombatMode.Magic);
            input.MagicSkills = new Dictionary<Skill, uint> { [Skill.WarMagic] = 480, [Skill.LifeMagic] = 450, [Skill.CreatureEnchantment] = 400 };

            var rows = Rows(CharacterDetailsProjector.Build(input, Baseline()));

            foreach (var key in new[] { "spell_war", "spell_life", "spell_creature", "crit_chance", "crit_damage" })
                Assert.IsTrue(rows.ContainsKey(key), key);

            Assert.IsFalse(rows.ContainsKey("spell_void"), "an untrained school has no row");
            Assert.IsFalse(rows.ContainsKey("hit_melee") || rows.ContainsKey("hit_missile") || rows.ContainsKey("accuracy_bar_99"));
            Assert.AreEqual("480 effective War Magic (buffed skill) vs 380 average Magic Defense of level 300 Thread monsters." + Tail, rows["spell_war"].Tooltip);
            Assert.AreEqual("400 effective Creature Enchantment (buffed skill) vs 380 average Magic Defense of level 300 Thread monsters." + Tail, rows["spell_creature"].Tooltip);
            Assert.AreEqual(CharacterDetailsProjector.Pct(SkillCheck.GetSkillChance(480u, 380u)), rows["spell_war"].Value);
        }

        // ---- crit chance and crit damage ----

        [TestMethod]
        public void CritChance_ComposesThroughApplyCritResist_AndTheTooltipListsTheComponents()
        {
            var rows = Rows(CharacterDetailsProjector.Build(Inputs(), Baseline(critResist: 25)));
            var mod = Creature.GetNegativeRatingMod(25).ToString("0.00", CultureInfo.InvariantCulture);

            Assert.AreEqual("Crit chance", rows["crit_chance"].Label);
            Assert.AreEqual(CharacterDetailsProjector.Pct(WorldObject.ApplyCritResist(0.15f, 25)), rows["crit_chance"].Value);
            Assert.AreEqual($"Weapon base 6% + crit rating 5 (5%) + Killer Instinct Openings 4%, which changes as you fight, times x{mod} for the 25 average crit resist rating of level 300 Thread monsters." + Tail,
                rows["crit_chance"].Tooltip);

            // a caster reads the same row with its own base and no Killer Instinct
            var caster = Inputs(CombatMode.Magic);
            caster.HeldCritPreResist = 0.10f;
            var magic = Rows(CharacterDetailsProjector.Build(caster, Baseline(critResist: 25)))["crit_chance"];
            Assert.AreEqual(CharacterDetailsProjector.Pct(WorldObject.ApplyCritResist(0.10f, 25)), magic.Value);
            StringAssert.StartsWith(magic.Tooltip, "Caster base 5% + crit rating 5 (5%), times");
            Assert.IsFalse(magic.Tooltip.Contains("Killer Instinct"));
        }

        [TestMethod]
        public void DualWield_HitChance_IsMainSlashOff_WithTheOffHandCap_AndEqualIsOneNumber()
        {
            var input = Inputs();
            input.OffWeaponAttackSkill = 400;
            input.OffWeaponSkill = Skill.DualWield;

            // main 500 vs 400 melee defense, off 400 (capped at Dual Wield) vs 400
            var rows = Rows(CharacterDetailsProjector.Build(input, Baseline()));
            var main = CharacterDetailsProjector.Pct(SkillCheck.GetSkillChance(500u, 400u));
            var off = CharacterDetailsProjector.Pct(SkillCheck.GetSkillChance(400u, 400u));
            Assert.AreNotEqual(main, off, "positive control: the cap lowers the off-hand chance");
            Assert.AreEqual($"{main} / {off}", rows["hit_melee"].Value);
            Assert.AreEqual("Main hand 500 effective Heavy Weapons, off hand 400 effective Dual Wield (capped by Dual Wield) vs 400 average Melee Defense of level 300 Thread monsters. Weapon modifier: the better of your two weapons." + Tail,
                rows["hit_melee"].Tooltip);
            StringAssert.EndsWith(rows["evade_melee"].Tooltip, "Weapon modifier: the better of your two weapons." + Tail);

            // an uncapped off hand names its own skill and has no cap note
            input.OffWeaponAttackSkill = 450;
            input.OffWeaponSkill = Skill.Sword;
            var uncapped = Rows(CharacterDetailsProjector.Build(input, Baseline()))["hit_melee"];
            StringAssert.StartsWith(uncapped.Tooltip, "Main hand 500 effective Heavy Weapons, off hand 450 effective Sword vs 400");
            Assert.IsFalse(uncapped.Tooltip.Contains("capped"));

            // equal chances: one number
            input.OffWeaponAttackSkill = 500;
            Assert.AreEqual(main, Rows(CharacterDetailsProjector.Build(input, Baseline()))["hit_melee"].Value);

            // a single weapon is unchanged: no off hand, no weapon-modifier sentence
            var single = Rows(CharacterDetailsProjector.Build(Inputs(), Baseline()));
            Assert.AreEqual(main, single["hit_melee"].Value);
            Assert.IsFalse(single["evade_melee"].Tooltip.Contains("better of your two"));
        }

        [TestMethod]
        public void CritChance_BaseNeverNegative_AndNeverPrintsNegativeZero()
        {
            // crit rating 5 (5 points) against a held pre-resist a hair under 5%: float error would make the base slightly negative
            var input = Inputs();
            input.HeldCritPreResist = 0.0499999f;
            input.KillerInstinctCritBonus = 0.0f;

            StringAssert.StartsWith(Rows(CharacterDetailsProjector.Build(input, null))["crit_chance"].Tooltip, "Weapon base 0% + crit rating 5 (5%)");

            Assert.AreEqual("0%", CharacterDetailsProjector.PctPoints(-1e-9));
            Assert.AreEqual("0%", CharacterDetailsProjector.PctPoints(-0.0));
            Assert.AreEqual("6%", CharacterDetailsProjector.PctPoints(0.06));
            Assert.AreEqual("-2.5%", CharacterDetailsProjector.PctPoints(-0.025), "a real negative still prints its sign");
        }

        [TestMethod]
        public void DualWield_ShowsMainSlashOff_OnlyWhenTheyDiffer()
        {
            var input = Inputs();
            input.OffCritPreResist = 0.25f;
            input.OffCritDamageMod = 2.5f;

            var rows = Rows(CharacterDetailsProjector.Build(input, null));
            Assert.AreEqual("15.0% / 25.0%", rows["crit_chance"].Value);
            StringAssert.Contains(rows["crit_chance"].Tooltip, "Main hand shown first, then the off hand");

            var main = CharacterDetailsProjector.CritDamageMultiplier(CombatMode.Melee, 2.0f, 30, 15);
            var off = CharacterDetailsProjector.CritDamageMultiplier(CombatMode.Melee, 2.5f, 30, 15);
            Assert.AreEqual($"x{main.ToString("0.00", CultureInfo.InvariantCulture)} / x{off.ToString("0.00", CultureInfo.InvariantCulture)}", rows["crit_damage"].Value);
            Assert.AreEqual("x2.23 / x2.79", rows["crit_damage"].Value);

            input.OffCritPreResist = 0.15f;
            input.OffCritDamageMod = 2.0f;
            var same = Rows(CharacterDetailsProjector.Build(input, null));
            Assert.AreEqual("15.0%", same["crit_chance"].Value);
            Assert.AreEqual("x2.23", same["crit_damage"].Value);
        }

        [TestMethod]
        public void CritDamage_IsTheRealHelpersMultiplier_TimesTheAdditiveRatingStep()
        {
            // the real helpers with no weapon: DamageEvent's (1 + default CriticalMultiplier 1.0) and the caster's default 1.0
            var physicalMod = AttackerTerms.PhysicalCritDamageMod(null, null, null, null);
            var magicMod = AttackerTerms.MagicCritDamageMod(null, null, null, null);
            Assert.AreEqual(2.0f, physicalMod);
            Assert.AreEqual(1.0f, magicMod);

            // damage rating 30 (x1.30) and crit damage rating 15 add: ratings 45 -> x1.45, so a crit is x1.45/1.30 above the plain damage rating
            var input = Inputs();
            input.HeldCritDamageMod = physicalMod;
            var row = Rows(CharacterDetailsProjector.Build(input, null))["crit_damage"];
            Assert.AreEqual("x2.23", row.Value, "2.0 x 1.45 / 1.30");
            Assert.AreEqual("Crit damage", row.Label);
            StringAssert.Contains(row.Tooltip, "A crit hits for the weapon's maximum damage times x2.00");
            StringAssert.Contains(row.Tooltip, "Crit damage rating 15 adds to your damage rating 30, a further x1.12.");

            // magic: half the spell's maximum damage times the crit multiplier is added, so x(1 + 0.5 x mod)
            var caster = Inputs(CombatMode.Magic);
            caster.HeldCritDamageMod = magicMod;
            var magicRow = Rows(CharacterDetailsProjector.Build(caster, null))["crit_damage"];
            Assert.AreEqual("x1.67", magicRow.Value, "1.5 x 1.45 / 1.30");
            StringAssert.Contains(magicRow.Tooltip, "x1.50 of a maximum-damage cast");

            // positive control: the crit damage rating moves it
            input.CritDamageRating = 0;
            Assert.AreEqual("x2.00", Rows(CharacterDetailsProjector.Build(input, null))["crit_damage"].Value);
        }

        // ---- the 99% accuracy bar ----

        [TestMethod]
        public void AccuracyBar99_Reachable_ReportsTheLowestBar()
        {
            const uint defense = 400;
            var skills = Enumerable.Range(0, 101).Select(p => (uint)(400 + 2 * p)).ToArray();

            var expected = Enumerable.Range(0, 101).First(p => SkillCheck.GetSkillChance(skills[p], defense) >= 0.99);
            Assert.IsTrue(expected > 0 && expected < 100, "positive control: the threshold is inside the bar");

            Assert.AreEqual($"{expected}%", CharacterDetailsProjector.AccuracyBarFor99(skills, defense));
            Assert.IsTrue(HitChance.Calculate(skills[expected - 1], defense) < 0.99, "one percent lower falls short");
        }

        [TestMethod]
        public void AccuracyBar99_AlreadyThereAtZero_IsAnyBar()
        {
            var skills = Enumerable.Repeat(1000u, 101).ToArray();
            Assert.AreEqual("Any bar", CharacterDetailsProjector.AccuracyBarFor99(skills, 400));
        }

        [TestMethod]
        public void AccuracyBar99_Unreachable_SaysSoWithTheFullBarChance()
        {
            var skills = Enumerable.Range(0, 101).Select(p => (uint)(300 + p)).ToArray();
            var full = HitChance.Calculate(400, 400);

            Assert.AreEqual($"Not reachable - {CharacterDetailsProjector.Pct(full)} at full bar", CharacterDetailsProjector.AccuracyBarFor99(skills, 400));
            Assert.AreEqual("Not reachable - 50.0% at full bar", CharacterDetailsProjector.AccuracyBarFor99(skills, 400));
        }

        [TestMethod]
        public void MissileStance_ShowsTheFullBarHitAndTheAccuracyRow()
        {
            var rows = Rows(CharacterDetailsProjector.Build(Inputs(CombatMode.Missile), Baseline()));

            Assert.IsTrue(rows.ContainsKey("hit_missile"));
            Assert.IsFalse(rows.ContainsKey("hit_melee"));
            StringAssert.EndsWith(rows["accuracy_bar_99"].Tooltip, Tail);
            Assert.AreEqual(1.0f, CharacterDetailsProjector.MissileAccuracyBar, "owner default: a full bar, one constant");
        }

        // ---- player values only ----

        [TestMethod]
        public void Dto_Shape_ReflectionWhitelist()
        {
            string[] Props(Type t) => t.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

            CollectionAssert.AreEqual(new[] { "BaselineAvailable", "BaselineNote", "CharacterGuid", "Level", "Sections" }, Props(typeof(ACE.Server.Managers.Market.CharacterDetails)));
            CollectionAssert.AreEqual(new[] { "Collapsed", "Columns", "Corner", "Key", "Kind", "Rows", "Tables", "Title" }, Props(typeof(CharacterDetailsSection)));
            CollectionAssert.AreEqual(new[] { "Columns", "Corner", "Key", "Rows" }, Props(typeof(CharacterDetailsTable)));
            CollectionAssert.AreEqual(new[] { "Cells", "Children", "Key", "Label", "Tooltip", "Value" }, Props(typeof(CharacterDetailsRow)));

            // and the only non-string payload is the character's own identity and level
            Assert.AreEqual(typeof(long), typeof(ACE.Server.Managers.Market.CharacterDetails).GetProperty("CharacterGuid").PropertyType);
            Assert.AreEqual(typeof(int), typeof(ACE.Server.Managers.Market.CharacterDetails).GetProperty("Level").PropertyType);
        }

        // ---- the /abilities points command keeps its formatter ----

        [TestMethod]
        public void PointsCommand_StillRepliesThroughTheFormatter_AndTheDetailsViewHasNoPointsRows()
        {
            // the literal /abilities points sentences before they moved (origin/master 031aaaa91, ClassAbilityCommands.HandlePoints)
            var previous = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

                Assert.AreEqual("Luminance purchases: 3 bought so far; the next point costs 1,234,567 Luminance. Exchange it at the reckoning-stone by the Skillmaster in the Drift Network. Prices rise with each point and there is no cap.",
                    ClassAbilityPointPriceText.Luminance(3, 1234567));
                Assert.AreEqual("Experience purchases: unlocked at level 275 (you are 120).",
                    ClassAbilityPointPriceText.Experience(120, 275, 0, 200000000000, 0));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }

            const string file = "ACE.Server/Command/Handlers/ClassAbilityCommands.cs";
            const string sig = "private static void HandlePoints(Session session)";
            AssertLineOnce(file, sig, "Reply(session, ClassAbilityPointPriceText.Luminance(purchased, nextCost));");
            AssertLineOnce(file, sig, "Reply(session, ClassAbilityPointPriceText.Experience(player.Level ?? 1, minLevel, xpPurchased, nextXpCost, player.AvailableExperience ?? 0));");
            AssertNoLineContains(file, sig, "bought so far");

            var rows = Rows(CharacterDetailsProjector.Build(Inputs(), Baseline()));
            Assert.IsFalse(rows.Keys.Any(k => k.StartsWith("points_") || k.StartsWith("lum")));
        }
    }
}
