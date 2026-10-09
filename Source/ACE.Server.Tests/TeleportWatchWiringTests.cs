using System;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-order pins for where the pending-teleport diagnostics hook into the teleport, completion and logout paths
    /// (Player_TeleportWatch). The hooks need a live Player to exercise, so the placement that makes them correct is
    /// asserted on the source instead, in the CombatStanceSyncTests style.
    /// </summary>
    [TestClass]
    public class TeleportWatchWiringTests
    {
        [TestMethod]
        public void Teleport_OpensTheWindow_AfterTheStamp_AndBeforeTheFirstSend()
        {
            var teleport = ExtractMember(Source("Player_Location.cs"), "public void Teleport(Position _newPosition");

            var stamp = teleport.IndexOf("LastTeleportStartTimestamp = Time.GetUnixTime();", StringComparison.Ordinal);
            Assert.IsTrue(stamp >= 0, "Teleport() must stamp LastTeleportStartTimestamp");

            var open = teleport.IndexOf("OpenTeleportWatch(", StringComparison.Ordinal);
            var firstSend = teleport.IndexOf("EnqueueSend(", stamp, StringComparison.Ordinal);

            Assert.IsTrue(open > stamp, "the window must open after the stamp it is keyed on");
            Assert.IsTrue(firstSend > open, "the window must open before the first send, so a throw in a send still leaves it open and counted");
        }

        [TestMethod]
        public void Teleport_TakesTheBaseline_AsItsLastStatement()
        {
            var teleport = ExtractMember(Source("Player_Location.cs"), "public void Teleport(Position _newPosition");

            var body = teleport.Substring(0, teleport.LastIndexOf('}')).TrimEnd();

            StringAssert.EndsWith(body, "BaselineTeleportWatch(watchStamp, true, originCell, originInstance);",
                "the baseline must be the last statement so every send Teleport() makes is inside it");
        }

        [TestMethod]
        public void OnTeleportComplete_RecordsCompletion_BeforeClearingTeleporting()
        {
            var onComplete = ExtractMember(Source("Player_Location.cs"), "public void OnTeleportComplete()");

            var hook = onComplete.IndexOf("TeleportWatchOnComplete();", StringComparison.Ordinal);
            var cleared = onComplete.IndexOf("Teleporting = false;", StringComparison.Ordinal);

            Assert.IsTrue(hook >= 0, "OnTeleportComplete must call the completion hook");
            Assert.IsTrue(cleared > hook, "the hook reads Teleporting, so it must run before it is cleared");
        }

        [TestMethod]
        public void LogOutInner_CallsTheAbandonHook_AsItsFirstStatement()
        {
            var logOutInner = ExtractMember(Source("Player.cs"), "public void LogOut_Inner(");

            var body = logOutInner.Substring(logOutInner.IndexOf('{') + 1);

            var firstStatement = body.Split('\n')
                .Select(l => l.Trim())
                .First(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));

            Assert.AreEqual("TeleportWatchOnLogOut();", firstStatement,
                "the abandon hook must run first, before the logout path changes any state it reads");
        }

        // ---- helpers ----

        private static string Source(string fileName)
        {
            return File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", fileName)));
        }

        private static string ExtractMember(string source, string signatureStart)
        {
            var start = source.IndexOf(signatureStart, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"could not find '{signatureStart}'");

            var open = source.IndexOf('{', start);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail($"unbalanced braces after '{signatureStart}'");
            return null;
        }

        private static string FindInSourceTree(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
