using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The layer-allocation invariant behind the prod duplicate-entry disconnects of 2026-09-06 through
    /// 09-08: no object may hold two enchantment-registry entries with the same (SpellId, LayerId).
    ///
    /// It is not a style rule, it is the shard schema. biota_properties_enchantment_registry carries a UNIQUE
    /// index over (object_Id, spell_Id, layer_Id) that is NARROWER than its primary key
    /// (object_Id, spell_Id, caster_Object_Id, layer_Id), so a registry that distinguishes two entries only by
    /// caster is unpersistable: the save fails with MySQL 1062 on both attempts, Player_Tick sees
    /// BiotaSaveFailed and drops the player to "Server could not access your account information".
    ///
    /// <see cref="EnchantmentManager.AddClassAbilityDebuff(uint, uint, WorldObject, SpellCategory, EnchantmentTypeFlags, uint, float, double, bool)"/>
    /// used to write a hardcoded layer 1, which is only safe while nothing else can hold the same spell id.
    /// Tinkerer's Inspiration breaks exactly that assumption: it reuses REAL retail spell ids in their real
    /// categories with refreshOnlyOwnCaster, so its refresh lookup deliberately ignores the player's own cast
    /// of the same spell and then laid a second entry beside it at the same layer.
    ///
    /// No Player and no Spell is constructed here, for the reasons ClassAbilityEnchantmentBandTests documents:
    /// a Player fails in this test host's static initializer, and Spell reads the client dat. That is why the
    /// Spell-free overload of AddClassAbilityDebuff is the virtual one.
    /// </summary>
    [TestClass]
    public class EnchantmentLayerAllocationTests
    {
        /// <summary>Incantation of Magic Item Tinkering Expertise Self - one of the four spells Tinkerer's Inspiration reuses, and one of the two that actually failed in prod.</summary>
        private const uint TinkeringSpellId = 4592;

        private const EnchantmentTypeFlags SkillBuff =
            EnchantmentTypeFlags.Skill | EnchantmentTypeFlags.Additive | EnchantmentTypeFlags.Beneficial;

        private static WorldObject CreateBareWorldObject(uint id)
        {
            var biota = new Biota
            {
                Id = id,
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
            };

            return new GenericObject(biota);
        }

        /// <summary>
        /// The entry a player's own self-cast Incantation leaves behind: the real spell id, the real category,
        /// layer 1, caster = the player. Written straight into the registry so no Spell is needed.
        /// </summary>
        private static void AddOwnSelfCast(WorldObject wo, uint spellId, SpellCategory category, uint powerLevel)
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = (int)spellId,
                SpellCategory = category,
                LayerId = 1,
                PowerLevel = powerLevel,
                StartTime = 0,
                Duration = 5400,
                CasterObjectId = wo.Guid.Full,
                StatModType = SkillBuff,
                StatModKey = (uint)Skill.MagicItemTinkering,
                StatModValue = 45f,
                EnchantmentCategory = (uint)SpellType.Enchantment,
            };

            wo.Biota.PropertiesEnchantmentRegistry.AddEnchantment(entry, wo.BiotaDatabaseLock);
        }

        private static void AssertNoDuplicateSpellLayerPairs(WorldObject wo)
        {
            var duplicates = wo.Biota.PropertiesEnchantmentRegistry
                .GroupBy(e => (e.SpellId, e.LayerId))
                .Where(g => g.Count() > 1)
                .Select(g => $"spell {g.Key.SpellId} layer {g.Key.LayerId} x{g.Count()}")
                .ToList();

            Assert.AreEqual(0, duplicates.Count,
                $"the shard unique index cannot store these: {string.Join(", ", duplicates)}");
        }

        [TestMethod]
        public void AddClassAbilityDebuff_BesideTheTargetsOwnCastOfTheSameSpell_UsesAFreeLayer()
        {
            var target = CreateBareWorldObject(0x50000001);
            var station = CreateBareWorldObject(0x70000001);

            AddOwnSelfCast(target, TinkeringSpellId, SpellCategory.CraftingMagicTinkeringRaising, 100);

            var added = target.EnchantmentManager.AddClassAbilityDebuff(TinkeringSpellId, 101, station,
                SpellCategory.CraftingMagicTinkeringRaising, SkillBuff, (uint)Skill.MagicItemTinkering,
                60f, 60.0, refreshOnlyOwnCaster: true);

            Assert.IsNotNull(added);
            Assert.AreEqual(station.Guid.Full, added.CasterObjectId, "refreshOnlyOwnCaster must not have matched the target's own cast");
            Assert.AreEqual(2, target.Biota.PropertiesEnchantmentRegistry.Count, "this is a second layer, not a refresh");
            Assert.AreNotEqual((ushort)1, added.LayerId, "layer 1 is taken by the target's own cast of this spell");
            AssertNoDuplicateSpellLayerPairs(target);
        }

        [TestMethod]
        public void AddClassAbilityDebuff_WithNothingElseOnThatSpell_StillUsesLayerOne()
        {
            var target = CreateBareWorldObject(0x50000002);
            var caster = CreateBareWorldObject(0x70000002);

            var added = target.EnchantmentManager.AddClassAbilityDebuff(9001, 1, caster,
                (SpellCategory)(EnchantmentManager.SpellCategory_ClassAbility_Base + 1), SkillBuff, 0, -10f, 20.0);

            Assert.AreEqual((ushort)1, added.LayerId, "the ordinary class-ability case must be unchanged");
        }

        [TestMethod]
        public void AddClassAbilityDebuff_DifferentSpellsInOneCategory_AllStayAtLayerOne()
        {
            // The cooldown shape: one synthetic category, many distinct spell ids, and every one of them
            // legitimately at layer 1. The free-layer search is keyed on the spell id alone precisely so it
            // cannot renumber these - the unique index does not care that they share a category.
            var target = CreateBareWorldObject(0x50000003);
            var caster = CreateBareWorldObject(0x70000003);
            var category = (SpellCategory)(EnchantmentManager.SpellCategory_ClassAbility_Base + 2);

            var layers = new List<ushort>();

            foreach (var spellId in new uint[] { 9101, 9102, 9103 })
                layers.Add(target.EnchantmentManager.AddClassAbilityDebuff(spellId, 1, caster, category, SkillBuff, 0, -5f, 20.0).LayerId);

            CollectionAssert.AreEqual(new[] { (ushort)1, (ushort)1, (ushort)1 }, layers);
            AssertNoDuplicateSpellLayerPairs(target);
        }

        [TestMethod]
        public void AddClassAbilityDebuff_SecondApplicationFromTheSameCaster_RefreshesInsteadOfLayering()
        {
            var target = CreateBareWorldObject(0x50000004);
            var station = CreateBareWorldObject(0x70000004);

            var first = target.EnchantmentManager.AddClassAbilityDebuff(TinkeringSpellId, 101, station,
                SpellCategory.CraftingMagicTinkeringRaising, SkillBuff, (uint)Skill.MagicItemTinkering,
                60f, 60.0, refreshOnlyOwnCaster: true);

            first.StartTime = -30;

            var second = target.EnchantmentManager.AddClassAbilityDebuff(TinkeringSpellId, 101, station,
                SpellCategory.CraftingMagicTinkeringRaising, SkillBuff, (uint)Skill.MagicItemTinkering,
                60f, 60.0, refreshOnlyOwnCaster: true);

            Assert.AreSame(first, second, "a repeat application from the same caster must refresh its own entry");
            Assert.AreEqual(1, target.Biota.PropertiesEnchantmentRegistry.Count);
            Assert.AreEqual(0, second.StartTime);
        }

        [TestMethod]
        public void AddEnchantmentAtFreeLayer_FillsTheLowestFreeLayerForThatSpell()
        {
            var registry = new List<PropertiesEnchantmentRegistry>
            {
                new PropertiesEnchantmentRegistry { SpellId = 4592, LayerId = 1 },
                new PropertiesEnchantmentRegistry { SpellId = 4592, LayerId = 3 },
                new PropertiesEnchantmentRegistry { SpellId = 4566, LayerId = 2 },
            };

            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);

            var layer = registry.AddEnchantmentAtFreeLayer(new PropertiesEnchantmentRegistry { SpellId = 4592 }, rwLock);

            Assert.AreEqual((ushort)2, layer, "layer 2 is free for spell 4592 even though another spell is using it");
            Assert.AreEqual(4, registry.Count);
        }
    }
}
