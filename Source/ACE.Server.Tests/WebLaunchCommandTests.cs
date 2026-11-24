using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Command.Handlers;
using ACE.Server.Managers.Market;

namespace ACE.Server.Tests
{
    [TestClass]
    public class WebLaunchCommandTests
    {
        [TestMethod]
        public void Reply_Issued_IsTheExactPluginWireText()
        {
            var s = new WebLaunchCodeStore();

            var r = WebLaunchCommands.Execute(s, true, true, 7, 1);

            var m = Regex.Match(r, @"^\[WEB\] Launch code: ([A-Za-z0-9_-]{43}) \(single use, expires in 60 seconds\)$");
            Assert.IsTrue(m.Success, r);
            Assert.IsTrue(s.TryRedeem(m.Groups[1].Value, out var a, out var g));
            Assert.AreEqual(7u, a);
            Assert.AreEqual(1u, g);
        }

        [TestMethod]
        public void Execute_Disabled_ReturnsDisabledText_IssuesNothing()
        {
            var s = new WebLaunchCodeStore();

            var r = WebLaunchCommands.Execute(s, false, true, 7, 1);

            Assert.AreEqual(WebLaunchCommandText.DisabledText, r);
            Assert.AreEqual(0, s.Count);
        }

        [TestMethod]
        public void Execute_HostNotRunning_ReturnsUnavailable()
        {
            var s = new WebLaunchCodeStore();

            Assert.AreEqual(WebLaunchCommandText.UnavailableText, WebLaunchCommands.Execute(s, true, false, 7, 1));
            Assert.AreEqual(0, s.Count);
        }

        [TestMethod]
        public void Execute_NullStore_ReturnsUnavailable()
        {
            Assert.AreEqual(WebLaunchCommandText.UnavailableText, WebLaunchCommands.Execute(null, true, true, 7, 1));
        }

        [TestMethod]
        public void Execute_AccountZero_ReturnsUnavailable()
        {
            var s = new WebLaunchCodeStore();

            Assert.AreEqual(WebLaunchCommandText.UnavailableText, WebLaunchCommands.Execute(s, true, true, 0, 1));
            Assert.AreEqual(0, s.Count);
        }

        [TestMethod]
        public void Execute_EleventhInAMinute_ReturnsTooQuickly()
        {
            var s = new WebLaunchCodeStore();

            for (var i = 0; i < WebLaunchCodeStore.MaxIssuesPerMinutePerAccount; i++)
                StringAssert.StartsWith(WebLaunchCommands.Execute(s, true, true, 7, 1), "[WEB] Launch code: ");

            Assert.AreEqual(WebLaunchCommandText.TooQuicklyText, WebLaunchCommands.Execute(s, true, true, 7, 1));
        }

        [TestMethod]
        public void WireText_ErrorLinesAreTheExactContract()
        {
            Assert.AreEqual("[WEB] Signing in to the website from the game is turned off on this server.", WebLaunchCommandText.DisabledText);
            Assert.AreEqual("[WEB] The website sign-in service is not available right now.", WebLaunchCommandText.UnavailableText);
            Assert.AreEqual("[WEB] You are requesting website sign-in codes too quickly. Wait a minute and try again.", WebLaunchCommandText.TooQuicklyText);
        }

        [TestMethod]
        public void WireText_AllRepliesStartWithThePrefix()
        {
            StringAssert.StartsWith(WebLaunchCommandText.DisabledText, "[WEB] ");
            StringAssert.StartsWith(WebLaunchCommandText.UnavailableText, "[WEB] ");
            StringAssert.StartsWith(WebLaunchCommandText.TooQuicklyText, "[WEB] ");
            StringAssert.StartsWith(WebLaunchCommandText.Issued("x"), "[WEB] ");
            Assert.AreEqual("[WEB] ", WebLaunchCommandText.Prefix);
        }
    }
}
