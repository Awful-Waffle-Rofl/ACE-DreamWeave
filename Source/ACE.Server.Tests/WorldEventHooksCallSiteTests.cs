using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Refactor slice R5: shared engine code reaches World Events only through WorldEventHooks. This scan walks
    /// every .cs file under Source/ACE.Server - except WorldEvents/ (the implementation), Command/ (admin
    /// commands drive the manager directly), Managers/Market/ (the admin panel service layer) and bin/obj - and
    /// fails on any World Events token outside the allowlist below. Comments are stripped first, so prose that
    /// names a type is fine; a call is not.
    ///
    /// Accepted limitation: the comment stripper is not string-aware, so a `//` inside a string literal (a URL in
    /// a log message, say) truncates the scanned line at that point and anything after it on the same line is
    /// not scanned. A token hidden that way is missed; nothing is ever falsely reported.
    ///
    /// The tokens are the World Events runtime and check surface: the manager, the hook classes, the
    /// per-creature run state, and the fork's WorldEvent* properties. Library types the system shares with
    /// Pinnacles, Threads, ML digsites and PvP (WorldEventGeometry, WorldEventSpawner's health maths,
    /// WorldEventRosterSelector, WorldEventBandMemo, WorldEventBossDamageController, ...) are not tokens: they are
    /// pure helpers other systems reuse, not World Events checks.
    /// </summary>
    [TestClass]
    public class WorldEventHooksCallSiteTests
    {
        private static readonly Regex Token = new Regex(
            @"\b(P_WorldEvent|WorldEventManager|WorldEventCombatCreditHook|WorldEventBossDamageHook|WorldEventObjectiveRules" +
            @"|WorldEventLootBank|WorldEventRewardCrate|WorldEventCacheHandler|WorldEventOrphanFilter" +
            @"|WorldEventSkyDrop\w*|WorldEventStyledIdle|WorldEventObjective|WorldEventDeathInProgress|WorldEventAdminService" +
            @"|WorldEventHold\w*|SetWorldEventHoldTag" +
            @"|Property(?:Int|Int64|Bool|Float|String|DataId|InstanceId)\s*\.\s*WorldEvent\w*)\b");

        private static readonly string[] ExcludedDirs = { "WorldEvents", "Command", Path.Combine("Managers", "Market"), "bin", "obj" };

        /// <summary>
        /// Genuinely necessary exceptions, keyed by path relative to Source/ACE.Server and the matched token. A
        /// count of -1 allows any number (a World Events partial of Creature, which IS implementation); any other
        /// count must match exactly, so a new raw use in that file still fails.
        /// </summary>
        private static readonly (string Path, string Token, int Count, string Why)[] Allowlist =
        {
            ("WorldObjects/Creature.cs", "P_WorldEvent", 1, "the field declaration itself"),
            ("WorldObjects/Creature.cs", "WorldEventSkyDrop", 1, "the field declaration itself"),
            ("WorldObjects/Creature.cs", "WorldEventSkyDropDeadline", 1, "the field declaration itself"),
            ("WorldObjects/Creature.cs", "WorldEventObjective", 2, "the cached-flag field declaration and its one assignment from WorldEventHooks.IsObjectiveCreature"),
            ("WorldObjects/Player_Death.cs", "WorldEventDeathInProgress", 1, "the latched-state property declaration itself; every read goes through WorldEventHooks.IsProtectedDeathInProgress"),
            ("WorldObjects/Creature_SkyDrop.cs", "*", -1, "the sky-drop Creature partial: the implementation WorldEventHooks forwards to"),
            ("WorldObjects/Creature_WorldEventNpc.cs", "*", -1, "the event-NPC Creature partial: the implementation WorldEventHooks forwards to"),
            ("WorldObjects/Creature_WorldEventHold.cs", "*", -1, "the World Events Hold Creature partial: the run tag and, from later slices, the implementation the Hold hooks forward to"),
            ("Program.cs", "WorldEventManager", 1, "log text \"Initializing WorldEventManager...\", not a call"),
            ("Managers/WorldManager.cs", "WorldEventManager", 1, "log text \"WorldEventManager.Tick() threw\", not a call"),
        };

        /// <summary>Walks up from the test assembly to the directory holding Source/ACE.Server. Fails, never Inconclusive.</summary>
        internal static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server", "WorldEvents", "WorldEventHooks.cs")))
                dir = dir.Parent;

            if (dir == null)
                Assert.Fail($"Could not find Source/ACE.Server/WorldEvents/WorldEventHooks.cs by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }

        /// <summary>
        /// Removes /* */ block comments (keeping their newlines, so line numbers survive) and // line comments,
        /// which also covers /// doc comments.
        /// </summary>
        internal static string StripComments(string source)
        {
            var noBlock = Regex.Replace(source, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
            return Regex.Replace(noBlock, @"//[^\n]*", "");
        }

        /// <summary>Every token hit in one file's comment-stripped text, as (token, 1-based line, trimmed line).</summary>
        internal static List<(string Token, int Line, string Text)> Hits(string source)
        {
            var hits = new List<(string, int, string)>();
            var lines = StripComments(source).Replace("\r\n", "\n").Split('\n');

            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in Token.Matches(lines[i]))
                    hits.Add((Regex.Replace(m.Value, @"\s+", ""), i + 1, lines[i].Trim()));

            return hits;
        }

        private static bool IsExcluded(string relative)
        {
            var parts = relative.Replace('\\', '/');
            return ExcludedDirs.Any(d => parts.StartsWith(d.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void EngineCode_ReachesWorldEventsOnlyThroughWorldEventHooks()
        {
            var serverRoot = Path.Combine(RepoRoot(), "Source", "ACE.Server");
            var files = Directory.GetFiles(serverRoot, "*.cs", SearchOption.AllDirectories);

            Assert.IsTrue(files.Length > 500, $"sanity: expected the whole server tree, found {files.Length} .cs files under {serverRoot}");

            var violations = new List<string>();
            var seen = new Dictionary<(string, string), int>();
            var scanned = 0;

            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(serverRoot, file).Replace('\\', '/');

                if (IsExcluded(relative))
                    continue;

                scanned++;

                foreach (var (token, line, text) in Hits(File.ReadAllText(file)))
                {
                    var wildcard = Allowlist.Any(a => a.Path == relative && a.Token == "*");
                    var counted = Allowlist.Any(a => a.Path == relative && a.Token == token);

                    if (wildcard)
                        continue;

                    if (counted)
                    {
                        seen.TryGetValue((relative, token), out var n);
                        seen[(relative, token)] = n + 1;
                        continue;
                    }

                    violations.Add($"{relative}:{line}: {token} :: {text}");
                }
            }

            foreach (var a in Allowlist.Where(a => a.Count >= 0))
            {
                seen.TryGetValue((a.Path, a.Token), out var n);

                if (n != a.Count)
                    violations.Add($"{a.Path}: allowlisted {a.Token} expected exactly {a.Count}, found {n} ({a.Why})");
            }

            Assert.IsTrue(scanned > 500, $"sanity: the exclusions left only {scanned} files to scan");

            Assert.AreEqual(0, violations.Count,
                "World Events must be reached from engine code only through WorldEventHooks (add a member there, or an " +
                "allowlist entry with a reason):\n" + string.Join("\n", violations));
        }

        [TestMethod]
        public void Scanner_Discriminates()
        {
            // Positive controls: each shape of raw use the scan exists to catch.
            Assert.AreEqual(1, Hits("if (P_WorldEvent != null) return;").Count);
            Assert.AreEqual(1, Hits("ACE.Server.WorldEvents.WorldEventManager.OnPlayerDied(this);").Count);
            Assert.AreEqual(1, Hits("if (creature.WorldEventSkyDropPendingAcquire) x();").Count);
            Assert.AreEqual(1, Hits("var s = GetProperty(PropertyInt.WorldEventId);").Count);
            Assert.AreEqual(1, Hits("var s = GetProperty(PropertyBool . WorldEventCache);").Count);
            Assert.AreEqual(1, Hits("WorldEventCombatCreditHook.OnCombatContact(a, b);").Count);
            Assert.AreEqual(1, Hits("if (creature.WorldEventObjective) return;").Count);
            Assert.AreEqual(1, Hits("|| WorldEventDeathInProgress ||").Count);
            Assert.AreEqual(1, Hits("x = WorldEventAdminService.CreateLive(a, b, 1);").Count);
            Assert.AreEqual(1, Hits("if (creature.WorldEventHold?.Kind == HoldUnitKind.Knight) x();").Count);
            Assert.AreEqual(1, Hits("var r = WorldEventHoldRules.FillLine(t, f, n, p, null, 1, 2);").Count);
            Assert.AreEqual(1, Hits("monster.SetWorldEventHoldTag(tag);").Count);

            // Negative controls: the hook itself, comments, and library types are not hits.
            Assert.AreEqual(0, Hits("ACE.Server.WorldEvents.WorldEventHooks.OnPlayerDied(this);").Count);
            Assert.AreEqual(0, Hits("// WorldEventManager.OnPlayerDied(this);").Count);
            Assert.AreEqual(0, Hits("/* P_WorldEvent */ var x = 1;").Count);
            Assert.AreEqual(0, Hits("/// <see cref=\"WorldEventManager\"/>").Count);
            Assert.AreEqual(0, Hits("var p = WorldEventGeometry.Disc(a, 1f, 2, rng);").Count);
            Assert.AreEqual(0, Hits("npc.EnableWorldEventStyledIdle(out _);").Count, "a name that merely contains a token is not one");
            Assert.AreEqual(0, Hits("if (WorldEventObjectiveRules2.X) {}").Count, "a longer identifier is not the field token");
            Assert.AreEqual(0, Hits("IWorldEventAdminService svc;").Count, "the interface is not the concrete service token");
            Assert.AreEqual(1, Hits("GetProperty(PropertyBool.WorldEventObjective)").Count, "the property form counts once, not twice");

            // Line numbers survive a multi-line block comment.
            var hit = Hits("/* one\ntwo */\nWorldEventManager.Tick();").Single();
            Assert.AreEqual(3, hit.Line);
            Assert.AreEqual("WorldEventManager", hit.Token);
        }
    }
}
