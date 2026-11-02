using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan guard for the recall / combat-mode invariant. A recall that plays its motion with
    /// MotionStance.NonCombat moves the stance to peace; if CombatMode is not dropped too, the player shows the combat
    /// bar but stands in peace stance (prod, Arutha 2026-10-09: /dn from Missile mode, STANCE_RESYNC MISMATCH).
    /// </summary>
    [TestClass]
    public class RecallCombatModeTests
    {
        private static readonly Regex MemberHeader = new Regex(
            @"^ {8}(?:public|private|protected|internal)[^;=\r\n]*\)[ \t]*\r?$", RegexOptions.Multiline);

        private static readonly Regex NonCombatMotion = new Regex(
            @"SendMotionAsCommands\s*\([^;]*MotionStance\.NonCombat", RegexOptions.Singleline);

        [TestMethod]
        public void EveryPlayerLocationMethodThatSendsANonCombatMotion_DropsCombatModeFirst()
        {
            var source = File.ReadAllText(FindInSourceTree(Path.Combine("Source", "ACE.Server", "WorldObjects", "Player_Location.cs")));

            var offenders = new List<string>();
            var scannedNames = new List<string>();

            foreach (var (name, body) in Members(source))
            {
                // the motion helper's own definition is not a call site
                if (name == "SendMotionAsCommands")
                    continue;

                var code = Regex.Replace(body, @"//[^\r\n]*", "");

                var motion = NonCombatMotion.Match(code);
                if (!motion.Success)
                    continue;

                scannedNames.Add(name);

                var drop = code.IndexOf("SetCombatMode(CombatMode.NonCombat)", StringComparison.Ordinal);
                if (drop < 0 || drop > motion.Index)
                    offenders.Add(name);
            }

            // positive control (a): the scan must have seen the known recalls by name
            foreach (var expected in new[] { "HandleActionTeleToNetworkHub", "HandleActionTeleToMarketPlace" })
                Assert.IsTrue(scannedNames.Contains(expected),
                    $"scan did not reach {expected}; expected both HandleActionTeleToNetworkHub and HandleActionTeleToMarketPlace. Scanned: {string.Join(", ", scannedNames)}");

            // positive control (b): every SendMotionAsCommands( occurrence is a scanned call site, plus the helper's own definition
            var totalCalls = Regex.Matches(Regex.Replace(source, @"//[^\r\n]*", ""), @"SendMotionAsCommands\s*\(").Count;
            Assert.AreEqual(scannedNames.Count + 1, totalCalls,
                $"SendMotionAsCommands( occurs {totalCalls} times but only {scannedNames.Count} methods were scanned (+1 for the definition); "
                + "a method with a multi-line signature, a non-NonCombat stance argument, or a new call shape is being skipped. "
                + $"Expected scanned set to include HandleActionTeleToNetworkHub and HandleActionTeleToMarketPlace. Scanned: {string.Join(", ", scannedNames)}");

            Assert.AreEqual(0, offenders.Count,
                "these Player_Location.cs methods send a NonCombat motion without first calling SetCombatMode(CombatMode.NonCombat): "
                + string.Join(", ", offenders));
        }

        private static IEnumerable<(string Name, string Body)> Members(string source)
        {
            foreach (Match header in MemberHeader.Matches(source))
            {
                var open = source.IndexOf('{', header.Index + header.Length);
                if (open < 0)
                    continue;

                var depth = 0;
                var end = -1;

                for (var i = open; i < source.Length; i++)
                {
                    if (source[i] == '{') depth++;
                    else if (source[i] == '}' && --depth == 0) { end = i; break; }
                }

                if (end < 0)
                    continue;

                var nameMatch = Regex.Match(header.Value, @"(\w+)\s*\(");
                yield return (nameMatch.Groups[1].Value, source.Substring(open, end - open + 1));
            }
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
