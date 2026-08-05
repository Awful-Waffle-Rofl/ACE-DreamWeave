using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for Sanguine Ward (Blood Mage T3): the registered tunable defaults and the pure
    /// SanguineWardMath arithmetic that backs Player_ClassAbilityBuffs' transient ward pool.
    ///
    /// The three load-bearing properties from BLOOD-MAGE-DESIGN.md sec 4a-iv each have their own test:
    /// the ward REFRESHES rather than stacks, it EXPIRES UNUSED, and it ABSORBS rather than heals.
    ///
    /// The stateful half (GrantSanguineWard / AbsorbWithSanguineWard / the heartbeat fade) lives on Player
    /// and needs a live Player to exercise - not covered here, same as Frenzy, Nether Rush and Blood Charge.
    /// </summary>
    [TestClass]
    public class SanguineWardTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        // the shipped defaults, used so the math tests read as the design table
        private const double CostR1 = 0.80, CostR2 = 0.65, CostR3 = 0.50;
        private const double AbsorbR1 = 0.50, AbsorbR2 = 0.75, AbsorbR3 = 1.00;
        private const double Duration = 15.0;

        private static SanguineWardMath.WardState WardAt(int rank, uint basis, double now = 1000.0)
        {
            var lost = SanguineWardMath.SelfCost(basis, SanguineWardMath.SelfCostFraction(rank, CostR1, CostR2, CostR3));

            return SanguineWardMath.Grant(default, lost, SanguineWardMath.WardFraction(rank, AbsorbR1, AbsorbR2, AbsorbR3), now, Duration);
        }

        // ---- tunables ----------------------------------------------------------------------------

        [TestMethod]
        public void Tunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.80, D("class_ability_sanguine_ward_selfcost_r1"), 1e-9);
            Assert.AreEqual(0.65, D("class_ability_sanguine_ward_selfcost_r2"), 1e-9);
            Assert.AreEqual(0.50, D("class_ability_sanguine_ward_selfcost_r3"), 1e-9);

            Assert.AreEqual(0.50, D("class_ability_sanguine_ward_absorb_r1"), 1e-9);
            Assert.AreEqual(0.75, D("class_ability_sanguine_ward_absorb_r2"), 1e-9);
            Assert.AreEqual(1.00, D("class_ability_sanguine_ward_absorb_r3"), 1e-9);

            Assert.AreEqual(15.0, D("class_ability_sanguine_ward_duration_seconds"), 1e-9);
        }

        [TestMethod]
        public void SelfCost_FallsWithRank_AndAbsorbRises()
        {
            // LOWER self-cost is stronger; higher absorb is stronger. Both must move monotonically with rank
            // or a rank-up would be a downgrade on one of the two halves.
            var c1 = D("class_ability_sanguine_ward_selfcost_r1");
            var c2 = D("class_ability_sanguine_ward_selfcost_r2");
            var c3 = D("class_ability_sanguine_ward_selfcost_r3");

            Assert.IsTrue(c1 > c2 && c2 > c3, "self-cost must fall with rank");
            Assert.IsTrue(c1 <= 1.0 && c3 > 0.0, "self-cost stays a fraction of the basis and never reaches zero");

            var a1 = D("class_ability_sanguine_ward_absorb_r1");
            var a2 = D("class_ability_sanguine_ward_absorb_r2");
            var a3 = D("class_ability_sanguine_ward_absorb_r3");

            Assert.IsTrue(a1 < a2 && a2 < a3, "ward share must rise with rank");
        }

        // ---- per-rank fractions ------------------------------------------------------------------

        [TestMethod]
        public void SelfCostFraction_IsTheDesignTable_AndUnlearnedPaysFull()
        {
            Assert.AreEqual(0.80, SanguineWardMath.SelfCostFraction(1, CostR1, CostR2, CostR3), 1e-9);
            Assert.AreEqual(0.65, SanguineWardMath.SelfCostFraction(2, CostR1, CostR2, CostR3), 1e-9);
            Assert.AreEqual(0.50, SanguineWardMath.SelfCostFraction(3, CostR1, CostR2, CostR3), 1e-9);

            // rank 0 / unlearned pays the full retail cost, so the cast site can apply this unconditionally
            Assert.AreEqual(1.0, SanguineWardMath.SelfCostFraction(0, CostR1, CostR2, CostR3), 1e-9);
            Assert.AreEqual(1.0, SanguineWardMath.SelfCostFraction(-3, CostR1, CostR2, CostR3), 1e-9);

            // a persisted rank above MaxRank behaves as max rank, never as unlearned
            Assert.AreEqual(0.50, SanguineWardMath.SelfCostFraction(9, CostR1, CostR2, CostR3), 1e-9);
        }

        [TestMethod]
        public void WardFraction_IsTheDesignTable_AndUnlearnedGetsNothing()
        {
            Assert.AreEqual(0.50, SanguineWardMath.WardFraction(1, AbsorbR1, AbsorbR2, AbsorbR3), 1e-9);
            Assert.AreEqual(0.75, SanguineWardMath.WardFraction(2, AbsorbR1, AbsorbR2, AbsorbR3), 1e-9);
            Assert.AreEqual(1.00, SanguineWardMath.WardFraction(3, AbsorbR1, AbsorbR2, AbsorbR3), 1e-9);

            Assert.AreEqual(0.0, SanguineWardMath.WardFraction(0, AbsorbR1, AbsorbR2, AbsorbR3), 1e-9);
            Assert.AreEqual(0.0, SanguineWardMath.WardFraction(-1, AbsorbR1, AbsorbR2, AbsorbR3), 1e-9);
            Assert.AreEqual(1.00, SanguineWardMath.WardFraction(9, AbsorbR1, AbsorbR2, AbsorbR3), 1e-9);
        }

        // ---- ward size per rank ------------------------------------------------------------------

        [TestMethod]
        public void WardSize_PerRank_OnA1000PointBasis()
        {
            // a Hecatomb basis of 1000: the caster loses 800/650/500 and wards 50/75/100% of what was lost
            Assert.AreEqual(800u, SanguineWardMath.SelfCost(1000, SanguineWardMath.SelfCostFraction(1, CostR1, CostR2, CostR3)));
            Assert.AreEqual(650u, SanguineWardMath.SelfCost(1000, SanguineWardMath.SelfCostFraction(2, CostR1, CostR2, CostR3)));
            Assert.AreEqual(500u, SanguineWardMath.SelfCost(1000, SanguineWardMath.SelfCostFraction(3, CostR1, CostR2, CostR3)));

            Assert.AreEqual(400u, WardAt(1, 1000).Amount);   // 50% of 800
            Assert.AreEqual(488u, WardAt(2, 1000).Amount);   // 75% of 650 = 487.5, rounded away from zero
            Assert.AreEqual(500u, WardAt(3, 1000).Amount);   // 100% of 500

            // the whole basis is still available to the spell as damage - none of this touches it, and the
            // ward is never larger than what was actually paid
            Assert.IsTrue(WardAt(3, 1000).Amount <= SanguineWardMath.SelfCost(1000, 0.50));
        }

        [TestMethod]
        public void UnlearnedCaster_PaysTheFullBasisAndGetsNoWard()
        {
            Assert.AreEqual(1000u, SanguineWardMath.SelfCost(1000, SanguineWardMath.SelfCostFraction(0, CostR1, CostR2, CostR3)));

            var ward = WardAt(0, 1000);

            Assert.AreEqual(0u, ward.Amount, "a caster without Sanguine Ward gets no ward at all");

            // and an incoming hit is completely unaffected by the absent ward
            var hit = SanguineWardMath.Absorb(ward, 250, 1000.0);

            Assert.AreEqual(0u, hit.Absorbed);
            Assert.AreEqual(250u, hit.DamageAfterWard);
        }

        [TestMethod]
        public void ZeroBasisOrZeroDuration_GrantsNothing()
        {
            Assert.AreEqual(0u, WardAt(3, 0).Amount);
            Assert.AreEqual(0u, SanguineWardMath.Grant(default, 500, 1.0, 1000.0, 0.0).Amount);
        }

        // ---- property 1: REFRESH, NOT STACK ------------------------------------------------------

        [TestMethod]
        public void Ward_Refreshes_ItDoesNotStack()
        {
            var first = WardAt(3, 1000, now: 1000.0);
            Assert.AreEqual(500u, first.Amount);

            // recast 2s later, untouched, at a lower basis: the new ward REPLACES the old one. If it stacked
            // this would be 500 + 150 = 650, which is exactly the spiral 4a-iv exists to prevent.
            var second = SanguineWardMath.Grant(first, 300, 0.50, 1002.0, Duration);

            Assert.AreEqual(150u, second.Amount, "a recast must replace the running ward, never add to it");
            Assert.AreEqual(1002.0 + Duration, second.ExpireTime, 1e-9, "the timer restarts from the recast");
        }

        [TestMethod]
        public void Ward_Refreshes_EvenWhenPartiallySpent()
        {
            var ward = WardAt(3, 1000, now: 1000.0);

            var afterHit = SanguineWardMath.Absorb(ward, 300, 1005.0).Remaining;
            Assert.AreEqual(200u, afterHit.Amount);

            // the 200 still running is discarded, not carried into the new ward
            var recast = SanguineWardMath.Grant(afterHit, 500, 1.00, 1006.0, Duration);

            Assert.AreEqual(500u, recast.Amount, "the unspent remainder of the old ward is discarded on recast");
        }

        [TestMethod]
        public void Ward_ChainedCasts_NeverExceedOneCastsWorth()
        {
            var ward = default(SanguineWardMath.WardState);

            // five Hecatombs back to back at rank 3, 500 lost each: the ward never climbs past one cast
            for (var i = 0; i < 5; i++)
            {
                ward = SanguineWardMath.Grant(ward, 500, 1.00, 1000.0 + i, Duration);
                Assert.AreEqual(500u, ward.Amount, $"cast {i + 1} must not compound");
            }
        }

        // ---- property 2: EXPIRES UNUSED ----------------------------------------------------------

        [TestMethod]
        public void Ward_ExpiresUnused_WithNoRefund()
        {
            var ward = WardAt(3, 1000, now: 1000.0);

            Assert.IsFalse(SanguineWardMath.IsExpired(ward, 1014.9), "still live one tenth of a second inside the window");
            Assert.IsTrue(SanguineWardMath.IsExpired(ward, 1015.1), "lapsed past 15s");

            // a hit past the window absorbs NOTHING and the remainder is dropped - not refunded, not healed,
            // not carried forward
            var hit = SanguineWardMath.Absorb(ward, 400, 1015.1);

            Assert.AreEqual(0u, hit.Absorbed);
            Assert.AreEqual(400u, hit.DamageAfterWard, "the full hit lands once the ward has lapsed");
            Assert.AreEqual(0u, hit.Remaining.Amount, "an expired ward is gone, not banked");
        }

        [TestMethod]
        public void EmptyWard_CountsAsExpired()
        {
            Assert.IsTrue(SanguineWardMath.IsExpired(default, 0.0));
            Assert.IsTrue(SanguineWardMath.IsExpired(new SanguineWardMath.WardState { Amount = 0, ExpireTime = double.MaxValue }, 0.0));
        }

        // ---- property 3: ABSORBS, DOES NOT HEAL --------------------------------------------------

        [TestMethod]
        public void Ward_AbsorbsIncomingDamage_ItNeverRestoresHealth()
        {
            var ward = WardAt(3, 1000, now: 1000.0);   // 500 absorb

            var hit = SanguineWardMath.Absorb(ward, 120, 1001.0);

            // the ONLY output that can reach a vital is a REDUCED incoming damage figure. There is no
            // "health restored" term anywhere in the result, by construction.
            Assert.AreEqual(120u, hit.Absorbed);
            Assert.AreEqual(0u, hit.DamageAfterWard);
            Assert.AreEqual(380u, hit.Remaining.Amount);

            // a hit larger than the ward is only ever REDUCED, never reversed - damage after the ward is
            // still positive, so the caster keeps taking damage at low health
            var overkill = SanguineWardMath.Absorb(ward, 900, 1001.0);

            Assert.AreEqual(500u, overkill.Absorbed);
            Assert.AreEqual(400u, overkill.DamageAfterWard, "the excess still lands on Health");
            Assert.AreEqual(0u, overkill.Remaining.Amount);
        }

        [TestMethod]
        public void Ward_AbsorbsPartially_AcrossSeveralHits()
        {
            var ward = WardAt(3, 1000, now: 1000.0);   // 500 absorb
            Assert.AreEqual(500u, ward.Amount);

            var hit1 = SanguineWardMath.Absorb(ward, 200, 1001.0);
            Assert.AreEqual(200u, hit1.Absorbed);
            Assert.AreEqual(0u, hit1.DamageAfterWard);
            Assert.AreEqual(300u, hit1.Remaining.Amount);
            Assert.AreEqual(ward.ExpireTime, hit1.Remaining.ExpireTime, 1e-9, "spending the ward must not extend it");

            var hit2 = SanguineWardMath.Absorb(hit1.Remaining, 250, 1003.0);
            Assert.AreEqual(250u, hit2.Absorbed);
            Assert.AreEqual(0u, hit2.DamageAfterWard);
            Assert.AreEqual(50u, hit2.Remaining.Amount);

            // third hit breaks through: 50 absorbed, 150 lands, ward gone
            var hit3 = SanguineWardMath.Absorb(hit2.Remaining, 200, 1004.0);
            Assert.AreEqual(50u, hit3.Absorbed);
            Assert.AreEqual(150u, hit3.DamageAfterWard);
            Assert.AreEqual(0u, hit3.Remaining.Amount);

            // fourth hit is completely unwarded
            var hit4 = SanguineWardMath.Absorb(hit3.Remaining, 200, 1005.0);
            Assert.AreEqual(0u, hit4.Absorbed);
            Assert.AreEqual(200u, hit4.DamageAfterWard);

            // total absorbed across the whole sequence is exactly one ward, never more
            Assert.AreEqual(500u, hit1.Absorbed + hit2.Absorbed + hit3.Absorbed + hit4.Absorbed);
        }

        [TestMethod]
        public void ZeroDamageHit_LeavesTheWardIntact()
        {
            // a hit fully mitigated to 0 still reaches the take-damage path - it must not consume the ward
            var ward = WardAt(3, 1000, now: 1000.0);

            var hit = SanguineWardMath.Absorb(ward, 0, 1001.0);

            Assert.AreEqual(0u, hit.Absorbed);
            Assert.AreEqual(0u, hit.DamageAfterWard);
            Assert.AreEqual(500u, hit.Remaining.Amount);
            Assert.AreEqual(ward.ExpireTime, hit.Remaining.ExpireTime, 1e-9);
        }

        // ---- SanguineWardAbility.SelfCost: the engine-facing wrapper -----------------------------

        /// <summary>
        /// Hecatomb is castable by monsters, and a null caster must pay the FULL basis - the same
        /// behaviour the inline `?? 1.0` at the old WorldObject_Magic call site produced. This is the one
        /// branch of <see cref="SanguineWardAbility.SelfCost"/> testable without a live Player:
        /// passing null never touches a Player instance member, it only takes the null-conditional's own
        /// short-circuit. The non-null branch needs a real Player.GetSanguineWardSelfCostFraction() and is
        /// not reachable here - Player's static initializer cannot run under the test host.
        /// </summary>
        [TestMethod]
        public void SelfCost_NullCaster_PaysTheFullBasis()
        {
            Assert.AreEqual(1000u, SanguineWardAbility.SelfCost(null, 1000),
                "a monster (or any null caster) casting a Hecatomb-shaped spell must pay the full damage basis, matching the pre-Sanguine-Ward retail cost.");

            Assert.AreEqual(0u, SanguineWardAbility.SelfCost(null, 0));
        }
    }
}
