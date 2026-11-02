using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

using ACE.Database.Adapter;
using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// SetBonusMaxLevel: past the dat's top tier (V for every cloak set), an upgraded cloak's tier-V spell keeps
    /// scaling linearly with level. Weave tier V is StatModValue 10, so level 10 lands at 20. Retail sets stay at 1.0.
    /// The dat HighestTier is a parameter here (5, the cloak sets' top tier), so no dat file is read.
    /// The Player wiring (equip, unequip, level-up) is pinned by a source scan; relog needs no code, because a set
    /// enchantment persists on the character's enchantment registry with the StatModValue it was built with.
    /// </summary>
    [TestClass]
    public class SetBonusScalingTests
    {
        private const uint CloakTopTier = 5;
        private const float WeaveTierV = 10f;

        private static float Scaled(int level, int? cap)
        {
            var items = new List<WorldObject> { UpgradedCloakTestItems.SetItem(EquipmentSet.CloakMeleeDefense, level, 10, cap) };

            var scale = WorldObject.SetSpellScale(WorldObject.SetLevel(items), WorldObject.SetBonusMaxLevelOf(items), CloakTopTier);

            // the same helper BuildEntry calls
            return WorldObject.ApplySetSpellScale(WeaveTierV, scale);
        }

        [TestMethod]
        public void ApplySetSpellScale_IsTheIdentityAtOne_AndLinearOtherwise()
        {
            Assert.AreEqual(10f, WorldObject.ApplySetSpellScale(10f, 1.0));
            Assert.AreEqual(0.1f, WorldObject.ApplySetSpellScale(0.1f, 1.0), "no float round trip at 1.0");
            Assert.AreEqual(12f, WorldObject.ApplySetSpellScale(10f, 1.2));
            Assert.AreEqual(20f, WorldObject.ApplySetSpellScale(10f, 2.0));
            Assert.AreEqual(-20f, WorldObject.ApplySetSpellScale(-10f, 2.0));
        }

        /// <summary>
        /// Relog: a set enchantment is not rebuilt at login, it is read back from the character's enchantment registry.
        /// The scaled StatModValue must survive both the in-memory registry clone and the shard entity conversion
        /// (BiotaConverter, the save/load path) unchanged.
        /// </summary>
        [TestMethod]
        public void ScaledEntry_SurvivesRegistryCloneAndShardRoundTrip()
        {
            var entry = new PropertiesEnchantmentRegistry
            {
                SpellId = 5697,
                SpellCategory = (SpellCategory)1,
                LayerId = 1,
                PowerLevel = 1,
                Duration = -1,
                StartTime = 0,
                CasterObjectId = 0x7F280001,
                StatModType = EnchantmentTypeFlags.Skill,
                StatModKey = 6,
                StatModValue = 14f,
                HasSpellSetId = true,
                SpellSetId = EquipmentSet.CloakMeleeDefense,
            };

            var registry = new List<PropertiesEnchantmentRegistry> { entry };
            var clone = registry.Clone(new ReaderWriterLockSlim());
            Assert.AreEqual(14f, clone.Single().StatModValue);

            var biota = new Biota { Id = 0x50000001, WeenieClassId = 1, PropertiesEnchantmentRegistry = registry };
            var dbBiota = BiotaConverter.ConvertFromEntityBiota(biota);
            var back = BiotaConverter.ConvertToEntityBiota(dbBiota);

            var loaded = back.PropertiesEnchantmentRegistry.Single();
            Assert.AreEqual(14f, loaded.StatModValue);
            Assert.AreEqual(5697, loaded.SpellId);
            Assert.AreEqual(EquipmentSet.CloakMeleeDefense, loaded.SpellSetId);
            Assert.IsTrue(loaded.HasSpellSetId);
        }

        [TestMethod]
        public void Upgraded_Level5_6_10()
        {
            Assert.AreEqual(10f, Scaled(5, 10));
            Assert.AreEqual(12f, Scaled(6, 10));
            Assert.AreEqual(20f, Scaled(10, 10));
        }

        [TestMethod]
        public void Upgraded_BelowTheTopTier_IsUnscaled()
        {
            // below V the dat tier itself carries the value (I-IV); the scale is exactly 1
            for (var level = 1; level <= 5; level++)
                Assert.AreEqual(1.0, WorldObject.SetSpellScale(level, 10, CloakTopTier), $"level {level}");
        }

        [TestMethod]
        public void Retail_IsUnchangedAtItsTier()
        {
            Assert.AreEqual(WeaveTierV, Scaled(5, null));
            Assert.AreEqual(WeaveTierV, Scaled(10, null), "even a level-10 item without the stamp stops at tier V");
            Assert.AreEqual(1.0, WorldObject.SetSpellScale(10, 0, CloakTopTier));
            Assert.AreEqual(1.0, WorldObject.SetSpellScale(10, 5, CloakTopTier), "a cap at the top tier is retail");
            Assert.AreEqual(1.0, WorldObject.SetSpellScale(10, 10, 0), "an unknown set never scales");
        }

        [TestMethod]
        public void Retail_GetSpellSetScale_ExitsBeforeAnyDatRead()
        {
            var items = new List<WorldObject> { UpgradedCloakTestItems.SetItem(EquipmentSet.CloakMeleeDefense, 5, 5, null) };

            // no SetBonusMaxLevel on any item: GetSpellSetScale returns before its dat lookup
            Assert.AreEqual(1.0, WorldObject.GetSpellSetScale(items));
            Assert.AreEqual(1.0, WorldObject.GetSpellSetScale(new List<WorldObject>()));
        }

        [TestMethod]
        public void Scale_CapsAtSetBonusMaxLevel_AndUsesTheHighestCap()
        {
            Assert.AreEqual(2.0, WorldObject.SetSpellScale(14, 10, CloakTopTier), 1e-12);

            var items = new List<WorldObject>
            {
                UpgradedCloakTestItems.SetItem(EquipmentSet.CloakMeleeDefense, 3, 10, null),
                UpgradedCloakTestItems.SetItem(EquipmentSet.CloakMeleeDefense, 4, 10, 10),
            };

            Assert.AreEqual(10, WorldObject.SetBonusMaxLevelOf(items));
            Assert.AreEqual(7, WorldObject.SetLevel(items));
            Assert.AreEqual(6, WorldObject.SetLevel(items, -1), "levelDiff feeds the level-up previous state");
        }

        [TestMethod]
        public void Wiring_BuildEntryScales_AndEveryPlayerSetPathRescales()
        {
            var enchantments = CloakProcMagnitudeTests.Source("Source/ACE.Server/WorldObjects/Managers/EnchantmentManager.cs");
            StringAssert.Matches(enchantments, new Regex(@"entry\.SpellSetId = \(EquipmentSet\)caster\.EquipmentSetId;[\s\S]{0,600}?WorldObject\.GetSpellSetScale\(caster, WorldObject\)"));

            var spells = CloakProcMagnitudeTests.Source("Source/ACE.Server/WorldObjects/Player_Spells.cs");

            foreach (var method in new[] { "EquipItemFromSet", "DequipItemFromSet", "OnItemLevelUp" })
            {
                var body = Regex.Match(spells, @"public void " + method + @"\(WorldObject item(, int prevItemLevel)?\)\s*\{(?<body>[\s\S]*?)\n        \}");
                Assert.IsTrue(body.Success, method + " not found");
                StringAssert.Contains(body.Groups["body"].Value, "RescaleSetSpells(", method + " must rebuild a set spell whose scale moved");
                StringAssert.Contains(body.Groups["body"].Value, "GetSpellSetScale(setItems", method);
            }
        }
    }
}
