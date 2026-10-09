using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-level proof that AdminChatFeed.Shared.Capture is the FIRST statement of LogTurbineChat
    /// (PLAN-P3.md section 2, invariant 7) - ahead of the chat_log_* switch (trap 11) and the Discord
    /// relay, so a channel whose log flag is off (General, by default) is still captured, and a future
    /// change that reorders the method fails this test loudly instead of silently dropping the feed.
    /// Comments stripped with AccountVaultPurgeTests.StripComments, repo root found the same way
    /// AdminRouteRegistrationTests finds it.
    /// </summary>
    [TestClass]
    public class TurbineChatCaptureWiringTests
    {
        private static readonly Regex CaptureFirstStatementPattern = new Regex(
            @"void LogTurbineChat\([^)]*\)\s*\{\s*AdminChatFeed\.Shared\.Capture\(\s*chatType\s*,\s*channelID\s*,\s*name\s*,\s*message\s*\)\s*;",
            RegexOptions.Singleline);

        [TestMethod]
        public void LogTurbineChat_FirstStatementIsCapture()
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "Network", "Handlers", "TurbineChatHandler.cs");
            Assert.IsTrue(File.Exists(path), $"could not find {path}");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var matches = CaptureFirstStatementPattern.Matches(code);
            Assert.AreEqual(1, matches.Count,
                "AdminChatFeed.Shared.Capture(chatType, channelID, name, message) must be the FIRST statement of LogTurbineChat, exactly once");
        }

        /// <summary>Negative control: proves the pattern actually catches the shape it exists to catch, so a passing test above is not merely a regex that never matches anything.</summary>
        [TestMethod]
        public void WiringPattern_CatchesCaptureAfterRelay()
        {
            const string synthetic =
                "private static void LogTurbineChat(uint channelID, string name, string message, uint senderID, ChatType chatType)\n" +
                "{\n" +
                "    DiscordRelayManager.QueueMessage(chatType, name, message);\n" +
                "    AdminChatFeed.Shared.Capture(chatType, channelID, name, message);\n" +
                "}\n";

            Assert.AreEqual(0, CaptureFirstStatementPattern.Matches(synthetic).Count,
                "the pattern must NOT match when Capture sits after the relay call rather than as the first statement");
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
