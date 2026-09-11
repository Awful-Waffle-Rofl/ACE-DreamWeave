using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the per-class CAP (class ability point) totals for all eight classes, after the 2026-08-17
    /// class-ability-point-costs repricing. Companion to <see cref="BloodMageAbilityDefinitionTests"/>'s
    /// Blood-Mage-only CapTotals_ReproduceTheDesignTable, but covering the whole registry so a future reprice
    /// of any class's <see cref="ClassAbilityDefinition.CostPerRank"/> is caught here rather than silently
    /// drifting.
    ///
    /// Totals below were computed from <see cref="ClassAbilityRegistry.Abilities"/> via
    /// <see cref="ClassAbilityCostSummary"/> at write time (a throwaway probe test), not derived by hand -
    /// they include the homed Enhanced-stat and Training entries (each 1/1/1) alongside the hand-written
    /// abilities.
    /// </summary>
    [TestClass]
    public class ClassAbilityCapTotalsTests
    {
        private static long Tunable(string key) => PropertyManager.GetLong(key).Item;

        /// <summary>(class, T1, T2, T3, total), verified against the live registry.</summary>
        private static readonly (ClassAbilityClass Class, int T1, int T2, int T3, int Total)[] ExpectedTotals =
        {
            (ClassAbilityClass.Archer,     15, 12, 15, 42),
            // Rogue was 15/15/15/45 until 2026-08-17, when the Berserker/Rogue balance pass added
            // Surefooted (T2, 2/2/2 = 6) and Pocket Sand (T3, 1/2/3 = 6).
            (ClassAbilityClass.Rogue,      15, 21, 21, 57),
            (ClassAbilityClass.Vanguard,   15, 21,  9, 45),
            // Berserker T2 was 18 (total 48) until 2026-08-17, when Blood Fury (3/3/3 = 9) was retired.
            // Phase 1 registers Break Armor (T2 2/2/2 = 6), taking T2 back to 15 and the class total to 45.
            (ClassAbilityClass.Berserker,  15, 15, 15, 45),
            // Archmage T1 was 13 (total 43) until 2026-08-26, when Spell AOE - the only tier-1 game
            // changer in the system still on a single rank - went to 3 ranks on the standard 1/2/3
            // scale (+5). T1 is now 18 rather than the 15 the other classes sit at, because Archmage
            // carries FIVE T1 entries where every other class carries four (CAP-COST-ASSESSMENT.md
            // sec 'Archmage is the outlier').
            (ClassAbilityClass.Archmage,   18, 15, 15, 48),
            (ClassAbilityClass.VoidSummon, 15, 12,  9, 36),
            (ClassAbilityClass.BloodMage,  15, 18, 15, 48),
            // Spellsword: 15 + 15 + 9 = 39, not the 42 an earlier repricing note claimed - the note's
            // total was a plain arithmetic error; the per-tier figures it gave were correct.
            (ClassAbilityClass.Spellsword, 15, 15,  9, 39),
        };

        [TestMethod]
        public void CapTotals_MatchTheRegistryForEveryClass()
        {
            var defs = ClassAbilityRegistry.Abilities.Values;

            foreach (var (cls, t1, t2, t3, total) in ExpectedTotals)
            {
                Assert.AreEqual(t1, ClassAbilityCostSummary.MaxCostForClassTier(defs, cls, 1), $"{cls}: T1 total");
                Assert.AreEqual(t2, ClassAbilityCostSummary.MaxCostForClassTier(defs, cls, 2), $"{cls}: T2 total");
                Assert.AreEqual(t3, ClassAbilityCostSummary.MaxCostForClassTier(defs, cls, 3), $"{cls}: T3 total");
                Assert.AreEqual(total, ClassAbilityCostSummary.MaxCostForClass(defs, cls), $"{cls}: class total");
            }
        }

        /// <summary>
        /// Every class covers the ExpectedTotals table above and vice versa - so a class added to (or removed
        /// from) the enum, or one whose entries drop to zero, cannot silently fall out of coverage.
        /// </summary>
        [TestMethod]
        public void ExpectedTotals_CoverEveryNonNoneClass()
        {
            var allClasses = System.Enum.GetValues(typeof(ClassAbilityClass))
                .Cast<ClassAbilityClass>()
                .Where(c => c != ClassAbilityClass.None)
                .OrderBy(c => c)
                .ToArray();

            CollectionAssert.AreEqual(
                allClasses,
                ExpectedTotals.Select(e => e.Class).OrderBy(c => c).ToArray());
        }

        /// <summary>
        /// The tier gate (<see cref="ACE.Server.WorldObjects.Player.MeetsClassAbilityTierUnlock"/>) is
        /// class-agnostic: it reads a spent-in-class total plus the tunables below. For every class, a fully
        /// maxed T1 (plus T2, for the T3 gate) must be enough to clear its own class's unlock thresholds -
        /// otherwise that class could earn CAP it can never actually unlock a later tier with.
        /// </summary>
        [TestMethod]
        public void EveryClass_CanReachItsOwnTierGatesWhenMaxed()
        {
            // Tunable() reads PropertyManager, which needs a live config; skip (Inconclusive) rather than NRE when run in isolation.
            TestEnvironment.RequireDatabases();
            var defs = ClassAbilityRegistry.Abilities.Values;
            var tier2Required = Tunable("class_ability_tier2_spent_required");
            var tier3Required = Tunable("class_ability_tier3_spent_required");

            foreach (var (cls, t1, t2, _, _) in ExpectedTotals)
            {
                Assert.IsTrue(t1 >= tier2Required,
                    $"{cls}: a maxed T1 ({t1}) must be enough to clear the Tier 2 spent-in-class gate ({tier2Required})");

                Assert.IsTrue(t1 + t2 >= tier3Required,
                    $"{cls}: a maxed T1+T2 ({t1 + t2}) must be enough to clear the Tier 3 spent-in-class gate ({tier3Required})");

                // cross-check against the live registry directly, not just the pinned table above
                Assert.IsTrue(ClassAbilityCostSummary.MaxCostForClassTier(defs, cls, 1) >= tier2Required);
                Assert.IsTrue(
                    ClassAbilityCostSummary.MaxCostForClassTier(defs, cls, 1)
                    + ClassAbilityCostSummary.MaxCostForClassTier(defs, cls, 2) >= tier3Required);
            }
        }
    }
}
