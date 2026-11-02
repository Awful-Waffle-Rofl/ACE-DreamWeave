using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pure truth-table coverage for TinkerLock.IsRefused(bool, bool, bool) - the injectable core
    /// used by the production wrapper (which needs a real WorldObject/PropertyManager/database and
    /// so is not exercised here; see BluespireTinkerLockContentTests for the content-side pins and
    /// TinkerLockSourceOrderTests for the three call sites).
    /// </summary>
    [TestClass]
    public class TinkerLockTests
    {
        [TestMethod]
        public void Unlocked_NeverRefused()
        {
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: false, hasCookbookRow: false));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: false, hasCookbookRow: true));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: true, hasCookbookRow: false));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: false, sourceIsManaStone: true, hasCookbookRow: true));
        }

        [TestMethod]
        public void Locked_ManaStoneSource_NeverRefused()
        {
            // Recharging is not tinkering (owner ruling), regardless of any cook_book row.
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: true, hasCookbookRow: false));
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: true, hasCookbookRow: true));
        }

        [TestMethod]
        public void Locked_NonManaStoneSource_RefusedWithoutACookbookRow()
        {
            Assert.IsTrue(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: false, hasCookbookRow: false));
        }

        [TestMethod]
        public void Locked_NonManaStoneSource_AllowedWithACookbookRow()
        {
            Assert.IsFalse(TinkerLock.IsRefused(targetLocked: true, sourceIsManaStone: false, hasCookbookRow: true));
        }

        // ---- Drift Atlan rule (6-argument core) ----

        private static bool Drift(bool manaStone = false, bool cookbook = false, bool modMaterial = false, bool modsEnabled = false) =>
            TinkerLock.IsRefused(targetLocked: false, targetIsDriftAtlan: true, sourceIsManaStone: manaStone,
                hasCookbookRow: cookbook, sourceIsWeaponModMaterial: modMaterial, weaponModsEnabled: modsEnabled);

        [TestMethod]
        public void Drift_ManaStone_Allowed()
        {
            Assert.IsFalse(Drift(manaStone: true));
        }

        [TestMethod]
        public void Drift_CookbookPair_Allowed()
        {
            Assert.IsFalse(Drift(cookbook: true));
        }

        [TestMethod]
        public void Drift_ModMaterial_AllowedOnlyWhileWeaponModsEnabled()
        {
            Assert.IsFalse(Drift(modMaterial: true, modsEnabled: true));
            Assert.IsTrue(Drift(modMaterial: true, modsEnabled: false));
        }

        [TestMethod]
        public void Drift_OtherSource_Refused()
        {
            Assert.IsTrue(Drift());
            // The enabled flag alone, with a source that is not mod material, grants nothing.
            Assert.IsTrue(Drift(modsEnabled: true));
        }

        [TestMethod]
        public void NonDriftTarget_UnaffectedByModMaterialAndEnabledFlag()
        {
            foreach (var manaStone in new[] { false, true })
            foreach (var cookbook in new[] { false, true })
            foreach (var modMaterial in new[] { false, true })
            foreach (var enabled in new[] { false, true })
            {
                Assert.IsFalse(TinkerLock.IsRefused(false, false, manaStone, cookbook, modMaterial, enabled),
                    $"unlocked non-drift refused: mana={manaStone} cookbook={cookbook} mod={modMaterial} enabled={enabled}");
            }
        }

        [TestMethod]
        public void SiraluunLock_UnchangedThroughSixArgCore()
        {
            // A locked non-drift target ignores the mod-material allowance entirely.
            Assert.IsTrue(TinkerLock.IsRefused(true, false, false, false, true, true));
            Assert.IsFalse(TinkerLock.IsRefused(true, false, true, false, false, false));
            Assert.IsFalse(TinkerLock.IsRefused(true, false, false, true, false, false));
        }

        [TestMethod]
        public void LockedAndDrift_ModMaterialDoesNotBypassTheSiraluunLock()
        {
            Assert.IsTrue(TinkerLock.IsRefused(true, true, false, false, true, true));
            Assert.IsFalse(TinkerLock.IsRefused(true, true, true, false, false, false));
            Assert.IsFalse(TinkerLock.IsRefused(true, true, false, true, false, false));
        }

        [TestMethod]
        public void DriftRefusalText_IsTheSpecifiedMessageAndDoesNotNameSiraluun()
        {
            Assert.AreEqual(
                "The Drift's power in this weapon rejects that material. Only Tourmaline, Amethyst, mana stones and Atlan Drift infusions can be used on it.",
                TinkerLock.DriftRefusalText);
            Assert.IsFalse(TinkerLock.DriftRefusalText.Contains("Siraluun"));
        }

        [TestMethod]
        public void IsDriftConversion_InfusionOnANonDriftTarget_IsAConversion()
        {
            foreach (uint wcid in new uint[] { 1007400, 1007401, 1007402 })
                Assert.IsTrue(TinkerLock.IsDriftConversion(wcid, targetIsDriftAtlan: false));
        }

        [TestMethod]
        public void IsDriftConversion_InfusionOnADriftTarget_IsAReimbueAndNeverPrompts()
        {
            foreach (uint wcid in new uint[] { 1007400, 1007401, 1007402 })
                Assert.IsFalse(TinkerLock.IsDriftConversion(wcid, targetIsDriftAtlan: true));
        }

        [TestMethod]
        public void IsDriftConversion_NonInfusionSource_NeverPrompts()
        {
            foreach (uint wcid in new uint[] { 1007399, 1007403, 1004110, 0 })
            {
                Assert.IsFalse(TinkerLock.IsDriftConversion(wcid, targetIsDriftAtlan: false));
                Assert.IsFalse(TinkerLock.IsDriftConversion(wcid, targetIsDriftAtlan: true));
            }
        }

        [TestMethod]
        public void DriftConversionPrompt_IsTheSpecifiedTextWithTheTargetName()
        {
            Assert.AreEqual(
                "Infusing the Shivering Atlan Sword will transform it into a Drift weapon. The original weapon, along with any tinkering or imbue it carries, will be lost. Proceed?",
                TinkerLock.DriftConversionPrompt("Shivering Atlan Sword"));
        }

        [TestMethod]
        public void RefusalMessage_DriftTargetWithADriftInfusion_GetsTheInfusionMismatchText()
        {
            foreach (uint wcid in new uint[] { 1007400, 1007401, 1007402 })
                Assert.AreEqual("That infusion cannot be used on this weapon.", TinkerLock.RefusalMessage(wcid, true, "Drift Sword"));
        }

        [TestMethod]
        public void RefusalMessage_DriftTargetWithAnyOtherSource_GetsTheDriftText()
        {
            Assert.AreEqual(TinkerLock.DriftRefusalText, TinkerLock.RefusalMessage(1007399u, true, "Drift Sword"));
            Assert.AreEqual(TinkerLock.DriftRefusalText, TinkerLock.RefusalMessage(1007403u, true, "Drift Sword"));
            Assert.AreEqual(TinkerLock.DriftRefusalText, TinkerLock.RefusalMessage(null, true, "Drift Sword"));
        }

        [TestMethod]
        public void RefusalMessage_NonDriftTarget_KeepsTheSiraluunText_EvenForAnInfusionWcid()
        {
            Assert.AreEqual("The Foo is bound by Siraluun craft and cannot be altered by that.", TinkerLock.RefusalMessage(5u, false, "Foo"));
            Assert.AreEqual("The Foo is bound by Siraluun craft and cannot be altered by that.", TinkerLock.RefusalMessage(1007400u, false, "Foo"));
            Assert.AreEqual("The Foo is bound by Siraluun craft and cannot be altered by that.", TinkerLock.RefusalMessage(null, false, "Foo"));
        }
    }
}
