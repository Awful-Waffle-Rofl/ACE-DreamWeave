using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the SECOND scalar (ClassAbilityReadout.Secondary) added to seven handlers so
    /// /abilities list can show more than one live magnitude on the same line (player report: Acid Proc's
    /// rank-scaled poison-damage bonus was invisible; the rank number backed nothing but a flat proc
    /// chance).
    ///
    /// AcidProcAbility.GetReadout, SanguineWardAbility.GetReadout and NetherBloomAbility.GetReadout are
    /// null-Player-safe (see BloodMageReadoutTests' header for why that matters under this test host), so
    /// those three are exercised through GetReadout(null, rank) directly, like SanguineReserve/
    /// WeakenedBlood/BloodPrice there - a REAL check of GetReadout's actual .Secondary contents, not a
    /// restatement.
    ///
    /// EmpoweredSummons, Hemomancy, Taunt and Soul Jump all call a live-Player affinity method WITHOUT a
    /// null-conditional on their PRIMARY term (pre-existing, unchanged by this pass), so their GetReadout
    /// cannot run here AT ALL, not even with the FormatterServices.GetUninitializedObject "inert Player"
    /// trick this assembly uses elsewhere (SalvageCombineTests.cs, MuleSummonTests.cs) for Player methods
    /// that touch no instance state - EmpoweredSummonsAbility.GetReadout does touch instance state
    /// (Creature.GetEquippedModValue reads an equipped-object dictionary that a field initializer, not the
    /// constructor body, populates - exactly the failure mode MuleCommerceTests.cs's header predicts for
    /// this trick), and was CONFIRMED to NullReferenceException on an inert Player at
    /// Creature_EquipmentMods.cs:37 before this file was written (probed live, then removed - see this
    /// task's friction report, not committed here since it would just be dead code proving a negative).
    /// A LIVE PLAYER CANNOT BE BUILT AT ALL IN THIS TEST PROJECT (Player's constructor makes an
    /// unconditional live ace_auth round-trip - see MuleCommerceTests.cs's class remarks, independently
    /// re-confirmed there twice already), so "exercise the real GetReadout" is not achievable for these
    /// four abilities by any means available in this suite.
    ///
    /// The closest honest substitute: EmpoweredSummonsAbility.DurationSecondary and
    /// TauntAbility.RadiusSecondary are the ACTUAL pure factory methods GetReadout calls for those two
    /// segments (not restatements of their arithmetic - see each ability file's own doc comment), so
    /// calling them directly here exercises the exact same code GetReadout would run, missing only the
    /// live-Player plumbing that resolves their inputs (the duration/gear multiplier). Hemomancy and Soul
    /// Jump are covered the same way ClassAbilityAffinityCapReadoutInvariantTests already establishes for
    /// this exact constraint: through their own pure static helpers.
    /// </summary>
    [TestClass]
    public class ClassAbilityMultiScalarReadoutTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---- Acid Proc: poison-damage secondary ------------------------------------------------------

        [TestMethod]
        public void AcidProc_Readout_PoisonDamageSecondary_ReportsRankLadderWithNoAffinity()
        {
            var ability = new AcidProcAbility();

            var r1 = ability.GetReadout(null, 1);
            var r2 = ability.GetReadout(null, 2);
            var r3 = ability.GetReadout(null, 3);

            // shipped defaults: base 0.25, step 0.25 -> +25/50/75% at ranks 1-3, no affinity from a null Player
            AssertSinglePoisonDamageSecondary(r1, expectedSkill: 25.0, expectedAffinity: 0.0);
            AssertSinglePoisonDamageSecondary(r2, expectedSkill: 50.0, expectedAffinity: 0.0);
            AssertSinglePoisonDamageSecondary(r3, expectedSkill: 75.0, expectedAffinity: 0.0);
        }

        [TestMethod]
        public void AcidProc_Readout_PrimaryProcChanceUnaffectedByPoisonDamageSecondary()
        {
            var ability = new AcidProcAbility();

            var readout = ability.GetReadout(null, 3);

            // the primary segment (proc chance) must render exactly as before this pass
            Assert.AreEqual(30.0, readout.Skill, 1e-9); // class_ability_acidproc_chance_base default 0.30
            Assert.AreEqual(0.0, readout.Affinity, 1e-9);
            Assert.AreEqual(0.0, readout.Gear, 1e-9);
            Assert.AreEqual(30.0, readout.Effective, 1e-9);
            Assert.AreEqual("proc", readout.Label);
            Assert.IsFalse(readout.Capped);
        }

        [TestMethod]
        public void AcidProc_PoisonDamageBonus_PureFunction_AffinityCapBitesAtDefault()
        {
            // Mirrors what the readout's affinity term would show at a saturating Alchemy - the same
            // saturating-rider shape ClassAbilityAffinityCapReadoutInvariantTests uses.
            const double rankBonus = 0.75; // rank 3
            const double cap = 0.20;       // class_ability_acidproc_damage_affinity_cap default
            var multiplier = 1.0 + (cap + 1.0) / rankBonus; // guarantees the raw added amount exceeds cap

            var uncapped = AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, multiplier, affinityCap: 0.0);
            var capped = AcidProcAbility.PoisonDamageBonus(3, 0.25, 0.25, multiplier, affinityCap: cap);

            Assert.IsTrue(uncapped - rankBonus > cap, "control: the rider must actually exceed the cap for this test to mean anything");
            Assert.AreEqual(rankBonus + cap, capped, 1e-9);
        }

        private static void AssertSinglePoisonDamageSecondary(ClassAbilityReadout readout, double expectedSkill, double expectedAffinity)
        {
            Assert.IsNotNull(readout.Secondary);
            Assert.AreEqual(1, readout.Secondary.Count);

            var secondary = readout.Secondary[0];

            Assert.IsTrue(secondary.HasValue);
            Assert.AreEqual(expectedSkill, secondary.Skill, 1e-9);
            Assert.AreEqual(expectedAffinity, secondary.Affinity, 1e-9);
            Assert.AreEqual(0.0, secondary.Gear, 1e-9);
            Assert.AreEqual(expectedSkill + expectedAffinity, secondary.Effective, 1e-9);
            Assert.AreEqual("%", secondary.Unit);
            Assert.AreEqual("+", secondary.Prefix);
            Assert.AreEqual("poison dmg", secondary.Label);
            Assert.IsFalse(secondary.Capped);
        }

        // ---- Empowered Summons: duration and leech secondaries ---------------------------------------

        [TestMethod]
        public void EmpoweredSummons_DurationMultiplier_ClampsToConfiguredCap()
        {
            const double cap = 1.00; // class_ability_empoweredsummons_max_duration_bonus default

            var uncapped = EmpoweredSummonsAbility.DurationMultiplier(3, loyaltyFraction: 5.0, cap: 1_000_000.0);
            var capped = EmpoweredSummonsAbility.DurationMultiplier(3, loyaltyFraction: 5.0, cap: cap);

            Assert.AreEqual(6.0, uncapped, 1e-9); // 1 + 5.0, uncapped
            Assert.AreEqual(1.0 + cap, capped, 1e-9);
        }

        [TestMethod]
        public void EmpoweredSummons_DurationMultiplier_RankGated_ZeroAtRankZero()
        {
            Assert.AreEqual(1.0f, EmpoweredSummonsAbility.DurationMultiplier(0, loyaltyFraction: 5.0, cap: 1.0));
        }

        [TestMethod]
        public void EmpoweredSummons_LeechFraction_MatchesRankLadderPlusLeadershipRider()
        {
            const double perRank = 0.03; // class_ability_empoweredsummons_leech_percent_per_rank default

            var r3NoAffinity = EmpoweredSummonsAbility.LeechFraction(3, perRank, leadershipFraction: 0.0);
            var r3WithAffinity = EmpoweredSummonsAbility.LeechFraction(3, perRank, leadershipFraction: 0.02);

            Assert.AreEqual(0.09, r3NoAffinity, 1e-9); // 3 * 0.03
            Assert.AreEqual(0.11, r3WithAffinity, 1e-9);
        }

        // DurationSecondary is the ACTUAL pure factory GetReadout calls (EmpoweredSummonsAbility.cs's own
        // GetReadout does `DurationSecondary(player.GetEmpoweredSummonsDurationMod())` - not restated
        // here), so these two tests exercise the real omission decision end to end short of the live-Player
        // plumbing that produces its input multiplier.

        [TestMethod]
        public void EmpoweredSummons_DurationSecondary_OmittedAtNeutralMultiplier_UntrainedLoyalty()
        {
            // 1.0 is exactly what Player.GetEmpoweredSummonsDurationMod returns for untrained/Innate
            // Loyalty (GetClassAbilityScaling's Trained-only threshold) or rank <= 0 - the case the
            // reviewer flagged: this MUST be omitted, not appended as a zero.
            var secondary = EmpoweredSummonsAbility.DurationSecondary(1.0);

            Assert.IsFalse(secondary.HasValue, "a neutral (1.0) duration multiplier must OMIT the secondary, not report a zero entry");
        }

        [TestMethod]
        public void EmpoweredSummons_DurationSecondary_PresentAndCorrect_WhenLoyaltyGrantsABonus()
        {
            var secondary = EmpoweredSummonsAbility.DurationSecondary(1.2); // +20% duration

            Assert.IsTrue(secondary.HasValue);
            var readout = secondary.Value;
            Assert.AreEqual(0.0, readout.Skill, 1e-9);
            Assert.AreEqual(20.0, readout.Affinity, 1e-9);
            Assert.AreEqual(0.0, readout.Gear, 1e-9);
            Assert.AreEqual(20.0, readout.Effective, 1e-9);
            Assert.AreEqual("%", readout.Unit);
            Assert.AreEqual("+", readout.Prefix);
            Assert.AreEqual("pet duration", readout.Label);
        }

        // ---- Hemomancy: drain secondary ---------------------------------------------------------------

        [TestMethod]
        public void Hemomancy_DrainMultiplier_MatchesTickMultiplierShapeAtEachRank()
        {
            const double perRank = 0.02; // class_ability_hemomancy_drain_percent_per_rank default

            for (var rank = 1; rank <= 5; rank++)
            {
                var expected = 1.0 + rank * perRank;
                Assert.AreEqual(expected, HemomancyAbility.DrainMultiplier(rank, perRank, affinityRider: 0.0), 1e-6,
                    $"rank {rank}");
            }
        }

        [TestMethod]
        public void Hemomancy_DrainMultiplier_RankZero_IsNeutral()
        {
            Assert.AreEqual(1.0f, HemomancyAbility.DrainMultiplier(0, 0.02, affinityRider: 0.5));
        }

        // ---- Taunt: radius secondary -------------------------------------------------------------------

        [TestMethod]
        public void Taunt_EffectiveRadius_SumsBaseAndBellowGearWithNoClamp()
        {
            const double baseRadius = 10.0;

            Assert.AreEqual(baseRadius, TauntAbility.EffectiveRadius(baseRadius));
            Assert.AreEqual(baseRadius + 4.0, TauntAbility.EffectiveRadius(baseRadius, 4.0));
        }

        [TestMethod]
        public void Taunt_EffectiveRadius_NegativeGear_IsFlooredAtZeroContribution()
        {
            const double baseRadius = 10.0;

            // matches Activate's own Math.Max(0.0, ...) treatment of the Bellow read
            Assert.AreEqual(baseRadius, TauntAbility.EffectiveRadius(baseRadius, -3.0));
        }

        // RadiusSecondary is the ACTUAL pure factory GetReadout calls (TauntAbility.cs's own GetReadout
        // does `RadiusSecondary(baseRadius, radiusGear)` - not restated here).

        [TestMethod]
        public void Taunt_RadiusSecondary_ReportsBaseAndGearInMetresWithNoAffinity()
        {
            var noGear = TauntAbility.RadiusSecondary(15.0);
            var withGear = TauntAbility.RadiusSecondary(15.0, 1.0);

            Assert.IsTrue(noGear.HasValue);
            Assert.AreEqual(15.0, noGear.Skill, 1e-9);
            Assert.AreEqual(0.0, noGear.Affinity, 1e-9);
            Assert.AreEqual(0.0, noGear.Gear, 1e-9);
            Assert.AreEqual(15.0, noGear.Effective, 1e-9);
            Assert.AreEqual("m", noGear.Unit);
            Assert.AreEqual("radius", noGear.Label);

            Assert.IsTrue(withGear.HasValue);
            Assert.AreEqual(15.0, withGear.Skill, 1e-9);
            Assert.AreEqual(1.0, withGear.Gear, 1e-9);
            Assert.AreEqual(16.0, withGear.Effective, 1e-9);
        }

        // ---- Soul Jump: cooldown secondary -------------------------------------------------------------

        [TestMethod]
        public void SoulJump_CooldownSeconds_MatchesRankLadder()
        {
            var baseSeconds = PropertyManager.GetDouble("class_ability_souljump_cooldown_seconds_base").Item;
            var stepSeconds = PropertyManager.GetDouble("class_ability_souljump_cooldown_seconds_step").Item;

            var r1 = SoulJumpMath.CooldownSeconds(1, baseSeconds, stepSeconds, SoulJumpAbility.MinimumCooldownSeconds);
            var r3 = SoulJumpMath.CooldownSeconds(3, baseSeconds, stepSeconds, SoulJumpAbility.MinimumCooldownSeconds);

            // 6 minutes at rank 1, 2 minutes at rank 3 per the ability's own description
            Assert.AreEqual(360.0, r1, 1e-6);
            Assert.AreEqual(120.0, r3, 1e-6);
        }

        // ---- Nether Bloom: gear-scaled extra-jump-chance secondary -------------------------------------
        //
        // NetherBloomAbility.GetReadout ignores `player` entirely once the gear read is null-conditional
        // (`player?.GetEquippedModValue(...) ?? 0.0`), so unlike EmpoweredSummons/Taunt this IS exercised
        // through the real GetReadout(null, rank) call - the strongest form of coverage in this file.

        [TestMethod]
        public void NetherBloom_Readout_PrimaryStaysHasValueFalse()
        {
            var ability = new NetherBloomAbility();

            var readout = ability.GetReadout(null, 3);

            Assert.IsFalse(readout.HasValue, "the jump count is a different unit than a live scalar and is already carried by the header's rank number");
        }

        [TestMethod]
        public void NetherBloom_Readout_NoGear_OmitsSecondaryEntirely()
        {
            var ability = new NetherBloomAbility();

            var readout = ability.GetReadout(null, 3);

            Assert.IsNull(readout.Secondary, "a null Player resolves the gear read to 0, which must OMIT the secondary, not report a zero entry");
        }

        [TestMethod]
        public void NetherBloom_GearJumpChanceSecondary_OmittedAtZeroGear()
        {
            Assert.IsFalse(NetherBloomAbility.GearJumpChanceSecondary(0.0).HasValue);
            Assert.IsFalse(NetherBloomAbility.GearJumpChanceSecondary(-1.0).HasValue, "a negative gear read must not produce a negative-percent entry");
        }

        [TestMethod]
        public void NetherBloom_GearJumpChanceSecondary_PresentAndCorrect_WhenGearGrantsAChance()
        {
            var secondary = NetherBloomAbility.GearJumpChanceSecondary(0.25); // 25% extra-jump roll

            Assert.IsTrue(secondary.HasValue);
            var readout = secondary.Value;
            Assert.AreEqual(0.0, readout.Skill, 1e-9);
            Assert.AreEqual(0.0, readout.Affinity, 1e-9);
            Assert.AreEqual(25.0, readout.Gear, 1e-9);
            Assert.AreEqual(25.0, readout.Effective, 1e-9);
            Assert.AreEqual("%", readout.Unit);
            Assert.AreEqual("extra jump", readout.Label);
        }
    }
}
