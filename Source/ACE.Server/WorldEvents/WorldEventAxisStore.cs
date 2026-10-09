using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using ACE.Common;
using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;

using log4net;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// Immutable, once-built store of the six World Events composition axes (TECH-DESIGN 2.3, 2.12, 5.4).
    /// Never throws on bad content - a malformed file, a bad id, or a dangling reference produces a
    /// human-readable diagnostic and the offending entry is dropped; loading continues.
    /// </summary>
    public sealed class WorldEventAxisStore
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly Regex IdPattern = new Regex("^[a-z0-9_]+$", RegexOptions.Compiled);

        public IReadOnlyDictionary<string, SourceThemeDef> Sources { get; }
        public IReadOnlyDictionary<string, FamilyDef> Families { get; }
        public IReadOnlyDictionary<string, BossDef> Bosses { get; }
        public IReadOnlyDictionary<string, GoalDef> Goals { get; }
        public IReadOnlyDictionary<string, RewardDef> Rewards { get; }
        public IReadOnlyDictionary<string, AnchorDef> Anchors { get; }

        /// <summary>Species reference tables from Content/events/axes/species/*.json, keyed by id
        /// (TECH-DESIGN C15, 2026-08-16). Each valid table also registers a matching entry in
        /// <see cref="Families"/> unless families.json already declares the same id.</summary>
        public IReadOnlyDictionary<string, SpeciesTableDef> SpeciesTables { get; }

        /// <summary>
        /// Content DEFECTS: something was dropped, ignored, or fell back to a default because the author
        /// got it wrong. Logged at WARN, because every entry is work for somebody.
        /// </summary>
        public IReadOnlyList<string> Diagnostics { get; }

        /// <summary>
        /// Working-as-intended observations: a documented rule fired and the outcome is what the author
        /// asked for. Logged at DEBUG, and kept only as an audit trail for someone reading back a boot.
        ///
        /// The distinction is not stylistic. A diagnostic that fires on correct content is noise that
        /// trains readers to skim the whole channel, and this channel is the only signal that an axis file
        /// silently lost an entry. The families.json override is the standing case: giving a family a
        /// custom event name REQUIRES declaring it in both families.json and its species table, so every
        /// intentional override necessarily collided and warned. Eight shipped families did exactly that,
        /// so eight WARNs fired on every single boot with nothing to fix.
        ///
        /// Put an entry here only when no authoring change could remove it. "You declared both X and Y,
        /// X wins" stays a diagnostic: the author can, and should, delete Y.
        /// </summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>
        /// The absolute folder this store was loaded from, or null when Parse was called directly
        /// (e.g. from a test) with no folder context.
        /// </summary>
        public string ResolvedFolder { get; }

        /// <summary>
        /// True when Sources, Goals or Rewards is empty - an event cannot be composed without all three.
        /// </summary>
        public bool IsEmpty => Sources.Count == 0 || Goals.Count == 0 || Rewards.Count == 0;

        public static readonly WorldEventAxisStore Empty = Parse(null, null, null, null, null, null, null);

        private WorldEventAxisStore(
            Dictionary<string, SourceThemeDef> sources,
            Dictionary<string, FamilyDef> families,
            Dictionary<string, BossDef> bosses,
            Dictionary<string, GoalDef> goals,
            Dictionary<string, RewardDef> rewards,
            Dictionary<string, AnchorDef> anchors,
            Dictionary<string, SpeciesTableDef> speciesTables,
            List<string> diagnostics,
            List<string> notes,
            string resolvedFolder)
        {
            Sources = sources;
            Families = families;
            Bosses = bosses;
            Goals = goals;
            Rewards = rewards;
            Anchors = anchors;
            SpeciesTables = speciesTables;
            Diagnostics = diagnostics;
            Notes = notes;
            ResolvedFolder = resolvedFolder;
        }

        /// <summary>
        /// Pure: parse the six axis files plus species/*.json from strings. Each axis file may be null
        /// (file absent). Used by tests and by Load.
        ///
        /// <paramref name="bossesJson"/> sits BEFORE <paramref name="resolvedFolder"/> deliberately: every
        /// existing call site passes five arguments and nothing else, so an optional sixth in this slot
        /// keeps them all compiling unchanged. <paramref name="speciesFiles"/> optionally feeds
        /// species/*.json content (fileName, json) pairs into the same pure path, so species-table parsing
        /// is testable with no disk (TECH-DESIGN C15, 2026-08-16).
        /// </summary>
        public static WorldEventAxisStore Parse(string sourcesJson, string familiesJson, string goalsJson,
            string rewardsJson, string anchorsJson, string bossesJson = null, string resolvedFolder = null,
            IEnumerable<(string fileName, string json)> speciesFiles = null)
        {
            var diagnostics = new List<string>();
            var notes = new List<string>();
            return BuildStore(sourcesJson, familiesJson, goalsJson, rewardsJson, anchorsJson, bossesJson,
                resolvedFolder, diagnostics, notes, speciesFiles);
        }

        /// <summary>
        /// Reads the six axis files from an absolute folder. A missing file is a diagnostic, never an
        /// exception.
        /// </summary>
        public static WorldEventAxisStore Load(string folder)
        {
            var diagnostics = new List<string>();
            var notes = new List<string>();

            string Read(string fileName)
            {
                var path = Path.Combine(folder, fileName);

                if (!File.Exists(path))
                {
                    diagnostics.Add($"{fileName}: file not found at {path}");
                    return null;
                }

                try
                {
                    return File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{fileName}: could not read - {ex.Message}");
                    return null;
                }
            }

            var sourcesJson = Read("sources.json");
            var familiesJson = Read("families.json");
            var goalsJson = Read("goals.json");
            var rewardsJson = Read("rewards.json");
            var anchorsJson = Read("anchors.json");
            var bossesJson = Read("bosses.json");
            var speciesFiles = ReadSpeciesFiles(folder, diagnostics);

            return BuildStore(sourcesJson, familiesJson, goalsJson, rewardsJson, anchorsJson, bossesJson,
                folder, diagnostics, notes, speciesFiles);
        }

        /// <summary>
        /// Reads Content/events/axes/species/*.json (TECH-DESIGN C15, 2026-08-16). A missing species folder
        /// is fine - zero tables, no diagnostic - since species tables are an additive, optional source on
        /// top of the weenie-scan catalog.
        /// </summary>
        private static List<(string fileName, string json)> ReadSpeciesFiles(string folder, List<string> diagnostics)
        {
            var result = new List<(string fileName, string json)>();
            var speciesDir = Path.Combine(folder, "species");

            if (!Directory.Exists(speciesDir))
                return result;

            foreach (var path in Directory.GetFiles(speciesDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                var fileName = Path.GetFileName(path);

                try
                {
                    result.Add((fileName, File.ReadAllText(path)));
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"species/{fileName}: could not read - {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Pure path resolution in the TECH-DESIGN 2.12 order, with an injected existence probe:
        /// (1) override folder if non-empty and it exists; (2) content_folder/events/axes if it exists
        /// (a leading-dot content folder is expanded against the current directory); (3) exe-adjacent
        /// Content/events/axes if it exists. Returns null when none exists. <paramref name="reason"/>
        /// names which rule won: "world_events_axis_folder", "content_folder", "exe-adjacent", or "none".
        /// </summary>
        public static string ResolveFolder(string overrideFolder, string contentFolder, string exeFolder,
            Func<string, bool> dirExists, out string reason)
        {
            var folder = ResolveFolder(overrideFolder, contentFolder, exeFolder, new[] { "events", "axes" }, dirExists, out reason);

            // keep the historical reason token for the World Events override rule
            if (reason == "override")
                reason = "world_events_axis_folder";

            return folder;
        }

        /// <summary>
        /// Generalised form: <paramref name="subPath"/> is the folder chain under the content root
        /// (e.g. ["events","axes"] or ["dungeons","dynamic"]). Rules and order are unchanged:
        /// (1) overrideFolder if it exists ("override"); (2) contentFolder/subPath, leading-dot content
        /// folder expanded against the current directory ("content_folder"); (3) exeFolder/Content/subPath
        /// ("exe-adjacent"); else null ("none").
        /// </summary>
        public static string ResolveFolder(string overrideFolder, string contentFolder, string exeFolder,
            string[] subPath, Func<string, bool> dirExists, out string reason)
        {
            if (!string.IsNullOrEmpty(overrideFolder) && dirExists(overrideFolder))
            {
                reason = "override";
                return overrideFolder;
            }

            if (!string.IsNullOrEmpty(contentFolder))
            {
                var expanded = contentFolder;

                if (expanded.StartsWith("."))
                {
                    var cwd = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
                    expanded = cwd + expanded;
                }

                var candidate = Path.Combine(new[] { expanded }.Concat(subPath).ToArray());

                if (dirExists(candidate))
                {
                    reason = "content_folder";
                    return candidate;
                }
            }

            if (!string.IsNullOrEmpty(exeFolder))
            {
                var candidate = Path.Combine(new[] { exeFolder, "Content" }.Concat(subPath).ToArray());

                if (dirExists(candidate))
                {
                    reason = "exe-adjacent";
                    return candidate;
                }
            }

            reason = "none";
            return null;
        }

        /// <summary>
        /// Server-side convenience: resolves the folder via <see cref="ResolveFolder"/> against
        /// PropertyManager's "world_events_axis_folder" / "content_folder" and the executing assembly's
        /// directory, logs which rule won, and returns Load(folder) - or Empty when nothing was found.
        /// </summary>
        public static WorldEventAxisStore LoadFromServerConfig()
        {
            var overrideFolder = PropertyManager.GetString("world_events_axis_folder").Item;
            var contentFolder = PropertyManager.GetString("content_folder").Item;
            var exeFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            var folder = ResolveFolder(overrideFolder, contentFolder, exeFolder, Directory.Exists, out var reason);

            if (folder == null)
            {
                log.Warn("[WORLDEVENT] no axis folder found; store is empty");
                return Empty;
            }

            log.Info($"[WORLDEVENT] axis folder resolved via {reason}: {Path.GetFullPath(folder)}");

            return Load(folder);
        }

        private static bool IsValidId(string id)
        {
            return !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);
        }

        private static bool TryParseEnum<TEnum>(string value, out TEnum result) where TEnum : struct
        {
            result = default;
            return !string.IsNullOrEmpty(value) && Enum.TryParse(value, true, out result) && Enum.IsDefined(typeof(TEnum), result);
        }

        private static WorldEventAxisStore BuildStore(string sourcesJson, string familiesJson, string goalsJson,
            string rewardsJson, string anchorsJson, string bossesJson, string resolvedFolder,
            List<string> diagnostics, List<string> notes,
            IEnumerable<(string fileName, string json)> speciesFiles = null)
        {
            var families = ParseFamilies(familiesJson, diagnostics);
            var goals = ParseGoals(goalsJson, diagnostics);
            var rewards = ParseRewards(rewardsJson, diagnostics);
            var anchors = ParseAnchors(anchorsJson, diagnostics);
            var sources = ParseSources(sourcesJson, diagnostics, goals);
            var bosses = ParseBosses(bossesJson, diagnostics);
            var speciesTables = ParseSpeciesTables(speciesFiles, diagnostics, notes, families);

            return new WorldEventAxisStore(sources, families, bosses, goals, rewards, anchors, speciesTables,
                diagnostics, notes, resolvedFolder);
        }

        /// <summary>
        /// bosses.json (BOSS-STANDARD.md section 5). The dictionary is always seeded with the two built-in
        /// entries first, so "none" and "family_champion" exist whether or not the file does, and a JSON
        /// entry can never shadow either of them. "auto" (WorldEventComposer.AutoBossId) is reserved too,
        /// but is never a real entry in this dictionary - WorldEventComposer resolves it to a concrete
        /// Named boss id before store.Bosses is ever looked up with it, so a reserved-id check on the
        /// incoming entries is enough to keep bosses.json from defining a boss called "auto".
        ///
        /// Two classes of defect, and they are treated differently on purpose:
        ///
        ///   * an ENTRY defect - one that makes the boss unusable at all (no id, a colliding id, no
        ///     displayName, wcid 0, a malformed familyId) - DROPS the entry;
        ///   * a DIAL defect - a bad perPlayer or cap - REPAIRS to the documented default and KEEPS the
        ///     boss, exactly as ValidateDifficulty does for the wave dials. The worst a bad dial can do is
        ///     turn health scaling off; it can never make the boss unplaceable.
        /// </summary>
        private static Dictionary<string, BossDef> ParseBosses(string json, List<string> diagnostics)
        {
            var result = new Dictionary<string, BossDef>
            {
                [BossDef.None.Id] = BossDef.None,
                [BossDef.FamilyChampion.Id] = BossDef.FamilyChampion
            };

            if (string.IsNullOrEmpty(json))
                return result;

            BossesFile file;

            try
            {
                file = JsonSerializer.Deserialize<BossesFile>(json, ConfigManager.SerializerOptions) ?? new BossesFile();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"bosses.json: malformed JSON - {ex.Message}");
                return result;
            }

            foreach (var boss in file.Bosses ?? new List<BossDef>())
            {
                if (!IsValidId(boss?.Id))
                {
                    diagnostics.Add($"bosses.json: entry with invalid or missing id '{boss?.Id}' dropped");
                    continue;
                }

                if (result.ContainsKey(boss.Id))
                {
                    // Also the collision guard for the two built-in ids, which are in the dictionary
                    // before the first entry is read.
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' collides with an existing boss id, dropped");
                    continue;
                }

                if (boss.Id == WorldEventComposer.AutoBossId)
                {
                    // "auto" is reserved by WorldEventComposer as a pseudo boss id (BOSS-STANDARD.md
                    // section 5) - it is resolved dynamically and never stored here, so it cannot be
                    // caught by the ContainsKey collision check above.
                    diagnostics.Add($"bosses.json: boss id '{boss.Id}' is reserved, dropped");
                    continue;
                }

                if (string.IsNullOrEmpty(boss.DisplayName))
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' missing displayName, dropped");
                    continue;
                }

                if (boss.NamedWcid == 0)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has a zero wcid, dropped");
                    continue;
                }

                // Empty is the shipped state and means "any family". Only a NON-empty familyId has to be a
                // well-formed id, because that is the value the composer compares against.
                if (!string.IsNullOrEmpty(boss.FamilyId) && !IdPattern.IsMatch(boss.FamilyId))
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has an invalid familyId '{boss.FamilyId}', dropped");
                    continue;
                }

                if (boss.FailLines == null)
                    boss.FailLines = new List<string>();

                if (!IsFinite(boss.PerPlayer) || boss.PerPlayer < 0)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has an invalid perPlayer, using the default");
                    boss.PerPlayer = BossDef.DefaultPerPlayer;
                }

                if (!IsFinite(boss.HealthCap) || boss.HealthCap < 1.0)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has a cap below 1.0, using the default");
                    boss.HealthCap = BossDef.DefaultHealthCap;
                }

                // The throughput dials (C16) repair exactly like the two above: a bad dial can only ever turn
                // throughput scaling off or hand it a default, never drop a boss.
                if (!IsFinite(boss.TargetKillSeconds) || boss.TargetKillSeconds <= 0)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has an invalid targetKillSeconds, using the default");
                    boss.TargetKillSeconds = BossDef.DefaultTargetKillSeconds;
                }

                if (!IsFinite(boss.ThroughputCalibration) || boss.ThroughputCalibration <= 0)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has an invalid throughputCalibration, using the default");
                    boss.ThroughputCalibration = BossDef.DefaultThroughputCalibration;
                }

                if (!IsFinite(boss.MinSampleSeconds) || boss.MinSampleSeconds < 0)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has an invalid minSampleSeconds, using the default");
                    boss.MinSampleSeconds = BossDef.DefaultMinSampleSeconds;
                }

                // The magnitude bound BossDef.BaseHealth documents, applied to the throughput ceiling: a
                // single ratchet raise is handed to Creature.UpdateVitalDelta, whose delta is an int, so a
                // ceiling above int.MaxValue describes a boss the vital write cannot express.
                // WorldEventSpawner.ClampVitalDelta is the correctness backstop; this is the diagnostic that
                // says the content is out of range rather than letting the backstop hide it.
                if (boss.ThroughputCapHealth > int.MaxValue)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has a throughputCapHealth above int.MaxValue, " +
                                    "clamped to int.MaxValue");
                    boss.ThroughputCapHealth = int.MaxValue;
                }

                // A cap at or below the floor would pin the boss to the floor for every crowd, which is far
                // more likely to be a typo than an intent. Repaired to the default cap; if the floor is
                // itself above that default the ratio still resolves to 1.0 in
                // WorldEventThroughput.CapRatio, so the outcome is a floored boss either way, never a
                // negative or inverted range.
                if (boss.ThroughputFloorHealth > 0 && boss.ThroughputCapHealth <= boss.ThroughputFloorHealth)
                {
                    diagnostics.Add($"bosses.json: boss '{boss.Id}' has a throughputCapHealth at or below its " +
                                    "throughputFloorHealth, using the default");
                    boss.ThroughputCapHealth = BossDef.DefaultThroughputCapHealth;
                }

                boss.Kind = BossKind.Named;

                result[boss.Id] = boss;
            }

            return result;
        }

        /// <summary>
        /// Parses species/*.json (TECH-DESIGN C15, 2026-08-16). A malformed file, an id that does not match
        /// its file stem, or a missing displayName drops the whole table with a diagnostic; a malformed
        /// individual member (wcid 0, role outside 0..2) drops just that member. Every kept table also
        /// registers a <see cref="FamilyDef"/> in <paramref name="families"/> unless families.json already
        /// declared the same id, in which case families.json metadata wins and the collision is recorded
        /// in <paramref name="notes"/> rather than <paramref name="diagnostics"/> - it is how a family gets
        /// a custom event display name, not a mistake. See <see cref="Notes"/>.
        ///
        /// This is structural validation only - it has no access to the world weenie cache, so "wcid not
        /// found", "already flagged WorldEventCreature" and eligibility checks happen later, in
        /// WorldEventCatalogBuilder, which is the only place that sees live weenies.
        /// </summary>
        private static Dictionary<string, SpeciesTableDef> ParseSpeciesTables(
            IEnumerable<(string fileName, string json)> speciesFiles, List<string> diagnostics,
            List<string> notes, Dictionary<string, FamilyDef> families)
        {
            var result = new Dictionary<string, SpeciesTableDef>();

            if (speciesFiles == null)
                return result;

            foreach (var (fileName, json) in speciesFiles)
            {
                if (string.IsNullOrEmpty(json))
                    continue;

                SpeciesTableDef table;

                try
                {
                    table = JsonSerializer.Deserialize<SpeciesTableDef>(json, ConfigManager.SerializerOptions);
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"species/{fileName}: malformed JSON - {ex.Message}");
                    continue;
                }

                if (table == null)
                {
                    diagnostics.Add($"species/{fileName}: malformed JSON - empty document");
                    continue;
                }

                if (!IsValidId(table.Id))
                {
                    diagnostics.Add($"species/{fileName}: entry with invalid or missing id '{table.Id}' dropped");
                    continue;
                }

                var stem = Path.GetFileNameWithoutExtension(fileName);

                if (table.Id != stem)
                {
                    diagnostics.Add($"species/{fileName}: id '{table.Id}' does not match the file stem '{stem}', dropped");
                    continue;
                }

                if (string.IsNullOrEmpty(table.DisplayName))
                {
                    diagnostics.Add($"species/{fileName}: species table '{table.Id}' missing displayName, dropped");
                    continue;
                }

                if (result.ContainsKey(table.Id))
                {
                    diagnostics.Add($"species/{fileName}: duplicate species id '{table.Id}', later entry dropped");
                    continue;
                }

                var keptMembers = new List<SpeciesMemberDef>();

                foreach (var member in table.Members ?? new List<SpeciesMemberDef>())
                {
                    if (member == null)
                    {
                        diagnostics.Add($"species/{fileName}: null member entry dropped");
                        continue;
                    }

                    if (member.Wcid == 0)
                    {
                        diagnostics.Add($"species/{fileName}: member with wcid 0 dropped");
                        continue;
                    }

                    if (member.Role < 0 || member.Role > 2)
                    {
                        diagnostics.Add($"species/{fileName}: wcid {member.Wcid} has role {member.Role} outside 0..2 (0 trash / 1 elite / 2 champion), dropped");
                        continue;
                    }

                    keptMembers.Add(member);
                }

                table.Members = keptMembers;

                result[table.Id] = table;

                if (families.ContainsKey(table.Id))
                {
                    notes.Add($"species/{fileName}: family '{table.Id}' also declared in families.json; families.json metadata wins");
                }
                else
                {
                    families[table.Id] = new FamilyDef
                    {
                        Id = table.Id,
                        DisplayName = table.DisplayName,
                        HueKey = table.HueKey,
                        BiomeTags = table.BiomeTags != null ? new List<string>(table.BiomeTags) : new List<string>()
                    };
                }
            }

            return result;
        }

        private static Dictionary<string, FamilyDef> ParseFamilies(string json, List<string> diagnostics)
        {
            var result = new Dictionary<string, FamilyDef>();

            if (string.IsNullOrEmpty(json))
                return result;

            FamiliesFile file;

            try
            {
                file = JsonSerializer.Deserialize<FamiliesFile>(json, ConfigManager.SerializerOptions) ?? new FamiliesFile();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"families.json: malformed JSON - {ex.Message}");
                return result;
            }

            foreach (var family in file.Families ?? new List<FamilyDef>())
            {
                if (!IsValidId(family?.Id))
                {
                    diagnostics.Add($"families.json: entry with invalid or missing id '{family?.Id}' dropped");
                    continue;
                }

                if (result.ContainsKey(family.Id))
                {
                    diagnostics.Add($"families.json: duplicate id '{family.Id}', later entry dropped");
                    continue;
                }

                if (string.IsNullOrEmpty(family.DisplayName))
                {
                    diagnostics.Add($"families.json: family '{family.Id}' missing displayName, dropped");
                    continue;
                }

                result[family.Id] = family;
            }

            return result;
        }

        private static Dictionary<string, GoalDef> ParseGoals(string json, List<string> diagnostics)
        {
            var result = new Dictionary<string, GoalDef>();

            if (string.IsNullOrEmpty(json))
                return result;

            GoalsFile file;

            try
            {
                file = JsonSerializer.Deserialize<GoalsFile>(json, ConfigManager.SerializerOptions) ?? new GoalsFile();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"goals.json: malformed JSON - {ex.Message}");
                return result;
            }

            foreach (var goal in file.Goals ?? new List<GoalDef>())
            {
                if (!IsValidId(goal?.Id))
                {
                    diagnostics.Add($"goals.json: entry with invalid or missing id '{goal?.Id}' dropped");
                    continue;
                }

                if (result.ContainsKey(goal.Id))
                {
                    diagnostics.Add($"goals.json: duplicate id '{goal.Id}', later entry dropped");
                    continue;
                }

                if (!TryParseEnum<GoalType>(goal.Type, out var typeKind))
                {
                    diagnostics.Add($"goals.json: goal '{goal.Id}' has unknown type '{goal.Type}', dropped");
                    continue;
                }

                if (!TryParseEnum<MvpRule>(goal.MvpRule, out var ruleKind))
                {
                    diagnostics.Add($"goals.json: goal '{goal.Id}' has unknown mvpRule '{goal.MvpRule}', dropped");
                    continue;
                }

                if (typeKind == GoalType.KillCount)
                {
                    if (goal.Count == null || goal.Count.Base < 1 || goal.Count.Cap < goal.Count.Base)
                    {
                        diagnostics.Add($"goals.json: goal '{goal.Id}' has an invalid count (base/cap), dropped");
                        continue;
                    }
                }

                if (typeKind == GoalType.Hold)
                {
                    if (goal.HoldSeconds < 1)
                    {
                        diagnostics.Add($"goals.json: goal '{goal.Id}' has an invalid holdSeconds, dropped");
                        continue;
                    }
                }

                goal.TypeKind = typeKind;
                goal.RuleKind = ruleKind;

                // 2026-08-19 outcome-line templates: same blank-repair pattern as ValidateFlavourOverrides
                // below - a blank value is repaired to null (no template, fall back to the fixed shape) with
                // a diagnostic; an absent key is silent.
                if (goal.SuccessTemplate != null && string.IsNullOrWhiteSpace(goal.SuccessTemplate))
                {
                    diagnostics.Add($"goals.json: goal '{goal.Id}' has a blank successTemplate, ignored");
                    goal.SuccessTemplate = null;
                }

                if (goal.FailTemplate != null && string.IsNullOrWhiteSpace(goal.FailTemplate))
                {
                    diagnostics.Add($"goals.json: goal '{goal.Id}' has a blank failTemplate, ignored");
                    goal.FailTemplate = null;
                }

                result[goal.Id] = goal;
            }

            return result;
        }

        private static Dictionary<string, RewardDef> ParseRewards(string json, List<string> diagnostics)
        {
            var result = new Dictionary<string, RewardDef>();

            if (string.IsNullOrEmpty(json))
                return result;

            RewardsFile file;

            try
            {
                file = JsonSerializer.Deserialize<RewardsFile>(json, ConfigManager.SerializerOptions) ?? new RewardsFile();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"rewards.json: malformed JSON - {ex.Message}");
                return result;
            }

            foreach (var reward in file.Rewards ?? new List<RewardDef>())
            {
                if (!IsValidId(reward?.Id))
                {
                    diagnostics.Add($"rewards.json: entry with invalid or missing id '{reward?.Id}' dropped");
                    continue;
                }

                if (result.ContainsKey(reward.Id))
                {
                    diagnostics.Add($"rewards.json: duplicate id '{reward.Id}', later entry dropped");
                    continue;
                }

                if (reward.SuccessCrateWcid == 0 || reward.ConsolationCrateWcid == 0 || reward.CacheWcid == 0)
                {
                    diagnostics.Add($"rewards.json: reward '{reward.Id}' has a zero wcid, dropped");
                    continue;
                }

                if (reward.ParticipantsPerCache < 1)
                {
                    diagnostics.Add($"rewards.json: reward '{reward.Id}' has an invalid participantsPerCache, dropped");
                    continue;
                }

                if (reward.ClaimWindowSeconds < 1)
                {
                    diagnostics.Add($"rewards.json: reward '{reward.Id}' has an invalid claimWindowSeconds, dropped");
                    continue;
                }

                // WP-18 item 3. 0 is legal and means "fall back to participantsPerCache", so only a negative
                // count or one past the run ceiling is a defect.
                if (reward.CacheCount < 0 || reward.CacheCount > WorldEventRewardDelivery.MaxCachesPerRun)
                {
                    diagnostics.Add($"rewards.json: reward '{reward.Id}' has an invalid cacheCount {reward.CacheCount} " +
                                    $"(must be 0 to {WorldEventRewardDelivery.MaxCachesPerRun}, where 0 means use participantsPerCache), dropped");
                    continue;
                }

                reward.CacheDecor = ValidateCacheDecor(reward, diagnostics);
                reward.CacheWcids = ValidateCacheWcids(reward, diagnostics);

                result[reward.Id] = reward;
            }

            return result;
        }

        private static Dictionary<string, AnchorDef> ParseAnchors(string json, List<string> diagnostics)
        {
            var result = new Dictionary<string, AnchorDef>();

            if (string.IsNullOrEmpty(json))
                return result;

            AnchorsFile file;

            try
            {
                file = JsonSerializer.Deserialize<AnchorsFile>(json, ConfigManager.SerializerOptions) ?? new AnchorsFile();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"anchors.json: malformed JSON - {ex.Message}");
                return result;
            }

            foreach (var anchor in file.Anchors ?? new List<AnchorDef>())
            {
                if (!IsValidId(anchor?.Id))
                {
                    diagnostics.Add($"anchors.json: entry with invalid or missing id '{anchor?.Id}' dropped");
                    continue;
                }

                if (result.ContainsKey(anchor.Id))
                {
                    diagnostics.Add($"anchors.json: duplicate id '{anchor.Id}', later entry dropped");
                    continue;
                }

                if (anchor.CellId == 0)
                {
                    diagnostics.Add($"anchors.json: anchor '{anchor.Id}' has a zero cellId, dropped");
                    continue;
                }

                result[anchor.Id] = anchor;
            }

            return result;
        }

        private static Dictionary<string, SourceThemeDef> ParseSources(string json, List<string> diagnostics,
            IReadOnlyDictionary<string, GoalDef> goals)
        {
            var result = new Dictionary<string, SourceThemeDef>();

            if (string.IsNullOrEmpty(json))
                return result;

            SourcesFile file;

            try
            {
                file = JsonSerializer.Deserialize<SourcesFile>(json, ConfigManager.SerializerOptions) ?? new SourcesFile();
            }
            catch (Exception ex)
            {
                diagnostics.Add($"sources.json: malformed JSON - {ex.Message}");
                return result;
            }

            foreach (var source in file.Sources ?? new List<SourceThemeDef>())
            {
                if (!IsValidId(source?.Id))
                {
                    diagnostics.Add($"sources.json: entry with invalid or missing id '{source?.Id}' dropped");
                    continue;
                }

                if (result.ContainsKey(source.Id))
                {
                    diagnostics.Add($"sources.json: duplicate id '{source.Id}', later entry dropped");
                    continue;
                }

                if (!TryParseEnum<SourceGeometry>(source.Geometry, out var geometryKind))
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an unknown geometry '{source.Geometry}', dropped");
                    continue;
                }

                if (source.GeometryPoints < 1)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an invalid geometryPoints, dropped");
                    continue;
                }

                if (source.MaxAlive < 1)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an invalid maxAlive, dropped");
                    continue;
                }

                if (source.WaveCount == null || source.WaveCount.Base < 1 || source.WaveCount.Cap < source.WaveCount.Base)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an invalid waveCount (base/cap), dropped");
                    continue;
                }

                if (source.RewardRadius <= 0)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an invalid rewardRadius, dropped");
                    continue;
                }

                // WP-17 sky-drop. Unlike decor's dz and an npc's dz - which are placement offsets a scene may
                // set to anything, including a negative - this one is a FALL, so it has a direction and a
                // ceiling: below 0 would bury the wave under the terrain, and above MaxSpawnDz the client
                // stops animating the falling object. A bad value is a whole-theme drop rather than a
                // per-entry one because it is a field of the theme itself, not of a list entry.
                if (!IsFinite(source.SpawnDz) || source.SpawnDz < 0 || source.SpawnDz > SourceThemeDef.MaxSpawnDz)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an invalid spawnDz {source.SpawnDz} " +
                                    $"(must be a finite value from 0 to {SourceThemeDef.MaxSpawnDz}), dropped");
                    continue;
                }

                if (source.ClearanceRadiusOverride.HasValue
                    && (!IsFinite(source.ClearanceRadiusOverride.Value) || source.ClearanceRadiusOverride.Value < 0))
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an invalid clearanceRadius {source.ClearanceRadiusOverride} " +
                                    "(must be a finite value of 0 or more), dropped");
                    continue;
                }

                source.GeometryKind = geometryKind;

                var keptGoals = new List<string>();

                foreach (var goalId in source.CompatibleGoals ?? new List<string>())
                {
                    if (goals.ContainsKey(goalId))
                    {
                        keptGoals.Add(goalId);
                    }
                    else
                    {
                        diagnostics.Add($"sources.json: source '{source.Id}' references unknown goal '{goalId}', removed");
                    }
                }

                source.CompatibleGoals = keptGoals;
                source.Decor = ValidateDecor(source, diagnostics);
                source.DecorStyles = ValidateDecorStyles(source, diagnostics);
                source.Npcs = ValidateNpcs(source, diagnostics);
                source.Objectives = ValidateObjectives(source, diagnostics);

                if (source.ObjectiveWcid != 0 && source.Objectives.Count > 0)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' declares both objectiveWcid and objectives; objectives wins");
                }

                if (source.ObjectiveHealth != null &&
                    (source.ObjectiveHealth.Base < 1 || source.ObjectiveHealth.Cap < source.ObjectiveHealth.Base))
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' has an invalid objectiveHealth (base/cap), dropped");
                    source.ObjectiveHealth = null;
                }

                ValidateDifficulty(source, diagnostics);
                ValidateFlavourOverrides(source, diagnostics);

                result[source.Id] = source;
            }

            return result;
        }

        /// <summary>
        /// Source-level flavour overrides (2026-08-19). Like the difficulty block, nothing here can make a
        /// theme unplaceable, so nothing here drops it: a blank goalDisplayName/successTemplate/failTemplate
        /// becomes null ("no override", use the goal's own wording) and a blank objective noun is repaired to
        /// the shipped default, each with a diagnostic. An ABSENT key is not a diagnostic - the property
        /// initializers already supply the defaults, so a theme that never heard of these fields validates
        /// silently. All three of goalDisplayName/successTemplate/failTemplate are applied only when the
        /// composed goal is DestroySource - see WorldEventComposition - but that scoping is a composition-time
        /// decision, not a load-time one, so it is not checked here.
        /// </summary>
        private static void ValidateFlavourOverrides(SourceThemeDef source, List<string> diagnostics)
        {
            if (source.GoalDisplayName != null && string.IsNullOrWhiteSpace(source.GoalDisplayName))
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a blank goalDisplayName, ignored");
                source.GoalDisplayName = null;
            }

            if (source.SuccessTemplate != null && string.IsNullOrWhiteSpace(source.SuccessTemplate))
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a blank successTemplate, ignored");
                source.SuccessTemplate = null;
            }

            if (source.FailTemplate != null && string.IsNullOrWhiteSpace(source.FailTemplate))
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a blank failTemplate, ignored");
                source.FailTemplate = null;
            }

            if (string.IsNullOrWhiteSpace(source.ObjectiveNoun))
            {
                if (source.ObjectiveNoun != null)
                    diagnostics.Add($"sources.json: source '{source.Id}' has a blank objectiveNoun, using '{SourceThemeDef.DefaultObjectiveNoun}'");

                source.ObjectiveNoun = SourceThemeDef.DefaultObjectiveNoun;
            }

            if (string.IsNullOrWhiteSpace(source.ObjectiveNounPlural))
            {
                if (source.ObjectiveNounPlural != null)
                    diagnostics.Add($"sources.json: source '{source.Id}' has a blank objectiveNounPlural, using '{SourceThemeDef.DefaultObjectiveNounPlural}'");

                source.ObjectiveNounPlural = SourceThemeDef.DefaultObjectiveNounPlural;
            }
        }

        /// <summary>
        /// TECH-DESIGN 2.15 difficulty-block validation. Unlike geometry or maxAlive, a bad value here can
        /// never make a theme unplaceable - the worst it can do is turn a dial off - so nothing in this
        /// method drops the theme. Out-of-range values are repaired to the documented default with a
        /// diagnostic, and an explicit JSON null becomes a default block rather than a null reference.
        /// </summary>
        private static void ValidateDifficulty(SourceThemeDef source, List<string> diagnostics)
        {
            if (source.CrowdHealth == null)
                source.CrowdHealth = new CrowdHealthDef();

            if (source.Pace == null)
                source.Pace = new PaceDef();

            if (source.OverflowPerChampion < 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a negative overflowPerChampion, using 0 (overflow champions off)");
                source.OverflowPerChampion = 0;
            }

            if (source.OverflowChampionMaxAlive < 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a negative overflowChampionMaxAlive, using 0");
                source.OverflowChampionMaxAlive = 0;
            }

            // WP-24: a synthetic health multiplier at or below 0 is nonsensical (it would shrink or zero the
            // creature's health rather than buff it), so it is repaired to the documented default rather
            // than dropping the theme - the same "never cost the theme" treatment every other difficulty
            // dial here gets. Absent from JSON is not this path at all: the C# default already applies.
            if (!IsFinite(source.SyntheticEliteHealthMult) || source.SyntheticEliteHealthMult <= 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has an invalid syntheticEliteHealthMult, using {SourceThemeDef.DefaultSyntheticEliteHealthMult}");
                source.SyntheticEliteHealthMult = SourceThemeDef.DefaultSyntheticEliteHealthMult;
            }

            if (!IsFinite(source.SyntheticChampionHealthMult) || source.SyntheticChampionHealthMult <= 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has an invalid syntheticChampionHealthMult, using {SourceThemeDef.DefaultSyntheticChampionHealthMult}");
                source.SyntheticChampionHealthMult = SourceThemeDef.DefaultSyntheticChampionHealthMult;
            }

            var crowd = source.CrowdHealth;

            if (crowd.StartAt < 0 || !IsFinite(crowd.PerParticipant) || crowd.PerParticipant < 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has an invalid crowdHealth startAt/perParticipant, using the defaults");
                crowd.StartAt = CrowdHealthDef.DefaultStartAt;
                crowd.PerParticipant = CrowdHealthDef.DefaultPerParticipant;
            }

            if (!IsFinite(crowd.Cap) || crowd.Cap < 1.0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a crowdHealth cap below 1.0, using 1.0 (crowd scaling off)");
                crowd.Cap = 1.0;
            }

            var pace = source.Pace;

            if (!IsFinite(pace.MinWaveSeconds) || !IsFinite(pace.MaxWaveSeconds)
                || pace.MinWaveSeconds < 0 || pace.MaxWaveSeconds <= pace.MinWaveSeconds)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has an invalid pace window " +
                                $"({pace.MinWaveSeconds}, {pace.MaxWaveSeconds}), using the defaults");
                pace.MinWaveSeconds = PaceDef.DefaultMinWaveSeconds;
                pace.MaxWaveSeconds = PaceDef.DefaultMaxWaveSeconds;
            }

            if (pace.QuantityStep < 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a negative pace quantityStep, using 0");
                pace.QuantityStep = 0;
            }

            if (!IsFinite(pace.HealthStep) || pace.HealthStep < 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a negative pace healthStep, using 0");
                pace.HealthStep = 0;
            }

            if (!IsFinite(pace.HealthCap) || pace.HealthCap < 1.0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' has a pace healthCap below 1.0, using 1.0 (pace health scaling off)");
                pace.HealthCap = 1.0;
            }
        }

        /// <summary>
        /// WP-14 decor validation. A decor entry is pure look, so a bad one must never cost the theme: each
        /// entry is checked on its own and dropped with a diagnostic, and the theme is always kept. A missing
        /// or null "decor" key is simply an empty list.
        ///
        /// Dz is deliberately unchecked - it is a compound of the disc height and 0.455 x Scale (see
        /// DecorDef's remarks) and any value, including a negative one, is a legitimate placement. Dx/Dy
        /// (WP-19) and Pitch (WP-21) carry no range rule either - a scene may offset or tilt a layer any way -
        /// so the only thing that can be wrong about them is not being a number.
        /// </summary>
        private static List<DecorDef> ValidateDecor(SourceThemeDef source, List<string> diagnostics)
        {
            var kept = new List<DecorDef>();

            if (source.Decor == null)
                return kept;

            for (var i = 0; i < source.Decor.Count; i++)
            {
                var entry = source.Decor[i];

                if (entry == null)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' decor entry {i} is null, dropped");
                    continue;
                }

                if (entry.Wcid == 0 || entry.Scale <= 0 || entry.Speed <= 0 || !IsFinite(entry.Yaw)
                    || !IsFinite(entry.Dx) || !IsFinite(entry.Dy) || !IsFinite(entry.Pitch))
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' decor entry {i} is invalid " +
                                    $"(wcid={entry.Wcid} dx={entry.Dx} dy={entry.Dy} scale={entry.Scale} " +
                                    $"speed={entry.Speed} yaw={entry.Yaw} pitch={entry.Pitch}), dropped");
                    continue;
                }

                kept.Add(entry);
            }

            return kept;
        }

        /// <summary>
        /// WP-25 decor-style validation, called AFTER <see cref="ValidateDecor"/> so it validates against the
        /// theme's already-validated Decor list. A bad style is dressing that failed, so it is dropped on its
        /// own with a diagnostic and the theme is always kept. A missing or null "decorStyles" key is simply
        /// an empty list.
        ///
        /// A style is dropped when: it is null or empty; it contains a wcid of 0; or its Count does not match
        /// the number of DISTINCT wcids in source.Decor (by first appearance) - a style must supply exactly
        /// one replacement per distinct decor color it will stand in for. Duplicate wcids WITHIN a style are
        /// legal (a monochrome style is a legitimate choice) and are never rejected. If the validated Decor
        /// list is empty but DecorStyles is non-empty, every style is dropped with one diagnostic, since a
        /// style cannot apply to no decor.
        /// </summary>
        private static List<List<uint>> ValidateDecorStyles(SourceThemeDef source, List<string> diagnostics)
        {
            var kept = new List<List<uint>>();

            if (source.DecorStyles == null)
                return kept;

            if (source.DecorStyles.Count == 0)
                return kept;

            var distinctWcidCount = source.Decor
                .Where(entry => entry != null)
                .Select(entry => entry.Wcid)
                .Distinct()
                .Count();

            if (distinctWcidCount == 0)
            {
                diagnostics.Add($"sources.json: source '{source.Id}' declares decorStyles but has no decor to style, all dropped");
                return kept;
            }

            for (var i = 0; i < source.DecorStyles.Count; i++)
            {
                var style = source.DecorStyles[i];

                if (style == null || style.Count == 0)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' decorStyles entry {i} is null or empty, dropped");
                    continue;
                }

                if (style.Any(wcid => wcid == 0))
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' decorStyles entry {i} contains a 0 wcid, dropped");
                    continue;
                }

                if (style.Count != distinctWcidCount)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' decorStyles entry {i} has {style.Count} " +
                                    $"wcids but decor has {distinctWcidCount} distinct colors, dropped");
                    continue;
                }

                kept.Add(style);
            }

            return kept;
        }

        /// <summary>
        /// WP-19 cache-decor validation, the same shape as <see cref="ValidateDecor"/>: a bad prop is
        /// dressing that failed, so it is dropped on its own with a diagnostic and the reward profile is
        /// always kept. A missing or null "cacheDecor" key is simply an empty list.
        ///
        /// Dx/Dy/Dz carry no range rule - a scene may stand a prop anywhere around the chest - so the only
        /// thing that can be wrong about them is not being a number.
        /// </summary>
        private static List<DecorDef> ValidateCacheDecor(RewardDef reward, List<string> diagnostics)
        {
            var kept = new List<DecorDef>();

            if (reward.CacheDecor == null)
                return kept;

            for (var i = 0; i < reward.CacheDecor.Count; i++)
            {
                var entry = reward.CacheDecor[i];

                if (entry == null)
                {
                    diagnostics.Add($"rewards.json: reward '{reward.Id}' cacheDecor entry {i} is null, dropped");
                    continue;
                }

                if (entry.Wcid == 0 || entry.Scale <= 0 || entry.Speed <= 0 || !IsFinite(entry.Yaw)
                    || !IsFinite(entry.Dx) || !IsFinite(entry.Dy) || !IsFinite(entry.Dz) || !IsFinite(entry.Pitch))
                {
                    diagnostics.Add($"rewards.json: reward '{reward.Id}' cacheDecor entry {i} is invalid " +
                                    $"(wcid={entry.Wcid} dx={entry.Dx} dy={entry.Dy} dz={entry.Dz} scale={entry.Scale} " +
                                    $"speed={entry.Speed} yaw={entry.Yaw} pitch={entry.Pitch}), dropped");
                    continue;
                }

                kept.Add(entry);
            }

            return kept;
        }

        /// <summary>
        /// The cache-look pool: a 0 entry is dropped individually with a diagnostic (same shape as
        /// <see cref="ValidateCacheDecor"/>), duplicates are de-duplicated silently, and the reward itself is
        /// always kept - an empty or all-zero pool just falls back to <see cref="RewardDef.CacheWcid"/> at
        /// delivery time. This stays a pure JSON-shape check: no weenie-existence lookup here, so the store
        /// keeps its DB-free load path; existence is checked at delivery time instead (WorldEventRewardDelivery).
        /// A missing or null "cacheWcids" key is simply an empty list.
        /// </summary>
        private static List<uint> ValidateCacheWcids(RewardDef reward, List<string> diagnostics)
        {
            var kept = new List<uint>();

            if (reward.CacheWcids == null)
                return kept;

            foreach (var wcid in reward.CacheWcids)
            {
                if (wcid == 0)
                {
                    diagnostics.Add($"rewards.json: reward '{reward.Id}' cacheWcids contains 0, entry dropped");
                    continue;
                }

                if (!kept.Contains(wcid))
                    kept.Add(wcid);
            }

            return kept;
        }

        /// <summary>
        /// WP-15 npc validation, deliberately the same shape as <see cref="ValidateDecor"/>: a bad npc entry
        /// is dressing that failed, so it is dropped on its own with a diagnostic and the theme is always
        /// kept. A missing or null "npcs" key is simply an empty list.
        ///
        /// Only the wcid and the four floats are checked. Dx/Dy/Dz/Yaw carry no range rule at all - a scene
        /// may stand an npc anywhere and point it any way - so the only thing that can be wrong about them
        /// is not being a number.
        /// </summary>
        private static List<NpcDef> ValidateNpcs(SourceThemeDef source, List<string> diagnostics)
        {
            var kept = new List<NpcDef>();

            if (source.Npcs == null)
                return kept;

            for (var i = 0; i < source.Npcs.Count; i++)
            {
                var entry = source.Npcs[i];

                if (entry == null)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' npc entry {i} is null, dropped");
                    continue;
                }

                if (entry.Wcid == 0 || !IsFinite(entry.Dx) || !IsFinite(entry.Dy) || !IsFinite(entry.Dz)
                    || !IsFinite(entry.Yaw))
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' npc entry {i} is invalid " +
                                    $"(wcid={entry.Wcid} dx={entry.Dx} dy={entry.Dy} dz={entry.Dz} yaw={entry.Yaw}), dropped");
                    continue;
                }

                kept.Add(entry);
            }

            return kept;
        }

        /// <summary>
        /// WP-21 fixed-offset objective validation, deliberately the same shape as <see cref="ValidateNpcs"/>:
        /// a bad objective entry is dropped on its own with a diagnostic and the theme is always kept. A
        /// missing or null "objectives" key is simply an empty list.
        ///
        /// Only the wcid and the four floats are checked. Dx/Dy/Dz/Yaw carry no range rule at all - a scene
        /// may stand an objective anywhere and point it any way - so the only thing that can be wrong about
        /// them is not being a number.
        /// </summary>
        private static List<ObjectiveDef> ValidateObjectives(SourceThemeDef source, List<string> diagnostics)
        {
            var kept = new List<ObjectiveDef>();

            if (source.Objectives == null)
                return kept;

            for (var i = 0; i < source.Objectives.Count; i++)
            {
                var entry = source.Objectives[i];

                if (entry == null)
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' objective entry {i} is null, dropped");
                    continue;
                }

                if (entry.Wcid == 0 || !IsFinite(entry.Dx) || !IsFinite(entry.Dy) || !IsFinite(entry.Dz)
                    || !IsFinite(entry.Yaw))
                {
                    diagnostics.Add($"sources.json: source '{source.Id}' objective entry {i} is invalid " +
                                    $"(wcid={entry.Wcid} dx={entry.Dx} dy={entry.Dy} dz={entry.Dz} yaw={entry.Yaw}), dropped");
                    continue;
                }

                kept.Add(entry);
            }

            return kept;
        }

        /// <summary>
        /// A JSON number can only arrive as NaN/Infinity through a value the serializer allows through
        /// (or an entry constructed in code by a test), but a non-finite yaw would reach
        /// Quaternion.CreateFromAxisAngle and produce a rotation the client cannot use - so it is refused
        /// here rather than at placement time.
        /// </summary>
        private static bool IsFinite(float value)
        {
            return float.IsFinite(value);
        }

        /// <summary>The double overload, for the TECH-DESIGN 2.15 difficulty blocks.</summary>
        private static bool IsFinite(double value)
        {
            return double.IsFinite(value);
        }
    }
}
