using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The GEAR TERM on the eight Spellsword ability helpers (equipment mods 39-46: Spellblade, Harmonics,
    /// Runeblade, Sundermark, Surge, Spellstorm, Cascade, Dispelling Edge) and the Vanguard correction
    /// (47: Provoke; 48: Bellow; 49: Shield Wall), plus the two AFFINITY CAPS that landed with Shield Wall -
    /// on ShieldBlockAbility.BlockChance and DispellingEdgeAbility.Chance, the two chance helpers the
    /// 2026-08-04 class_ability_affinity_chance_cap fix missed.
    ///
    /// Three invariants are pinned here, and each one is a real failure this design exists to prevent:
    ///
    ///  1. THE AXIS RULE (DESIGN.md 3.3). A gear term is added INSIDE its ability's own parenthesis, on the
    ///     same additive axis as the ability's own bonus - never as a separate multiplier. Each mod's test
    ///     therefore carries a CONTROL asserting the result is NOT what the multiplicative shape would have
    ///     produced, because "bigger than the ungeared value" is true of both shapes and proves nothing.
    ///  2. INERT WHEN OFF. Every helper takes its gear parameter LAST and DEFAULTED TO 0, so passing 0.0
    ///     explicitly reproduces the no-gear overload's value bit-for-bit, and every pre-existing call site
    ///     and test keeps compiling untouched.
    ///  3. THE GEAR TERM SURVIVES A FULLY CAPPED RIDER. Spellblade, Runeblade and Sundermark clamp their
    ///     affinity rider to class_ability_affinity_chance_cap, and Taunt clamps its Loyalty rider to
    ///     class_ability_taunt_loyalty_bonus_cap_seconds. The gear term is added OUTSIDE all four clamps, so
    ///     each of those four carries a control that drives the rider far past its cap and asserts the mod is
    ///     still worth its full magnitude. Folding gear inside a cap is exactly the Resonance failure
    ///     recorded in DESIGN.md 2.2, where a saturating rider silently ate a mod.
    ///
    /// Every helper under test is a pure static, so nothing here needs a live Player - which is why the
    /// arithmetic lives in them at all (Player's static initializer cannot run under this test host).
    ///
    /// NOT COVERED HERE, and deliberately:
    ///  - BELLOW has no pure helper. Its gear term is an inline addition to the taunt radius read in
    ///    TauntAbility.Activate, which needs a live player, a landblock and visible creatures. Its unit -
    ///    metres of radius - and its machinery gating are asserted structurally below instead.
    ///  - The /abilities readouts for Spellblade, Runeblade, Sundermark, Dispelling Edge and Taunt. All five
    ///    call player.GetClassAbilityScaling on the live Player before reaching the gear read, so they cannot
    ///    be invoked from this host at all. The four that CAN (Resonance, Spellsurge, Spellstorm, Cascade)
    ///    are covered in SpellswordReadoutTests.
    ///  - That Player.GetMachineryEquipmentModValue actually returns 0 for a non-owner running /testskill
    ///    taunt. The gate itself is a Player method; what is asserted here is that Activate routes through
    ///    it rather than through the ungated read.
    /// </summary>
    [TestClass]
    public class SpellswordVanguardEquipmentModTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        // The perfect-roll magnitudes the arithmetic below is written against. Read from the live registry
        // rather than hardcoded, so a retune of any row fails this one test instead of silently invalidating
        // every expected value in the file.
        private static double Max(EquipmentModId id) => EquipmentModRegistry.Get(id).MaxMagnitude;

        // Far past any affinity cap: the live 2026-08-04 report was a rider of +209 percentage points.
        private const double SaturatingRider = 2.09;

        [TestMethod]
        public void RegistryMagnitudes_AreWhatTheseTestsAssume()
        {
            Assert.AreEqual(0.036, Max(EquipmentModId.Spellblade), 1e-12, "pp of Spellblade proc chance");
            Assert.AreEqual(0.0048, Max(EquipmentModId.Harmonics), 1e-12, "magic damage per Resonance stack");
            Assert.AreEqual(0.036, Max(EquipmentModId.Runeblade), 1e-12, "pp of Runeblade proc chance");
            Assert.AreEqual(0.015, Max(EquipmentModId.Sundermark), 1e-12, "pp of Sundermark proc chance");
            Assert.AreEqual(0.0051, Max(EquipmentModId.Surge), 1e-12, "pp of war-proc chance per Spellsurge stack");
            Assert.AreEqual(0.033, Max(EquipmentModId.Spellstorm), 1e-12, "pp of Spellstorm proc chance");
            Assert.AreEqual(0.025, Max(EquipmentModId.Cascade), 1e-12, "pp of Cascade chain chance");
            Assert.AreEqual(0.02, Max(EquipmentModId.DispellingEdge), 1e-12, "pp of Dispelling Edge proc chance");
            Assert.AreEqual(2.0, Max(EquipmentModId.Provoke), 1e-12, "seconds of Taunt hold duration");
            Assert.AreEqual(1.0, Max(EquipmentModId.Bellow), 1e-12, "metres of Taunt radius");
            Assert.AreEqual(0.0141, Max(EquipmentModId.ShieldWall), 1e-12, "pp of shield block chance");
        }

        // ---- 39: Spellblade -> SpellbladeAbility.Chance ----------------------------------------------
        //
        // Shape: chanceBase + clamp(rider) + spellsurgeBonus + gear. The gear term is OUTSIDE the clamp.

        [TestMethod]
        public void Spellblade_ZeroGear_ReproducesTheUngearedChanceExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(SpellbladeAbility.Chance(rank, 0.20, 0.05, 0.06, 0.20),
                                SpellbladeAbility.Chance(rank, 0.20, 0.05, 0.06, 0.20, 0.0),
                                0.0f, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            // and still the shipped number
            Assert.AreEqual(0.20f, SpellbladeAbility.Chance(1, 0.20, 0.0, 0.0, 0.20, 0.0), 1e-6f);
        }

        [TestMethod]
        public void Spellblade_Gear_IsAFurtherAdditiveSummandOnTheProcChance()
        {
            var gear = Max(EquipmentModId.Spellblade); // 0.036

            // 0.20 base + 0.05 rider (under the 0.20 cap) + 0.036 gear
            Assert.AreEqual(0.286f, SpellbladeAbility.Chance(1, 0.20, 0.05, 0.0, 0.20, gear), 1e-6f);

            // CONTROL - the axis rule. A separate multiplier would be 0.25 * 1.036 = 0.259, a DIFFERENT
            // number, so this assertion can actually fail if the shape ever changes.
            Assert.AreNotEqual(0.25 * (1.0 + gear), (double)SpellbladeAbility.Chance(1, 0.20, 0.05, 0.0, 0.20, gear), 1e-4,
                "the gear term must be a summand on the chance, not a factor on it");
        }

        [TestMethod]
        public void Spellblade_Gear_StillAppliesWhenTheAffinityRiderIsFullyCapped()
        {
            var gear = Max(EquipmentModId.Spellblade); // 0.036
            var cap = D("class_ability_affinity_chance_cap"); // 0.20

            // control: the rider really is saturating - uncapped it alone would put the proc past certainty
            Assert.IsTrue(SpellbladeAbility.Chance(3, 0.20, SaturatingRider, 0.0, 0.0) >= 1.0f,
                "control: an uncapped rider of +209pp saturates the proc, which is what the cap exists for");

            var cappedNoGear = SpellbladeAbility.Chance(3, 0.20, SaturatingRider, 0.0, cap);
            var cappedWithGear = SpellbladeAbility.Chance(3, 0.20, SaturatingRider, 0.0, cap, gear);

            Assert.AreEqual(0.40f, cappedNoGear, 1e-6f, "base 0.20 plus the rider clamped to 0.20");

            // THE REGRESSION THIS FILE EXISTS FOR: the mod is worth its FULL magnitude even though the rider
            // has entirely consumed the cap. Inside the clamp it would have been worth exactly nothing.
            Assert.AreEqual(0.436f, cappedWithGear, 1e-6f);
            Assert.AreEqual(gear, cappedWithGear - cappedNoGear, 1e-6,
                "a fully capped rider must not eat one point of the gear term");
        }

        [TestMethod]
        public void Spellblade_RankZeroReturnsNothingHoweverMuchGearIsWorn()
        {
            // the machinery gate: the rank test runs BEFORE any term is summed, so an item alone can never
            // proc a spell for a character who never bought Spellblade
            Assert.AreEqual(0.0f, SpellbladeAbility.Chance(0, 0.20, 0.05, 0.06, 0.20, 0.036));
            Assert.AreEqual(0.0f, SpellbladeAbility.Chance(-1, 0.20, 0.05, 0.06, 0.20, 0.036));
        }

        // ---- 41: Runeblade -> RunebladeAbility.Chance ------------------------------------------------

        [TestMethod]
        public void Runeblade_ZeroGear_ReproducesTheUngearedChanceExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(RunebladeAbility.Chance(rank, 0.15, 0.05, 0.04, 0.20),
                                RunebladeAbility.Chance(rank, 0.15, 0.05, 0.04, 0.20, 0.0),
                                0.0f, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(0.15f, RunebladeAbility.Chance(1, 0.15, 0.0, 0.0, 0.20, 0.0), 1e-6f);
        }

        [TestMethod]
        public void Runeblade_Gear_IsAFurtherAdditiveSummandOnTheProcChance()
        {
            var gear = Max(EquipmentModId.Runeblade); // 0.036

            Assert.AreEqual(0.236f, RunebladeAbility.Chance(1, 0.15, 0.05, 0.0, 0.20, gear), 1e-6f);

            // CONTROL - a separate multiplier would be 0.20 * 1.036 = 0.2072
            Assert.AreNotEqual(0.20 * (1.0 + gear), (double)RunebladeAbility.Chance(1, 0.15, 0.05, 0.0, 0.20, gear), 1e-4,
                "the gear term must be a summand on the chance, not a factor on it");
        }

        [TestMethod]
        public void Runeblade_Gear_StillAppliesWhenTheAffinityRiderIsFullyCapped()
        {
            var gear = Max(EquipmentModId.Runeblade);
            var cap = D("class_ability_affinity_chance_cap");

            Assert.IsTrue(RunebladeAbility.Chance(3, 0.15, SaturatingRider, 0.0) >= 1.0f, "control: uncapped saturates");

            var cappedNoGear = RunebladeAbility.Chance(3, 0.15, SaturatingRider, 0.0, cap);
            var cappedWithGear = RunebladeAbility.Chance(3, 0.15, SaturatingRider, 0.0, cap, gear);

            Assert.AreEqual(0.35f, cappedNoGear, 1e-6f);
            Assert.AreEqual(0.386f, cappedWithGear, 1e-6f);
            Assert.AreEqual(gear, cappedWithGear - cappedNoGear, 1e-6,
                "a fully capped rider must not eat one point of the gear term");
        }

        [TestMethod]
        public void Runeblade_RankZeroReturnsNothingHoweverMuchGearIsWorn()
        {
            Assert.AreEqual(0.0f, RunebladeAbility.Chance(0, 0.15, 0.05, 0.04, 0.20, 0.036));
        }

        // ---- 42: Sundermark -> SundermarkAbility.Chance ----------------------------------------------
        //
        // Unlike the war procs this one DOES carry a rank term, and no Spellsurge term.

        [TestMethod]
        public void Sundermark_ZeroGear_ReproducesTheUngearedChanceExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(SundermarkAbility.Chance(rank, 0.05, 0.05, 0.03, 0.20),
                                SundermarkAbility.Chance(rank, 0.05, 0.05, 0.03, 0.20, 0.0),
                                0.0f, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(0.15f, SundermarkAbility.Chance(3, 0.05, 0.05, 0.0, 0.20, 0.0), 1e-6f);
        }

        [TestMethod]
        public void Sundermark_Gear_IsAFurtherAdditiveSummandAndRidesTheRankLadder()
        {
            var gear = Max(EquipmentModId.Sundermark); // 0.015

            Assert.AreEqual(0.065f, SundermarkAbility.Chance(1, 0.05, 0.05, 0.0, 0.20, gear), 1e-6f);
            Assert.AreEqual(0.165f, SundermarkAbility.Chance(3, 0.05, 0.05, 0.0, 0.20, gear), 1e-6f);

            // CONTROL - a separate multiplier would be 0.15 * 1.015 = 0.15225
            Assert.AreNotEqual(0.15 * (1.0 + gear), (double)SundermarkAbility.Chance(3, 0.05, 0.05, 0.0, 0.20, gear), 1e-4,
                "the gear term must be a summand on the chance, not a factor on it");
        }

        [TestMethod]
        public void Sundermark_Gear_StillAppliesWhenTheAffinityRiderIsFullyCapped()
        {
            var gear = Max(EquipmentModId.Sundermark);
            var cap = D("class_ability_affinity_chance_cap");

            Assert.IsTrue(SundermarkAbility.Chance(3, 0.05, 0.05, SaturatingRider) >= 1.0f, "control: uncapped saturates");

            var cappedNoGear = SundermarkAbility.Chance(3, 0.05, 0.05, SaturatingRider, cap);
            var cappedWithGear = SundermarkAbility.Chance(3, 0.05, 0.05, SaturatingRider, cap, gear);

            Assert.AreEqual(0.35f, cappedNoGear, 1e-6f);
            Assert.AreEqual(0.365f, cappedWithGear, 1e-6f);
            Assert.AreEqual(gear, cappedWithGear - cappedNoGear, 1e-6,
                "a fully capped rider must not eat one point of the gear term");
        }

        [TestMethod]
        public void Sundermark_RankZeroReturnsNothingHoweverMuchGearIsWorn()
        {
            Assert.AreEqual(0.0f, SundermarkAbility.Chance(0, 0.05, 0.05, 0.03, 0.20, 0.015));
        }

        // ---- 44: Spellstorm -> SpellstormAbility.Chance ----------------------------------------------
        //
        // The only war proc with no affinity rider at all, so there is no cap for gear to sit outside of.

        [TestMethod]
        public void Spellstorm_ZeroGear_ReproducesTheUngearedChanceExactly()
        {
            for (var rank = 0; rank <= 1; rank++)
            {
                Assert.AreEqual(SpellstormAbility.Chance(rank, 0.10, 0.08),
                                SpellstormAbility.Chance(rank, 0.10, 0.08, 0.0),
                                0.0f, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(0.10f, SpellstormAbility.Chance(1, 0.10, 0.0, 0.0), 1e-6f);
        }

        [TestMethod]
        public void Spellstorm_Gear_IsAFurtherAdditiveSummandAlongsideSpellsurge()
        {
            var gear = Max(EquipmentModId.Spellstorm); // 0.033

            Assert.AreEqual(0.133f, SpellstormAbility.Chance(1, 0.10, 0.0, gear), 1e-6f);

            // with a full Spellsurge ramp all three terms add: 0.10 + 0.10 + 0.033
            Assert.AreEqual(0.233f, SpellstormAbility.Chance(1, 0.10, 0.10, gear), 1e-6f);

            // CONTROL - a separate multiplier would be 0.20 * 1.033 = 0.2066
            Assert.AreNotEqual(0.20 * (1.0 + gear), (double)SpellstormAbility.Chance(1, 0.10, 0.10, gear), 1e-4,
                "the gear term must be a summand on the chance, not a factor on it");
        }

        [TestMethod]
        public void Spellstorm_RankZeroReturnsNothingHoweverMuchGearIsWorn()
        {
            Assert.AreEqual(0.0f, SpellstormAbility.Chance(0, 0.10, 0.08, 0.033));
        }

        // ---- 45: Cascade -> CascadeAbility.CascadeChance ---------------------------------------------

        [TestMethod]
        public void Cascade_ZeroGear_ReproducesTheUngearedChainChanceExactly()
        {
            for (var rank = 0; rank <= 1; rank++)
            {
                Assert.AreEqual(CascadeAbility.CascadeChance(rank, 0.25),
                                CascadeAbility.CascadeChance(rank, 0.25, 0.0),
                                0.0f, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(0.25f, CascadeAbility.CascadeChance(1, 0.25, 0.0), 1e-6f);
        }

        [TestMethod]
        public void Cascade_Gear_AddsToTheChainChance()
        {
            var gear = Max(EquipmentModId.Cascade); // 0.025

            Assert.AreEqual(0.275f, CascadeAbility.CascadeChance(1, 0.25, gear), 1e-6f);

            // CONTROL - a separate multiplier would be 0.25 * 1.025 = 0.25625
            Assert.AreNotEqual(0.25 * (1.0 + gear), (double)CascadeAbility.CascadeChance(1, 0.25, gear), 1e-4,
                "the gear term must be a summand on the chain chance, not a factor on it");

            Assert.AreEqual(0.0f, CascadeAbility.CascadeChance(0, 0.25, gear), "no ability, no chain, whatever is equipped");
        }

        [TestMethod]
        public void Cascade_TheGenerationBudgetIsStructurallyUnmoddable()
        {
            // Not "the call site happens not to pass gear" - CanCascade has no parameter to pass it to. The
            // generation counter is the entire safety argument against unbounded chaining (DESIGN.md 2.3),
            // so the mod moves the CHANCE and never the number of hops.
            var canCascade = typeof(CascadeAbility).GetMethod(nameof(CascadeAbility.CanCascade));

            Assert.IsNotNull(canCascade);

            var guardParameters = canCascade.GetParameters();

            Assert.AreEqual(3, guardParameters.Length,
                "CanCascade must stay (isClassAbilityProc, generation, maxGeneration) - a gear term here would unbound the chain");
            Assert.AreEqual(typeof(long), guardParameters[2].ParameterType, "the budget is an integer count, not a magnitude");
            Assert.IsFalse(guardParameters[2].IsOptional, "and it is required, so no call site can silently omit it");

            // control: the CHANCE half is where the mod landed, and its last parameter IS an optional double
            var chanceParameters = typeof(CascadeAbility).GetMethod(nameof(CascadeAbility.CascadeChance)).GetParameters();

            Assert.AreEqual(typeof(double), chanceParameters[chanceParameters.Length - 1].ParameterType);
            Assert.IsTrue(chanceParameters[chanceParameters.Length - 1].IsOptional);
        }

        // ---- 46: Dispelling Edge -> DispellingEdgeAbility.Chance -------------------------------------

        [TestMethod]
        public void DispellingEdge_ZeroGear_ReproducesTheUngearedChanceExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(DispellingEdgeAbility.Chance(rank, 0.10, 0.05, 0.05),
                                DispellingEdgeAbility.Chance(rank, 0.10, 0.05, 0.05, 0.0),
                                0.0f, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(0.20f, DispellingEdgeAbility.Chance(3, 0.10, 0.05, 0.0, 0.0), 1e-6f);
        }

        [TestMethod]
        public void DispellingEdge_Gear_IsAThirdAdditiveSummand()
        {
            var gear = Max(EquipmentModId.DispellingEdge); // 0.02

            Assert.AreEqual(0.22f, DispellingEdgeAbility.Chance(3, 0.10, 0.05, 0.0, gear), 1e-6f);

            // all three terms add: rank ladder 0.20 + Arcane Lore rider 0.05 + gear 0.02
            Assert.AreEqual(0.27f, DispellingEdgeAbility.Chance(3, 0.10, 0.05, 0.05, gear), 1e-6f);

            // CONTROL - a separate multiplier would be 0.25 * 1.02 = 0.255
            Assert.AreNotEqual(0.25 * (1.0 + gear), (double)DispellingEdgeAbility.Chance(3, 0.10, 0.05, 0.05, gear), 1e-4,
                "the gear term must be a summand on the chance, not a factor on it");

            Assert.AreEqual(0.0f, DispellingEdgeAbility.Chance(0, 0.10, 0.05, 0.05, gear));
        }

        // ---- 40: Harmonics -> ResonanceAbility.DamageMultiplier --------------------------------------
        //
        // Shape: 1.0 + stacks * (perStack + gear)

        [TestMethod]
        public void Harmonics_ZeroGear_ReproducesTheUngearedRampExactly()
        {
            for (var stacks = 0; stacks <= 5; stacks++)
            {
                Assert.AreEqual(ResonanceAbility.DamageMultiplier(3, stacks, 0.02, 0.03, 0.04),
                                ResonanceAbility.DamageMultiplier(3, stacks, 0.02, 0.03, 0.04, 0.0),
                                0.0f, $"{stacks} stacks: an explicit 0.0 gear term must be bit-identical");
            }

            // and still the shipped numbers
            Assert.AreEqual(1.10f, ResonanceAbility.DamageMultiplier(1, 5, 0.02, 0.03, 0.04, 0.0), 1e-6f);
            Assert.AreEqual(1.20f, ResonanceAbility.DamageMultiplier(3, 5, 0.02, 0.03, 0.04, 0.0), 1e-6f);
        }

        [TestMethod]
        public void Harmonics_Gear_RidesThePerStackRateInsideTheRamp()
        {
            var gear = Max(EquipmentModId.Harmonics); // 0.0048

            // 1 + 5 * (0.04 + 0.0048) = 1.224
            Assert.AreEqual(1.224f, ResonanceAbility.DamageMultiplier(3, 5, 0.02, 0.03, 0.04, gear), 1e-6f);

            // CONTROL - a separate multiplier would be 1.20 * (1 + 5 * 0.0048) = 1.2288, a DIFFERENT number
            Assert.AreNotEqual(1.20 * (1.0 + 5 * gear), (double)ResonanceAbility.DamageMultiplier(3, 5, 0.02, 0.03, 0.04, gear), 1e-4,
                "the gear term must be INSIDE the per-stack parenthesis, not a factor on the ramp");

            // it scales with the pool, exactly as the ability's own per-stack value does
            Assert.AreEqual((float)(1.0 + 1 * (0.04 + gear)), ResonanceAbility.DamageMultiplier(3, 1, 0.02, 0.03, 0.04, gear), 1e-6f);
        }

        [TestMethod]
        public void Harmonics_PaysOutAloneIfTheAbilityTunableIsZeroed_ButNeverWithoutTheAbility()
        {
            var gear = Max(EquipmentModId.Harmonics);

            // the "no bonus" test is on the SUM, so gear still pays when the ability's own tunable is 0
            Assert.AreEqual((float)(1.0 + 5 * gear), ResonanceAbility.DamageMultiplier(3, 5, 0.0, 0.0, 0.0, gear), 1e-6f);

            // an empty pool has nothing to multiply, gear or not
            Assert.AreEqual(1.0f, ResonanceAbility.DamageMultiplier(3, 0, 0.02, 0.03, 0.04, gear), 1e-9f);

            // and the machinery gate: rank 0 is inert however much is equipped
            Assert.AreEqual(1.0f, ResonanceAbility.DamageMultiplier(0, 5, 0.02, 0.03, 0.04, gear), 1e-9f);
        }

        // ---- 43: Surge -> SpellsurgeAbility.ProcChanceBonus ------------------------------------------
        //
        // Shape: stacks * (perStack + gear). A DELEGATED injection point - see the delegation test below.

        [TestMethod]
        public void Surge_ZeroGear_ReproducesTheUngearedBonusExactly()
        {
            for (var stacks = 0; stacks <= 5; stacks++)
            {
                Assert.AreEqual(SpellsurgeAbility.ProcChanceBonus(1, stacks, 0.02),
                                SpellsurgeAbility.ProcChanceBonus(1, stacks, 0.02, 0.0),
                                0.0f, $"{stacks} stacks: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(0.10f, SpellsurgeAbility.ProcChanceBonus(1, 5, 0.02, 0.0), 1e-6f);
        }

        [TestMethod]
        public void Surge_Gear_RidesThePerStackRate()
        {
            var gear = Max(EquipmentModId.Surge); // 0.0051

            // 5 * (0.02 + 0.0051) = 0.1255
            Assert.AreEqual(0.1255f, SpellsurgeAbility.ProcChanceBonus(1, 5, 0.02, gear), 1e-6f);

            // CONTROL - a separate multiplier would be 0.10 * (1 + 5 * 0.0051) = 0.10255
            Assert.AreNotEqual(0.10 * (1.0 + 5 * gear), (double)SpellsurgeAbility.ProcChanceBonus(1, 5, 0.02, gear), 1e-4,
                "the gear term must be INSIDE the per-stack parenthesis, not a factor on the ramp");

            // empty pool, and no ability, are both inert
            Assert.AreEqual(0.0f, SpellsurgeAbility.ProcChanceBonus(1, 0, 0.02, gear));
            Assert.AreEqual(0.0f, SpellsurgeAbility.ProcChanceBonus(0, 5, 0.02, gear));
        }

        [TestMethod]
        public void Surge_ReachesAWarProcExactlyOnce_AndDoesNotDisplaceThatProcsOwnMod()
        {
            // THE DELEGATION RULE (DESIGN.md 2.4). Player.GetSpellsurgeProcChanceBonus is the single read of
            // the Surge mod, and its result is handed to all three war procs as their spellsurgeBonus
            // argument. This test walks that composition by hand: one Surge term arrives inside the bonus,
            // and Spellblade's OWN mod arrives separately as the gear argument, so the two never collide.
            var surge = Max(EquipmentModId.Surge);          // 0.0051 per stack
            var spellblade = Max(EquipmentModId.Spellblade); // 0.036 flat

            var surgeBonus = SpellsurgeAbility.ProcChanceBonus(1, 5, 0.02, surge); // 0.1255

            var chance = SpellbladeAbility.Chance(3, 0.20, 0.0, surgeBonus, 0.20, spellblade);

            // 0.20 base + 0 rider + 0.1255 Surge-inflated Spellsurge ramp + 0.036 Spellblade
            Assert.AreEqual(0.3615f, chance, 1e-6f);

            // control: dropping Surge costs exactly the 5-stack Surge delta and nothing else, which is what
            // "the delegate's gear term is forwarded once" means numerically
            var withoutSurge = SpellbladeAbility.Chance(3, 0.20, 0.0,
                SpellsurgeAbility.ProcChanceBonus(1, 5, 0.02), 0.20, spellblade);

            Assert.AreEqual(5 * surge, chance - withoutSurge, 1e-6,
                "Surge must reach the war proc exactly once, at its per-stack rate times the held stacks");

            // control: dropping Spellblade's own mod costs exactly its magnitude, so the two mods are
            // independent summands rather than one being folded into the other
            var withoutSpellblade = SpellbladeAbility.Chance(3, 0.20, 0.0, surgeBonus, 0.20);
            Assert.AreEqual(spellblade, chance - withoutSpellblade, 1e-6,
                "wiring Surge must not displace or double-count Spellblade's own row");
        }

        // ---- 47: Provoke -> TauntAbility.EffectiveDuration -------------------------------------------
        //
        // Shape: baseDuration + clamp(loyaltyRider) + gear. The gear term is OUTSIDE the Loyalty clamp.

        [TestMethod]
        public void Provoke_ZeroGear_ReproducesTheUngearedDurationExactly()
        {
            foreach (var rider in new[] { 0.0, 4.0, 25.0 })
            {
                Assert.AreEqual(TauntAbility.EffectiveDuration(10.0, rider, 10.0),
                                TauntAbility.EffectiveDuration(10.0, rider, 10.0, 0.0),
                                0.0, $"rider {rider}: an explicit 0.0 gear term must be bit-identical");
            }

            // and still the shipped numbers
            Assert.AreEqual(10.0, TauntAbility.EffectiveDuration(10.0, 0.0, 10.0, 0.0), 1e-9);
            Assert.AreEqual(20.0, TauntAbility.EffectiveDuration(10.0, 25.0, 10.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void Provoke_Gear_IsAFurtherSummandOnTheDuration()
        {
            var gear = Max(EquipmentModId.Provoke); // 2.0 seconds

            Assert.AreEqual(12.0, TauntAbility.EffectiveDuration(10.0, 0.0, 10.0, gear), 1e-9);
            Assert.AreEqual(16.0, TauntAbility.EffectiveDuration(10.0, 4.0, 10.0, gear), 1e-9);

            // CONTROL - a separate multiplier of the same size would be 14.0 * 1.2 = 16.8 seconds. The mod is
            // 2 SECONDS, not 2 percent of anything.
            Assert.AreNotEqual(14.0 * 1.2, TauntAbility.EffectiveDuration(10.0, 4.0, 10.0, gear), 1e-4,
                "Provoke adds seconds to the hold; it does not scale it");
        }

        [TestMethod]
        public void Provoke_Gear_StillAppliesWhenTheLoyaltyRiderIsFullyCapped()
        {
            var gear = Max(EquipmentModId.Provoke);

            // control: the rider really is saturating - 25 seconds of raw bonus against a 10 second cap
            Assert.AreEqual(35.0, TauntAbility.EffectiveDuration(10.0, 25.0, 25.0, 0.0), 1e-9,
                "control: with the cap raised out of the way the rider alone is worth 25 seconds");

            var cappedNoGear = TauntAbility.EffectiveDuration(10.0, 25.0, 10.0);
            var cappedWithGear = TauntAbility.EffectiveDuration(10.0, 25.0, 10.0, gear);

            Assert.AreEqual(20.0, cappedNoGear, 1e-9, "base 10 plus the rider clamped to 10");

            // THE REGRESSION: only the RIDER is clamped. Feeding gear into the clamp would have returned
            // 20.0 here and the mod would be worth exactly nothing to any developed Vanguard.
            Assert.AreEqual(22.0, cappedWithGear, 1e-9);
            Assert.AreEqual(gear, cappedWithGear - cappedNoGear, 1e-9,
                "a fully capped Loyalty rider must not eat one second of the gear term");
        }

        // ---- 48: Bellow -> the Taunt radius ----------------------------------------------------------
        //
        // No pure helper carries this one: the gear term is an inline addition to the radius read in
        // TauntAbility.Activate, which needs a live player and landblock. What is asserted here is the unit
        // and the bound, since a radius mod priced in the wrong unit is the failure mode that matters.

        [TestMethod]
        public void Bellow_WidensTheTauntRadiusInMetres_AndIsBoundedByItsRegistryMaximum()
        {
            var baseRadius = D("class_ability_taunt_radius"); // 15.0
            var gear = Max(EquipmentModId.Bellow);            // 1.0 metre

            Assert.AreEqual(15.0, baseRadius, 1e-12);
            Assert.AreEqual(16.0, baseRadius + gear, 1e-12, "one perfect roll is worth one metre, not one percent");

            // CONTROL - read as a percentage the same number would be 15.0 * 2.0 = 30 metres, doubling the
            // reach of the catalog's largest outlier's attacker count. This is the unit confusion the
            // registry row's DisplayScale of 1.0 encodes.
            Assert.AreNotEqual(baseRadius * (1.0 + gear), baseRadius + gear, 1e-4,
                "Bellow adds metres to the radius; it is not a percentage of it");
        }

        // ---- 49: Shield Wall -> ShieldBlockAbility.BlockChance ---------------------------------------
        //
        // Shape: baseChance + (rank-1)*step + clamp(armorTinkRider) + gear. The gear term is OUTSIDE the
        // clamp, and Shield Block is the one ability in this family sitting under a SECOND clamp as well -
        // the pooled Shield Block + Parry avoidance cap.

        [TestMethod]
        public void ShieldWall_ZeroGearAndUncapped_ReproducesTheUngearedBlockChanceExactly()
        {
            foreach (var rider in new[] { 0.0, 0.08, SaturatingRider })
            {
                for (var rank = 0; rank <= 3; rank++)
                {
                    Assert.AreEqual(ShieldBlockAbility.BlockChance(rank, 0.08, 0.06, rider),
                                    ShieldBlockAbility.BlockChance(rank, 0.08, 0.06, rider, 0.0, 0.0),
                                    0.0, $"rank {rank}, rider {rider}: explicit 0.0 cap and gear must be bit-identical");
                }
            }

            // and still the shipped ladder
            Assert.AreEqual(0.08, ShieldBlockAbility.BlockChance(1, 0.08, 0.06, 0.0, 0.0, 0.0), 1e-9);
            Assert.AreEqual(0.14, ShieldBlockAbility.BlockChance(2, 0.08, 0.06, 0.0, 0.0, 0.0), 1e-9);
            Assert.AreEqual(0.20, ShieldBlockAbility.BlockChance(3, 0.08, 0.06, 0.0, 0.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void ShieldBlock_AffinityCap_ClampsTheRiderAndNothingElse()
        {
            var cap = D("class_ability_affinity_chance_cap"); // 0.20
            var gear = Max(EquipmentModId.ShieldWall);        // 0.0141

            // control: uncapped, the saturating rider alone is worth +209pp on top of the rank ladder
            Assert.AreEqual(0.20 + SaturatingRider, ShieldBlockAbility.BlockChance(3, 0.08, 0.06, SaturatingRider), 1e-9,
                "control: with no cap the rider is unbounded, which is the defect this clamp exists for");

            // the rank ladder survives the clamp untouched: 0.20 base ladder + 0.20 capped rider
            Assert.AreEqual(0.40, ShieldBlockAbility.BlockChance(3, 0.08, 0.06, SaturatingRider, cap), 1e-9,
                "the clamp must bound the rider only, never the rank ladder");

            // and so does the gear term: 0.20 ladder + 0.20 capped rider + 0.0141 gear
            Assert.AreEqual(0.4141, ShieldBlockAbility.BlockChance(3, 0.08, 0.06, SaturatingRider, cap, gear), 1e-9,
                "the clamp must bound the rider only, never the gear term");

            // a rider UNDER the cap is passed through unchanged - the clamp is a ceiling, not a floor
            Assert.AreEqual(0.28, ShieldBlockAbility.BlockChance(3, 0.08, 0.06, 0.08, cap), 1e-9);
        }

        [TestMethod]
        public void ShieldWall_Gear_StillAppliesWhenTheArmorTinkeringRiderIsFullyCapped()
        {
            // THE REGRESSION this whole placement exists to prevent (DESIGN.md 2.2/2.4). Folding the gear
            // term inside the affinity clamp would let a saturating Armor Tinkering rider silently eat the
            // mod - the Resonance failure - and Shield Wall is the row where that would bite hardest,
            // because a Vanguard's Armor Tinkering is exactly the skill the class is built around.
            var gear = Max(EquipmentModId.ShieldWall);
            var cap = D("class_ability_affinity_chance_cap");

            var cappedNoGear = ShieldBlockAbility.BlockChance(3, 0.08, 0.06, SaturatingRider, cap);
            var cappedWithGear = ShieldBlockAbility.BlockChance(3, 0.08, 0.06, SaturatingRider, cap, gear);

            Assert.AreEqual(0.40, cappedNoGear, 1e-9, "the 20pp ladder plus the rider clamped to 20pp");

            Assert.AreEqual(gear, cappedWithGear - cappedNoGear, 1e-12,
                "a fully saturated Armor Tinkering rider must not eat one point of the gear term");

            // CONTROL - the axis rule. A separate multiplier would be 0.40 * 1.0141 = 0.405640, a DIFFERENT
            // number, so the assertion above can actually fail if the shape ever changes.
            Assert.AreNotEqual(0.40 * (1.0 + gear), cappedWithGear, 1e-6,
                "the gear term must be a summand on the block chance, not a factor on it");

            // and the machinery gate: rank 0 is inert however much is equipped
            Assert.AreEqual(0.0, ShieldBlockAbility.BlockChance(0, 0.08, 0.06, SaturatingRider, cap, gear), 1e-12);
        }

        [TestMethod]
        public void ShieldWall_AtFullStackCap_LeavesHeadroomUnderThePooledAvoidanceCap()
        {
            // The SECOND clamp. Shield Block is unique in this family: even with its own affinity rider
            // capped, the result is fed to ClassAbilityAvoidance.Pooled against class_ability_avoidance_cap.
            // The mod is only worth anything if a max-rank Vanguard running a full stack of it still sits
            // UNDER that pooled cap - otherwise the pooled scale-down absorbs exactly what the affinity
            // clamp just made room for.
            //
            // A Vanguard has no Parry (it is a Rogue ability), so the pool is block alone.
            var gear = Max(EquipmentModId.ShieldWall);              // 0.0141 per perfect roll
            var stackCap = EquipmentModRegistry.Get(EquipmentModId.ShieldWall).StackCap; // 3.0
            var affinityCap = D("class_ability_affinity_chance_cap"); // 0.20
            var avoidanceCap = D("class_ability_avoidance_cap");      // 0.50

            Assert.AreEqual(3.0, stackCap, 1e-12, "three perfect rolls is the ratified per-type stack cap");

            var stackedGear = gear * stackCap; // 0.0423

            var block = ShieldBlockAbility.BlockChance(3, 0.08, 0.06, SaturatingRider, affinityCap, stackedGear);

            Assert.AreEqual(0.4423, block, 1e-9, "20pp ladder + 20pp capped rider + 4.23pp of Shield Wall");

            var (pooledBlock, pooledParry, capped) = ClassAbilityAvoidance.Pooled(block, 0.0, avoidanceCap);

            Assert.IsFalse(capped, "a full 3.0 stack on a capped rider must still fit under the 50pp pooled cap");
            Assert.AreEqual(block, pooledBlock, 1e-12, "nothing was scaled away, so the mod is live in combat");
            Assert.AreEqual(0.0, pooledParry, 1e-12);

            // CONTROL - the pooled cap is genuinely reachable, so "IsFalse(capped)" above is a real result
            // rather than a cap that never fires. This is the pre-fix state: an UNCAPPED rider.
            var uncapped = ShieldBlockAbility.BlockChance(3, 0.08, 0.06, SaturatingRider, 0.0, stackedGear);
            var (_, _, cappedControl) = ClassAbilityAvoidance.Pooled(uncapped, 0.0, avoidanceCap);

            Assert.IsTrue(cappedControl, "control: without the affinity clamp the rider alone saturates the pooled cap");
        }

        // ---- Dispelling Edge's affinity cap ----------------------------------------------------------
        //
        // Row 46's gear term shipped before its ability had a cap at all. The cap landed afterwards, so the
        // parameter order here is gear-then-cap (matching AcidProcAbility) rather than cap-then-gear.

        [TestMethod]
        public void DispellingEdge_ZeroCap_ReproducesTheUncappedChanceExactly()
        {
            var gear = Max(EquipmentModId.DispellingEdge);

            foreach (var rider in new[] { 0.0, 0.05, SaturatingRider })
            {
                for (var rank = 0; rank <= 3; rank++)
                {
                    Assert.AreEqual(DispellingEdgeAbility.Chance(rank, 0.10, 0.05, rider, gear),
                                    DispellingEdgeAbility.Chance(rank, 0.10, 0.05, rider, gear, 0.0),
                                    0.0f, $"rank {rank}, rider {rider}: an explicit 0.0 cap must be bit-identical");
                }
            }
        }

        [TestMethod]
        public void DispellingEdge_AffinityCap_ClampsTheRiderAndNothingElse()
        {
            var cap = D("class_ability_affinity_chance_cap"); // 0.20
            var gear = Max(EquipmentModId.DispellingEdge);    // 0.02

            // control: uncapped, the saturating rider alone drives the strip chance past certainty
            Assert.IsTrue(DispellingEdgeAbility.Chance(3, 0.10, 0.05, SaturatingRider) > 1.0f,
                "control: with no cap the Arcane Lore rider strips an enchantment on literally every hit");

            // the rank ladder survives: 0.20 ladder + 0.20 capped rider
            Assert.AreEqual(0.40f, DispellingEdgeAbility.Chance(3, 0.10, 0.05, SaturatingRider, 0.0, cap), 1e-6f,
                "the clamp must bound the rider only, never the rank ladder");

            // and so does the gear term
            Assert.AreEqual(0.42f, DispellingEdgeAbility.Chance(3, 0.10, 0.05, SaturatingRider, gear, cap), 1e-6f,
                "the clamp must bound the rider only, never the gear term");
        }

        [TestMethod]
        public void DispellingEdge_Gear_StillAppliesWhenTheArcaneLoreRiderIsFullyCapped()
        {
            var gear = Max(EquipmentModId.DispellingEdge);
            var cap = D("class_ability_affinity_chance_cap");

            var cappedNoGear = DispellingEdgeAbility.Chance(3, 0.10, 0.05, SaturatingRider, 0.0, cap);
            var cappedWithGear = DispellingEdgeAbility.Chance(3, 0.10, 0.05, SaturatingRider, gear, cap);

            Assert.AreEqual(gear, cappedWithGear - cappedNoGear, 1e-6,
                "a fully capped Arcane Lore rider must not eat one point of the gear term");
        }

        // ---- the shared contract ---------------------------------------------------------------------

        [TestMethod]
        public void EveryGearParameter_IsLastAndDefaultsToZero()
        {
            // This is what makes the whole feature provably inert when nothing is equipped: a caller that
            // predates the mods still compiles and still gets the old number. Asserted structurally rather
            // than by inspection, because a later edit could reorder a parameter list without any test
            // noticing - the arithmetic assertions above all pass gear explicitly.
            AssertLastParameterIsOptionalAndZero(typeof(SpellbladeAbility), nameof(SpellbladeAbility.Chance));
            AssertLastParameterIsOptionalAndZero(typeof(ResonanceAbility), nameof(ResonanceAbility.DamageMultiplier));
            AssertLastParameterIsOptionalAndZero(typeof(RunebladeAbility), nameof(RunebladeAbility.Chance));
            AssertLastParameterIsOptionalAndZero(typeof(SundermarkAbility), nameof(SundermarkAbility.Chance));
            AssertLastParameterIsOptionalAndZero(typeof(SpellsurgeAbility), nameof(SpellsurgeAbility.ProcChanceBonus));
            AssertLastParameterIsOptionalAndZero(typeof(SpellstormAbility), nameof(SpellstormAbility.Chance));
            AssertLastParameterIsOptionalAndZero(typeof(CascadeAbility), nameof(CascadeAbility.CascadeChance));
            AssertLastParameterIsOptionalAndZero(typeof(TauntAbility), nameof(TauntAbility.EffectiveDuration));
            AssertLastParameterIsOptionalAndZero(typeof(ShieldBlockAbility), nameof(ShieldBlockAbility.BlockChance));

            // DispellingEdgeAbility.Chance is the one exception to "gear is last", and deliberately so: its
            // gear parameter shipped BEFORE its affinity cap did, so the cap had to be appended after it or
            // every existing positional call site would have silently changed meaning. Both trailing
            // parameters are still optional and still default to 0, which is what the inert-when-off
            // contract actually requires - so this asserts the gear parameter BY NAME rather than by
            // position, and pins the order so it cannot be quietly swapped back.
            AssertOptionalDoubleParameterDefaultsToZero(typeof(DispellingEdgeAbility), nameof(DispellingEdgeAbility.Chance), "gearModChance");
            AssertOptionalDoubleParameterDefaultsToZero(typeof(DispellingEdgeAbility), nameof(DispellingEdgeAbility.Chance), "affinityCap");

            var dispelParameters = typeof(DispellingEdgeAbility).GetMethod(nameof(DispellingEdgeAbility.Chance)).GetParameters();

            Assert.AreEqual("gearModChance", dispelParameters[dispelParameters.Length - 2].Name);
            Assert.AreEqual("affinityCap", dispelParameters[dispelParameters.Length - 1].Name);
        }

        [TestMethod]
        public void EveryOneOfTheseRowsIsMachineryAndLinksToARealAbility()
        {
            // All eleven are machinery (DESIGN.md 2.3): every linked ability returns 0 or the identity value at
            // rank 0, so none of them has a rank-0 formula for a standalone term to stand alone in. A row
            // flipped to Standalone would need a rank-0 mirror in Player_EquipmentMods.cs, and there is none.
            var ids = new[]
            {
                EquipmentModId.Spellblade, EquipmentModId.Harmonics, EquipmentModId.Runeblade,
                EquipmentModId.Sundermark, EquipmentModId.Surge, EquipmentModId.Spellstorm,
                EquipmentModId.Cascade, EquipmentModId.DispellingEdge,
                EquipmentModId.Provoke, EquipmentModId.Bellow, EquipmentModId.ShieldWall,
            };

            foreach (var id in ids)
            {
                var definition = EquipmentModRegistry.Get(id);

                Assert.IsNotNull(definition, $"{id} is not in the registry");
                Assert.IsFalse(definition.Standalone, $"{id} must be machinery");
                Assert.AreEqual(EquipmentModHookKind.AbilityMachinery, definition.HookKind, $"{id} hook kind");
            }
        }

        [TestMethod]
        public void EverySpellswordProcChanceRowCarriesTheProcStackCap()
        {
            // DESIGN.md 2.6: proc-chance rows ship with StackCap 3.0 so an uncapped cross-item stack cannot
            // walk a chance-on-hit ability toward a guarantee. Provoke and Bellow are a duration and a radius
            // rather than chances, so they are deliberately NOT in this list.
            var procRows = new[]
            {
                EquipmentModId.Spellblade, EquipmentModId.Runeblade, EquipmentModId.Sundermark,
                EquipmentModId.Surge, EquipmentModId.Spellstorm, EquipmentModId.Cascade,
                EquipmentModId.DispellingEdge,
            };

            foreach (var id in procRows)
                Assert.AreEqual(3.0, EquipmentModRegistry.Get(id).StackCap, 1e-12, $"{id} must carry the proc StackCap");

            // control: the two non-chance rows and the per-stack damage row leave it uncapped, so the
            // assertion above is a real distinction rather than a property every row happens to have
            Assert.AreEqual(0.0, EquipmentModRegistry.Get(EquipmentModId.Harmonics).StackCap, 1e-12);
            Assert.AreEqual(0.0, EquipmentModRegistry.Get(EquipmentModId.Provoke).StackCap, 1e-12);
            Assert.AreEqual(0.0, EquipmentModRegistry.Get(EquipmentModId.Bellow).StackCap, 1e-12);
        }

        private static void AssertLastParameterIsOptionalAndZero(Type type, string methodName)
        {
            var method = type.GetMethod(methodName);

            Assert.IsNotNull(method, $"{type.Name}.{methodName} not found");

            var parameters = method.GetParameters();
            var last = parameters[parameters.Length - 1];

            Assert.AreEqual(typeof(double), last.ParameterType, $"{type.Name}.{methodName}: the gear parameter must be a double");
            Assert.IsTrue(last.IsOptional, $"{type.Name}.{methodName}: the gear parameter must be optional");
            Assert.AreEqual(0.0, (double)last.DefaultValue, 0.0, $"{type.Name}.{methodName}: the gear parameter must default to 0.0");
        }

        private static void AssertOptionalDoubleParameterDefaultsToZero(Type type, string methodName, string parameterName)
        {
            var method = type.GetMethod(methodName);

            Assert.IsNotNull(method, $"{type.Name}.{methodName} not found");

            var parameter = Array.Find(method.GetParameters(), p => p.Name == parameterName);

            Assert.IsNotNull(parameter, $"{type.Name}.{methodName}: no parameter named '{parameterName}'");
            Assert.AreEqual(typeof(double), parameter.ParameterType, $"{type.Name}.{methodName}.{parameterName} must be a double");
            Assert.IsTrue(parameter.IsOptional, $"{type.Name}.{methodName}.{parameterName} must be optional");
            Assert.AreEqual(0.0, (double)parameter.DefaultValue, 0.0, $"{type.Name}.{methodName}.{parameterName} must default to 0.0");
        }
    }
}
