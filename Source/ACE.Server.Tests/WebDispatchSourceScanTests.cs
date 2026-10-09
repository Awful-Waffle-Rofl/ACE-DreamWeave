using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// PLAN-P4.md invariants 5 and 6, enforced at source level over the whole ACE.Server tree (comments
    /// stripped): GetCommandHandler(null, ...) is called from exactly one place, the console thread,
    /// and a command handler is invoked with a null session only from CommandManager.cs and (from P4b)
    /// Command/Web/WebCommandDispatcher.cs.
    /// </summary>
    [TestClass]
    public class WebDispatchSourceScanTests
    {
        internal static readonly Regex NullSessionGetCommandHandler = new Regex(@"GetCommandHandler\(\s*null\s*,", RegexOptions.None);
        internal static readonly Regex NullCommandHandlerInvoke = new Regex(@"\(\s*CommandHandler\s*\)[^;]*?\)\s*\.Invoke\(\s*null\s*,", RegexOptions.None);

        private static readonly string[] NullInvokeAllowed = { "Command/CommandManager.cs", "Command/Web/WebCommandDispatcher.cs" };

        private static List<(string RelativePath, int Count)> Scan(Regex pattern)
        {
            var serverDir = Path.Combine(RepoRoot(), "Source", "ACE.Server");
            Assert.IsTrue(Directory.Exists(serverDir), $"could not find {serverDir}");

            var hits = new List<(string, int)>();

            foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(serverDir, file).Replace('\\', '/');
                if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                    continue;

                var count = pattern.Matches(AccountVaultPurgeTests.StripComments(File.ReadAllText(file))).Count;
                if (count > 0)
                    hits.Add((relative, count));
            }

            return hits;
        }

        [TestMethod]
        public void GetCommandHandlerWithNullSession_OnlyOnConsoleThread()
        {
            var hits = Scan(NullSessionGetCommandHandler);

            Assert.AreEqual(1, hits.Sum(h => h.Count),
                $"GetCommandHandler(null, ...) must appear exactly once (the console thread); found: {string.Join(", ", hits.Select(h => $"{h.RelativePath} x{h.Count}"))}");
            Assert.AreEqual("Command/CommandManager.cs", hits.Single().RelativePath);
        }

        [TestMethod]
        public void NullInvoke_OnlyInWebCommandDispatcher()
        {
            var hits = Scan(NullCommandHandlerInvoke);

            foreach (var (path, count) in hits)
                Assert.IsTrue(NullInvokeAllowed.Contains(path), $"{path} invokes a CommandHandler with a null session {count} time(s); only {string.Join(" and ", NullInvokeAllowed)} may");

            Assert.AreEqual(1, hits.Where(h => h.RelativePath == "Command/CommandManager.cs").Sum(h => h.Count),
                "the console thread's null-session invoke in CommandManager.cs was not found - has the pattern stopped matching?");

            // P4b: the web bucket's null-session invoke exists, exactly once, and only in the dispatcher.
            Assert.AreEqual(1, hits.Where(h => h.RelativePath == "Command/Web/WebCommandDispatcher.cs").Sum(h => h.Count),
                "WebCommandDispatcher.cs must hold exactly one null-session invoke (the web bucket's)");
        }

        [TestMethod]
        public void NullSessionGetCommandHandlerRegex_NegativeControl()
        {
            Assert.AreEqual(1, NullSessionGetCommandHandler.Matches("if (CommandManager.GetCommandHandler(null, command, parameters, out var info) == CommandHandlerResponse.Ok)").Count);
            Assert.AreEqual(1, NullSessionGetCommandHandler.Matches("GetCommandHandler( null , c, p, out _)").Count);
            Assert.AreEqual(0, NullSessionGetCommandHandler.Matches("GetCommandHandler(session, command, parameters, out var info)").Count);
        }

        [TestMethod]
        public void NullInvokeRegex_NegativeControl()
        {
            Assert.AreEqual(1, NullCommandHandlerInvoke.Matches("((CommandHandler)commandHandler.Handler).Invoke(null, parameters);").Count);
            Assert.AreEqual(1, NullCommandHandlerInvoke.Matches("((CommandHandler)info.Handler).Invoke( null, stuffed);").Count);
            Assert.AreEqual(0, NullCommandHandlerInvoke.Matches("((CommandHandler)commandHandler.Handler).Invoke(session, parameters);").Count);
            Assert.AreEqual(0, NullCommandHandlerInvoke.Matches("method.Invoke(null, new object[] { 1 });").Count);
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
