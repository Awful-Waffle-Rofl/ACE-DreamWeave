using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guards the two halves of the Enhanced-vital live refresh fix. The client rebuilds max vitals
    /// itself from terms the server pushes, so a live vital message that carries the RAW StartingValue
    /// drops the Enhanced bonus the login description packet includes, and the display silently
    /// disagrees with the server until the next relog.
    ///
    /// These are source-shape assertions rather than behavioural ones because ACE.Server.Tests has no
    /// database and no client dat, so a live CreatureVital cannot be constructed here.
    /// </summary>
    [TestClass]
    public class EnhancedVitalRefreshTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        private static string ReadServerFile(params string[] relative)
            => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(relative)));

        [TestMethod]
        public void VitalUpdateMessage_SendsNetworkStartingValue()
        {
            var text = ReadServerFile("Source", "ACE.Server", "Network", "GameMessages", "Messages", "GameMessagePrivateUpdateVital.cs");

            StringAssert.Contains(text, "creatureVital.NetworkStartingValue",
                "the live vital message must carry NetworkStartingValue, matching what GameEventPlayerDescription writes at login");

            Assert.IsFalse(text.Contains("Writer.Write(creatureVital.StartingValue)"),
                "writing the raw StartingValue drops the Enhanced-vital bonus and desyncs the client's max vital");
        }

        [TestMethod]
        public void SendEnhancedStatUpdate_VitalBranchIsNotEmpty()
        {
            var text = ReadServerFile("Source", "ACE.Server", "WorldObjects", "Player_ClassAbilities.cs");

            var marker = "case EnhancedStatKind.Vital:";
            var at = text.IndexOf(marker, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, "EnhancedStatKind.Vital case not found in SendEnhancedStatUpdate");

            var branch = text.Substring(at, Math.Min(600, text.Length - at));

            StringAssert.Contains(branch, "GameMessagePrivateUpdateVital",
                "the Vital branch must actually send a vital update, not just comment about relogging");
        }

        [TestMethod]
        public void SendEnhancedStatUpdate_BundleBranchIteratesBundledSkills()
        {
            var text = ReadServerFile("Source", "ACE.Server", "WorldObjects", "Player_ClassAbilities.cs");

            var marker = "is IStatBundleAbility bundle";
            var at = text.IndexOf(marker, StringComparison.Ordinal);

            Assert.IsTrue(at >= 0, "IStatBundleAbility branch not found in SendEnhancedStatUpdate - learning/unlearning a " +
                "Training-style bundle ability (e.g. Archer Training) must push a live skill update, not just wait for a relog");

            var branch = text.Substring(at, Math.Min(400, text.Length - at));

            StringAssert.Contains(branch, "BundledSkills",
                "the bundle branch must iterate BundledSkills, not a single skill");

            StringAssert.Contains(branch, "GameMessagePrivateUpdateSkill",
                "the bundle branch must actually send a skill update per bundled skill, not just comment about relogging");
        }
    }
}
