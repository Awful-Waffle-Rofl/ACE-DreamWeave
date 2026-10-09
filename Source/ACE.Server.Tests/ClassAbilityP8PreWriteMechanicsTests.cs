using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The arithmetic of the four 2026-09-12 abilities that ride the pre-write damage hook: Adrenaline
    /// (Berserker T1), Kinetic Charge (Vanguard T2), Runic Ward (Spellsword T2) and Soul Jump
    /// (Void/Summon T3). Their ORDER on that hook is pinned separately, in PreWriteDamageHookTests.
    ///
    /// EXERCISED THROUGH THE PURE STATICS, never through a live Player: Player's static initializer cannot
    /// run under this test host, which is why each ability's magnitude lives in a pure function that both
    /// its combat path and its /abilities readout call. The same constraint ManaBarrierTests and
    /// SanguineWardTests already record.
    ///
    /// Every affinity assertion checks BOTH directions of the multiplicative model: at a factor of exactly
    /// 1.0 (an Untrained or Inactive affinity skill, which is what Player.GetClassAbilityAffinityMultiplier
    /// returns there) the output must be bit-identical to rank alone, and above 1.0 the factor must scale
    /// the ability's OWN rank bonus rather than adding a separate rider.
    /// </summary>
    [TestClass]
    public class ClassAbilityP8PreWriteMechanicsTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        // ---- Adrenaline ------------------------------------------------------------------------------

        [TestMethod]
        public void Adrenaline_BonusIsTwoPercentPerRank_AndZeroUnlearned()
        {
            var perRank = D("class_ability_adrenaline_percent_per_rank");

            Assert.AreEqual(0.0, AdrenalineAbility.DamageBonus(0, perRank, 1.0), 1e-9, "rank 0 arms nothing worth anything");
            Assert.AreEqual(0.02, AdrenalineAbility.DamageBonus(1, perRank, 1.0), 1e-9);
            Assert.AreEqual(0.10, AdrenalineAbility.DamageBonus(5, perRank, 1.0), 1e-9, "+10% at rank 5, the signed-off top end");
        }

        [TestMethod]
        public void Adrenaline_RecklessnessMultipliesTheRankBonusOnly()
        {
            var perRank = D("class_ability_adrenaline_percent_per_rank");

            // a factor of exactly 1.0 is what a zero / Untrained / Inactive affinity skill produces
            Assert.AreEqual(AdrenalineAbility.DamageBonus(5, perRank, 1.0), 5 * perRank, 1e-12,
                "at zero effective Recklessness the bonus must be bit-identical to rank alone");

            // 200 points of a Specialized source at the shared 0.17 rate = 1.34x the ability's own bonus
            Assert.AreEqual(0.134, AdrenalineAbility.DamageBonus(5, perRank, 1.34), 1e-9);

            // and it scales the rank term, so it is worth nothing at rank 0 - the whole point of the
            // multiplicative model over an additive rider
            Assert.AreEqual(0.0, AdrenalineAbility.DamageBonus(0, perRank, 1.34), 1e-12);
        }

        // ---- Kinetic Charge --------------------------------------------------------------------------

        [TestMethod]
        public void KineticCharge_SpendBonusIsTwentyPercentPerRank()
        {
            var perRank = D("class_ability_kineticcharge_percent_per_rank");

            Assert.AreEqual(0.0, KineticChargeAbility.SpendBonus(0, perRank, 1.0), 1e-9);
            Assert.AreEqual(0.20, KineticChargeAbility.SpendBonus(1, perRank, 1.0), 1e-9);
            Assert.AreEqual(0.60, KineticChargeAbility.SpendBonus(3, perRank, 1.0), 1e-9, "+60% at rank 3");
        }

        [TestMethod]
        public void KineticCharge_ArmorTinkeringMultipliesTheRankBonusOnly()
        {
            var perRank = D("class_ability_kineticcharge_percent_per_rank");

            Assert.AreEqual(KineticChargeAbility.SpendBonus(3, perRank, 1.0), 3 * perRank, 1e-12,
                "at zero effective Armor Tinkering the bonus must be bit-identical to rank alone");

            Assert.AreEqual(0.804, KineticChargeAbility.SpendBonus(3, perRank, 1.34), 1e-9);
            Assert.AreEqual(0.0, KineticChargeAbility.SpendBonus(0, perRank, 1.34), 1e-12);
        }

        [TestMethod]
        public void KineticCharge_FlooredCleaveTargets_ClampsNegativeToZero()
        {
            Assert.AreEqual(2, KineticChargeAbility.FlooredCleaveTargets(2));
            Assert.AreEqual(0, KineticChargeAbility.FlooredCleaveTargets(0));
            Assert.AreEqual(0, KineticChargeAbility.FlooredCleaveTargets(-5));
        }

        [TestMethod]
        public void KineticCharge_CleaveHitDamageMultiplier_OnlyAppliesWhileResolvingACleaveHitWithARecordedBonus()
        {
            // not a cleave hit at all (the primary strike itself) - always a no-op here, regardless of what
            // happens to be recorded, since the primary applies its own bonus directly rather than through
            // this method
            Assert.AreEqual(1.0, KineticChargeAbility.CleaveHitDamageMultiplier(false, 0.40), 1e-12);
            Assert.AreEqual(1.0, KineticChargeAbility.CleaveHitDamageMultiplier(false, 0.0), 1e-12);

            // a cleave hit whose strike's primary spent a full stack for +40%
            Assert.AreEqual(1.40, KineticChargeAbility.CleaveHitDamageMultiplier(true, 0.40), 1e-12);

            // a cleave hit whose strike's primary MISSED - nothing was ever spent, recordedBonus stays 0.0
            Assert.AreEqual(1.0, KineticChargeAbility.CleaveHitDamageMultiplier(true, 0.0), 1e-12);
        }

        [TestMethod]
        public void KineticCharge_TotalCleaveTargets_StacksWeaponWhirlwindAndKinetic()
        {
            // no weapon cleave, no Whirlwind, no Kinetic spend -> no cleave at all
            Assert.AreEqual(0, Creature.TotalCleaveTargets(0, false, 0));

            // a non-cleaving weapon still cleaves for exactly the Kinetic Charge extra
            Assert.AreEqual(2, Creature.TotalCleaveTargets(0, false, 2));

            // a two-handed cleaving weapon (1), Whirlwind's +1 and a full Kinetic spend (2) all stack
            Assert.AreEqual(4, Creature.TotalCleaveTargets(1, true, 2));

            // a negative Kinetic extra (should never happen - FlooredCleaveTargets guards the call site) is
            // floored at 0 here too, as a second line of defense
            Assert.AreEqual(1, Creature.TotalCleaveTargets(1, false, -3));
        }

        // ---- Runic Ward ------------------------------------------------------------------------------

        [TestMethod]
        public void RunicWard_GainIsThreeFiveSevenPercentByRank()
        {
            var gainBase = D("class_ability_runicward_gain_base");
            var gainStep = D("class_ability_runicward_gain_step");

            Assert.AreEqual(0.0, RunicWardMath.GainFraction(0, gainBase, gainStep, 1.0), 1e-9);
            Assert.AreEqual(0.03, RunicWardMath.GainFraction(1, gainBase, gainStep, 1.0), 1e-9);
            Assert.AreEqual(0.05, RunicWardMath.GainFraction(2, gainBase, gainStep, 1.0), 1e-9);
            Assert.AreEqual(0.07, RunicWardMath.GainFraction(3, gainBase, gainStep, 1.0), 1e-9);
        }

        [TestMethod]
        public void RunicWard_ItemTinkeringMultipliesTheRankGainOnly()
        {
            var gainBase = D("class_ability_runicward_gain_base");
            var gainStep = D("class_ability_runicward_gain_step");

            Assert.AreEqual(RunicWardMath.GainFraction(3, gainBase, gainStep, 1.0), 0.07, 1e-12,
                "at zero effective Item Tinkering the gain must be bit-identical to rank alone");

            Assert.AreEqual(0.0938, RunicWardMath.GainFraction(3, gainBase, gainStep, 1.34), 1e-9);
            Assert.AreEqual(0.0, RunicWardMath.GainFraction(0, gainBase, gainStep, 1.34), 1e-12);
        }

        [TestMethod]
        public void RunicWard_PoolIsCappedAtFifteenPercentOfMaxHealth()
        {
            var capFraction = D("class_ability_runicward_cap_fraction");

            Assert.AreEqual(0.15, capFraction, 1e-9);
            Assert.AreEqual(75u, RunicWardMath.Cap(500, capFraction));
            Assert.AreEqual(0u, RunicWardMath.Cap(0, capFraction), "no maximum health, no ward");
        }

        [TestMethod]
        public void RunicWard_AccumulatesAcrossHits_AndStopsAtTheCap()
        {
            const double now = 100.0;

            // 30 points of a 1000 damage hit at the rank 3 gain, against a 75 point ceiling
            var first = RunicWardMath.Inscribe(default, 1000, 0.03, 75, now, 12.0);
            Assert.AreEqual(30u, first.Amount);
            Assert.AreEqual(112.0, first.ExpireTime, 1e-9, "each hit refreshes the 12 second window");

            var second = RunicWardMath.Inscribe(first, 1000, 0.03, 75, now + 1.0, 12.0);
            Assert.AreEqual(60u, second.Amount, "it ACCUMULATES - unlike Sanguine Ward, which refreshes instead");

            var third = RunicWardMath.Inscribe(second, 1000, 0.03, 75, now + 2.0, 12.0);
            Assert.AreEqual(75u, third.Amount, "a hit that would overflow tops the ward off rather than being wasted");

            var fourth = RunicWardMath.Inscribe(third, 1000, 0.03, 75, now + 3.0, 12.0);
            Assert.AreEqual(75u, fourth.Amount, "and a full ward still refreshes its window");
            Assert.AreEqual(115.0, fourth.ExpireTime, 1e-9);
        }

        [TestMethod]
        public void RunicWard_LapsedPoolStartsAgainFromZero_AndAbsorbsNothing()
        {
            const double now = 100.0;

            var ward = RunicWardMath.Inscribe(default, 1000, 0.05, 500, now, 12.0);
            Assert.AreEqual(50u, ward.Amount);

            Assert.IsTrue(RunicWardMath.IsExpired(ward, now + 12.5), "12 seconds without a weapon hit and it is gone");

            var afterLapse = RunicWardMath.Absorb(ward, 40, now + 12.5);
            Assert.AreEqual(0u, afterLapse.Absorbed, "a lapsed ward absorbs nothing");
            Assert.AreEqual(40u, afterLapse.DamageAfterWard);
            Assert.AreEqual(0u, afterLapse.Remaining.Amount);

            var reinscribed = RunicWardMath.Inscribe(ward, 1000, 0.05, 500, now + 12.5, 12.0);
            Assert.AreEqual(50u, reinscribed.Amount, "a hit after the window starts a new ward rather than reviving the stale one");
        }

        [TestMethod]
        public void RunicWard_DrainsPartially_AcrossSeveralHits()
        {
            const double now = 50.0;

            var ward = RunicWardMath.Inscribe(default, 1000, 0.10, 500, now, 12.0);
            Assert.AreEqual(100u, ward.Amount);

            var first = RunicWardMath.Absorb(ward, 30, now + 1.0);
            Assert.AreEqual(30u, first.Absorbed);
            Assert.AreEqual(0u, first.DamageAfterWard);
            Assert.AreEqual(70u, first.Remaining.Amount);
            Assert.AreEqual(ward.ExpireTime, first.Remaining.ExpireTime, 1e-9, "draining must not move the window");

            var second = RunicWardMath.Absorb(first.Remaining, 200, now + 2.0);
            Assert.AreEqual(70u, second.Absorbed);
            Assert.AreEqual(130u, second.DamageAfterWard, "the ward never absorbs more than it holds");
            Assert.AreEqual(0u, second.Remaining.Amount);
        }

        [TestMethod]
        public void RunicWard_SpendIsAllOrNothing_AndEmptyOnceLapsed()
        {
            const double now = 10.0;

            var ward = RunicWardMath.Inscribe(default, 1000, 0.07, 500, now, 12.0);
            Assert.AreEqual(70u, ward.Amount);

            Assert.AreEqual(70u, RunicWardMath.Spend(ward, now + 5.0), "a landed war spell cashes the WHOLE pool");
            Assert.AreEqual(0u, RunicWardMath.Spend(ward, now + 13.0), "a lapsed ward is worth nothing to cash");
            Assert.AreEqual(0u, RunicWardMath.Spend(default, now), "and an empty one likewise");
        }

        // ---- Runic Ward chat feedback -----------------------------------------------------------------

        [TestMethod]
        public void RunicWard_FullyInscribedMessage_ExactText()
        {
            Assert.AreEqual("Your runic ward is fully inscribed, absorbing your next 75 points of damage.",
                RunicWardMessages.FullyInscribedMessage(75));
        }

        [TestMethod]
        public void RunicWard_ShatterMessage_ExactText()
        {
            Assert.AreEqual("Your runic ward shatters after absorbing 142 points of damage.",
                RunicWardMessages.ShatterMessage(142));
        }

        [TestMethod]
        public void RunicWard_FadeMessage_ExactText_WithAndWithoutAbsorbs()
        {
            Assert.AreEqual("Your runic ward fades after absorbing 60 points of damage.",
                RunicWardMessages.FadeMessage(60));
            Assert.AreEqual("Your runic ward fades.", RunicWardMessages.FadeMessage(0));
        }

        [TestMethod]
        public void RunicWard_DischargeMessage_ExactText()
        {
            Assert.AreEqual("Your runic ward discharges 75 points of damage into Blast Wave!",
                RunicWardMessages.DischargeMessage(75, "Blast Wave"));
        }

        [TestMethod]
        public void RunicWard_ShouldAnnounceFullyInscribed_LifetimeCases()
        {
            // empty -> partial: below cap, no announce
            Assert.IsFalse(RunicWardMessages.ShouldAnnounceFullyInscribed(after: 40, cap: 75, alreadyAnnouncedThisLifetime: false));

            // empty -> cap in one hit: announce
            Assert.IsTrue(RunicWardMessages.ShouldAnnounceFullyInscribed(after: 75, cap: 75, alreadyAnnouncedThisLifetime: false));

            // partial -> partial: still below cap, no announce
            Assert.IsFalse(RunicWardMessages.ShouldAnnounceFullyInscribed(after: 60, cap: 75, alreadyAnnouncedThisLifetime: false));

            // partial -> cap, not yet announced this lifetime: announce
            Assert.IsTrue(RunicWardMessages.ShouldAnnounceFullyInscribed(after: 75, cap: 75, alreadyAnnouncedThisLifetime: false));

            // cap -> cap, already announced this lifetime (e.g. drained then topped back up): no second announce
            Assert.IsFalse(RunicWardMessages.ShouldAnnounceFullyInscribed(after: 75, cap: 75, alreadyAnnouncedThisLifetime: true));

            // no cap configured: never announce
            Assert.IsFalse(RunicWardMessages.ShouldAnnounceFullyInscribed(after: 0, cap: 0, alreadyAnnouncedThisLifetime: false));
        }

        // ---- Soul Jump -------------------------------------------------------------------------------

        [TestMethod]
        public void SoulJump_CooldownIsSixFourTwoMinutesByRank_WithNoAffinityTerm()
        {
            var baseSeconds = D("class_ability_souljump_cooldown_seconds_base");
            var stepSeconds = D("class_ability_souljump_cooldown_seconds_step");

            Assert.AreEqual(360.0, SoulJumpMath.CooldownSeconds(1, baseSeconds, stepSeconds, SoulJumpAbility.MinimumCooldownSeconds), 1e-9);
            Assert.AreEqual(240.0, SoulJumpMath.CooldownSeconds(2, baseSeconds, stepSeconds, SoulJumpAbility.MinimumCooldownSeconds), 1e-9);
            Assert.AreEqual(120.0, SoulJumpMath.CooldownSeconds(3, baseSeconds, stepSeconds, SoulJumpAbility.MinimumCooldownSeconds), 1e-9);

            Assert.AreEqual(0.0, SoulJumpMath.CooldownSeconds(0, baseSeconds, stepSeconds, SoulJumpAbility.MinimumCooldownSeconds), 1e-9);
        }

        [TestMethod]
        public void SoulJump_CooldownCannotReachZeroOrGoNegativeOnAMisTunedStep()
        {
            // a step steeper than the base drives the rank 3 figure negative, which would read as
            // "always ready" - the floor is what stops that
            var floored = SoulJumpMath.CooldownSeconds(3, 360.0, -200.0, SoulJumpAbility.MinimumCooldownSeconds);

            Assert.AreEqual(SoulJumpAbility.MinimumCooldownSeconds, floored, 1e-9);
            Assert.IsTrue(floored > 0.0, "a cooldown must never be zero or negative");
        }

        [TestMethod]
        public void SoulJump_JumpMultipliesTheRestoredHealth()
        {
            var baseFraction = D("class_ability_souljump_restore_fraction");

            Assert.AreEqual(0.10, baseFraction, 1e-9);

            Assert.AreEqual(baseFraction, SoulJumpMath.RestoreFraction(baseFraction, 1.0), 1e-12,
                "at zero effective Jump the restore must be bit-identical to the bare tunable");

            Assert.AreEqual(0.114, SoulJumpMath.RestoreFraction(baseFraction, 1.14), 1e-9);
            Assert.AreEqual(0.11, SoulJumpMath.RestoreFraction(baseFraction, 1.10), 1e-9);
        }

        /// <summary>
        /// Soul Jump's own off-standard rate pair was RETIRED by the 2026-10-02 owner ruling - it now
        /// reads the SHARED pair directly via the single-argument GetClassAbilityAffinityMultiplier(Skill)
        /// overload (<see cref="Player_ClassAbilities.GetClassAbilityAffinityMultiplier(ACE.Entity.Enum.Skill)"/>),
        /// which in turn calls the three-argument overload with
        /// class_ability_affinity_rate_per_trained / _per_spec. This test proves that WIRING rather than
        /// pinning today's shared-rate number: it reads the shared pair from PropertyManager (never a
        /// literal 0.12/0.17) and recomputes the restore the same way
        /// <see cref="Player_ClassAbilities.GetClassAbilityAffinityMultiplier(ACE.Entity.Enum.Skill, double, double)"/>
        /// -> <see cref="ClassAbilityAffinity.Multiplier"/> does, so it keeps passing at any shared rate,
        /// including after the shared pair itself is retuned (owner-ruled 0.09/0.14 landing separately on
        /// another branch). A hardcoded 0.12/0.17 here would re-create exactly the coupling this ruling
        /// was meant to eliminate.
        /// </summary>
        [TestMethod]
        public void SoulJump_RestoreScalesWithTheLiveSharedAffinityRate_NotAPinnedLiteral()
        {
            var baseFraction = D("class_ability_souljump_restore_fraction");
            var sharedTrained = D("class_ability_affinity_rate_per_trained");
            var sharedSpec = D("class_ability_affinity_rate_per_spec");

            // Trained and Specialized at a skill value chosen only to be non-trivial (200 points);
            // the point is that both sides of the comparison derive from the SAME live shared values.
            var trainedMultiplier = ClassAbilityAffinity.Multiplier(200.0, isSpecialized: false, sharedTrained, sharedSpec);
            var specializedMultiplier = ClassAbilityAffinity.Multiplier(200.0, isSpecialized: true, sharedTrained, sharedSpec);

            var expectedTrainedRestore = baseFraction * trainedMultiplier;
            var expectedSpecializedRestore = baseFraction * specializedMultiplier;

            Assert.AreEqual(expectedTrainedRestore, SoulJumpMath.RestoreFraction(baseFraction, trainedMultiplier), 1e-12,
                "Soul Jump's restore must equal baseFraction times the SHARED multiplier, whatever the shared rate currently is");
            Assert.AreEqual(expectedSpecializedRestore, SoulJumpMath.RestoreFraction(baseFraction, specializedMultiplier), 1e-12,
                "Soul Jump's restore must equal baseFraction times the SHARED multiplier, whatever the shared rate currently is");

            // Negative control: the Specialized multiplier must differ from the Trained one whenever the
            // shared pair's two rates differ, so this is not a test that would pass by coincidence if the
            // Trained/Specialized selection were ever broken.
            if (sharedTrained != sharedSpec)
                Assert.AreNotEqual(trainedMultiplier, specializedMultiplier,
                    "Trained and Specialized must select different rates when the shared pair's two rates differ");
        }

        [TestMethod]
        public void SoulJump_LeavesTheSummonerOnExactlyTheRestoreLine()
        {
            // above the line: the save is paid for in damage, so the ordinary vital write lands on it and no
            // health is ever conjured
            var fromFull = SoulJumpMath.Resolve(currentHealth: 400, maxHealth: 500, restoreFraction: 0.10);
            Assert.AreEqual(50u, fromFull.HealthAfterSave);
            Assert.AreEqual(350u, fromFull.DamageAfterSave, "400 health minus 350 damage lands exactly on the 50 point line");

            // below the line: the damage is zeroed and the handler tops the shortfall up
            var fromLow = SoulJumpMath.Resolve(currentHealth: 20, maxHealth: 500, restoreFraction: 0.10);
            Assert.AreEqual(50u, fromLow.HealthAfterSave);
            Assert.AreEqual(0u, fromLow.DamageAfterSave);

            // a bigger affinity-scaled fraction restores more, which is the whole payoff of the rider
            var scaled = SoulJumpMath.Resolve(currentHealth: 400, maxHealth: 500, restoreFraction: 0.114);
            Assert.AreEqual(57u, scaled.HealthAfterSave);
            Assert.AreEqual(343u, scaled.DamageAfterSave);
        }

        [TestMethod]
        public void SoulJump_RestoreIsNeverZeroAndNeverAboveMaximum()
        {
            Assert.AreEqual(1u, SoulJumpMath.RestoreAmount(5, 0.10), "a save that left the summoner on 0 health would be no save");
            Assert.AreEqual(500u, SoulJumpMath.RestoreAmount(500, 2.0), "and it can never exceed maximum health");
            Assert.AreEqual(0u, SoulJumpMath.RestoreAmount(0, 0.10));
        }
    }
}
