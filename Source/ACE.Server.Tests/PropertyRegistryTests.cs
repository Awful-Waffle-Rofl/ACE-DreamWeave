using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ACE.Entity.Enum.Properties;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The collision gate for fork-allocated property ids. Source/property-registry.tsv is the single
    /// authority for every custom property enum id this fork allocates; reserving an id there BEFORE the
    /// code lands turns a concurrent claim into a git conflict in one small file, instead of a duplicate
    /// that only shows up once both branches have merged (the PropertyInt 9032 case).
    ///
    /// WaveChallengePropertyTests guards specific shipped ids and catches duplicate enum values; this
    /// suite guards the registry <-> enum relationship. Pure file + reflection - no database, no world.
    ///
    /// Every failure message names the offending row (with its line number) and the exact edit to make.
    /// </summary>
    [TestClass]
    public class PropertyRegistryTests
    {
        private const string RegistryRelativePath = "Source/property-registry.tsv";

        private const char BomCharacter = '\uFEFF';

        private static readonly Dictionary<string, Type> EnumTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
        {
            { "PropertyBool", typeof(PropertyBool) },
            { "PropertyDataId", typeof(PropertyDataId) },
            { "PropertyFloat", typeof(PropertyFloat) },
            { "PropertyInstanceId", typeof(PropertyInstanceId) },
            { "PropertyInt", typeof(PropertyInt) },
            { "PropertyInt64", typeof(PropertyInt64) },
            { "PropertyString", typeof(PropertyString) },
        };

        [TestMethod]
        public void Registry_IsWellFormed()
        {
            var registry = LoadRegistry();
            var problems = new List<string>();

            Assert.IsTrue(registry.Bands.Count > 0, $"{RegistryRelativePath} declares no #band lines - the guard would pass vacuously.");

            foreach (var band in registry.Bands)
            {
                if (!EnumTypes.ContainsKey(band.EnumFile))
                    problems.Add($"line {band.LineNumber}: #band names unknown enum file '{band.EnumFile}'. Known: {string.Join(", ", EnumTypes.Keys)}.");

                if (band.Start > band.End)
                    problems.Add($"line {band.LineNumber}: #band {band.EnumFile} {band.Start}-{band.End} runs backwards.");
            }

            foreach (var row in registry.Rows)
            {
                if (!EnumTypes.ContainsKey(row.EnumFile))
                    problems.Add($"{row.Describe()}: unknown enum file '{row.EnumFile}'. Known: {string.Join(", ", EnumTypes.Keys)}.");

                if (row.Status != "active" && row.Status != "reserved")
                    problems.Add($"{row.Describe()}: status '{row.Status}' is not 'active' or 'reserved'.");

                if (row.IsRange && row.Status != "reserved")
                    problems.Add($"{row.Describe()}: a range row is a block reservation and must be status 'reserved', not '{row.Status}'. Split a single-id row out of the block instead.");

                if (row.Start > row.End)
                    problems.Add($"{row.Describe()}: range {row.Start}-{row.End} runs backwards.");

                if (string.IsNullOrWhiteSpace(row.Name))
                    problems.Add($"{row.Describe()}: the enum_name_or_block_label column is empty.");

                if (string.IsNullOrWhiteSpace(row.Owner))
                    problems.Add($"{row.Describe()}: the owner column is empty.");
            }

            // Overlaps (which subsume duplicate ids), per enum file.
            foreach (var group in registry.Rows.GroupBy(r => r.EnumFile, StringComparer.Ordinal))
            {
                var ordered = group.OrderBy(r => r.Start).ThenBy(r => r.End).ToList();

                for (var i = 1; i < ordered.Count; i++)
                {
                    var previous = ordered[i - 1];
                    var current = ordered[i];

                    if (current.Start <= previous.End)
                    {
                        var kind = previous.Start == current.Start && previous.End == current.End ? "duplicates" : "overlaps";
                        problems.Add($"{current.Describe()} {kind} {previous.Describe()} - {current.Range} vs {previous.Range}. One id belongs to exactly one row.");
                    }
                }
            }

            // Sort order: enum file (ordinal), then id / range start. Keeps the file diffable and keeps
            // two concurrent claims on the same id landing on the same line - i.e. as a git conflict.
            for (var i = 1; i < registry.Rows.Count; i++)
            {
                var previous = registry.Rows[i - 1];
                var current = registry.Rows[i];

                var fileOrder = string.CompareOrdinal(previous.EnumFile, current.EnumFile);

                if (fileOrder > 0 || (fileOrder == 0 && previous.Start > current.Start))
                    problems.Add($"{current.Describe()} is out of order - it must sort after {previous.Describe()} by enum_file then id. Rows sort by enum_file (ordinal), then id / range start.");
            }

            AssertNoProblems(problems);
        }

        /// <summary>
        /// (a) Every enum member inside a guarded band must be registered under its exact name. This is the
        /// half that stops an unregistered id from ever reaching master.
        /// </summary>
        [TestMethod]
        public void Registry_CoversEveryEnumMemberInsideAGuardedBand()
        {
            var registry = LoadRegistry();
            var problems = new List<string>();

            foreach (var pair in EnumTypes)
            {
                var enumFile = pair.Key;
                var bands = registry.Bands.Where(b => b.EnumFile == enumFile).ToList();

                if (bands.Count == 0)
                    continue;

                foreach (var member in Members(pair.Value))
                {
                    var band = bands.FirstOrDefault(b => member.Value >= b.Start && member.Value <= b.End);

                    if (band == null)
                        continue;

                    var single = registry.Rows.FirstOrDefault(r => r.EnumFile == enumFile && !r.IsRange && r.Start == member.Value);

                    if (single != null)
                    {
                        if (single.Name != member.Name)
                            problems.Add($"{single.Describe()} registers {enumFile} {member.Value} as '{single.Name}', but the enum member at that id is named '{member.Name}'. Fix whichever is wrong.");

                        continue;
                    }

                    var block = registry.Rows.FirstOrDefault(r => r.EnumFile == enumFile && r.IsRange && member.Value >= r.Start && member.Value <= r.End);

                    if (block != null)
                    {
                        problems.Add($"{enumFile}.{member.Name} = {member.Value} falls inside reserved block {block.Describe()} ('{block.Name}'). A block reservation does not register an individual id - split out a row: {enumFile}\\t{member.Value}\\t{member.Name}\\t{block.Owner}\\tactive\\t<notes>");
                        continue;
                    }

                    problems.Add($"{enumFile}.{member.Name} = {member.Value} is inside guarded band {band.Start}-{band.End} but has no row in {RegistryRelativePath}. Add (tab-separated): {enumFile}\\t{member.Value}\\t{member.Name}\\t<owner>\\tactive\\t<notes>");
                }
            }

            AssertNoProblems(problems);
        }

        /// <summary>
        /// (b) An active row claims an id that exists on master right now. Reserved rows are exempt - that
        /// exemption is exactly what lets a registry-only PR land before the code does.
        /// </summary>
        [TestMethod]
        public void Registry_ActiveRowsMatchAnExistingEnumMember()
        {
            var registry = LoadRegistry();
            var problems = new List<string>();

            foreach (var row in registry.Rows.Where(r => r.Status == "active" && !r.IsRange))
            {
                if (!EnumTypes.TryGetValue(row.EnumFile, out var type))
                    continue; // reported by Registry_IsWellFormed

                var matches = Members(type).Where(m => m.Value == row.Start).ToList();

                if (matches.Count == 0)
                {
                    problems.Add($"{row.Describe()} is 'active' but {row.EnumFile} has no member with value {row.Start}. Either the enum member was removed or renumbered, or the row should be 'reserved' until its code merges.");
                    continue;
                }

                if (!matches.Any(m => m.Name == row.Name))
                    problems.Add($"{row.Describe()} is 'active' as '{row.Name}', but {row.EnumFile} {row.Start} is named {string.Join("/", matches.Select(m => "'" + m.Name + "'"))}.");
            }

            AssertNoProblems(problems);
        }

        /// <summary>
        /// (c) A reserved row need not exist yet, but if the member DOES exist it must name-match - so the
        /// branch that reserved the id merges green with no registry edit, only a later flip to active.
        /// </summary>
        [TestMethod]
        public void Registry_ReservedRowsThatAlreadyExistStillNameMatch()
        {
            var registry = LoadRegistry();
            var problems = new List<string>();

            foreach (var row in registry.Rows.Where(r => r.Status == "reserved" && !r.IsRange))
            {
                if (!EnumTypes.TryGetValue(row.EnumFile, out var type))
                    continue; // reported by Registry_IsWellFormed

                var matches = Members(type).Where(m => m.Value == row.Start).ToList();

                if (matches.Count == 0)
                    continue; // reserved and not yet built - fine

                if (!matches.Any(m => m.Name == row.Name))
                    problems.Add($"{row.Describe()} reserves {row.EnumFile} {row.Start} for '{row.Name}', but that id is already taken by {string.Join("/", matches.Select(m => "'" + m.Name + "'"))}. This is a collision: pick a free id for one of them.");
            }

            AssertNoProblems(problems);
        }

        private static void AssertNoProblems(List<string> problems)
        {
            if (problems.Count == 0)
                return;

            Assert.Fail($"{problems.Count} problem(s) in {RegistryRelativePath}:{Environment.NewLine}  - {string.Join(Environment.NewLine + "  - ", problems)}");
        }

        private sealed class EnumMember
        {
            public string Name;
            public long Value;
        }

        private static IEnumerable<EnumMember> Members(Type enumType)
        {
            foreach (var name in System.Enum.GetNames(enumType))
                yield return new EnumMember { Name = name, Value = Convert.ToInt64(System.Enum.Parse(enumType, name)) };
        }

        private sealed class Band
        {
            public string EnumFile;
            public long Start;
            public long End;
            public int LineNumber;
        }

        /// <summary>
        /// Single-id lookup into the same parsed registry this suite validates, for the suites that guard one
        /// specific allocation (WaveChallengePropertyTests). Exposed so those tests read these rows rather than
        /// standing up a second parser over the same file. Returns null when no single-id row claims that id.
        /// </summary>
        internal static Row FindSingleIdRow(string enumFile, long id)
        {
            return LoadRegistry().Rows.FirstOrDefault(r => !r.IsRange && r.EnumFile == enumFile && r.Start == id);
        }

        internal sealed class Row
        {
            public string EnumFile;
            public string Range;
            public long Start;
            public long End;
            public bool IsRange;
            public string Name;
            public string Owner;
            public string Status;
            public string Notes;
            public int LineNumber;

            public string Describe() => $"line {LineNumber} ({EnumFile} {Range})";
        }

        private sealed class Registry
        {
            public List<Band> Bands = new List<Band>();
            public List<Row> Rows = new List<Row>();
        }

        private static Registry LoadRegistry()
        {
            var path = FindInSourceTree(RegistryRelativePath);

            Assert.IsNotNull(path, $"Could not find {RegistryRelativePath} by walking up from {AppContext.BaseDirectory}.");

            var registry = new Registry();
            var lines = File.ReadAllLines(path);

            for (var i = 0; i < lines.Length; i++)
            {
                var lineNumber = i + 1;

                // PowerShell's Set-Content -Encoding UTF8 prepends a BOM; without this the header line
                // would parse as a data row and the failure would point at the wrong thing entirely.
                var line = i == 0 ? lines[i].TrimStart(BomCharacter) : lines[i];

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    var bandFields = line.Split('\t');

                    if (bandFields[0] != "#band")
                        continue;

                    Assert.AreEqual(3, bandFields.Length, $"line {lineNumber}: a #band line needs exactly 3 tab-separated fields (#band, enum_file, start-end), found {bandFields.Length}.");

                    ParseIdOrRange(bandFields[2], lineNumber, out var bandStart, out var bandEnd, out _);

                    registry.Bands.Add(new Band { EnumFile = bandFields[1], Start = bandStart, End = bandEnd, LineNumber = lineNumber });
                    continue;
                }

                var fields = line.Split('\t');

                Assert.AreEqual(6, fields.Length,
                    $"line {lineNumber}: expected 6 tab-separated columns (enum_file, id_or_range, enum_name_or_block_label, owner, status, notes), found {fields.Length}. Line: {line}");

                ParseIdOrRange(fields[1], lineNumber, out var start, out var end, out var isRange);

                registry.Rows.Add(new Row
                {
                    EnumFile = fields[0],
                    Range = fields[1],
                    Start = start,
                    End = end,
                    IsRange = isRange,
                    Name = fields[2],
                    Owner = fields[3],
                    Status = fields[4],
                    Notes = fields[5],
                    LineNumber = lineNumber,
                });
            }

            return registry;
        }

        private static void ParseIdOrRange(string text, int lineNumber, out long start, out long end, out bool isRange)
        {
            var parts = text.Split('-');

            if (parts.Length == 1)
            {
                Assert.IsTrue(long.TryParse(parts[0], out start), $"line {lineNumber}: '{text}' is not a number or a start-end range.");
                end = start;
                isRange = false;
                return;
            }

            Assert.AreEqual(2, parts.Length, $"line {lineNumber}: '{text}' is not a number or a start-end range.");
            Assert.IsTrue(long.TryParse(parts[0], out start), $"line {lineNumber}: '{text}' has a non-numeric range start.");
            Assert.IsTrue(long.TryParse(parts[1], out end), $"line {lineNumber}: '{text}' has a non-numeric range end.");
            isRange = true;
        }

        /// <summary>
        /// Walks up from the test output directory looking for relativePath, so the lookup works regardless
        /// of the bin\Debug vs bin\x64\Debug output layout (same idiom as TestEnvironment's Config.js lookup).
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
