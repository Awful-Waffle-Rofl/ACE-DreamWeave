using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins Creature.SneakAttackChance, the pure proc-chance decision extracted out of
    /// Creature_Combat.GetSneakAttackMod, so the Pocket Sand rider (user ruling, 2026-09-13: while a target
    /// is blinded by Pocket Sand, Sneak Attack against it fires 100% of the time) can be covered without a
    /// live Player. Blind is folded into the same 100% branch as attacking from behind, and must NOT change
    /// any non-blinded case - the trained/specialized Deception tiers and the sub-306 proportional reduction
    /// stay exactly as before.
    /// </summary>
    [TestClass]
    public class SneakAttackPocketSandTests
    {
        [TestMethod]
        public void BlindedFromFront_UntrainedDeception_Is100Percent()
        {
            // Pocket Sand should force the proc even with no Deception investment at all.
            var chance = Creature.SneakAttackChance(behind: false, blinded: true, deceptionClass: SkillAdvancementClass.Untrained, deceptionCurrent: 0);
            Assert.AreEqual(1.0f, chance);
        }

        [TestMethod]
        public void Behind_IsAlways100Percent()
        {
            var chance = Creature.SneakAttackChance(behind: true, blinded: false, deceptionClass: SkillAdvancementClass.Untrained, deceptionCurrent: 0);
            Assert.AreEqual(1.0f, chance);
        }

        [TestMethod]
        public void Front_NotBlinded_UntrainedDeception_IsZero()
        {
            var chance = Creature.SneakAttackChance(behind: false, blinded: false, deceptionClass: SkillAdvancementClass.Untrained, deceptionCurrent: 0);
            Assert.AreEqual(0.0f, chance);
        }

        [TestMethod]
        public void Front_NotBlinded_SpecializedDeceptionAtCap_Is15Percent()
        {
            var chance = Creature.SneakAttackChance(behind: false, blinded: false, deceptionClass: SkillAdvancementClass.Specialized, deceptionCurrent: 306);
            Assert.AreEqual(0.15f, chance, 0.0001f);
        }

        [TestMethod]
        public void Front_NotBlinded_TrainedDeceptionAtHalfCap_Is5Percent()
        {
            // Trained base is 10%, scaled by 153/306 = 0.5 -> 5%.
            var chance = Creature.SneakAttackChance(behind: false, blinded: false, deceptionClass: SkillAdvancementClass.Trained, deceptionCurrent: 153);
            Assert.AreEqual(0.05f, chance, 0.0001f);
        }

        [TestMethod]
        public void BlindedBehind_StaysAt100Percent()
        {
            // Both flags set is not a real GetSneakAttackMod state (blind only matters from the front), but
            // the helper must not regress the behind case if it ever occurs.
            var chance = Creature.SneakAttackChance(behind: true, blinded: true, deceptionClass: SkillAdvancementClass.Untrained, deceptionCurrent: 0);
            Assert.AreEqual(1.0f, chance);
        }

        /// <summary>
        /// Pins <see cref="Creature.SkipsAssessPersonReduction"/>, the other half of the fold: behind and
        /// Pocket Sand blind both bypass the front-only Assess Person damage-rating reduction in
        /// GetSneakAttackMod, and a plain front hit (neither flag set) does not.
        /// </summary>
        [TestMethod]
        public void SkipsAssessPersonReduction_BehindOrBlinded_IsTrue()
        {
            Assert.IsTrue(Creature.SkipsAssessPersonReduction(behind: true, blinded: false));
            Assert.IsTrue(Creature.SkipsAssessPersonReduction(behind: false, blinded: true));
            Assert.IsTrue(Creature.SkipsAssessPersonReduction(behind: true, blinded: true));
        }

        [TestMethod]
        public void SkipsAssessPersonReduction_PlainFront_IsFalse()
        {
            Assert.IsFalse(Creature.SkipsAssessPersonReduction(behind: false, blinded: false));
        }

        /// <summary>
        /// Creature.IsBlindedByPocketSand reads EnchantmentManager.SpellCategory_ClassAbility_PocketSand
        /// caster-agnostically, exactly the same primitive ClassAbilityEnchantmentBandTests exercises for
        /// Pocket Sand's attack-skill debuff itself: a bare Creature (no Player, no dat files - a live
        /// Player fails in this test host's static initializer per TestCreatures' own doc comment) plus
        /// EnchantmentManager.AddClassAbilityDebuff writing directly into the registry. Must fail against
        /// the pre-change Creature, which has no IsBlindedByPocketSand member at all.
        /// </summary>
        [TestMethod]
        public void IsBlindedByPocketSand_FlipsTrueAfterAnEntryIsAddedToItsCategory()
        {
            var creature = TestCreatures.CreateQuestBearer();

            Assert.IsFalse(creature.IsBlindedByPocketSand, "no entry yet - must not read as blinded");

            const EnchantmentTypeFlags attackDebuff =
                EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.AttackSkills;

            creature.EnchantmentManager.AddClassAbilityDebuff(
                (uint)SpellId.DF_Specialized_AttackDebuff, 1, null,
                (SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_PocketSand,
                attackDebuff, 0, -40.0f, 20.0);

            Assert.IsTrue(creature.IsBlindedByPocketSand, "an entry in the Pocket Sand category must read as blinded");
        }
    }
}
