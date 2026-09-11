using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The two Rogue additions from the 2026-08-17 Berserker/Rogue balance pass: Surefooted (T2) and
    /// Pocket Sand (T3). Covers the pure math each handler exposes plus the two invariants that are not
    /// local to either handler - that Surefooted's parry contribution stays inside the pooled avoidance
    /// cap, and that both entries are registered at the ids and costs the design fixed.
    ///
    /// The parts that genuinely need a live Player (the stack pool itself, the OnEvade trigger, the
    /// melee-damage reset, the three Pocket Sand call sites) are exercised in-game: Player's static
    /// initializer cannot run under this test host, so no test here constructs one.
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

        // ---------------- Surefooted ----------------

        /// <summary>
        /// The ramp at each rank: 1/2/3% per stack, so an empty pool is worth nothing, a partial pool
        /// scales linearly, and a full 5-stack pool is worth +5/10/15% by rank.
        /// </summary>
        [TestMethod]
        public void Surefooted_ParryBonus_ScalesWithRankAndStacks()
        {
            const double perRankPerStack = 0.01;

            // no stacks, no bonus, at every rank
            Assert.AreEqual(0.0, SurefootedAbility.ParryBonus(1, 0, perRankPerStack), 1e-9);
            Assert.AreEqual(0.0, SurefootedAbility.ParryBonus(3, 0, perRankPerStack), 1e-9);

            // three stacks
            Assert.AreEqual(0.03, SurefootedAbility.ParryBonus(1, 3, perRankPerStack), 1e-9);
            Assert.AreEqual(0.06, SurefootedAbility.ParryBonus(2, 3, perRankPerStack), 1e-9);
            Assert.AreEqual(0.09, SurefootedAbility.ParryBonus(3, 3, perRankPerStack), 1e-9);

            // a full pool: the +5/10/15% the ability's Description promises
            Assert.AreEqual(0.05, SurefootedAbility.ParryBonus(1, 5, perRankPerStack), 1e-9);
            Assert.AreEqual(0.10, SurefootedAbility.ParryBonus(2, 5, perRankPerStack), 1e-9);
            Assert.AreEqual(0.15, SurefootedAbility.ParryBonus(3, 5, perRankPerStack), 1e-9);

            // unowned is inert, so a caller that skipped the ownership lookup still gets nothing
            Assert.AreEqual(0.0, SurefootedAbility.ParryBonus(0, 5, perRankPerStack), 1e-9);
        }

        /// <summary>
        /// The stack cap is the pool's only ceiling (rank buys a bigger per-stack bonus, not more stacks),
        /// so the whole ability is bounded by cap * rank * rate. Pins the shipped cap and the ceiling it
        /// implies, in the same spirit as the proc-chance clamp tests.
        /// </summary>
        [TestMethod]
        public void Surefooted_StackCapBoundsTheWholeAbility()
        {
            var cap = SurefootedAbility.StackCap();
            var perRankPerStack = PropertyManager.GetDouble("class_ability_surefooted_percent_per_rank_per_stack").Item;

            Assert.AreEqual(5, cap, "the shipped Surefooted stack cap is 5");
            Assert.AreEqual(0.01, perRankPerStack, 1e-9);

            // combat clamps held stacks to the cap, so this is the most the ability can ever be worth
            Assert.AreEqual(0.15, SurefootedAbility.ParryBonus(3, cap, perRankPerStack), 1e-9,
                "max rank at a full pool is +15% parry");

            // and an over-full pool (a cap lowered under a player already holding more) is clamped by the
            // caller, never by ParryBonus itself - which is why the caller must do the clamping
            Assert.AreEqual(0.30, SurefootedAbility.ParryBonus(3, 10, perRankPerStack), 1e-9,
                "ParryBonus deliberately does not clamp; Player.GetSurefootedParryBonus does");
        }

        /// <summary>
        /// The idle window is inclusive at the boundary: a pool touched exactly windowSeconds ago survives,
        /// a hair later it does not. Pins which side of the boundary an evade landing on the tick falls.
        /// </summary>
        [TestMethod]
        public void Surefooted_IdleWindow_IsInclusiveAtTheBoundary()
        {
            const double window = 10.0;

            Assert.IsFalse(SurefootedAbility.StacksExpired(1000.0, 1000.0, window), "just evaded");
            Assert.IsFalse(SurefootedAbility.StacksExpired(1009.9, 1000.0, window), "inside the window");
            Assert.IsFalse(SurefootedAbility.StacksExpired(1010.0, 1000.0, window), "exactly at the window - still alive");
            Assert.IsTrue(SurefootedAbility.StacksExpired(1010.001, 1000.0, window), "a hair past the window");

            Assert.AreEqual(10.0, PropertyManager.GetDouble("class_ability_surefooted_expire_seconds").Item, 1e-9,
                "the shipped window is 10 seconds");
        }

        /// <summary>
        /// THE CONTAINMENT INVARIANT, and the reason Surefooted is added into parryChance BEFORE
        /// ClassAbilityAvoidance.Resolve rather than after it: the pooled Shield Block + Parry cap must
        /// still bite on the combined figure, so the ability can only claim a bigger SHARE of an
        /// already-bounded pool, never widen the pool.
        ///
        /// Worst case built here: rank-3 Shield Block (0.20) + rank-3 Parry (0.15) + a full rank-3
        /// Surefooted pool (0.15) = 0.50 raw. Push it past the cap and Pooled must scale both shares down.
        /// </summary>
        [TestMethod]
        public void Surefooted_CannotPushCombinedAvoidancePastThePooledCap()
        {
            var cap = PropertyManager.GetDouble("class_ability_avoidance_cap").Item;
            Assert.AreEqual(0.50, cap, 1e-9);

            const double blockChance = 0.20;
            const double parryFromParryAbility = 0.15;
            var surefooted = SurefootedAbility.ParryBonus(3, 5, 0.01);   // 0.15

            var parryChance = parryFromParryAbility + surefooted;        // 0.30

            Assert.AreEqual(0.50, blockChance + parryChance, 1e-9, "raw total sits exactly at the cap");

            var (block, parry, capped) = ClassAbilityAvoidance.Pooled(blockChance, parryChance, cap);
            Assert.IsFalse(capped, "exactly at the cap does not reduce anything");
            Assert.AreEqual(0.50, block + parry, 1e-9);

            // now add a Deception rider on top and the cap must bite, scaling BOTH shares proportionally
            var withRider = parryChance + 0.10;                          // 0.40
            var (blockCapped, parryCapped, bites) = ClassAbilityAvoidance.Pooled(blockChance, withRider, cap);

            Assert.IsTrue(bites, "0.20 + 0.40 = 0.60 raw must be capped back to 0.50");
            Assert.AreEqual(0.50, blockCapped + parryCapped, 1e-9, "total avoidance never exceeds the cap");
            Assert.IsTrue(blockCapped < blockChance, "the block share is scaled down too, preserving the ratio");

            // and the roll agrees: nothing above the cap can ever avoid
            Assert.AreEqual(ClassAbilityAvoidanceOutcome.None,
                ClassAbilityAvoidance.Resolve(blockChance, withRider, cap, 0.5000001),
                "a roll past the cap is never an avoidance, however much Surefooted contributed");
        }

        // ---------------- Pocket Sand ----------------

        /// <summary>
        /// 10/20/30% by rank, plus the Deception rider. Unowned is inert.
        /// </summary>
        [TestMethod]
        public void PocketSand_Chance_IsTenTwentyThirtyByRank()
        {
            const double chanceBase = 0.10;
            const double chanceStep = 0.10;

            Assert.AreEqual(0.0, PocketSandAbility.Chance(0, chanceBase, chanceStep, 0.0), 1e-9);
            Assert.AreEqual(0.10, PocketSandAbility.Chance(1, chanceBase, chanceStep, 0.0), 1e-9);
            Assert.AreEqual(0.20, PocketSandAbility.Chance(2, chanceBase, chanceStep, 0.0), 1e-9);
            Assert.AreEqual(0.30, PocketSandAbility.Chance(3, chanceBase, chanceStep, 0.0), 1e-9);

            // the rider is additive on the same axis
            Assert.AreEqual(0.35, PocketSandAbility.Chance(3, chanceBase, chanceStep, 0.05), 1e-9);
        }

        /// <summary>
        /// The Deception rider is CLAMPED. GetClassAbilityScaling returns an unbounded skill quotient, and
        /// this ability fires on EVERY avoidance, so an uncapped rider would mean permanent uptime from the
        /// first dodge. A cap of 0 means uncapped, reproducing the pre-cap behaviour exactly.
        /// </summary>
        [TestMethod]
        public void PocketSand_DeceptionRider_IsClampedByTheAffinityCap()
        {
            const double chanceBase = 0.10;
            const double chanceStep = 0.10;
            const double affinityCap = 0.20;

            // rider under the cap passes through untouched
            Assert.AreEqual(0.40, PocketSandAbility.Chance(3, chanceBase, chanceStep, 0.10, affinityCap), 1e-9);

            // rider at the cap
            Assert.AreEqual(0.50, PocketSandAbility.Chance(3, chanceBase, chanceStep, 0.20, affinityCap), 1e-9);

            // an absurd Deception cannot push past base + cap
            Assert.AreEqual(0.50, PocketSandAbility.Chance(3, chanceBase, chanceStep, 5.0, affinityCap), 1e-9);

            // cap 0 = uncapped, the pre-cap behaviour
            Assert.AreEqual(5.30, PocketSandAbility.Chance(3, chanceBase, chanceStep, 5.0, 0.0), 1e-9);

            // a negative rider is floored at 0 rather than subtracting
            Assert.AreEqual(0.30, PocketSandAbility.Chance(3, chanceBase, chanceStep, -1.0, affinityCap), 1e-9);

            Assert.AreEqual(0.20, PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item, 1e-9,
                "the shipped affinity cap this ability is clamped by");
        }

        /// <summary>
        /// The shipped tunables the two Descriptions quote. A Description that promises numbers the defaults
        /// do not produce is a player-facing lie, and these are the only place the two are compared.
        /// </summary>
        [TestMethod]
        public void ShippedTunables_MatchTheNumbersTheDescriptionsQuote()
        {
            Assert.AreEqual(0.10, PropertyManager.GetDouble("class_ability_pocketsand_chance_base").Item, 1e-9);
            Assert.AreEqual(0.10, PropertyManager.GetDouble("class_ability_pocketsand_chance_step").Item, 1e-9);
            Assert.AreEqual(30.0, PropertyManager.GetDouble("class_ability_pocketsand_magnitude").Item, 1e-9);
            Assert.AreEqual(20.0, PropertyManager.GetDouble("class_ability_pocketsand_duration_seconds").Item, 1e-9);
            Assert.AreEqual(50.0, PropertyManager.GetDouble("class_ability_pocketsand_deception_per_trained").Item, 1e-9);
            Assert.AreEqual(37.0, PropertyManager.GetDouble("class_ability_pocketsand_deception_per_spec").Item, 1e-9);
        }

        // ---------------- registration ----------------

        /// <summary>
        /// Both entries are registered at the ids Phase 0 reserved, in the Rogue class at the tiers and
        /// costs the plan fixed. Two parallel worktrees agreed on these ids before either handler existed,
        /// so a silent renumber here would desync them.
        /// </summary>
        [TestMethod]
        public void BothAbilities_AreRegisteredAtTheAgreedIdsTiersAndCosts()
        {
            Assert.IsTrue(ClassAbilityRegistry.Abilities.TryGetValue(ClassAbilityId.Surefooted, out var surefooted));
            Assert.AreEqual(72, (int)ClassAbilityId.Surefooted);
            Assert.AreEqual(ClassAbilityClass.Rogue, surefooted.AbilityClass);
            Assert.AreEqual(2, surefooted.Tier);
            Assert.AreEqual(3, surefooted.MaxRank);
            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, surefooted.CostPerRank);
            Assert.AreEqual("surefooted", surefooted.Name);
            Assert.IsTrue(surefooted.Implemented);

            Assert.IsTrue(ClassAbilityRegistry.Abilities.TryGetValue(ClassAbilityId.PocketSand, out var pocketSand));
            Assert.AreEqual(73, (int)ClassAbilityId.PocketSand);
            Assert.AreEqual(ClassAbilityClass.Rogue, pocketSand.AbilityClass);
            Assert.AreEqual(3, pocketSand.Tier);
            Assert.AreEqual(3, pocketSand.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, pocketSand.CostPerRank);
            Assert.AreEqual("pocketsand", pocketSand.Name);
            Assert.IsTrue(pocketSand.Implemented);
        }

        /// <summary>
        /// Neither carries a combat hook - both are IPassiveStatAbility, read at their own sites. If a
        /// future change gives one a hook interface, its registration order in ClassAbilityRegistry starts
        /// mattering and this test is the reminder.
        /// </summary>
        [TestMethod]
        public void BothAbilities_ArePassiveWithNoCombatHook()
        {
            var surefooted = ClassAbilityRegistry.Handlers.Single(h => h.Definition.Id == ClassAbilityId.Surefooted);
            var pocketSand = ClassAbilityRegistry.Handlers.Single(h => h.Definition.Id == ClassAbilityId.PocketSand);

            Assert.IsInstanceOfType(surefooted, typeof(IPassiveStatAbility));
            Assert.IsInstanceOfType(pocketSand, typeof(IPassiveStatAbility));

            Assert.IsFalse(ClassAbilityRegistry.OutgoingDamageAbilities.Any(h => h.Definition.Id == ClassAbilityId.Surefooted));
            Assert.IsFalse(ClassAbilityRegistry.OutgoingDamageAbilities.Any(h => h.Definition.Id == ClassAbilityId.PocketSand));
        }
    }
}
