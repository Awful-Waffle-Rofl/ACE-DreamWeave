using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the house-login perf fix in WorldObjectFactory.CreateNewWorldObjects: the
    /// restrict_wcid skip must run BEFORE the weenie cache lookup, not after it.
    ///
    /// Why this is a source-order guard rather than a behavioural test: CreateNewWorldObjects calls the
    /// static DatabaseManager.World.GetCachedWeenie, which is not mockable/injectable here (DatabaseManager
    /// is a static entry point with no seam for a fake WorldDatabase in this test project), so a call to
    /// the method under test would need a live world database connection to exercise either branch. The
    /// ordering itself is exactly what the fix changed and exactly what a regression would silently
    /// re-invert, so it is what gets pinned. See LandblockInitObservabilityTests for the same pattern and
    /// its rationale.
    /// </summary>
    [TestClass]
    public class WorldObjectFactoryTests
    {
        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works
        /// regardless of the bin\Debug vs bin\x64\Debug output layout.
        /// </summary>
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

        /// <summary>
        /// Returns the source text of one method body, by brace matching from the method's opening brace.
        /// Scoping to a single method is the point: a file-wide occurrence check would be satisfied by
        /// something unrelated elsewhere in this large file.
        /// </summary>
        private static string ExtractMethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find '{signature}' in the source under test");

            var open = source.IndexOf('{', start);
            Assert.IsTrue(open >= 0, $"Could not find the opening brace of '{signature}'");

            var depth = 0;

            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return source.Substring(open, i - open + 1);
                }
            }

            Assert.Fail($"Unbalanced braces while extracting '{signature}'");
            return null;
        }

        /// <summary>
        /// The restrict_wcid skip must be checked before the cached-weenie lookup runs, so that a
        /// house load (which passes restrict_wcid) never pays for a weenie fetch on instances it is about
        /// to discard. Before the fix, GetCachedWeenie ran unconditionally for every non-link-child
        /// instance on the landblock, then the restrict_wcid check discarded most of them afterward.
        /// </summary>
        [TestMethod]
        public void CreateNewWorldObjects_RestrictWcidSkipPrecedesTheWeenieLookup()
        {
            var path = FindInSourceTree(Path.Combine("Source", "ACE.Server", "Factories", "WorldObjectFactory.cs"));
            var source = File.ReadAllText(path);

            var body = ExtractMethodBody(source,
                "public static List<WorldObject> CreateNewWorldObjects(List<LandblockInstance> sourceObjects, List<Biota> biotas, uint? restrict_wcid = null)");

            var restrictCheckAt = body.IndexOf("restrict_wcid != null", StringComparison.Ordinal);
            var weenieLookupAt = body.IndexOf("GetCachedWeenie(instance.WeenieClassId)", StringComparison.Ordinal);

            Assert.IsTrue(restrictCheckAt >= 0, "the restrict_wcid skip is missing from CreateNewWorldObjects - this test needs updating");
            Assert.IsTrue(weenieLookupAt >= 0, "the GetCachedWeenie lookup is missing from CreateNewWorldObjects - this test needs updating");

            Assert.IsTrue(restrictCheckAt < weenieLookupAt,
                "the restrict_wcid skip must run BEFORE GetCachedWeenie, so a restricted load (e.g. House.Load) " +
                "never fetches a weenie for an instance it is about to discard");
        }
    }
}
