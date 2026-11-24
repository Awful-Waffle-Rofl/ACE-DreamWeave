using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The ward's Thorns bonus (owner ruling 2026-11-05): a standing Runic or Sanguine Ward adds its shield
    /// level to the armor level Thorns reflects from. The pure decision and arithmetic are covered here; the
    /// behaviour through a real Player (GetWardThornsLevel, ApplyThornsReflect) is covered in DefensiveWeaponTests,
    /// whose ThornsPair harness can drive an uninitialised Player.
    /// </summary>
    [TestClass]
    public class WardThornsTests
    {
        [TestMethod]
        public void ThornsLevelFor_NoWardUp_IsZero()
        {
            Assert.AreEqual(0.0, WardShieldMath.ThornsLevelFor(false, false, true, true, 100, 120), 1e-12);
        }

        [TestMethod]
        public void ThornsLevelFor_RunicOnly_IsRunicLevel()
        {
            Assert.AreEqual(100.0, WardShieldMath.ThornsLevelFor(true, false, true, true, 100, 120), 1e-12);
        }

        [TestMethod]
        public void ThornsLevelFor_SanguineOnly_IsSanguineLevel()
        {
            Assert.AreEqual(120.0, WardShieldMath.ThornsLevelFor(false, true, true, true, 100, 120), 1e-12);
        }

        [TestMethod]
        public void ThornsLevelFor_BothUp_IsMaxNotSum()
        {
            Assert.AreEqual(120.0, WardShieldMath.ThornsLevelFor(true, true, true, true, 100, 120), 1e-12, "max, never 220");
            Assert.AreEqual(150.0, WardShieldMath.ThornsLevelFor(true, true, true, true, 150, 100), 1e-12, "max regardless of which ward is larger");
        }

        [TestMethod]
        public void ThornsLevelFor_NonEnemyAttacker_IsZero()
        {
            Assert.AreEqual(0.0, WardShieldMath.ThornsLevelFor(true, true, false, true, 100, 120), 1e-12, "a player (PvP) or self gives 0");
        }

        [TestMethod]
        public void ThornsLevelFor_Disabled_IsZero()
        {
            Assert.AreEqual(0.0, WardShieldMath.ThornsLevelFor(true, true, true, false, 100, 120), 1e-12);
        }

        [TestMethod]
        public void ThornsLevelFor_NegativeLevel_IsZero_AndDownWardIsIgnored()
        {
            Assert.AreEqual(0.0, WardShieldMath.ThornsLevelFor(true, false, true, true, -50, 120), 1e-12, "negative floors at 0 and the down ward's level is ignored");
            Assert.AreEqual(0.0, WardShieldMath.ThornsLevelFor(true, true, true, true, -50, -10), 1e-12);
            Assert.AreEqual(100.0, WardShieldMath.ThornsLevelFor(true, false, true, true, 100, 999), 1e-12, "only the up ward counts");
        }

        [TestMethod]
        public void ThornsLevelFor_HasNoFacingInput_SoARearHitStillGetsTheBonus()
        {
            var method = typeof(WardShieldMath).GetMethod(nameof(WardShieldMath.ThornsLevelFor));
            Assert.IsNotNull(method);

            var names = method.GetParameters().Select(p => p.Name.ToLowerInvariant()).ToArray();
            Assert.IsFalse(names.Any(n => n.Contains("frontal") || n.Contains("angle") || n.Contains("facing")),
                "the Thorns bonus has no frontal gate: " + string.Join(",", names));

            // the same inputs LevelFor refuses from behind still give the bonus here
            Assert.AreEqual(0.0, WardShieldMath.LevelFor(true, false, true, false, true, 100, 100), 1e-12, "control: the shield bonus is frontal-only");
            Assert.AreEqual(100.0, WardShieldMath.ThornsLevelFor(true, false, true, true, 100, 100), 1e-12);
        }

        [TestMethod]
        public void ThornsArmorLevel_AddsTheWardTerm()
        {
            Assert.AreEqual(50.0 + 30.0 + 100.0, DefensiveWeaponMath.ThornsArmorLevel(50.0, 30.0, 100.0), 1e-12);
            Assert.AreEqual(50.0 + 30.0, DefensiveWeaponMath.ThornsArmorLevel(50.0, 30.0, 0.0), 1e-12, "no ward: unchanged");
            Assert.AreEqual(50.0, DefensiveWeaponMath.ThornsArmorLevel(50.0, 0.0, -20.0), 1e-12, "a negative ward term counts as 0");
            Assert.AreEqual(50.0, DefensiveWeaponMath.ThornsArmorLevel(50.0, 0.0, double.NaN), 1e-12, "NaN counts as 0");
        }

        [TestMethod]
        public void Wiring_WardThornsIsReadAfterTheShieldCheck_AndHasNoFrontalCone()
        {
            var combat = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityCombat.cs");
            var thorns = PooledLootSourceText.MethodBody(combat, "public void ApplyThornsReflect(");
            var shieldCheck = thorns.IndexOf("if (shield == null)", System.StringComparison.Ordinal);
            var ward = thorns.IndexOf("GetWardThornsLevel(attacker)", System.StringComparison.Ordinal);
            Assert.IsTrue(shieldCheck >= 0 && ward > shieldCheck, "a ward never enables shieldless Thorns");

            var buffs = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityPreWriteBuffs.cs");
            var body = PooledLootSourceText.MethodBody(buffs, "public double GetWardThornsLevel(");
            Assert.IsFalse(body.Contains("IsFrontal", System.StringComparison.Ordinal) || body.Contains("GetAngle", System.StringComparison.Ordinal),
                "GetWardThornsLevel must not apply the frontal cone");
            StringAssert.Contains(body, "WardShieldMath.ThornsLevelFor(");
        }
    }
}
