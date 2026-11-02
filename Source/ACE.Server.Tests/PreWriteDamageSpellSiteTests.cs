using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-scan pins for the spell flags on Player.ApplyPreWriteDamageClassAbilities' call sites, in the
    /// style of PvpDamageCapTests.AssertSite. Reflect's creature-spell immunity window
    /// (ReflectMagicAbility) only sees spell damage through PreWriteDamageContext.SpellDamageFromEnemyCreature,
    /// which the dispatch fills only when the CALL SITE passes `isSpell: true`. Forgetting that argument
    /// compiles cleanly (it is optional) and silently turns the window off for that whole damage route,
    /// so the arguments are pinned here as text:
    ///  - the four spell routes (SpellProjectile, Harm and Drain Health in WorldObject_Magic, the DoT tick
    ///    in EnchantmentManager) each pass `isSpell: true`;
    ///  - the DoT tick, which has no source, also passes `enemyCreatureSpellDamage:`;
    ///  - the physical route (Player_Combat) passes no isSpell at all;
    ///  - and there is no fifth call site anywhere in ACE.Server that this table does not classify.
    ///
    /// A STRUCTURAL check on code text with `//` comments stripped, not a behavioral one - the dispatch
    /// needs a live Player, which this test host cannot construct. The scan runs over text, so
    /// <see cref="TheScan_Discriminates_WhenAnIsSpellArgumentIsRemoved"/> feeds it a mutated copy to prove
    /// it actually fails on the defect it exists for.
    /// </summary>
    [TestClass]
    public class PreWriteDamageSpellSiteTests
    {
        private const string Method = "ApplyPreWriteDamageClassAbilities(";

        private sealed class SiteRule
        {
            public string File;
            public int Calls;
            public string[] Required;
            public string[] Forbidden;
        }

        private static readonly SiteRule[] Rules =
        {
            new SiteRule { File = "ACE.Server/WorldObjects/SpellProjectile.cs", Calls = 1, Required = new[] { "isSpell: true" }, Forbidden = new string[0] },
            new SiteRule { File = "ACE.Server/WorldObjects/WorldObject_Magic.cs", Calls = 2, Required = new[] { "isSpell: true" }, Forbidden = new string[0] },
            new SiteRule { File = "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs", Calls = 1, Required = new[] { "isSpell: true", "enemyCreatureSpellDamage:", "isDamageOverTime: true" }, Forbidden = new string[0] },
            new SiteRule { File = "ACE.Server/WorldObjects/Player_Combat.cs", Calls = 1, Required = new string[0], Forbidden = new[] { "isSpell" } },
        };

        [TestMethod]
        public void EverySpellRoute_PassesIsSpell_AndThePhysicalRouteDoesNot()
        {
            var root = FindSourceRoot();

            foreach (var rule in Rules)
            {
                var path = Path.Combine(root, rule.File.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(path), $"missing {path}");

                var problems = Check(rule, File.ReadAllText(path));

                Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
            }
        }

        [TestMethod]
        public void NoUnclassifiedCallSite_ExistsAnywhereInTheServer()
        {
            var root = FindSourceRoot();
            var serverDir = Path.Combine(root, "ACE.Server");

            var classified = new HashSet<string>(Rules.Select(r => Path.GetFullPath(Path.Combine(root, r.File.Replace('/', Path.DirectorySeparatorChar)))), StringComparer.OrdinalIgnoreCase);

            var unclassified = new List<string>();

            foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                    continue;

                if (ExtractCalls(File.ReadAllText(file)).Count > 0 && !classified.Contains(Path.GetFullPath(file)))
                    unclassified.Add(file);
            }

            Assert.AreEqual(0, unclassified.Count,
                "a new ApplyPreWriteDamageClassAbilities call site must be classified here as a spell or a physical route: " + string.Join(", ", unclassified));
        }

        /// <summary>
        /// The negative control: the same check, run over the real SpellProjectile.cs text with its
        /// `isSpell: true` argument deleted, must report the site. Without this the pin above could pass
        /// for a reason that has nothing to do with the argument (a wrong file, a scan that matches nothing).
        /// The Player_Combat rule is exercised the other way: adding an isSpell there must be reported too.
        /// </summary>
        [TestMethod]
        public void TheScan_Discriminates_WhenAnIsSpellArgumentIsRemoved()
        {
            var root = FindSourceRoot();

            var spellRule = Rules.Single(r => r.File.EndsWith("SpellProjectile.cs", StringComparison.Ordinal));
            var spellText = File.ReadAllText(Path.Combine(root, spellRule.File.Replace('/', Path.DirectorySeparatorChar)));

            Assert.AreEqual(0, Check(spellRule, spellText).Count, "control: the real file passes");

            var mutated = spellText.Replace(", isSpell: true)", ")");
            Assert.AreNotEqual(spellText, mutated, "control: the mutation must actually change the text");
            Assert.AreEqual(1, Check(spellRule, mutated).Count, "deleting isSpell from the spell projectile site must be reported");

            var dotRule = Rules.Single(r => r.File.EndsWith("EnchantmentManager.cs", StringComparison.Ordinal));
            var dotText = File.ReadAllText(Path.Combine(root, dotRule.File.Replace('/', Path.DirectorySeparatorChar)));

            var dotMutated = dotText.Replace(", enemyCreatureSpellDamage: enemyCreatureShare", "");
            Assert.AreNotEqual(dotText, dotMutated, "control: the DoT mutation must actually change the text");
            Assert.AreEqual(1, Check(dotRule, dotMutated).Count, "dropping the DoT tick's creature share must be reported");

            var physicalRule = Rules.Single(r => r.File.EndsWith("Player_Combat.cs", StringComparison.Ordinal));
            var physicalText = File.ReadAllText(Path.Combine(root, physicalRule.File.Replace('/', Path.DirectorySeparatorChar)));

            // the physical site also carries isHazard (owner ruling R3, HazardContext.HitPlayers); the mutation adds isSpell beside it
            var physicalMutated = physicalText.Replace("ApplyPreWriteDamageClassAbilities(source, damageType, amount, isHazard: isHazard)", "ApplyPreWriteDamageClassAbilities(source, damageType, amount, isSpell: true, isHazard: isHazard)");
            Assert.AreNotEqual(physicalText, physicalMutated, "control: the physical mutation must actually change the text");
            Assert.AreEqual(1, Check(physicalRule, physicalMutated).Count, "an isSpell on the physical route must be reported");
        }

        // ---- the DoT heartbeat pass (Reflect's "one heartbeat is one hit" ruling, 2026-10-17) ----------

        private const string EnchantmentManagerFile = "ACE.Server/WorldObjects/Managers/EnchantmentManager.cs";

        /// <summary>
        /// EnchantmentManager.HeartBeat_DamageOverTime must open Reflect's DoT pass before its first tick,
        /// keep all three ApplyDamageTick calls inside one try, and close the pass in that try's finally.
        /// Without the pass the heartbeat's own ticks block each other; without the finally a throwing tick
        /// leaves the player stuck in a pass that later DoT ticks would decide against a stale snapshot.
        /// </summary>
        [TestMethod]
        public void TheDotHeartbeat_WrapsEveryTickInTheReflectPass()
        {
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), EnchantmentManagerFile.Replace('/', Path.DirectorySeparatorChar)));

            var problems = CheckDotPass(text);

            Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
        }

        /// <summary>
        /// The negative control for the pass scan: removing Begin, and moving End out of the finally, must
        /// each be reported on a mutated copy of the real file.
        /// </summary>
        [TestMethod]
        public void TheDotPassScan_Discriminates()
        {
            // normalized to LF so the multi-line mutation below matches whatever the checkout's line endings are
            var text = File.ReadAllText(Path.Combine(FindSourceRoot(), EnchantmentManagerFile.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n");

            Assert.AreEqual(0, CheckDotPass(text).Count, "control: the real file passes");

            var noBegin = text.Replace("dotPassPlayer?.BeginReflectDotPass();", "");
            Assert.AreNotEqual(text, noBegin, "control: the Begin mutation must actually change the text");
            Assert.IsTrue(CheckDotPass(noBegin).Count > 0, "removing BeginReflectDotPass must be reported");

            // End moved out of the finally, to just after it
            var endOutside = text.Replace("dotPassPlayer?.EndReflectDotPass();", "")
                                 .Replace("// apply healing over time (HoTs)", "dotPassPlayer?.EndReflectDotPass();\n            // apply healing over time (HoTs)");
            Assert.AreNotEqual(text, endOutside, "control: the End mutation must actually change the text");
            Assert.IsTrue(CheckDotPass(endOutside).Count > 0, "EndReflectDotPass outside the finally must be reported");

            // a tick moved out of the try, after the finally
            var tickOutside = text.Replace("if (aetheriaDots.Count > 0)\n                    ApplyDamageTick(aetheriaDots, DamageType.Undef, true);", "")
                                  .Replace("// apply healing over time (HoTs)", "ApplyDamageTick(aetheriaDots, DamageType.Undef, true);\n            // apply healing over time (HoTs)");
            Assert.IsTrue(CountOf(StripComments(tickOutside), "ApplyDamageTick(aetheriaDots") == 1, "control: the tick mutation must move the call, not duplicate or drop it");
            Assert.AreNotEqual(text, tickOutside, "control: the tick mutation must actually change the text");
            Assert.IsTrue(CheckDotPass(tickOutside).Count > 0, "a tick outside the try must be reported");
        }

        /// <summary>
        /// The pass rules over the body of HeartBeat_DamageOverTime, `//` comments stripped. Pure over text.
        /// </summary>
        private static List<string> CheckDotPass(string text)
        {
            var problems = new List<string>();

            var code = StripComments(text);
            var body = MethodBody(code, "public void HeartBeat_DamageOverTime(");

            if (body == null)
            {
                problems.Add("HeartBeat_DamageOverTime not found");
                return problems;
            }

            var begin = body.IndexOf("BeginReflectDotPass()", StringComparison.Ordinal);
            var firstTick = body.IndexOf("ApplyDamageTick(", StringComparison.Ordinal);

            if (begin < 0)
                problems.Add("HeartBeat_DamageOverTime must call BeginReflectDotPass()");
            else if (firstTick >= 0 && begin > firstTick)
                problems.Add("BeginReflectDotPass() must come before the first ApplyDamageTick(");

            var tryAt = IndexOfKeywordBlock(body, "try", 0);
            if (tryAt < 0)
            {
                problems.Add("HeartBeat_DamageOverTime must wrap its ticks in a try");
                return problems;
            }

            var tryOpen = body.IndexOf('{', tryAt);
            var tryClose = MatchingBrace(body, tryOpen);
            var tryBlock = body.Substring(tryOpen, tryClose - tryOpen + 1);

            var ticksInTry = CountOf(tryBlock, "ApplyDamageTick(");
            var ticksTotal = CountOf(body, "ApplyDamageTick(");

            if (ticksInTry != 3 || ticksTotal != 3)
                problems.Add($"all three ApplyDamageTick( calls must sit inside the one try (in try: {ticksInTry}, in method: {ticksTotal})");

            var finallyAt = IndexOfKeywordBlock(body, "finally", tryClose);
            var between = finallyAt < 0 ? "" : body.Substring(tryClose + 1, finallyAt - tryClose - 1).Trim();

            if (finallyAt < 0 || between.Length != 0)
            {
                problems.Add("the try must be followed directly by a finally");
                return problems;
            }

            var finallyOpen = body.IndexOf('{', finallyAt);
            var finallyBlock = body.Substring(finallyOpen, MatchingBrace(body, finallyOpen) - finallyOpen + 1);

            if (!finallyBlock.Contains("EndReflectDotPass()", StringComparison.Ordinal))
                problems.Add("EndReflectDotPass() must be in the try's finally");

            if (CountOf(body, "EndReflectDotPass()") != 1)
                problems.Add("EndReflectDotPass() must appear exactly once, in the finally");

            return problems;
        }

        private static string StripComments(string text)
        {
            return string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line =>
            {
                var commentAt = line.IndexOf("//", StringComparison.Ordinal);
                return commentAt >= 0 ? line.Substring(0, commentAt) : line;
            }));
        }

        private static string MethodBody(string code, string signature)
        {
            var at = code.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0)
                return null;

            var open = code.IndexOf('{', at);
            return code.Substring(open, MatchingBrace(code, open) - open + 1);
        }

        private static int MatchingBrace(string code, int open)
        {
            var depth = 0;

            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return i;
            }

            return code.Length - 1;
        }

        /// <summary>Index of a `keyword` that stands as its own token and is followed by a `{` block.</summary>
        private static int IndexOfKeywordBlock(string code, string keyword, int from)
        {
            var at = from;

            while ((at = code.IndexOf(keyword, at, StringComparison.Ordinal)) >= 0)
            {
                var beforeOk = at == 0 || !char.IsLetterOrDigit(code[at - 1]);
                var rest = code.Substring(at + keyword.Length).TrimStart();

                if (beforeOk && rest.StartsWith("{", StringComparison.Ordinal))
                    return at;

                at += keyword.Length;
            }

            return -1;
        }

        private static int CountOf(string text, string token)
        {
            var count = 0;
            var at = 0;

            while ((at = text.IndexOf(token, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += token.Length;
            }

            return count;
        }

        private static List<string> Check(SiteRule rule, string text)
        {
            var problems = new List<string>();
            var calls = ExtractCalls(text);

            if (calls.Count != rule.Calls)
            {
                problems.Add($"{rule.File}: expected {rule.Calls} call(s) to {Method}..), found {calls.Count}");
                return problems;
            }

            foreach (var call in calls)
            {
                foreach (var required in rule.Required)
                {
                    if (!call.Contains(required, StringComparison.Ordinal))
                        problems.Add($"{rule.File}: `{call}` must carry `{required}`");
                }

                foreach (var forbidden in rule.Forbidden)
                {
                    if (call.Contains(forbidden, StringComparison.Ordinal))
                        problems.Add($"{rule.File}: `{call}` must not carry `{forbidden}`");
                }
            }

            return problems;
        }

        /// <summary>
        /// Every CALL of the method in <paramref name="text"/> (member access or a bare call from inside
        /// Player), `//` comments stripped and the argument list captured to its matching close paren, so a
        /// call wrapped over several lines is read whole. The method's own declaration is skipped.
        /// </summary>
        private static List<string> ExtractCalls(string text)
        {
            var code = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line =>
            {
                var commentAt = line.IndexOf("//", StringComparison.Ordinal);
                return commentAt >= 0 ? line.Substring(0, commentAt) : line;
            }));

            var calls = new List<string>();
            var from = 0;

            while (true)
            {
                var at = code.IndexOf(Method, from, StringComparison.Ordinal);
                if (at < 0)
                    break;

                // the declaration `public uint ApplyPreWriteDamageClassAbilities(` is not a call
                if (at >= 5 && code.Substring(at - 5, 5) == "uint ")
                {
                    from = at + Method.Length;
                    continue;
                }

                at--;   // keep the arithmetic below aligned on the character before the name
                var open = at + Method.Length;   // index of the '('
                var depth = 0;
                var end = open;

                for (; end < code.Length; end++)
                {
                    if (code[end] == '(') depth++;
                    else if (code[end] == ')' && --depth == 0) break;
                }

                var call = code.Substring(at + 1, Math.Min(end, code.Length - 1) - at);
                calls.Add(string.Join(" ", call.Split(new[] { ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)));

                from = end;
            }

            return calls;
        }

        private static string FindSourceRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ACE.Server", "Entity", "DamageEvent.cs")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find ACE.Server/Entity/DamageEvent.cs by walking up from {AppContext.BaseDirectory}");
            return dir.FullName;
        }
    }
}
