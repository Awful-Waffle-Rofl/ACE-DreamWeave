using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads melee defense ceiling must also bound the wielded weapon's melee defense modifier. Before this fix the
    /// ceiling clamped only the pre-enchantment skill inside CreatureSkill.Current, and Creature.GetEffectiveDefenseSkill
    /// then multiplied by GetWeaponMeleeDefenseModifier, so a Thread creature wielding a Quiddity weapon (WeaponDefense
    /// 1.06-1.08 plus Aura of Defender Self IV, +0.10) rolled evade well above its ceiling. The rule:
    /// effective = min(Current * mod, max(Current, ceiling)) for a creature that carries a melee defense ceiling, and
    /// unchanged for every creature that does not.
    /// </summary>
    [TestClass]
    public class WeaponDefenseCeilingTests
    {
        private static uint nextGuid = 0x7D3A0000;
        private static uint nextWcid = 994700;

        // CreateDefender's melee defense formula: (Coordination 20 + Quickness 40) / 3 = 20, so InitLevel 300 reads 320.
        private const uint Init = 300;
        private const uint Uncapped = 320;
        private const uint Ceiling = 300;
        private const double QuiddityMod = 1.18;   // Mace of the Quiddity: WeaponDefense 1.08 + Aura of Defender Self IV 0.10

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static WorldObject MakeWeapon(double defense)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Ceiling Test Mace" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.WeaponType, (int)WeaponType.Mace },
                    { PropertyInt.WeaponSkill, (int)Skill.HeavyWeapons },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.WeaponDefense, defense } },
            };

            return new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        private static Creature Armed(double weaponDefense, uint? ceiling, float meleeDefenseEnchantment = 0)
        {
            var creature = TestCreatures.CreateDefender(meleeDefense: Init);
            Assert.IsTrue(creature.TryEquipObject(MakeWeapon(weaponDefense), EquipMask.MeleeWeapon), "could not wield the test weapon");

            if (ceiling.HasValue)
                creature.DefenseSkillCeilings = new Dictionary<Skill, uint> { [Skill.MeleeDefense] = ceiling.Value };

            if (meleeDefenseEnchantment != 0)
            {
                creature.Biota.PropertiesEnchantmentRegistry.AddEnchantment(new PropertiesEnchantmentRegistry
                {
                    SpellId = 1,
                    SpellCategory = meleeDefenseEnchantment < 0 ? SpellCategory.MeleeDefenseLowering : SpellCategory.MeleeDefenseRaising,
                    LayerId = 1,
                    PowerLevel = 100,
                    StartTime = 0,
                    Duration = -1,
                    StatModType = EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.SingleStat,
                    StatModKey = (uint)Skill.MeleeDefense,
                    StatModValue = meleeDefenseEnchantment,
                    EnchantmentCategory = (uint)SpellType.Enchantment,
                }, creature.BiotaDatabaseLock);
            }

            return creature;
        }

        private static uint Melee(Creature c) => c.GetEffectiveDefenseSkill(CombatType.Melee, CombatMode.Melee);

        private static uint Product(uint current, double mod) => (uint)Math.Round(current * (float)mod);

        // ---- the pure bound ------------------------------------------------------------------------------

        [TestMethod]
        public void The_bound_only_lowers_and_a_null_ceiling_is_a_no_op()
        {
            Assert.AreEqual(300u, DungeonCombatNormalizer.BoundWeaponLiftedDefense(354, 300, 300), "a weapon lift above the ceiling is cut to it");
            Assert.AreEqual(300u, DungeonCombatNormalizer.BoundWeaponLiftedDefense(330, 280, 300), "a lightly debuffed creature is cut to the ceiling, not to Current");
            Assert.AreEqual(236u, DungeonCombatNormalizer.BoundWeaponLiftedDefense(236, 200, 300), "below the ceiling stays put");
            Assert.AreEqual(240u, DungeonCombatNormalizer.BoundWeaponLiftedDefense(240, 300, 300), "a modifier below 1 still applies");
            Assert.AreEqual(380u, DungeonCombatNormalizer.BoundWeaponLiftedDefense(448, 380, 300), "Current above the ceiling (a self buff) bounds at Current");
            Assert.AreEqual(354u, DungeonCombatNormalizer.BoundWeaponLiftedDefense(354, 300, null), "no ceiling is a no-op");
        }

        // ---- (a) the reported case: a ceiling creature with a weapon modifier above 1 ---------------------

        [TestMethod]
        public void A_ceiling_creature_wielding_a_high_defense_weapon_is_held_at_the_ceiling()
        {
            var creature = Armed(QuiddityMod, Ceiling);

            Assert.AreEqual(Ceiling, creature.GetCreatureSkill(Skill.MeleeDefense).Current, "positive control: the skill itself is clamped");
            Assert.AreEqual(QuiddityMod, WorldObject.GetWeaponMeleeDefenseModifier(creature, CombatMode.Melee), 1e-6, "positive control: the weapon modifier is live");
            Assert.IsTrue(Product(Ceiling, QuiddityMod) > Ceiling, "positive control: unbounded, the weapon would lift it above the ceiling");

            Assert.AreEqual(Ceiling, Melee(creature));
        }

        // ---- (b) debuffs still land ----------------------------------------------------------------------

        [TestMethod]
        public void A_debuffed_ceiling_creature_still_drops_below_the_ceiling()
        {
            var creature = Armed(QuiddityMod, Ceiling, meleeDefenseEnchantment: -100);

            Assert.AreEqual(200u, creature.GetCreatureSkill(Skill.MeleeDefense).Current, "the debuff lands on top of the clamp");
            Assert.AreEqual(Product(200, QuiddityMod), Melee(creature), "Current * mod, unchanged from before the fix");
            Assert.IsTrue(Melee(creature) < Ceiling);
        }

        [TestMethod]
        public void A_weapon_modifier_below_one_still_applies_to_a_ceiling_creature()
        {
            var creature = Armed(0.8, Ceiling);

            Assert.AreEqual(Product(Ceiling, 0.8), Melee(creature));
        }

        // ---- (c) a creature without a ceiling is unchanged -----------------------------------------------

        [TestMethod]
        public void A_creature_without_a_ceiling_keeps_the_full_weapon_lift()
        {
            var creature = Armed(QuiddityMod, ceiling: null);

            Assert.IsNull(creature.GetDefenseSkillCeiling(Skill.MeleeDefense), "positive control: no ceiling");
            Assert.AreEqual(Uncapped, creature.GetCreatureSkill(Skill.MeleeDefense).Current);
            Assert.AreEqual(Product(Uncapped, QuiddityMod), Melee(creature));
        }

        [TestMethod]
        public void A_ceiling_on_another_skill_does_not_bound_melee_defense()
        {
            var creature = Armed(QuiddityMod, ceiling: null);
            creature.DefenseSkillCeilings = new Dictionary<Skill, uint> { [Skill.MissileDefense] = Ceiling };

            Assert.AreEqual(Product(Uncapped, QuiddityMod), Melee(creature));
        }

        // ---- the band cap: every non-boss Thread creature, ceiling or not -------------------------------

        private const string Spawner = "Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs";
        private const string PrepareSignature = "internal static void PrepareForPlacement(ThreadDungeonRun run, Creature creature, DungeonSpawnPlanEntry entry, DungeonSpawnPlan plan,";

        // Band effective melee defense median 250 + offset 50 = cap 300 (the shipped default offset is 100; these plans set 50).
        private const uint BandMedian = 250;

        private static DungeonBandStandard Standard(uint meleeMedian)
            => DungeonBandStandard.ForTest(null, 0, 0, effectiveDefenseMedians: new Dictionary<Skill, uint> { [Skill.MeleeDefense] = meleeMedian });

        private static DungeonSpawnPlan Plan(bool strip = true, double offset = 50, uint median = BandMedian)
            => new DungeonSpawnPlan { StripCombatTraits = strip, DefenseSkillCapOffset = offset, BandStandard = Standard(median) };

        private static DungeonSpawnPlanEntry Entry(DungeonRole role = DungeonRole.Trash) => new DungeonSpawnPlanEntry(1, role, null);

        [TestMethod]
        public void An_under_cap_Thread_creature_carries_no_ceiling_but_is_held_at_the_band_cap()
        {
            // InitLevel 260 + 20 = Current 280, under the cap 300, so neither the strip nor an uplift writes a ceiling for it.
            var creature = TestCreatures.CreateDefender(meleeDefense: 260);
            Assert.IsTrue(creature.TryEquipObject(MakeWeapon(QuiddityMod), EquipMask.MeleeWeapon), "could not wield the test weapon");

            ThreadDungeonSpawner.ApplyMeleeDefenseBandCap(creature, Entry(), Plan());

            Assert.AreEqual(Ceiling, creature.MeleeDefenseBandCap);
            Assert.IsNull(creature.GetDefenseSkillCeiling(Skill.MeleeDefense), "positive control: no ceiling entry is written");
            Assert.AreEqual(280u, creature.GetCreatureSkill(Skill.MeleeDefense).Current, "the band cap does not clamp the skill");
            Assert.IsTrue(Product(280, QuiddityMod) > Ceiling, "positive control: unbounded, the weapon would lift it above the cap");

            Assert.AreEqual(Ceiling, Melee(creature));
        }

        [TestMethod]
        public void An_uplift_that_tops_a_skill_up_to_exactly_the_cap_writes_no_ceiling()
        {
            // Why the band cap is needed: both uplift ceiling rules fire only on a value STRICTLY above the cap, and the uplift
            // tops a defense skill up to at most the cap, so an uplifted reach-down creature (the reported Hea Nualuan, level
            // 50/80) lands at or under it with no ceiling.
            Assert.IsNull(ArchetypeUplift.CeilingFor(Ceiling, rawAfter: Ceiling, existing: null, raised: true), "archetype uplift");
            Assert.AreEqual(0, BandUplift.CeilingsToSet(Standard(BandMedian), 50, true, _ => Ceiling, _ => null).Count, "band uplift");
            Assert.AreEqual(Ceiling, ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(), Plan(), null), "but the band cap is recorded");
        }

        [TestMethod]
        public void The_ceiling_wins_over_the_band_cap_when_both_are_set()
        {
            var creature = Armed(QuiddityMod, Ceiling);
            creature.MeleeDefenseBandCap = 400;

            Assert.AreEqual(Ceiling, creature.GetMeleeDefenseWeaponBound());
            Assert.AreEqual(Ceiling, Melee(creature));
        }

        [TestMethod]
        public void The_band_cap_is_recorded_only_for_a_non_boss_creature_in_a_capped_run()
        {
            Assert.AreEqual(300u, ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(), Plan(), null), "positive control: natural trash");
            Assert.AreEqual(300u, ThreadDungeonSpawner.MeleeDefenseBandCapFor(new DungeonSpawnPlanEntry(1, DungeonRole.Trash, null, upliftLevel: 400), Plan(), null),
                "a BandUplift entry reads the same standard");
            Assert.IsNull(ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(DungeonRole.Boss), Plan(), null), "a boss is exempt from the defense cap");
            Assert.IsNull(ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(), Plan(strip: false), null), "the strip off means no defense cap in the run");
            Assert.IsNull(ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(), Plan(offset: -1), null), "a negative offset disables the axis");
            Assert.IsNull(ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(), Plan(median: 0), null), "no median, no cap");
            Assert.IsNull(ThreadDungeonSpawner.MeleeDefenseBandCapFor(null, Plan(), null));
            Assert.IsNull(ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(), null, null));

            // An archetype-uplifted entry reads the archetype's run-level melee defense standard, the cap ArchetypeUplift clamps to.
            var archetype = new ArchetypeUpliftInfo
            {
                Run = new ArchetypeAxes(Standard(400), 0, 0, 0, 0, 0, 0, 0, 8, 1.0),
                AllRun = Standard(BandMedian),
            };
            Assert.AreEqual(450u, ThreadDungeonSpawner.MeleeDefenseBandCapFor(Entry(), Plan(), archetype));
        }

        [TestMethod]
        public void A_creature_nobody_prepared_has_no_band_cap()
        {
            var creature = Armed(QuiddityMod, ceiling: null);
            Assert.IsNull(creature.MeleeDefenseBandCap, "World Events, Pinnacles and the open world never get one");
            Assert.IsNull(creature.GetMeleeDefenseWeaponBound());

            ThreadDungeonSpawner.ApplyMeleeDefenseBandCap(creature, Entry(DungeonRole.Boss), Plan());
            Assert.IsNull(creature.MeleeDefenseBandCap);
            Assert.AreEqual(Product(Uncapped, QuiddityMod), Melee(creature));
        }

        [TestMethod]
        public void Every_prepared_creature_gets_the_band_cap_hook()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(Spawner), PrepareSignature);
            var hook = new Regex(@"^[ ]+ApplyMeleeDefenseBandCap\(creature, entry, plan\);\r?$", RegexOptions.Multiline);
            Assert.AreEqual(1, hook.Matches(body).Count, "PrepareForPlacement records the band cap, live");

            var missile = body.IndexOf("ApplyMissileDefenseCeiling(creature, plan);", StringComparison.Ordinal);
            Assert.IsTrue(missile >= 0, "positive control: the missile ceiling hook is in the body");
            Assert.IsTrue(hook.Match(body).Index > missile, "after every uplift step, next to the other last-word ceilings");
        }

        // ---- the player-attack proficiency path uses the same bound --------------------------------------

        [TestMethod]
        public void The_player_proficiency_difficulty_applies_the_same_bound()
        {
            // A Player cannot be constructed cleanly in this harness, so Player.GetTargetEffectiveDefenseSkill is pinned as source text.
            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Combat.cs");
            var start = source.IndexOf("public uint GetTargetEffectiveDefenseSkill(WorldObject target)", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "positive control: the method is in the file");

            var end = source.IndexOf("return effectiveDefense;", start, StringComparison.Ordinal);
            var body = source.Substring(start, end - start);

            // Exact if-head and assignment on consecutive lines, so a commented-out or detached call does not satisfy the pin.
            var pin = new Regex(@"^[ ]+if \(defenseSkill == Skill\.MeleeDefense && creature is not Player\)\r?\n"
                + @"[ ]+effectiveDefense = ACE\.Server\.ThreadDungeons\.DungeonCombatNormalizer\.BoundWeaponLiftedDefense\(effectiveDefense, current, creature\.GetMeleeDefenseWeaponBound\(\)\);\r?$",
                RegexOptions.Multiline);
            Assert.AreEqual(1, pin.Matches(body).Count, "the bound is applied, live, inside GetTargetEffectiveDefenseSkill");
        }
    }
}
