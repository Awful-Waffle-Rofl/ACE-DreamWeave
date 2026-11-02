using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Regression: a damage-over-time tick keeps landing after its caster has died, and each tick fired the
    /// creature's WoundedTaunt Tell at that dead player (Line-Captain Isaure Vellamy vs a Blood Mage).
    /// A Player cannot be constructed in this harness (see the notes on "new Player(" elsewhere in the suite),
    /// so the live/dead branch of EmoteManager.IsTauntSourcePresent is pinned by source text, in the style of
    /// WaveChallengeRecordAnnounceCallSiteTests, alongside the one behaviour that needs no Player.
    /// </summary>
    [TestClass]
    public class EmoteTauntSourceTests
    {
        private const string SourceRelativePath = "Source/ACE.Server/WorldObjects/Managers/EmoteManager.cs";

        [TestMethod]
        public void NonPlayerSource_IsNeverFiltered()
        {
            Assert.IsTrue(EmoteManager.IsTauntSourcePresent(null));
        }

        [DataTestMethod]
        [DataRow(true, false, false, false, true)]
        [DataRow(false, false, false, false, false)]
        [DataRow(true, true, false, false, false)]
        [DataRow(true, false, true, false, false)]
        [DataRow(true, false, false, true, false)]
        [DataRow(false, true, true, true, false)]
        public void TruthTable(bool alive, bool inDeath, bool loggingOut, bool destroyed, bool expected)
        {
            Assert.AreEqual(expected, EmoteManager.IsTauntSourcePresent(alive, inDeath, loggingOut, destroyed));
        }

        [TestMethod]
        public void PlayerOverload_FeedsThePureRule_FromTheFourPlayerFlags()
        {
            var body = ExtractBody(ReadSource(), "public static bool IsTauntSourcePresent(WorldObject source)");

            StringAssert.Contains(body, "IsTauntSourcePresent(player.IsAlive, player.IsInDeathProcess, player.IsLoggingOut, player.IsDestroyed)");
        }

        [TestMethod]
        public void OnDamage_DropsADeadSourceAsTauntTarget()
        {
            var body = ExtractBody(ReadSource(), "public void OnDamage(Creature attacker)");

            StringAssert.Contains(body, "if (!IsTauntSourcePresent(attacker))");
            StringAssert.Contains(body, "attacker = null;");
        }

        [TestMethod]
        public void WoundedTauntTell_ReChecksTheSourceWhenItFires()
        {
            var source = ReadSource();
            var start = source.IndexOf("case EmoteType.Tell:", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Tell case not found");

            var window = source.Substring(start, 500);

            StringAssert.Contains(window, "EmoteCategory.WoundedTaunt");
            StringAssert.Contains(window, "IsTauntSourcePresent(player)");
        }

        private static string ReadSource()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, SourceRelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
            }

            Assert.Fail($"Could not find {SourceRelativePath} by walking up from {AppContext.BaseDirectory}.");
            return null;
        }

        private static string ExtractBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(at >= 0, $"Could not find '{signature}'");

            var open = source.IndexOf('{', at);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            Assert.Fail($"No closing brace for '{signature}'");
            return null;
        }
    }
}
