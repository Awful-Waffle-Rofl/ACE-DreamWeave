using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ACE.Server
{
    /// <summary>
    /// Phase buckets for boot-time world-content application. Lower numeric value = applied earlier.
    /// The numeric spacing is deliberate so intermediate phases can be inserted later without a renumber.
    /// </summary>
    internal enum ContentPhase
    {
        Weenies = 10,
        Realms = 20,
        Placements = 30,
        Quests = 40,
        Patches = 50,
        Unclassified = 90,
    }

    /// <summary>
    /// One discovered content .sql file handed to the planner. Carries only what the planner needs so
    /// it has NO database or filesystem dependency and is fully unit-testable.
    /// </summary>
    internal sealed class ContentFileInput
    {
        /// <summary>Absolute path on disk (opaque to the planner; echoed back on the plan).</summary>
        public string AbsolutePath { get; }

        /// <summary>Repo-relative path, forward slashes (e.g. "sql/weenies/1000045 Foo.sql"). Drives phase + ordering.</summary>
        public string RelativePath { get; }

        /// <summary>The first ~30 lines of the file (the unit header block). May be null/empty.</summary>
        public string HeaderText { get; }

        public ContentFileInput(string absolutePath, string relativePath, string headerText)
        {
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            HeaderText = headerText ?? string.Empty;
        }
    }

    /// <summary>One file in a VALID plan, in application order, carrying its resolved phase/unit/edges.</summary>
    internal sealed class PlannedContentFile
    {
        public string AbsolutePath { get; }
        public string RelativePath { get; }
        public ContentPhase Phase { get; }

        /// <summary>The file's @unit name, or null if it declared none (can't be depended on).</summary>
        public string UnitName { get; }

        /// <summary>Resolved dependency unit names (each names a file that must apply before this one).</summary>
        public IReadOnlyList<string> DependsOn { get; }

        public bool IsUnclassified => Phase == ContentPhase.Unclassified;

        public PlannedContentFile(string absolutePath, string relativePath, ContentPhase phase, string unitName, IReadOnlyList<string> dependsOn)
        {
            AbsolutePath = absolutePath;
            RelativePath = relativePath;
            Phase = phase;
            UnitName = unitName;
            DependsOn = dependsOn ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Result of planning: either VALID (an ordered file list + a transitive-dependent helper) or
    /// INVALID (a full list of human-readable diagnostics). On INVALID the caller applies nothing.
    /// </summary>
    internal sealed class WorldContentPlan
    {
        public bool IsValid { get; }

        /// <summary>Ordered files to apply (VALID only; empty on INVALID).</summary>
        public IReadOnlyList<PlannedContentFile> Files { get; }

        /// <summary>All problems found (INVALID only; empty on VALID).</summary>
        public IReadOnlyList<string> Diagnostics { get; }

        // unit name -> the units that directly depend on it (reverse of DependsOn).
        private readonly Dictionary<string, List<string>> _dependents;

        private WorldContentPlan(bool isValid, IReadOnlyList<PlannedContentFile> files, IReadOnlyList<string> diagnostics, Dictionary<string, List<string>> dependents)
        {
            IsValid = isValid;
            Files = files ?? Array.Empty<PlannedContentFile>();
            Diagnostics = diagnostics ?? Array.Empty<string>();
            _dependents = dependents ?? new Dictionary<string, List<string>>();
        }

        internal static WorldContentPlan Valid(IReadOnlyList<PlannedContentFile> files, Dictionary<string, List<string>> dependents)
            => new WorldContentPlan(true, files, Array.Empty<string>(), dependents);

        internal static WorldContentPlan Invalid(IReadOnlyList<string> diagnostics)
            => new WorldContentPlan(false, Array.Empty<PlannedContentFile>(), diagnostics, null);

        /// <summary>
        /// Given a set of failed unit names, returns every unit that transitively depends on any of them
        /// (the failed units themselves are NOT included). Lets the applier skip the fallout of a failed
        /// file without re-walking the graph. Unknown/unit-less names are simply ignored.
        /// </summary>
        public HashSet<string> GetTransitiveDependents(IEnumerable<string> failedUnits)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (failedUnits == null)
                return result;

            var queue = new Queue<string>();
            foreach (var f in failedUnits)
                if (f != null)
                    queue.Enqueue(f);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!_dependents.TryGetValue(cur, out var deps))
                    continue;

                foreach (var d in deps)
                {
                    if (result.Add(d))
                        queue.Enqueue(d);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Turns a flat set of discovered content files into a deterministic, dependency-correct application
    /// plan. No DB dependency; pure function of its inputs. See Docs/Deployment.md for the header grammar
    /// (this mirrors tools/apply-content.sh's -- @unit / -- @depends-on parsing).
    /// </summary>
    internal static class WorldContentPlanner
    {
        private static readonly string[] ValidPhaseNames = { "weenies", "realms", "placements", "quests", "patches" };

        // Matches "-- @field: value" (leading whitespace + flexible spacing around the dashes/colon),
        // case-insensitive on the field name. Mirrors apply-content.sh's get_header grep.
        private static Regex HeaderFieldRegex(string field)
            => new Regex($@"^\s*--\s*@{Regex.Escape(field)}:\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>Reads the first "-- @field:" line's value from a header block, trailing whitespace trimmed. Null if absent.</summary>
        private static string ReadHeaderField(string headerText, string field)
        {
            if (string.IsNullOrEmpty(headerText))
                return null;

            var m = HeaderFieldRegex(field).Match(headerText);
            if (!m.Success)
                return null;

            return m.Groups[1].Value.TrimEnd();
        }

        /// <summary>
        /// apply-content.sh strips a trailing inline SQL comment from @depends-on lines
        /// ("-- @depends-on: realms/x   -- realm 1 owned by x"). Cut at the first "--".
        /// </summary>
        private static string StripInlineComment(string value)
        {
            if (value == null)
                return null;

            var idx = value.IndexOf("--", StringComparison.Ordinal);
            if (idx >= 0)
                value = value.Substring(0, idx);

            return value.TrimEnd();
        }

        /// <summary>
        /// True if a discovered content file must never be auto-applied to the world database.
        /// Content/preview/ holds render and sign-off artifacts a human reviews before anything ships:
        /// draft weenies, palette experiments, throwaway wcids. It is deliberately NOT world content, so
        /// nothing under a preview/ folder may reach ace_world just because a server booted.
        /// This is enforced at DISCOVERY (the file never becomes a planner input) rather than by phase,
        /// because ContentPhase.Unclassified is still applied - it merely sorts last.
        /// Matches a whole path SEGMENT only, case-insensitively: "preview/x.sql" and "a/b/preview/c.sql"
        /// are excluded; "previewthing.sql" and "previews/x.sql" are not.
        /// Pure/filesystem-free; takes the repo-relative key produced by ComputeContentRelativeKey.
        /// </summary>
        internal static bool IsExcludedFromAutoApply(string relativeKey)
        {
            if (string.IsNullOrEmpty(relativeKey))
                return false;

            var segments = relativeKey.Replace('\\', '/').Split('/');

            foreach (var segment in segments)
            {
                if (string.Equals(segment, "preview", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>Classifies a repo-relative path (forward slashes) into its folder-derived phase.</summary>
        internal static ContentPhase ClassifyByPath(string relativePath)
        {
            var p = (relativePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();

            bool Starts(string prefix) => p.StartsWith(prefix, StringComparison.Ordinal);

            // weenies: sql/weenies/. preview/ is NOT a content location at all -- it is excluded from
            // auto-apply entirely (see IsExcludedFromAutoApply), never classified into a phase.
            if (Starts("sql/weenies/"))
                return ContentPhase.Weenies;

            if (Starts("realms/") || Starts("dungeons/") || Starts("outdoor/"))
                return ContentPhase.Realms;

            if (Starts("placements/"))
                return ContentPhase.Placements;

            if (Starts("sql/quests/"))
                return ContentPhase.Quests;

            if (Starts("sql/patches/") || Starts("migrations/"))
                return ContentPhase.Patches;

            return ContentPhase.Unclassified;
        }

        private static bool TryPhaseFromName(string name, out ContentPhase phase)
        {
            switch (name)
            {
                case "weenies": phase = ContentPhase.Weenies; return true;
                case "realms": phase = ContentPhase.Realms; return true;
                case "placements": phase = ContentPhase.Placements; return true;
                case "quests": phase = ContentPhase.Quests; return true;
                case "patches": phase = ContentPhase.Patches; return true;
                default: phase = ContentPhase.Unclassified; return false;
            }
        }

        // Working record while parsing, before validation resolves it into a PlannedContentFile.
        private sealed class ParsedFile
        {
            public ContentFileInput Input;
            public ContentPhase Phase;
            public string Unit;
            public List<string> Deps = new List<string>();
        }

        public static WorldContentPlan CreatePlan(IEnumerable<ContentFileInput> files)
        {
            var diagnostics = new List<string>();
            var parsed = new List<ParsedFile>();

            // --- parse + phase classification ---
            foreach (var input in files ?? Enumerable.Empty<ContentFileInput>())
            {
                var pf = new ParsedFile { Input = input };

                // @phase override (unknown = hard error).
                var phaseOverride = ReadHeaderField(input.HeaderText, "phase");
                if (phaseOverride != null)
                {
                    var normalized = phaseOverride.Trim().ToLowerInvariant();
                    if (TryPhaseFromName(normalized, out var overriddenPhase))
                        pf.Phase = overriddenPhase;
                    else
                    {
                        diagnostics.Add($"Unknown @phase value '{phaseOverride.Trim()}' in {input.RelativePath} (valid: {string.Join(", ", ValidPhaseNames)}).");
                        pf.Phase = ClassifyByPath(input.RelativePath); // best-effort so later checks still run
                    }
                }
                else
                {
                    pf.Phase = ClassifyByPath(input.RelativePath);
                }

                pf.Unit = ReadHeaderField(input.HeaderText, "unit")?.Trim();
                if (string.IsNullOrEmpty(pf.Unit))
                    pf.Unit = null;

                var depsRaw = StripInlineComment(ReadHeaderField(input.HeaderText, "depends-on"));
                if (!string.IsNullOrWhiteSpace(depsRaw) && depsRaw.Trim().ToLowerInvariant() != "none")
                {
                    foreach (var token in depsRaw.Split(','))
                    {
                        var dep = token.Trim();
                        if (dep.Length > 0)
                            pf.Deps.Add(dep);
                    }
                }

                parsed.Add(pf);
            }

            // --- unit name map + duplicate detection ---
            var unitToFile = new Dictionary<string, ParsedFile>(StringComparer.Ordinal);
            foreach (var pf in parsed)
            {
                if (pf.Unit == null)
                    continue;

                if (unitToFile.TryGetValue(pf.Unit, out var existing))
                    diagnostics.Add($"Duplicate @unit name '{pf.Unit}' defined in both {existing.Input.RelativePath} and {pf.Input.RelativePath}.");
                else
                    unitToFile[pf.Unit] = pf;
            }

            // --- dependency resolution: unknown deps + phase inversion ---
            foreach (var pf in parsed)
            {
                foreach (var dep in pf.Deps)
                {
                    if (!unitToFile.TryGetValue(dep, out var depFile))
                    {
                        diagnostics.Add($"Unknown dependency '{dep}' referenced by {pf.Input.RelativePath} (no file declares that @unit).");
                        continue;
                    }

                    // Phase inversion: the dependency is applied in a LATER phase than its dependent.
                    if ((int)depFile.Phase > (int)pf.Phase)
                    {
                        diagnostics.Add(
                            $"Phase inversion: {pf.Input.RelativePath} (phase {pf.Phase.ToString().ToLowerInvariant()}) depends on '{dep}' in " +
                            $"{depFile.Input.RelativePath} (phase {depFile.Phase.ToString().ToLowerInvariant()}), which is applied later.");
                    }
                }
            }

            // --- cycle detection (over resolvable edges), naming the cycle path ---
            var cyclePath = FindCycle(parsed, unitToFile);
            if (cyclePath != null)
                diagnostics.Add($"Dependency cycle: {string.Join(" -> ", cyclePath)}.");

            if (diagnostics.Count > 0)
                return WorldContentPlan.Invalid(diagnostics);

            // --- deterministic topological order: Kahn with a (phase, relativePath ordinal) priority key ---
            var ordered = KahnOrder(parsed, unitToFile);

            // reverse edges for the transitive-dependent helper (unit -> units directly depending on it)
            var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pf in parsed)
            {
                if (pf.Unit == null)
                    continue;

                foreach (var dep in pf.Deps)
                {
                    if (!dependents.TryGetValue(dep, out var list))
                        dependents[dep] = list = new List<string>();
                    list.Add(pf.Unit);
                }
            }

            var plannedFiles = ordered
                .Select(pf => new PlannedContentFile(pf.Input.AbsolutePath, pf.Input.RelativePath, pf.Phase, pf.Unit, pf.Deps.ToArray()))
                .ToList();

            return WorldContentPlan.Valid(plannedFiles, dependents);
        }

        private static string SortKey(ParsedFile pf)
            => ((int)pf.Phase).ToString("D2") + " " + pf.Input.RelativePath;

        private static List<ParsedFile> KahnOrder(List<ParsedFile> parsed, Dictionary<string, ParsedFile> unitToFile)
        {
            // in-degree = number of resolvable deps; edges point dependency -> dependent.
            var inDegree = new Dictionary<ParsedFile, int>();
            var outEdges = new Dictionary<ParsedFile, List<ParsedFile>>();
            foreach (var pf in parsed)
            {
                inDegree[pf] = 0;
                outEdges[pf] = new List<ParsedFile>();
            }

            foreach (var pf in parsed)
            {
                foreach (var dep in pf.Deps)
                {
                    if (unitToFile.TryGetValue(dep, out var depFile))
                    {
                        outEdges[depFile].Add(pf);
                        inDegree[pf]++;
                    }
                }
            }

            // Priority "queue": ready set ordered by (phase, relativePath) with StringComparer.Ordinal.
            var ready = new SortedSet<ParsedFile>(Comparer<ParsedFile>.Create(
                (a, b) => string.CompareOrdinal(SortKey(a), SortKey(b))));

            foreach (var pf in parsed)
                if (inDegree[pf] == 0)
                    ready.Add(pf);

            var ordered = new List<ParsedFile>(parsed.Count);
            while (ready.Count > 0)
            {
                var next = ready.Min;
                ready.Remove(next);
                ordered.Add(next);

                foreach (var dependent in outEdges[next])
                {
                    if (--inDegree[dependent] == 0)
                        ready.Add(dependent);
                }
            }

            // Cycles are caught earlier; if any node was left out (defensive), append deterministically.
            if (ordered.Count != parsed.Count)
            {
                foreach (var pf in parsed.Where(p => !ordered.Contains(p)).OrderBy(SortKey, StringComparer.Ordinal))
                    ordered.Add(pf);
            }

            return ordered;
        }

        /// <summary>DFS cycle finder over unit edges. Returns the cycle as a list of unit names (closing on the start), or null.</summary>
        private static List<string> FindCycle(List<ParsedFile> parsed, Dictionary<string, ParsedFile> unitToFile)
        {
            // 0 = unvisited, 1 = on stack, 2 = done
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new List<string>();

            foreach (var kvp in unitToFile)
                state[kvp.Key] = 0;

            List<string> Visit(string unit)
            {
                state[unit] = 1;
                stack.Add(unit);

                var pf = unitToFile[unit];
                foreach (var dep in pf.Deps)
                {
                    if (!unitToFile.ContainsKey(dep))
                        continue; // unknown dep reported separately

                    var s = state[dep];
                    if (s == 1)
                    {
                        // back-edge: build the cycle from dep .. unit .. dep
                        var start = stack.IndexOf(dep);
                        var cycle = stack.GetRange(start, stack.Count - start);
                        cycle.Add(dep);
                        return cycle;
                    }
                    if (s == 0)
                    {
                        var found = Visit(dep);
                        if (found != null)
                            return found;
                    }
                }

                stack.RemoveAt(stack.Count - 1);
                state[unit] = 2;
                return null;
            }

            // deterministic start order
            foreach (var unit in unitToFile.Keys.OrderBy(u => u, StringComparer.Ordinal))
            {
                if (state[unit] == 0)
                {
                    stack.Clear();
                    var cycle = Visit(unit);
                    if (cycle != null)
                        return cycle;
                }
            }

            return null;
        }
    }
}
