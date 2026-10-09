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
    /// per-rank cost, plus the generated families (the SpellswordTraining bundle and the
    /// EnhancedStatAbility skill still homed to this class) and the tier-count structural rule from
    /// DESIGN.md section 3 (4-5 / 3-4 / 2-3 entries per tier). Mirrors
    /// BloodMageAbilityDefinitionTests.cs's structure for the 8th class.
    ///
    /// REPRICED 2026-09-12 by the class ability overhaul's data pass: Spellblade came off the 1/2/3
    /// escalator and Sundermark off flat 2/2/2, both onto flat 1 per rank. Tiers, max ranks and the
    /// bespoke entry set are unchanged by that pass.
    ///
    /// REPRICED AGAIN 2026-09-13 by the premium repricing, which took Sundermark (T2) and Dispelling Edge
    /// (T3) from 1 CAP per rank to 2, moving the class to 17/15/12/44. Sundermark has therefore been
    /// 2/2/2, then 1/1/1, and is now 2/2/2 again - a diff against an old revision can look like the data
    /// pass was reverted, and it was not; this is a separate later decision to price the class's two
    /// strongest entries above the flat baseline. Dispelling Edge also gained spell reach in the same
    /// change (it now implements ISpellHitAbility as well as IOutgoingDamageAbility), which is what the
    /// higher price is buying.
    ///
    /// THEN RESHAPED 2026-09-12 by the same overhaul's new-ability wave, which moved this class's tier
    /// counts from 4/4/3 to 5/4/3 in two independent steps:
    ///  - SPELLWEAVE was added at Tier 1, the class's 5-rank "splash" ability (all eight classes got
    ///    one), taking T1 from four entries to five.
    ///  - ENHANCED MELEE DEFENSE LEFT THE CLASS, returning to Rogue Tier 2. This is the overhaul's
    ///    single signed-off class-change row, not drift: the skill began as Rogue T2, was loaned to
    ///    Spellsword on 2026-08-03 while this class had no defensive Enhanced entry, and has now gone
    ///    home. Runic Ward was added at Tier 2 in the same wave, so T2 stays at four entries and 12 CAP
    ///    even though it lost a 3-CAP member.
    ///
    /// THE STRUCTURAL RULE THIS FILE IS NAMED AFTER STILL HOLDS, and the test name is still accurate:
    /// it names the RANGE rule (4-5 / 3-4 / 2-3 entries per tier), not a fixed 4/4/3 shape, and 5/4/3
    /// satisfies every band. Only the exact-count pins inside the tests moved.
    ///
    /// Neither Spellweave nor Runic Ward is asserted by this file's DesignTable, which covers the eight
    /// bespoke handlers that shipped 2026-08-03 plus the generated bundle. Both new entries are
    /// registration-only (Implemented = false), and DesignTable asserts Implemented on every row it
    /// lists, so adding them here would be wrong until their mechanic slices land.
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
            ("spellblade",          1, 3, new[] { 1, 1, 1 }),
            ("resonance",           1, 3, new[] { 1, 1, 1 }),
            ("spellsword_training", 1, 3, new[] { 1, 1, 1 }),
            // Tier 2 - unlock: 3 CAPs, 5 spent in class
            ("runeblade",           2, 3, new[] { 1, 1, 1 }),
            // premium reprice 2026-09-13: 1/rank -> 2/rank
            ("sundermark",          2, 3, new[] { 2, 2, 2 }),
            ("spellsurge",          2, 1, new[] { 3 }),
            // Tier 3 - unlock: 8 CAPs, 15 spent in class
            ("spellstorm",          3, 1, new[] { 3 }),
            ("cascade",             3, 1, new[] { 3 }),
            // premium reprice 2026-09-13: 1/rank -> 2/rank
            ("dispellingedge",      3, 3, new[] { 2, 2, 2 }),
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

        // ---- the generated Enhanced-stat entries: one still homed here, one that went home ---------

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

        /// <summary>
        /// Enhanced Melee Defense is NOT a Spellsword entry, and this test survives in this file to say so
        /// deliberately rather than being deleted. It asserted Spellsword Tier 2 until 2026-09-12, when the
        /// class ability overhaul's single signed-off class-change row returned the skill to Rogue Tier 2 -
        /// the home it had before its 2026-08-03 loan to this class.
        ///
        /// It is repointed rather than removed because the loan is exactly the kind of thing that gets
        /// re-made by accident. Homing is a dictionary, so a Spellsword row for Skill.MeleeDefense would not
        /// double-home it - it would silently take it back off Rogue and break that class's 44 CAP total.
        /// This test fails first and names the class if that happens.
        /// </summary>
        [TestMethod]
        public void EnhancedMeleeDefense_LeftSpellsword_AndIsHomedToRogueTier2()
        {
            var id = EnhancedStatAbility.ClassIdForSkill(Skill.MeleeDefense);

            Assert.IsTrue(ClassAbilityRegistry.Abilities.ContainsKey(id), "Enhanced Melee Defense is not registered");

            var def = ClassAbilityRegistry.Abilities[id];

            Assert.AreEqual(ClassAbilityClass.Rogue, def.AbilityClass,
                "Enhanced Melee Defense returned to Rogue on 2026-09-12; a Spellsword home here means the move was reverted or re-loaned");
            Assert.AreEqual(2, def.Tier);

            var handler = ClassAbilityRegistry.GetHandler(id);
            Assert.IsInstanceOfType(handler, typeof(EnhancedStatAbility));
            Assert.AreEqual(Skill.MeleeDefense, ((EnhancedStatAbility)handler).TargetSkill);
        }

        // ---- tier distribution structural rule ----------------------------------------------------

        /// <summary>
        /// DESIGN.md section 3's structural rule: 4-5 entries at T1, 3-4 at T2, 2-3 at T3. Counts every
        /// Spellsword-homed entry actually in the registry, including the generated bundle, the homed
        /// Enhanced entry and the two registration-only entries from the 2026-09-12 new-ability wave, not
        /// just the eight bespoke handlers - a count that only looked at DesignTable would miss all of
        /// those and undercount every tier.
        ///
        /// THE RANGE RULE THIS TEST IS NAMED AFTER IS UNCHANGED AND STILL SATISFIED. The exact shape moved
        /// from 4/4/3 to 5/4/3 on 2026-09-12 (Spellweave added at T1; Enhanced Melee Defense left T2 for
        /// Rogue and Runic Ward replaced it), and 5 is still inside the T1 band of 4-5. Only the exact
        /// pins below moved; the name still describes the rule being tested.
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

            // exact counts as of the 2026-09-12 new-ability wave: T1 5 (Spellblade, Enhanced Light
            // Weapons, Spellsword Training, Resonance, Spellweave), T2 4 (Runeblade, Sundermark,
            // Spellsurge, Runic Ward - Enhanced Melee Defense left for Rogue and Runic Ward replaced it,
            // so the count is unchanged), T3 3 (Spellstorm, Dispelling Edge, Cascade)
            Assert.AreEqual(5, t1);
            Assert.AreEqual(4, t2);
            Assert.AreEqual(3, t3);
        }

        [TestMethod]
        public void EntryCountForClassTier_MatchesTheDirectCounts()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            // FIVE at T1 since the 2026-09-12 new-ability wave added Spellweave; T2 is still four because
            // Runic Ward replaced Enhanced Melee Defense, which returned to Rogue in the same wave.
            Assert.AreEqual(5, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Spellsword, 1));
            Assert.AreEqual(4, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Spellsword, 2));
            Assert.AreEqual(3, ClassAbilityCostSummary.EntryCountForClassTier(defs, ClassAbilityClass.Spellsword, 3));
        }
    }
}
