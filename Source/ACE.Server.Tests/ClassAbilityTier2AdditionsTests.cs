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
            Assert.AreEqual(0.06, ManaBarrierAbility.DivertShare(1, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.12, ManaBarrierAbility.DivertShare(2, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.18, ManaBarrierAbility.DivertShare(3, b, s, 0.0, cap), 1e-9);
        }

        [TestMethod]
        public void ManaBarrier_DivertShare_AddsRiderAndClampsToCap()
        {
            var b = D("class_ability_manabarrier_base");
            var s = D("class_ability_manabarrier_step");
            var cap = D("class_ability_manabarrier_max_share");

            // the Magic Defense rider adds flat to the rank share when the total stays under the cap:
            // rank 2 (0.12) + a 0.05 rider = 0.17, which is still below 0.25
            Assert.AreEqual(0.17, ManaBarrierAbility.DivertShare(2, b, s, 0.05, cap), 1e-9);

            // the Magic Defense rider adds flat to the rank share, but 0.18 + 0.10 = 0.28 exceeds the
            // 0.25 cap, so it clamps there
            Assert.AreEqual(cap, ManaBarrierAbility.DivertShare(3, b, s, 0.10, cap), 1e-9);

            // ... but the total can never pass the cap
            Assert.AreEqual(cap, ManaBarrierAbility.DivertShare(3, b, s, 5.0, cap), 1e-9);

            // a negative rider is ignored rather than reducing the rank share
            Assert.AreEqual(0.18, ManaBarrierAbility.DivertShare(3, b, s, -1.0, cap), 1e-9);

            // rank 0 stays inert no matter how large the rider
            Assert.AreEqual(0.0, ManaBarrierAbility.DivertShare(0, b, s, 5.0, cap), 1e-9);
        }

        /// <summary>
        /// The MULTIPLICATIVE Magic Defense affinity (migrated off the legacy additive rider, mirroring Soul
        /// Tether's 2026-09-12 overhaul): the caller computes rider = skillShare * multiplier - skillShare
        /// and passes that AMOUNT into the pure DivertShare helper above, whose own additive-rider signature
        /// is unchanged. rankShare + rider == rankShare * multiplier exactly.
        /// </summary>
        [TestMethod]
        public void ManaBarrier_MagicDefenseAffinity_MultipliesTheRankShare()
        {
            var b = D("class_ability_manabarrier_base");
            var s = D("class_ability_manabarrier_step");
            var cap = D("class_ability_manabarrier_max_share");

            // rank 1: 0.06 skill share, a 1.89x multiplier -> rider = 0.06*1.89 - 0.06 = 0.0534, uncapped
            // total 0.1134, which stays under the 0.25 cap
            const double multiplier = 1.89;

            var rank1Share = b;
            var rank1Rider = rank1Share * multiplier - rank1Share;

            Assert.AreEqual(0.0534, rank1Rider, 1e-9);
            Assert.AreEqual(0.1134, ManaBarrierAbility.DivertShare(1, b, s, rank1Rider, cap), 1e-9);

            // rank 3: 0.18 skill share, the same 1.89x multiplier -> rider = 0.18*1.89 - 0.18 = 0.1602,
            // uncapped total 0.3402, which clamps to the 0.25 cap
            var rank3Share = b + 2 * s;
            var rank3Rider = rank3Share * multiplier - rank3Share;

            Assert.AreEqual(0.1602, rank3Rider, 1e-9);
            Assert.AreEqual(cap, ManaBarrierAbility.DivertShare(3, b, s, rank3Rider, cap), 1e-9);
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
        public void ManaBarrier_RidesThePreWriteHookAtTier2()
        {
            var handler = ClassAbilityRegistry.GetHandler(ClassAbilityId.ManaBarrier);

            // The barrier is a REDUCTION applied before each site's health write, so it rides
            // IPreWriteDamageAbility - the same hook Sanguine Ward rides. It rode IIncomingDamageAbility
            // until 2026-09-08, and that was the overkill bug: that dispatch runs AFTER the health write,
            // which clamps at zero, so an overkill hit handed the barrier the victim's remaining health
            // instead of the damage thrown. Re-registering it on that hook would reintroduce the defect.
            Assert.IsInstanceOfType(handler, typeof(IPreWriteDamageAbility));
            Assert.IsTrue(ClassAbilityRegistry.PreWriteDamageAbilities.Contains((IPreWriteDamageAbility)handler));
            Assert.IsFalse(handler is IPassiveStatAbility, "it is dispatched from a hook now, not read as a passive marker");
            Assert.IsFalse(handler is IIncomingDamageAbility, "a pre-write reduction must not ride the post-write hook");
            Assert.IsFalse(ClassAbilityRegistry.IncomingDamageAbilities.Contains(handler as IIncomingDamageAbility));
            Assert.IsFalse(handler is IOutgoingDamageAbility);
            Assert.IsFalse(handler is IMissileVolleyAbility);
            Assert.IsFalse(handler is IItemProcAbility);
            Assert.IsFalse(handler is ISpellHitAbility);
            Assert.IsFalse(handler is ICreatureDeathAbility);

            var def = handler.Definition;
            Assert.AreEqual(ClassAbilityClass.Archmage, def.AbilityClass);
            // moved T2 -> T3 by the 2026-09-12 class ability overhaul
            Assert.AreEqual(3, def.Tier);
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
            Assert.AreEqual(0.105, SoulTetherAbility.DamageReduction(1, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.190, SoulTetherAbility.DamageReduction(2, b, s, 0.0, cap), 1e-9);
            Assert.AreEqual(0.275, SoulTetherAbility.DamageReduction(3, b, s, 0.0, cap), 1e-9);
        }

        [TestMethod]
        public void SoulTether_DamageReduction_AddsRiderAndClampsToCap()
        {
            var b = D("class_ability_soultether_base");
            var s = D("class_ability_soultether_step");
            var cap = D("class_ability_soultether_max_reduction");

            Assert.AreEqual(0.375, SoulTetherAbility.DamageReduction(3, b, s, 0.10, cap), 1e-9);
            Assert.AreEqual(cap, SoulTetherAbility.DamageReduction(3, b, s, 5.0, cap), 1e-9);
            Assert.AreEqual(0.275, SoulTetherAbility.DamageReduction(3, b, s, -1.0, cap), 1e-9);
            Assert.AreEqual(0.0, SoulTetherAbility.DamageReduction(0, b, s, 5.0, cap), 1e-9);
        }

        [TestMethod]
        public void SoulTether_DamageMultiplier_IsOneWhenUnlearnedAndNeverAmplifies()
        {
            var b = D("class_ability_soultether_base");
            var s = D("class_ability_soultether_step");
            var cap = D("class_ability_soultether_max_reduction");

            Assert.AreEqual(1.0f, SoulTetherAbility.DamageMultiplier(0, b, s, 0.0, cap), 1e-6f);
            Assert.AreEqual(0.895f, SoulTetherAbility.DamageMultiplier(1, b, s, 0.0, cap), 1e-6f);
            Assert.AreEqual(0.725f, SoulTetherAbility.DamageMultiplier(3, b, s, 0.0, cap), 1e-6f);

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
            // moved T2 -> T3 by the 2026-09-12 class ability overhaul
            Assert.AreEqual(3, def.Tier);
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
            Assert.AreEqual(0.06, D("class_ability_manabarrier_base"), 1e-9);
            Assert.AreEqual(0.06, D("class_ability_manabarrier_step"), 1e-9);
            Assert.AreEqual(0.25, D("class_ability_manabarrier_max_share"), 1e-9);
            Assert.AreEqual(1.0, D("class_ability_manabarrier_mana_per_health"), 1e-9);

            // Mana Barrier's own Magic Defense divisor pair (class_ability_manabarrier_magicdef_per_trained /
            // _per_spec) was DELETED by the multiplicative-affinity migration mirroring Soul Tether's
            // 2026-09-12 overhaul - the ability now reads the shared multiplicative rate instead, so there
            // is nothing per-ability left to pin here.
            //
            // Do not "restore" these two assertions to find out what the keys hold. An unregistered key
            // does not fail cleanly: PropertyManager.GetDouble misses the seeded cache and falls through
            // to a live shard-config read, which throws a MySqlException that reads like an unrelated
            // infrastructure fault rather than a missing key.

            Assert.AreEqual(1.0, D("class_ability_netherbloom_jumps_per_rank"), 1e-9);
            Assert.AreEqual(10.0, D("class_ability_netherbloom_radius"), 1e-9);
            Assert.AreEqual(1.0, D("class_ability_netherbloom_duration_retained"), 1e-9);

            Assert.AreEqual(0.105, D("class_ability_soultether_base"), 1e-9);
            Assert.AreEqual(0.085, D("class_ability_soultether_step"), 1e-9);
            Assert.AreEqual(0.50, D("class_ability_soultether_max_reduction"), 1e-9);

            // Soul Tether's own Loyalty divisor pair (class_ability_soultether_loyalty_per_trained /
            // _per_spec) was DELETED by the 2026-09-12 affinity overhaul - the ability now reads the
            // shared multiplicative rate instead, so there is nothing per-ability left to pin here.
            //
            // Do not "restore" these two assertions to find out what the keys hold. An unregistered key
            // does not fail cleanly: PropertyManager.GetDouble misses the seeded cache and falls through
            // to a live shard-config read, which throws a MySqlException that reads like an unrelated
            // infrastructure fault rather than a missing key.
        }
    }
}
