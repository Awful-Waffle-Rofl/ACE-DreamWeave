using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the nine Spellsword (8th class) entries to the design table in
    /// SPELLSWORD-DESIGN.md section 3 (2026-08-03): which entries exist, their tier, max rank and
    /// per-rank cost, plus the two generated families (the SpellswordTraining bundle and the two
    /// EnhancedStatAbility skills homed to this class) and the tier-count structural rule from
    /// DESIGN.md section 3 (4-5 / 3-4 / 2-3 entries per tier). Mirrors
    /// BloodMageAbilityDefinitionTests.cs's structure for the 7th class.
    /// </summary>
    [TestClass]
    public class SpellswordAbilityDefinitionTests
    {
        private static ClassAbilityDefinition ByName(string name)
        {
            Assert.IsTrue(ClassAbilityRegistry.TryGetByName(name, out var def), $"{name} is not registered");
            return def;
        }

        /// <summary>The design table for the eight bespoke handlers plus the generated bundle: name, tier, max rank, per-rank cost.</summary>
        private static readonly (string Name, int Tier, int MaxRank, int[] Cost)[] DesignTable =
        {
            // Tier 1 - open
            ("spellblade",          1, 3, new[] { 1, 2, 3 }),
            ("resonance",           1, 3, new[] { 3, 3, 3 }),
            ("spellsword_training", 1, 3, new[] { 1, 1, 1 }),
            // Tier 2 - unlock: 3 CAPs, 5 spent in class
            ("runeblade",           2, 3, new[] { 1, 2, 3 }),
            ("sundermark",          2, 3, new[] { 3, 3, 3 }),
            ("spellsurge",          2, 1, new[] { 5 }),
            // Tier 3 - unlock: 8 CAPs, 15 spent in class
            ("spellstorm",          3, 1, new[] { 5 }),
            ("cascade",             3, 1, new[] { 5 }),
            ("dispellingedge",      3, 3, new[] { 3, 3, 3 }),
        };

        // ---- registration ------------------------------------------------------------------------

        [TestMethod]
        public void EveryDesignTableEntry_HasItsTierRankAndCost()
        {
            foreach (var (name, tier, maxRank, cost) in DesignTable)
            {
                var def = ByName(name);

                Assert.AreEqual(ClassAbilityClass.Spellsword, def.AbilityClass, $"{name}: wrong class");
                Assert.AreEqual(tier, def.Tier, $"{name}: wrong tier");
                Assert.AreEqual(maxRank, def.MaxRank, $"{name}: wrong max rank");
                CollectionAssert.AreEqual(cost, def.CostPerRank, $"{name}: wrong per-rank cost");
                Assert.IsTrue(def.Implemented, $"{name}: should be Implemented (all nine ship with a live mechanic)");
            }
        }

        // ---- the generated bundle -----------------------------------------------------------------

        [TestMethod]
        public void SpellswordTraining_IsABundleOverItemEnchantmentDirtyFightingJumpItemTinkering()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.SpellswordTraining);

            Assert.IsInstanceOfType(handler, typeof(BundleStatAbility));

            CollectionAssert.AreEqual(
                new[] { Skill.ItemEnchantment, Skill.DirtyFighting, Skill.Jump, Skill.ItemTinkering },
                ((BundleStatAbility)handler).BundledSkills.ToArray());
        }

        // ---- the two generated Enhanced-stat entries homed to Spellsword -------------------------

        [TestMethod]
        public void EnhancedLightWeapons_IsTheGeneratedEnhancedStatEntryHomedToSpellswordTier1()
        {
            var id = EnhancedStatAbility.ClassIdForSkill(Skill.LightWeapons);

            Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(id), "Enhanced Light Weapons is not registered");

            var def = ClassAbilityRegistry.Abilities[id];

            Assert.AreEqual(ClassAbilityClass.Spellsword, def.AbilityClass);
            Assert.AreEqual(1, def.Tier);

            var handler = ClassAbilityRegistry.GetHandler(id);
            Assert.IsInstanceOfType(handler, typeof(EnhancedStatAbility));
            Assert.AreEqual(Skill.LightWeapons, ((EnhancedStatAbility)handler).TargetSkill);
        }

        [TestMethod]
        public void EnhancedMeleeDefense_IsTheGeneratedEnhancedStatEntryHomedToSpellswordTier2()
        {
            var id = EnhancedStatAbility.ClassIdForSkill(Skill.MeleeDefense);

            Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(id), "Enhanced Melee Defense is not registered");

            var def = ClassAbilityRegistry.Abilities[id];

            Assert.AreEqual(ClassAbilityClass.Spellsword, def.AbilityClass);
            Assert.AreEqual(2, def.Tier);

            var handler = ClassAbilityRegistry.GetHandler(id);
            Assert.IsInstanceOfType(handler, typeof(EnhancedStatAbility));
            Assert.AreEqual(Skill.MeleeDefense, ((EnhancedStatAbility)handler).TargetSkill);
        }

        // ---- tier distribution structural rule ----------------------------------------------------

        /// <summary>
        /// DESIGN.md section 3's structural rule: 4-5 entries at T1, 3-4 at T2, 2-3 at T3. Counts every
        /// Spellsword-homed entry actually in the registry, including the generated bundle and the two
        /// homed Enhanced entries, not just the eight bespoke handlers - a count that only looked at
        /// DesignTable would silently miss the two generated Enhanced-stat entries and undercount T1/T2.
        /// </summary>
        [TestMethod]
        public void TierDistribution_SatisfiesTheFourFiveThreeFourTwoThreeStructuralRule()
        {
            var spellswordEntries = ClassAbilityRegistry.Abilities.Values
                .Where(d => d.AbilityClass == ClassAbilityClass.Spellsword)
                .ToArray();

            var t1 = spellswordEntries.Count(d => d.Tier == 1);
            var t2 = spellswordEntries.Count(d => d.Tier == 2);
            var t3 = spellswordEntries.Count(d => d.Tier == 3);

            Assert.IsTrue(t1 >= 4 && t1 <= 5, $"T1 entry count {t1} is outside the 4-5 structural rule");
            Assert.IsTrue(t2 >= 3 && t2 <= 4, $"T2 entry count {t2} is outside the 3-4 structural rule");
            Assert.IsTrue(t3 >= 2 && t3 <= 3, $"T3 entry count {t3} is outside the 2-3 structural rule");

            // exact counts as of the 2026-08-03 landing: T1 4 (Spellblade, Enhanced Light Weapons,
            // Spellsword Training, Resonance), T2 4 (Runeblade, Sundermark, Enhanced Melee Defense,
            // Spellsurge), T3 3 (Spellstorm, Dispelling Edge, Cascade)
            Assert.AreEqual(4, t1);
            Assert.AreEqual(4, t2);
            Assert.AreEqual(3, t3);
        }

        [TestMethod]
        public void EntryCountForClassTier_MatchesTheDirectCounts()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            Assert.AreEqual(4, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Spellsword, 1));
            Assert.AreEqual(4, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Spellsword, 2));
            Assert.AreEqual(3, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Spellsword, 3));
        }
    }
}
