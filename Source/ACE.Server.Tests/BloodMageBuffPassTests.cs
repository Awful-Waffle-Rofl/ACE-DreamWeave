using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Blood Mage buff pass, Package A (blood economy and re-ranks): the three new affinity riders
    /// (Sanguine Reserve per-charge value x Creature Enchantment, Blood Price damage bonus x Life Magic,
    /// Sanguine Ward absorb x Cooking), Exsanguinate's kept charges, and source pins on the Player-bound
    /// wiring that cannot run under this test host (Player's static initializer).
    ///
    /// READS NO PropertyManager KEY. Every expectation is a literal passed straight into a pure helper, so
    /// the class needs no seeding and passes alone or in any order.
    /// </summary>
    [TestClass]
    public class BloodMageBuffPassTests
    {
        // ---- affinity shape: rankBonus * multiplier + gear ------------------------------------------

        [TestMethod]
        public void SanguineReserve_PerChargeValue_IsTheTunableTimesTheMultiplier_AndGearStaysOutside()
        {
            Assert.AreEqual(0.14, BloodChargeMath.PerChargeValue(0.07, 2.0), 1e-12);

            // a full pool of 5 with Blood Charge gear: 1 + 5 * (0.07 * 2.0 + 0.0054) = 1.727
            var ramp = BloodChargeMath.DamageMultiplier(5, BloodChargeMath.PerChargeValue(0.07, 2.0), 0.0054);
            Assert.AreEqual(1.727, ramp, 1e-6);

            // CONTROL - multiplying the gear as well would be 1 + 5 * (0.07 + 0.0054) * 2.0 = 1.754
            Assert.AreNotEqual(1.0 + 5 * (0.07 + 0.0054) * 2.0, (double)ramp, 1e-4,
                "the Blood Charge gear term must never be multiplied by the Creature Enchantment affinity");
        }

        [TestMethod]
        public void SanguineReserve_PerChargeValue_FloorsTheMultiplierAtNeutral()
        {
            // 0.0 is the old ADDITIVE primitive's neutral value; handed to a multiplier it would delete the
            // ramp. It degrades to the bare tunable instead.
            Assert.AreEqual(0.07, BloodChargeMath.PerChargeValue(0.07, 0.0), 1e-12);
            Assert.AreEqual(0.07, BloodChargeMath.PerChargeValue(0.07, 1.0), 0.0);
        }

        [TestMethod]
        public void Exsanguinate_Burst_InheritsTheAffinityScaledPerChargeValue()
        {
            // the burst multiplies the SAME per-charge value the ramp uses: 1 + 5 * (0.07 * 2.0) * 3.0 = 3.1
            Assert.AreEqual(3.1, ExsanguinateMath.BurstMultiplier(5, BloodChargeMath.PerChargeValue(0.07, 2.0), 3.0), 1e-6);

            // control: the unscaled burst is the shipped 2.05, so the affinity really reached the burst
            Assert.AreEqual(2.05, ExsanguinateMath.BurstMultiplier(5, BloodChargeMath.PerChargeValue(0.07, 1.0), 3.0), 1e-6);
        }

        [TestMethod]
        public void SanguineWard_Rank1_Mult2_Gear0p1_Is1p3()
        {
            // 0.60 * 2.0 + 0.10
            Assert.AreEqual(1.30, SanguineWardMath.ScaledWardFraction(1, 0.60, 0.90, 1.20, 2.0, 0.10), 1e-9);

            // the self-cost is untouched by any of it: SelfCostFraction has no affinity or gear parameter
            Assert.AreEqual(4, typeof(SanguineWardMath).GetMethod(nameof(SanguineWardMath.SelfCostFraction)).GetParameters().Length);
        }

        [TestMethod]
        public void BloodPrice_HealthCost_IsUnchangedUnderAffinity_WhileTheBonusGrows()
        {
            var costFraction = BloodPriceMath.HealthCostFraction(3, 0.03, 0.04, 0.05);

            var plain = BloodPriceMath.Resolve(1000, 1000, costFraction,
                BloodPriceMath.ScaledDamageBonus(3, 0.10, 0.20, 0.30, 1.0), 0.20);

            var scaled = BloodPriceMath.Resolve(1000, 1000, costFraction,
                BloodPriceMath.ScaledDamageBonus(3, 0.10, 0.20, 0.30, 2.0), 0.20);

            Assert.AreEqual(50u, plain.HealthCost);
            Assert.AreEqual(plain.HealthCost, scaled.HealthCost, "the Life Magic affinity must never change the health cost");

            // control: the affinity really did move the payoff, so the equality above is not vacuous
            Assert.AreEqual(1.30f, plain.DamageMultiplier, 1e-6f);
            Assert.AreEqual(1.60f, scaled.DamageMultiplier, 1e-6f);

            // and HealthCostFraction has no parameter an affinity could even be passed to
            Assert.AreEqual(4, typeof(BloodPriceMath).GetMethod(nameof(BloodPriceMath.HealthCostFraction)).GetParameters().Length);
        }

        [TestMethod]
        public void AffinityDeclarations_MatchThePlan()
        {
            Assert.AreEqual(Skill.CreatureEnchantment, Definition(ClassAbilityId.SanguineReserve).AffinitySkill);
            Assert.AreEqual(Skill.LifeMagic, Definition(ClassAbilityId.BloodPrice).AffinitySkill);
            Assert.AreEqual(Skill.Cooking, Definition(ClassAbilityId.SanguineWard).AffinitySkill);

            // Exsanguinate inherits Sanguine Reserve's affinity through the shared per-charge value but has
            // none of its own, and Crimson Harvest's rank buys a count with no rider
            Assert.IsNull(Definition(ClassAbilityId.Exsanguinate).AffinitySkill);
            Assert.IsNull(Definition(ClassAbilityId.CrimsonHarvest).AffinitySkill);
        }

        // ---- Exsanguinate: charges kept ------------------------------------------------------------

        [TestMethod]
        public void ChargesKept_ClampsToCapMinusOne()
        {
            // rank 3 keeps 3, but against a rank-1 Sanguine Reserve cap of 3 that would leave the pool FULL
            Assert.AreEqual(2, ExsanguinateMath.ChargesKept(3, 1, 2, 3, 3));

            // rank 1 against the rank-3 cap of 5 keeps its own 1, unclamped
            Assert.AreEqual(1, ExsanguinateMath.ChargesKept(1, 1, 2, 3, 5));

            Assert.AreEqual(2, ExsanguinateMath.ChargesKept(2, 1, 2, 3, 5));
            Assert.AreEqual(3, ExsanguinateMath.ChargesKept(3, 1, 2, 3, 5));
        }

        [TestMethod]
        public void ChargesKept_DegenerateInputsKeepNothing()
        {
            Assert.AreEqual(0, ExsanguinateMath.ChargesKept(0, 1, 2, 3, 5), "rank 0 owns nothing");
            Assert.AreEqual(0, ExsanguinateMath.ChargesKept(3, 1, 2, 3, 0), "no Sanguine Reserve, no pool");
            Assert.AreEqual(0, ExsanguinateMath.ChargesKept(1, -4, 2, 3, 5), "a negative tunable floors at 0");
            Assert.AreEqual(3, ExsanguinateMath.ChargesKept(9, 1, 2, 3, 5), "a rank above 3 clamps to rank 3");
        }

        [TestMethod]
        public void KeptPool_NeverBurstsOnTheVeryNextCast()
        {
            // the full-pool gate must still need a rebuild: for every rank and every cap the kept pool resolves
            // to Accrue, never Burst
            for (var cap = 1; cap <= 5; cap++)
            {
                for (var rank = 1; rank <= 3; rank++)
                {
                    var kept = ExsanguinateMath.ChargesKept(rank, 1, 2, 3, cap);

                    Assert.AreEqual(BloodChargeCastOutcome.Accrue, ExsanguinateMath.Resolve(true, true, kept, cap),
                        $"rank {rank}, cap {cap}: a kept pool of {kept} must not be full");
                }
            }
        }

        // ---- source pins on Player-bound wiring ----------------------------------------------------

        [TestMethod]
        public void Burst_SetsThePoolToTheKeptCharges_InsteadOfClearingIt()
        {
            var text = BuffsSource();

            var body = MethodBody(text, "public float ApplyBloodChargeDamage(", "public float GetExsanguinateResistanceIgnoreMod(");

            Assert.IsFalse(body.Contains("ClearBloodChargeStacks"), "the burst must no longer empty the pool");
            StringAssert.Contains(body, "ExsanguinateMath.ChargesKept(");
            StringAssert.Contains(body, "SetBloodChargeStacks(kept)");

            // the old clearing method is gone outright, so nothing can call it by mistake
            Assert.IsFalse(text.Contains("void ClearBloodChargeStacks("), "ClearBloodChargeStacks should have been removed");

            // and the setter refreshes the idle clock, so the kept charges get a full window
            var setter = MethodBody(text, "public void SetBloodChargeStacks(", "public int GetBloodChargeStacks(");
            StringAssert.Contains(setter, "bloodChargeLastAddTime = Time.GetUnixTime()");
        }

        [TestMethod]
        public void HemomancyDamageMod_IsAppliedAtTheThreeMagicSites()
        {
            var text = BuffsSource();

            var sites = new (string Start, string End)[]
            {
                ("public float GetClassAbilitySpellDamageMod(", "public float GetFastAimLowEndBonus("),
                ("public float ApplyHarmClassAbilityDamage(", "public LifeProjectileCastStamp GetLifeProjectileCastStamp("),
                ("public float ApplyLifeProjectileClassAbilityDamage(", "public float ApplyLifeHealthBoltBloodCharge("),
            };

            foreach (var (start, end) in sites)
            {
                var body = MethodBody(text, start, end);

                Assert.AreEqual(1, Regex.Matches(body, @"mod \*= GetHemomancyDamageMod\(\);").Count,
                    $"{start}: expected exactly one Hemomancy damage term");

                // positioned like the Resonance term: right after it
                Assert.IsTrue(body.IndexOf("GetHemomancyDamageMod()", StringComparison.Ordinal) > body.IndexOf("GetResonanceMagicDamageMod()", StringComparison.Ordinal),
                    $"{start}: the Hemomancy term should follow the Resonance term");
            }
        }

        [TestMethod]
        public void ExsanguinateHandler_NamesNoAffinitySkill()
        {
            var text = File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(),
                "Source", "ACE.Server", "ClassAbilities", "Abilities", "ExsanguinateAbility.cs"));

            Assert.IsFalse(Regex.IsMatch(text, @"GetClassAbility(?:Scaling|AffinityMultiplier)\s*\("),
                "Sanguine Reserve's affinity must reach the Exsanguinate readout through a Player helper, never a direct call in this file");

            StringAssert.Contains(text, "GetBloodChargePerStackValue()");
        }

        // ---- helpers -------------------------------------------------------------------------------

        private static ClassAbilityDefinition Definition(ClassAbilityId id)
        {
            Assert.IsTrue(ClassAbilityRegistry.Abilities.TryGetValue(id, out var def), $"{id} is not registered");
            return def;
        }

        private static string BuffsSource() =>
            File.ReadAllText(Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(),
                "Source", "ACE.Server", "WorldObjects", "Player_ClassAbilityBuffs.cs"));

        private static string MethodBody(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(from >= 0, $"could not find '{start}'");

            var to = text.IndexOf(end, from, StringComparison.Ordinal);
            Assert.IsTrue(to > from, $"could not find '{end}' after '{start}'");

            return text.Substring(from, to - from);
        }
    }
}
