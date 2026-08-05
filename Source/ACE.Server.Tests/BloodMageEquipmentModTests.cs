using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The GEAR TERM on the eight Blood Mage ability helpers (equipment mods 31-38: Blood Charge,
    /// Transfusion, Hemorrhage, Deepen, Bloodletting, Sanguinate, Blood Price, Clotting).
    ///
    /// Every test here pins one of the two invariants that govern the whole feature:
    ///
    ///  1. THE AXIS RULE (DESIGN.md 3.3). A gear term is added INSIDE its ability's own parenthesis, on the
    ///     same additive axis as the ability's own bonus - never as a separate multiplier. Each mod's test
    ///     therefore carries a CONTROL asserting the result is NOT what the multiplicative shape would have
    ///     produced, because "bigger than the ungeared value" is true of both shapes and proves nothing.
    ///  2. INERT WHEN OFF. Every helper takes its gear parameter LAST and DEFAULTED TO 0, so passing 0.0
    ///     explicitly must reproduce the no-gear overload's value EXACTLY (bit-for-bit, asserted at 0.0
    ///     tolerance where the arithmetic is exact), and every pre-existing test keeps compiling untouched.
    ///
    /// All eight helpers are pure statics, so nothing here needs a live Player - which is the reason the
    /// arithmetic lives in them at all (Player's static initializer cannot run under this test host).
    ///
    /// NOT COVERED HERE, and deliberately:
    ///  - BLOODLETTING has no pure helper. Its gear term is an inline addition to the Crimson Harvest radius
    ///    read in WorldObject_Magic.TryCrimsonHarvest, which needs a live caster and landblock. What IS
    ///    testable - that a wider radius selects more targets, nearest-first - is CrimsonHarvestMath
    ///    .SelectSecondaryTargets, already covered by BloodMageMechanicsTests; the radius-mod test below only
    ///    pins that a widened radius reaches strictly more of the same candidate set.
    ///  - the /abilities readouts. Six of the eight need a live Player (GetEquippedModValue), so the
    ///    null-Player assertions in BloodMageReadoutTests are the whole of what this host can check.
    /// </summary>
    [TestClass]
    public class BloodMageEquipmentModTests
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

        [TestMethod]
        public void RegistryMagnitudes_AreWhatTheseTestsAssume()
        {
            Assert.AreEqual(0.0054, Max(EquipmentModId.BloodCharge), 1e-12, "per-charge life damage");
            Assert.AreEqual(0.10, Max(EquipmentModId.Transfusion), 1e-12, "pp of Drain surplus");
            Assert.AreEqual(0.05, Max(EquipmentModId.Hemorrhage), 1e-12, "BARE MULTIPLIER, not a fraction");
            Assert.AreEqual(0.02, Max(EquipmentModId.Deepen), 1e-12, "pp of debuff intensity");
            Assert.AreEqual(1.0, Max(EquipmentModId.Bloodletting), 1e-12, "metres of harvest radius");
            Assert.AreEqual(0.117, Max(EquipmentModId.Sanguinate), 1e-12, "added to the burst multiplier");
            Assert.AreEqual(0.025, Max(EquipmentModId.BloodPrice), 1e-12, "spell damage while paying");
            Assert.AreEqual(0.10, Max(EquipmentModId.Clotting), 1e-12, "pp of the ward absorb");
        }

        // ---- 31: Blood Charge -> BloodChargeMath.DamageMultiplier ------------------------------------
        //
        // Shape: 1.0 + stacks * (perStack + gear)

        [TestMethod]
        public void BloodCharge_ZeroGear_ReproducesTheUngearedRampExactly()
        {
            for (var stacks = 0; stacks <= 5; stacks++)
            {
                Assert.AreEqual((double)BloodChargeMath.DamageMultiplier(stacks, 0.07),
                                (double)BloodChargeMath.DamageMultiplier(stacks, 0.07, 0.0),
                                0.0, $"{stacks} charges: an explicit 0.0 gear term must be bit-identical");
            }

            // and still the shipped numbers
            Assert.AreEqual(1.35, BloodChargeMath.DamageMultiplier(5, 0.07, 0.0), 1e-6);
        }

        [TestMethod]
        public void BloodCharge_Gear_RidesThePerChargeRateInsideTheRamp()
        {
            var gear = Max(EquipmentModId.BloodCharge); // 0.0054

            // 1 + 5 * (0.07 + 0.0054) = 1.377
            Assert.AreEqual(1.377, BloodChargeMath.DamageMultiplier(5, 0.07, gear), 1e-6);

            // CONTROL - the axis rule. A separate multiplier would be 1.35 * (1 + 5 * 0.0054) = 1.38645,
            // which is a DIFFERENT number, so this assertion can actually fail if the shape ever changes.
            var multiplicativeShape = 1.35 * (1.0 + 5 * gear);
            Assert.AreNotEqual(multiplicativeShape, (double)BloodChargeMath.DamageMultiplier(5, 0.07, gear), 1e-4,
                "the gear term must be INSIDE the per-charge parenthesis, not a factor on the ramp");

            // it scales with the pool, exactly as the ability's own per-charge value does
            Assert.AreEqual(1.0 + 1 * (0.07 + gear), BloodChargeMath.DamageMultiplier(1, 0.07, gear), 1e-6);
        }

        [TestMethod]
        public void BloodCharge_TheNoBonusTestIsOnTheSum_SoGearPaysOutAloneAndANegativeSumStillFloors()
        {
            // the ability's own tunable zeroed: gear is then the whole per-charge value
            Assert.AreEqual(1.027, BloodChargeMath.DamageMultiplier(5, 0.0, 0.0054), 1e-6);

            // an empty pool has nothing to multiply, gear or not
            Assert.AreEqual(1.0f, BloodChargeMath.DamageMultiplier(0, 0.07, 0.0054), 1e-9f);

            // a negative sum can never become a damage PENALTY
            Assert.AreEqual(1.0f, BloodChargeMath.DamageMultiplier(5, -0.10, 0.0054), 1e-9f);
        }

        // ---- 32: Transfusion -> DrainSurplusDistribution.ShareFraction -------------------------------
        //
        // Shape: rankFraction + healingRider + gear

        [TestMethod]
        public void Transfusion_ZeroGear_ReproducesTheUngearedShareExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(DrainSurplusDistribution.ShareFraction(rank, 0.40, 0.70, 1.00, 0.08),
                                DrainSurplusDistribution.ShareFraction(rank, 0.40, 0.70, 1.00, 0.08, 0.0),
                                0.0, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(1.00, DrainSurplusDistribution.ShareFraction(3, 0.40, 0.70, 1.00, 0.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void Transfusion_Gear_IsAThirdAdditiveSummandOnTheSameAxis()
        {
            var gear = Max(EquipmentModId.Transfusion); // 0.10

            // rank 3 alone: 1.00 + 0.10 = 1.10, and there is deliberately NO clamp at 1.0
            Assert.AreEqual(1.10, DrainSurplusDistribution.ShareFraction(3, 0.40, 0.70, 1.00, 0.0, gear), 1e-9);

            // with the Healing rider, all three add: 1.00 + 0.08 + 0.10
            Assert.AreEqual(1.18, DrainSurplusDistribution.ShareFraction(3, 0.40, 0.70, 1.00, 0.08, gear), 1e-9);

            // CONTROL - a multiplicative shape would be 1.08 * 1.10 = 1.188, a different number
            Assert.AreNotEqual(1.08 * (1.0 + gear),
                DrainSurplusDistribution.ShareFraction(3, 0.40, 0.70, 1.00, 0.08, gear), 1e-4,
                "gear must ADD to the share, not scale it");
        }

        [TestMethod]
        public void Transfusion_RankZeroReturnsNothingHoweverMuchGearIsWorn()
        {
            // the machinery gate: the rank test runs BEFORE the gear term is read, so a caster who does not
            // own Transfusion cannot cascade anything on the strength of an item
            Assert.AreEqual(0.0, DrainSurplusDistribution.ShareFraction(0, 0.40, 0.70, 1.00, 0.08, 0.10), 1e-12);
            Assert.AreEqual(0.0, DrainSurplusDistribution.ShareFraction(-1, 0.40, 0.70, 1.00, 0.08, 0.10), 1e-12);
        }

        // ---- 33: Hemorrhage -> WeakenedBloodMath.ResistanceMod ---------------------------------------
        //
        // Shape: rank table value + gear, in the BARE MULTIPLIER unit

        [TestMethod]
        public void Hemorrhage_ZeroGear_ReproducesTheUngearedMarkExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(WeakenedBloodMath.ResistanceMod(rank, 2.00, 2.50, 3.10),
                                WeakenedBloodMath.ResistanceMod(rank, 2.00, 2.50, 3.10, 0.0),
                                0.0, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }
        }

        [TestMethod]
        public void Hemorrhage_Gear_IsAddedInTheBareMultiplierUnit_NotAsAPercentage()
        {
            var gear = Max(EquipmentModId.Hemorrhage); // 0.05 ON the multiplier

            Assert.AreEqual(3.15, WeakenedBloodMath.ResistanceMod(3, 2.00, 2.50, 3.10, gear), 1e-9);
            Assert.AreEqual(2.05, WeakenedBloodMath.ResistanceMod(1, 2.00, 2.50, 3.10, gear), 1e-9);

            // CONTROL - read as a percentage the mod would be 3.10 * 1.05 = 3.255, which is 2.1x its
            // intended value. This is the unit confusion the registry row's DisplayScale of 1.0 encodes.
            Assert.AreNotEqual(3.10 * (1.0 + gear), WeakenedBloodMath.ResistanceMod(3, 2.00, 2.50, 3.10, gear), 1e-4,
                "Hemorrhage adds to a bare multiplier; it is not a percentage of it");
        }

        [TestMethod]
        public void Hemorrhage_RankZeroStaysNeutral_AndTheVulnerabilityFloorStillBinds()
        {
            // no mark without the ability, whatever is equipped
            Assert.AreEqual(1.0, WeakenedBloodMath.ResistanceMod(0, 2.00, 2.50, 3.10, 0.05), 1e-12);

            // a mis-set rank-1 tunable below the identity is still floored to 1.0 AFTER the gear term,
            // so this axis can never produce a RESISTANCE
            Assert.AreEqual(1.0, WeakenedBloodMath.ResistanceMod(1, 0.50, 2.50, 3.10, 0.05), 1e-12);
        }

        // ---- 34: Deepen -> MaledictionAbility.IntensityBonus -----------------------------------------
        //
        // Shape: base + step*(rank-1) + lifeMagicRider + gear

        [TestMethod]
        public void Deepen_ZeroGear_ReproducesTheUngearedIntensityExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(MaledictionAbility.IntensityBonus(rank, 0.10, 0.10, 0.08),
                                MaledictionAbility.IntensityBonus(rank, 0.10, 0.10, 0.08, 0.0),
                                0.0, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }

            Assert.AreEqual(0.30, MaledictionAbility.IntensityBonus(3, 0.10, 0.10, 0.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void Deepen_Gear_IsAFourthAdditiveSummand()
        {
            var gear = Max(EquipmentModId.Deepen); // 0.02

            // rank 3 alone: +30% -> +32%, the "+2pp takes 38 to 40" derivation in DESIGN.md 2.3 read
            // against the rank bonus rather than the rank-plus-rider total
            Assert.AreEqual(0.32, MaledictionAbility.IntensityBonus(3, 0.10, 0.10, 0.0, gear), 1e-9);

            // with the Life Magic rider all four terms add: 0.30 + 0.08 + 0.02
            Assert.AreEqual(0.40, MaledictionAbility.IntensityBonus(3, 0.10, 0.10, 0.08, gear), 1e-9);

            // CONTROL - a multiplicative shape would be 0.38 * 1.02 = 0.3876
            Assert.AreNotEqual(0.38 * (1.0 + gear), MaledictionAbility.IntensityBonus(3, 0.10, 0.10, 0.08, gear), 1e-4,
                "gear must ADD to the intensity, not scale it");
        }

        [TestMethod]
        public void Deepen_RankZeroReturnsNothingHoweverMuchGearIsWorn()
        {
            Assert.AreEqual(0.0, MaledictionAbility.IntensityBonus(0, 0.10, 0.10, 0.08, 0.02), 1e-12);
        }

        // ---- 35: Bloodletting -> the Crimson Harvest radius ------------------------------------------
        //
        // No pure helper carries this one: the gear term is an inline addition to the radius read in
        // WorldObject_Magic.TryCrimsonHarvest. What is testable is that widening the radius reaches strictly
        // more of the same candidate set while the TARGET COUNT still binds - the reason DESIGN.md moves the
        // radius rather than the integer cap.

        [TestMethod]
        public void Bloodletting_AWiderRadiusReachesFurtherButNeverRaisesTheTargetCap()
        {
            // four creatures fanned out from the primary; the 5th sits between the base and extended reach
            var distances = new[] { 3.0, 5.0, 7.0, 8.4, 8.9 };

            var baseRadius = D("class_ability_crimsonharvest_radius"); // 8.0
            var extended = baseRadius + Max(EquipmentModId.Bloodletting); // 9.0
            var maxTargets = (int)PropertyManager.GetLong("class_ability_crimsonharvest_max_targets").Item; // 4

            var atBase = CrimsonHarvestMath.SelectSecondaryTargets(distances, baseRadius, maxTargets);
            var atExtended = CrimsonHarvestMath.SelectSecondaryTargets(distances, extended, maxTargets);

            Assert.AreEqual(8.0, baseRadius, 1e-12);
            Assert.AreEqual(9.0, extended, 1e-12);
            Assert.AreEqual(4, maxTargets);

            // control: the two creatures past 8m are genuinely out of reach unmodded, so "more targets"
            // below is a real difference and not an artefact of the fixture
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, atBase, "unmodded, only the three inside 8m are struck");

            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, atExtended, "the radius mod pulls the 8.4m creature in");

            // AND THE CAP STILL BINDS - the 8.9m creature is inside the extended radius and is still not
            // struck, because the target COUNT deliberately takes no gear term
            Assert.AreEqual(maxTargets, atExtended.Length,
                "Bloodletting widens the reach; it must never raise the 4-target cap");
        }

        // ---- 36: Sanguinate -> ExsanguinateMath.BurstMultiplier --------------------------------------
        //
        // Shape: burstMultiplier + gear (and it forwards Blood Charge's per-charge gear through the ramp)

        [TestMethod]
        public void Sanguinate_ZeroGear_ReproducesTheUngearedBurstExactly()
        {
            for (var stacks = 0; stacks <= 5; stacks++)
            {
                Assert.AreEqual((double)ExsanguinateMath.BurstMultiplier(stacks, 0.07, 3.0),
                                (double)ExsanguinateMath.BurstMultiplier(stacks, 0.07, 3.0, 0.0, 0.0),
                                0.0, $"{stacks} charges: explicit 0.0 gear terms must be bit-identical");
            }

            Assert.AreEqual(2.05, ExsanguinateMath.BurstMultiplier(5, 0.07, 3.0, 0.0, 0.0), 1e-6);

            // the mis-set-tunable degrade is unchanged when no gear is worn
            Assert.AreEqual((double)BloodChargeMath.DamageMultiplier(4, 0.07),
                            (double)ExsanguinateMath.BurstMultiplier(4, 0.07, 0.5, 0.0, 0.0), 1e-6);
        }

        [TestMethod]
        public void Sanguinate_Gear_AddsToTheBurstMultiplierItself()
        {
            var gear = Max(EquipmentModId.Sanguinate); // 0.117

            // burst 3.0 + 0.117 = 3.117, so 1 + 5 * 0.07 * 3.117 = 2.09095
            Assert.AreEqual(2.09095, ExsanguinateMath.BurstMultiplier(5, 0.07, 3.0, gear), 1e-6);

            // CONTROL - scaling the finished burst would be 2.05 * 1.117 = 2.28985, more than 2x the
            // intended delta. The gear term belongs to the MULTIPLIER, not to the result.
            Assert.AreNotEqual(2.05 * (1.0 + gear), (double)ExsanguinateMath.BurstMultiplier(5, 0.07, 3.0, gear), 1e-4,
                "Sanguinate adds to the burst multiplier, not to the burst");
        }

        [TestMethod]
        public void Sanguinate_TheDegradeFloorIsAppliedToTheSum_NotBeforeIt()
        {
            // a mis-set tunable of 0.5 plus 0.2 of gear is 0.7, still below the floor, so the burst
            // degrades to the plain ramp. Floor-then-add would have produced 1.2 and a LARGER answer.
            Assert.AreEqual((double)BloodChargeMath.DamageMultiplier(4, 0.07),
                            (double)ExsanguinateMath.BurstMultiplier(4, 0.07, 0.5, 0.2), 1e-6);

            Assert.AreNotEqual((double)BloodChargeMath.DamageMultiplier(4, 0.07 * 1.2),
                               (double)ExsanguinateMath.BurstMultiplier(4, 0.07, 0.5, 0.2), 1e-4,
                               "the floor must clamp the SUM of tunable and gear, not the tunable alone");
        }

        [TestMethod]
        public void Sanguinate_ABurstingCastStillCarriesTheBloodChargeMod()
        {
            var bloodCharge = Max(EquipmentModId.BloodCharge); // 0.0054

            // 1 + 5 * (0.07 + 0.0054) * 3.0 = 2.131. Without the forward this would collapse to 2.05 and
            // Blood Charge would silently vanish on exactly the cast it is worth most on.
            Assert.AreEqual(2.131, ExsanguinateMath.BurstMultiplier(5, 0.07, 3.0, 0.0, bloodCharge), 1e-6);

            Assert.AreNotEqual(2.05, (double)ExsanguinateMath.BurstMultiplier(5, 0.07, 3.0, 0.0, bloodCharge), 1e-4,
                "a bursting cast must not drop Sanguine Reserve's own equipment mod");
        }

        // ---- 37: Blood Price -> BloodPriceMath.DamageBonus -------------------------------------------
        //
        // Shape: damageBonus + gear. The HEALTH COST deliberately takes no gear term at all.

        [TestMethod]
        public void BloodPrice_ZeroGear_ReproducesTheUngearedBonusExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(BloodPriceMath.DamageBonus(rank, 0.08, 0.16, 0.24),
                                BloodPriceMath.DamageBonus(rank, 0.08, 0.16, 0.24, 0.0),
                                0.0, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }
        }

        [TestMethod]
        public void BloodPrice_Gear_AddsToTheDamageBonus()
        {
            var gear = Max(EquipmentModId.BloodPrice); // 0.025

            Assert.AreEqual(0.265, BloodPriceMath.DamageBonus(3, 0.08, 0.16, 0.24, gear), 1e-9);
            Assert.AreEqual(0.105, BloodPriceMath.DamageBonus(1, 0.08, 0.16, 0.24, gear), 1e-9);

            // CONTROL - scaling would be 0.24 * 1.025 = 0.246
            Assert.AreNotEqual(0.24 * (1.0 + gear), BloodPriceMath.DamageBonus(3, 0.08, 0.16, 0.24, gear), 1e-4,
                "gear must ADD to the damage bonus, not scale it");

            // rank 0 pays nothing and gains nothing, gear included
            Assert.AreEqual(0.0, BloodPriceMath.DamageBonus(0, 0.08, 0.16, 0.24, gear), 1e-12);
        }

        [TestMethod]
        public void BloodPrice_TheHealthCostIsStructurallyUnmoddable()
        {
            // Not "the call site happens not to pass gear" - HealthCostFraction has no parameter to pass it
            // to. Gear amplifies the payoff and never discounts the price (DESIGN.md 2.3), the same ruling
            // Savage Blows' stamina surcharge carries.
            var cost = typeof(BloodPriceMath).GetMethod(nameof(BloodPriceMath.HealthCostFraction));

            Assert.IsNotNull(cost);
            Assert.AreEqual(4, cost.GetParameters().Length,
                "HealthCostFraction must stay (rank, r1, r2, r3) - adding a gear parameter would let an item discount the price");

            // control: the payoff half DID gain one, so the count above is a real distinction
            var bonus = typeof(BloodPriceMath).GetMethod(nameof(BloodPriceMath.DamageBonus));
            Assert.AreEqual(5, bonus.GetParameters().Length);
        }

        // ---- 38: Clotting -> SanguineWardMath.WardFraction -------------------------------------------
        //
        // Shape: wardFraction + gear. The SELF-COST fraction deliberately takes no gear term at all.

        [TestMethod]
        public void Clotting_ZeroGear_ReproducesTheUngearedWardExactly()
        {
            for (var rank = 0; rank <= 3; rank++)
            {
                Assert.AreEqual(SanguineWardMath.WardFraction(rank, 0.50, 0.75, 1.00),
                                SanguineWardMath.WardFraction(rank, 0.50, 0.75, 1.00, 0.0),
                                0.0, $"rank {rank}: an explicit 0.0 gear term must be bit-identical");
            }
        }

        [TestMethod]
        public void Clotting_Gear_AddsToTheAbsorbFraction_WithNoUpperClamp()
        {
            var gear = Max(EquipmentModId.Clotting); // 0.10

            // rank 3 is already 100% and is deliberately not a ceiling
            Assert.AreEqual(1.10, SanguineWardMath.WardFraction(3, 0.50, 0.75, 1.00, gear), 1e-9);
            Assert.AreEqual(0.60, SanguineWardMath.WardFraction(1, 0.50, 0.75, 1.00, gear), 1e-9);

            // CONTROL - scaling would be 1.00 * 1.10, which coincides at rank 3; rank 1 is where the two
            // shapes separate (0.60 additive vs 0.55 multiplicative)
            Assert.AreNotEqual(0.50 * (1.0 + gear), SanguineWardMath.WardFraction(1, 0.50, 0.75, 1.00, gear), 1e-4,
                "gear must ADD to the ward fraction, not scale it");

            // no ability, no ward, whatever is equipped
            Assert.AreEqual(0.0, SanguineWardMath.WardFraction(0, 0.50, 0.75, 1.00, gear), 1e-12);

            // a negative sum still floors to zero rather than inverting the ward
            Assert.AreEqual(0.0, SanguineWardMath.WardFraction(1, -1.0, 0.75, 1.00, gear), 1e-12);
        }

        [TestMethod]
        public void Clotting_TheSelfCostIsStructurallyUnmoddable()
        {
            var selfCost = typeof(SanguineWardMath).GetMethod(nameof(SanguineWardMath.SelfCostFraction));

            Assert.IsNotNull(selfCost);
            Assert.AreEqual(4, selfCost.GetParameters().Length,
                "SelfCostFraction must stay (rank, r1, r2, r3) - gear buys a bigger ward, never a cheaper cast");

            // control: the absorb half DID gain one
            var ward = typeof(SanguineWardMath).GetMethod(nameof(SanguineWardMath.WardFraction));
            Assert.AreEqual(5, ward.GetParameters().Length);
        }

        // ---- the shared contract ---------------------------------------------------------------------

        [TestMethod]
        public void EveryGearParameter_IsLastAndDefaultsToZero()
        {
            // This is what makes the whole feature provably inert when nothing is equipped: a caller that
            // predates the mods still compiles and still gets the old number. Asserted structurally rather
            // than by inspection, because a later edit could reorder a parameter list without any test
            // noticing - the arithmetic assertions above all pass gear explicitly.
            AssertLastParameterIsOptionalAndZero(typeof(BloodChargeMath), nameof(BloodChargeMath.DamageMultiplier));
            AssertLastParameterIsOptionalAndZero(typeof(DrainSurplusDistribution), nameof(DrainSurplusDistribution.ShareFraction));
            AssertLastParameterIsOptionalAndZero(typeof(WeakenedBloodMath), nameof(WeakenedBloodMath.ResistanceMod));
            AssertLastParameterIsOptionalAndZero(typeof(MaledictionAbility), nameof(MaledictionAbility.IntensityBonus));
            AssertLastParameterIsOptionalAndZero(typeof(ExsanguinateMath), nameof(ExsanguinateMath.BurstMultiplier));
            AssertLastParameterIsOptionalAndZero(typeof(BloodPriceMath), nameof(BloodPriceMath.DamageBonus));
            AssertLastParameterIsOptionalAndZero(typeof(SanguineWardMath), nameof(SanguineWardMath.WardFraction));
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
    }
}
