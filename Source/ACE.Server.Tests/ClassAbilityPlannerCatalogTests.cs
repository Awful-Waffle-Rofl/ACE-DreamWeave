using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Exports the LIVE class ability registry to tools/ca-planner/catalog.json and fails if the committed
    /// file has drifted from it.
    ///
    /// WHY A TEST AND NOT A SCRIPT. Roughly half the registry does not exist as source text: the Enhanced
    /// stat, Training bundle and Rating families are built at runtime by EnhancedStatAbility.GenerateAll,
    /// BundleStatAbility.GenerateAll and RatingAbility.GenerateAll. A regex parser over
    /// ClassAbilities/Abilities/*.cs cannot see them at all, so the export has to run against the constructed
    /// registry object - and the cheapest host that constructs it without a database or a running server is
    /// this test assembly (ClassAbilityRegistry is a pure static; ClassAbilityCapTotalsTests already
    /// enumerates it the same way).
    ///
    /// TUNABLES ARE READ, NEVER TYPED. Every value under "tunables" comes from
    /// <see cref="DefaultPropertyManager"/>'s default tables rather than being restated here, so a retune of
    /// a class_ability_* default shows up as a catalog diff. The default tables are plain public static
    /// dictionaries, so they are readable with no database and no PropertyManager.Initialize call - which is
    /// why this reads them directly instead of going through PropertyManager.GetLong/GetDouble (those resolve
    /// through the loaded, DB-backed set).
    ///
    /// REGENERATING. Set CA_PLANNER_REGEN=1 and run this test; it overwrites catalog.json and reports
    /// Inconclusive instead of asserting. See tools/ca-planner/README.md.
    ///
    /// PROVENANCE IS EXCLUDED FROM THE COMPARISON. "generatedFrom" records the sha and date of the run that
    /// produced the file. It cannot participate in the drift check, because committing the file changes HEAD
    /// and would make the very next run disagree with itself. The block's SHAPE is still asserted.
    /// </summary>
    [TestClass]
    public class ClassAbilityPlannerCatalogTests
    {
        private const string CatalogRelativePath = "tools/ca-planner/catalog.json";

        private const string RegenEnvironmentVariable = "CA_PLANNER_REGEN";

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            WriteIndented = true,

            // The descriptions are player-facing prose carrying apostrophes and the odd "+"; the default
            // encoder escapes them to ' / + and makes the committed file unreadable.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        [TestMethod]
        public void PlannerCatalog_MatchesTheLiveRegistry()
        {
            var path = CatalogPath();

            var generated = BuildCatalog(withProvenance: true);

            if (string.Equals(Environment.GetEnvironmentVariable(RegenEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                File.WriteAllText(path, Render(generated), new UTF8Encoding(false));

                Assert.Inconclusive($"{RegenEnvironmentVariable}=1: rewrote {path}. Unset it and re-run to validate, then commit the file.");
                return;
            }

            Assert.IsTrue(File.Exists(path),
                $"{CatalogRelativePath} does not exist. Regenerate it with {RegenEnvironmentVariable}=1 and commit it.");

            var committedText = File.ReadAllText(path);

            JsonNode committed;

            try
            {
                committed = JsonNode.Parse(committedText);
            }
            catch (JsonException ex)
            {
                Assert.Fail($"{CatalogRelativePath} is not valid JSON: {ex.Message}");
                return;
            }

            AssertProvenanceShape(committed);

            var expectedText = Render(StripProvenance(generated));
            var actualText = Render(StripProvenance(committed));

            if (string.Equals(expectedText, actualText, StringComparison.Ordinal))
                return;

            Assert.Fail(
                $"{CatalogRelativePath} has drifted from the live registry.{Environment.NewLine}" +
                $"Regenerate with {RegenEnvironmentVariable}=1 and commit the result.{Environment.NewLine}{Environment.NewLine}" +
                DescribeDifference(actualText, expectedText));
        }

        /// <summary>
        /// The tunables block reproduces the class_ability_* defaults. Pinned separately from the file
        /// comparison so a retune reads as "a tunable changed", not as "the catalog is stale" - and so the
        /// keys the planner depends on cannot be renamed out from under it silently.
        /// </summary>
        [TestMethod]
        public void PlannerCatalog_TunablesComeFromThePropertyDefaults()
        {
            var tunables = BuildTunables();

            Assert.AreEqual(Long("class_ability_tier2_cap_required"), (long)tunables["tier2CapRequired"]);
            Assert.AreEqual(Long("class_ability_tier2_spent_required"), (long)tunables["tier2SpentRequired"]);
            Assert.AreEqual(Long("class_ability_tier3_cap_required"), (long)tunables["tier3CapRequired"]);
            Assert.AreEqual(Long("class_ability_tier3_spent_required"), (long)tunables["tier3SpentRequired"]);

            var lum = tunables["lum"].AsObject();
            Assert.AreEqual(Long("class_ability_lum_base_cost"), (long)lum["base"]);
            Assert.AreEqual(Double("class_ability_lum_ratio_1"), (double)lum["r1"], 0.0);
            Assert.AreEqual(Double("class_ability_lum_ratio_2"), (double)lum["r2"], 0.0);
            Assert.AreEqual(Double("class_ability_lum_ratio_3"), (double)lum["r3"], 0.0);
            Assert.AreEqual(Long("class_ability_lum_breakpoint_1"), (long)lum["bp1"]);
            Assert.AreEqual(Long("class_ability_lum_breakpoint_2"), (long)lum["bp2"]);

            var xp = tunables["xp"].AsObject();
            Assert.AreEqual(Long("class_ability_xp_base_cost"), (long)xp["base"]);
            Assert.AreEqual(Double("class_ability_xp_ratio_1"), (double)xp["r1"], 0.0);
            Assert.AreEqual(Double("class_ability_xp_ratio_2"), (double)xp["r2"], 0.0);
            Assert.AreEqual(Double("class_ability_xp_ratio_3"), (double)xp["r3"], 0.0);
            Assert.AreEqual(Long("class_ability_xp_breakpoint_1"), (long)xp["bp1"]);
            Assert.AreEqual(Long("class_ability_xp_breakpoint_2"), (long)xp["bp2"]);
            Assert.AreEqual(Long("class_ability_xp_min_level"), (long)xp["minLevel"]);
        }

        /// <summary>
        /// Retired abilities are EMITTED, not dropped: a saved planner build referencing one has to be
        /// explainable rather than looking corrupt. They are the only rows carrying "retired": true.
        /// </summary>
        [TestMethod]
        public void PlannerCatalog_CarriesEveryRetiredAbility()
        {
            var abilities = BuildCatalog(withProvenance: false)["abilities"].AsArray();

            var retiredInFile = abilities
                .Where(a => (bool)a["retired"])
                .Select(a => (string)a["id"])
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            var retiredInSource = RetiredClassAbilities.All()
                .Select(r => r.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(retiredInSource, retiredInFile,
                "the catalog's retired rows must be exactly RetiredClassAbilities.All()");

            // ...and every LIVE registry entry appears exactly once, un-retired.
            var liveInFile = abilities
                .Where(a => !(bool)a["retired"])
                .Select(a => (string)a["id"])
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            var liveInRegistry = ClassAbilityRegistry.Abilities.Values
                .Select(d => d.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(liveInRegistry, liveInFile);
        }

        /// <summary>
        /// The ordering contract: class, then tier, then id. A planner diffing two catalogs across releases
        /// needs the file to be line-stable, so this is pinned rather than left to whatever order the
        /// registry dictionary happens to enumerate in.
        /// </summary>
        [TestMethod]
        public void PlannerCatalog_IsDeterministicallyOrdered()
        {
            var abilities = BuildCatalog(withProvenance: false)["abilities"].AsArray();

            var keys = abilities
                .Select(a => (Class: ClassRank((string)a["class"]), Tier: (int)a["tier"], Id: (string)a["id"]))
                .ToArray();

            for (var i = 1; i < keys.Length; i++)
            {
                var previous = keys[i - 1];
                var current = keys[i];

                var ordered = previous.Class < current.Class
                    || (previous.Class == current.Class && previous.Tier < current.Tier)
                    || (previous.Class == current.Class && previous.Tier == current.Tier
                        && string.CompareOrdinal(previous.Id, current.Id) < 0);

                Assert.IsTrue(ordered, $"catalog rows out of order at index {i}: {previous} then {current}");
            }
        }

        /// <summary>The level milestones come from ClassAbilityMilestones, never from a hand-typed list.</summary>
        [TestMethod]
        public void PlannerCatalog_LevelMilestonesComeFromTheSource()
        {
            var milestones = BuildCatalog(withProvenance: false)["levelMilestones"].AsArray()
                .Select(n => (int)n)
                .ToArray();

            CollectionAssert.AreEqual(ClassAbilityMilestones.Levels.ToArray(), milestones);
        }

        // ---------------------------------------------------------------------------------------------
        // catalog construction
        // ---------------------------------------------------------------------------------------------

        private static JsonObject BuildCatalog(bool withProvenance)
        {
            var root = new JsonObject();

            if (withProvenance)
            {
                root["generatedFrom"] = new JsonObject
                {
                    ["sha"] = HeadSha(),
                    ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                };
            }

            root["tunables"] = BuildTunables();

            var milestones = new JsonArray();
            foreach (var level in ClassAbilityMilestones.Levels)
                milestones.Add(level);
            root["levelMilestones"] = milestones;

            var rows = new List<(int ClassRank, int Tier, string Id, JsonObject Node)>();

            foreach (var definition in ClassAbilityRegistry.Abilities.Values)
            {
                rows.Add(((int)definition.AbilityClass, definition.Tier, definition.Name, new JsonObject
                {
                    ["id"] = definition.Name,
                    ["displayName"] = definition.DisplayName,
                    ["class"] = definition.AbilityClass.ToString(),
                    ["tier"] = definition.Tier,
                    ["category"] = definition.Category,
                    ["maxRank"] = definition.MaxRank,
                    ["costPerRank"] = ToArray(definition.CostPerRank),
                    ["affinitySkill"] = definition.AffinitySkill.HasValue
                        ? JsonValue.Create(definition.AffinitySkill.Value.ToString())
                        : null,
                    ["additionalAffinitySkills"] = ToSkillArray(definition.AdditionalAffinitySkills),
                    ["description"] = definition.Description,
                    ["implemented"] = definition.Implemented,
                    ["retired"] = false,
                }));
            }

            // Retired abilities are gone from the registry, so class / tier / category / description no
            // longer exist for them. They are emitted with the neutral class (None) and tier 0 and an empty
            // description, which is enough for the planner to price and name an orphaned rank. See
            // tools/ca-planner/README.md.
            foreach (var retired in RetiredClassAbilities.All())
            {
                rows.Add((0, 0, retired.Name, new JsonObject
                {
                    ["id"] = retired.Name,
                    ["displayName"] = retired.DisplayName,
                    ["class"] = ClassAbilityClass.None.ToString(),
                    ["tier"] = 0,
                    ["category"] = "Retired",
                    ["maxRank"] = retired.MaxRank,
                    ["costPerRank"] = ToArray(retired.CostPerRank),
                    ["affinitySkill"] = null,
                    ["additionalAffinitySkills"] = new JsonArray(),
                    ["description"] = string.Empty,
                    ["implemented"] = false,
                    ["retired"] = true,
                }));
            }

            var abilities = new JsonArray();

            foreach (var row in rows
                .OrderBy(r => r.ClassRank)
                .ThenBy(r => r.Tier)
                .ThenBy(r => r.Id, StringComparer.Ordinal))
            {
                abilities.Add(row.Node);
            }

            root["abilities"] = abilities;

            return root;
        }

        private static JsonObject BuildTunables() => new JsonObject
        {
            ["tier2CapRequired"] = Long("class_ability_tier2_cap_required"),
            ["tier2SpentRequired"] = Long("class_ability_tier2_spent_required"),
            ["tier3CapRequired"] = Long("class_ability_tier3_cap_required"),
            ["tier3SpentRequired"] = Long("class_ability_tier3_spent_required"),

            ["lum"] = new JsonObject
            {
                ["base"] = Long("class_ability_lum_base_cost"),
                ["r1"] = Double("class_ability_lum_ratio_1"),
                ["r2"] = Double("class_ability_lum_ratio_2"),
                ["r3"] = Double("class_ability_lum_ratio_3"),
                ["bp1"] = Long("class_ability_lum_breakpoint_1"),
                ["bp2"] = Long("class_ability_lum_breakpoint_2"),
            },

            ["xp"] = new JsonObject
            {
                ["base"] = Long("class_ability_xp_base_cost"),
                ["r1"] = Double("class_ability_xp_ratio_1"),
                ["r2"] = Double("class_ability_xp_ratio_2"),
                ["r3"] = Double("class_ability_xp_ratio_3"),
                ["bp1"] = Long("class_ability_xp_breakpoint_1"),
                ["bp2"] = Long("class_ability_xp_breakpoint_2"),
                ["minLevel"] = Long("class_ability_xp_min_level"),
            },
        };

        /// <summary>
        /// The secondary affinity skills, by name, or an empty array. Always emitted so the planner can read
        /// the field unconditionally rather than probing for it.
        /// </summary>
        private static JsonArray ToSkillArray(ACE.Entity.Enum.Skill[] skills)
        {
            var array = new JsonArray();

            if (skills != null)
            {
                foreach (var skill in skills)
                    array.Add(skill.ToString());
            }

            return array;
        }

        private static JsonArray ToArray(IReadOnlyList<int> values)
        {
            var array = new JsonArray();

            if (values != null)
            {
                foreach (var value in values)
                    array.Add(value);
            }

            return array;
        }

        private static long Long(string key)
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey(key),
                $"{key} is not a known long property default - it was renamed or removed, and the planner catalog depends on it.");

            return DefaultPropertyManager.DefaultLongProperties[key].Item;
        }

        private static double Double(string key)
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key),
                $"{key} is not a known double property default - it was renamed or removed, and the planner catalog depends on it.");

            return DefaultPropertyManager.DefaultDoubleProperties[key].Item;
        }

        private static int ClassRank(string name) => (int)System.Enum.Parse<ClassAbilityClass>(name);

        // ---------------------------------------------------------------------------------------------
        // rendering / comparison
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Serializes with a fixed newline. System.Text.Json's indented writer defaults to
        /// Environment.NewLine, which would make the file differ between a Windows and a Linux run for no
        /// reason - so the newline is normalized to \n here, and the file is committed that way.
        /// </summary>
        private static string Render(JsonNode node) =>
            node.ToJsonString(WriteOptions).Replace("\r\n", "\n") + "\n";

        private static JsonNode StripProvenance(JsonNode node)
        {
            var clone = JsonNode.Parse(node.ToJsonString());
            clone.AsObject().Remove("generatedFrom");
            return clone;
        }

        private static void AssertProvenanceShape(JsonNode committed)
        {
            var provenance = committed["generatedFrom"];

            Assert.IsNotNull(provenance, "catalog.json is missing its generatedFrom block.");

            var sha = (string)provenance["sha"];
            var date = (string)provenance["date"];

            Assert.IsFalse(string.IsNullOrWhiteSpace(sha), "generatedFrom.sha is empty.");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(date ?? string.Empty, @"^\d{4}-\d{2}-\d{2}$"),
                $"generatedFrom.date must be yyyy-mm-dd, found '{date}'.");
        }

        /// <summary>
        /// Prints the first differing lines with line numbers plus the surrounding context, so a failing run
        /// says WHAT drifted rather than only THAT something did.
        /// </summary>
        private static string DescribeDifference(string actual, string expected)
        {
            var actualLines = actual.Split('\n');
            var expectedLines = expected.Split('\n');

            var report = new StringBuilder();
            var shown = 0;

            for (var i = 0; i < Math.Max(actualLines.Length, expectedLines.Length) && shown < 20; i++)
            {
                var a = i < actualLines.Length ? actualLines[i] : "<end of file>";
                var e = i < expectedLines.Length ? expectedLines[i] : "<end of file>";

                if (string.Equals(a, e, StringComparison.Ordinal))
                    continue;

                report.AppendLine($"line {i + 1}:");
                report.AppendLine($"  committed: {a.Trim()}");
                report.AppendLine($"  live     : {e.Trim()}");
                shown++;
            }

            if (shown == 0)
                report.AppendLine("(no line-level difference found - the files differ only in trailing whitespace)");
            else if (actualLines.Length != expectedLines.Length)
                report.AppendLine($"(committed has {actualLines.Length} lines, live registry produces {expectedLines.Length})");

            return report.ToString();
        }

        // ---------------------------------------------------------------------------------------------
        // paths
        // ---------------------------------------------------------------------------------------------

        internal static string CatalogPath() =>
            Path.Combine(ClassAbilityAffinityDeclarationTests.RepoRoot(), "tools", "ca-planner", "catalog.json");

        /// <summary>
        /// HEAD's sha, for the provenance block only. Shelling out to git is acceptable here because it runs
        /// on the regeneration path alone; a checkout with no git available still validates, it just cannot
        /// re-stamp provenance.
        /// </summary>
        private static string HeadSha()
        {
            try
            {
                var info = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = ClassAbilityAffinityDeclarationTests.RepoRoot(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (var process = Process.Start(info))
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(15000);

                    return string.IsNullOrWhiteSpace(output) ? "unknown" : output;
                }
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
