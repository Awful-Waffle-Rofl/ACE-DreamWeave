using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class DungeonCombatNormalizerTests
    {
        private static DungeonBandStandard Standard(uint dmg = 220, uint armor = 300, params (Skill, uint)[] skills)
            => DungeonBandStandard.ForTest(skills.ToDictionary(s => s.Item1, s => s.Item2), dmg, armor);

        private static DungeonBandStandard StandardWithEffective(params (Skill, uint)[] effectiveMedians)
            => DungeonBandStandard.ForTest(new Dictionary<Skill, uint>(), 0, 0,
                effectiveDefenseMedians: effectiveMedians.ToDictionary(s => s.Item1, s => s.Item2));

        // ---- skill targets ------------------------------------------------------------------------------

        [TestMethod]
        public void Attack_skill_is_set_to_its_band_median_times_the_ratio()
        {
            var std = Standard(skills: new[] { (Skill.HeavyWeapons, 400u), (Skill.WarMagic, 350u) });

            Assert.AreEqual(400u, DungeonCombatNormalizer.AttackSkillTarget(std, Skill.HeavyWeapons, 1.0));
            Assert.AreEqual(500u, DungeonCombatNormalizer.AttackSkillTarget(std, Skill.HeavyWeapons, 1.25));
        }

        [TestMethod]
        public void Attack_skill_with_no_band_median_uses_the_highest_attack_median()
        {
            var std = Standard(skills: new[] { (Skill.HeavyWeapons, 400u), (Skill.WarMagic, 350u), (Skill.MeleeDefense, 900u) });

            Assert.AreEqual(400u, DungeonCombatNormalizer.AttackSkillTarget(std, Skill.VoidMagic, 1.0),
                "void has no median; the highest ATTACK median is 400 - the 900 defense median must not be borrowed");
        }

        [TestMethod]
        public void Attack_and_defense_targets_are_zero_for_an_empty_standard_or_a_zero_ratio()
        {
            var std = Standard(skills: new[] { (Skill.HeavyWeapons, 400u), (Skill.MeleeDefense, 300u) });

            Assert.AreEqual(0u, DungeonCombatNormalizer.AttackSkillTarget(DungeonBandStandard.Empty, Skill.HeavyWeapons, 1.0));
            Assert.AreEqual(0u, DungeonCombatNormalizer.AttackSkillTarget(std, Skill.HeavyWeapons, 0.0), "0 = leave the axis authored");
            Assert.AreEqual(0u, DungeonCombatNormalizer.AttackSkillTarget(std, Skill.HeavyWeapons, double.NaN));
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillTarget(std, Skill.MeleeDefense, 0.0));
        }

        [TestMethod]
        public void Defense_skill_has_no_cross_skill_fallback()
        {
            var std = Standard(skills: new[] { (Skill.MeleeDefense, 300u) });

            Assert.AreEqual(300u, DungeonCombatNormalizer.DefenseSkillTarget(std, Skill.MeleeDefense, 1.0));
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillTarget(std, Skill.MagicDefense, 1.0));
        }

        [TestMethod]
        public void Defense_cap_is_the_effective_median_plus_the_offset()
        {
            var std = StandardWithEffective((Skill.MissileDefense, 250u));

            Assert.AreEqual(350u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MissileDefense, 100.0));
            Assert.AreEqual(250u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MissileDefense, 0.0), "0 is a valid offset - cap exactly at the median");
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MissileDefense, -1.0), "a negative offset disables the axis");
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillCap(std, Skill.MagicDefense, 100.0), "no effective median, no cap");
            Assert.AreEqual(0u, DungeonCombatNormalizer.DefenseSkillCap(DungeonBandStandard.Empty, Skill.MissileDefense, 100.0));
        }

        [TestMethod]
        public void Ceiling_clamp_only_lowers_a_value_above_the_ceiling()
        {
            Assert.AreEqual(300u, DungeonCombatNormalizer.ClampToDefenseCeiling(400, 300));
            Assert.AreEqual(300u, DungeonCombatNormalizer.ClampToDefenseCeiling(300, 300), "at the ceiling stays put");
            Assert.AreEqual(200u, DungeonCombatNormalizer.ClampToDefenseCeiling(200, 300), "below the ceiling stays put");
            Assert.AreEqual(400u, DungeonCombatNormalizer.ClampToDefenseCeiling(400, null), "no ceiling is a no-op");
        }

        // ---- two-way body parts -------------------------------------------------------------------------

        private static Biota BodyBiota(params (int dval, int armor)[] parts)
        {
            var biota = new Biota { PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>() };

            for (var i = 0; i < parts.Length; i++)
                biota.PropertiesBodyPart[(CombatBodyPart)i] = new PropertiesBodyPart { DVal = parts[i].dval, BaseArmor = parts[i].armor };

            return biota;
        }

        [TestMethod]
        public void An_over_tuned_boss_comes_down_to_the_standard_keeping_its_part_shape()
        {
            // 72180's case in shape: a best DVal of 1200 against a band standard near 220.
            var biota = BodyBiota((1200, 600), (600, 300));

            var changed = DungeonCombatNormalizer.SetBossBodyParts(biota, 220, 300, 1.0, 1.0);

            Assert.AreEqual(2, changed);
            Assert.AreEqual(220, biota.PropertiesBodyPart[(CombatBodyPart)0].DVal);
            Assert.AreEqual(110, biota.PropertiesBodyPart[(CombatBodyPart)1].DVal, "half the max stays half the max");
            Assert.AreEqual(300, biota.PropertiesBodyPart[(CombatBodyPart)0].BaseArmor);
            Assert.AreEqual(150, biota.PropertiesBodyPart[(CombatBodyPart)1].BaseArmor);
        }

        [TestMethod]
        public void An_under_tuned_boss_comes_up_and_the_ratios_scale_the_target()
        {
            var biota = BodyBiota((100, 100));

            DungeonCombatNormalizer.SetBossBodyParts(biota, 220, 300, 1.5, 2.0);

            Assert.AreEqual(330, biota.PropertiesBodyPart[(CombatBodyPart)0].DVal);
            Assert.AreEqual(600, biota.PropertiesBodyPart[(CombatBodyPart)0].BaseArmor);
        }

        [TestMethod]
        public void Body_parts_are_cloned_never_written_through_to_the_shared_weenie_collection()
        {
            var shared = BodyBiota((1200, 600));
            var sharedPart = shared.PropertiesBodyPart[(CombatBodyPart)0];
            var instance = new Biota { PropertiesBodyPart = shared.PropertiesBodyPart };

            DungeonCombatNormalizer.SetBossBodyParts(instance, 220, 300, 1.0, 1.0);

            Assert.AreNotSame(shared.PropertiesBodyPart, instance.PropertiesBodyPart, "the dictionary must be replaced");
            Assert.AreEqual(1200, sharedPart.DVal, "the cached weenie's part must be untouched");
            Assert.AreEqual(600, sharedPart.BaseArmor);
        }

        [TestMethod]
        public void A_tiny_authored_max_sets_every_damaging_part_to_the_target()
        {
            // 49641's case in shape: a best DVal of 2 against a standard in the hundreds.
            var biota = BodyBiota((2, 50), (1, 50), (0, 50));

            DungeonCombatNormalizer.SetBossBodyParts(biota, 220, 0, 1.0, 1.0);

            Assert.AreEqual(220, biota.PropertiesBodyPart[(CombatBodyPart)0].DVal);
            Assert.AreEqual(220, biota.PropertiesBodyPart[(CombatBodyPart)1].DVal, "shape carries no information past the threshold");
            Assert.AreEqual(0, biota.PropertiesBodyPart[(CombatBodyPart)2].DVal, "a non-damaging part stays non-damaging");
            Assert.AreEqual(50, biota.PropertiesBodyPart[(CombatBodyPart)0].BaseArmor, "armor axis had no standard (0), so it is left authored");
        }

        [TestMethod]
        public void No_melee_stays_no_melee_but_no_armor_is_raised()
        {
            var biota = BodyBiota((0, 0), (0, 0));

            DungeonCombatNormalizer.SetBossBodyParts(biota, 220, 300, 1.0, 1.0);

            Assert.IsTrue(biota.PropertiesBodyPart.Values.All(p => p.DVal == 0), "a weapon/spell fighter is not handed a bite");
            Assert.IsTrue(biota.PropertiesBodyPart.Values.All(p => p.BaseArmor == 300), "a soft boss is not softer than its band");
        }

        [TestMethod]
        public void Nothing_changes_and_nothing_is_cloned_when_the_boss_already_sits_on_the_standard()
        {
            var biota = BodyBiota((220, 300));
            var before = biota.PropertiesBodyPart;

            Assert.AreEqual(0, DungeonCombatNormalizer.SetBossBodyParts(biota, 220, 300, 1.0, 1.0));
            Assert.AreSame(before, biota.PropertiesBodyPart);
            Assert.AreEqual(0, DungeonCombatNormalizer.SetBossBodyParts(biota, 220, 300, 0.0, 0.0), "both axes disabled");
        }

        [TestMethod]
        public void A_small_part_never_rounds_to_zero()
        {
            Assert.AreEqual(1, DungeonCombatNormalizer.TwoWayPartValue(1, 1200, 220, zeroOwnMaxStaysZero: true));
        }

        // ---- category-immunity guard --------------------------------------------------------------------

        private static System.Func<PropertyFloat, double?> Floats(params (PropertyFloat, double)[] values)
        {
            var d = values.ToDictionary(v => v.Item1, v => v.Item2);
            return p => d.TryGetValue(p, out var v) ? v : (double?)null;
        }

        [TestMethod]
        public void Absent_resists_read_as_full_damage_and_write_nothing()
        {
            Assert.AreEqual(0, DungeonCombatNormalizer.CategoryResistWrites(Floats(), 0.5).Count);
        }

        [TestMethod]
        public void A_physically_immune_creature_has_all_three_physical_resists_raised_to_the_floor()
        {
            var writes = DungeonCombatNormalizer.CategoryResistWrites(Floats(
                (PropertyFloat.ResistSlash, 0.0), (PropertyFloat.ResistPierce, 0.1), (PropertyFloat.ResistBludgeon, 0.2)), 0.5);

            CollectionAssert.AreEquivalent(new[] { PropertyFloat.ResistSlash, PropertyFloat.ResistPierce, PropertyFloat.ResistBludgeon },
                writes.Select(w => w.Property).ToArray());
            Assert.IsTrue(writes.All(w => w.Value == 0.5));
        }

        [TestMethod]
        public void One_physical_type_that_still_hurts_keeps_the_whole_group_authored()
        {
            var writes = DungeonCombatNormalizer.CategoryResistWrites(Floats(
                (PropertyFloat.ResistSlash, 0.0), (PropertyFloat.ResistPierce, 0.0), (PropertyFloat.ResistBludgeon, 0.6)), 0.5);

            Assert.AreEqual(0, writes.Count, "bludgeon hurts; slash and pierce immunity is the element-style variety the owner kept");
        }

        [TestMethod]
        public void A_magic_immune_creature_has_its_elemental_and_nether_resists_raised_and_single_immunity_survives()
        {
            var immune = DungeonCombatNormalizer.CategoryResistWrites(Floats(
                (PropertyFloat.ResistFire, 0.0), (PropertyFloat.ResistCold, 0.0), (PropertyFloat.ResistAcid, 0.0),
                (PropertyFloat.ResistElectric, 0.0), (PropertyFloat.ResistNether, 0.3)), 0.5);

            Assert.AreEqual(5, immune.Count);

            var fireImmuneOnly = DungeonCombatNormalizer.CategoryResistWrites(Floats((PropertyFloat.ResistFire, 0.0)), 0.5);

            Assert.AreEqual(0, fireImmuneOnly.Count, "absent cold/acid/electric/nether read as 1.0, so the group is not immune");
        }

        [TestMethod]
        public void A_zero_or_garbled_floor_disables_the_guard()
        {
            var all = Floats((PropertyFloat.ResistSlash, 0.0), (PropertyFloat.ResistPierce, 0.0), (PropertyFloat.ResistBludgeon, 0.0));

            Assert.AreEqual(0, DungeonCombatNormalizer.CategoryResistWrites(all, 0.0).Count);
            Assert.AreEqual(0, DungeonCombatNormalizer.CategoryResistWrites(all, double.NaN).Count);
        }

        [TestMethod]
        public void Physical_armor_mods_above_the_ceiling_scale_by_one_factor()
        {
            var writes = DungeonCombatNormalizer.ArmorModWrites(Floats(
                (PropertyFloat.ArmorModVsSlash, 4.0), (PropertyFloat.ArmorModVsPierce, 5.0), (PropertyFloat.ArmorModVsBludgeon, 8.0)), 2.0)
                .ToDictionary(w => w.Property, w => w.Value);

            Assert.AreEqual(2.0, writes[PropertyFloat.ArmorModVsSlash], 1e-9, "the smallest lands exactly on the ceiling");
            Assert.AreEqual(2.5, writes[PropertyFloat.ArmorModVsPierce], 1e-9);
            Assert.AreEqual(4.0, writes[PropertyFloat.ArmorModVsBludgeon], 1e-9, "relative weakness survives");
        }

        [TestMethod]
        public void Armor_mods_with_any_member_at_or_under_the_ceiling_are_untouched()
        {
            Assert.AreEqual(0, DungeonCombatNormalizer.ArmorModWrites(Floats(
                (PropertyFloat.ArmorModVsSlash, 9.0), (PropertyFloat.ArmorModVsPierce, 9.0)), 2.0).Count,
                "absent bludgeon reads as 1.0, so the creature is not armoured against every physical type");
            Assert.AreEqual(0, DungeonCombatNormalizer.ArmorModWrites(Floats(
                (PropertyFloat.ArmorModVsSlash, 9.0), (PropertyFloat.ArmorModVsPierce, 9.0), (PropertyFloat.ArmorModVsBludgeon, 9.0)), 0.0).Count, "0 disables");
        }

        [TestMethod]
        public void The_strip_lists_carry_the_verified_ids()
        {
            // Pinned against the enum values verified 2026-09-13, so a renumbering or a dropped entry is loud.
            CollectionAssert.AreEquivalent(new[] { 65, 66, 70, 98, 103 }, DungeonCombatNormalizer.BypassBools.Select(b => (int)b).ToArray());
            // 9012 HollowIntensity added 2026-09-17 with the hollow-intensity change
            CollectionAssert.AreEquivalent(new[] { 151, 155, 159, 9012 }, DungeonCombatNormalizer.BypassFloats.Select(f => (int)f).ToArray());
            CollectionAssert.AreEquivalent(new[] { 386 }, DungeonCombatNormalizer.BypassInts.Select(i => (int)i).ToArray());
        }
    }
}
