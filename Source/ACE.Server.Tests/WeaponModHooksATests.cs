using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;

using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;
using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Weapon Mods v4 Phase 2, hook group A: Efficiency, Longevity and Quick Refresh (see
    /// Docs/WeaponMods/DESIGN.md and WeaponModRegistry.cs's Tier B v4 remarks). Only the pure arithmetic is
    /// testable without a live Player - WeaponModCombat.CostMultiplier for Efficiency. Longevity's duration
    /// multiply (EnchantmentManager.Add/BuildEntry) and Quick Refresh's
    /// Player.GetWeaponModCastSpeedMod (Player_WeaponMods_Casting.cs) both need a live Player/caster and are
    /// queued in Docs/VERIFY-QUEUE.md instead, the same treatment WeaponModTierBTests gives the other
    /// Player-only hooks.
    ///
    /// LONGEVITY'S !isWeaponSpell GUARD (code review, 2026-08-17) IS UNTESTED HERE FOR THE SAME REASON:
    /// EnchantmentManager.Add/BuildEntry take a live Player as caster (to read
    /// GetCasterOnlyModValue off the equipped wand) and a Spell built from a real spell id, neither of
    /// which ACE.Server.Tests can construct without a live server - see the grep in
    /// StageTestCommandsTests.cs's remarks confirming no test in this project constructs a Player. The
    /// condition itself - `caster is Player && caster == WorldObject && !isWeaponSpell && spell.IsBeneficial`
    /// - mirrors the pre-existing AugmentationIncreasedSpellDuration guard two lines above it at both sites,
    /// which carries the same !isWeaponSpell exclusion for the same reason: a wand's built-in self-targeting
    /// beneficial spell (WorldObject_Magic.CreateEnchantment passes caster = this with isWeaponSpell = true)
    /// must not additionally benefit from a weapon mod rolled on that same wand. Queued in
    /// Docs/VERIFY-QUEUE.md alongside the rest of Longevity/Quick Refresh's live-loop verification.
    /// </summary>
    [TestClass]
    public class WeaponModHooksATests
    {
        [TestMethod]
        public void CostMultiplier_ClampsAndReduces()
        {
            Assert.AreEqual(1.0, WeaponModCombat.CostMultiplier(0.0), 1e-15, "no Efficiency rolled: full cost");
            Assert.AreEqual(0.5, WeaponModCombat.CostMultiplier(0.5), 1e-15, "half the fraction, half the cost");
            Assert.AreEqual(0.0, WeaponModCombat.CostMultiplier(1.2), 1e-15, "a fraction above 1.0 clamps rather than paying the player");
            Assert.AreEqual(1.0, WeaponModCombat.CostMultiplier(-0.5), 1e-15, "a negative fraction must never INCREASE cost");
            Assert.AreEqual(0.75, WeaponModCombat.CostMultiplier(0.25), 1e-15);
        }

        [TestMethod]
        public void CostMultiplier_AtEfficiencyMaxRoll()
        {
            // Efficiency's MaxRoll is 0.50 - a max-rolled item halves the cost, never more.
            var maxRoll = WeaponModRegistry.Get(WeaponModId.Efficiency).MaxRoll;

            Assert.AreEqual(0.50, maxRoll, 1e-15);
            Assert.AreEqual(0.50, WeaponModCombat.CostMultiplier(maxRoll), 1e-15);
        }

        private static readonly ObjectGuid Caster = new ObjectGuid(0x50000001);
        private static readonly ObjectGuid Armor = new ObjectGuid(0x80000abc);

        private static Dictionary<ObjectGuid, WorldObject> Equipped(params ObjectGuid[] guids)
        {
            var d = new Dictionary<ObjectGuid, WorldObject>();
            foreach (var g in guids)
                d[g] = null;
            return d;
        }

        [TestMethod]
        public void LongevitySelfTarget_CasterIsOwner()
        {
            Assert.IsTrue(EnchantmentManager.IsLongevitySelfTarget(Caster, Caster, Equipped()), "a self buff");
        }

        [TestMethod]
        public void LongevitySelfTarget_BaneOnCastersWornArmor()
        {
            // The owner is the armor (its own guid), present in the caster's EquippedObjects: first cast and re-cast both use this test.
            Assert.IsTrue(EnchantmentManager.IsLongevitySelfTarget(Caster, Armor, Equipped(Armor)));
        }

        [TestMethod]
        public void LongevitySelfTarget_ItemInPackDoesNotCount()
        {
            Assert.IsFalse(EnchantmentManager.IsLongevitySelfTarget(Caster, Armor, Equipped()));
        }

        [TestMethod]
        public void LongevitySelfTarget_StaleWielderIdButNotEquippedDoesNotCount()
        {
            // The persisted WielderId can be stale (double-parented rows); only runtime equipment counts, and the
            // helper takes no WielderId at all. An item that is not in EquippedObjects is not self, whatever its row says.
            Assert.IsFalse(EnchantmentManager.IsLongevitySelfTarget(Caster, Armor, Equipped(new ObjectGuid(0x80000def))));
        }

        [TestMethod]
        public void LongevitySelfTarget_ArmorWornBySomeoneElseDoesNotCount()
        {
            // Another player's bane on armor worn by someone else: the CASTER is not the wearer, so the armor is
            // absent from the caster's own EquippedObjects.
            Assert.IsFalse(EnchantmentManager.IsLongevitySelfTarget(Caster, Armor, Equipped()));
        }

        [TestMethod]
        public void LongevitySelfTarget_NullEquipmentIsSafe()
        {
            Assert.IsFalse(EnchantmentManager.IsLongevitySelfTarget(Caster, Armor, null));
        }

        [TestMethod]
        public void EnchantmentManager_CallsLongevityHelperExactlyTwice_EachWithTheSpellGuards()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldObjects", "Managers", "EnchantmentManager.cs");
                if (File.Exists(candidate)) { path = candidate; break; }
                dir = dir.Parent;
            }
            Assert.IsNotNull(path, "could not find EnchantmentManager.cs by walking up from the test directory");

            var calls = File.ReadAllLines(path).Where(l => l.Contains("IsLongevitySelfTarget(") && l.Contains("if (")).ToList();

            Assert.AreEqual(2, calls.Count, "Longevity is wired at exactly two sites (BuildEntry first cast, Add re-cast)");
            foreach (var line in calls)
                StringAssert.Contains(line, "&& !isWeaponSpell && spell.IsBeneficial", "each Longevity site keeps both spell guards");
        }
    }
}
