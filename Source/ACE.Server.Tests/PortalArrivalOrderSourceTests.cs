using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Source-level guard for the phantom realm-0 landblock load (see PortalArrivalInstanceTests): in the three
    /// portal-family teleports, AdjustDungeon must never run on a weenie Destination before its instance is
    /// resolved. A weenie Destination is always instance 0, and AdjustDungeon activates the landblock in the
    /// instance it is handed, so adjusting first loads the realm-0 copy of every realm-routed destination.
    ///
    /// Scoped to the three method bodies, comments stripped (AccountVaultPurgeTests.StripComments). Two rules,
    /// both bound to the identifier actually passed to AdjustDungeon:
    ///   1. no AdjustDungeon(x) call appears before the first instance-resolution call in the method
    ///      (ResolvePortalDestination / ResolveArrivalDestination / AsInstancedPosition);
    ///   2. for each AdjustDungeon(x), the nearest preceding assignment to x is not a raw
    ///      `new Position(Destination)` / `new Position(something.Destination)`.
    /// Rule 2 is what catches the portal recall: its hop guard already resolves the destination near the top of
    /// the method, so rule 1 alone would pass the old recall order.
    ///
    /// The *_OldOrder_IsCaught tests prove the checker discriminates: each splices the pre-fix order into an
    /// in-memory copy of the real method text (never the file) and requires a violation.
    /// </summary>
    [TestClass]
    public class PortalArrivalOrderSourceTests
    {
        private static readonly Regex AdjustCall = new Regex(@"\bAdjustDungeon\s*\(\s*([A-Za-z_]\w*)\s*\)");
        private static readonly Regex Resolution = new Regex(@"\b(ResolvePortalDestination|ResolveArrivalDestination|AsInstancedPosition)\s*\(");
        private static readonly Regex RawDestination = new Regex(@"^new\s+Position\s*\(\s*(\w+\s*\.\s*)?Destination\s*\)$");

        private const string GemFile = "Gem.cs";
        private const string GemMethod = "private void UsePortalGem(Player player)";

        private const string MagicFile = "WorldObject_Magic.cs";
        private const string RecallMethod = "private void HandleCastSpell_PortalRecall(";

        private const string PortalFile = "Portal.cs";
        private const string ActOnUseMethod = "public override void ActOnUse(WorldObject activator)";

        // ================= the guard, on the real source =================

        [TestMethod]
        public void Gem_UsePortalGem_AdjustsOnlyAResolvedDestination() => AssertClean(MethodBody(GemFile, GemMethod), "Gem.UsePortalGem");

        [TestMethod]
        public void PortalRecall_AdjustsOnlyAResolvedDestination() => AssertClean(MethodBody(MagicFile, RecallMethod), "WorldObject.HandleCastSpell_PortalRecall");

        [TestMethod]
        public void Portal_ActOnUse_AdjustsOnlyAResolvedDestination() => AssertClean(MethodBody(PortalFile, ActOnUseMethod), "Portal.ActOnUse");

        // ================= discrimination: the pre-fix order, spliced into a copy =================

        [TestMethod]
        public void Gem_OldOrder_IsCaught()
        {
            var old = Splice(MethodBody(GemFile, GemMethod),
                @"var\s+portalDest\s*=\s*Portal\.ResolvePortalDestination\(this,\s*player,\s*new\s+Position\(Destination\)\);",
                "var portalDest = new Position(Destination);\nAdjustDungeon(portalDest);\nportalDest = Portal.ResolvePortalDestination(this, player, portalDest);");

            AssertCaught(old, "Gem.UsePortalGem with the pre-fix order");
        }

        [TestMethod]
        public void PortalRecall_OldOrder_IsCaught()
        {
            var old = Splice(MethodBody(MagicFile, RecallMethod),
                @"var\s+teleportDest\s*=\s*Portal\.ResolveArrivalDestination\([^;]*\);",
                "var teleportDest = new Position(portal.Destination);\nAdjustDungeon(teleportDest);\nteleportDest = Portal.ResolvePortalDestination(portal, targetPlayer, teleportDest);");

            AssertCaught(old, "HandleCastSpell_PortalRecall with the pre-fix order");
        }

        [TestMethod]
        public void Portal_ActOnUse_OldOrder_IsCaught()
        {
            var old = Splice(MethodBody(PortalFile, ActOnUseMethod),
                @"portalDest\s*=\s*new\s+Position\(Destination\);",
                "portalDest = new Position(Destination);\nAdjustDungeon(portalDest);");

            AssertCaught(old, "Portal.ActOnUse with the pre-fix order");
        }

        // ================= ActOnUse's deferred adjust is gated by MayAdjustInInstance =================

        private static readonly Regex GuardedDeferredAdjust = new Regex(@"if\s*\(\s*adjustDestination\s*&&\s*MayAdjustInInstance\(\s*portalDest\s*,[^;]*\)\s*AdjustDungeon\(\s*portalDest\s*\)\s*;");

        /// <summary>
        /// PortalArrivalInstanceTests pins the predicate; this pins that ActOnUse actually calls it, on portalDest,
        /// as the gate of the deferred AdjustDungeon(portalDest). Without it a HousePortal used from an ephemeral
        /// instance toward another landblock would create an unregistered landblock under the ephemeral id.
        /// </summary>
        [TestMethod]
        public void Portal_ActOnUse_DeferredAdjust_IsGatedByMayAdjustInInstance()
        {
            Assert.AreEqual(1, GuardedDeferredAdjust.Matches(MethodBody(PortalFile, ActOnUseMethod)).Count,
                "ActOnUse must run its deferred AdjustDungeon(portalDest) only under `adjustDestination && MayAdjustInInstance(portalDest, ...)`");
        }

        [TestMethod]
        public void Portal_ActOnUse_UngatedDeferredAdjust_IsCaught()
        {
            var ungated = Splice(MethodBody(PortalFile, ActOnUseMethod),
                @"if\s*\(\s*adjustDestination\s*&&\s*MayAdjustInInstance\([^;]*\)\s*AdjustDungeon\(\s*portalDest\s*\)\s*;",
                "if (adjustDestination)\nAdjustDungeon(portalDest);");

            Assert.AreEqual(0, GuardedDeferredAdjust.Matches(ungated).Count, "the gate check must reject the pre-review (ungated) deferred adjust");
        }

        // ================= ActOnUse's instanced-branch adjust excludes the speed season =================

        private static readonly Regex GuardedInstancedAdjust = new Regex(@"if\s*\(\s*adjustDestination\s*\)\s*AdjustDungeon\(\s*instancedDest\s*\)\s*;");
        private static readonly Regex AnyInstancedAdjust = new Regex(@"AdjustDungeon\(\s*instancedDest\s*\)\s*;");

        /// <summary>
        /// The speed-challenge portal also goes through the instanced branch, and it deliberately never adjusts its
        /// season row (see the speed-season branch's comment in ActOnUse). So the instanced-branch
        /// AdjustDungeon(instancedDest) must be gated by adjustDestination, which that branch leaves false - and it
        /// must be the ONLY AdjustDungeon(instancedDest) in the method, so an ungated copy cannot sit beside it.
        /// </summary>
        [TestMethod]
        public void Portal_ActOnUse_InstancedAdjust_IsGatedByAdjustDestination()
        {
            var body = MethodBody(PortalFile, ActOnUseMethod);

            Assert.AreEqual(1, AnyInstancedAdjust.Matches(body).Count, "expected exactly one AdjustDungeon(instancedDest) in ActOnUse");
            Assert.AreEqual(1, GuardedInstancedAdjust.Matches(body).Count,
                "ActOnUse's AdjustDungeon(instancedDest) must sit directly under `if (adjustDestination)`, or the speed-challenge portal adjusts its season row");
        }

        [TestMethod]
        public void Portal_ActOnUse_UngatedInstancedAdjust_IsCaught()
        {
            var ungated = Splice(MethodBody(PortalFile, ActOnUseMethod),
                @"if\s*\(\s*adjustDestination\s*\)\s*AdjustDungeon\(\s*instancedDest\s*\)\s*;",
                "AdjustDungeon(instancedDest);");

            Assert.AreEqual(1, AnyInstancedAdjust.Matches(ungated).Count, "precondition: the splice kept the adjust");
            Assert.AreEqual(0, GuardedInstancedAdjust.Matches(ungated).Count, "the gate check must reject the unconditional instanced adjust");
        }

        // ================= checker =================

        internal static List<string> Violations(string body)
        {
            var violations = new List<string>();

            var firstResolution = Resolution.Match(body);
            if (!firstResolution.Success)
                violations.Add("no instance-resolution call (ResolvePortalDestination / ResolveArrivalDestination / AsInstancedPosition) in scope - has the routing moved? This guard must follow it, not pass vacuously.");

            var calls = AdjustCall.Matches(body);

            if (calls.Count == 0 && !body.Contains("ResolveArrivalDestination("))
                violations.Add("neither AdjustDungeon( nor ResolveArrivalDestination( appears in scope - the scope is wrong or the adjust moved; refusing to pass vacuously.");

            foreach (Match call in calls)
            {
                var name = call.Groups[1].Value;

                if (firstResolution.Success && call.Index < firstResolution.Index)
                    violations.Add($"AdjustDungeon({name}) at offset {call.Index} runs before the first instance resolution at offset {firstResolution.Index}");

                // nearest preceding assignment to the bound identifier (var x = ... / x = ...; never ==)
                Match lastAssign = null;
                foreach (Match m in new Regex(@"\b" + Regex.Escape(name) + @"\s*=(?!=)\s*([^;]*);").Matches(body))
                {
                    if (m.Index < call.Index)
                        lastAssign = m;
                }

                if (lastAssign != null && RawDestination.IsMatch(lastAssign.Groups[1].Value.Trim()))
                    violations.Add($"AdjustDungeon({name}) adjusts a raw weenie Destination ({lastAssign.Groups[1].Value.Trim()}), which is instance 0");
            }

            return violations;
        }

        private static void AssertClean(string body, string what)
        {
            var v = Violations(body);
            Assert.AreEqual(0, v.Count, $"{what}:\n  " + string.Join("\n  ", v));
        }

        private static void AssertCaught(string body, string what)
        {
            Assert.IsTrue(Violations(body).Count > 0, $"{what} must be reported - otherwise the guard proves nothing");
        }

        /// <summary>Exactly one occurrence must be replaced, so a drifted needle fails loudly instead of testing the unmodified text.</summary>
        private static string Splice(string body, string pattern, string replacement)
        {
            var count = Regex.Matches(body, pattern).Count;
            Assert.AreEqual(1, count, $"discrimination needle /{pattern}/ should match exactly once in the method body; it matched {count}");

            return Regex.Replace(body, pattern, replacement.Replace("$", "$$"));
        }

        private static string MethodBody(string file, string signature)
        {
            var path = Path.Combine(RepoRoot(), "Source", "ACE.Server", "WorldObjects", file);
            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path).Replace("\r\n", "\n"));

            var start = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"'{signature}' not found in {file} - has it moved? This guard must follow it.");

            var open = code.IndexOf('{', start);
            Assert.IsTrue(open >= 0, $"no body after '{signature}'");

            var depth = 0;
            for (var i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }

            Assert.Fail($"unbalanced braces after '{signature}' in {file}");
            return null;
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Source", "ACE.Server")))
                dir = dir.Parent;

            Assert.IsNotNull(dir, $"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");

            return dir.FullName;
        }
    }
}
