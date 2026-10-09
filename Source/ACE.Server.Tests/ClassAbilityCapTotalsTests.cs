using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the per-class CAP (class ability point) totals for all eight classes, after the 2026-09-12 class
    /// ability overhaul (data pass: costs, tiers, three retirements, two un-homings). Companion to
    /// <see cref="BloodMageAbilityDefinitionTests"/>'s Blood-Mage-only CapTotals_ReproduceTheDesignTable, but
    /// covering the whole registry so a future reprice of any class's
    /// <see cref="ClassAbilityDefinition.CostPerRank"/> is caught here rather than silently drifting.
    ///
    /// Totals below were computed from <see cref="ClassAbilityRegistry.Abilities"/> via
    /// <see cref="ClassAbilityCostSummary"/> at write time (a throwaway probe test), not derived by hand -
    /// they include the homed Enhanced-stat and Training entries (each 1/1/1) alongside the hand-written
    /// abilities.
    ///
    /// THE 2026-09-12 OVERHAUL TOTALS ARE NO LONGER THE CURRENT ONES, and the claim this comment used to
    /// make has been retired rather than renumbered. It read that every class "lands exactly on its
    /// signed-off design-table buyout total" and then named all eight - Archer 38, Rogue 44, Vanguard 41,
    /// Berserker 38, Archmage 41, VoidSummon 41, BloodMage 41, Spellsword 38. The 2026-09-13 PREMIUM
    /// REPRICING moved sixteen abilities from 1 CAP per rank to 2, so all eight of those figures are now
    /// historical and the design table they were signed off against is the pre-repricing one: Archer 41,
    /// Rogue 50, Vanguard 47, Berserker 44, Archmage 44, VoidSummon 47, BloodMage 47, Spellsword 44.
    ///
    /// THE EIGHT DELTAS ARE NOT ALL THE SAME SIZE, and the reason is worth recording because it looks like
    /// an arithmetic slip. Fourteen of the sixteen were flat 1/1/1 and each adds exactly 3 to its class.
    /// The other two - Crit Rating (Archer T3) and Echo Cast (Archmage T3) - were on the 1/2/3 escalator
    /// and went to flat 2/2/2, so their buyout total is UNCHANGED at 6 and their rank 3 actually got
    /// cheaper. Archer and Archmage therefore gain 3 rather than 6. A reprice that "must" raise a total is
    /// the assumption to distrust here.
    ///
    /// EVERY FIGURE BELOW IS CHECKED AGAINST THE LIVE REGISTRY BY THIS TEST, which is the only thing that
    /// makes them trustworthy - CapTotals_MatchTheRegistryForEveryClass reads
    /// ClassAbilityRegistry.Abilities through ClassAbilityCostSummary and compares every cell. Do not
    /// hand-adjust a cell here to make a failure go away: a mismatch means an ability moved class or tier,
    /// or a cost changed, and the registry is the authority.
    ///
    /// THE FIFTEEN NEW ENTRIES ARE REGISTRATION ONLY (Implemented = false) AND STILL COUNT HERE. That is
    /// correct rather than an oversight: ClassAbilityCostSummary deliberately ignores Implemented, because a
    /// class's published CAP budget is a property of its design table and not of which mechanics have
    /// shipped. So these totals will NOT move again as the fifteen mechanic slices land one by one - a slice
    /// that changes a total has changed a cost, which is what this test exists to catch.
    /// </summary>
    [TestClass]
    public class ClassAbilityCapTotalsTests
    {
        private static long Tunable(string key) => PropertyManager.GetLong(key).Item;

        /// <summary>(class, T1, T2, T3, total), verified against the live registry.</summary>
        private static readonly (ClassAbilityClass Class, int T1, int T2, int T3, int Total)[] ExpectedTotals =
        {
            // 2026-09-12 class ability overhaul (data pass + new-ability wave), THEN the 2026-09-13 premium
            // repricing. Every figure here is checked against the live registry by the test below.
            //
            // The data pass repriced 26 abilities (almost all off a 1/2/3 or flat-2/flat-3 curve onto flat
            // 1/rank), moved 13 between tiers, retired three (Heavy Draw from Archer T2, Shield Check from
            // Vanguard T2, Surefooted from Rogue T2), un-homed Enhanced Mana and Enhanced Stamina out of
            // Archmage T1 and Berserker T2, and moved the six class attributes T1 -> T2.
            //
            // The new-ability wave then added fifteen registration-only entries and moved Enhanced Melee
            // Defense back from Spellsword T2 to Rogue T2. EVERY CLASS GAINED EXACTLY 5 CAP AT T1 from its
            // 5-rank splash ability, which is why all eight T1 figures below were 14 or 17 at that point -
            // the 17s were Blood Mage and Spellsword, the two classes whose T1 Enhanced entry is a skill
            // (Life Magic, Light Weapons) rather than an attribute that moved to T2, so they carried one
            // more T1 entry than the other six. NO T1 FIGURE MOVED IN THE REPRICING: all sixteen repriced
            // entries sit at T2 or T3, so every change from the repricing was in the middle two columns.
            //
            // The premium repricing took exactly two entries per class to 2 CAP per rank, one at T2 and one
            // at T3 - Archer (pinning_shot, crit_rating), Rogue (acidproc, attackspeed), Vanguard
            // (crit_resist_rating, enhanced_health), Berserker (vengeance, damage_rating), Archmage
            // (overchannel, echocast), VoidSummon (empoweredsummons, soul_jump), BloodMage (malediction,
            // blood_price), Spellsword (sundermark, dispellingedge).
            //
            // THE 2026-09-14 RE-HOMING moved Enhanced Stamina back to Berserker T1 and Enhanced Mana back
            // to Archmage T1 (owner ruling - both had been un-homed on 2026-09-12; see this dictionary's doc
            // comment in EnhancedStatAbility.cs). Each is a flat 1/1/1 entry, so it adds exactly 3 to its
            // class's T1 and class total; Berserker T1 goes 14 -> 17 and Archmage T1 goes 14 -> 17, with
            // Berserker and Archmage totals both up 3.
            //
            // The trailing comment on each row is the ENTRY COUNT per tier, because a total alone cannot
            // distinguish "an ability got cheaper" from "an ability left the class". NO ENTRY COUNT CHANGED
            // IN THE REPRICING - it moved prices only, never homes - so every count below reflects the
            // new-ability wave plus the 2026-09-14 re-homing.
            //
            // Archer T3 stays 12: crit_rating went 1/2/3 -> 2/2/2, which is 6 either way.
            // Archer T2 is 12, down from 15: Pinning Shot's 2026-09-13 premium reprice (2/rank) was
            // REVERSED 2026-09-29 back to 1/rank (Fast Aim rework companion change), -3.
            (ClassAbilityClass.Archer,     14, 12, 12, 38),   // entries 4/4/3
            // Rogue T3 is the highest T3 in the system at 18: Killer Instinct (2/2/2 = 6, priced that way by
            // the design table for a stacking crit-damage-and-crit-chance engine) now sits beside
            // attackspeed, repriced to 2/2/2 as well. Rogue T2 is 18 because Enhanced Melee Defense returned
            // to it from Spellsword AND acidproc was repriced.
            (ClassAbilityClass.Rogue,      14, 18, 18, 50),   // entries 4/5/4
            // VANGUARD PASS 2026-09-29, and the two moves are deliberate rather than a failure being papered
            // over (which is what this table's doc comment above warns against). Kinetic Charge's 1/1/1
            // buyout of 3 moved T2 -> T1 on an owner ruling: T1 14 -> 17, T2 18 -> 15, class total unchanged
            // at 47 because MaxCostForClass does not filter on tier. Cloaked in Power then landed as a new
            // 1-CAP single-rank Vanguard T2 entry: T2 15 -> 16 and the class total 47 -> 48. Entry counts go
            // 4/5/4 -> 5/5/4 (Kinetic Charge leaves T2 and joins T1, Cloaked in Power replaces it in T2).
            //
            // CONSEQUENCE WORTH NAMING: at T1 = 17 a maxed Vanguard T1 ALONE now clears the Tier 3
            // spent-in-class gate (class_ability_tier3_spent_required = 15), the same property Blood Mage and
            // Spellsword already have at 17. EveryClass_CanReachItsOwnTierGatesWhenMaxed only asserts the
            // gates are REACHABLE, so it stays green; this is a real balance consequence of the ruling, not a
            // test artifact.
            (ClassAbilityClass.Vanguard,   17, 16, 15, 48),   // entries 5/5/4
            // Berserker T1 is 17, up from 14: Enhanced Stamina re-homed here 2026-09-14 (see above).
            (ClassAbilityClass.Berserker,  17, 18, 12, 47),   // entries 5/5/3
            // Archmage T3 stays 15. Echo Cast went 1/2/3 -> 2/2/2 (6 either way), so the only reason this
            // column is above 9 is Echo Cast's 6 plus Mana Barrier's 6 - and Mana Barrier is now the LAST
            // entry in the system still on the 1/2/3 escalator, Echo Cast having left it.
            // Archmage T1 is 17, up from 14: Enhanced Mana re-homed here 2026-09-14 (see above).
            (ClassAbilityClass.Archmage,   17, 15, 15, 47),   // entries 5/4/3
            (ClassAbilityClass.VoidSummon, 14, 18, 15, 47),   // entries 4/5/4
            // Blood Mage T1 is 17, which RESTORES the "a maxed T1 alone unlocks T3" property the data pass
            // had broken by taking it to 12 against a 15-point gate. See
            // BloodMageAbilityDefinitionTests.MaxedTier1_ExactlyMeetsTheTier3SpentInClassGate, whose
            // equality assertion this deliberately ended. The repricing did not touch T1, so it still holds.
            (ClassAbilityClass.BloodMage,  17, 15, 15, 47),   // entries 5/4/3
            // Spellsword T2 is 15: it lost Enhanced Melee Defense (-3), Runic Ward replaced it (+3) in the
            // same wave, and sundermark's reprice then added 3.
            (ClassAbilityClass.Spellsword, 17, 15, 12, 44),   // entries 5/4/3
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
