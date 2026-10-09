using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Removing one enchantment must never delete a DIFFERENT enchantment that shares its spell id and caster.
    ///
    /// PropertiesEnchantmentRegistryExtensions.TryRemoveEnchantment used to match on (SpellId, CasterObjectId)
    /// alone and take the first hit. Two live entries share that pair in shipped code:
    ///   - different CATEGORY: Pocket Sand borrows DF_Specialized_AttackDebuff into the synthetic
    ///     SpellCategory_ClassAbility_PocketSand, beside the same player's real Dirty Fighting debuff in
    ///     DFAttackSkillDebuff (Player_ClassAbilityCombat.TryPocketSand, Creature_Combat's Dirty Fighting);
    ///   - different LAYER of one category: a monster DebuffEffect lays ImperilOther4 in ArmorValueLowering, and
    ///     the same monster's real Imperil IV cast surpasses it onto the next layer.
    /// Expiry (HeartBeat -> Remove), Dispel(entry) and Dispel(list) all routed through that match, so the
    /// second entry's removal deleted the first one instead.
    ///
    /// Every registry below is built with the REAL entry first, because that is the order in which the old
    /// FirstOrDefault picked the wrong object; removing the second entry is the discriminating direction.
    ///
    /// Driven on bare Creatures from TestCreatures, as SneakAttackPocketSandTests does: no Player can be
    /// constructed in this test host, and with no Player and no owner, Remove/Dispel send nothing.
    /// </summary>
    [TestClass]
    public class EnchantmentRemoveMatchTests
    {
        private const EnchantmentTypeFlags AttackDebuff =
            EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.AttackSkills;

        private static SpellCategory PocketSandCategory =>
            (SpellCategory)EnchantmentManager.SpellCategory_ClassAbility_PocketSand;

        private static List<PropertiesEnchantmentRegistry> Registry(WorldObject wo) =>
            wo.Biota.PropertiesEnchantmentRegistry.ToList();

        /// <summary>The entry a real Dirty Fighting attack debuff leaves behind: its own category, layer 1.</summary>
        private static PropertiesEnchantmentRegistry AddRealDirtyFighting(Creature target, Creature caster, double duration)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = (int)SpellId.DF_Specialized_AttackDebuff,
                SpellCategory = SpellCategory.DFAttackSkillDebuff,
                LayerId = 1,
                PowerLevel = 1,
                CasterObjectId = caster.Guid.Full,
                StartTime = 0,
                Duration = duration,
                StatModType = AttackDebuff,
                StatModValue = -20.0f,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, target.BiotaDatabaseLock);

            return entry;
        }

        /// <summary>Pocket Sand, written through the real primitive, from the SAME caster.</summary>
        private static PropertiesEnchantmentRegistry AddPocketSand(Creature target, Creature caster, double duration)
        {
            return target.EnchantmentManager.AddClassAbilityDebuff(
                (uint)SpellId.DF_Specialized_AttackDebuff, 1, caster, PocketSandCategory,
                AttackDebuff, 0, -30.0f, duration);
        }

        /// <summary>
        /// A target holding a real Dirty Fighting debuff (added first) and a Pocket Sand debuff (added second)
        /// from one caster, with the precondition that makes this a collision asserted up front.
        /// </summary>
        private static (Creature Target, PropertiesEnchantmentRegistry Real, PropertiesEnchantmentRegistry Synthetic) BuildCategoryCollision(double realDuration = 60.0, double syntheticDuration = 60.0)
        {
            var target = TestCreatures.CreateQuestBearer("Collision Target");
            var caster = TestCreatures.CreateQuestBearer("Collision Caster");

            var real = AddRealDirtyFighting(target, caster, realDuration);
            var synthetic = AddPocketSand(target, caster, syntheticDuration);

            Assert.AreEqual(2, Registry(target).Count, "precondition: two entries");
            Assert.AreEqual(real.SpellId, synthetic.SpellId, "precondition: same spell id");
            Assert.AreEqual(real.CasterObjectId, synthetic.CasterObjectId, "precondition: same caster");
            Assert.AreNotEqual(real.SpellCategory, synthetic.SpellCategory, "precondition: different categories");
            Assert.AreSame(real, Registry(target)[0], "precondition: the real entry is first, where the old match looked");

            return (target, real, synthetic);
        }

        // ---------------------------------------------------------------------------------------------
        // Remove (the expiry path)
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Remove_SyntheticEntry_LeavesTheRealEntrySharingSpellAndCaster()
        {
            var (target, real, synthetic) = BuildCategoryCollision();

            target.EnchantmentManager.Remove(synthetic);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count, "exactly one entry removed");
            Assert.AreSame(real, remaining[0], "the real Dirty Fighting debuff must survive Pocket Sand's removal");
        }

        [TestMethod]
        public void Remove_RealEntry_LeavesTheSyntheticEntrySharingSpellAndCaster()
        {
            var (target, real, synthetic) = BuildCategoryCollision();

            target.EnchantmentManager.Remove(real);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count, "exactly one entry removed");
            Assert.AreSame(synthetic, remaining[0], "Pocket Sand must survive the real debuff's removal");
        }

        [TestMethod]
        public void Remove_TheSameEntryTwice_NeverFallsThroughToItsSibling()
        {
            var (target, real, synthetic) = BuildCategoryCollision();

            target.EnchantmentManager.Remove(synthetic);
            target.EnchantmentManager.Remove(synthetic);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count, "a stale removal must not take the sibling in another category");
            Assert.AreSame(real, remaining[0]);
        }

        /// <summary>
        /// The real expiry path end to end: HeartBeat ticks every entry and routes each expired one through
        /// Remove. Only Pocket Sand is expired, and only Pocket Sand may go.
        /// </summary>
        [TestMethod]
        public void HeartBeat_ExpiringTheSyntheticEntry_LeavesTheLiveRealEntry()
        {
            var (target, real, _) = BuildCategoryCollision(realDuration: 60.0, syntheticDuration: 5.0);

            target.EnchantmentManager.HeartBeat(5.0);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count, "exactly the expired entry is removed");
            Assert.AreSame(real, remaining[0], "the live Dirty Fighting debuff must not be stripped early");
        }

        [TestMethod]
        public void HeartBeat_ExpiringTheRealEntry_LeavesTheLiveSyntheticEntry()
        {
            var (target, _, synthetic) = BuildCategoryCollision(realDuration: 5.0, syntheticDuration: 60.0);

            target.EnchantmentManager.HeartBeat(5.0);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count, "exactly the expired entry is removed");
            Assert.AreSame(synthetic, remaining[0], "the live Pocket Sand debuff must not be stripped early");
        }

        /// <summary>
        /// The DebuffEffect shape: one spell, one caster, one category, two layers. The category alone does not
        /// tell these apart, so the layer has to be part of the match as well.
        /// </summary>
        [TestMethod]
        public void Remove_SameCategoryDifferentLayer_RemovesOnlyThatLayer()
        {
            var target = TestCreatures.CreateQuestBearer("Layer Target");
            var caster = TestCreatures.CreateQuestBearer("Layer Caster");

            PropertiesEnchantmentRegistry Imperil(ushort layer, uint power, float value)
            {
                var entry = new PropertiesEnchantmentRegistry
                {
                    SpellId = (int)SpellId.ImperilOther4,
                    SpellCategory = SpellCategory.ArmorValueLowering,
                    LayerId = layer,
                    PowerLevel = power,
                    CasterObjectId = caster.Guid.Full,
                    Duration = 60.0,
                    StatModType = EnchantmentTypeFlags.BodyArmorValue | EnchantmentTypeFlags.Additive,
                    StatModValue = value,
                    EnchantmentCategory = (uint)SpellType.Enchantment,
                };
                target.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, target.BiotaDatabaseLock);
                return entry;
            }

            var lower = Imperil(1, 1, -50.0f);
            var upper = Imperil(2, 200, -150.0f);

            target.EnchantmentManager.Remove(upper);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count);
            Assert.AreSame(lower, remaining[0], "removing layer 2 must not take layer 1 of the same spell, caster and category");
        }

        // ---------------------------------------------------------------------------------------------
        // Dispel
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Dispel_SyntheticEntry_LeavesTheRealEntrySharingSpellAndCaster()
        {
            var (target, real, synthetic) = BuildCategoryCollision();

            target.EnchantmentManager.Dispel(synthetic);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count, "exactly one entry dispelled");
            Assert.AreSame(real, remaining[0]);
        }

        [TestMethod]
        public void DispelList_SyntheticEntry_LeavesTheRealEntrySharingSpellAndCaster()
        {
            var (target, real, synthetic) = BuildCategoryCollision();

            target.EnchantmentManager.Dispel(new List<PropertiesEnchantmentRegistry> { synthetic });

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count, "exactly one entry dispelled");
            Assert.AreSame(real, remaining[0]);
        }

        [TestMethod]
        public void DispelList_BothCollidingEntries_RemovesBoth()
        {
            var (target, real, synthetic) = BuildCategoryCollision();

            target.EnchantmentManager.Dispel(new List<PropertiesEnchantmentRegistry> { synthetic, real });

            Assert.AreEqual(0, Registry(target).Count);
        }

        // ---------------------------------------------------------------------------------------------
        // No collision: behaviour unchanged
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Remove_NoCollision_RemovesTheOnlyEntry()
        {
            var target = TestCreatures.CreateQuestBearer("Solo Target");
            var caster = TestCreatures.CreateQuestBearer("Solo Caster");

            var real = AddRealDirtyFighting(target, caster, 60.0);

            target.EnchantmentManager.Remove(real);

            Assert.AreEqual(0, Registry(target).Count);
        }

        [TestMethod]
        public void Remove_NoCollision_UnrelatedSpellSurvives()
        {
            var target = TestCreatures.CreateQuestBearer("Unrelated Target");
            var caster = TestCreatures.CreateQuestBearer("Unrelated Caster");

            var real = AddRealDirtyFighting(target, caster, 60.0);
            var other = target.EnchantmentManager.AddClassAbilityDebuff(
                (uint)SpellId.ImperilOther4, 1, caster, SpellCategory.ArmorValueLowering,
                EnchantmentTypeFlags.BodyArmorValue | EnchantmentTypeFlags.Additive, 0, -50.0f, 60.0);

            target.EnchantmentManager.Dispel(real);

            var remaining = Registry(target);
            Assert.AreEqual(1, remaining.Count);
            Assert.AreSame(other, remaining[0]);
        }

        // ---------------------------------------------------------------------------------------------
        // PropertiesEnchantmentRegistryExtensions.TryRemoveEnchantment directly
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void TryRemoveEnchantment_ValueEqualCopy_StillRemovesItsOriginal()
        {
            // A caller holding a copy rather than the registry's own object must keep working: the fallback
            // matches on the full (SpellId, CasterObjectId, SpellCategory, LayerId) identity.
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            var original = new PropertiesEnchantmentRegistry { SpellId = 100, CasterObjectId = 7, SpellCategory = SpellCategory.DFAttackSkillDebuff, LayerId = 1 };
            var registry = new List<PropertiesEnchantmentRegistry> { original };

            var copy = new PropertiesEnchantmentRegistry { SpellId = 100, CasterObjectId = 7, SpellCategory = SpellCategory.DFAttackSkillDebuff, LayerId = 1 };

            Assert.IsTrue(registry.TryRemoveEnchantment(copy, rwLock));
            Assert.AreEqual(0, registry.Count);
        }

        [TestMethod]
        public void TryRemoveEnchantment_CopyFromAnotherCategoryOrLayer_RemovesNothing()
        {
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            var original = new PropertiesEnchantmentRegistry { SpellId = 100, CasterObjectId = 7, SpellCategory = SpellCategory.DFAttackSkillDebuff, LayerId = 1 };
            var registry = new List<PropertiesEnchantmentRegistry> { original };

            var otherCategory = new PropertiesEnchantmentRegistry { SpellId = 100, CasterObjectId = 7, SpellCategory = PocketSandCategory, LayerId = 1 };
            var otherLayer = new PropertiesEnchantmentRegistry { SpellId = 100, CasterObjectId = 7, SpellCategory = SpellCategory.DFAttackSkillDebuff, LayerId = 2 };

            Assert.IsFalse(registry.TryRemoveEnchantment(otherCategory, rwLock));
            Assert.IsFalse(registry.TryRemoveEnchantment(otherLayer, rwLock));
            Assert.AreEqual(1, registry.Count);
            Assert.AreSame(original, registry[0]);
        }

        [TestMethod]
        public void TryRemoveEnchantment_NullEntryOrNullRegistry_ReturnsFalse()
        {
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
            var registry = new List<PropertiesEnchantmentRegistry> { new PropertiesEnchantmentRegistry { SpellId = 1 } };

            Assert.IsFalse(registry.TryRemoveEnchantment(null, rwLock));
            Assert.IsFalse(((ICollection<PropertiesEnchantmentRegistry>)null).TryRemoveEnchantment(new PropertiesEnchantmentRegistry(), rwLock));
            Assert.AreEqual(1, registry.Count);
        }
    }
}
