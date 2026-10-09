using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-text pin on the wave-gauntlet record-announce CALL SITE (Player_WaveChallenge.cs's
    /// FinishWaveChallengeRun), in the style of AccountBankSchemaTests: this class never calls the production
    /// method (that needs a live Player/PlayerManager), so nothing else in the suite would notice if someone
    /// reverted the call site back to the old score-only rule (score > waveChallengeServerMaxAtRunStart) -
    /// WaveChallengeRecordAnnounceTests only exercises the pure helper, IsNewWaveRecord, in isolation. This test
    /// exists so THAT revert fails CI even though the helper itself still passes every one of its own tests.
    /// <para/>
    /// Comments are stripped before matching (source-text pins match comments too - a comment that quotes the
    /// old rule, or merely mentions the helper's name, would otherwise fool this pin in either direction).
    /// </summary>
    [TestClass]
    public class WaveChallengeRecordAnnounceCallSiteTests
    {
        private const string SourceRelativePath = "Source/ACE.Server/WorldObjects/Player_WaveChallenge.cs";

        // The exact condition the OLD, buggy rule used. If this substring reappears in the method body (with
        // comments stripped), the call site has regressed to score-only comparison.
        private const string OldRuleFragment = "score > waveChallengeServerMaxAtRunStart";

        [TestMethod]
        public void FinishWaveChallengeRun_AnnounceCondition_CallsHelper_NotTheOldScoreOnlyRule()
        {
            var body = ExtractMethodBody(ReadSource(), "private void FinishWaveChallengeRun(string message, bool teleportOut)");
            var stripped = StripLineComments(body);

            StringAssert.Contains(stripped, "IsNewWaveRecord(",
                "FinishWaveChallengeRun must gate the world-record announcement through Player.IsNewWaveRecord, " +
                "not a hand-rolled score comparison, or a #1 won purely on a faster clear time stops announcing again");

            Assert.IsFalse(stripped.Contains(OldRuleFragment),
                $"FinishWaveChallengeRun must not contain the old score-only rule fragment '{OldRuleFragment}' - " +
                "that is exactly the regression this pin guards against");
        }

        private static string ReadSource()
        {
            var path = FindInSourceTree(SourceRelativePath);

            Assert.IsNotNull(path, $"Could not find {SourceRelativePath} by walking up from {AppContext.BaseDirectory}.");

            return File.ReadAllText(path);
        }

        /// <summary>
        /// Strips "//" line comments. Not string-literal-aware, but Player_WaveChallenge.cs has no "//" inside a
        /// string literal in or near FinishWaveChallengeRun, so this is safe for this pin. Deliberately simple:
        /// a full C# tokenizer would be overkill for one method body.
        /// </summary>
        private static string StripLineComments(string source)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
            {
                var idx = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0)
                    lines[i] = lines[i].Substring(0, idx);
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Finds the exact method declaration text (not just the bare method name, which would also match a call
        /// site elsewhere in the file) and returns the brace-balanced body between its opening and matching
        /// closing brace. Naive brace counting is fine here: the method has no braces inside string or char
        /// literals.
        /// </summary>
        private static string ExtractMethodBody(string source, string methodSignature)
        {
            // The full declaration (modifiers + return type + name + params), not just the bare name, so this
            // matches only the method's own definition and never one of its call sites elsewhere in the file.
            var nameIndex = source.IndexOf(methodSignature, StringComparison.Ordinal);
            Assert.IsTrue(nameIndex >= 0, $"Could not find method declaration '{methodSignature}' in the source file");

            var openBrace = source.IndexOf('{', nameIndex);
            Assert.IsTrue(openBrace >= 0, $"Could not find the opening brace of '{methodSignature}'");

            var depth = 0;
            for (var i = openBrace; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return source.Substring(openBrace, i - openBrace + 1);
                }
            }

            Assert.Fail($"Never found a matching closing brace for '{methodSignature}'");
            return null;
        }

        /// <summary>
        /// Same walk-up-from-AppContext.BaseDirectory idiom as PropertyRegistryTests.FindInSourceTree, so this
        /// resolves the same way regardless of the bin\Debug vs bin\x64\Debug output layout.
        /// </summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
