using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.WeaponMods;

namespace ACE.Server.Tests
{
    /// <summary>
    /// TinkerLock's Drift Atlan allow-list for the two Paragon sources, and the property-based detection of those
    /// sources. Pure: no PropertyManager and no database.
    /// </summary>
    [TestClass]
    public class ParagonModTinkerLockTests
    {
        private static bool DriftRefuses(bool paragonSource, bool modMaterial = false, bool modsEnabled = true) =>
            TinkerLock.IsRefused(targetLocked: false, targetIsDriftAtlan: true, sourceIsManaStone: false, hasCookbookRow: false,
                sourceIsWeaponModMaterial: modMaterial, weaponModsEnabled: modsEnabled, sourceIsParagonModSource: paragonSource);

        [TestMethod]
        public void DriftAtlanWeapon_AllowsAParagonSource_WithModsOnOrOff()
        {
            Assert.IsFalse(DriftRefuses(paragonSource: true, modsEnabled: true));
            Assert.IsFalse(DriftRefuses(paragonSource: true, modsEnabled: false), "the manager claims the use and refuses with its own message");
        }

        [TestMethod]
        public void DriftAtlanWeapon_StillRefusesAnOrdinarySource()
        {
            Assert.IsTrue(DriftRefuses(paragonSource: false));
            Assert.IsTrue(DriftRefuses(paragonSource: false, modMaterial: true, modsEnabled: false));
            Assert.IsFalse(DriftRefuses(paragonSource: false, modMaterial: true, modsEnabled: true), "Tourmaline and Amethyst are unchanged");
        }

        [TestMethod]
        public void TheSixArgumentCallers_AreUnchanged()
        {
            Assert.IsTrue(TinkerLock.IsRefused(false, true, false, false, false, false));
        }

        [TestMethod]
        public void ParagonSourceFlag_DoesNotLiftTheSiraluunLock()
        {
            Assert.IsTrue(TinkerLock.IsRefused(targetLocked: true, targetIsDriftAtlan: false, sourceIsManaStone: false, hasCookbookRow: false,
                sourceIsWeaponModMaterial: false, weaponModsEnabled: true, sourceIsParagonModSource: true));
        }

        [TestMethod]
        public void Detection_IsByProperty_Gem()
        {
            var gem = WeaponModTestKit.MakeWeapon(ItemType.Gem, null, workmanship: null);

            Assert.IsFalse(ParagonModManager.IsParagonSource(gem));

            gem.SetProperty(PropertyInt.ParagonModGemModifier, (int)WeaponModId.Devastation);

            Assert.IsTrue(ParagonModManager.IsGem(gem));
            Assert.IsTrue(ParagonModManager.IsParagonSource(gem));
            Assert.AreEqual(ParagonModManager.ParagonModAction.ApplyGem, ParagonModManager.ResolveAction(gem));
        }

        [TestMethod]
        public void Detection_IsByProperty_ResetItem_AndFalseIsNotASource()
        {
            var reset = WeaponModTestKit.MakeWeapon(ItemType.Misc, null, workmanship: null);

            reset.SetProperty(PropertyBool.ParagonModReset, false);
            Assert.IsFalse(ParagonModManager.IsParagonSource(reset), "a false flag is not a reset item");

            reset.SetProperty(PropertyBool.ParagonModReset, true);

            Assert.IsTrue(ParagonModManager.IsResetItem(reset));
            Assert.IsTrue(ParagonModManager.IsParagonSource(reset));
            Assert.AreEqual(ParagonModManager.ParagonModAction.Reset, ParagonModManager.ResolveAction(reset));
        }

        [TestMethod]
        public void Detection_NullIsNotASource()
        {
            Assert.IsFalse(ParagonModManager.IsParagonSource(null));
        }
    }
}
