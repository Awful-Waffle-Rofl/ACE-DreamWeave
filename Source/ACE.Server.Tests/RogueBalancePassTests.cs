using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pocket Sand (Rogue T3), from the 2026-08-17 Berserker/Rogue balance pass. Covers the pure math the
    /// handler exposes plus its registration at the id, tier and cost the design fixed.
    ///
    /// SUREFOOTED (Rogue T2) WAS THE OTHER HALF OF THIS FILE until 2026-09-12, when the class ability
    /// overhaul retired it: its stacking avoidance overlapped Parry inside the same pooled cap. Its four
    /// tests went with the handler. The pooled-cap containment invariant they exercised is not lost - it is
    /// owned by ClassAbilityHandlerTests.Avoidance_PooledRoll_BlockThenParry_UnderCap and
    /// Avoidance_OverCap_ScalesBothProportionally_PreservingRatio, which test ClassAbilityAvoidance directly
    /// rather than through a contributor.
    ///
    /// The parts that genuinely need a live Player (the three Pocket Sand call sites) are exercised in-game:
    /// Player's static initializer cannot run under this test host, so no test here constructs one.
    /// </summary>
    [TestClass]
    public class RogueBalancePassTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // ---------------- Pocket Sand ----------------

        /// <summary>
        /// 10/20/30% by rank, then SCALED by the Deception affinity multiplier. Unowned is inert.
        ///
        /// The affinity moved from the ADDITIVE primitive to the MULTIPLICATIVE one on 2026-09-12, so the
        /// fourth argument is a FACTOR whose neutral value is 1.0, not a flat rider whose neutral value was
        /// 0.0. The two have identical shapes, which is exactly why the helper floors the factor at 1.0.
        /// </summary>
        [TestMethod]
        public void PocketSand_Chance_IsTenTwentyThirtyByRank()
        {
            const double chanceBase = 0.10;
            const double chanceStep = 0.10;

            Assert.AreEqual(0.0, PocketSandAbility.Chance(0, chanceBase, chanceStep, 1.0), 1e-9);
            Assert.AreEqual(0.10, PocketSandAbility.Chance(1, chanceBase, chanceStep, 1.0), 1e-9);
            Assert.AreEqual(0.20, PocketSandAbility.Chance(2, chanceBase, chanceStep, 1.0), 1e-9);
            Assert.AreEqual(0.30, PocketSandAbility.Chance(3, chanceBase, chanceStep, 1.0), 1e-9);

            // the affinity MULTIPLIES this ability's own rank bonus: 0.30 * 1.5
            Assert.AreEqual(0.45, PocketSandAbility.Chance(3, chanceBase, chanceStep, 1.5), 1e-9);

            // the old primitive's neutral 0.0 degrades to rank alone rather than deleting the whole bonus
            Assert.AreEqual(0.30, PocketSandAbility.Chance(3, chanceBase, chanceStep, 0.0), 1e-9);
        }

        /// <summary>
        /// The Deception affinity is CLAMPED, and what the clamp bounds is the AMOUNT the multiply ADDS
        /// (rankBonus * multiplier - rankBonus), never the rank ladder and never the factor itself. The
        /// affinity is linear in a skill value the server does not constrain, and this ability fires on
        /// EVERY avoidance, so an uncapped affinity would mean permanent uptime from the first dodge. A cap
        /// of 0 means uncapped.
        /// </summary>
        [TestMethod]
        public void PocketSand_DeceptionAffinity_IsClampedByTheAffinityCap()
        {
            const double chanceBase = 0.10;
            const double chanceStep = 0.10;
            const double affinityCap = 0.20;

            // added amount under the cap passes through untouched: 0.30 * 1.5 adds 0.15
            Assert.AreEqual(0.45, PocketSandAbility.Chance(3, chanceBase, chanceStep, 1.5, affinityCap), 1e-9);

            // exactly at the cap: the factor that makes a 0.30 rank bonus add precisely 0.20
            Assert.AreEqual(0.50, PocketSandAbility.Chance(3, chanceBase, chanceStep, 1.0 + 0.20 / 0.30, affinityCap), 1e-9);

            // an absurd Deception cannot push past the rank ladder + cap
            Assert.AreEqual(0.50, PocketSandAbility.Chance(3, chanceBase, chanceStep, 9.88, affinityCap), 1e-9);

            // cap 0 = uncapped, which is the defect the clamp exists for
            Assert.AreEqual(0.30 * 9.88, PocketSandAbility.Chance(3, chanceBase, chanceStep, 9.88, 0.0), 1e-9);

            // a sub-neutral factor is floored at 1.0 rather than shrinking the bonus
            Assert.AreEqual(0.30, PocketSandAbility.Chance(3, chanceBase, chanceStep, -1.0, affinityCap), 1e-9);

            Assert.AreEqual(0.20, PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item, 1e-9,
                "the shipped affinity cap this ability is clamped by");
        }

        /// <summary>
        /// The shipped tunables the Description quotes. A Description that promises numbers the defaults do
        /// not produce is a player-facing lie, and this is the only place the two are compared.
        /// </summary>
        [TestMethod]
        public void ShippedTunables_MatchTheNumbersTheDescriptionsQuote()
        {
            Assert.AreEqual(0.15, PropertyManager.GetDouble("class_ability_pocketsand_chance_base").Item, 1e-9);
            Assert.AreEqual(0.15, PropertyManager.GetDouble("class_ability_pocketsand_chance_step").Item, 1e-9);
            Assert.AreEqual(40.0, PropertyManager.GetDouble("class_ability_pocketsand_magnitude").Item, 1e-9);
            Assert.AreEqual(20.0, PropertyManager.GetDouble("class_ability_pocketsand_duration_seconds").Item, 1e-9);
            // The per-Deception DIVISOR pair this used to assert was DELETED on 2026-09-12: the affinity is
            // multiplicative now and reads the SHARED rate pair instead of two per-ability divisors.
            Assert.AreEqual(0.09, PropertyManager.GetDouble("class_ability_affinity_rate_per_trained").Item, 1e-9);
            Assert.AreEqual(0.14, PropertyManager.GetDouble("class_ability_affinity_rate_per_spec").Item, 1e-9);
        }

        // ---------------- registration ----------------

        /// <summary>
        /// Registered at the id Phase 0 reserved, in the Rogue class at the tier the plan fixed. Two parallel
        /// worktrees agreed on that id before either handler existed, so a silent renumber here would desync
        /// them. Surefooted's half of this test was removed on 2026-09-12 with the ability; its id (72) stays
        /// reserved and unregistered, which <see cref="ClassAbilityHandlerTests"/> pins.
        ///
        /// The cost is 1/1/1, not the 1/2/3 it shipped with: the 2026-09-12 class ability overhaul repriced
        /// almost every escalating curve onto a flat 1 per rank.
        /// </summary>
        [TestMethod]
        public void PocketSand_IsRegisteredAtTheAgreedIdTierAndCost()
        {
            Assert.IsTrue(ClassAbilityRegistry.Abilities.TryGetValue(ClassAbilityId.PocketSand, out var pocketSand));
            Assert.AreEqual(73, (int)ClassAbilityId.PocketSand);
            Assert.AreEqual(ClassAbilityClass.Rogue, pocketSand.AbilityClass);
            Assert.AreEqual(3, pocketSand.Tier);
            Assert.AreEqual(3, pocketSand.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, pocketSand.CostPerRank);
            Assert.AreEqual("pocketsand", pocketSand.Name);
            Assert.IsTrue(pocketSand.Implemented);

            // Surefooted retired 2026-09-12: the id stays reserved and must never re-register.
            Assert.AreEqual(72, (int)ClassAbilityId.Surefooted);
            Assert.IsFalse(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.Surefooted));
        }

        /// <summary>
        /// It carries no combat hook - it is IPassiveStatAbility, dispatched by hand from the three avoidance
        /// sites. If a future change gives it a hook interface, its registration order in
        /// ClassAbilityRegistry starts mattering and this test is the reminder.
        /// </summary>
        [TestMethod]
        public void PocketSand_IsPassiveWithNoCombatHook()
        {
            var pocketSand = ClassAbilityRegistry.Handlers.Single(h => h.Definition.Id == ClassAbilityId.PocketSand);

            Assert.IsInstanceOfType(pocketSand, typeof(IPassiveStatAbility));
            Assert.IsFalse(ClassAbilityRegistry.OutgoingDamageAbilities.Any(h => h.Definition.Id == ClassAbilityId.PocketSand));
        }
    }
}
