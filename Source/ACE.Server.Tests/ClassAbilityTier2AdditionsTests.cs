using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure-math and wiring coverage for the 2026-07-30 Tier-2 additions: Mana Barrier (Archmage),
    /// Nether Bloom (Void/Summon), Soul Tether (Void/Summon). The parts that need a live Player or
    /// landblock - the Mana/Health vital swap itself, Nether Bloom's neighbor sweep and enchantment
    /// re-application, the pet damage site, the resummon-cooldown waiver - are exercised in-game.
    /// </summary>
    [TestClass]
    public class ClassAbilityTier2AdditionsTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;

        // ---- Mana Barrier ---------------------------------------------------------------------

        [TestMethod]
        public void ManaBarrier_DivertShare_FollowsPerRankCurve()
        {
            var b = D("class_ability_manabarrier_base");
            var s = D("class_ability_manabarrier_step");
            var cap = D("class_ability_manabarrier_max_share");

            Assert.AreEqual(0.0, ManaBarrierAbility.DivertShare(0, b, s, 0.0, cap), 1e-9, "unlearned must divert nothing");
            Assert.AreEqual(0.100, ManaBarrierAbility.DivertShare(1, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.175, ManaBarrierAbility.DivertShare(2, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.250, ManaBarrierAbility.DivertShare(3, b, s, 0.0, cap), 1e-9);
        }

        [TestMethod]
        public void ManaBarrier_DivertShare_AddsRiderAndClampsToCap()
        {
            var b = D("class_ability_manabarrier_base");
            var s = D("class_ability_manabarrier_step");
            var cap = D("class_ability_manabarrier_max_share");

            // the Magic Defense rider adds flat to the rank share
            Assert.AreEqual(0.35, ManaBarrierAbility.DivertShare(3, b, s, 0.10, cap), 1e-9);

            // ... but the total can never pass the cap
            Assert.AreEqual(cap, ManaBarrierAbility.DivertShare(3, b, s, 5.0, cap), 1e-9);

            // a negative rider is ignored rather than reducing the rank share
            Assert.AreEqual(0.25, ManaBarrierAbility.DivertShare(3, b, s, -1.0, cap), 1e-9);

            // rank 0 stays inert no matter how large the rider
            Assert.AreEqual(0.0, ManaBarrierAbility.DivertShare(0, b, s, 5.0, cap), 1e-9);
        }

        [TestMethod]
        public void ManaBarrier_Resolve_PaysFullShareWhenManaAllows()
        {
            var divert = ManaBarrierAbility.Resolve(200, 0.25, currentMana: 1000, manaPerHealth: 1.0);

            Assert.AreEqual(50u, divert.DamageAbsorbed);
            Assert.AreEqual(50u, divert.ManaSpent);
        }

        [TestMethod]
        public void ManaBarrier_Resolve_PaysPartiallyAndNeverOverspendsMana()
        {
            // wants 50 health (50 mana) but only 20 mana is left
            var divert = ManaBarrierAbility.Resolve(200, 0.25, currentMana: 20, manaPerHealth: 1.0);

            Assert.AreEqual(20u, divert.DamageAbsorbed);
            Assert.AreEqual(20u, divert.ManaSpent);
            Assert.IsTrue(divert.ManaSpent <= 20u, "must never spend more mana than the player has");
        }

        [TestMethod]
        public void ManaBarrier_Resolve_HonoursTheManaPerHealthRate()
        {
            // at 2 mana per health, 40 mana buys 20 health
            var divert = ManaBarrierAbility.Resolve(200, 0.25, currentMana: 40, manaPerHealth: 2.0);

            Assert.AreEqual(20u, divert.DamageAbsorbed);
            Assert.AreEqual(40u, divert.ManaSpent);
            Assert.IsTrue(divert.ManaSpent <= 40u);
        }

        [TestMethod]
        public void ManaBarrier_Resolve_IsInertWithoutShareDamageOrMana()
        {
            Assert.AreEqual(0u, ManaBarrierAbility.Resolve(0, 0.25, 1000, 1.0).DamageAbsorbed);
            Assert.AreEqual(0u, ManaBarrierAbility.Resolve(200, 0.0, 1000, 1.0).DamageAbsorbed);
            Assert.AreEqual(0u, ManaBarrierAbility.Resolve(200, 0.25, 0, 1.0).DamageAbsorbed);

            // a share too small to buy a whole point of health spends nothing
            Assert.AreEqual(0u, ManaBarrierAbility.Resolve(1, 0.10, 1000, 1.0).ManaSpent);
        }

        [TestMethod]
        public void ManaBarrier_IsAPassiveAtTier2()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.ManaBarrier);

            // The barrier is a REDUCTION applied before each site's health write, read directly off the
            // player by Player.AbsorbWithManaBarrier - so it hooks nothing and carries IPassiveStatAbility,
            // the same shape as Sanguine Ward. It rode IIncomingDamageAbility until 2026-09-08, and that
            // was the overkill bug: the dispatch runs AFTER the health write, which clamps at zero, so an
            // overkill hit handed the barrier the victim's remaining health instead of the damage thrown.
            // Re-registering it on that hook would reintroduce the defect.
            Assert.IsInstanceOfType(handler, typeof(IPassiveStatAbility));
            Assert.IsFalse(handler is IIncomingDamageAbility, "a pre-write reduction must not ride the post-write hook");
            Assert.IsFalse(ClassAbilityRegistry.IncomingDamageAbilities.Contains(handler as IIncomingDamageAbility));
            Assert.IsFalse(handler is IOutgoingDamageAbility);
            Assert.IsFalse(handler is IMissileVolleyAbility);
            Assert.IsFalse(handler is IItemProcAbility);
            Assert.IsFalse(handler is ISpellHitAbility);
            Assert.IsFalse(handler is ICreatureDeathAbility);

            var def = handler.Definition;
            Assert.AreEqual(ClassAbilityClass.Archmage, def.AbilityClass);
            Assert.AreEqual(2, def.Tier);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, def.CostPerRank);
            Assert.IsTrue(def.Implemented);
            StringAssert.Contains(def.Description, "Magic Defense", "the description must name the affinity skill");
        }

        // ---- Nether Bloom ---------------------------------------------------------------------

        [TestMethod]
        public void NetherBloom_JumpCount_FollowsPerRankCurve()
        {
            var perRank = D("class_ability_netherbloom_jumps_per_rank");

            Assert.AreEqual(0, NetherBloomAbility.JumpCount(0, perRank), "unlearned must spread to nobody");
            Assert.AreEqual(1, NetherBloomAbility.JumpCount(1, perRank));
            Assert.AreEqual(2, NetherBloomAbility.JumpCount(2, perRank));
            Assert.AreEqual(3, NetherBloomAbility.JumpCount(3, perRank));

            // a non-positive rate disables the spread entirely
            Assert.AreEqual(0, NetherBloomAbility.JumpCount(3, 0.0));
            Assert.AreEqual(0, NetherBloomAbility.JumpCount(3, -1.0));
        }

        [TestMethod]
        public void NetherBloom_RetainedDuration_CarriesRemainingTimeAndFloorsAtZero()
        {
            Assert.AreEqual(12.0, NetherBloomAbility.RetainedDuration(12.0, 1.0), 1e-9);
            Assert.AreEqual(6.0, NetherBloomAbility.RetainedDuration(12.0, 0.5), 1e-9);

            // an already-expired (or zero-length) source spreads nothing
            Assert.AreEqual(0.0, NetherBloomAbility.RetainedDuration(0.0, 1.0), 1e-9);
            Assert.AreEqual(0.0, NetherBloomAbility.RetainedDuration(-3.0, 1.0), 1e-9);
            Assert.AreEqual(0.0, NetherBloomAbility.RetainedDuration(12.0, 0.0), 1e-9);
        }

        [TestMethod]
        public void NetherBloom_CascadeGuard_RecognisesItsOwnStamp()
        {
            var pristine = new PropertiesEnchantmentRegistry { DegradeLimit = 0.0f };
            var cooldown = new PropertiesEnchantmentRegistry { DegradeLimit = -666.0f };
            var bloomed = new PropertiesEnchantmentRegistry { DegradeLimit = NetherBloomAbility.BloomMarker };

            Assert.IsFalse(NetherBloomAbility.IsBloomed(null));
            Assert.IsFalse(NetherBloomAbility.IsBloomed(pristine));
            Assert.IsFalse(NetherBloomAbility.IsBloomed(cooldown), "the cooldown sentinel must not read as a bloom");
            Assert.IsTrue(NetherBloomAbility.IsBloomed(bloomed));
        }

        [TestMethod]
        public void NetherBloom_IsACreatureDeathAbilityAtTier2()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.NetherBloom);

            Assert.IsInstanceOfType(handler, typeof(ICreatureDeathAbility));
            Assert.IsTrue(ClassAbilityRegistry.CreatureDeathAbilities.Contains((ICreatureDeathAbility)handler));

            // the death hook is its whole effect - it must not ride any other hook, and it is not a passive
            Assert.IsFalse(handler is IPassiveStatAbility);
            Assert.IsFalse(handler is IOutgoingDamageAbility);
            Assert.IsFalse(handler is IIncomingDamageAbility);
            Assert.IsFalse(handler is IMissileVolleyAbility);
            Assert.IsFalse(handler is IItemProcAbility);
            Assert.IsFalse(handler is ISpellHitAbility);

            var def = handler.Definition;
            Assert.AreEqual(ClassAbilityClass.VoidSummon, def.AbilityClass);
            Assert.AreEqual(2, def.Tier);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
            Assert.IsTrue(def.Implemented);
        }

        [TestMethod]
        public void NetherBloom_IsTheOnlyCreatureDeathAbility()
        {
            // one dispatch site, one handler today - this guards against a second handler being added to
            // the bucket without a deliberate look at Creature.OnDeath ordering
            Assert.AreEqual(1, ClassAbilityRegistry.CreatureDeathAbilities.Count);
        }

        // ---- Soul Tether ----------------------------------------------------------------------

        [TestMethod]
        public void SoulTether_DamageReduction_FollowsPerRankCurve()
        {
            var b = D("class_ability_soultether_base");
            var s = D("class_ability_soultether_step");
            var cap = D("class_ability_soultether_max_reduction");

            Assert.AreEqual(0.0, SoulTetherAbility.DamageReduction(0, b, s, 0.0, cap), 1e-9, "unlearned must reduce nothing");
            Assert.AreEqual(0.100, SoulTetherAbility.DamageReduction(1, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.175, SoulTetherAbility.DamageReduction(2, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.250, SoulTetherAbility.DamageReduction(3, b, s, 0.0, cap), 1e-9);
        }

        [TestMethod]
        public void SoulTether_DamageReduction_AddsRiderAndClampsToCap()
        {
            var b = D("class_ability_soultether_base");
            var s = D("class_ability_soultether_step");
            var cap = D("class_ability_soultether_max_reduction");

            Assert.AreEqual(0.35, SoulTetherAbility.DamageReduction(3, b, s, 0.10, cap), 1e-9);
            Assert.AreEqual(cap, SoulTetherAbility.DamageReduction(3, b, s, 5.0, cap), 1e-9);
            Assert.AreEqual(0.25, SoulTetherAbility.DamageReduction(3, b, s, -1.0, cap), 1e-9);
            Assert.AreEqual(0.0, SoulTetherAbility.DamageReduction(0, b, s, 5.0, cap), 1e-9);
        }

        [TestMethod]
        public void SoulTether_DamageMultiplier_IsOneWhenUnlearnedAndNeverAmplifies()
        {
            var b = D("class_ability_soultether_base");
            var s = D("class_ability_soultether_step");
            var cap = D("class_ability_soultether_max_reduction");

            Assert.AreEqual(1.0f, SoulTetherAbility.DamageMultiplier(0, b, s, 0.0, cap), 1e-6f);
            Assert.AreEqual(0.90f, SoulTetherAbility.DamageMultiplier(1, b, s, 0.0, cap), 1e-6f);
            Assert.AreEqual(0.75f, SoulTetherAbility.DamageMultiplier(3, b, s, 0.0, cap), 1e-6f);

            // the cap keeps the pet from ever becoming immune
            Assert.AreEqual((float)(1.0 - cap), SoulTetherAbility.DamageMultiplier(3, b, s, 99.0, cap), 1e-6f);
            Assert.IsTrue(SoulTetherAbility.DamageMultiplier(3, b, s, 99.0, cap) > 0.0f);
        }

        [TestMethod]
        public void SoulTether_IsAPassiveAtTier2()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.SoulTether);

            // read at the pet damage site, so it hooks nothing and must carry IPassiveStatAbility
            Assert.IsInstanceOfType(handler, typeof(IPassiveStatAbility));
            Assert.IsFalse(handler is IOutgoingDamageAbility);
            Assert.IsFalse(handler is IIncomingDamageAbility);
            Assert.IsFalse(handler is IMissileVolleyAbility);
            Assert.IsFalse(handler is IItemProcAbility);
            Assert.IsFalse(handler is ISpellHitAbility);
            Assert.IsFalse(handler is ICreatureDeathAbility);

            var def = handler.Definition;
            Assert.AreEqual(ClassAbilityClass.VoidSummon, def.AbilityClass);
            Assert.AreEqual(2, def.Tier);
            Assert.AreEqual(3, def.MaxRank);
            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, def.CostPerRank);
            Assert.IsTrue(def.Implemented);
            StringAssert.Contains(def.Description, "Loyalty", "the description must name the affinity skill");
        }

        // ---- shared contract ------------------------------------------------------------------

        [TestMethod]
        public void NewAbilities_HaveDistinctAppendedEnumIds()
        {
            Assert.AreEqual(48, (int)ClassAbilityId.ManaBarrier);
            Assert.AreEqual(49, (int)ClassAbilityId.NetherBloom);
            Assert.AreEqual(50, (int)ClassAbilityId.SoulTether);

            // 43 (StreakToArc) stays retired and reserved - never re-pointed at a new ability
            Assert.IsFalse(ClassAbilityRegistry.Abilities.ContainsKey(ClassAbilityId.StreakToArc));
        }

        [TestMethod]
        public void NewAbilities_HaveTokenOfferingsForEveryRank()
        {
            foreach (var id in new[] { ClassAbilityId.ManaBarrier, ClassAbilityId.NetherBloom, ClassAbilityId.SoulTether })
            {
                for (var tier = 1; tier <= 3; tier++)
                    Assert.IsTrue(ClassAbilityTokenCatalog.TryResolve(id, tier, out _), $"{id} tier {tier} has no token offering");

                Assert.IsFalse(ClassAbilityTokenCatalog.TryResolve(id, 4, out _), $"{id} must have no tier 4 token");
            }

            // appended after Withering, so no existing token wcid moved
            Assert.AreEqual(1000884u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.ManaBarrier, 1));
            Assert.AreEqual(1000890u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.NetherBloom, 1));
            Assert.AreEqual(1000896u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.SoulTether, 1));
            Assert.AreEqual(1000878u, ClassAbilityTokenCatalog.WcidFor(ClassAbilityId.Withering, 1));
        }

        [TestMethod]
        public void NewTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.10, D("class_ability_manabarrier_base"), 1e-9);
            Assert.AreEqual(0.075, D("class_ability_manabarrier_step"), 1e-9);
            Assert.AreEqual(0.60, D("class_ability_manabarrier_max_share"), 1e-9);
            Assert.AreEqual(1.0, D("class_ability_manabarrier_mana_per_health"), 1e-9);
            Assert.AreEqual(25.0, D("class_ability_manabarrier_magicdef_per_trained"), 1e-9);
            Assert.AreEqual(18.0, D("class_ability_manabarrier_magicdef_per_spec"), 1e-9);

            Assert.AreEqual(1.0, D("class_ability_netherbloom_jumps_per_rank"), 1e-9);
            Assert.AreEqual(10.0, D("class_ability_netherbloom_radius"), 1e-9);
            Assert.AreEqual(1.0, D("class_ability_netherbloom_duration_retained"), 1e-9);

            Assert.AreEqual(0.10, D("class_ability_soultether_base"), 1e-9);
            Assert.AreEqual(0.075, D("class_ability_soultether_step"), 1e-9);
            Assert.AreEqual(0.50, D("class_ability_soultether_max_reduction"), 1e-9);
            Assert.AreEqual(25.0, D("class_ability_soultether_loyalty_per_trained"), 1e-9);
            Assert.AreEqual(18.0, D("class_ability_soultether_loyalty_per_spec"), 1e-9);
        }
    }
}
