using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="ACE.Server.WorldContentPlanner"/> (phase classification, header parsing,
    /// dependency ordering, hard-error detection) plus the boot applier's pure cache-key/migration helpers.
    /// No database or server bootstrap is touched -- the planner is a pure function of its inputs.
    /// </summary>
    [TestClass]
    public class WorldContentPlannerTests
    {
        // ---- helpers ----------------------------------------------------------------

        private static ACE.Server.ContentFileInput File(string relPath, string header = null)
            => new ACE.Server.ContentFileInput("C:/abs/" + relPath, relPath, header ?? string.Empty);

        private static string Header(string unit = null, string dependsOn = null, string phase = null)
        {
            var lines = new List<string>();
            if (phase != null) lines.Add($"-- @phase: {phase}");
            if (unit != null) lines.Add($"-- @unit: {unit}");
            if (dependsOn != null) lines.Add($"-- @depends-on: {dependsOn}");
            lines.Add("");
            lines.Add("INSERT INTO `weenie` ...;");
            return string.Join("\n", lines);
        }

        private static List<string> OrderedPaths(ACE.Server.WorldContentPlan plan)
            => plan.Files.Select(f => f.RelativePath).ToList();

        // Builds a platform-native ABSOLUTE path from segments. The cache-key/migration helpers rely on
        // Path.GetRelativePath, which treats a "C:\..." string as absolute only on Windows -- on Linux (where
        // CI and stage/prod containers run) it is a relative path and the relative-key math breaks. Rooting at
        // "/" on non-Windows keeps these fixtures genuinely absolute on every OS.
        private static string AbsPath(params string[] segs)
            => Path.GetFullPath(Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(segs)));

        // ---- folder -> phase mapping ------------------------------------------------

        [TestMethod]
        public void Classify_FolderMapping_CoversEveryPhase()
        {
            Assert.AreEqual(ACE.Server.ContentPhase.Weenies, ACE.Server.WorldContentPlanner.ClassifyByPath("sql/weenies/1000045 Foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Realms, ACE.Server.WorldContentPlanner.ClassifyByPath("realms/foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Realms, ACE.Server.WorldContentPlanner.ClassifyByPath("dungeons/foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Realms, ACE.Server.WorldContentPlanner.ClassifyByPath("outdoor/foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Placements, ACE.Server.WorldContentPlanner.ClassifyByPath("placements/foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Quests, ACE.Server.WorldContentPlanner.ClassifyByPath("sql/quests/Foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Patches, ACE.Server.WorldContentPlanner.ClassifyByPath("sql/patches/foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Patches, ACE.Server.WorldContentPlanner.ClassifyByPath("migrations/foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Unclassified, ACE.Server.WorldContentPlanner.ClassifyByPath("json/foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Unclassified, ACE.Server.WorldContentPlanner.ClassifyByPath("random.sql"));
        }

        [TestMethod]
        public void Classify_PreviewIsNoLongerAWeenieLocation()
        {
            // preview/ is excluded at discovery (see IsExcludedFromAutoApply); the phase map must no longer
            // advertise it as an applied weenie location.
            Assert.AreNotEqual(ACE.Server.ContentPhase.Weenies, ACE.Server.WorldContentPlanner.ClassifyByPath("preview/1000093 PalTest.sql"));
            Assert.AreNotEqual(ACE.Server.ContentPhase.Weenies, ACE.Server.WorldContentPlanner.ClassifyByPath("preview/monster_palette_units_test/1000093 PalTest.sql"));
            Assert.AreNotEqual(ACE.Server.ContentPhase.Weenies, ACE.Server.WorldContentPlanner.ClassifyByPath("MyAddedRoot/preview/x.sql"));
        }

        // ---- preview exclusion (never auto-applied) ---------------------------------

        [TestMethod]
        public void IsExcludedFromAutoApply_ExcludesPreviewSegment_RootLevelAndNested()
        {
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("preview/x.sql"));
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("sql/preview/x.sql"));
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("a/b/preview/c.sql"));
            // nested under a prefixed added-path root key
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("MyAddedRoot/preview/monster_palette_units_test/1001500 Foo.sql"));
        }

        [TestMethod]
        public void IsExcludedFromAutoApply_IsCaseInsensitive()
        {
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("Preview/x.sql"));
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("PREVIEW/x.sql"));
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("sql/PreView/x.sql"));
        }

        [TestMethod]
        public void IsExcludedFromAutoApply_MatchesWholeSegmentOnly()
        {
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("previewthing.sql"));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("previews/x.sql"));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("sql/previews/x.sql"));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("sql/weenies/preview_of_a_thing.sql"));
        }

        // ---- landblock export exclusion (never auto-applied) ------------------------

        [TestMethod]
        public void IsLandblockExport_MatchesWholeSegmentOnly()
        {
            // true: whole "landblocks" segment, any depth, either slash style, case-insensitive
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsLandblockExport("sql/landblocks/016C.sql"));
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsLandblockExport(@"sql\landblocks\016C.sql"));
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsLandblockExport("MyAddedRoot/sql/landblocks/016C.sql"));
            Assert.IsTrue(ACE.Server.WorldContentPlanner.IsLandblockExport("SQL/LANDBLOCKS/016C.sql"));

            // false: not a "landblocks" segment (different unit, prefix/suffix match, no segment at all)
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsLandblockExport("placements/marketplace_effigist.sql"));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsLandblockExport("sql/weenies/landblocks.sql"));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsLandblockExport("sql/landblocksfoo/x.sql"));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsLandblockExport(""));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsLandblockExport(null));
        }

        [TestMethod]
        public void IsExcludedFromAutoApply_NormalWeenieIsNotExcluded_AndStillClassifies()
        {
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply("sql/weenies/1000001 Thing.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Weenies, ACE.Server.WorldContentPlanner.ClassifyByPath("sql/weenies/1000001 Thing.sql"));
        }

        [TestMethod]
        public void IsExcludedFromAutoApply_HandlesEmptyAndNull()
        {
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply(null));
            Assert.IsFalse(ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply(string.Empty));
        }

        [TestMethod]
        public void Classify_IsCaseInsensitive()
        {
            Assert.AreEqual(ACE.Server.ContentPhase.Weenies, ACE.Server.WorldContentPlanner.ClassifyByPath("SQL/WEENIES/Foo.sql"));
            Assert.AreEqual(ACE.Server.ContentPhase.Realms, ACE.Server.WorldContentPlanner.ClassifyByPath("Realms/Foo.sql"));
        }

        // ---- @phase override --------------------------------------------------------

        [TestMethod]
        public void PhaseOverride_OverridesFolderMapping()
        {
            // File physically under realms/ but declares @phase: weenies -> classified weenies.
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/actually_a_weenie.sql", Header(unit: "realms/aw", phase: "weenies")),
            });

            Assert.IsTrue(plan.IsValid, string.Join(" | ", plan.Diagnostics));
            Assert.AreEqual(ACE.Server.ContentPhase.Weenies, plan.Files.Single().Phase);
        }

        [TestMethod]
        public void UnknownPhase_IsHardError()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/x.sql", Header(unit: "realms/x", phase: "bogus")),
            });

            Assert.IsFalse(plan.IsValid);
            Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Unknown @phase") && d.Contains("bogus")));
        }

        // ---- unclassified -----------------------------------------------------------

        [TestMethod]
        public void Unclassified_SortsLast_PlanStillValid()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("json/orphan.sql"),
                File("sql/weenies/1000045 Foo.sql"),
                File("realms/foo.sql", Header(unit: "realms/foo")),
            });

            Assert.IsTrue(plan.IsValid, string.Join(" | ", plan.Diagnostics));
            Assert.AreEqual("json/orphan.sql", plan.Files.Last().RelativePath);
            Assert.AreEqual(ACE.Server.ContentPhase.Unclassified, plan.Files.Last().Phase);
        }

        // ---- deterministic / ordinal ordering --------------------------------------

        [TestMethod]
        public void Ordering_IsDeterministic_AcrossRuns()
        {
            var inputs = new[]
            {
                File("sql/quests/B.sql"),
                File("realms/z.sql", Header(unit: "realms/z")),
                File("sql/weenies/2.sql"),
                File("sql/weenies/1.sql"),
                File("placements/p.sql"),
            };

            var a = OrderedPaths(ACE.Server.WorldContentPlanner.CreatePlan(inputs));
            var b = OrderedPaths(ACE.Server.WorldContentPlanner.CreatePlan(inputs.Reverse().ToArray()));

            CollectionAssert.AreEqual(a, b);
            // phase order: weenies(10) then realms(20) then placements(30) then quests(40)
            CollectionAssert.AreEqual(
                new List<string> { "sql/weenies/1.sql", "sql/weenies/2.sql", "realms/z.sql", "placements/p.sql", "sql/quests/B.sql" },
                a);
        }

        [TestMethod]
        public void Ordering_IsOrdinalNotCulture()
        {
            // Within one phase, ordinal puts uppercase 'Z' (0x5A) before lowercase 'a' (0x61);
            // a culture-aware sort would case-fold and place "apple" first. Prove we use ordinal.
            var inputs = new[]
            {
                File("realms/apple.sql", Header(unit: "realms/apple")),
                File("realms/Zebra.sql", Header(unit: "realms/Zebra")),
            };

            var order = OrderedPaths(ACE.Server.WorldContentPlanner.CreatePlan(inputs));

            var ordinal = inputs.Select(i => i.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var culture = inputs.Select(i => i.RelativePath).OrderBy(p => p, StringComparer.InvariantCulture).ToList();

            CollectionAssert.AreEqual(ordinal, order);                       // matches ordinal
            CollectionAssert.AreNotEqual(culture, order);                    // and differs from culture
            Assert.AreEqual("realms/Zebra.sql", order.First());
        }

        // ---- @depends-on reordering -------------------------------------------------

        [TestMethod]
        public void DependsOn_ReordersWithinPhase()
        {
            // "realms/aaa" sorts before "realms/zzz" alphabetically, but aaa depends on zzz,
            // so zzz must be applied first.
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/aaa.sql", Header(unit: "realms/aaa", dependsOn: "realms/zzz")),
                File("realms/zzz.sql", Header(unit: "realms/zzz", dependsOn: "none")),
            });

            Assert.IsTrue(plan.IsValid, string.Join(" | ", plan.Diagnostics));
            CollectionAssert.AreEqual(new List<string> { "realms/zzz.sql", "realms/aaa.sql" }, OrderedPaths(plan));
        }

        [TestMethod]
        public void DependsOn_StripsInlineComment()
        {
            // apply-content.sh grammar allows a trailing inline SQL comment on @depends-on.
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/a.sql", Header(unit: "realms/a", dependsOn: "realms/b   -- b owns the registry")),
                File("realms/b.sql", Header(unit: "realms/b")),
            });

            Assert.IsTrue(plan.IsValid, string.Join(" | ", plan.Diagnostics));
            CollectionAssert.AreEqual(new List<string> { "realms/b.sql", "realms/a.sql" }, OrderedPaths(plan));
        }

        // ---- cross-phase dependencies ----------------------------------------------

        [TestMethod]
        public void LegalCrossPhaseDependency_WeenieBeforeRealm()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/foo.sql", Header(unit: "realms/foo", dependsOn: "weenies/mob")),
                File("sql/weenies/mob.sql", Header(unit: "weenies/mob")),
            });

            Assert.IsTrue(plan.IsValid, string.Join(" | ", plan.Diagnostics));
            CollectionAssert.AreEqual(new List<string> { "sql/weenies/mob.sql", "realms/foo.sql" }, OrderedPaths(plan));
        }

        [TestMethod]
        public void PhaseInversion_IsHardError()
        {
            // A weenie (phase 10) depending on a quest (phase 40) is a phase inversion.
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("sql/weenies/mob.sql", Header(unit: "weenies/mob", dependsOn: "quests/q")),
                File("sql/quests/q.sql", Header(unit: "quests/q")),
            });

            Assert.IsFalse(plan.IsValid);
            Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Phase inversion")));
        }

        // ---- error cases ------------------------------------------------------------

        [TestMethod]
        public void UnknownDependency_IsHardError()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/foo.sql", Header(unit: "realms/foo", dependsOn: "realms/does_not_exist")),
            });

            Assert.IsFalse(plan.IsValid);
            Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Unknown dependency") && d.Contains("realms/does_not_exist")));
        }

        [TestMethod]
        public void Cycle_IsHardError_NamesTheCycle()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/a.sql", Header(unit: "realms/a", dependsOn: "realms/b")),
                File("realms/b.sql", Header(unit: "realms/b", dependsOn: "realms/a")),
            });

            Assert.IsFalse(plan.IsValid);
            var cycleDiag = plan.Diagnostics.FirstOrDefault(d => d.StartsWith("Dependency cycle"));
            Assert.IsNotNull(cycleDiag, "expected a cycle diagnostic");
            Assert.IsTrue(cycleDiag.Contains("realms/a") && cycleDiag.Contains("realms/b"));
        }

        [TestMethod]
        public void DuplicateUnit_IsHardError_NamesBothPaths()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/a.sql", Header(unit: "realms/dup")),
                File("realms/b.sql", Header(unit: "realms/dup")),
            });

            Assert.IsFalse(plan.IsValid);
            var dup = plan.Diagnostics.FirstOrDefault(d => d.Contains("Duplicate @unit"));
            Assert.IsNotNull(dup);
            Assert.IsTrue(dup.Contains("realms/a.sql") && dup.Contains("realms/b.sql"));
        }

        [TestMethod]
        public void MultipleErrors_AllReportedInOneResult()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/a.sql", Header(unit: "realms/dup", phase: "bogus")),          // unknown phase
                File("realms/b.sql", Header(unit: "realms/dup")),                            // duplicate unit
                File("realms/c.sql", Header(unit: "realms/c", dependsOn: "realms/ghost")),  // unknown dep
            });

            Assert.IsFalse(plan.IsValid);
            Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Unknown @phase")));
            Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Duplicate @unit")));
            Assert.IsTrue(plan.Diagnostics.Any(d => d.Contains("Unknown dependency")));
            Assert.IsTrue(plan.Diagnostics.Count >= 3, $"expected >=3 diagnostics, got {plan.Diagnostics.Count}");
        }

        // ---- transitive-dependent helper -------------------------------------------

        [TestMethod]
        public void TransitiveDependents_ComputesFalloutOfAFailedUnit()
        {
            // A <- B <- C : B depends on A, C depends on B (A is the root dependency).
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("realms/a.sql", Header(unit: "A")),
                File("realms/b.sql", Header(unit: "B", dependsOn: "A")),
                File("realms/c.sql", Header(unit: "C", dependsOn: "B")),
            });

            Assert.IsTrue(plan.IsValid, string.Join(" | ", plan.Diagnostics));

            // B failed -> only C is downstream of B.
            var fromB = plan.GetTransitiveDependents(new[] { "B" });
            CollectionAssert.AreEquivalent(new List<string> { "C" }, fromB.ToList());

            // A failed -> both B and C fall out.
            var fromA = plan.GetTransitiveDependents(new[] { "A" });
            CollectionAssert.AreEquivalent(new List<string> { "B", "C" }, fromA.ToList());

            // A leaf unit with no dependents yields nothing.
            Assert.AreEqual(0, plan.GetTransitiveDependents(new[] { "C" }).Count);
        }

        [TestMethod]
        public void EmptyInput_IsValidEmptyPlan()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(Array.Empty<ACE.Server.ContentFileInput>());
            Assert.IsTrue(plan.IsValid);
            Assert.AreEqual(0, plan.Files.Count);
        }

        [TestMethod]
        public void HeaderlessFile_ParticipatesButCannotBeDependedOn()
        {
            var plan = ACE.Server.WorldContentPlanner.CreatePlan(new[]
            {
                File("sql/weenies/1000045 Foo.sql"),                                     // no header
                File("realms/foo.sql", Header(unit: "realms/foo", dependsOn: "none")),
            });

            Assert.IsTrue(plan.IsValid, string.Join(" | ", plan.Diagnostics));
            var weenie = plan.Files.Single(f => f.RelativePath.StartsWith("sql/weenies/"));
            Assert.IsNull(weenie.UnitName);
        }

        // ---- cache-key normalization + v1->v2 migration (pure applier helpers) ------

        [TestMethod]
        public void ComputeContentRelativeKey_PrimaryRoot_IsUnprefixed()
        {
            var root = AbsPath("ace", "Content");
            var abs = AbsPath("ace", "Content", "realms", "foo.sql");
            Assert.AreEqual("realms/foo.sql", ACE.Server.Program.ComputeContentRelativeKey(abs, root, isAddedPath: false));
        }

        [TestMethod]
        public void ComputeContentRelativeKey_AddedRoot_IsPrefixedWithLeafDir()
        {
            var root = AbsPath("extra", "MoreContent");
            var abs = AbsPath("extra", "MoreContent", "realms", "foo.sql");
            Assert.AreEqual("MoreContent/realms/foo.sql", ACE.Server.Program.ComputeContentRelativeKey(abs, root, isAddedPath: true));
        }

        [TestMethod]
        public void MigrateCacheKeysToV2_RewritesResolvable_DropsUnresolvable()
        {
            var roots = new List<(string, bool)>
            {
                (AbsPath("ace", "Content"), false),
                (AbsPath("extra", "MoreContent"), true),
            };

            var v1 = new Dictionary<string, ACE.Server.Program.WorldCustomizationCacheEntry>
            {
                [AbsPath("ace", "Content", "realms", "foo.sql")] = new ACE.Server.Program.WorldCustomizationCacheEntry { Size = 1, Sha256 = "AAA" },
                [AbsPath("extra", "MoreContent", "sql", "weenies", "9.sql")] = new ACE.Server.Program.WorldCustomizationCacheEntry { Size = 2, Sha256 = "BBB" },
                [AbsPath("somewhere", "else", "orphan.sql")] = new ACE.Server.Program.WorldCustomizationCacheEntry { Size = 3, Sha256 = "CCC" },
            };

            var v2 = ACE.Server.Program.MigrateCacheKeysToV2(v1, roots);

            Assert.AreEqual(2, v2.Count, "orphan key not under any root should be dropped");
            Assert.IsTrue(v2.ContainsKey("realms/foo.sql"));
            Assert.AreEqual("AAA", v2["realms/foo.sql"].Sha256);              // entry preserved (no re-apply)
            Assert.IsTrue(v2.ContainsKey("MoreContent/sql/weenies/9.sql"));
            Assert.IsFalse(v2.Keys.Any(k => k.Contains("orphan")));
        }

        // ---- live-tree regression tripwire -----------------------------------------

        [TestMethod]
        public void LiveTree_AllRealContentUnits_ProduceAValidPlan()
        {
            var contentDir = FindRepoContentDir();
            if (contentDir == null)
                Assert.Inconclusive("Could not locate the repo's Content/ directory by walking up from the test assembly -- skipping live-tree regression check.");

            var inputs = new List<ACE.Server.ContentFileInput>();
            foreach (var file in Directory.EnumerateFiles(contentDir, "*.sql", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(contentDir, file).Replace('\\', '/');

                // Exclude a nested worktree's Content that sits BELOW the found Content dir. This checks the
                // path RELATIVE to contentDir, so the found dir's own ancestors (this worktree lives under
                // .claude/worktrees/ itself) never disqualify it.
                if (rel.IndexOf(".claude/worktrees/", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                // Mirror the boot applier: preview/ and sql/landblocks/ files are dropped at discovery and never planned.
                if (ACE.Server.WorldContentPlanner.IsExcludedFromAutoApply(rel) ||
                    ACE.Server.WorldContentPlanner.IsLandblockExport(rel))
                    continue;

                var header = ReadFirstLines(file, 30);
                inputs.Add(new ACE.Server.ContentFileInput(file, rel, header));
            }

            if (inputs.Count == 0)
                Assert.Inconclusive($"Found Content/ at '{contentDir}' but it held no .sql files -- skipping.");

            var plan = ACE.Server.WorldContentPlanner.CreatePlan(inputs);

            Assert.IsTrue(plan.IsValid,
                $"The repo's real content units ({inputs.Count} files under {contentDir}) must produce a VALID plan. Diagnostics:\n  " +
                string.Join("\n  ", plan.Diagnostics));
        }

        private static string ReadFirstLines(string filePath, int maxLines)
        {
            var lines = new List<string>();
            using var reader = new StreamReader(filePath);
            string line;
            var count = 0;
            while (count < maxLines && (line = reader.ReadLine()) != null)
            {
                lines.Add(line);
                count++;
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Walks up from the test assembly location looking for a sibling "Content" directory that actually
        /// holds .sql files. Robust to the bin/x64/Debug/netX nesting and to running inside a git worktree.
        /// </summary>
        private static string FindRepoContentDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Content");
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, "*.sql", SearchOption.AllDirectories).Any())
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            return null;
        }
    }
}
