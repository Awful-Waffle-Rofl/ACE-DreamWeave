using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The chat lines for a class ability rank change (ClassAbilityChangeMessages): the exact text, the web panel's
    /// marked variant, and pins that every in-game path builds its line through the shared helper rather than inline.
    /// Reads no PropertyManager key.
    /// </summary>
    [TestClass]
    public class ClassAbilityChangeMessageTests
    {
        private static string Source(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { ClassAbilityAffinityDeclarationTests.RepoRoot() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

        private static string ServerSource(params string[] parts) => Source(new[] { "Source", "ACE.Server" }.Concat(parts).ToArray());

        [TestMethod]
        public void Learned_IsTheTrainerAndVendorLine()
        {
            Assert.AreEqual("You learn Thorns rank 2. (5 class ability points left.)", ClassAbilityChangeMessages.Learned("Thorns", 2, 5));
            Assert.AreEqual("You learn Thorns rank 1. (1 class ability point left.)", ClassAbilityChangeMessages.Learned("Thorns", 1, 1));
            Assert.AreEqual("You learn Thorns rank 3. (0 class ability points left.)", ClassAbilityChangeMessages.Learned("Thorns", 3, 0));
        }

        [TestMethod]
        public void Unlearned_IsTheAbilitiesUnlearnLine()
        {
            Assert.AreEqual("You have unlearned Thorns and recovered 3 class ability points. Points: 8 available, 40 lifetime earned.",
                ClassAbilityChangeMessages.Unlearned("Thorns", 3, 8, 40));
            Assert.AreEqual("You have unlearned Thorns and recovered 1 class ability point. Points: 1 available, 1 lifetime earned.",
                ClassAbilityChangeMessages.Unlearned("Thorns", 1, 1, 1));
        }

        [TestMethod]
        public void Lowered_FollowsTheUnlearnLinesStyle()
        {
            Assert.AreEqual("You have lowered Thorns to rank 1 and recovered 1 class ability point. Points: 6 available, 40 lifetime earned.",
                ClassAbilityChangeMessages.Lowered("Thorns", 1, 1, 6, 40));
            Assert.AreEqual("You have lowered Thorns to rank 2 and recovered 4 class ability points. Points: 9 available, 40 lifetime earned.",
                ClassAbilityChangeMessages.Lowered("Thorns", 2, 4, 9, 40));
        }

        [TestMethod]
        public void PointsSummary_IsTheAbilitiesPointsLine()
        {
            Assert.AreEqual("Points: 5 available, 40 lifetime earned.", ClassAbilityChangeMessages.PointsSummary(5, 40));
        }

        [TestMethod]
        public void ChangeChatLine_IsTheInGameLinePlusTheMarker_ForEachKind()
        {
            var def = ClassAbilityPanelModel.AbilitySet[0];
            var name = def.DisplayName;

            Assert.AreEqual(" (web panel)", ClassAbilityChangeMessages.WebPanelMarker);

            Assert.AreEqual($"You learn {name} rank 2. (5 class ability points left.) (web panel)",
                ClassAbilityPanelModel.ChangeChatLine(ClassAbilityPanelChangeKind.Raise, def, rankAfter: 2, refunded: 0, pointsAvailable: 5, pointsEarned: 40));

            Assert.AreEqual($"You have lowered {name} to rank 1 and recovered 2 class ability points. Points: 7 available, 40 lifetime earned. (web panel)",
                ClassAbilityPanelModel.ChangeChatLine(ClassAbilityPanelChangeKind.Lower, def, rankAfter: 1, refunded: 2, pointsAvailable: 7, pointsEarned: 40));

            Assert.AreEqual($"You have unlearned {name} and recovered 3 class ability points. Points: 8 available, 40 lifetime earned. (web panel)",
                ClassAbilityPanelModel.ChangeChatLine(ClassAbilityPanelChangeKind.Unlearn, def, rankAfter: 0, refunded: 3, pointsAvailable: 8, pointsEarned: 40));

            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
                ClassAbilityPanelModel.ChangeChatLine(ClassAbilityPanelChangeKind.Invalid, def, 0, 0, 0, 0));
        }

        /// <summary>The trainer, the vendor and /abilities unlearn all build their line through the helper.</summary>
        [TestMethod]
        public void InGamePaths_UseTheSharedHelper()
        {
            StringAssert.Contains(ServerSource("ClassAbilities", "ClassAbilityTrainer.cs"),
                "Send(player, ClassAbilityChangeMessages.Learned(capturedDef.DisplayName, nextTier, player.AvailableClassAbilityPoints));");

            StringAssert.Contains(ServerSource("WorldObjects", "Player_ClassAbilityTokens.cs"),
                "ClassAbilityChangeMessages.Learned(def.DisplayName, tier, AvailableClassAbilityPoints),");

            var commands = ServerSource("Command", "Handlers", "ClassAbilityCommands.cs");
            StringAssert.Contains(commands,
                "Reply(session, ClassAbilityChangeMessages.Unlearned(skill.DisplayName, refunded, player.AvailableClassAbilityPoints, player.TotalClassAbilityPointsEarned));");
            StringAssert.Contains(commands,
                "return ClassAbilityChangeMessages.PointsSummary(player.AvailableClassAbilityPoints, player.TotalClassAbilityPointsEarned);");
        }

        /// <summary>
        /// No server file but the helper spells the wording inline, so a copy cannot drift from it. The dev-only
        /// "/abilities learn" reply ("You have learned ...") and the full respec are different sentences and are not matched.
        /// </summary>
        [TestMethod]
        public void TheWording_LivesOnlyInTheHelper()
        {
            var root = Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "Source", "ACE.Server");
            var wording = new Regex(@"\$""You learn \{|\$""You have unlearned \{|\$""You have lowered \{");
            var helper = Path.Combine(root, "ClassAbilities", "ClassAbilityChangeMessages.cs");

            Assert.AreEqual(3, wording.Matches(File.ReadAllText(helper)).Count, "control: the pattern finds all three lines in the helper itself");

            var hits = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => !string.Equals(f, helper, StringComparison.OrdinalIgnoreCase))
                .Where(f => wording.IsMatch(File.ReadAllText(f)))
                .Select(Path.GetFileName)
                .ToList();

            Assert.AreEqual(0, hits.Count, "inline copies of the change wording: " + string.Join(", ", hits));
        }
    }
}
