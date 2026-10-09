using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the pure static math helpers behind the Spellsword mechanics: the three war procs'
    /// rank-invariant Chance(), Sundermark's ranked Chance(), Resonance's stacking DamageMultiplier(),
    /// Spellsurge's ProcChanceBonus(), and Cascade's CanCascade() generation guard. All five are static
    /// and side-effect-free, which is exactly what makes them testable without constructing a Player.
    /// </summary>
    [TestClass]
    public class SpellswordMechanicsTests
    {
        // ---- SpellbladeAbility.Chance: rank-invariance is the design's core claim ------------------
        //
        // MIGRATED 2026-09-12 to the multiplicative affinity primitive: the third parameter is now a
        // FACTOR >= 1.0 (what Player.GetClassAbilityAffinityMultiplier returns), not a raw additive
        // quotient. 1.0 is neutral (no rider); Chance() reports/caps the AMOUNT the multiply ADDS
        // (chanceBase * multiplier - chanceBase), so a multiplier chosen as (1 + desiredAdd / chanceBase)
        // reproduces the exact "added percentage points" the pre-migration tests asserted.

        [TestMethod]
        public void SpellbladeChance_DoesNotChangeWithRank()
        {
            var r1 = SpellbladeAbility.Chance(1, 0.20, 1.0);
            var r2 = SpellbladeAbility.Chance(2, 0.20, 1.0);
            var r3 = SpellbladeAbility.Chance(3, 0.20, 1.0);

            Assert.AreEqual(0.20f, r1, 1e-6f, "rank must not raise chance - rank buys spell level, not chance");
            Assert.AreEqual(r1, r2, 1e-6f);
            Assert.AreEqual(r1, r3, 1e-6f);
        }

        [TestMethod]
        public void SpellbladeChance_AddsTheAffinityRiderAndTheSpellsurgeBonus()
        {
            // multiplier 1.25 on a 0.20 base adds exactly 0.05 (0.20 * 1.25 - 0.20), matching the
            // pre-migration rider of the same size
            var chance = SpellbladeAbility.Chance(1, 0.20, 1.25, 0.06);

            Assert.AreEqual(0.31f, chance, 1e-6f);
        }

        [TestMethod]
        public void SpellbladeChance_RankZeroOrBelow_IsZero()
        {
            Assert.AreEqual(0.0f, SpellbladeAbility.Chance(0, 0.20, 1.25));
            Assert.AreEqual(0.0f, SpellbladeAbility.Chance(-1, 0.20, 1.25));
        }

        [TestMethod]
        public void SpellbladeChance_SubNeutralMultiplierAndNegativeSpellsurge_AreClampedToZero()
        {
            // a multiplier BELOW 1.0 (untrained/inactive never produces one in practice, but the floor must
            // hold regardless) must never reduce the base chance below itself, any more than a negative
            // Spellsurge bonus can
            var chance = SpellbladeAbility.Chance(1, 0.20, 0.50, -0.50);

            Assert.AreEqual(0.20f, chance, 1e-6f, "a sub-neutral affinity multiplier or negative Spellsurge term must not reduce the base chance");
        }

        // ---- RunebladeAbility.Chance: same shape as Spellblade ---------------------------------------

        [TestMethod]
        public void RunebladeChance_DoesNotChangeWithRank()
        {
            var r1 = RunebladeAbility.Chance(1, 0.15, 1.0);
            var r3 = RunebladeAbility.Chance(3, 0.15, 1.0);

            Assert.AreEqual(r1, r3, 1e-6f);
            Assert.AreEqual(0.15f, r1, 1e-6f);
        }

        [TestMethod]
        public void RunebladeChance_AddsTheAffinityRiderAndTheSpellsurgeBonus()
        {
            // multiplier 4/3 on a 0.15 base adds exactly 0.05 (0.15 * 4/3 - 0.15)
            var chance = RunebladeAbility.Chance(1, 0.15, 4.0 / 3.0, 0.04);

            Assert.AreEqual(0.24f, chance, 1e-6f);
        }

        [TestMethod]
        public void RunebladeChance_RankZeroOrBelow_IsZero()
        {
            Assert.AreEqual(0.0f, RunebladeAbility.Chance(0, 0.15, 4.0 / 3.0));
        }

        [TestMethod]
        public void RunebladeChance_SubNeutralMultiplierAndNegativeSpellsurge_AreClampedToZero()
        {
            var chance = RunebladeAbility.Chance(1, 0.15, 0.0, -1.0);

            Assert.AreEqual(0.15f, chance, 1e-6f);
        }

        // ---- SpellstormAbility.Chance: same shape, no affinity rider ---------------------------------

        [TestMethod]
        public void SpellstormChance_DoesNotChangeWithRank()
        {
            // Spellstorm is single-rank (MaxRank 1), but Chance() still takes rank so an unowned caller
            // gets 0; rank 1 is the only owned value it will ever see in production.
            var r1 = SpellstormAbility.Chance(1, 0.10);

            Assert.AreEqual(0.10f, r1, 1e-6f);
        }

        [TestMethod]
        public void SpellstormChance_AddsTheSpellsurgeBonus()
        {
            var chance = SpellstormAbility.Chance(1, 0.10, 0.08);

            Assert.AreEqual(0.18f, chance, 1e-6f);
        }

        [TestMethod]
        public void SpellstormChance_RankZeroOrBelow_IsZero()
        {
            Assert.AreEqual(0.0f, SpellstormAbility.Chance(0, 0.10));
        }

        [TestMethod]
        public void SpellstormChance_NegativeSpellsurgeBonus_IsClampedToZero()
        {
            var chance = SpellstormAbility.Chance(1, 0.10, -1.0);

            Assert.AreEqual(0.10f, chance, 1e-6f);
        }

        // ---- SundermarkAbility.Chance: DOES scale with rank, unlike the war procs --------------------

        [TestMethod]
        public void SundermarkChance_IsFiveTenFifteenPercentAtRanksOneTwoThree_WithNeutralAffinity()
        {
            Assert.AreEqual(0.05f, SundermarkAbility.Chance(1, 0.05, 0.05, 1.0), 1e-6f);
            Assert.AreEqual(0.10f, SundermarkAbility.Chance(2, 0.05, 0.05, 1.0), 1e-6f);
            Assert.AreEqual(0.15f, SundermarkAbility.Chance(3, 0.05, 0.05, 1.0), 1e-6f);
        }

        [TestMethod]
        public void SundermarkChance_RankZero_IsZero()
        {
            Assert.AreEqual(0.0f, SundermarkAbility.Chance(0, 0.05, 0.05, 1.0));
        }

        [TestMethod]
        public void SundermarkChance_AddsTheLifeMagicRider()
        {
            // at rank 1 the rank-scaled base is 0.05; multiplier 1.6 adds exactly 0.03 (0.05 * 1.6 - 0.05)
            var chance = SundermarkAbility.Chance(1, 0.05, 0.05, 1.6);

            Assert.AreEqual(0.08f, chance, 1e-6f);
        }

        // ---- the affinity cap: the regression guard for the 100%-proc bug --------------------------------
        //
        // LIVE BUG, 2026-08-04, still the regression this cap guards against after the 2026-09-12
        // migration to the multiplicative primitive. GetClassAbilityAffinityMultiplier has no ceiling of its
        // own (ClassAbilityAffinity.Multiplier is linear in the source skill), so the AMOUNT it adds to an
        // ability's own bonus is unbounded unless the call site clamps it. A character with Item Enchantment
        // 5226 produced an added amount of +209 percentage points under the old additive rider and every war
        // proc with an affinity fired on literally every swing. Spellstorm - the one war proc with no
        // affinity - was simultaneously correct at ~20%, which is what isolated the term.
        //
        // These tests pin the cap at the magnitude that actually broke it, not at a token value, because a
        // cap that only holds for small riders would not have caught this. Each multiplier below is chosen
        // as (1 + 2.09 / chanceBase) so the ADDED amount is the same +2.09 the live bug measured, whatever
        // the ability's own base happens to be.

        [TestMethod]
        public void SpellbladeChance_AffinityCap_BoundsAnAbsurdRider()
        {
            // 1 + 2.09/0.20 = 11.45; added = 0.20 * 11.45 - 0.20 = 2.09, i.e. +209 percentage points
            var uncapped = SpellbladeAbility.Chance(3, 0.20, 11.45, 0.0);
            Assert.IsTrue(uncapped >= 1.0f, "control: without a cap this rider saturates the proc to certainty");

            var capped = SpellbladeAbility.Chance(3, 0.20, 11.45, 0.0, 0.20);

            Assert.AreEqual(0.40f, capped, 1e-6f, "base 0.20 + capped affinity 0.20");
            Assert.IsTrue(capped < 1.0f, "a capped proc must never reach certainty from the affinity alone");
        }

        [TestMethod]
        public void RunebladeChance_AffinityCap_BoundsAnAbsurdRider()
        {
            // 1 + 2.09/0.15 = 14.9333...
            var hugeMultiplier = 1.0 + 2.09 / 0.15;

            Assert.IsTrue(RunebladeAbility.Chance(3, 0.15, hugeMultiplier, 0.0) >= 1.0f, "control: uncapped saturates");

            Assert.AreEqual(0.35f, RunebladeAbility.Chance(3, 0.15, hugeMultiplier, 0.0, 0.20), 1e-6f);
        }

        [TestMethod]
        public void SundermarkChance_AffinityCap_KeepsTheChanceLow()
        {
            // Sundermark is the entry most sensitive to this: the design's containment argument is that its
            // chance stays LOW, so a rider reaching certainty deletes the premise rather than just tuning it.
            // Rank 3's rank-scaled base is 0.15 (0.05 + 2*0.05), so the same multiplier as Runeblade's test
            // adds the same +2.09.
            var hugeMultiplier = 1.0 + 2.09 / 0.15;

            Assert.IsTrue(SundermarkAbility.Chance(3, 0.05, 0.05, hugeMultiplier) >= 1.0f, "control: uncapped saturates");

            Assert.AreEqual(0.35f, SundermarkAbility.Chance(3, 0.05, 0.05, hugeMultiplier, 0.20), 1e-6f);
        }

        [TestMethod]
        public void AffinityCap_DoesNotBiteAtLegitimateEndgameSkill()
        {
            // 1 + 0.16/0.20 = 1.8; added = 0.20 * 1.8 - 0.20 = 0.16, comfortably under the 0.20 cap - so the
            // cap fixes the pathological case without silently repricing the endgame anchor the power
            // ledger scored.
            var atEndgame = SpellbladeAbility.Chance(3, 0.20, 1.8, 0.0, 0.20);

            Assert.AreEqual(0.36f, atEndgame, 1e-6f, "the ledger's modelled 36% must be unchanged by the cap");
        }

        [TestMethod]
        public void AffinityCap_OfZero_MeansUncapped()
        {
            Assert.AreEqual(2.29f, SpellbladeAbility.Chance(3, 0.20, 11.45, 0.0, 0.0), 1e-5f);
        }

        // ---- ResonanceAbility.DamageMultiplier --------------------------------------------------------

        [TestMethod]
        public void ResonanceDamageMultiplier_ZeroStacks_IsOne()
        {
            Assert.AreEqual(1.0f, ResonanceAbility.DamageMultiplier(3, 0, 0.02, 0.03, 0.04), 1e-6f);
        }

        [TestMethod]
        public void ResonanceDamageMultiplier_FullFiveStacks_MatchesTheFullRampByRank()
        {
            Assert.AreEqual(1.10f, ResonanceAbility.DamageMultiplier(1, 5, 0.02, 0.03, 0.04), 1e-6f, "rank 1: +10% at 5 stacks");
            Assert.AreEqual(1.15f, ResonanceAbility.DamageMultiplier(2, 5, 0.02, 0.03, 0.04), 1e-6f, "rank 2: +15% at 5 stacks");
            Assert.AreEqual(1.20f, ResonanceAbility.DamageMultiplier(3, 5, 0.02, 0.03, 0.04), 1e-6f, "rank 3: +20% at 5 stacks");
        }

        [TestMethod]
        public void ResonanceDamageMultiplier_RankZero_IsOne()
        {
            Assert.AreEqual(1.0f, ResonanceAbility.DamageMultiplier(0, 5, 0.02, 0.03, 0.04), 1e-6f);
        }

        // ---- SpellsurgeAbility.ProcChanceBonus ----------------------------------------------------------

        [TestMethod]
        public void SpellsurgeProcChanceBonus_ZeroStacks_IsZero()
        {
            Assert.AreEqual(0.0f, SpellsurgeAbility.ProcChanceBonus(1, 0, 0.02));
        }

        [TestMethod]
        public void SpellsurgeProcChanceBonus_FiveStacks_IsTenPercentagePoints()
        {
            Assert.AreEqual(0.10f, SpellsurgeAbility.ProcChanceBonus(1, 5, 0.02), 1e-6f);
        }

        [TestMethod]
        public void SpellsurgeProcChanceBonus_Unowned_IsZero()
        {
            // rank 0 = not owned
            Assert.AreEqual(0.0f, SpellsurgeAbility.ProcChanceBonus(0, 5, 0.02));
        }

        // ---- CascadeAbility.CanCascade: the generation guard --------------------------------------------

        [TestMethod]
        public void CanCascade_GenerationZeroWithMaxOne_IsAllowed()
        {
            Assert.IsTrue(CascadeAbility.CanCascade(isClassAbilityProc: true, generation: 0, maxGeneration: 1),
                "a primary proc (generation 0) must be allowed exactly one hop when max generation is 1");
        }

        /// <summary>
        /// This is the refusal that is the entire point of the Q13 guard: without it, an unflagged
        /// cascade child (generation 1) is itself a landed proc that could cascade again, and again,
        /// unbounded in the tail. At generation 1 with max generation 1, the guard must refuse.
        /// </summary>
        [TestMethod]
        public void CanCascade_GenerationOneWithMaxOne_IsRefused()
        {
            Assert.IsFalse(CascadeAbility.CanCascade(isClassAbilityProc: true, generation: 1, maxGeneration: 1),
                "a cascade child (generation 1) must not be allowed to cascade again under the default max generation of 1");
        }

        [TestMethod]
        public void CanCascade_NotAClassAbilityProc_IsRefusedRegardlessOfGeneration()
        {
            Assert.IsFalse(CascadeAbility.CanCascade(isClassAbilityProc: false, generation: 0, maxGeneration: 1),
                "an ordinary war cast (not a Spellsword proc) must never cascade");
        }
    }
}
