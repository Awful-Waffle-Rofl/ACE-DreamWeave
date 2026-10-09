using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The fork-reserved synthetic spell-category band for class-ability enchantments
    /// (<see cref="EnchantmentManager.SpellCategory_ClassAbility_Base"/>) and the
    /// <see cref="EnchantmentManager.AddClassAbilityDebuff(uint, uint, WorldObject, SpellCategory, EnchantmentTypeFlags, uint, float, double)"/>
    /// primitive built on it. Foundation for Pocket Sand (Rogue T3); the ability itself lands in Phase 1.
    ///
    /// TWO THINGS ARE UNDER TEST, and they are the two ways this primitive can be silently wrong.
    ///
    /// 1. STACKING. Effective values take the top layer of EACH spell category and then SUM across
    ///    categories, so an entry in a category of its own is additive with a retail entry of the same
    ///    StatModType, while a second entry in the SAME category merely duels the first for one slot. The
    ///    private band is the entire reason a class-ability debuff adds to Dirty Fighting's rather than
    ///    replacing it, so the control case (same category, no stacking) is tested alongside.
    ///
    /// 2. CACHE INVALIDATION. EnchantmentManagerWithCaching memoizes GetSkillMod_Additives and
    ///    HasEnchantments for the object's lifetime until something clears them, and this primitive writes
    ///    into the registry WITHOUT going through Add(), which is where that clearing normally happens. A
    ///    stale HasEnchantments is the worse of the two failures: WorldObject_Tick gates the enchantment
    ///    heartbeat on it, so a debuff added to an object with no prior enchantments would never tick down
    ///    and never expire.
    ///
    /// No Player is constructed anywhere here (that fails in this test host's static initializer) and no
    /// Spell is either (Spell's constructor reads the client dat), which is why the Spell-free overload of
    /// AddClassAbilityDebuff is the virtual one.
    /// </summary>
    [TestClass]
    public class ClassAbilityEnchantmentBandTests
    {
        /// <summary>The attack-skill debuff shape retail Dirty Fighting applies (Creature_Combat.cs).</summary>
        private const EnchantmentTypeFlags AttackDebuff =
            EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.AttackSkills;

        /// <summary>Pocket Sand's category: the first slot of the fork band.</summary>
        private static SpellCategory ClassAbilityCategory(int n) =>
            (SpellCategory)(EnchantmentManager.SpellCategory_ClassAbility_Base + n);

        private static WorldObject CreateBareWorldObject()
        {
            var biota = new Biota
            {
                Id = 0x80000042,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        /// <summary>
        /// Writes the entry a retail Dirty Fighting attack debuff produces, directly into the registry -
        /// the same thing EnchantmentManager.Add would leave behind, without needing a Spell or a caster.
        /// </summary>
        private static void AddRetailAttackDebuff(WorldObject wo, int spellId, float value, uint powerLevel)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = spellId,
                SpellCategory = SpellCategory.DFAttackSkillDebuff,
                LayerId = 1,
                PowerLevel = powerLevel,
                StartTime = 0,
                Duration = 20.0,
                StatModType = AttackDebuff,
                StatModKey = 0,
                StatModValue = value,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            wo.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, wo.BiotaDatabaseLock);
        }

        /// <summary>
        /// Pocket Sand's category is what makes its -30 SUM with retail Dirty Fighting's rather than duel it
        /// for one slot, so "distinct from DFAttackSkillDebuff" is the whole mechanism, not a detail. One
        /// named constant per ability, inside the band, never sharing a number.
        /// </summary>
        [TestMethod]
        public void PocketSandCategory_IsInsideTheBandAndDistinctFromDirtyFightings()
        {
            Assert.AreEqual(EnchantmentManager.SpellCategory_ClassAbility_Base + 1,
                EnchantmentManager.SpellCategory_ClassAbility_PocketSand,
                "Pocket Sand holds slot 1 of the band");

            Assert.AreNotEqual((int)SpellCategory.DFAttackSkillDebuff,
                (int)EnchantmentManager.SpellCategory_ClassAbility_PocketSand,
                "sharing Dirty Fighting's category would make the two replace each other instead of summing");

            Assert.AreEqual(684, (int)SpellCategory.DFAttackSkillDebuff,
                "the retail category Pocket Sand must stay clear of");

            Assert.IsTrue(EnchantmentManager.SpellCategory_ClassAbility_PocketSand > 733,
                "clear of every real SpellCategory");
            Assert.IsTrue(EnchantmentManager.SpellCategory_ClassAbility_PocketSand < EnchantmentManager.SpellCategory_Cooldown,
                "clear of the cooldown band");
        }

        /// <summary>
        /// The end-to-end shape Pocket Sand ships: a retail Dirty Fighting attack debuff already on the
        /// creature, plus Pocket Sand's own -30 in the fork band, must SUM through the getter a creature's
        /// attack skill actually reads (CreatureSkill reads GetSkillMod_Additives, which folds in
        /// GetAttackDebuffMod).
        /// </summary>
        [TestMethod]
        public void PocketSand_SumsWithDirtyFightingOnTheAttackersAttackSkills()
        {
            var wo = CreateBareWorldObject();
            var em = wo.EnchantmentManager;

            // retail Dirty Fighting, Specialized: -20 to attack skills
            AddRetailAttackDebuff(wo, (int)SpellId.DF_Specialized_AttackDebuff, -20.0f, 1);
            Assert.AreEqual(-20, em.GetSkillMod_Additives(Skill.HeavyWeapons));

            em.AddClassAbilityDebuff(
                (uint)SpellId.DF_Specialized_AttackDebuff, 1, null,
                (SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_PocketSand,
                AttackDebuff, 0, -30.0f, 20.0);

            Assert.AreEqual(-50, em.GetSkillMod_Additives(Skill.HeavyWeapons),
                "Pocket Sand adds to Blinding Assault rather than replacing it");

            // NOTE the two entries share a SPELL ID and still both count - the grouping is by CATEGORY, so
            // borrowing Dirty Fighting's spell id for the client-facing icon costs nothing.
            Assert.AreEqual(2, wo.Biota.PropertiesEnchantmentRegistry.Count);
        }

        [TestMethod]
        public void TheBandSitsClearOfEveryRealSpellCategoryAndOfCooldowns()
        {
            // real categories top out at 733; 0x8000 is cooldowns. 0x4000 is between them, with room for
            // 0x4000 + n abilities before it could ever meet 0x8000.
            Assert.AreEqual(733, (int)SpellCategory.GauntletCriticalDamageReductionRatingRaising);
            Assert.AreEqual(0x8000, EnchantmentManager.SpellCategory_Cooldown);
            Assert.AreEqual(0x4000, EnchantmentManager.SpellCategory_ClassAbility_Base);

            Assert.IsTrue(EnchantmentManager.SpellCategory_ClassAbility_Base > 733);
            Assert.IsTrue(EnchantmentManager.SpellCategory_ClassAbility_Base < EnchantmentManager.SpellCategory_Cooldown);
        }

        [TestMethod]
        public void ClassAbilityDebuff_SumsWithARetailDebuffOfTheSameStatModType()
        {
            var wo = CreateBareWorldObject();
            var em = wo.EnchantmentManager;

            // NOTE: no "starts at 0" read here on purpose. AddRetailAttackDebuff writes to the registry
            // directly (as EnchantmentManager.Add would) without clearing the caching manager, so an early
            // read would memoize 0 and this test would be measuring the harness rather than the primitive.
            AddRetailAttackDebuff(wo, (int)SpellId.DF_Specialized_AttackDebuff, -20.0f, 1);

            // read it BEFORE adding ours, so the caching manager memoizes the -20. If AddClassAbilityDebuff
            // does not invalidate, the assertion after it reads this stale value back.
            Assert.AreEqual(-20, em.GetSkillMod_Additives(Skill.HeavyWeapons));

            em.AddClassAbilityDebuff(
                (uint)SpellId.DF_Trained_AttackDebuff, 1, null,
                ClassAbilityCategory(1), AttackDebuff, 0, -30.0f, 20.0);

            Assert.AreEqual(-50, em.GetSkillMod_Additives(Skill.HeavyWeapons),
                "a class-ability debuff in its own category must ADD to the retail one, and the caching manager must have been invalidated");

            // a non-attack skill is untouched: the AttackSkills flag still gates which skills it reaches
            Assert.AreEqual(0, em.GetSkillMod_Additives(Skill.Healing));
        }

        /// <summary>
        /// The control for the test above: two entries in the SAME category do not stack, they duel for one
        /// slot. This is what makes the private band load-bearing rather than decorative.
        /// </summary>
        [TestMethod]
        public void TwoEntriesInOneCategory_DoNotStack()
        {
            var wo = CreateBareWorldObject();
            var em = wo.EnchantmentManager;

            AddRetailAttackDebuff(wo, (int)SpellId.DF_Specialized_AttackDebuff, -20.0f, 2);
            AddRetailAttackDebuff(wo, (int)SpellId.DF_Trained_AttackDebuff, -10.0f, 1);

            // one winner per category - the higher PowerLevel - not -30
            Assert.AreEqual(-20, em.GetSkillMod_Additives(Skill.HeavyWeapons));
        }

        /// <summary>
        /// HasEnchantments is cached and gates the enchantment heartbeat in WorldObject_Tick, so a stale
        /// FALSE means the debuff never expires. This is the invalidation failure that would not show up in
        /// the magnitude assertions above.
        /// </summary>
        [TestMethod]
        public void ClassAbilityDebuff_InvalidatesTheHasEnchantmentsCache()
        {
            var wo = CreateBareWorldObject();
            var em = wo.EnchantmentManager;

            Assert.IsFalse(em.HasEnchantments, "precondition: nothing on the object yet (and this caches FALSE)");

            em.AddClassAbilityDebuff(
                (uint)SpellId.DF_Trained_AttackDebuff, 1, null,
                ClassAbilityCategory(1), AttackDebuff, 0, -30.0f, 20.0);

            Assert.IsTrue(em.HasEnchantments,
                "the object now has an enchantment; a stale FALSE here would skip the heartbeat that expires it");
        }

        /// <summary>
        /// Re-applying the same (category, spell) refreshes the existing entry rather than layering a second
        /// one - so a repeated proc cannot stack its own magnitude on itself.
        /// </summary>
        [TestMethod]
        public void ReapplyingTheSameDebuff_RefreshesInsteadOfStacking()
        {
            var wo = CreateBareWorldObject();
            var em = wo.EnchantmentManager;

            var first = em.AddClassAbilityDebuff(
                (uint)SpellId.DF_Trained_AttackDebuff, 1, null,
                ClassAbilityCategory(1), AttackDebuff, 0, -30.0f, 20.0);

            first.StartTime = -15.0;   // as the heartbeat would have ticked it

            var second = em.AddClassAbilityDebuff(
                (uint)SpellId.DF_Trained_AttackDebuff, 1, null,
                ClassAbilityCategory(1), AttackDebuff, 0, -30.0f, 20.0);

            Assert.AreSame(first, second, "the same entry must be refreshed, not replaced by a second layer");
            Assert.AreEqual(0.0, second.StartTime, 1e-9, "the clock is reset");
            Assert.AreEqual(1, wo.Biota.PropertiesEnchantmentRegistry.Count);
            Assert.AreEqual(-30, em.GetSkillMod_Additives(Skill.HeavyWeapons), "magnitude does not stack on itself");
        }
    }
}
