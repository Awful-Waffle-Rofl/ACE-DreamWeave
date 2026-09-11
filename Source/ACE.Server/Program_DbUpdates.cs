using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

using ACE.Common;

namespace ACE.Server
{
    partial class Program
    {
        // Internal (not private) so the pure cache-key/migration helpers can be exercised from ACE.Server.Tests.
        internal class WorldCustomizationCacheEntry
        {
            public long Size { get; set; }
            public long MTimeUtcTicks { get; set; }
            public string Sha256 { get; set; }
        }

        internal class WorldCustomizationCacheFile
        {
            // Cache schema version. v2 keys entries by repo-relative path (forward slashes) instead of the
            // absolute FullName v1 used. A loaded cache with Version < 2 is migrated in place (see
            // MigrateCacheKeysToV2) so a stage doesn't pointlessly re-apply its whole content set once.
            public int Version { get; set; }

            // Identifies the exact ace_world database + version this cache was built against.
            // A mismatch (different DB, restored backup, or a base world update that just ran)
            // means the cache can no longer be trusted and must be discarded.
            public string Fingerprint { get; set; }

            public Dictionary<string, WorldCustomizationCacheEntry> Files { get; set; } = new Dictionary<string, WorldCustomizationCacheEntry>();
        }

        internal const int WorldCustomizationCacheVersion = 2;

