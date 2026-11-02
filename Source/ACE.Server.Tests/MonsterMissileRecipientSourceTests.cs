using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source pin for the monster-missile recipient in ProjectileCollisionHelper.OnCollideObject.
    ///
    /// The damage of a monster's arrow is resolved against the creature it STRUCK
    /// (DamageEvent.CalculateDamage(sourceCreature, targetCreature, ...)), so it must also be APPLIED to that
    /// creature. The branch used to pick the recipient from the firer's CURRENT AttackTarget, which a Taunt or a
    /// FindNextTarget can change while the arrow is in flight: the struck creature's mitigated damage (a caster's
    /// or a pet's, far less mitigated than a tank's) then landed on a different player, and the creature actually
    /// struck took nothing. Suspected (not proven) cause of a player report of a one-off 6468 hit from a mob that
    /// normally hits for about 235.
    ///
    /// A source-text pin rather than a behavioural test because the branch needs a live Player recipient, which
    /// the unit-test host cannot construct. Same idiom as ReflectBounceTests.Code.
    /// </summary>
    [TestClass]
    public class MonsterMissileRecipientSourceTests
    {
        private const string HelperPath = "Source/ACE.Server/WorldObjects/ProjectileCollisionHelper.cs";

        [TestMethod]
        public void MonsterMissileBranch_PicksTheStruckCreatureAsThePlayerRecipient()
        {
            var body = MethodBody(Code(HelperPath), "public static void OnCollideObject(WorldObject worldObject, WorldObject target)");

            // The branch condition is the bare non-null firer test: requiring sourceCreature.AttackTarget != null made
            // an arrow whose firer dropped aggro mid-flight land for nothing.
            var branch = body.IndexOf("else if (sourceCreature != null) {", StringComparison.Ordinal);
            Assert.IsTrue(branch >= 0, "monster-missile branch not found, or its condition is no longer the bare 'sourceCreature != null'");

            var recipient = body.IndexOf("var targetPlayer = targetCreature as Player;", branch, StringComparison.Ordinal);
            var calculate = body.IndexOf("DamageEvent.CalculateDamage(sourceCreature, targetCreature, worldObject", branch, StringComparison.Ordinal);

            Assert.IsTrue(recipient >= 0, "the monster-missile recipient must be the struck creature (targetCreature as Player)");
            Assert.IsTrue(calculate >= 0, "the monster-missile damage must still be resolved against the struck creature");
            Assert.IsTrue(recipient < calculate, "the recipient must be chosen before the damage it receives is calculated");
        }

        [TestMethod]
        public void OnCollideObject_NeverPicksARecipientFromTheFirersAttackTarget()
        {
            var body = MethodBody(Code(HelperPath), "public static void OnCollideObject(WorldObject worldObject, WorldObject target)");

            // as / is, with or without a pattern variable: every spelling that derives a recipient from AttackTarget
            Assert.IsFalse(Regex.IsMatch(body, @"AttackTarget\s+(as|is)\s+(Player|Creature)\b"),
                "OnCollideObject must not choose who takes the hit from the firer's AttackTarget - it can change while the arrow is in flight");

            Assert.IsFalse(Regex.IsMatch(body, @"AttackTarget\s*!=\s*null"),
                "OnCollideObject must not gate a monster missile on the firer still having an AttackTarget when it lands");
        }

        // Comment lines are dropped so the explanatory comment above the fix (which names the old expression) does
        // not satisfy or trip the scans; whitespace is collapsed so formatting changes do not.
        private static string Code(string repoRelativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, repoRelativePath)))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");

            var lines = File.ReadAllLines(Path.Combine(dir.FullName, repoRelativePath));
            var code = string.Join(" ", Array.FindAll(lines, l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            return Regex.Replace(code, @"\s+", " ");
        }

        private static string MethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"signature not found - has it changed? {signature}");

            var open = source.IndexOf('{', start + signature.Length);
            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(start, i - start + 1);
            }

            Assert.Fail($"unbalanced braces after {signature}");
            return null;
        }
    }
}
