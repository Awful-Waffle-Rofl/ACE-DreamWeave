using System;
using System.Collections.Generic;
using System.Linq;

using ACE.DatLoader.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// ArchetypeClassifier and its inputs, all hand-built: no database, no dat, no PropertyManager. Also pins that the
    /// new DungeonStatProfile.Archetype field cannot move DungeonBandStandard.Compute (World Events share it), that the
    /// one-argument DungeonStatProfile.FromWeenie (the runtime resolver) still reads no archetype facts, and that
    /// WieldedTreasureMath reproduces the engine's wielded-treasure roll.
    /// </summary>
    [TestClass]
    public class ArchetypeClassifierTests
    {
        // Every skill: (Strength + Coordination) / 3. With the attributes below the term is 200.
        private static SkillFormula FormulaOf(Skill skill) => new SkillFormula(PropertyAttribute.Strength, PropertyAttribute.Coordination, 3);

        private static readonly IReadOnlyDictionary<PropertyAttribute, uint> Attrs300 = new Dictionary<PropertyAttribute, uint>
        {
            [PropertyAttribute.Strength] = 300,
            [PropertyAttribute.Coordination] = 300,
        };

        private static DungeonStatProfile Profile(int level, (Skill, uint)[] skills, ArchetypeFacts facts = null,
            uint health = 0, uint damage = 0, uint armor = 0, IReadOnlyDictionary<PropertyAttribute, uint> attributes = null)
            => new DungeonStatProfile(level, health, skills.ToDictionary(s => s.Item1, s => s.Item2), damage, armor, 0, 0.0, attributes, facts);

        private static SpellRoll Dmg(double p, Skill school = Skill.WarMagic) => new SpellRoll(p, school);
        private static SpellRoll Other(double p) => new SpellRoll(p, null);

        private static ArchetypeFacts Facts(bool launcher = false, double treasure = 0.0, bool hasTreasure = false, SpellRoll[] rolls = null,
            (Skill, uint)[] ranks = null, int style = 0)
            => new ArchetypeFacts(ranks?.ToDictionary(r => r.Item1, r => r.Item2), launcher, hasTreasure || treasure > 0, null, rolls, treasure, style);

        private static ArchetypeResult Run(DungeonStatProfile p, ArchetypeBandStandard s = null) => ArchetypeClassifier.Classify(p, s, FormulaOf);

        private static CreatureArchetype Kind(DungeonStatProfile p, ArchetypeBandStandard s = null) => Run(p, s).Archetype;

        private static readonly (Skill, uint)[] SwordWar = { (Skill.Sword, 400u), (Skill.WarMagic, 400u) };
        private static readonly (Skill, uint)[] SwordBow = { (Skill.Sword, 400u), (Skill.Bow, 400u) };

        // ---- MagicShare: TryRollSpell_Unconditional semantics ---------------------------------------------

        [TestMethod]
        public void CastProbability_reads_a_stored_value_the_way_the_engine_does()
        {
            Assert.AreEqual(0.05, ArchetypeFacts.CastProbability(2.05f), 1e-6, "2.05 is a 5% chance");
            Assert.AreEqual(0.02, ArchetypeFacts.CastProbability(2.0f), 1e-6, "base 2.0 alone is 2%");
            Assert.AreEqual(0.015, ArchetypeFacts.CastProbability(1.5f), 1e-6, "a value at or below 2.0 is a percentage");
            Assert.AreEqual(3.0, ArchetypeFacts.CastProbability(5.0f), 1e-6, "above 2.0 it is the excess; the share maths clamps a probability to 1");
        }

        [TestMethod]
        public void MagicShare_is_the_first_success_chance_of_a_sequential_walk()
        {
            // Two damaging 0.2 spells: 0.2 + 0.8 x 0.2.
            var two = Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.2), Dmg(0.2) }));
            Assert.AreEqual(0.36, ArchetypeClassifier.MagicShareOf(two), 1e-12);

            // A non-damaging spell EARLIER in the book shadows the damaging one: 0.5 x 0.4.
            var shadowed = Profile(100, SwordWar, Facts(rolls: new[] { Other(0.5), Dmg(0.4) }));
            Assert.AreEqual(0.20, ArchetypeClassifier.MagicShareOf(shadowed), 1e-12);

            // ...and a non-damaging one AFTER it does not.
            var after = Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.4), Other(0.5) }));
            Assert.AreEqual(0.40, ArchetypeClassifier.MagicShareOf(after), 1e-12);

            Assert.AreEqual(0.0, ArchetypeClassifier.MagicShareOf(Profile(100, SwordWar, Facts())), "an empty book");
            Assert.AreEqual(0.0, ArchetypeClassifier.MagicShareOf(null));
        }

        [TestMethod]
        public void A_damaging_spell_needs_a_usable_skill_in_its_school_but_still_shadows_later_spells()
        {
            var rolls = new[] { Dmg(0.5, Skill.LifeMagic), Dmg(0.4, Skill.WarMagic) };

            // War only: the Life spell contributes 0 yet consumes its trial: 0.5 x 0.4.
            Assert.AreEqual(0.20, ArchetypeClassifier.MagicShareOf(Profile(100, SwordWar, Facts(rolls: rolls))), 1e-12);

            // A zero-InitLevel School row is not usable.
            var zeroed = Profile(100, new[] { (Skill.Sword, 400u), (Skill.WarMagic, 0u), (Skill.LifeMagic, 300u) }, Facts(rolls: rolls));
            Assert.AreEqual(0.50, ArchetypeClassifier.MagicShareOf(zeroed), 1e-12, "only the Life spell has its skill");
        }

        // ---- Caster ---------------------------------------------------------------------------------------

        [TestMethod]
        public void A_creature_that_casts_damage_on_enough_attacks_is_a_Caster_with_the_threshold_inclusive()
        {
            Assert.AreEqual(0.40, ArchetypeClassifier.CasterShare);

            Assert.AreEqual(CreatureArchetype.Caster, Kind(Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.5) }), attributes: Attrs300)));
            Assert.AreEqual(CreatureArchetype.Caster, Kind(Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.40) }))), "exactly CasterShare");

            var just = Kind(Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.399) })));
            Assert.AreEqual(CreatureArchetype.Hybrid, just, "0.399 magic is no Caster but its 0.601 melee leaves magic as a Hybrid second mode");
        }

        [TestMethod]
        public void A_Caster_is_decided_by_the_mix_not_by_skill_magnitude()
        {
            // The census regression: a monster with a far higher melee skill than magic skill, but a book that fires half the time.
            var p = Profile(100, new[] { (Skill.Sword, 900u), (Skill.WarMagic, 100u) }, Facts(rolls: new[] { Dmg(0.5) }));
            var r = Run(p);

            Assert.AreEqual(CreatureArchetype.Caster, r.Archetype);
            Assert.IsTrue(r.Om > r.Oc, "melee skill dwarfs the magic skill, and it does not matter");
        }

        [TestMethod]
        public void Spells_without_a_school_skill_do_not_make_a_Caster()
        {
            var p = Profile(100, new[] { (Skill.Sword, 400u) }, Facts(rolls: new[] { Dmg(0.9) }));
            Assert.AreEqual(0.0, ArchetypeClassifier.MagicShareOf(p));
            Assert.AreEqual(CreatureArchetype.MeleeTank, Kind(p));
        }

        // ---- Ranged ---------------------------------------------------------------------------------------

        [TestMethod]
        public void A_launcher_wielder_with_a_missile_skill_is_Ranged()
        {
            var p = Profile(100, SwordBow, Facts(launcher: true), attributes: Attrs300);
            var r = Run(p);

            Assert.AreEqual(CreatureArchetype.Ranged, r.Archetype);
            Assert.AreEqual(1.0, r.MissileShare);
            Assert.AreEqual(1.0, r.MissileMix);
            Assert.AreEqual(0.0, r.MeleeMix);
        }

        [TestMethod]
        public void A_missile_weapon_without_a_missile_skill_is_not_a_missile_attacker()
        {
            var p = Profile(100, new[] { (Skill.Sword, 400u) }, Facts(launcher: true));
            Assert.AreEqual(0.0, ArchetypeClassifier.MissileShareOf(p));
            Assert.AreEqual(CreatureArchetype.MeleeTank, Kind(p));

            var zero = Profile(100, new[] { (Skill.Sword, 400u), (Skill.Bow, 0u) }, Facts(launcher: true));
            Assert.AreEqual(0.0, ArchetypeClassifier.MissileShareOf(zero), "a zero-InitLevel Bow row is not usable");
        }

        [TestMethod]
        public void Wielded_treasure_gives_a_fractional_missile_share_and_Ranged_needs_half_the_attacks()
        {
            Assert.AreEqual(0.50, ArchetypeClassifier.RangedShare);

            Assert.AreEqual(CreatureArchetype.Ranged, Kind(Profile(100, SwordBow, Facts(treasure: 0.5))), "exactly RangedShare");
            Assert.AreEqual(CreatureArchetype.Hybrid, Kind(Profile(100, SwordBow, Facts(treasure: 0.499))), "0.499 missile leaves 0.501 melee: a Hybrid");
            Assert.AreEqual(CreatureArchetype.Hybrid, Kind(Profile(100, SwordBow, Facts(treasure: 0.3))));

            // No resolved probability (e.g. the table holds no launcher): nothing missile.
            Assert.AreEqual(CreatureArchetype.MeleeTank, Kind(Profile(100, SwordBow, Facts(hasTreasure: true))));
        }

        [TestMethod]
        public void Magic_is_rolled_first_so_it_takes_its_share_of_a_launcher_wielders_attacks()
        {
            var launcher = Facts(launcher: true, rolls: new[] { Dmg(0.2) });
            var mixed = Run(Profile(100, new[] { (Skill.Bow, 400u), (Skill.WarMagic, 400u) }, launcher));

            Assert.AreEqual(0.2, mixed.MagicShare, 1e-12);
            Assert.AreEqual(0.8, mixed.MissileMix, 1e-12, "(1 - magic) x 1.0");
            Assert.AreEqual(CreatureArchetype.Ranged, mixed.Archetype);

            // Magic 0.5 versus missile mix 0.5: both thresholds met, magic is at least as large: Caster.
            var even = Run(Profile(100, new[] { (Skill.Bow, 400u), (Skill.WarMagic, 400u) }, Facts(launcher: true, rolls: new[] { Dmg(0.5) })));
            Assert.AreEqual(CreatureArchetype.Caster, even.Archetype);

            // Magic 0.4 versus missile mix 0.6: the larger mode wins.
            var missileLarger = Run(Profile(100, new[] { (Skill.Bow, 400u), (Skill.WarMagic, 400u) }, Facts(launcher: true, rolls: new[] { Dmg(0.4) })));
            Assert.AreEqual(CreatureArchetype.Ranged, missileLarger.Archetype);
        }

        // ---- Hybrid ---------------------------------------------------------------------------------------

        [TestMethod]
        public void Hybrid_needs_the_two_largest_modes_to_reach_the_floor_inclusive()
        {
            Assert.AreEqual(0.25, ArchetypeClassifier.HybridFloor);

            Assert.AreEqual(CreatureArchetype.Hybrid, Kind(Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.25) }))), "magic exactly at the floor, melee 0.75");

            var below = Kind(Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.249) })));
            Assert.IsTrue(below == CreatureArchetype.MeleeTank || below == CreatureArchetype.MeleeDps, $"0.249 magic is melee, got {below}");

            // magic + missile with melee small: magic 0.3, missile treasure 0.5 -> missile mix 0.35, melee 0.35.
            var all3 = Run(Profile(100, new[] { (Skill.Sword, 400u), (Skill.Bow, 400u), (Skill.WarMagic, 400u) }, Facts(treasure: 0.5, rolls: new[] { Dmg(0.3) })));
            Assert.AreEqual(CreatureArchetype.Hybrid, all3.Archetype);
            Assert.AreEqual(0.35, all3.MissileMix, 1e-12);
        }

        // ---- Melee split ----------------------------------------------------------------------------------

        private static readonly ArchetypeBandStandard Standard = new ArchetypeBandStandard(damage: 100, armor: 1000, health: 5000,
            new Dictionary<Skill, uint> { [Skill.Sword] = 600 });

        [TestMethod]
        public void A_melee_creature_tougher_than_it_is_lethal_is_a_MeleeTank()
        {
            // Om = 200 + 400 = 600 = standard, so lethality = ln(1) + ln(1) = 0; toughness = ln2 + ln2 + 0 > 0.
            var p = Profile(100, new[] { (Skill.Sword, 400u) }, Facts(), health: 10000, damage: 100, armor: 2000, attributes: Attrs300);

            var r = Run(p, Standard);

            Assert.AreEqual(CreatureArchetype.MeleeTank, r.Archetype);
            Assert.AreEqual(Math.Log(2) * 2, r.Toughness, 1e-9);
            Assert.AreEqual(0.0, r.Lethality, 1e-9);
            Assert.AreEqual(1.0, r.MeleeMix);
        }

        [TestMethod]
        public void A_melee_creature_more_lethal_than_it_is_tough_is_a_MeleeDps()
        {
            // Om = 200 + 1000 = 1200 = 2x standard; toughness = ln.5 + ln.5 + 0 < lethality = 0 + ln2.
            var p = Profile(100, new[] { (Skill.Sword, 1000u) }, Facts(), health: 2500, damage: 100, armor: 500, attributes: Attrs300);

            var r = Run(p, Standard);

            Assert.AreEqual(CreatureArchetype.MeleeDps, r.Archetype);
            Assert.AreEqual(Math.Log(0.5) * 2, r.Toughness, 1e-9);
            Assert.AreEqual(Math.Log(2), r.Lethality, 1e-9);
        }

        [TestMethod]
        public void The_melee_split_is_a_tank_exactly_at_zero_and_missing_data_reads_as_zero_terms()
        {
            var even = Profile(100, new[] { (Skill.Sword, 400u) }, Facts(), health: 5000, damage: 100, armor: 1000, attributes: Attrs300);
            Assert.AreEqual(CreatureArchetype.MeleeTank, Kind(even, Standard));

            var bare = Profile(100, new[] { (Skill.Sword, 400u) }, Facts(), attributes: Attrs300);
            var r = Run(bare, null);
            Assert.AreEqual(CreatureArchetype.MeleeTank, r.Archetype);
            Assert.AreEqual(0.0, r.Toughness);
            Assert.AreEqual(0.0, r.Lethality);
            Assert.AreEqual(0.0, ArchetypeClassifier.LnRatio(0, 5));
            Assert.AreEqual(0.0, ArchetypeClassifier.LnRatio(5, 0));
        }

        [TestMethod]
        public void The_melee_skill_standard_falls_back_to_the_highest_melee_median()
        {
            var s = new ArchetypeBandStandard(100, 1000, 5000, new Dictionary<Skill, uint> { [Skill.Mace] = 500, [Skill.Axe] = 700, [Skill.WarMagic] = 9000 });

            Assert.AreEqual(500.0, s.MeleeSkillStandard(Skill.Mace));
            Assert.AreEqual(700.0, s.MeleeSkillStandard(Skill.Sword), "no Sword median: the highest MELEE median, never a magic one");
        }

        [TestMethod]
        public void Ranks_are_part_of_a_skills_effective_value()
        {
            var p = Profile(100, new[] { (Skill.Sword, 800u) }, Facts(ranks: new[] { (Skill.Sword, 150u) }));
            Assert.AreEqual(950.0, Run(p).Om);
        }

        // ---- Unclassified ---------------------------------------------------------------------------------

        [TestMethod]
        public void No_usable_offense_is_Unclassified()
        {
            Assert.AreEqual(CreatureArchetype.Unclassified, Kind(Profile(100, new (Skill, uint)[0])));
            Assert.AreEqual(CreatureArchetype.Unclassified, Kind(Profile(100, new[] { (Skill.MeleeDefense, 500u) })));
            Assert.AreEqual(CreatureArchetype.Unclassified, Kind(null));
            Assert.AreEqual(CreatureArchetype.Unclassified, Kind(DungeonStatProfile.Empty));
        }

        // ---- inputs ---------------------------------------------------------------------------------------

        [TestMethod]
        public void DamagingSpellSkill_counts_only_harmful_projectile_family_spells_of_the_three_attack_schools()
        {
            Assert.AreEqual(Skill.WarMagic, ArchetypeFacts.DamagingSpellSkill(MagicSchool.WarMagic, SpellType.Projectile, false));
            Assert.AreEqual(Skill.VoidMagic, ArchetypeFacts.DamagingSpellSkill(MagicSchool.VoidMagic, SpellType.Projectile, false));
            Assert.AreEqual(Skill.LifeMagic, ArchetypeFacts.DamagingSpellSkill(MagicSchool.LifeMagic, SpellType.LifeProjectile, false));
            Assert.AreEqual(Skill.VoidMagic, ArchetypeFacts.DamagingSpellSkill(MagicSchool.VoidMagic, SpellType.EnchantmentProjectile, false));

            Assert.IsNull(ArchetypeFacts.DamagingSpellSkill(MagicSchool.WarMagic, SpellType.Projectile, true), "beneficial");
            Assert.IsNull(ArchetypeFacts.DamagingSpellSkill(MagicSchool.LifeMagic, SpellType.Transfer, false), "a drain is not a projectile");
            Assert.IsNull(ArchetypeFacts.DamagingSpellSkill(MagicSchool.CreatureEnchantment, SpellType.Enchantment, false));
            Assert.IsNull(ArchetypeFacts.DamagingSpellSkill(MagicSchool.ItemEnchantment, SpellType.Projectile, false), "not an attack school");
        }

        private static Weenie Bow() => new Weenie { WeenieClassId = 100, WeenieType = WeenieType.MissileLauncher };
        private static Weenie Sword() => new Weenie { WeenieClassId = 101, WeenieType = WeenieType.MeleeWeapon };

        [TestMethod]
        public void FromWeenie_reads_launcher_treasure_ranks_spell_rolls_in_book_order_and_default_style()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 5000,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 120, [PropertyInt.DefaultCombatStyle] = (int)CombatStyle.Magic },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.WieldedTreasureType] = 77 },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    [Skill.Bow] = new PropertiesSkill { InitLevel = 300, LevelFromPP = 40 },
                    [Skill.Sword] = new PropertiesSkill { InitLevel = 100 },
                },
                PropertiesCreateList = new List<PropertiesCreateList>
                {
                    new PropertiesCreateList { DestinationType = DestinationType.Contain, WeenieClassId = 100 },   // a bow in a pack: not wielded
                },
                PropertiesSpellBook = new Dictionary<int, float> { [1] = 2.05f, [2] = 2.10f, [3] = 5.0f },
            };

            Func<uint, Weenie> weenieOf = id => id == 100 ? Bow() : id == 101 ? Sword() : null;
            Func<int, Skill?> spellOf = id => id == 1 ? Skill.WarMagic : id == 2 ? Skill.WarMagic : (Skill?)null;
            Func<uint, double> treasureOf = id => id == 77 ? 0.6 : 0.0;

            var f = DungeonStatProfile.FromWeenie(weenie, weenieOf, spellOf, treasureOf).Archetype;

            Assert.IsFalse(f.WieldsLauncher, "a Contain row is not wielded");
            Assert.IsTrue(f.HasWieldedTreasure);
            Assert.AreEqual(0.6, f.TreasureMissileProbability);
            Assert.AreEqual(40u, f.SkillRanks[Skill.Bow]);
            Assert.IsFalse(f.SkillRanks.ContainsKey(Skill.Sword), "zero ranks are not recorded");
            Assert.AreEqual(2, f.DamagingSpellCounts[Skill.WarMagic]);
            Assert.AreEqual(2, f.TotalDamagingSpells);
            Assert.IsTrue(f.IsDefaultStyleCaster);

            Assert.AreEqual(3, f.SpellRolls.Count);
            Assert.AreEqual(0.05, f.SpellRolls[0].Probability, 1e-6);
            Assert.AreEqual(Skill.WarMagic, f.SpellRolls[0].DamagingSkill);
            Assert.AreEqual(0.10, f.SpellRolls[1].Probability, 1e-6);
            Assert.IsNull(f.SpellRolls[2].DamagingSkill, "a non-damaging spell is kept as a trial");

            weenie.PropertiesCreateList.Add(new PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = 101 });
            Assert.IsFalse(DungeonStatProfile.FromWeenie(weenie, weenieOf, spellOf, treasureOf).Archetype.WieldsLauncher, "a wielded sword is not a launcher");

            weenie.PropertiesCreateList.Add(new PropertiesCreateList { DestinationType = DestinationType.WieldTreasure, WeenieClassId = 100 });
            Assert.IsTrue(DungeonStatProfile.FromWeenie(weenie, weenieOf, spellOf, treasureOf).Archetype.WieldsLauncher);

            // No treasure delegate (the three-argument form): the flag is read, the probability is not.
            var noResolver = DungeonStatProfile.FromWeenie(weenie, weenieOf, spellOf).Archetype;
            Assert.IsTrue(noResolver.HasWieldedTreasure);
            Assert.AreEqual(0.0, noResolver.TreasureMissileProbability);

            weenie.PropertiesDID.Clear();
            Assert.IsFalse(DungeonStatProfile.FromWeenie(weenie, weenieOf, spellOf, treasureOf).Archetype.HasWieldedTreasure);
        }

        [TestMethod]
        public void The_one_argument_FromWeenie_reads_no_archetype_facts_and_matches_the_extended_form_on_every_legacy_field()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 5001,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 90 },
                PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.WieldedTreasureType] = 77 },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill> { [Skill.Bow] = new PropertiesSkill { InitLevel = 300, LevelFromPP = 40 } },
                PropertiesSpellBook = new Dictionary<int, float> { [1] = 2.0f },
            };

            var plain = DungeonStatProfile.FromWeenie(weenie);
            var rich = DungeonStatProfile.FromWeenie(weenie, id => null, id => Skill.WarMagic, id => 0.5);

            Assert.AreSame(ArchetypeFacts.Empty, plain.Archetype, "the runtime resolver pays nothing for the facts");
            Assert.AreEqual(1, rich.Archetype.TotalDamagingSpells);

            Assert.AreEqual(plain.Level, rich.Level);
            Assert.AreEqual(plain.Health, rich.Health);
            Assert.AreEqual(plain.MaxBodyDamage, rich.MaxBodyDamage);
            Assert.AreEqual(plain.MaxBaseArmor, rich.MaxBaseArmor);
            Assert.AreEqual(plain.MaxSpellTier, rich.MaxSpellTier);
            Assert.AreEqual(plain.HasData, rich.HasData);
            CollectionAssert.AreEqual(plain.Skills.OrderBy(k => k.Key).ToList(), rich.Skills.OrderBy(k => k.Key).ToList());
        }

        [TestMethod]
        public void Archetype_facts_do_not_count_toward_HasData_and_survive_WithLevel()
        {
            var factsOnly = new DungeonStatProfile(100, 0, null, 0, 0, archetype: Facts(launcher: true, treasure: 0.5, rolls: new[] { Dmg(0.5) }));
            Assert.IsFalse(factsOnly.HasData, "facts alone say nothing about the band's combat standard");

            var f = Facts(launcher: true);
            var p = new DungeonStatProfile(100, 5, null, 10, 10, archetype: f);
            Assert.AreSame(f, p.WithLevel(200).Archetype);
            Assert.AreSame(DungeonStatProfile.Empty.Archetype, ArchetypeFacts.Empty);
        }

        // ---- wielded treasure: exact probability versus a verbatim replica of the engine ----------------------

        private static WieldedRow Row(uint wcid, float p, bool start = false, bool sub = false, bool cont = false) => new WieldedRow(wcid, p, start, sub, cont);

        private static bool IsHit(uint wcid) => wcid >= 100;

        [TestMethod]
        public void WieldedTreasureMath_handles_the_simple_shapes()
        {
            Assert.AreEqual(0.0, WieldedTreasureMath.ProbabilityOfAny(null, IsHit));
            Assert.AreEqual(0.0, WieldedTreasureMath.ProbabilityOfAny(new WieldedRow[0], IsHit));

            // One row.
            Assert.AreEqual(0.3, WieldedTreasureMath.ProbabilityOfAny(new[] { Row(100, 0.3f, start: true) }, IsHit), 1e-6);

            // Two rows of ONE set share a single draw: mutually exclusive, so the launcher keeps its own 0.3.
            Assert.AreEqual(0.3, WieldedTreasureMath.ProbabilityOfAny(new[] { Row(100, 0.3f, start: true), Row(1, 0.5f) }, IsHit), 1e-6);

            // Order matters: the launcher second wins only on the slice (0.5, 0.8].
            Assert.AreEqual(0.3, WieldedTreasureMath.ProbabilityOfAny(new[] { Row(1, 0.5f, start: true), Row(100, 0.3f) }, IsHit), 1e-6);

            // Two independent sets (SetStart) each 0.3: 1 - 0.7^2.
            Assert.AreEqual(1 - 0.7 * 0.7, WieldedTreasureMath.ProbabilityOfAny(new[] { Row(100, 0.3f, start: true), Row(100, 0.3f, start: true) }, IsHit), 1e-6);

            // A set whose probabilities pass 1.0: the second row can only win on the part of its slice below 1.
            Assert.AreEqual(0.4, WieldedTreasureMath.ProbabilityOfAny(new[] { Row(1, 0.6f, start: true), Row(100, 0.6f) }, IsHit), 1e-6);

            // No launcher in the table at all.
            Assert.AreEqual(0.0, WieldedTreasureMath.ProbabilityOfAny(new[] { Row(1, 1.0f, start: true), Row(2, 1.0f, start: true) }, IsHit));
        }

        [TestMethod]
        public void WieldedTreasureMath_a_losing_rows_subset_is_skipped_and_a_winning_rows_subset_is_rolled()
        {
            // Row A (0.5, plain item) has a subset [B (1.0, launcher)]. The subset is rolled only when A wins.
            var rows = new[] { Row(1, 0.5f, start: true, sub: true), Row(100, 1.0f, start: true) };
            Assert.AreEqual(0.5, WieldedTreasureMath.ProbabilityOfAny(rows, IsHit), 1e-9);
        }

        // The engine's GenerateWieldedTreasureSets, copied statement for statement (WorldObject_Equipment.cs:50-110), with the
        // random source and the item factory replaced.
        private static void EngineRoll(IReadOnlyList<WieldedRow> items, List<uint> results, ref int curIdx, Func<double> next, bool skip = false)
        {
            var rng = next();
            var probability = 0.0f;
            var rolled = false;
            var continued = false;

            for (; curIdx < items.Count; curIdx++)
            {
                var item = items[curIdx];

                if (item.ContinuesPreviousSet)
                {
                    if (!continued)
                    {
                        curIdx--;
                        return;
                    }
                    else
                        continued = false;
                }

                var skipNext = true;

                if (!skip)
                {
                    if (item.SetStart || probability >= 1.0f)
                    {
                        rng = next();
                        probability = 0.0f;
                        rolled = false;
                    }

                    probability += item.Probability;

                    if (rng < probability && !rolled)
                    {
                        rolled = true;
                        skipNext = false;
                        results.Add(item.Wcid);
                    }
                }

                if (item.HasSubSet)
                {
                    curIdx++;
                    EngineRoll(items, results, ref curIdx, next, skipNext);
                    continued = true;
                }
            }
        }

        [TestMethod]
        public void WieldedTreasureMath_matches_a_Monte_Carlo_run_of_the_engines_algorithm_on_random_tables()
        {
            var gen = new Random(20261023);
            var probs = new[] { 0.1f, 0.25f, 0.3f, 0.5f, 0.6f, 0.75f, 1.0f };

            for (var table = 0; table < 40; table++)
            {
                var n = gen.Next(1, 9);
                var rows = new WieldedRow[n];

                for (var i = 0; i < n; i++)
                    rows[i] = new WieldedRow((uint)(gen.Next(0, 2) == 0 ? gen.Next(1, 50) : gen.Next(100, 150)), probs[gen.Next(probs.Length)],
                        gen.Next(0, 3) == 0, gen.Next(0, 4) == 0, gen.Next(0, 5) == 0);

                var exact = WieldedTreasureMath.ProbabilityOfAny(rows, IsHit);

                var mc = new Random(table + 1);
                const int trials = 60000;
                var hits = 0;

                for (var t = 0; t < trials; t++)
                {
                    var results = new List<uint>();
                    var cur = 0;
                    EngineRoll(rows, results, ref cur, () => mc.NextDouble());

                    if (results.Any(IsHit))
                        hits++;
                }

                var observed = hits / (double)trials;

                // 5 sigma of a binomial at p = 0.5 over 60000 trials is about 0.0102.
                Assert.AreEqual(exact, observed, 0.011, $"table {table}: {string.Join(" ", rows.Select(r => $"[{r.Wcid} p{r.Probability}{(r.SetStart ? " S" : "")}{(r.HasSubSet ? " sub" : "")}{(r.ContinuesPreviousSet ? " cont" : "")}]"))}");
            }
        }

        // ---- World Events must be byte-identical --------------------------------------------------------------

        private static DungeonBandStandard ComputeFixture(Func<int, ArchetypeFacts> factsFor)
        {
            var profiles = new[]
            {
                (150u, 300u, 700u, 100u, 600u),
                (160u, 320u, 760u, 200u, 650u),
                (170u, 340u, 820u, 300u, 700u),
                (180u, 360u, 880u, 400u, 750u),
                (190u, 380u, 940u, 500u, 800u),
            };

            var members = profiles.Select((q, i) => new DungeonStatProfile(200, q.Item1 * 10,
                new Dictionary<Skill, uint> { [Skill.Sword] = q.Item4, [Skill.MeleeDefense] = q.Item5, [Skill.WarMagic] = q.Item4 / 2 },
                q.Item2, q.Item3, 4 + i, 0.5 + i * 0.1,
                new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 250 + (uint)i * 10, [PropertyAttribute.Coordination] = 240 + (uint)i * 10 },
                factsFor(i))).ToArray();

            var tables = new Dictionary<string, SpeciesTableDef>
            {
                ["f"] = new SpeciesTableDef { Members = members.Select((m, i) => new SpeciesMemberDef { Wcid = (uint)i + 1, Role = 0 }).ToList() },
            };

            return DungeonBandStandard.Compute(tables, 200, w => members[(int)w - 1], new DungeonRosterBand(1.0, 1.15), 1.0, formulaOf: FormulaOf);
        }

        [TestMethod]
        public void DungeonBandStandard_Compute_output_is_pinned_and_blind_to_archetype_facts()
        {
            var without = ComputeFixture(i => null);
            // Every archetype fact, including the archetype uplift's (effective damage, projectile mean, the four ratings).
            var with = ComputeFixture(i => new ArchetypeFacts(new Dictionary<Skill, uint> { [Skill.Sword] = 999u }, i % 2 == 0, true, null,
                new[] { Dmg(0.3), Other(0.2), new SpellRoll(0.4, null, Skill.CreatureEnchantment) }, 0.5, 512,
                effectiveMaxDamage: 9999, bestProjectileMean: 750, damageRating: 50 + i, damageResistRating: 40, critRating: 30, critDamageRating: 20));

            foreach (var std in new[] { without, with })
            {
                Assert.AreEqual(5, std.SampleCount);
                Assert.AreEqual(1.0, std.SampleLowRatio);
                Assert.AreEqual(340u, std.MaxBodyDamage);
                Assert.AreEqual(820u, std.MaxBaseArmor);
                Assert.AreEqual(6, std.SpellTier);
                Assert.AreEqual(0.7, std.HealthRate, 1e-9);
                Assert.AreEqual(300u, std.MedianFor(Skill.Sword));
                Assert.AreEqual(700u, std.MedianFor(Skill.MeleeDefense));
                Assert.AreEqual(150u, std.MedianFor(Skill.WarMagic));
                Assert.AreEqual(270u, std.AttributeMedianFor(PropertyAttribute.Strength));
                Assert.AreEqual(260u, std.AttributeMedianFor(PropertyAttribute.Coordination));
                // (Str 270 + Coord 260) / 3 = 176.67 -> 177, plus InitLevel.
                Assert.AreEqual(177u + 300u, std.EffectiveSkillMedianFor(Skill.Sword));
                Assert.AreEqual(177u + 700u, std.EffectiveSkillMedianFor(Skill.MeleeDefense));
            }

            Assert.AreEqual(without.SkillMedians.Count, with.SkillMedians.Count);
            Assert.AreEqual(without.EffectiveSkillMedians.Count, with.EffectiveSkillMedians.Count);
            foreach (var kvp in without.EffectiveSkillMedians)
                Assert.AreEqual(kvp.Value, with.EffectiveSkillMedians[kvp.Key], kvp.Key.ToString());
        }

        // ---- harmful vs damaging share, primary archetype (2026-10-23) -----------------------------------------

        private static SpellRoll Debuff(double p, Skill school = Skill.CreatureEnchantment) => new SpellRoll(p, null, school);

        [TestMethod]
        public void HarmfulSpellSkill_counts_every_non_beneficial_spell_of_the_five_casting_schools()
        {
            Assert.AreEqual(Skill.WarMagic, ArchetypeFacts.HarmfulSpellSkill(MagicSchool.WarMagic, false));
            Assert.AreEqual(Skill.LifeMagic, ArchetypeFacts.HarmfulSpellSkill(MagicSchool.LifeMagic, false), "drains");
            Assert.AreEqual(Skill.VoidMagic, ArchetypeFacts.HarmfulSpellSkill(MagicSchool.VoidMagic, false));
            Assert.AreEqual(Skill.CreatureEnchantment, ArchetypeFacts.HarmfulSpellSkill(MagicSchool.CreatureEnchantment, false), "debuffs");
            Assert.AreEqual(Skill.ItemEnchantment, ArchetypeFacts.HarmfulSpellSkill(MagicSchool.ItemEnchantment, false), "armour and weapon debuffs");
            Assert.IsNull(ArchetypeFacts.HarmfulSpellSkill(MagicSchool.CreatureEnchantment, true), "a buff is not harmful");
            Assert.IsNull(ArchetypeFacts.HarmfulSpellSkill(MagicSchool.LifeMagic, true), "a heal is not harmful");
        }

        [TestMethod]
        public void MagicShare_counts_harmful_casts_and_DamagingShare_keeps_the_damaging_only_rule()
        {
            // A debuff caster with one 0.2 bolt behind a 0.5 Creature debuff: harmful 0.5 + 0.5 x 0.2 = 0.6; damaging 0.5 x 0.2 = 0.1.
            var shadow = Profile(100, new[] { (Skill.CreatureEnchantment, 400u), (Skill.WarMagic, 400u) }, Facts(rolls: new[] { Debuff(0.5), Dmg(0.2) }));

            Assert.AreEqual(0.6, ArchetypeClassifier.MagicShareOf(shadow), 1e-12);
            Assert.AreEqual(0.1, ArchetypeClassifier.DamagingShareOf(shadow), 1e-12);

            var r = Run(shadow);
            Assert.AreEqual(CreatureArchetype.Caster, r.Archetype, "a debuff caster reads Caster under the harmful share");
            Assert.AreEqual(0.1, r.DamagingShare, 1e-12);
            Assert.AreEqual(CreatureArchetype.Caster, r.PrimaryArchetype);

            // The harmful skill must be usable too: no Creature Enchantment skill, the debuff is a trial that shadows.
            var unskilled = Profile(100, SwordWar, Facts(rolls: new[] { Debuff(0.5), Dmg(0.2) }));
            Assert.AreEqual(0.1, ArchetypeClassifier.MagicShareOf(unskilled), 1e-12);

            // A roll built the old way (harmful defaults to damaging) is unchanged.
            Assert.AreEqual(Skill.WarMagic, Dmg(0.2).HarmfulSkill);
            Assert.IsNull(Other(0.2).HarmfulSkill);
        }

        [TestMethod]
        public void A_Hybrid_records_its_largest_mode_as_its_primary_archetype()
        {
            // magic 0.3, melee 0.7: Hybrid, primary melee (the split against no standard is a tank at zero).
            var meleeLead = Run(Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.3) })));
            Assert.AreEqual(CreatureArchetype.Hybrid, meleeLead.Archetype);
            Assert.AreEqual(CreatureArchetype.MeleeTank, meleeLead.PrimaryArchetype);
            Assert.AreEqual(0.0, meleeLead.Toughness, "Toughness/Lethality stay 0 off the melee branch");

            // magic 0.3, missile 0.35, melee 0.35: missile >= melee and > magic -> Ranged.
            var all3 = Run(Profile(100, new[] { (Skill.Sword, 400u), (Skill.Bow, 400u), (Skill.WarMagic, 400u) }, Facts(treasure: 0.5, rolls: new[] { Dmg(0.3) })));
            Assert.AreEqual(CreatureArchetype.Hybrid, all3.Archetype);
            Assert.AreEqual(CreatureArchetype.Ranged, all3.PrimaryArchetype);

            Assert.AreEqual(CreatureArchetype.Caster, Run(Profile(100, SwordWar, Facts(rolls: new[] { Dmg(0.6) }))).PrimaryArchetype, "a pure archetype is its own primary");
            Assert.AreEqual(CreatureArchetype.Unclassified, Run(Profile(100, new (Skill, uint)[0])).PrimaryArchetype);
        }

        [TestMethod]
        public void EffectiveMaxDamage_is_the_best_of_body_parts_wielded_weapons_and_ammunition_times_the_launcher_mod()
        {
            var body = new Weenie
            {
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    [CombatBodyPart.Head] = new PropertiesBodyPart { DVal = 30 },
                    [CombatBodyPart.Chest] = new PropertiesBodyPart { DVal = 12 },
                },
            };

            Weenie Item(WeenieType type, int damage, double? mod = null)
            {
                var w = new Weenie { WeenieType = type, PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Damage] = damage } };
                if (mod.HasValue)
                    w.PropertiesFloat = new Dictionary<PropertyFloat, double> { [PropertyFloat.DamageMod] = mod.Value };
                return w;
            }

            Assert.AreEqual(30u, ArchetypeFacts.EffectiveMaxDamageOf(body, null));
            Assert.AreEqual(55u, ArchetypeFacts.EffectiveMaxDamageOf(body, new[] { Item(WeenieType.MeleeWeapon, 55) }));
            Assert.AreEqual(40u, ArchetypeFacts.EffectiveMaxDamageOf(body, new[] { Item(WeenieType.Missile, 40) }), "a thrown weapon");
            Assert.AreEqual(50u, ArchetypeFacts.EffectiveMaxDamageOf(body, new[] { Item(WeenieType.Ammunition, 20), Item(WeenieType.MissileLauncher, 0, 2.5) }), "ammo 20 x DamageMod 2.5");
            Assert.AreEqual(30u, ArchetypeFacts.EffectiveMaxDamageOf(body, new[] { Item(WeenieType.Ammunition, 20) }), "no launcher: mod 1, the body part wins");
            Assert.AreEqual(30u, ArchetypeFacts.EffectiveMaxDamageOf(body, new[] { Item(WeenieType.MissileLauncher, 99, 3.0) }), "a launcher's own Damage is not an attack");
            Assert.AreEqual(0u, ArchetypeFacts.EffectiveMaxDamageOf(null, null));
        }

        [TestMethod]
        public void ProjectileMean_counts_single_war_and_void_projectiles_only()
        {
            Assert.AreEqual(150.0, ArchetypeFactsResolver.ProjectileMean(MagicSchool.WarMagic, SpellType.Projectile, 1, 100, 200), 1e-12);
            Assert.AreEqual(150.0, ArchetypeFactsResolver.ProjectileMean(MagicSchool.VoidMagic, SpellType.Projectile, 1, 100, 200), 1e-12);
            Assert.AreEqual(0.0, ArchetypeFactsResolver.ProjectileMean(MagicSchool.WarMagic, SpellType.Projectile, 3, 100, 200), "a multi-projectile spell");
            Assert.AreEqual(0.0, ArchetypeFactsResolver.ProjectileMean(MagicSchool.LifeMagic, SpellType.LifeProjectile, 1, 100, 200));
            Assert.AreEqual(0.0, ArchetypeFactsResolver.ProjectileMean(MagicSchool.VoidMagic, SpellType.EnchantmentProjectile, 1, 100, 200));
            Assert.AreEqual(0.0, ArchetypeFactsResolver.ProjectileMean(MagicSchool.CreatureEnchantment, SpellType.Enchantment, 1, 100, 200));
        }

        [TestMethod]
        public void FromWeenie_with_a_resolver_reads_harmful_skills_the_projectile_mean_and_the_four_ratings()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 5002,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    [PropertyInt.Level] = 150, [PropertyInt.DamageRating] = 15, [PropertyInt.DamageResistRating] = 10,
                    [PropertyInt.CritRating] = 5, [PropertyInt.CritDamageRating] = 7,
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart> { [CombatBodyPart.Head] = new PropertiesBodyPart { DVal = 25 } },
                PropertiesSpellBook = new Dictionary<int, float> { [1] = 2.5f, [2] = 2.3f },
            };

            var resolver = new ArchetypeFactsResolver(id => null,
                id => id == 2 ? Skill.WarMagic : (Skill?)null,
                id => id == 1 ? Skill.CreatureEnchantment : id == 2 ? Skill.WarMagic : (Skill?)null,
                null,
                id => id == 2 ? 180.0 : 0.0);

            var f = ArchetypeFacts.FromWeenie(weenie, resolver);

            Assert.AreEqual(Skill.CreatureEnchantment, f.SpellRolls[0].HarmfulSkill);
            Assert.IsNull(f.SpellRolls[0].DamagingSkill);
            Assert.AreEqual(Skill.WarMagic, f.SpellRolls[1].HarmfulSkill);
            Assert.AreEqual(180.0, f.BestProjectileMean, 1e-12);
            Assert.AreEqual(180.0 * 1.15, f.SpellOutput, 1e-9, "mean x (1 + DR / 100)");
            Assert.AreEqual(25u, f.EffectiveMaxDamage);
            Assert.AreEqual(15, f.DamageRating);
            Assert.AreEqual(10, f.DamageResistRating);
            Assert.AreEqual(5, f.CritRating);
            Assert.AreEqual(7, f.CritDamageRating);

            // The legacy four-delegate overload: harmful = damaging, no projectile mean.
            var legacy = ArchetypeFacts.FromWeenie(weenie, id => null, id => id == 2 ? Skill.WarMagic : (Skill?)null);
            Assert.IsNull(legacy.SpellRolls[0].HarmfulSkill);
            Assert.AreEqual(0.0, legacy.BestProjectileMean);
            Assert.AreEqual(0.0, legacy.SpellOutput);
        }

        // ---- post-strip classification (orchestrator ruling 2026-10-23) -----------------------------------------

        /// <summary>
        /// A war caster whose book is one 9-projectile ring (id 1, the AoE class), one banned drain (id 2) and nothing
        /// else. The strip removes both, so with a damaging body part it is a melee creature, not a Caster.
        /// </summary>
        private static Weenie RingCaster(int dval) => new Weenie
        {
            WeenieClassId = 5003,
            PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 200 },
            PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
            {
                [Skill.WarMagic] = new PropertiesSkill { InitLevel = 400 },
                [Skill.LifeMagic] = new PropertiesSkill { InitLevel = 400 },
                [Skill.UnarmedCombat] = new PropertiesSkill { InitLevel = 300 },
            },
            PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart> { [CombatBodyPart.Head] = new PropertiesBodyPart { DVal = dval } },
            PropertiesSpellBook = new Dictionary<int, float> { [1] = 2.5f, [2] = 2.5f },
        };

        private static ArchetypeFactsResolver StripResolver(SpellStripConfig strip)
            => new ArchetypeFactsResolver(id => null,
                id => id == 1 ? Skill.WarMagic : (Skill?)null,
                id => id == 1 ? Skill.WarMagic : id == 2 ? Skill.LifeMagic : (Skill?)null,
                null,
                id => id == 1 ? 300.0 : 0.0,
                strip,
                id => id == 1 ? 9 : 1);

        [TestMethod]
        public void A_caster_whose_spells_are_all_stripped_is_classified_by_what_remains()
        {
            var strip = new SpellStripConfig(true, new[] { 2 });

            var authored = ArchetypeFacts.FromWeenie(RingCaster(50), StripResolver(SpellStripConfig.None));
            var stripped = ArchetypeFacts.FromWeenie(RingCaster(50), StripResolver(strip));

            Assert.AreEqual(2, authored.SpellRolls.Count, "no strip: the authored book");
            Assert.AreEqual(0, stripped.SpellRolls.Count, "the ring (9 projectiles) and the banned drain are gone");
            Assert.AreEqual(0, stripped.TotalDamagingSpells);
            Assert.AreEqual(0.0, stripped.BestProjectileMean, "a stripped projectile is no spell output either");

            DungeonStatProfile ProfileWith(ArchetypeFacts f) => Profile(200, new[] { (Skill.WarMagic, 400u), (Skill.LifeMagic, 400u), (Skill.UnarmedCombat, 300u) }, f);

            Assert.AreEqual(CreatureArchetype.Caster, Kind(ProfileWith(authored)), "fixture: authored, it reads Caster (harmful share 0.75)");
            var after = Kind(ProfileWith(stripped));
            Assert.IsTrue(after == CreatureArchetype.MeleeTank || after == CreatureArchetype.MeleeDps, $"post-strip it is melee, got {after}");
        }

        [TestMethod]
        public void The_strip_guard_and_the_switch_carry_into_classification()
        {
            // No damaging body part: the strip's safety guard refuses to empty the book, so the creature still casts.
            var guarded = ArchetypeFacts.FromWeenie(RingCaster(0), StripResolver(new SpellStripConfig(true, new[] { 2 })));
            Assert.AreEqual(2, guarded.SpellRolls.Count);

            // Strip off: authored book.
            Assert.AreEqual(2, ArchetypeFacts.FromWeenie(RingCaster(50), StripResolver(new SpellStripConfig(false, new[] { 2 }))).SpellRolls.Count);

            // Only the banned id listed and no projectile lookup: the ring survives, the drain does not.
            var bannedOnly = ArchetypeFacts.FromWeenie(RingCaster(50),
                new ArchetypeFactsResolver(id => null, id => id == 1 ? Skill.WarMagic : (Skill?)null, null, null, null, new SpellStripConfig(true, new[] { 2 })));
            Assert.AreEqual(1, bannedOnly.SpellRolls.Count);
            Assert.AreEqual(1, bannedOnly.TotalDamagingSpells);
        }

        [TestMethod]
        public void SpellStripConfig_is_pure_and_keyed_on_both_dials()
        {
            var book = new Dictionary<int, float> { [1] = 2.5f, [2] = 2.5f, [3] = 2.5f };
            var config = new SpellStripConfig(true, new[] { 3, 2 });

            var removed = config.WouldRemove(book.Keys, id => id == 1 ? 5 : 1, true);

            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, removed.ToArray());
            Assert.AreEqual(3, book.Count, "the book itself is never touched");

            Assert.AreEqual(0, new SpellStripConfig(false, new[] { 2 }).WouldRemove(book.Keys, id => 9, true).Count);
            Assert.AreEqual(0, config.WouldRemove(book.Keys, id => 9, false).Count, "the guard: emptied book, no damaging body part");

            Assert.AreEqual(new SpellStripConfig(true, new[] { 2, 3 }).Key, config.Key, "order of the ids does not matter");
            Assert.AreNotEqual(new SpellStripConfig(false, new[] { 2, 3 }).Key, config.Key);
            Assert.AreNotEqual(new SpellStripConfig(true, new[] { 2 }).Key, config.Key);

            Assert.IsTrue(SpellStripConfig.Default.Enabled);
            CollectionAssert.AreEquivalent(DungeonSpellFilter.ParseBannedIds(DungeonSpellFilter.DefaultBannedSpellIds).ToArray(), SpellStripConfig.Default.BannedIds.ToArray());
        }

        // ---- EntryFor tagging rule --------------------------------------------------------------------------

        [TestMethod]
        public void TagEntry_uplifts_below_the_natural_band_stamps_above_the_authored_level_and_otherwise_tags_nothing()
        {
            var below = DungeonPopulationBuilder.TagEntry(7, DungeonRole.Trash, null, projectedLevel: 90, rawLevelOf: () => throw new InvalidOperationException("raw level must not be read on the uplift branch"),
                naturalBandLow: 100, gemLevel: 200);
            Assert.AreEqual(200, below.UpliftLevel);
            Assert.AreEqual(0, below.StampLevel);

            var stamped = DungeonPopulationBuilder.TagEntry(7, DungeonRole.Trash, null, 420, () => 375, 100, 400);
            Assert.AreEqual(0, stamped.UpliftLevel);
            Assert.AreEqual(420, stamped.StampLevel);

            var plain = DungeonPopulationBuilder.TagEntry(7, DungeonRole.Trash, null, 150, () => 150, 100, 200);
            Assert.AreEqual(0, plain.UpliftLevel);
            Assert.AreEqual(0, plain.StampLevel);

            var edge = DungeonPopulationBuilder.TagEntry(7, DungeonRole.Trash, null, 100, () => 100, 100, 200);
            Assert.AreEqual(0, edge.UpliftLevel, "projected == natural low is NOT below it");
        }
    }
}