        /// <summary>
        /// Computes a file's repo-relative cache key: the path relative to its content root with forward
        /// slashes. Files discovered under a WorldCustomizationAddedPaths root are prefixed with that root's
        /// leaf directory name so keys stay unambiguous across roots. Pure/testable (no DB, no I/O).
        /// </summary>
        internal static string ComputeContentRelativeKey(string absoluteFilePath, string rootPath, bool isAddedPath)
        {
            var rel = Path.GetRelativePath(rootPath, absoluteFilePath).Replace('\\', '/');

            if (isAddedPath)
            {
                var leaf = new DirectoryInfo(rootPath.TrimEnd('/', '\\', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
                if (!string.IsNullOrEmpty(leaf))
                    rel = leaf + "/" + rel;
            }

            return rel;
        }

        /// <summary>
        /// True (with the relative key) if <paramref name="absoluteKey"/> resolves under one of the given
        /// roots. Used to migrate a v1 (absolute-path-keyed) cache to v2; keys that don't resolve are dropped.
        /// Roots are tried in order, so the primary content folder should come first.
        /// </summary>
        internal static bool TryResolveRelativeKey(string absoluteKey, IReadOnlyList<(string rootPath, bool isAddedPath)> roots, out string relativeKey)
        {
            relativeKey = null;
            if (string.IsNullOrEmpty(absoluteKey) || roots == null)
                return false;

            foreach (var (rootPath, isAddedPath) in roots)
            {
                var rel = Path.GetRelativePath(rootPath, absoluteKey);
                if (rel != absoluteKey && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                {
                    relativeKey = ComputeContentRelativeKey(absoluteKey, rootPath, isAddedPath);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Rewrites a v1 (absolute-key) file map to v2 (relative-key). Entries whose absolute key resolves
        /// under a known root are preserved under the new key (avoids a pointless full re-apply); keys that
        /// don't resolve are dropped. Pure/testable.
        /// </summary>
        internal static Dictionary<string, WorldCustomizationCacheEntry> MigrateCacheKeysToV2(
            Dictionary<string, WorldCustomizationCacheEntry> oldFiles,
            IReadOnlyList<(string rootPath, bool isAddedPath)> roots)
        {
            var migrated = new Dictionary<string, WorldCustomizationCacheEntry>();
            if (oldFiles == null)
                return migrated;

            foreach (var kvp in oldFiles)
            {
                if (TryResolveRelativeKey(kvp.Key, roots, out var relKey))
                    migrated[relKey] = kvp.Value; // last-writer-wins if two absolutes collapse (shouldn't happen)
            }

            return migrated;
        }
        private static void CheckForWorldDatabaseUpdate()
        {
            log.Info($"Automatic World Database Update started...");
            try
            {
                var worldDb = new Database.WorldDatabase();
                var currentVersion = worldDb.GetVersion();
                log.Info($"Current World Database version: Base - {currentVersion.BaseVersion} | Patch - {currentVersion.PatchVersion}");

                var url = "https://api.github.com/repos/ACEmulator/ACE-World-16PY-Patches/releases/latest";

                using var client = new WebClient();
                var html = client.GetStringFromURL(url).Result;
                var json = JsonSerializer.Deserialize<JsonElement>(html);
                string tag = json.GetProperty("tag_name").GetString();
                string dbURL = json.GetProperty("assets")[0].GetProperty("browser_download_url").GetString();
                string dbFileName = json.GetProperty("assets")[0].GetProperty("name").GetString();

                if (currentVersion.PatchVersion != tag)
                {
                    var patchVersionSplit = currentVersion.PatchVersion.Split(".");
                    var tagSplit = tag.Split(".");

                    int.TryParse(patchVersionSplit[0], out var patchMajor);
                    int.TryParse(patchVersionSplit[1], out var patchMinor);
                    int.TryParse(patchVersionSplit[2], out var patchBuild);

                    int.TryParse(tagSplit[0], out var tagMajor);
                    int.TryParse(tagSplit[1], out var tagMinor);
                    int.TryParse(tagSplit[2], out var tagBuild);

                    if (tagMajor > patchMajor || tagMinor > patchMinor || (tagBuild > patchBuild && patchBuild != 0))
                    {
                        log.Info($"Latest patch version is {tag} -- Update Required!");
                        UpdateToLatestWorldDatabase(dbURL, dbFileName);
                        var newVersion = worldDb.GetVersion();
                        log.Info($"Updated World Database version: Base - {newVersion.BaseVersion} | Patch - {newVersion.PatchVersion}");
                    }
                    else
                    {
                        log.Info($"Latest patch version is {tag} -- No Update Required!");
                    }
                }
                else
                {
                    log.Info($"Latest patch version is {tag} -- No Update Required!");
                }
            }
            catch (Exception ex)
            {
                log.Info($"Unable to continue with Automatic World Database Update due to the following error: {ex}");
            }
            log.Info($"Automatic World Database Update complete.");
        }

        private static void UpdateToLatestWorldDatabase(string dbURL, string dbFileName)
        {
            Console.WriteLine();

            if (IsRunningInContainer)
            {
                Console.WriteLine(" ");
                Console.WriteLine("This process will take a while, depending on many factors, and may look stuck while reading and importing the world database, please be patient! ");
                Console.WriteLine(" ");
            }

            Console.Write($"Downloading {dbFileName} .... ");
            using var client = new WebClient();
            try
            {
                var dlTask = client.DownloadFile(dbURL, dbFileName);
                dlTask.Wait();
            }
            catch
            {
                Console.Write($"Download for {dbFileName} failed!");
                return;
            }
            Console.WriteLine("download complete!");

            Console.Write($"Extracting {dbFileName} .... ");
            ZipFile.ExtractToDirectory(dbFileName, ".", true);
            Console.WriteLine("extraction complete!");
            Console.Write($"Deleting {dbFileName} .... ");
            File.Delete(dbFileName);
            Console.WriteLine("Deleted!");

            var sqlFile = dbFileName.Substring(0, dbFileName.Length - 4);
            Console.Write($"Importing {sqlFile} into SQL server at {ConfigManager.Config.MySql.World.Host}:{ConfigManager.Config.MySql.World.Port} (This will take a while, please be patient) .... ");
            using (var sr = File.OpenText(sqlFile))
            {
                var sqlConnect = new MySqlConnector.MySqlConnection($"server={ConfigManager.Config.MySql.World.Host};port={ConfigManager.Config.MySql.World.Port};user={ConfigManager.Config.MySql.World.Username};password={ConfigManager.Config.MySql.World.Password};{ConfigManager.Config.MySql.World.ConnectionOptions}");

                var line = string.Empty;
                var completeSQLline = string.Empty;

                var dbname = ConfigManager.Config.MySql.World.Database;

                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Replace("ace_world", dbname);
                    //do minimal amount of work here
                    if (line.EndsWith(";"))
                    {
                        completeSQLline += line + Environment.NewLine;

                        var script = new MySqlConnector.MySqlCommand(completeSQLline, sqlConnect);
                        try
                        {
                            ExecuteScript(script);
                        }
                        catch (MySqlConnector.MySqlException)
                        {

                        }
                        completeSQLline = string.Empty;
                    }
                    else
                        completeSQLline += line + Environment.NewLine;
                }
                CleanupConnection(sqlConnect);
            }
            Console.WriteLine(" complete!");

            Console.Write($"Deleting {sqlFile} .... ");
            File.Delete(sqlFile);
            Console.WriteLine("Deleted!");
        }

        private static string GetContentFolder()
        {
            var sqlConnect = new MySqlConnector.MySqlConnection($"server={ConfigManager.Config.MySql.Shard.Host};port={ConfigManager.Config.MySql.Shard.Port};user={ConfigManager.Config.MySql.Shard.Username};password={ConfigManager.Config.MySql.Shard.Password};database={ConfigManager.Config.MySql.Shard.Database};{ConfigManager.Config.MySql.Shard.ConnectionOptions}");
            var sqlQuery = "SELECT `value` FROM config_properties_string WHERE `key` = 'content_folder';";
            var sqlCommand = new MySqlConnector.MySqlCommand(sqlQuery, sqlConnect);

            sqlConnect.Open();
            var sqlReader = sqlCommand.ExecuteReader();

            var content_folder = "";

            if (sqlReader.HasRows)
            {
                while (sqlReader.Read())
                {
                    content_folder = sqlReader.GetString(0);
                    break;
                }
            }
            else
                content_folder = @".\Content";

            sqlReader.Close();
            sqlCommand.Connection.Close();

            // handle relative path
            if (content_folder.StartsWith("."))
            {
                var cwd = Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
                content_folder = cwd + content_folder;
            }

            return content_folder;
        }

        private static string GetWorldCustomizationCacheFilePath()
        {
            var cwdPath = Path.Combine(Directory.GetCurrentDirectory(), "WorldCustomizationCache.json");
            if (File.Exists(cwdPath))
                return cwdPath;

            var executingAssemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var directoryName = Path.GetFullPath(Path.GetDirectoryName(executingAssemblyLocation));

            return Path.Combine(directoryName, "WorldCustomizationCache.json");
        }

        // Ties the cache to the specific ace_world database it was built against (host/port/db name + its
        // Base/Patch version). If this doesn't match what's stored on disk, the target database changed identity
        // (new DB, restored backup) or CheckForWorldDatabaseUpdate() just applied an update this run -- either way
        // the cache is stale and must not be trusted.
        private static string GetWorldCustomizationFingerprint()
        {
            try
            {
                var version = new Database.WorldDatabase().GetVersion();
                var baseVersion = version?.BaseVersion ?? "unknown";
                var patchVersion = version?.PatchVersion ?? "unknown";

                return $"{ConfigManager.Config.MySql.World.Host}:{ConfigManager.Config.MySql.World.Port}/{ConfigManager.Config.MySql.World.Database}|{baseVersion}|{patchVersion}";
            }
            catch
            {
                // Can't establish an identity for the target database right now -- don't cache anything and fall back
                // to a full reapply rather than risk trusting a cache built against a different database.
                return null;
            }
        }

        private static WorldCustomizationCacheFile LoadWorldCustomizationCache(string fingerprint)
        {
            if (fingerprint == null)
                return new WorldCustomizationCacheFile { Fingerprint = null };

            var cachePath = GetWorldCustomizationCacheFilePath();

            if (File.Exists(cachePath))
            {
                try
                {
                    var cache = JsonSerializer.Deserialize<WorldCustomizationCacheFile>(File.ReadAllText(cachePath));

                    if (cache != null && cache.Fingerprint == fingerprint)
                        return cache;
                }
                catch
                {
                    // Corrupt/unreadable cache file -- fall through to a fresh one.
                }
            }

            return new WorldCustomizationCacheFile { Fingerprint = fingerprint };
        }

        private static void SaveWorldCustomizationCache(WorldCustomizationCacheFile cache)
        {
            if (cache.Fingerprint == null)
                return;

            try
            {
                cache.Version = WorldCustomizationCacheVersion;
                var cachePath = GetWorldCustomizationCacheFilePath();
                File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unable to save World Customization cache: {ex.Message}");
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha256.ComputeHash(stream));
        }

        // A single content .sql discovered on disk, paired with the metadata the applier needs.
        private sealed class DiscoveredContentFile
        {
            public FileInfo File;
            public string RelativeKey;      // repo-relative, forward slashes; the cache key + planner path
        }

        private static void AutoApplyWorldCustomizations()
        {
            var content_folders_search_option = ConfigManager.Config.Offline.RecurseWorldCustomizationPaths ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            // Roots: the primary content folder first (unprefixed keys), then the configured added paths
            // (keys prefixed with the added root's leaf directory name). Order matters for key resolution.
            var roots = new List<(string rootPath, bool isAddedPath)> { (GetContentFolder(), false) };
            foreach (var added in ConfigManager.Config.Offline.WorldCustomizationAddedPaths.OrderBy(p => p, StringComparer.Ordinal))
                roots.Add((added, true));

            var fingerprint = GetWorldCustomizationFingerprint();
            var cache = LoadWorldCustomizationCache(fingerprint);

            // Migrate a v1 (absolute-path-keyed) cache to v2 (relative-key) in place, so an existing stage
            // doesn't do one pointless full re-apply just because the key scheme changed.
            if (cache.Fingerprint != null && cache.Version < WorldCustomizationCacheVersion)
            {
                var before = cache.Files.Count;
                cache.Files = MigrateCacheKeysToV2(cache.Files, roots);
                cache.Version = WorldCustomizationCacheVersion;
                Console.WriteLine($"Migrated World Customization cache to v{WorldCustomizationCacheVersion} (relative keys): {cache.Files.Count} of {before} entries carried over.");
            }

            if (fingerprint == null)
                Console.WriteLine($"Unable to determine World database identity/version -- World Customization caching disabled for this run (full reapply).");
            else if (cache.Files.Count == 0)
                Console.WriteLine($"No valid World Customization cache found for this database -- performing a full reapply and rebuilding the cache.");

            Console.WriteLine($"Searching for World Customization SQL scripts .... ");

            // --- discovery: gather every .sql across all roots, computing its repo-relative key ---
            var discovered = new List<DiscoveredContentFile>();
            var byRelKey = new Dictionary<string, DiscoveredContentFile>(StringComparer.Ordinal);
            var plannerInputs = new List<ContentFileInput>();
            var previewExcludedCount = 0;
            var landblockExportExcludedCount = 0;

            foreach (var (rootPath, isAddedPath) in roots)
            {
                var contentDI = new DirectoryInfo(rootPath);
                if (!contentDI.Exists)
                    continue;

                Console.WriteLine($"Searching for SQL files within {rootPath} .... ");

                foreach (var file in contentDI.GetFiles("*.sql", content_folders_search_option))
                {
                    var relKey = ComputeContentRelativeKey(file.FullName, rootPath, isAddedPath);

                    // preview/ is human sign-off material, never world content -- drop it before it can
                    // become a planner input (an excluded-but-planned file would still be applied).
                    // Checked against the ABSOLUTE path as well as the relative key: an added root
                    // pointing INSIDE Content/preview (e.g. ...\Content\preview\decor_scale_check) is
                    // keyed by its leaf directory only, so the relative key alone would carry no
                    // "preview" segment and the whole root would auto-apply.
                    if (WorldContentPlanner.IsExcludedFromAutoApply(relKey) ||
                        WorldContentPlanner.IsExcludedFromAutoApply(file.FullName))
                    {
                        previewExcludedCount++;
                        continue;
                    }

                    // sql/landblocks/ is the /createinst export folder: a whole-landblock DELETE + re-insert
                    // snapshot, operator scratch never versioned as content. Same absolute-path double-check
                    // as preview/ above, for the same reason (an added root pointing inside Content/sql/landblocks
                    // would otherwise carry no "landblocks" segment in its relative key).
                    if (WorldContentPlanner.IsLandblockExport(relKey) ||
                        WorldContentPlanner.IsLandblockExport(file.FullName))
                    {
                        landblockExportExcludedCount++;
                        log.Warn($"World Customization: skipped landblock export '{relKey}' -- sql/landblocks/ is the /createinst snapshot folder and is never applied; delete it from the checkout and place through a placements/ unit.");
                        continue;
                    }

                    // First root to claim a relative key wins (primary content folder has precedence).
                    if (byRelKey.ContainsKey(relKey))
                        continue;

                    // Header block for the planner: first 30 lines (matches apply-content.sh's `head -30`).
                    var header = ReadHeaderLines(file.FullName, 30);

                    var df = new DiscoveredContentFile { File = file, RelativeKey = relKey };
                    discovered.Add(df);
                    byRelKey[relKey] = df;
                    plannerInputs.Add(new ContentFileInput(file.FullName, relKey, header));
                }
            }

            if (previewExcludedCount > 0)
                Console.WriteLine($"Skipped {previewExcludedCount} preview .sql file(s) -- preview/ is human sign-off material and is never applied to the World database.");

            if (landblockExportExcludedCount > 0)
                Console.WriteLine($"Skipped {landblockExportExcludedCount} landblock export .sql file(s) -- sql/landblocks/ holds /createinst snapshots (DELETE + re-insert of a whole landblock) that would override placements/ units. Delete them from the checkout.");

            // --- plan: deterministic, dependency-correct order (or hard-fail with diagnostics) ---
            var plan = WorldContentPlanner.CreatePlan(plannerInputs);
            if (!plan.IsValid)
            {
                log.Error("World Customization content plan is INVALID -- applying ZERO files this run. Fix the following and restart:");
                foreach (var diag in plan.Diagnostics)
                    log.Error($"  content-plan: {diag}");

                // Apply nothing; leave the cache untouched so the server boots on yesterday's content.
                Console.WriteLine($"World Customization SQL scripts import aborted -- content plan invalid ({plan.Diagnostics.Count} problem(s)). See log.");
                return;
            }

            var appliedCount = 0;
            var skippedCount = 0;
            var failedUnits = new HashSet<string>(StringComparer.Ordinal);
            var deadFiles = 0;

            var sqlConnect = new MySqlConnector.MySqlConnection($"server={ConfigManager.Config.MySql.World.Host};port={ConfigManager.Config.MySql.World.Port};user={ConfigManager.Config.MySql.World.Username};password={ConfigManager.Config.MySql.World.Password};database={ConfigManager.Config.MySql.World.Database};{ConfigManager.Config.MySql.World.ConnectionOptions}");

            try
            {
                sqlConnect.Open();

                foreach (var planned in plan.Files)
                {
                    if (planned.IsUnclassified)
                        log.Warn($"World Customization: '{planned.RelativePath}' is unclassified (no matching content folder / @phase) -- applying last.");

                    var df = byRelKey[planned.RelativePath];
                    var file = df.File;
                    var cacheKey = planned.RelativePath;

                    // Transitive-dependent skip: if any dependency failed (or was itself skipped) this run,
                    // don't apply this file. Its own unit joins the failed set so its dependents skip too.
                    var deadDep = planned.DependsOn.FirstOrDefault(d => failedUnits.Contains(d));
                    if (deadDep != null)
                    {
                        Console.WriteLine($"Skipping {planned.RelativePath} -- depends on failed unit '{deadDep}'.");
                        log.Warn($"World Customization: skipped {planned.RelativePath}: depends on failed unit {deadDep}");
                        if (planned.UnitName != null)
                            failedUnits.Add(planned.UnitName);
                        // Skipped files must NOT enter the cache (preserve retry-next-boot).
                        deadFiles++;
                        continue;
                    }

                    var mtimeTicks = file.LastWriteTimeUtc.Ticks;
                    cache.Files.TryGetValue(cacheKey, out var cachedEntry);

                    // Fast path: if size+mtime match what we last recorded, trust the stored hash without re-hashing.
                    // Otherwise (including right after a fresh git checkout, which resets mtimes) fall back to a
                    // real content hash before deciding whether anything actually changed.
                    var needsHash = cachedEntry == null || cachedEntry.Size != file.Length || cachedEntry.MTimeUtcTicks != mtimeTicks;
                    var hash = needsHash ? ComputeSha256(file.FullName) : cachedEntry.Sha256;

                    if (cachedEntry != null && cachedEntry.Sha256 == hash)
                    {
                        // Unchanged since it was last successfully applied to this exact database -- skip re-running it.
                        if (needsHash)
                            cache.Files[cacheKey] = new WorldCustomizationCacheEntry { Size = file.Length, MTimeUtcTicks = mtimeTicks, Sha256 = hash };

                        skippedCount++;
                        continue;
                    }

                    Console.Write($"Found {file.FullName} .... ");
                    var sqlDBFile = File.ReadAllText(file.FullName);
                    sqlDBFile = sqlDBFile.Replace("ace_world", ConfigManager.Config.MySql.World.Database);
                    var script = new MySqlConnector.MySqlCommand(sqlDBFile, sqlConnect);

                    Console.Write($"Importing into World database on SQL server at {ConfigManager.Config.MySql.World.Host}:{ConfigManager.Config.MySql.World.Port} .... ");

                    // Wrap the whole-file batch in a transaction so a mid-file failure leaves no partial rows.
                    var transaction = sqlConnect.BeginTransaction();
                    script.Transaction = transaction;
                    try
                    {
                        ExecuteScript(script);
                        transaction.Commit();
                        Console.WriteLine(" complete!");

                        cache.Files[cacheKey] = new WorldCustomizationCacheEntry { Size = file.Length, MTimeUtcTicks = mtimeTicks, Sha256 = hash };
                        appliedCount++;
                    }
                    catch (MySqlConnector.MySqlException ex)
                    {
                        try { transaction.Rollback(); } catch { /* connection may already be faulted */ }

                        Console.WriteLine($" error!");
                        Console.WriteLine($" Unable to apply patch due to following exception: {ex}");
                        log.Error($"World Customization: failed to apply {planned.RelativePath}: {ex.Message}");

                        // Mark this file's unit failed so its transitive dependents skip this run. Don't record it
                        // as applied -- leave it out of the cache so it's retried on every subsequent startup until
                        // it succeeds, matching today's implicit retry-forever behavior.
                        if (planned.UnitName != null)
                            failedUnits.Add(planned.UnitName);
                        cache.Files.Remove(cacheKey);
                    }
                }
            }
            finally
            {
                CleanupConnection(sqlConnect);
            }

            // Drop entries for files that no longer exist on disk (matched by relative key), so the cache
            // doesn't grow unbounded. byRelKey is the authoritative set of what was discovered this run.
            foreach (var key in cache.Files.Keys.Where(k => !byRelKey.ContainsKey(k)).ToList())
                cache.Files.Remove(key);

            SaveWorldCustomizationCache(cache);

            var deadNote = deadFiles > 0 ? $", {deadFiles} skipped (failed dependency)" : "";
            Console.WriteLine($"World Customization SQL scripts import complete! ({appliedCount} applied, {skippedCount} unchanged/skipped{deadNote})");
        }

        /// <summary>Reads up to <paramref name="maxLines"/> lines from a file into a single string (the unit header block).</summary>
        private static string ReadHeaderLines(string filePath, int maxLines)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                using var reader = new StreamReader(filePath);
                string line;
                var count = 0;
                while (count < maxLines && (line = reader.ReadLine()) != null)
                {
                    sb.AppendLine(line);
                    count++;
                }
            }
            catch
            {
                // Unreadable header -> treat as headerless; the file still participates by folder phase.
            }
            return sb.ToString();
        }

        private static void AutoApplyDatabaseUpdates()
        {
            log.Info($"Automatic Database Patching started...");
            Thread.Sleep(1000);

            PatchDatabase("Authentication", ConfigManager.Config.MySql.Authentication.Host, ConfigManager.Config.MySql.Authentication.Port, ConfigManager.Config.MySql.Authentication.Username, ConfigManager.Config.MySql.Authentication.Password, ConfigManager.Config.MySql.Authentication.Database, ConfigManager.Config.MySql.Shard.Database, ConfigManager.Config.MySql.World.Database);
            PatchDatabase("Shard", ConfigManager.Config.MySql.Shard.Host, ConfigManager.Config.MySql.Shard.Port, ConfigManager.Config.MySql.Shard.Username, ConfigManager.Config.MySql.Shard.Password, ConfigManager.Config.MySql.Authentication.Database, ConfigManager.Config.MySql.Shard.Database, ConfigManager.Config.MySql.World.Database);
            PatchDatabase("World", ConfigManager.Config.MySql.World.Host, ConfigManager.Config.MySql.World.Port, ConfigManager.Config.MySql.World.Username, ConfigManager.Config.MySql.World.Password, ConfigManager.Config.MySql.Authentication.Database, ConfigManager.Config.MySql.Shard.Database, ConfigManager.Config.MySql.World.Database);

            Thread.Sleep(1000);
            log.Info($"Automatic Database Patching complete.");
        }

        // Outcome of applying (or failing to apply) one update script, decided by EvaluateDbPatchScriptResult.
        internal enum DbPatchOutcome
        {
            // Record the script as applied and keep processing the remaining scripts in the directory.
            Continue,
            // Do not record the script as applied; stop processing the remaining scripts in the directory
            // this boot so they retry (in order) next boot, once this failure is fixed.
            Stop
        }

        // Pure result of EvaluateDbPatchScriptResult: what PatchDatabase's loop should do after one script.
        internal readonly struct DbPatchScriptResult
        {
            public string RecordedFileName { get; }
            public DbPatchOutcome Outcome { get; }
            public int SkippedCount { get; }

            private DbPatchScriptResult(string recordedFileName, DbPatchOutcome outcome, int skippedCount)
            {
                RecordedFileName = recordedFileName;
                Outcome = outcome;
                SkippedCount = skippedCount;
            }

            public static DbPatchScriptResult Success(string fileName)
            {
                return new DbPatchScriptResult(fileName, DbPatchOutcome.Continue, 0);
            }

            public static DbPatchScriptResult Failure(int skippedCount)
            {
                return new DbPatchScriptResult(null, DbPatchOutcome.Stop, skippedCount);
            }
        }

        // Pure helper (no I/O): of the scripts still to come after the failed one, only the ones NOT already
        // recorded in applied_updates.txt are actually going to be skipped by a Stop -- an already-applied
        // name in the remaining list would have been skipped by the loop's own appliedUpdates.Contains check
        // regardless, so it must not inflate the reported skipped count.
        internal static List<string> GetPendingFileNames(IEnumerable<string> remainingFileNames, IReadOnlyCollection<string> appliedUpdates)
        {
            return remainingFileNames.Where(f => !appliedUpdates.Contains(f)).ToList();
        }

        // Pure decision helper (no I/O) for the per-script outcome inside PatchDatabase's loop, so it can be
        // unit tested without a database or filesystem. `applied` is whether ExecuteScript succeeded for
        // `fileName`; `remainingFileNames` is the ordered list of PENDING (not already-applied) scripts still
        // to come after `fileName` in this directory (not including `fileName` itself) -- see
        // GetPendingFileNames, which callers should filter through first.
        internal static DbPatchScriptResult EvaluateDbPatchScriptResult(string fileName, IReadOnlyList<string> remainingFileNames, bool applied)
        {
            if (applied)
                return DbPatchScriptResult.Success(fileName);

            return DbPatchScriptResult.Failure(remainingFileNames.Count);
        }

        private static void PatchDatabase(string dbType, string host, uint port, string username, string password, string authDB, string shardDB, string worldDB)
        {
            var updatesPath = $"DatabaseSetupScripts{Path.DirectorySeparatorChar}Updates{Path.DirectorySeparatorChar}{dbType}";
            var updatesFile = $"{updatesPath}{Path.DirectorySeparatorChar}applied_updates.txt";

            if (!Directory.Exists(updatesPath))
            {
                // File not found in Environment.CurrentDirectory
                // Lets try the ExecutingAssembly Location
                var executingAssemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;

                var directoryName = Path.GetFullPath(Path.GetDirectoryName(executingAssemblyLocation));

                updatesPath = Path.Combine(directoryName, $"DatabaseSetupScripts{Path.DirectorySeparatorChar}Updates{Path.DirectorySeparatorChar}{dbType}");

                if (!Directory.Exists(updatesPath))
                {
                    Console.WriteLine($" error!");
                    Console.WriteLine($" Unable to locate updates directory");
                }
                else
                {
                    updatesFile = $"{updatesPath}{Path.DirectorySeparatorChar}applied_updates.txt";
                }

            }

            var appliedUpdates = Array.Empty<string>();

            var containerUpdatesFile = $"/ace/Config/{dbType}_applied_updates.txt";
            if (IsRunningInContainer && File.Exists(containerUpdatesFile))
                File.Copy(containerUpdatesFile, updatesFile, true);

            if (File.Exists(updatesFile))
                appliedUpdates = File.ReadAllLines(updatesFile);

            Console.WriteLine($"Searching for {dbType} update SQL scripts .... ");
            var orderedFiles = new DirectoryInfo(updatesPath).GetFiles("*.sql").OrderBy(f => f.Name).ToList();
            for (var fileIndex = 0; fileIndex < orderedFiles.Count; fileIndex++)
            {
                var file = orderedFiles[fileIndex];

                if (appliedUpdates.Contains(file.Name))
                    continue;

                Console.Write($"Found {file.Name} .... ");
                var sqlDBFile = File.ReadAllText(file.FullName);
                var database = "";
                switch (dbType)
                {
                    case "Authentication":
                        database = authDB;
                        break;
                    case "Shard":
                        database = shardDB;
                        break;
                    case "World":
                        database = worldDB;
                        break;
                }
                // AllowUserVariables=true is what lets an update script guard its own DDL. MySqlConnector
                // otherwise reads `@name` as a command PARAMETER and throws "Parameter '@x' must be defined"
                // before the statement ever reaches the server, which rules out the SET/PREPARE/EXECUTE idiom
                // that stands in for MySQL 8.0's missing ADD COLUMN IF NOT EXISTS. Scripts need that guard
                // because this ledger (applied_updates.txt) lives in the build output directory while the
                // database is shared: a second checkout, a clean bin, or a restored backup all present a
                // database that already carries changes this ledger has never seen, and since #830 a script
                // that throws is (correctly) not recorded, so an unguarded ALTER then fails on EVERY boot and
                // blocks every later script in the directory. No script here is ever given parameters, so
                // reinterpreting `@name` costs nothing.
                var sqlConnect = new MySqlConnector.MySqlConnection($"server={host};port={port};user={username};password={password};database={database};DefaultCommandTimeout=120;SslMode=None;AllowPublicKeyRetrieval=true;AllowUserVariables=true");
                sqlDBFile = sqlDBFile.Replace("ace_auth", authDB);
                sqlDBFile = sqlDBFile.Replace("ace_shard", shardDB);
                sqlDBFile = sqlDBFile.Replace("ace_world", worldDB);
                var script = new MySqlConnector.MySqlCommand(sqlDBFile, sqlConnect);

                Console.Write($"Importing into {database} database on SQL server at {host}:{port} .... ");
                var applied = true;
                try
                {
                    ExecuteScript(script);
                    //Console.Write($" {count} database records affected ....");
                    Console.WriteLine(" complete!");
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    applied = false;
                    Console.WriteLine($" error!");
                    Console.WriteLine($" Unable to apply patch due to following exception: {ex}");
                    log.Error($"[DBPATCH] Failed to apply {file.Name} against {dbType} ({database}): {ex}");
                }

                CleanupConnection(sqlConnect);

                var remainingFileNames = GetPendingFileNames(orderedFiles.Skip(fileIndex + 1).Select(f => f.Name), appliedUpdates);
                var result = EvaluateDbPatchScriptResult(file.Name, remainingFileNames, applied);

                if (result.RecordedFileName != null)
                    File.AppendAllText(updatesFile, result.RecordedFileName + Environment.NewLine);

                if (result.Outcome == DbPatchOutcome.Stop)
                {
                    log.Error($"[DBPATCH] {file.Name} will be retried next boot; skipping {result.SkippedCount} remaining {dbType} update script(s) this boot.");
                    break;
                }
            }

            if (IsRunningInContainer && File.Exists(updatesFile))
                File.Copy(updatesFile, containerUpdatesFile, true);

            Console.WriteLine($"{dbType} update SQL scripts import complete!");
        }
    }
}
