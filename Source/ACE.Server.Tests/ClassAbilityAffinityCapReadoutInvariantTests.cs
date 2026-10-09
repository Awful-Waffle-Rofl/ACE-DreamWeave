using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the readout invariant this pass (2026-09-12 "make the readout panel consistent") exists to
    /// establish: for every capped-affinity ability, Skill + Affinity + Gear == Effective EXACTLY, whether or
    /// not the cap is biting. Before this pass BreakArmor, Elemental Rend, Shield Block and Pocket Sand
    /// reported the RAW (pre-clamp) added amount in Affinity while Effective used the clamped one, so the
    /// three displayed terms visibly did not sum to the total whenever the cap actually bit; Spellblade,
    /// Runeblade, Sundermark and Dispelling Edge already reported the clamped amount and are pinned here as
    /// controls so a future change cannot silently regress them back to the RAW shape.
    ///
    /// EXERCISED THROUGH THE PURE STATIC HELPERS (Chance/BlockChance/RendChance), not through GetReadout:
    /// GetReadout on every one of these eight abilities reads a live Player's affinity skill, and Player's
    /// static initializer cannot run under this test host (see SpellswordReadoutTests/
    /// SpellswordVanguardEquipmentModTests for the same constraint and the same workaround). Each assertion
    /// below reproduces the exact arithmetic the corresponding GetReadout performs - rankBonus/chanceBase,
    /// the raw added amount, the clamped added amount, and the gear term - and checks it against what the
    /// ability's own pure Chance()-shaped method returns, which is what GetReadout's Effective is built from.
    ///
    /// A SATURATING multiplier (the same +209 percentage point live-measured rider used throughout
    /// SpellswordVanguardEquipmentModTests) is used everywhere so every test actually exercises the cap
    /// biting - "the cap never fires in this test" would make the invariant trivially true and prove nothing.
    ///
    /// OUT OF SCOPE, and why (see the ca-readout-cap-uniformity task report for the full reasoning):
    ///  - SpellAoeAbility clamps a total-splash damage FRACTION (baseMult + rider + gearMod jointly, floored
    ///    at baseMult), not a proc-chance affinity-added-amount - explicitly named out of scope by the task.
    ///  - ManaBarrierAbility and SoulTetherAbility clamp the TOTAL (skillShare/skillReduction + rawAffinity +
    ///    gearBonus) jointly against one ceiling, with no independently-clamped affinity value of their own -
    ///    the same shape as Spell AOE's total-splash cap, not the four/eight target handlers' shape where the
    ///    affinity term alone is clamped before summation.
    ///  - ParryAbility, BattleHardenedAbility, AttackSpeedAbility, FrenzyAbility and SanguineWardAbility
    ///    either carry no affinity term on this readout line, or their only cap is a composed
    ///    multiplicative-product ceiling (the shared attack-speed "anim ceiling") or a different ability's
    ///    pooled avoidance cap, never a clamp on their own affinity-added amount.
    ///
    /// AcidProcAbility's PRIMARY (proc chance) is still out of scope for the same reason - it carries no
    /// affinity term at all. Its SECONDARY (poison-damage bonus, added 2026-09-24 once the readout
    /// contract grew a Secondary list) DOES clamp its own affinity-added amount
    /// (class_ability_acidproc_damage_affinity_cap) exactly like the four target abilities above, so it is
    /// walked here too, mirroring PoisonDamageBonus rather than GetReadout for the same null-Player reason
    /// the other four use their own pure Chance()-shaped helpers.
    /// </summary>
    [TestClass]
    public class ClassAbilityAffinityCapReadoutInvariantTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // The live 2026-08-04 measured saturating rider, expressed as a multiplier: 1 + 2.09/base reproduces
        // a raw added amount of +2.09 (209 percentage points) whatever the base is, guaranteeing every test
        // below actually drives the cap.
        private const double SaturatingDelta = 2.09;

        private static double Multiplier(double rankBonus) => 1.0 + SaturatingDelta / rankBonus;

        // Several of the underlying Chance()-shaped helpers return float, not double, so a value carried
        // through a percentage-point multiply can differ from its double-computed mirror by float-precision
        // noise (~1e-6 on these magnitudes) even when the two are computing the same clamp arithmetic. The
        // tolerance is loosened accordingly; it is still far below anything a cap boundary could produce.
        private static void AssertTermsSumToEffective(double skill, double affinity, double gear, double effective, string label)
        {
            Assert.AreEqual(effective, skill + affinity + gear, 1e-4,
                $"{label}: Skill + Affinity + Gear must equal Effective exactly");
        }

        [TestMethod]
        public void BreakArmor_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double chanceBase = 0.15;
            const double cap = 0.10;
            const double gear = 0.02;
            var multiplier = Multiplier(chanceBase);

            var rawAdded = chanceBase * (multiplier - 1.0);
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap for this test to mean anything");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = chanceBase * 100.0;
            var affinity = clampedAdded * 100.0;
            var gearPct = gear * 100.0;
            var effective = BreakArmorAbility.Chance(1, chanceBase, multiplier, cap, gear) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Break Armor");

            // control: the OLD (raw) shape would NOT have summed once the cap bit
            Assert.AreNotEqual(effective, skill + rawAdded * 100.0 + gearPct, 1e-6,
                "control: reporting the raw pre-clamp amount must break the sum once the cap bites");
        }

        [TestMethod]
        public void ElementalRend_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double chanceBase = 0.08;
            const double chanceStep = 0.06;
            const int rank = 3;
            const double cap = 0.10;
            const double gear = 0.02;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;
            var multiplier = Multiplier(rankBonus);

            var rawRider = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawRider > cap, "control: the rider must actually exceed the cap");

            var clampedRider = System.Math.Min(rawRider, cap);

            var skill = rankBonus * 100.0;
            var affinity = clampedRider * 100.0;
            var gearPct = gear * 100.0;
            var effective = ElementalRendAbility.RendChance(rank, chanceBase, chanceStep, rawRider, gear, cap) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Elemental Rend");

            Assert.AreNotEqual(effective, skill + rawRider * 100.0 + gearPct, 1e-6,
                "control: reporting the raw pre-clamp rider must break the sum once the cap bites");
        }

        [TestMethod]
        public void ShieldBlock_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double baseChance = 0.08;
            const double step = 0.06;
            const int rank = 3;
            const double cap = 0.20;
            const double gear = 0.0141;

            var rankBonus = baseChance + (rank - 1) * step;
            var multiplier = Multiplier(rankBonus);

            var rawAdded = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = rankBonus * 100.0;
            var affinity = clampedAdded * 100.0;
            var gearPct = gear * 100.0;
            var effective = ShieldBlockAbility.BlockChance(rank, baseChance, step, multiplier, cap, gear) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Shield Block");

            Assert.AreNotEqual(effective, skill + rawAdded * 100.0 + gearPct, 1e-6,
                "control: reporting the raw pre-clamp amount must break the sum once the cap bites");
        }

        [TestMethod]
        public void PocketSand_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double chanceBase = 0.10;
            const double chanceStep = 0.10;
            const int rank = 3;
            const double cap = 0.10;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;
            var multiplier = Multiplier(rankBonus);

            var rawAdded = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = rankBonus * 100.0;
            var affinity = clampedAdded * 100.0;
            const double gearPct = 0.0; // Pocket Sand carries no equipment mod
            var effective = PocketSandAbility.Chance(rank, chanceBase, chanceStep, multiplier, cap) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Pocket Sand");

            Assert.AreNotEqual(effective, skill + rawAdded * 100.0 + gearPct, 1e-6,
                "control: reporting the raw pre-clamp amount must break the sum once the cap bites");
        }

        /// <summary>
        /// Cloaked in Power (Vanguard T2, added 2026-09-29). Its floor is a PROC CHANCE bounded by the shared
        /// class_ability_affinity_chance_cap, so it belongs in this family. Single rank, so there is no ladder:
        /// the tunable itself is the rank bonus.
        ///
        /// This test class is NOT a generic registry scan - it carries one hand-added [TestMethod] per covered
        /// ability, so a new capped-affinity readout gets zero coverage until a method like this one exists.
        /// </summary>
        [TestMethod]
        public void CloakedInPower_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double floorBase = 0.10;   // class_ability_cloakedinpower_floor default
            const double cap = 0.20;         // class_ability_affinity_chance_cap default
            const int rank = 1;              // MaxRank is 1

            var multiplier = Multiplier(floorBase);

            var rawAdded = floorBase * multiplier - floorBase;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap for this test to mean anything");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = floorBase * 100.0;
            var affinity = clampedAdded * 100.0;
            const double gearPct = 0.0; // Cloaked in Power carries no equipment mod
            var effective = CloakedInPowerAbility.Floor(rank, floorBase, multiplier, cap) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Cloaked in Power");

            // control: the OLD (raw) shape would NOT have summed once the cap bit
            Assert.AreNotEqual(effective, skill + rawAdded * 100.0 + gearPct, 1e-6,
                "control: reporting the raw pre-clamp amount must break the sum once the cap bites");
        }

        // ---- Controls: these four already reported the clamped amount before this pass, and must stay ----
        // ---- that way. ------------------------------------------------------------------------------------

        [TestMethod]
        public void Spellblade_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double chanceBase = 0.20;
            const double cap = 0.20;
            const double gear = 0.036;
            var multiplier = Multiplier(chanceBase);

            var rawAdded = chanceBase * multiplier - chanceBase;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = chanceBase * 100.0;
            var affinity = clampedAdded * 100.0;
            var gearPct = gear * 100.0;
            var effective = SpellbladeAbility.Chance(1, chanceBase, multiplier, 0.0, cap, gear) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Spellblade");
        }

        [TestMethod]
        public void Runeblade_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double chanceBase = 0.15;
            const double cap = 0.20;
            const double gear = 0.036;
            var multiplier = Multiplier(chanceBase);

            var rawAdded = chanceBase * multiplier - chanceBase;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = chanceBase * 100.0;
            var affinity = clampedAdded * 100.0;
            var gearPct = gear * 100.0;
            var effective = RunebladeAbility.Chance(1, chanceBase, multiplier, 0.0, cap, gear) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Runeblade");
        }

        [TestMethod]
        public void Sundermark_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double chanceBase = 0.05;
            const double chanceStep = 0.05;
            const int rank = 3;
            const double cap = 0.20;
            const double gear = 0.015;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;
            var multiplier = Multiplier(rankBonus);

            var rawAdded = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = rankBonus * 100.0;
            var affinity = clampedAdded * 100.0;
            var gearPct = gear * 100.0;
            var effective = SundermarkAbility.Chance(rank, chanceBase, chanceStep, multiplier, cap, gear) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Sundermark");
        }

        [TestMethod]
        public void DispellingEdge_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double chanceBase = 0.10;
            const double chanceStep = 0.05;
            const int rank = 3;
            const double cap = 0.20;
            const double gear = 0.02;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;
            var multiplier = Multiplier(rankBonus);

            var rawAdded = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = rankBonus * 100.0;
            var affinity = clampedAdded * 100.0;
            var gearPct = gear * 100.0;
            var effective = DispellingEdgeAbility.Chance(rank, chanceBase, chanceStep, multiplier, gear, cap) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Dispelling Edge");
        }

        // ---- Acid Proc's SECONDARY (poison-damage bonus) - added once the readout contract grew a -----
        // ---- Secondary list; the PRIMARY (proc chance) stays out of scope, see the type doc. ----------

        [TestMethod]
        public void AcidProc_PoisonDamageSecondary_CappedAffinity_SkillPlusAffinityPlusGearEqualsEffective()
        {
            const double damageBase = 0.25;
            const double damageStep = 0.25;
            const int rank = 3;
            const double cap = 0.20;

            var rankBonus = damageBase + (rank - 1) * damageStep;
            var multiplier = Multiplier(rankBonus);

            var rawAdded = rankBonus * multiplier - rankBonus;
            Assert.IsTrue(rawAdded > cap, "control: the rider must actually exceed the cap for this test to mean anything");

            var clampedAdded = System.Math.Min(rawAdded, cap);

            var skill = rankBonus * 100.0;
            var affinity = clampedAdded * 100.0;
            const double gearPct = 0.0; // the poison-damage bonus carries no equipment-mod term
            var effective = AcidProcAbility.PoisonDamageBonus(rank, damageBase, damageStep, multiplier, cap) * 100.0;

            AssertTermsSumToEffective(skill, affinity, gearPct, effective, "Acid Proc poison-damage secondary");

            // control: the OLD (raw) shape would NOT have summed once the cap bit
            Assert.AreNotEqual(effective, skill + rawAdded * 100.0 + gearPct, 1e-6,
                "control: reporting the raw pre-clamp amount must break the sum once the cap bites");
        }
    }
}
