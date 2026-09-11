using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for Mana Barrier (Archmage T2): the registered tunable defaults and the pure
    /// ManaBarrierAbility.AbsorbDamage arithmetic that backs Player.AbsorbWithManaBarrier.
    ///
    /// THE 2026-09-08 OVERKILL BUG IS PINNED HERE AND NOWHERE ELSE, so do not delete these. The barrier
    /// used to be a REFUND applied after each site's health write. Every health write clamps at zero, so on
    /// an overkill hit the barrier was handed the victim's CURRENT HEALTH rather than the damage thrown; it
    /// refunded a share of the smaller number on top of an emptied health bar and the player survived. The
    /// fix is structural: AbsorbDamage takes no health input at all, and every caller applies its result
    /// BEFORE the vital write.
    ///
    /// The stateful half (Player.AbsorbWithManaBarrier / AbsorbWithManaBarrierDot, the Mana spend and the
    /// absorb chat line) lives on Player and needs a live Player to exercise - not covered here, the same
    /// limit SanguineWardTests records for AbsorbWithSanguineWard.
    ///
    /// NOR IS THE DoT CALL SITE COVERED END TO END. EnchantmentManager.ApplyDamageTick resolves each
    /// damager through WorldObject.CurrentLandblock.GetObject, so with no landblock every enchantment is
    /// skipped and the method does nothing at all - it cannot be driven from this harness, and a victim
    /// Player cannot be constructed here either. The two halves of that site are pinned separately instead:
    /// AbsorbDamage_ClampBeforeAbsorbIsWhatSpares_TheDoTOverkillCase below pins the clamp-order arithmetic,
    /// and DotTickCreditsTests pins the per-damager attribution that the restructure had to preserve. What
    /// remains unpinned is the wiring between them - that the absorbers really are called above the clamp
    /// and the credits really are fed from the split - which is verified by reading ApplyDamageTick.
    /// </summary>
    [TestClass]
    public class ManaBarrierTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        // ---- tunables ----------------------------------------------------------------------------

        [TestMethod]
        public void Tunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.10, D("class_ability_manabarrier_base"), 1e-9);
            Assert.AreEqual(0.075, D("class_ability_manabarrier_step"), 1e-9);
            Assert.AreEqual(0.60, D("class_ability_manabarrier_max_share"), 1e-9);
            Assert.AreEqual(1.0, D("class_ability_manabarrier_mana_per_health"), 1e-9);

            Assert.AreEqual(25.0, D("class_ability_manabarrier_magicdef_per_trained"), 1e-9);
            Assert.AreEqual(18.0, D("class_ability_manabarrier_magicdef_per_spec"), 1e-9);
        }

        // ---- the reported live case ---------------------------------------------------------------

        /// <summary>
        /// The 2026-09-08 player report, by its own numbers: 477 Health, a 942-damage critical melee hit,
        /// and a barrier share of 0.3942 (the share is recoverable from the two absorb lines in the same
        /// chat log - 188/477 and 123/312 both give 0.3942). The old refund shape absorbed against the
        /// CLAMPED 477 and the player lived. The reduction shape absorbs against the 942 actually thrown
        /// and leaves 571 to reach Health, which is more than the 477 they had - the hit stays lethal.
        ///
        /// 571 is asserted as the arithmetic of the contract, not as a magic constant: the barrier takes
        /// floor(942 * 0.3942) = 371 and DamageAfter is what is left of the hit.
        /// </summary>
        [TestMethod]
        public void AbsorbDamage_LeavesAnOverkillHitLethal_The20260908OverkillBug()
        {
            const uint incoming = 942;
            const uint victimHealth = 477;
            const double share = 0.3942;

            var absorbed = ManaBarrierAbility.AbsorbDamage(incoming, share, currentMana: 100000, manaPerHealth: 1.0);

            // derived from the contract: the share of the HIT, floored, is what the barrier eats
            var expectedAbsorbed = (uint)System.Math.Floor(incoming * share);
            var expectedAfter = incoming - expectedAbsorbed;

            Assert.AreEqual(expectedAbsorbed, absorbed.DamageAbsorbed);
            Assert.AreEqual(expectedAfter, absorbed.DamageAfter);
            Assert.AreEqual(571u, absorbed.DamageAfter, "the contract's arithmetic must still land on the reported case");

            Assert.IsTrue(absorbed.DamageAfter > victimHealth,
                $"a {incoming} hit must stay lethal against {victimHealth} health: {absorbed.DamageAfter} reached Health");
        }

        /// <summary>
        /// The DoT half of the same bug, found in code review after the first fix landed. Taking no health
        /// input is necessary but NOT sufficient: a CALLER that clamps before it calls reproduces the defect
        /// exactly. EnchantmentManager.ApplyDamageTick used to cap its accumulated tick total to the
        /// victim's current Health before the absorb ran, so the barrier was handed a health-clamped figure
        /// after all.
        ///
        /// Both halves are pinned here as arithmetic, since the surrounding method needs a live Player and
        /// landblock (see the class doc comment): the fixed order is lethal and the old order is not, on the
        /// same numbers.
        /// </summary>
        [TestMethod]
        public void AbsorbDamage_ClampBeforeAbsorbIsWhatSpares_TheDoTOverkillCase()
        {
            const uint victimHealth = 300;
            const uint rawTick = 5000;
            const double share = 0.30;

            // FIXED ORDER: absorb the raw tick, then let the vital write floor at zero. 3500 reaches Health
            // against 300 available, so the tick kills.
            var absorbedRaw = ManaBarrierAbility.AbsorbDamage(rawTick, share, currentMana: 100000, manaPerHealth: 1.0);

            Assert.AreEqual(1500u, absorbedRaw.DamageAbsorbed);
            Assert.AreEqual(3500u, absorbedRaw.DamageAfter);
            Assert.IsTrue(absorbedRaw.DamageAfter > victimHealth,
                $"the raw {rawTick} tick must stay lethal against {victimHealth} health");

            // OLD ORDER, kept as a counterexample: cap the tick to current Health first, then absorb. The
            // result is strictly less than the health it was capped to, so the victim always survives - and
            // this holds for ANY nonzero share and ANY tick size, which is the whole defect.
            var absorbedClamped = ManaBarrierAbility.AbsorbDamage(victimHealth, share, currentMana: 100000, manaPerHealth: 1.0);

            Assert.AreEqual(90u, absorbedClamped.DamageAbsorbed);
            Assert.AreEqual(210u, absorbedClamped.DamageAfter);
            Assert.IsTrue(absorbedClamped.DamageAfter < victimHealth,
                "clamping before the absorb always spares the victim - this is the defect, not the fix");
        }

        /// <summary>
        /// The load-bearing property in its general form: the barrier is sized off the HIT, never off the
        /// victim's health, so its output does not vary with how much health the victim happens to have.
        /// There is no health parameter to pass, which is the point - this test exists to state that the
        /// absence is deliberate and to fail loudly if one is ever reintroduced.
        /// </summary>
        [TestMethod]
        public void AbsorbDamage_ScalesOffTheHit_NotTheVictimsRemainingHealth()
        {
            // the same hit, resolved twice, is the same answer regardless of any victim state
            var first = ManaBarrierAbility.AbsorbDamage(942, 0.25, 100000, 1.0);
            var second = ManaBarrierAbility.AbsorbDamage(942, 0.25, 100000, 1.0);

            Assert.AreEqual(first.DamageAbsorbed, second.DamageAbsorbed);
            Assert.AreEqual(first.DamageAfter, second.DamageAfter);

            // and a hit twice as large is absorbed twice as hard, which a health-clamped figure never was.
            // 940/1880 rather than the 942 above so the floor in the share does not round the doubling off.
            var single = ManaBarrierAbility.AbsorbDamage(940, 0.25, 100000, 1.0);
            var doubled = ManaBarrierAbility.AbsorbDamage(1880, 0.25, 100000, 1.0);

            Assert.AreEqual(235u, single.DamageAbsorbed);
            Assert.AreEqual(single.DamageAbsorbed * 2, doubled.DamageAbsorbed);
        }

        // ---- the total identity -------------------------------------------------------------------

        [TestMethod]
        public void AbsorbDamage_AlwaysSplitsTheIncomingHitExactly()
        {
            uint[] damages = { 1, 7, 50, 199, 312, 942, 5000, 123456 };
            double[] shares = { 0.0, 0.05, 0.10, 0.175, 0.25, 0.3942, 0.60, 1.0 };
            uint[] manaPools = { 0, 1, 37, 500, 100000 };
            double[] rates = { 0.5, 1.0, 2.0, 3.7 };

            foreach (var damage in damages)
            {
                foreach (var share in shares)
                {
                    foreach (var mana in manaPools)
                    {
                        foreach (var rate in rates)
                        {
                            var absorbed = ManaBarrierAbility.AbsorbDamage(damage, share, mana, rate);

                            var label = $"damage={damage} share={share} mana={mana} rate={rate}";

                            Assert.AreEqual(damage, absorbed.DamageAfter + absorbed.DamageAbsorbed,
                                $"the split must be exact: {label}");

                            Assert.IsTrue(absorbed.DamageAbsorbed <= damage,
                                $"the barrier must never absorb more than was thrown: {label}");

                            Assert.IsTrue(absorbed.ManaSpent <= mana,
                                $"the barrier must never overspend the Mana pool: {label}");
                        }
                    }
                }
            }
        }

        // ---- partial payment ----------------------------------------------------------------------

        [TestMethod]
        public void AbsorbDamage_PaysPartiallyWhenManaIsShort()
        {
            // wants 25% of 200 = 50 absorbed, costing 50 mana, but only 20 mana is on hand
            var absorbed = ManaBarrierAbility.AbsorbDamage(200, 0.25, currentMana: 20, manaPerHealth: 1.0);

            Assert.AreEqual(20u, absorbed.DamageAbsorbed);
            Assert.AreEqual(20u, absorbed.ManaSpent);
            Assert.AreEqual(180u, absorbed.DamageAfter);
            Assert.IsTrue(absorbed.ManaSpent <= 20u, "must never spend more mana than the player has");
        }

        [TestMethod]
        public void AbsorbDamage_HonoursTheManaPerHealthRate()
        {
            // at 2 mana per point absorbed, 40 mana buys 20 points of the 50 it wanted
            var absorbed = ManaBarrierAbility.AbsorbDamage(200, 0.25, currentMana: 40, manaPerHealth: 2.0);

            Assert.AreEqual(20u, absorbed.DamageAbsorbed);
            Assert.AreEqual(40u, absorbed.ManaSpent);
            Assert.AreEqual(180u, absorbed.DamageAfter);
        }

        // ---- inert cases --------------------------------------------------------------------------

        [TestMethod]
        public void AbsorbDamage_IsInertWithoutManaShareOrDamage()
        {
            var noMana = ManaBarrierAbility.AbsorbDamage(200, 0.25, currentMana: 0, manaPerHealth: 1.0);
            Assert.AreEqual(200u, noMana.DamageAfter);
            Assert.AreEqual(0u, noMana.DamageAbsorbed);
            Assert.AreEqual(0u, noMana.ManaSpent);

            var noShare = ManaBarrierAbility.AbsorbDamage(200, 0.0, currentMana: 1000, manaPerHealth: 1.0);
            Assert.AreEqual(200u, noShare.DamageAfter);
            Assert.AreEqual(0u, noShare.DamageAbsorbed);
            Assert.AreEqual(0u, noShare.ManaSpent);

            var noDamage = ManaBarrierAbility.AbsorbDamage(0, 0.25, currentMana: 1000, manaPerHealth: 1.0);
            Assert.AreEqual(0u, noDamage.DamageAfter);
            Assert.AreEqual(0u, noDamage.DamageAbsorbed);
            Assert.AreEqual(0u, noDamage.ManaSpent);

            // a share too small to buy a whole point spends nothing and changes nothing
            var tooSmall = ManaBarrierAbility.AbsorbDamage(1, 0.10, currentMana: 1000, manaPerHealth: 1.0);
            Assert.AreEqual(1u, tooSmall.DamageAfter);
            Assert.AreEqual(0u, tooSmall.ManaSpent);
        }

        /// <summary>
        /// An out-of-contract share above 1.0 (only reachable by an admin setting
        /// class_ability_manabarrier_max_share above 1) absorbs the whole hit and no more. The old refund
        /// shape would have HEALED the victim on the same input.
        /// </summary>
        [TestMethod]
        public void AbsorbDamage_ClampsAShareAboveOne()
        {
            var absorbed = ManaBarrierAbility.AbsorbDamage(200, 2.0, currentMana: 100000, manaPerHealth: 1.0);

            Assert.AreEqual(200u, absorbed.DamageAbsorbed);
            Assert.AreEqual(0u, absorbed.DamageAfter);
            Assert.AreEqual(200u, absorbed.ManaSpent);
        }
    }
}
