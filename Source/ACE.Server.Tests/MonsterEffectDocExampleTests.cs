using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.MonsterEffects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Every worked example in the monster-effects docs, run through the real parser and the real handler
    /// Validate.
    ///
    /// A DOC EXAMPLE THAT DOES NOT PARSE IS WORSE THAN NO EXAMPLE - it is the string a content author copies
    /// first, and it fails at import with a warning about a weenie they did not write. DESIGN.md shipped one:
    /// "ward pcthp=0.25 secs=30 on=spawn,heartbeat" was rejected, because ward hand-parsed its on= against a
    /// single value while every other kind read a comma list. Nothing pointed that out, because the example
    /// lived in prose and the parser only ever saw hand-written test strings.
    ///
    /// EXTRACTION IS DELIBERATELY NARROW: single-quoted strings whose first whitespace-separated token is a
    /// known effect kind. That is the shape the docs use (a SQL INSERT of PropertyString 9015), and it will
    /// not mistake a prose mention of "on=hit" for a record. If a doc starts writing examples some other way,
    /// this test goes quiet rather than wrong - so the count assertion below is a floor, not decoration: it
    /// fails if the extractor stops finding the examples that exist today.
    /// </summary>
    [TestClass]
    public class MonsterEffectDocExampleTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        // Path.Combine, NOT a literal with backslashes: CI runs ubuntu-latest, where a backslash is an
        // ordinary filename character rather than a separator, so a hardcoded @"Docs\..." never resolves and
        // the walk below silently finds nothing. It passes on Windows either way, which is exactly why this
        // has to be built portably rather than tested locally and assumed.
        private static readonly string[] Docs =
        {
            Path.Combine("Docs", "MonsterEffects", "DESIGN.md"),
            Path.Combine("Docs", "MonsterEffects", "CATALOG.md"),
        };

        /// <summary>
        /// A single-quoted run with no embedded quote, which is what the docs' SQL INSERT examples are.
        /// </summary>
        private static readonly Regex QuotedString = new Regex("'([^']+)'", RegexOptions.Compiled);

        private static string FindInSourceTree(string relativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        [TestMethod]
        public void EveryAuthoredExampleInTheDocs_ParsesAndValidates()
        {
            var checkedRecords = 0;
            var checkedStrings = 0;

            foreach (var doc in Docs)
            {
                var path = FindInSourceTree(doc);

                Assert.IsNotNull(path, $"could not find {doc} by walking up from {AppContext.BaseDirectory}");

                var lines = File.ReadAllLines(path);

                for (var i = 0; i < lines.Length; i++)
                {
                    foreach (Match match in QuotedString.Matches(lines[i]))
                    {
                        var candidate = match.Groups[1].Value.Trim();

                        var firstToken = candidate.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                        if (firstToken.Length == 0 || !MonsterEffectRegistry.IsKnownKind(firstToken[0].ToLowerInvariant()))
                            continue;

                        checkedStrings++;

                        var where = $"{doc}:{i + 1} '{candidate}'";

                        MonsterEffectParser.Parse(candidate, out var specs, out var errors);

                        Assert.AreEqual(0, errors.Count, $"{where} - parser rejected: {string.Join(" | ", errors)}");
                        Assert.IsTrue(specs.Count > 0, $"{where} - parsed to no records at all");

                        var buildErrors = new List<string>();

                        var set = MonsterEffectSet.Build(specs, buildErrors);

                        Assert.AreEqual(0, buildErrors.Count, $"{where} - handler Validate rejected: {string.Join(" | ", buildErrors)}");
                        Assert.IsNotNull(set, $"{where} - resolved to nothing");
                        Assert.AreEqual(specs.Count, set.Count, $"{where} - a record was dropped between parse and build");

                        checkedRecords += specs.Count;
                    }
                }
            }

            // the floor: DESIGN.md's two worked examples, three records each. If the extractor ever stops
            // finding them, that is a silent hole, so it fails here rather than passing with nothing checked.
            Assert.IsTrue(checkedStrings >= 2, $"expected at least the two DESIGN.md worked examples, found {checkedStrings}");
            Assert.IsTrue(checkedRecords >= 6, $"expected at least six authored records across them, found {checkedRecords}");
        }
    }
}
