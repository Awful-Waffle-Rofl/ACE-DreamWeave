using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WeaponMods;

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
    }
}
