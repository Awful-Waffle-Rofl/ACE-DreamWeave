using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

using log4net;

using ACE.Server.HazardMechanics;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.SpeedCourses
{
    /// <summary>The Threads-side facts a course is cross-checked against, injectable so the checks are unit-testable.</summary>
    public interface ISpeedCourseLookup
    {
        bool DungeonExists(string dungeonId);
        bool PuzzleSiteExists(string dungeonId, string siteId);
        bool FamilyExists(string family);
    }

    /// <summary>The live lookup, over the Threads store's rows and sites and the species tables.</summary>
    public sealed class ThreadStoreCourseLookup : ISpeedCourseLookup
    {
        private readonly ThreadDungeonStore threads;
        private readonly IReadOnlyDictionary<string, SpeciesTableDef> species;

        public ThreadStoreCourseLookup(ThreadDungeonStore threads, IReadOnlyDictionary<string, SpeciesTableDef> species)
        {
            this.threads = threads;
            this.species = species;
        }

        public bool DungeonExists(string dungeonId)
            => threads != null && dungeonId != null && threads.Dungeons.ContainsKey(dungeonId);

        public bool PuzzleSiteExists(string dungeonId, string siteId)
            => threads != null && threads.GetPuzzleSites(dungeonId).Any(s => string.Equals(s.Id, siteId, StringComparison.Ordinal));

        public bool FamilyExists(string family)
            => species != null && family != null && species.ContainsKey(family);
    }

    /// <summary>
    /// Loads Content/dungeons/dynamic/speed-courses.json (the Threads data folder). Never throws on content: a
    /// bad file or a bad course costs the file or the course and adds a line to <see cref="Diagnostics"/>.
    /// </summary>
    public sealed class SpeedCourseStore
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public const string FileName = "speed-courses.json";
        public const int SupportedVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private readonly Dictionary<int, SpeedCourseDef> bySeason;

        public static readonly SpeedCourseStore Empty = new SpeedCourseStore(new Dictionary<int, SpeedCourseDef>(), new List<string>());

        private SpeedCourseStore(Dictionary<int, SpeedCourseDef> bySeason, List<string> diagnostics)
        {
            this.bySeason = bySeason;
            Diagnostics = diagnostics;
        }

        public IReadOnlyCollection<SpeedCourseDef> Courses => bySeason.Values;

        public IReadOnlyList<string> Diagnostics { get; }

        /// <summary>The course for a season, or null (a season with no course is a no-op for the controller).</summary>
        public SpeedCourseDef GetCourse(int seasonId) => bySeason.TryGetValue(seasonId, out var c) ? c : null;

        /// <summary>
        /// Parses the file text. <paramref name="lookup"/> null skips the cross-checks against the Threads data.
        /// Empty or whitespace text is "no courses" with no diagnostic.
        /// </summary>
        public static SpeedCourseStore Parse(string json, ISpeedCourseLookup lookup)
        {
            var diagnostics = new List<string>();
            var courses = new Dictionary<int, SpeedCourseDef>();

            if (string.IsNullOrWhiteSpace(json))
                return new SpeedCourseStore(courses, diagnostics);

            SpeedCoursesFileDef file;

            try
            {
                file = JsonSerializer.Deserialize<SpeedCoursesFileDef>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                diagnostics.Add($"{FileName}: could not parse - {ex.Message}");
                return new SpeedCourseStore(courses, diagnostics);
            }

            if (file == null)
            {
                diagnostics.Add($"{FileName}: empty document");
                return new SpeedCourseStore(courses, diagnostics);
            }

            if (file.Version != SupportedVersion)
            {
                diagnostics.Add($"{FileName}: version {file.Version} is not supported (expected {SupportedVersion}); no courses loaded");
                return new SpeedCourseStore(courses, diagnostics);
            }

            var index = 0;

            foreach (var element in file.Courses ?? new List<JsonElement>())
            {
                index++;
                SpeedCourseDef course;

                try
                {
                    course = element.Deserialize<SpeedCourseDef>(JsonOptions);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{FileName}: course #{index} could not be read - {ex.Message}; course dropped");
                    continue;
                }

                if (course == null)
                {
                    diagnostics.Add($"{FileName}: course #{index} is null; course dropped");
                    continue;
                }

                var problems = Validate(course, lookup);

                if (courses.ContainsKey(course.SeasonId))
                    problems.Add($"duplicate seasonId {course.SeasonId}; the first course keeps the season");

                if (problems.Count > 0)
                {
                    foreach (var p in problems)
                        diagnostics.Add($"{FileName}: season {course.SeasonId} (course #{index}): {p}");

                    // Any problem drops the course: a half-valid course is a puzzle that cannot be finished.
                    continue;
                }

                courses[course.SeasonId] = course;
            }

            return new SpeedCourseStore(courses, diagnostics);
        }

        /// <summary>Every problem with a course, empty when it is valid. Pure given the lookup; never throws.</summary>
        public static List<string> Validate(SpeedCourseDef c, ISpeedCourseLookup lookup)
        {
            var problems = new List<string>();

            if (c.SeasonId <= 0)
                problems.Add("seasonId is missing or not positive");

            if (string.IsNullOrWhiteSpace(c.DungeonId))
                problems.Add("dungeonId is required");

            if (string.IsNullOrWhiteSpace(c.Family))
                problems.Add("family is required");

            if (string.IsNullOrWhiteSpace(c.SealKey))
                problems.Add("sealKey is required");

            if (c.Tier < 1 || c.Tier > DungeonGemSpec.MaxTier)
                problems.Add($"tier {c.Tier} must be 1..{DungeonGemSpec.MaxTier}");

            if (c.LevelFloor < 0 || c.LevelFloor > DungeonGemSpec.MaxRunLevel)
                problems.Add($"levelFloor {c.LevelFloor} must be 0..{DungeonGemSpec.MaxRunLevel}");

            if (c.FixedLevel < 0 || c.FixedLevel > DungeonGemSpec.MaxRunLevel)
                problems.Add($"fixedLevel {c.FixedLevel} must be 0..{DungeonGemSpec.MaxRunLevel}");

            // ---- tokens and the Seal ----
            var tokens = c.Tokens ?? new Dictionary<string, double>();

            if (tokens.Count == 0)
                problems.Add("tokens table is empty");

            foreach (var kv in tokens)
                if (string.IsNullOrWhiteSpace(kv.Key) || !(kv.Value > 0) || double.IsInfinity(kv.Value))
                    problems.Add($"token '{kv.Key}' needs a positive finite weight (got {kv.Value})");

            if (!(c.SealRequired > 0))
                problems.Add($"sealRequired {c.SealRequired} must be positive");
            else if (tokens.Values.Where(w => w > 0 && !double.IsInfinity(w)).Sum() < c.SealRequired)
                problems.Add($"sealRequired {c.SealRequired} is not satisfiable: the whole token table weighs {tokens.Values.Where(w => w > 0 && !double.IsInfinity(w)).Sum()}");

            void CheckToken(string token, string where)
            {
                if (token != null && !tokens.ContainsKey(token))
                    problems.Add($"{where}: token '{token}' is not in the token table");
            }

            // ---- puzzles ----
            var puzzles = c.Puzzles ?? new List<SpeedPuzzleDef>();
            var puzzleSlots = puzzles.Select(p => p?.Slot).OrderBy(s => s, StringComparer.Ordinal).ToList();

            if (!puzzleSlots.SequenceEqual(SpeedPuzzleSlots.All.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add($"puzzles must be exactly the slots P1, P2, P3 once each (got [{string.Join(", ", puzzleSlots)}])");

            foreach (var p in puzzles.Where(p => p != null))
            {
                if (string.IsNullOrWhiteSpace(p.SiteId))
                    problems.Add($"puzzle {p.Slot}: siteId is required");
                else if (lookup != null && !string.IsNullOrWhiteSpace(c.DungeonId) && !lookup.PuzzleSiteExists(c.DungeonId, p.SiteId))
                    problems.Add($"puzzle {p.Slot}: siteId '{p.SiteId}' is not a puzzle site of dungeon '{c.DungeonId}'");

                if (p.Slot == SpeedPuzzleSlots.P2 && p.Token != null)
                    problems.Add("puzzle P2 must not carry a token (it contributes nothing)");

                if (p.Slot != SpeedPuzzleSlots.P2 && string.IsNullOrWhiteSpace(p.Token))
                    problems.Add($"puzzle {p.Slot}: token is required");

                CheckToken(p.Token, $"puzzle {p.Slot}");

                if (p.Rounds < 1)
                    problems.Add($"puzzle {p.Slot}: rounds {p.Rounds} must be at least 1");

                if (p.LockoutSeconds < 0 || double.IsNaN(p.LockoutSeconds))
                    problems.Add($"puzzle {p.Slot}: lockoutSeconds {p.LockoutSeconds} must not be negative");
            }

            var siteIds = puzzles.Where(p => !string.IsNullOrWhiteSpace(p?.SiteId)).Select(p => p.SiteId).ToList();

            if (siteIds.Count != siteIds.Distinct(StringComparer.Ordinal).Count())
                problems.Add("two puzzles share a siteId");

            // ---- named ----
            var named = c.Named ?? new List<SpeedNamedDef>();
            var namedSlots = named.Select(n => n?.Slot).OrderBy(s => s, StringComparer.Ordinal).ToList();

            if (!namedSlots.SequenceEqual(SpeedNamedSlots.All.OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add($"named must be exactly the slots K1, K2, BOSS once each (got [{string.Join(", ", namedSlots)}])");

            foreach (var n in named.Where(n => n != null))
            {
                if (n.Position == null)
                    problems.Add($"named {n.Slot}: position is required");

                CheckToken(n.Token, $"named {n.Slot}");

                if (n.HoldUntilSolved != null)
                {
                    if (n.Slot != SpeedNamedSlots.K1 && n.Slot != SpeedNamedSlots.K2)
                        problems.Add($"named {n.Slot}: holdUntilSolved is for K1/K2 only (the boss is held for the Seal)");
                    else if (!puzzles.Any(p => p != null && string.Equals(p.Slot, n.HoldUntilSolved, StringComparison.Ordinal)))
                        problems.Add($"named {n.Slot}: holdUntilSolved '{n.HoldUntilSolved}' is not a puzzle slot of this course");
                }
            }

            // ---- runner messages ----
            foreach (var kv in c.Labels ?? new Dictionary<string, string>())
                if (!tokens.ContainsKey(kv.Key ?? ""))
                    problems.Add($"labels: '{kv.Key}' is not in the token table");

            foreach (var g in c.ProgressGroups ?? new List<SpeedProgressGroupDef>())
            {
                if (g == null)
                {
                    problems.Add("a progress group is null");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(g.Name))
                    problems.Add("a progress group has no name");

                if (g.Tokens == null || g.Tokens.Count == 0)
                    problems.Add($"progress group '{g.Name}' has no tokens");
                else
                    foreach (var t in g.Tokens)
                        CheckToken(t ?? "", $"progress group '{g.Name}'");

                if (g.Of < 1 || (g.Tokens != null && g.Of > g.Tokens.Count))
                    problems.Add($"progress group '{g.Name}': of {g.Of} must be 1..{g.Tokens?.Count ?? 0}");
            }

            // ---- hazards ----
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var h in c.Hazards ?? new List<SpeedHazardDef>())
            {
                if (h == null)
                {
                    problems.Add("a hazard entry is null");
                    continue;
                }

                var who = $"hazard '{h.Id}'";

                if (string.IsNullOrWhiteSpace(h.Id))
                    problems.Add("a hazard has no id");
                else if (!ids.Add(h.Id))
                    problems.Add($"{who}: duplicate id");

                if (!HazardMechanicRegistry.TryCreate(h.Kind, out var mechanic) || HazardMechanicRegistry.IsTool(h.Kind))
                    problems.Add($"{who}: unknown hazard kind '{h.Kind}'");
                else if (!HazardManager.TryResolvePattern(mechanic, h.Pattern, out _, out var patternError))
                    problems.Add($"{who}: unknown pattern '{h.Pattern}' for kind '{h.Kind}' ({patternError})");

                if (h.Region == null)
                    problems.Add($"{who}: region is required");
                else
                    ValidateRegion(h.Region, who, problems);

                if (h.TriggerRegion != null)
                    ValidateRegion(h.TriggerRegion, $"{who} triggerRegion", problems);

                var t = h.Trigger;

                if (t == null || !SpeedHazardTriggers.All.Contains(t.Type ?? "", StringComparer.Ordinal))
                    problems.Add($"{who}: unknown trigger type '{t?.Type}'");
                else
                {
                    if (t.Type == SpeedHazardTriggers.OnNamedDeadAndInRegion && !SpeedNamedSlots.All.Contains(t.Slot ?? "", StringComparer.Ordinal))
                        problems.Add($"{who}: trigger {t.Type} needs slot K1, K2 or BOSS (got '{t.Slot}')");

                    if (t.Type == SpeedHazardTriggers.BossAliveInterval && !(t.Seconds > 0))
                        problems.Add($"{who}: trigger {t.Type} needs seconds > 0");

                    if (t.Type == SpeedHazardTriggers.BossHealthBelow && !(t.Fraction > 0 && t.Fraction <= 1))
                        problems.Add($"{who}: trigger {t.Type} needs fraction in (0, 1]");
                }

                CheckToken(h.Token, who);

                if (h.Origin != null && !SpeedHazardOrigins.All.Contains(h.Origin, StringComparer.Ordinal))
                    problems.Add($"{who}: origin '{h.Origin}' must be region or boss");

                if (h.RetrySeconds < 0 || double.IsNaN(h.RetrySeconds))
                    problems.Add($"{who}: retrySeconds {h.RetrySeconds} must not be negative");

                if (h.PctAuthored < 0 || h.PctMin < 0 || h.PctMax < h.PctMin)
                    problems.Add($"{who}: pctAuthored/pctMin/pctMax must satisfy 0 <= min <= max (got {h.PctAuthored}, {h.PctMin}, {h.PctMax})");
            }

            // ---- boss phases ----
            problems.AddRange(SpeedBossPhaseRules.Validate(c));

            if (c.SealLock != null)
            {
                if (c.SealLock.Region == null)
                    problems.Add("sealLock: region is required");
                else
                    ValidateRegion(c.SealLock.Region, "sealLock", problems);
            }

            foreach (var n in named.Where(n => n != null))
                if (!(n.Scale > 0f && n.Scale <= 10f))
                    problems.Add($"named {n.Slot}: scale {n.Scale} must be in (0, 10]");

            // ---- optional gates ----
            foreach (var key in c.OptionalGateKeys ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(key))
                    problems.Add("optionalGateKeys: a key is blank");
                else if (string.Equals(key, c.SealKey, StringComparison.Ordinal))
                    problems.Add($"optionalGateKeys: '{key}' is the sealKey, which is never optional");
            }

            // ---- guardians ----
            var guardianIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var g in c.Guardians ?? new List<SpeedGuardianDef>())
            {
                if (g == null)
                {
                    problems.Add("a guardian entry is null");
                    continue;
                }

                var who = $"guardian '{g.Id}'";

                if (string.IsNullOrWhiteSpace(g.Id))
                    problems.Add("a guardian has no id");
                else if (!guardianIds.Add(g.Id))
                    problems.Add($"{who}: duplicate id");

                if (g.DoorKeys == null || g.DoorKeys.Count == 0)
                    problems.Add($"{who}: doorKeys is required (at least one door)");
                else
                {
                    var keys = new HashSet<string>(StringComparer.Ordinal);

                    foreach (var key in g.DoorKeys)
                    {
                        if (string.IsNullOrWhiteSpace(key))
                            problems.Add($"{who}: a doorKeys entry is blank");
                        else if (string.Equals(key, c.SealKey, StringComparison.Ordinal))
                            problems.Add($"{who}: doorKey '{key}' must not be the sealKey (a guardian opens its own doors, never the Seal)");
                        else if (!keys.Add(key))
                            problems.Add($"{who}: doorKey '{key}' is listed twice");
                    }
                }

                if (g.Position == null)
                    problems.Add($"{who}: position is required");

                if (!(g.Scale > 0f && g.Scale <= 10f))
                    problems.Add($"{who}: scale {g.Scale} must be in (0, 10]");
            }

            // ---- Threads cross-checks ----
            if (lookup != null)
            {
                if (!string.IsNullOrWhiteSpace(c.DungeonId) && !lookup.DungeonExists(c.DungeonId))
                    problems.Add($"dungeonId '{c.DungeonId}' is not a Threads index row");

                if (!string.IsNullOrWhiteSpace(c.Family) && !lookup.FamilyExists(c.Family))
                    problems.Add($"family '{c.Family}' is not a species table");
            }

            return problems;
        }

        private static void ValidateRegion(SpeedHazardRegionDef r, string who, List<string> problems)
        {
            var circle = string.Equals(r.Shape, "circle", StringComparison.OrdinalIgnoreCase);

            if (!circle && !string.Equals(r.Shape, "rect", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{who}: region shape '{r.Shape}' must be rect or circle");
            else if (circle && !(r.Radius > 0))
                problems.Add($"{who}: circle region needs radius > 0");
            else if (!circle && (r.MinX == r.MaxX || r.MinY == r.MaxY))
                problems.Add($"{who}: rect region has zero width or height");

            if (r.CeilingZ.HasValue && r.CeilingZ.Value <= r.GroundZ)
                problems.Add($"{who}: ceilingZ {r.CeilingZ} must be above groundZ {r.GroundZ}");

            if (r.Exclusions == null)
                return;

            for (var i = 0; i < r.Exclusions.Count; i++)
            {
                var e = r.Exclusions[i];

                if (e == null)
                    problems.Add($"{who}: exclusion {i} is null");
                else if (!(e.Radius > 0) || float.IsInfinity(e.Radius) || float.IsNaN(e.CenterX) || float.IsNaN(e.CenterY))
                    problems.Add($"{who}: exclusion {i} needs a finite centre and radius > 0 (got ({e.CenterX}, {e.CenterY}) r {e.Radius})");
            }
        }

        /// <summary>Reads speed-courses.json from <paramref name="folder"/>. Missing file: an empty store, no diagnostic.</summary>
        public static SpeedCourseStore Load(string folder, ISpeedCourseLookup lookup)
        {
            try
            {
                var path = Path.Combine(folder, FileName);

                if (!File.Exists(path))
                    return Empty;

                return Parse(File.ReadAllText(path), lookup);
            }
            catch (Exception ex)
            {
                return new SpeedCourseStore(new Dictionary<int, SpeedCourseDef>(), new List<string> { $"{FileName}: could not read - {ex.Message}" });
            }
        }

        /// <summary>Loads from the Threads data folder (<see cref="ThreadDungeonStore.ResolveDataFolder"/>), cross-checked against the live Threads store and species tables.</summary>
        public static SpeedCourseStore LoadFromServerConfig(ThreadDungeonStore threads, IReadOnlyDictionary<string, SpeciesTableDef> species)
        {
            var folder = ThreadDungeonStore.ResolveDataFolder(out _);

            if (folder == null)
                return Empty;

            var store = Load(folder, new ThreadStoreCourseLookup(threads, species));

            foreach (var d in store.Diagnostics)
                log.Warn($"[SPEEDCOURSE] {d}");

            log.Info($"[SPEEDCOURSE] loaded {store.Courses.Count} speed courses from {folder}");
            return store;
        }
    }
}