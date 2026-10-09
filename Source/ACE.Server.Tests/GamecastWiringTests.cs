using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-level proof that /gamecast and POST /v1/admin/announce share exactly one send path
    /// (PLAN-P3.md section 2, invariant 11): AdminCommands.HandleGamecast delegates to
    /// Gamecast.Broadcast, which is the only place that builds the WorldBroadcast message and logs it.
    /// Comments stripped with AccountVaultPurgeTests.StripComments, repo root found the same way
    /// AdminRouteRegistrationTests finds it.
    /// </summary>
    [TestClass]
    public class GamecastWiringTests
    {
        private static readonly Regex HandleGamecastPattern = new Regex(
            @"public\s+static\s+void\s+HandleGamecast\s*\([^)]*\)\s*\{[^}]*Gamecast\.Broadcast\(\s*session\?\.Player\s*,\s*string\.Join\(\s*""\s""\s*,\s*parameters\)\s*\)\s*;",
            RegexOptions.Singleline);

        [TestMethod]
        public void HandleGamecast_CallsGamecastBroadcast_WithSessionPlayer()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Command", "Handlers", "AdminCommands.cs");
            Assert.IsTrue(File.Exists(path), $"could not find {path}");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            Assert.IsTrue(HandleGamecastPattern.IsMatch(code),
                "HandleGamecast must call Gamecast.Broadcast(session?.Player, string.Join(\" \", parameters)) - the in-game text and the sender resolution must stay byte-identical to before P3");
        }

        [TestMethod]
        public void GamecastBroadcast_UsesFormat_WorldBroadcast_LogBroadcastChat()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Managers", "Gamecast.cs");
            Assert.IsTrue(File.Exists(path), $"could not find {path}");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            StringAssert.Contains(code, "Format(", "Gamecast.Broadcast must build its message through Gamecast.Format, the one place the prefix is written");
            StringAssert.Contains(code, "ChatMessageType.WorldBroadcast", "the broadcast must use the same chat message type /gamecast always has");
            StringAssert.Contains(code, "LogBroadcastChat", "the broadcast must be recorded in the broadcast chat log, exactly as /gamecast did before P3");
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Database")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"could not find the repo root by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
