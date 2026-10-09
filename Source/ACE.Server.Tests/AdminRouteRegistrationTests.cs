using System;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Every /v1/admin/* route MUST be registered through MapAdmin, never through the bare Guard/MapGet
    /// used by non-admin routes - MapAdmin is what wraps a route with AdminAuthorizer (PLAN-P1.md
    /// section 2, DESIGN.md section 7.1/8: "the null-session dispatch path is forbidden" is the analog
    /// for commands; this is the analog for HTTP routes). Source-level, comments stripped, over the
    /// whole ACE.Server tree - not just MarketApiHost.cs - so a stray admin route added anywhere else
    /// in the project is still caught.
    /// </summary>
    [TestClass]
    public class AdminRouteRegistrationTests
    {
        private static readonly Regex LiteralPattern = new Regex(@"""/v1/admin", RegexOptions.None);

        /// <summary>Widened (PLAN-P2.md ruling 10) to also count MapAdminPost - a non-generic helper sharing AdminGate with MapAdmin. A generic MapAdminPost&lt;T&gt;( would NOT match this and must never be used for an admin write route.</summary>
        private static readonly Regex MapAdminPattern = new Regex(@"MapAdmin(Post)?\(\s*a\s*,\s*""/v1/admin", RegexOptions.None);
        private static readonly Regex BareMapPattern = new Regex(@"\.Map(Get|Post|Put|Delete|Patch|Methods)\(\s*""/v1/admin", RegexOptions.None);

        [TestMethod]
        public void EveryAdminRoute_GoesThroughMapAdmin()
        {
            var serverDir = Path.Combine(RepoRoot(), "Source", "ACE.Server");
            Assert.IsTrue(Directory.Exists(serverDir), $"could not find {serverDir}");

            var literalCount = 0;
            var mapAdminCount = 0;
            var bareMapCount = 0;

            foreach (var file in Directory.EnumerateFiles(serverDir, "*.cs", SearchOption.AllDirectories))
            {
                var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(file));

                literalCount += LiteralPattern.Matches(code).Count;
                mapAdminCount += MapAdminPattern.Matches(code).Count;
                bareMapCount += BareMapPattern.Matches(code).Count;
            }

            Assert.IsTrue(literalCount > 0, "no \"/v1/admin literal was found at all - has the route family moved or been renamed?");
            Assert.AreEqual(literalCount, mapAdminCount,
                $"found {literalCount} \"/v1/admin literal(s) but only {mapAdminCount} MapAdmin(a, \"/v1/admin registration(s) - every admin route literal must be registered through MapAdmin");
            Assert.AreEqual(0, bareMapCount,
                "an admin route was registered through a bare Map*/MapMethods call instead of MapAdmin - it would skip AdminAuthorizer entirely");

            // PLAN-P4.md section 7: me, settings, settings/{key}, chat, announce, commands, and P4b's
            // commands/run; WORLD-EVENTS-START.md: world-events catalog, status, preview, start, stop.
            // A pinned count also catches a route that silently disappears.
            Assert.AreEqual(12, mapAdminCount, "the admin route count changed - update this pin together with the route and market-api-v1.yaml");
        }

        /// <summary>Negative control: proves the bare-map regex actually catches the shape it exists to catch, so a passing test above is not merely a regex that never matches anything.</summary>
        [TestMethod]
        public void BareMapPattern_CatchesASyntheticViolation()
        {
            const string synthetic = "a.MapGet(\"/v1/admin/x\", (HttpContext ctx) => Results.Ok());";

            Assert.AreEqual(1, BareMapPattern.Matches(synthetic).Count,
                "the bare-map regex must catch a MapGet(\"/v1/admin/... call that bypasses MapAdmin");
        }

        /// <summary>The widened pattern (PLAN-P2.md ruling 10) counts both MapAdmin and MapAdminPost registrations.</summary>
        [TestMethod]
        public void MapAdminPattern_CountsBothHelpers()
        {
            const string synthetic =
                "MapAdmin(a, \"/v1/admin/x\", (ctx, p) => Results.Ok());\n" +
                "MapAdminPost(a, \"/v1/admin/y\", (ctx, p, body) => Results.Ok());";

            Assert.AreEqual(2, MapAdminPattern.Matches(synthetic).Count);
        }

        /// <summary>Proves the widening did not turn the guard into a prefix match that any MapAdmin* wrapper satisfies.</summary>
        [TestMethod]
        public void MapAdminPattern_RejectsLookalikeHelper()
        {
            const string synthetic = "MapAdminWhatever(a, \"/v1/admin/z\", (ctx, p) => Results.Ok());";

            Assert.AreEqual(0, MapAdminPattern.Matches(synthetic).Count);
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
