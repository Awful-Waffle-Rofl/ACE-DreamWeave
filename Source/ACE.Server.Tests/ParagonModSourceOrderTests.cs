using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text pins for the Paragon wiring, with comments stripped first (following TinkerLockSourceOrderTests) so a
    /// comment naming the right call cannot satisfy them. Each pin matches an exact if-head or statement, not a substring.
    /// </summary>
    [TestClass]
    public class ParagonModSourceOrderTests
    {
        private static string Read(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            var relative = Path.Combine(parts);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {relative} by walking up from {AppContext.BaseDirectory}");

            var src = File.ReadAllText(Path.Combine(dir.FullName, relative));
            var noBlock = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);

            return Regex.Replace(noBlock, @"//[^\n]*", "");
        }

        private static int Only(string src, string exact)
        {
            var matches = Regex.Matches(src, Regex.Escape(exact));
            Assert.AreEqual(1, matches.Count, $"expected exactly one: {exact}");
            return matches[0].Index;
        }

        [TestMethod]
        public void RecipeManager_ParagonInterceptSitsAfterTinkerLock_AndBeforeTheCookbookLookup()
        {
            var src = Read("Source", "ACE.Server", "Managers", "RecipeManager.cs");

            var tinkerLock = Only(src, "if (TinkerLock.IsRefused(source, target))");
            var intercept = Only(src, "if (ParagonModManager.IsParagonSource(source))");
            var dispatch = Only(src, "ParagonModManager.UseObjectOnTarget(player, source, target);");
            var cookbook = Only(src, "var recipe = GetRecipe(player, source, target);");

            Assert.IsTrue(tinkerLock < intercept, "the Paragon claim must run AFTER the TinkerLock check");
            Assert.IsTrue(intercept < dispatch, "the dispatch must sit inside the claim");
            Assert.IsTrue(dispatch < cookbook, "the Paragon claim must run BEFORE the cookbook lookup");
        }

        [TestMethod]
        public void RecipeManager_ParagonDispatch_IsFollowedByAReturn()
        {
            var src = Read("Source", "ACE.Server", "Managers", "RecipeManager.cs");

            Assert.AreEqual(1, Regex.Matches(src, @"ParagonModManager\.UseObjectOnTarget\(player, source, target\);\s*return;").Count,
                "a Paragon use must return after dispatch, or it falls through to the cookbook");
        }

        [TestMethod]
        public void TinkerLock_ProductionCall_PassesTheParagonSourceArgument()
        {
            var src = Read("Source", "ACE.Server", "Entity", "TinkerLock.cs");

            var compute = Only(src, "var sourceIsParagonModSource = ParagonModManager.IsParagonSource(source);");
            var call = Only(src, "if (IsRefused(targetLocked, targetIsDriftAtlan, sourceIsManaStone, hasCookbookRow, sourceIsWeaponModMaterial, weaponModsEnabled, sourceIsParagonModSource))");

            Assert.IsTrue(compute < call, "the flag must be computed before the call that consumes it");
        }

        [TestMethod]
        public void OverflowYesPath_RunsMuleTemplateAndTinkerLockGates_BeforeReentering()
        {
            var src = Read("Source", "ACE.Server", "WeaponMods", "ParagonModManager.cs");
            var body = src.Substring(Only(src, "internal static void OnOverflowConfirmed("));

            var mule = Only(body, "if (player.MuleBlocked(MuleAction.Craft))");
            var template = Only(body, "var templateRefusal = player.PvpTemplateBlocked(PvpTemplateAction.UseWithTarget, source, target);");
            var lockRefusal = Only(body, "if (TinkerLock.IsRefused(source, target))");
            var reenter = Only(body, "UseObjectOnTarget(player, source, target, true);");

            Assert.IsTrue(mule < template && template < lockRefusal && lockRefusal < reenter,
                "mule, then PvP template, then TinkerLock, all before the confirmed re-entry");
        }
    }
}