using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text guards over the three TinkerLock call sites named in the design: comments are
    /// stripped before matching, following OneTimeItemGrantOrderTests, so a comment mentioning the
    /// right names in the right order cannot satisfy these.
    /// </summary>
    [TestClass]
    public class TinkerLockSourceOrderTests
    {
        private static string FindRepoFile(params string[] relativeParts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            var relative = Path.Combine(relativeParts);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {relative} by walking up from {AppContext.BaseDirectory}");

            return Path.Combine(dir.FullName, relative);
        }

        private static string StripComments(string source)
        {
            var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(noBlockComments, @"//[^\n]*", "");
        }

        /// <summary>
        /// Call site (a): Player_Use.HandleActionUseWithTarget. TinkerLock.IsRefused must run after
        /// the TargetType re-verify (the "Cannot use the {sourceItem.Name} with the {target.Name}"
        /// refusal) and before BOTH HandleActionUseOnTarget dispatches - the landblock move-to-chain
        /// branch and the same-location branch - so every use-on-target is covered regardless of
        /// which branch fires.
        /// </summary>
        [TestMethod]
        public void PlayerUse_TinkerLockRunsAfterTargetTypeCheckAndBeforeBothDispatches()
        {
            var src = StripComments(File.ReadAllText(FindRepoFile("Source", "ACE.Server", "WorldObjects", "Player_Use.cs")));

            var targetTypeCheck = src.IndexOf("Cannot use the {sourceItem.Name} with the {target.Name}", StringComparison.Ordinal);
            var tinkerLockCheck = src.IndexOf("TinkerLock.IsRefused(sourceItem, target)", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, targetTypeCheck, "Player_Use.HandleActionUseWithTarget must still re-verify TargetType");
            Assert.AreNotEqual(-1, tinkerLockCheck, "Player_Use.HandleActionUseWithTarget must call TinkerLock.IsRefused(sourceItem, target)");
            Assert.AreEqual(1, Regex.Matches(src, Regex.Escape("TinkerLock.IsRefused(sourceItem, target)")).Count);

            Assert.IsTrue(targetTypeCheck < tinkerLockCheck,
                "the TinkerLock check must run AFTER the TargetType re-verify");

            var dispatchSites = Regex.Matches(src, Regex.Escape("sourceItem.HandleActionUseOnTarget(this, target);"));
            Assert.AreEqual(2, dispatchSites.Count,
                "expected both HandleActionUseOnTarget dispatches (the move-to-chain branch and the same-location branch)");

            foreach (Match dispatch in dispatchSites)
            {
                Assert.IsTrue(tinkerLockCheck < dispatch.Index,
                    "the TinkerLock check must run BEFORE every HandleActionUseOnTarget dispatch");
            }
        }

        /// <summary>
        /// Call site (b): RefireStationCommon.HandleGive. TinkerLock.IsRefusedByStation must run
        /// before the eligibility `verify` call, so a locked item is refused before the station's own
        /// per-station eligibility check ever sees it.
        /// </summary>
        [TestMethod]
        public void RefireStationCommon_TinkerLockRunsBeforeVerify()
        {
            var src = StripComments(File.ReadAllText(FindRepoFile("Source", "ACE.Server", "RefireStations", "RefireStationCommon.cs")));

            var tinkerLockCheck = src.IndexOf("TinkerLock.IsRefusedByStation(item)", StringComparison.Ordinal);
            var verifyCall = src.IndexOf("verify(player, item, out var refusal);", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, tinkerLockCheck, "RefireStationCommon.HandleGive must call TinkerLock.IsRefusedByStation(item)");
            Assert.AreNotEqual(-1, verifyCall, "RefireStationCommon.HandleGive must still call verify(player, item, out var refusal)");
            Assert.AreEqual(1, Regex.Matches(src, Regex.Escape("TinkerLock.IsRefusedByStation(item)")).Count);

            // HandleGive's own verify call, not HandleConfirm's second re-verify further down the file.
            var firstVerifyCall = Regex.Matches(src, Regex.Escape("verify(player, item, out var refusal);"))[0].Index;

            Assert.IsTrue(tinkerLockCheck < firstVerifyCall,
                "the TinkerLock station check must run BEFORE HandleGive's own eligibility verify() call");
        }

        /// <summary>
        /// Call site (c): RecipeManager.UseObjectOnTarget, defense-in-depth for the Confirmation.cs
        /// re-entry path. TinkerLock.IsRefused must run after the source==target self-use check and
        /// before every other tinker intercept below it (salvage forge, tiger eye, equipment mods),
        /// so a locked target cannot reach any of those fork systems either.
        /// </summary>
        [TestMethod]
        public void RecipeManager_TinkerLockRunsAfterSelfUseCheckAndBeforeOtherIntercepts()
        {
            var src = StripComments(File.ReadAllText(FindRepoFile("Source", "ACE.Server", "Managers", "RecipeManager.cs")));

            var selfUseCheck = src.IndexOf("cannot be combined with itself", StringComparison.Ordinal);
            var tinkerLockCheck = src.IndexOf("TinkerLock.IsRefused(source, target)", StringComparison.Ordinal);
            var salvageForgeCheck = src.IndexOf("SalvageForge.IsHollowHammer(source)", StringComparison.Ordinal);
            var tigerEyeCheck = src.IndexOf("TigerEyeArmorTinker.IsTigerEyeSalvage(source)", StringComparison.Ordinal);
            var equipmentModsCheck = src.IndexOf("EquipmentModManager.IsModMaterial(source)", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, selfUseCheck, "RecipeManager.UseObjectOnTarget must still refuse source == target");
            Assert.AreNotEqual(-1, tinkerLockCheck, "RecipeManager.UseObjectOnTarget must call TinkerLock.IsRefused(source, target)");
            Assert.AreNotEqual(-1, salvageForgeCheck);
            Assert.AreNotEqual(-1, tigerEyeCheck);
            Assert.AreNotEqual(-1, equipmentModsCheck);
            Assert.AreEqual(1, Regex.Matches(src, Regex.Escape("TinkerLock.IsRefused(source, target)")).Count);

            Assert.IsTrue(selfUseCheck < tinkerLockCheck,
                "the TinkerLock defense-in-depth check must run AFTER the self-use check");

            Assert.IsTrue(tinkerLockCheck < salvageForgeCheck,
                "the TinkerLock check must run BEFORE the salvage forge intercept");
            Assert.IsTrue(tinkerLockCheck < tigerEyeCheck,
                "the TinkerLock check must run BEFORE the tiger eye armor tinker intercept");
            Assert.IsTrue(tinkerLockCheck < equipmentModsCheck,
                "the TinkerLock check must run BEFORE the equipment mods intercept");
        }

        /// <summary>
        /// Call site (d): PrismaticDriftStone.VerifyUseRequirements. Confirmation.cs's confirmed
        /// re-entry calls PrismaticDriftStone.UseObjectOnTarget directly (bypassing both
        /// Player_Use's and RecipeManager's gated call sites), and UseObjectOnTarget's own re-verify
        /// action runs VerifyUseRequirements again before Apply - so this is the only place left
        /// that can catch a locked target on that path. TinkerLock.IsRefused must run before the
        /// IsEligibleWeapon/IsCleanWeapon refusals, matching how those two refuse.
        /// </summary>
        [TestMethod]
        public void PrismaticDriftStone_TinkerLockRunsBeforeEligibilityChecks()
        {
            var src = StripComments(File.ReadAllText(FindRepoFile("Source", "ACE.Server", "Entity", "PrismaticDriftStone.cs")));

            var tinkerLockCheck = src.IndexOf("TinkerLock.IsRefused(source, target)", StringComparison.Ordinal);
            var eligibleWeaponCheck = src.IndexOf("IsEligibleWeapon(target)", StringComparison.Ordinal);
            var cleanWeaponCheck = src.IndexOf("IsCleanWeapon(target)", StringComparison.Ordinal);

            Assert.AreNotEqual(-1, tinkerLockCheck, "PrismaticDriftStone.VerifyUseRequirements must call TinkerLock.IsRefused(source, target)");
            Assert.AreNotEqual(-1, eligibleWeaponCheck, "PrismaticDriftStone.VerifyUseRequirements must still call IsEligibleWeapon(target)");
            Assert.AreNotEqual(-1, cleanWeaponCheck, "PrismaticDriftStone.VerifyUseRequirements must still call IsCleanWeapon(target)");
            Assert.AreEqual(1, Regex.Matches(src, Regex.Escape("TinkerLock.IsRefused(source, target)")).Count);

            Assert.IsTrue(tinkerLockCheck < eligibleWeaponCheck,
                "the TinkerLock check must run BEFORE the IsEligibleWeapon refusal");
            Assert.IsTrue(tinkerLockCheck < cleanWeaponCheck,
                "the TinkerLock check must run BEFORE the IsCleanWeapon refusal");
        }
    }
}
