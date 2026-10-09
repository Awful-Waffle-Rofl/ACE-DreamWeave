using System;
using System.IO;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The equipped-item half of the Threads combat-trait strip (dynamic_dungeons_strip_combat_traits): the
    /// live incident it fixes is boss wcid 24496 General Garsh wielding axe wcid 24567, which carried
    /// IgnoreMagicResist/IgnoreMagicArmor on the WEAPON's own row while the boss's own copies were clean, so
    /// every player armor buff, bane and protection was ignored (Monster_Melee.cs:429-430 reads
    /// (weapon?.Flag ?? false) || attacker.Flag).
    ///
    /// DungeonCreatureNormalizer.StripCombatTraits itself cannot be exercised here: it takes a live Creature,
    /// and ACE.Server.Tests cannot construct one (Creature's constructor runs SetEphemeralValues, which needs
    /// a live PropertyManager and DatabaseManager.World - see ThreadDungeonSpawnerLootStripTests.cs's "NOT
    /// COVERED HERE" note for the identical constraint on StripCorpseTransfer). StripItemCombatTraits was
    /// split out specifically so the per-item half is testable without one: it takes a bare WorldObject, and
    /// GenericObject/MeleeWeapon have no such dependency.
    /// </summary>
    [TestClass]
    public class DungeonCreatureNormalizerItemTraitStripTests
    {
        private static WorldObject MakeItem(uint wcid, uint guid) =>
            new GenericObject(new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic }, new ObjectGuid(guid));

        [TestMethod]
        public void Strips_every_bypass_bool_the_item_carries()
        {
            var item = MakeItem(24567, 0x70000001);

            item.SetProperty(PropertyBool.IgnoreMagicResist, true);
            item.SetProperty(PropertyBool.IgnoreMagicArmor, true);

            var changes = DungeonCreatureNormalizer.StripItemCombatTraits(item);

            CollectionAssert.AreEquivalent(new[] { "IgnoreMagicResist", "IgnoreMagicArmor" }, changes);
            Assert.IsNull(item.GetProperty(PropertyBool.IgnoreMagicResist), "General Garsh's axe 24567 must lose IgnoreMagicResist");
            Assert.IsNull(item.GetProperty(PropertyBool.IgnoreMagicArmor), "General Garsh's axe 24567 must lose IgnoreMagicArmor");
        }

        [TestMethod]
        public void Strips_every_bypass_float_the_item_carries()
        {
            var item = MakeItem(30000, 0x70000001);

            item.SetProperty(PropertyFloat.IgnoreShield, 1.0);
            item.SetProperty(PropertyFloat.IgnoreArmor, 0.5);
            item.SetProperty(PropertyFloat.AbsorbMagicDamage, 0.25);

            var changes = DungeonCreatureNormalizer.StripItemCombatTraits(item);

            CollectionAssert.AreEquivalent(new[] { "IgnoreShield", "IgnoreArmor", "AbsorbMagicDamage" }, changes);
            Assert.IsFalse(item.GetProperty(PropertyFloat.IgnoreShield).HasValue);
            Assert.IsFalse(item.GetProperty(PropertyFloat.IgnoreArmor).HasValue);
            Assert.IsFalse(item.GetProperty(PropertyFloat.AbsorbMagicDamage).HasValue);
        }

        [TestMethod]
        public void Strips_every_bypass_int_the_item_carries()
        {
            var item = MakeItem(30000, 0x70000001);

            item.SetProperty(PropertyInt.Overpower, 1);

            var changes = DungeonCreatureNormalizer.StripItemCombatTraits(item);

            CollectionAssert.AreEquivalent(new[] { "Overpower" }, changes);
            Assert.IsFalse(item.GetProperty(PropertyInt.Overpower).HasValue);
        }

        [TestMethod]
        public void An_item_with_no_bypass_traits_is_a_no_op()
        {
            var item = MakeItem(30000, 0x70000001);

            var changes = DungeonCreatureNormalizer.StripItemCombatTraits(item);

            Assert.AreEqual(0, changes.Count);
        }

        [TestMethod]
        public void A_null_item_is_tolerated()
        {
            var changes = DungeonCreatureNormalizer.StripItemCombatTraits(null);

            Assert.AreEqual(0, changes.Count);
        }

        [TestMethod]
        public void Does_not_touch_a_sibling_items_copy_of_the_same_wcid()
        {
            // Per-instance safety, the same property this file's class remarks cite: two items built from
            // the same wcid must not share a PropertiesBool dictionary.
            var weenie = new Weenie { WeenieClassId = 24567, WeenieType = WeenieType.Generic };
            var strippedInstance = new GenericObject(weenie, new ObjectGuid(0x70000001));
            var siblingInstance = new GenericObject(weenie, new ObjectGuid(0x70000002));

            strippedInstance.SetProperty(PropertyBool.IgnoreMagicResist, true);
            siblingInstance.SetProperty(PropertyBool.IgnoreMagicResist, true);

            DungeonCreatureNormalizer.StripItemCombatTraits(strippedInstance);

            Assert.IsNull(strippedInstance.GetProperty(PropertyBool.IgnoreMagicResist));
            Assert.AreEqual(true, siblingInstance.GetProperty(PropertyBool.IgnoreMagicResist),
                "stripping one instance's weapon must not reach a sibling instance of the same wcid");
        }

        /// <summary>
        /// Structural guard for the half of the fix that has no live-Creature test: that StripCombatTraits
        /// actually sweeps both creature.EquippedObjects.Values AND creature.Inventory.Values through
        /// StripItemCombatTraits, and tags each change with the item's wcid for the spawner's log line. The
        /// Inventory half exists because Monster_Missile.SwitchToMeleeAttack and Monster_Tick's ammo-out path
        /// both re-equip a melee/missile backup straight out of Inventory mid-fight with no strip in between
        /// (Monster_Missile.cs:222-256, Monster_Inventory.cs:323-345, Monster_Tick.cs:135) - a hollow backup
        /// weapon would otherwise go live only once switched to. Fails against the pre-fix source, where
        /// StripCombatTraits only ever read/removed properties on the creature itself and StripItemCombatTraits
        /// did not exist (verified 2026-09-14 via `git show HEAD:.../DungeonCreatureNormalizer.cs` on the base
        /// commit, which greps zero hits for EquippedObjects in this file); the Inventory sweep is equally
        /// absent from the first version of this fix (verified against this session's own prior commit, which
        /// strips only EquippedObjects.Values).
        /// </summary>
        [TestMethod]
        public void StripCombatTraits_sweeps_equipped_and_inventory_items_and_tags_the_wcid()
        {
            var repoRoot = FindRepoRoot();
            var path = Path.Combine(repoRoot, "Source", "ACE.Server", "ThreadDungeons", "CombatTraitGuard.cs");

            Assert.IsTrue(File.Exists(path), $"CombatTraitGuard.cs not found at {path}.");

            var source = File.ReadAllText(path);

            StringAssert.Contains(source, "creature.EquippedObjects.Values.Concat(creature.Inventory.Values)",
                "StripCombatTraits must sweep both EquippedObjects and Inventory, not EquippedObjects alone.");
            StringAssert.Contains(source, "DungeonCreatureNormalizer.StripItemCombatTraits(item)",
                "StripCombatTraits must delegate each item to StripItemCombatTraits.");
            StringAssert.Contains(source, "@{item.WeenieClassId}",
                "each item change must be tagged with the item's wcid for the spawner's log line.");
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Source", "property-registry.tsv")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new AssertFailedException($"Could not find the repo root by walking up from {AppContext.BaseDirectory}.");
        }
    }
}
