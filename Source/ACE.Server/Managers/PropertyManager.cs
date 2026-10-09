using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Timers;

using log4net;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Server.MlTreasure;

namespace ACE.Server.Managers
{
    public static class PropertyManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // The one key WorldManager.HandleWorldClosedPropertyChanged cares about - see ModifyBool and DoWork.
        private const string WorldClosedKey = "world_closed";

        // caching internally to the server
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<bool>> CachedBooleanSettings = new ConcurrentDictionary<string, ConfigurationEntry<bool>>();
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<long>> CachedLongSettings = new ConcurrentDictionary<string, ConfigurationEntry<long>>();
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<double>> CachedDoubleSettings = new ConcurrentDictionary<string, ConfigurationEntry<double>>();

        private static long doubleSettingsEpoch;

        /// <summary>
        /// Bumped whenever a double setting can have changed value: ModifyDouble, and both merges of DB rows into the cache (boot Initialize and the DoWork / ResyncVariables reload). A reader that caches a snapshot of several doubles (PvpContextTunables) compares this to know it is stale.
        /// </summary>
        internal static long DoubleSettingsEpoch => System.Threading.Interlocked.Read(ref doubleSettingsEpoch);
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<string>> CachedStringSettings = new ConcurrentDictionary<string, ConfigurationEntry<string>>();

        private static Timer _workerThread;

        /// <summary>
        /// Guards every read-modify-write against the four Cached*Settings dictionaries. INVARIANT: no
        /// DB I/O ever runs while this lock is held, with no exceptions and no boot-order argument -
        /// every ShardConfig call (the Write*SnapshotToDB writes and the GetAll* reads, each on a
        /// ShardDbContext with EnableRetryOnFailure(10)) happens OUTSIDE it, in both Initialize and
        /// DoWork. In-game admin commands (@world, @modifybool, leaderboard-exempt, ip-limit - see
        /// PropertyAdminService.cs:84, all dispatched on the world thread) take this same lock via
        /// Modify*/Modify*Description, so a lock held across a DB round trip would stall the world thread
        /// for however long that round trip's retry sequence takes on a DB hiccup - a third review round
        /// caught this on the second round's fix, which held the lock across DoWork's whole write+reload,
        /// and a fourth review round caught the same shape still present in Initialize's own load, which
        /// had been left under the lock on a boot-order argument (nothing else can call Modify* yet) that
        /// the invariant no longer needs to lean on.
        ///
        /// DoWork runs three steps (see its own comment for the exact sequence): an in-memory snapshot
        /// under this lock, the DB writes and reads with NO lock held at all, then an in-memory merge back
        /// under this lock, keyed on a per-entry Version counter (ConfigurationEntry<T>.Version, bumped by
        /// Modify*/Modify*Description) rather than a DB round trip's timing. A Modify* call landing in the
        /// no-lock middle step is never lost: its Version bump means the version check leaves that entry
        /// Modified, so the write is simply retried next cycle instead of being reverted. The second
        /// round's fix (ShouldSkipReload, a Modified-flag check with no version) held the whole
        /// write+reload under one lock and closed its own race that way; this round keeps the two DB calls
        /// out of the lock instead, which is why a version counter is needed - "still Modified" alone can
        /// no longer distinguish "not written this cycle" from "written, then modified again after".
        ///
        /// Initialize follows the same two-step shape for its own one-time load (read the DB with no lock,
        /// then merge under this lock via the same MergeReloadedInPlace) but has nothing to snapshot or
        /// write, and passes applyOverModified: true because at that point every defaulted entry is already
        /// Modified from LoadDefaultProperties' own Modify* calls - see Initialize's own comment.
        ///
        /// GetBool/GetLong/GetDouble/GetString deliberately do NOT take this lock for their own
        /// cache-fill-on-miss: that fill only ever creates a pending (Modified=true) entry for a key not
        /// yet in the DB, and the reload's per-key merge only ever touches keys the DB result set already
        /// contains, so that specific write can never be the one a reload discards.
        ///
        /// The world_closed hook (WorldManager.HandleWorldClosedPropertyChanged) is fired OUTSIDE this
        /// lock everywhere it is called from (ModifyBool, DoWork) - old/new values are captured inside the
        /// lock and the hook runs after releasing it. WorldManager.Open/Close only broadcast to the audit
        /// channel and read PropertyManager.GetBool (which does not take this lock), so nothing reachable
        /// from the hook can try to re-acquire ConfigSync - had the hook run INSIDE the lock instead, and
        /// anything it reached tried to call back into a Modify* on another thread, that would deadlock.
        /// </summary>
        private static readonly object ConfigSync = new object();

        /// <summary>
        /// Initializes the PropertyManager.
        /// Run this only once per server instance.
        /// </summary>
        /// <param name="loadDefaultValues">Should we use the DefaultPropertyManager to load the default properties for keys?</param>
        public static void Initialize(bool loadDefaultValues = true)
        {
            if (loadDefaultValues)
                DefaultPropertyManager.LoadDefaultProperties();

            // Same two-step shape as DoWork's own reload half, so the ConfigSync invariant (no DB I/O
            // while it is held) is true everywhere, with no boot-order argument needed: read the DB with
            // no lock at all, then merge the rows into the cache under ConfigSync via the same
            // MergeReloadedInPlace DoWork's step 3 uses. There is nothing to snapshot/write here, but the
            // caches are NOT empty: if loadDefaultValues, LoadDefaultProperties ran a few lines above and
            // called ModifyBool/ModifyLong/ModifyDouble/ModifyString for every code default, each of which
            // leaves its entry Modified. That is why this merge passes applyOverModified: true - at boot
            // "Modified" means only "a code default was just loaded", never "a local value the DB does not
            // have yet", so the shard's persisted row must win. DoWork's live reload keeps the opposite
            // behaviour, where Modified really does mean an unwritten local change.
            //
            // Nothing can race this read: Program.cs calls PropertyManager.Initialize() (:360)
            // synchronously, well before WorldManager.Initialize() starts the world thread (:460) or
            // CommandManager.Initialize() (:528) - the only ways a Modify* becomes reachable - and
            // _workerThread (DoWork's own timer) is not even created until after this method returns.
            var loadedBools = DatabaseManager.ShardConfig.GetAllBools().Select(i => (i.Key, i.Value, i.Description)).ToList();
            var loadedLongs = DatabaseManager.ShardConfig.GetAllLongs().Select(i => (i.Key, i.Value, i.Description)).ToList();
            var loadedDoubles = DatabaseManager.ShardConfig.GetAllDoubles().Select(i => (i.Key, i.Value, i.Description)).ToList();
            var loadedStrings = DatabaseManager.ShardConfig.GetAllStrings().Select(i => (i.Key, i.Value, i.Description)).ToList();

            lock (ConfigSync)
            {
                try
                {
                    MergeReloadedInPlace(CachedBooleanSettings, loadedBools, applyOverModified: true);
                    MergeReloadedInPlace(CachedLongSettings, loadedLongs, applyOverModified: true);
                    MergeReloadedInPlace(CachedDoubleSettings, loadedDoubles, applyOverModified: true);
                    MergeReloadedInPlace(CachedStringSettings, loadedStrings, applyOverModified: true);
                }
                finally
                {
                    // bumped even if a merge threw partway: some doubles may already have changed
                    System.Threading.Interlocked.Increment(ref doubleSettingsEpoch);
                }
            }

            if (Program.IsRunningInContainer && !GetString("content_folder").Equals("/ace/Content"))
                ModifyString("content_folder", "/ace/Content");

            _workerThread = new Timer(300000);
            _workerThread.Elapsed += DoWork;
            _workerThread.AutoReset = true;
            _workerThread.Start();
        }


        /// <summary>
        /// Resyncs the variables with the database manually.
        /// Disables the timer so that the elapsed event cannot run during the update operation.
        /// </summary>
        public static void ResyncVariables()
        {
            _workerThread.Stop();

            DoWork(null, null);

            _workerThread.Start();
        }

        /// <summary>
        /// Stops updating the cached store from the database.
        /// </summary>
        public static void StopUpdating()
        {
            if (_workerThread != null)
                _workerThread.Stop();
        }


        /// <summary>
        /// Retrieves a boolean property from the cache or database
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="fallback">The value to return if the property cannot be found.</param>
        /// <param name="cacheFallback">Whether or not the fallback property should be cached.</param>
        /// <returns>A boolean value representing the property</returns>
        public static Property<bool> GetBool(string key, bool fallback = false, bool cacheFallback = true)
        {
            // first, check the cache. If the key exists in the cache, grab it regardless of its modified value
            // then, check the database. if the key exists in the database, grab it and cache it
            // finally, set it to a default of false.
            if (CachedBooleanSettings.ContainsKey(key))
                return new Property<bool>(CachedBooleanSettings[key].Item, CachedBooleanSettings[key].Description);

            var dbValue = DatabaseManager.ShardConfig.GetBool(key);

            bool useFallback = dbValue?.Value == null;

            var value = dbValue?.Value ?? fallback;

            if (!useFallback || cacheFallback)
                CachedBooleanSettings[key] = new ConfigurationEntry<bool>(useFallback, value, dbValue?.Description);

            return new Property<bool>(value, dbValue?.Description);
        }

        /// <summary>
        /// Modifies a boolean value in the cache and marks it for being synced on the next cycle.
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="newVal">The value to replace the old value with</param>
        /// <returns>true if the property was modified, false if no property exists with the given key</returns>
        public static bool ModifyBool(string key, bool newVal)
        {
            if (!DefaultPropertyManager.DefaultBooleanProperties.ContainsKey(key))
                return false;

            bool oldVal;
            lock (ConfigSync)
            {
                if (CachedBooleanSettings.ContainsKey(key))
                {
                    oldVal = CachedBooleanSettings[key].Item;
                    CachedBooleanSettings[key].Modify(newVal);
                }
                else
                {
                    oldVal = DefaultPropertyManager.DefaultBooleanProperties[key].Item;
                    CachedBooleanSettings[key] = new ConfigurationEntry<bool>(true, newVal, DefaultPropertyManager.DefaultBooleanProperties[key].Description);
                }
            }

            // Targeted live hook for this one key - see WorldManager.HandleWorldClosedPropertyChanged.
            // Fired outside the lock above - see ConfigSync's own doc comment for why.
            if (key == WorldClosedKey && oldVal != newVal)
                WorldManager.HandleWorldClosedPropertyChanged(oldVal, newVal);

            return true;
        }

        public static void ModifyBoolDescription(string key, string description)
        {
            lock (ConfigSync)
            {
                if (CachedBooleanSettings.ContainsKey(key))
                    CachedBooleanSettings[key].ModifyDescription(description);
                else
                    log.Warn($"Attempted to modify {key} which did not exist in the BOOL cache.");
            }
        }

        /// <summary>
        /// Retreives an integer property from the cache or database
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="fallback">The value to return if the property cannot be found.</param>
        /// <param name="cacheFallback">Whether or not the fallback property should be cached</param>
        /// <returns>An integer value representing the property</returns>
        public static Property<long> GetLong(string key, long fallback = 0, bool cacheFallback = true)
        {
            if (CachedLongSettings.ContainsKey(key))
                return new Property<long>(CachedLongSettings[key].Item, CachedLongSettings[key].Description);

            var dbValue = DatabaseManager.ShardConfig.GetLong(key);

            bool useFallback = dbValue?.Value == null;

            var value = dbValue?.Value ?? fallback;

            if (!useFallback || cacheFallback)
                CachedLongSettings[key] = new ConfigurationEntry<long>(useFallback, value, dbValue?.Description);

            return new Property<long>(value, dbValue?.Description);
        }

        /// <summary>
        /// Modifies an integer value in the cache and marks it for being synced on the next cycle.
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="newVal">The value to replace the old value with</param>
        /// <returns>true if the property was modified, false if no property exists with the given key</returns>
        public static bool ModifyLong(string key, long newVal)
        {
            if (!DefaultPropertyManager.DefaultLongProperties.ContainsKey(key))
                return false;

            lock (ConfigSync)
            {
                if (CachedLongSettings.ContainsKey(key))
                    CachedLongSettings[key].Modify(newVal);
                else
                    CachedLongSettings[key] = new ConfigurationEntry<long>(true, newVal, DefaultPropertyManager.DefaultLongProperties[key].Description);
            }
            return true;
        }

        public static void ModifyLongDescription(string key, string description)
        {
            lock (ConfigSync)
            {
                if (CachedLongSettings.ContainsKey(key))
                    CachedLongSettings[key].ModifyDescription(description);
                else
                    log.Warn($"Attempted to modify {key} which did not exist in the LONG cache.");
            }
        }

        /// <summary>
        /// Retrieves a float property from the cache or database
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="fallback">The value to return if the property cannot be found.</param>
        /// <param name="cacheFallback">Whether or not the fallpack property should be cached</param>
        /// <returns>A float value representing the property</returns>
        public static Property<double> GetDouble(string key, double fallback = 0.0f, bool cacheFallback = true)
        {
            if (CachedDoubleSettings.ContainsKey(key))
                return new Property<double>(CachedDoubleSettings[key].Item, CachedDoubleSettings[key].Description);

            var dbValue = DatabaseManager.ShardConfig.GetDouble(key);

            bool useFallback = dbValue?.Value == null;

            var value = dbValue?.Value ?? fallback;

            if (!useFallback || cacheFallback)
                CachedDoubleSettings[key] = new ConfigurationEntry<double>(useFallback, value, dbValue?.Description);

            return new Property<double>(value, dbValue?.Description);
        }

        /// <summary>
        /// Modifies a float value in the cache and marks it for being synced on the next cycle.
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="newVal">The value to replace the old value with</param>
        public static bool ModifyDouble(string key, double newVal, bool init = false)
        {
            if (!DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(key))
                return false;

            lock (ConfigSync)
            {
                if (CachedDoubleSettings.ContainsKey(key))
                    CachedDoubleSettings[key].Modify(newVal);
                else
                    CachedDoubleSettings[key] = new ConfigurationEntry<double>(true, newVal, DefaultPropertyManager.DefaultDoubleProperties[key].Description);

                System.Threading.Interlocked.Increment(ref doubleSettingsEpoch);
            }

            // Fired outside the lock above, same reasoning as ModifyBool's world_closed hook.
            if (!init)
            {
                switch (key)
                {
                    case "cantrip_drop_rate":
                        Factories.Tables.CantripChance.ApplyNumCantripsMod();
                        break;
                    case "minor_cantrip_drop_rate":
                    case "major_cantrip_drop_rate":
                    case "epic_cantrip_drop_rate":
                    case "legendary_cantrip_drop_rate":
                        Factories.Tables.CantripChance.ApplyCantripLevelsMod();
                        break;
                }
            }
            return true;
        }

        public static void ModifyDoubleDescription(string key, string description)
        {
            lock (ConfigSync)
            {
                if (CachedDoubleSettings.ContainsKey(key))
                    CachedDoubleSettings[key].ModifyDescription(description);
                else
                    log.Warn($"Attempted to modify the description of {key} which did not exist in the DOUBLE cache.");
            }
        }

        /// <summary>
        /// Retreives a string property from the cache or database
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="fallback">The value to return if the property cannot be found.</param>
        /// <param name="cacheFallback">Whether or not the fallback value will be cached.</param>
        /// <returns>A string value representing the property</returns>
        public static Property<string> GetString(string key, string fallback = "", bool cacheFallback = true)
        {
            if (CachedStringSettings.ContainsKey(key))
                return new Property<string>(CachedStringSettings[key].Item, CachedStringSettings[key].Description);

            var dbValue = DatabaseManager.ShardConfig.GetString(key);

            bool useFallback = dbValue?.Value == null;

            var value = dbValue?.Value ?? fallback;

            if (!useFallback || cacheFallback)
                CachedStringSettings[key] = new ConfigurationEntry<string>(useFallback, value, dbValue?.Description);

            return new Property<string>(value, dbValue?.Description);
        }

        /// <summary>
        /// Modifies a string value in the cache and marks it for being synced on the next cycle
        /// </summary>
        /// <param name="key">The string key for the property</param>
        /// <param name="newVal">The value to replace the old value with</param>
        /// <returns>true if the property was modified, false if no property exists with the given key</returns>
        public static bool ModifyString(string key, string newVal)
        {
            if (!DefaultPropertyManager.DefaultStringProperties.ContainsKey(key))
                return false;

            lock (ConfigSync)
            {
                if (CachedStringSettings.ContainsKey(key))
                    CachedStringSettings[key].Modify(newVal);
                else
                    CachedStringSettings[key] = new ConfigurationEntry<string>(true, newVal, DefaultPropertyManager.DefaultStringProperties[key].Description);
            }
            return true;
        }

        public static void ModifyStringDescription(string key, string description)
        {
            lock (ConfigSync)
            {
                if (CachedStringSettings.ContainsKey(key))
                    CachedStringSettings[key].ModifyDescription(description);
                else
                    log.Warn($"Attempted to modify {key} which did not exist in the STRING cache.");
            }
        }


        /// <summary>
        /// Step 1 helper (call under ConfigSync): snapshots every Modified entry of one cache as
        /// (key, value, description, version) - exactly what step 2 will attempt to write with no lock
        /// held, and the version step 3 checks before clearing Modified.
        /// </summary>
        internal static List<(string Key, T Value, string Description, int Version)> SnapshotModified<T>(ConcurrentDictionary<string, ConfigurationEntry<T>> cache)
        {
            var snapshot = new List<(string Key, T Value, string Description, int Version)>();

            foreach (var kvp in cache)
            {
                if (kvp.Value.Modified)
                    snapshot.Add((kvp.Key, kvp.Value.Item, kvp.Value.Description, kvp.Value.Version));
            }

            return snapshot;
        }

        /// <summary>
        /// Pure: whether the entry a key currently maps to (if any) is still exactly the version step 1
        /// snapshotted - i.e. whether step 2's write of that snapshot is safe to mark no-longer-pending. A
        /// version that has moved on means a Modify* landed during step 2's no-lock DB round trip, so this
        /// write is already stale; leaving Modified set is what makes the NEXT cycle retry with the newer
        /// value instead of the write being silently lost.
        /// </summary>
        internal static bool VersionUnchanged<T>(ConfigurationEntry<T> currentEntry, int snapshotVersion)
        {
            return currentEntry != null && currentEntry.Version == snapshotVersion;
        }

        /// <summary>
        /// Step 3 helper (call under ConfigSync): for every key step 2 successfully wrote, clears Modified
        /// only if VersionUnchanged since the step 1 snapshot. A key whose version moved stays Modified,
        /// so it is picked up by the next cycle's own step 1 snapshot instead of being dropped.
        /// </summary>
        internal static void ClearModifiedIfVersionUnchanged<T>(ConcurrentDictionary<string, ConfigurationEntry<T>> cache, List<(string Key, int Version)> written)
        {
            foreach (var (key, version) in written)
            {
                if (cache.TryGetValue(key, out var entry) && VersionUnchanged(entry, version))
                    entry.Modified = false;
            }
        }

        /// <summary>
        /// Pure: whether DoWork's LIVE reload (step 3) should skip overwriting a key's cached value with a
        /// freshly read DB row. A still-Modified entry there always means a value the DB does not have yet -
        /// reloading over it would silently drop it, with nothing left to ever retry that write. See
        /// PropertyManagerConfigSyncTests for this exact scenario, exercised end to end against
        /// ConfigurationEntry&lt;T&gt; with no DB involved.
        ///
        /// This is deliberately NOT consulted by Initialize's one-time boot merge, where "Modified" carries
        /// the opposite meaning: LoadDefaultProperties has just called Modify* for every code default, so
        /// every defaulted entry is Modified for that reason alone and nothing is actually pending. See
        /// MergeReloadedInPlace's applyOverModified parameter.
        /// </summary>
        internal static bool ShouldSkipReload<T>(ConfigurationEntry<T> cachedEntry)
        {
            return cachedEntry != null && cachedEntry.Modified;
        }

        /// <summary>
        /// Step 3 helper (call under ConfigSync): merges one type's freshly read DB rows into the cache IN
        /// PLACE via ConfigurationEntry&lt;T&gt;.ReloadFrom - never replacing an entry object a concurrent
        /// reader might hold a reference to - skipping any key ShouldSkipReload says is still pending, and
        /// inserting a new entry only for a key not cached at all yet.
        ///
        /// <paramref name="applyOverModified"/> is false for DoWork's live reload (the default: a Modified
        /// entry is an unwritten local change and must win) and true for Initialize's ONE-TIME boot merge
        /// only. At boot, every entry is Modified purely because LoadDefaultProperties just called Modify*
        /// for each code default, so consulting ShouldSkipReload there discards the shard's persisted
        /// config_properties_* row for every key that has a default and runs the code default instead - the
        /// precedence regression PR #1451 introduced. Applying the row and clearing Modified restores the
        /// pre-#1451 behaviour exactly: a key WITH a row holds the row's value and is not pending, a key
        /// with NO row keeps its default, stays Modified, and is seeded as a row by the first flush.
        /// </summary>
        internal static void MergeReloadedInPlace<T>(ConcurrentDictionary<string, ConfigurationEntry<T>> cache, List<(string Key, T Value, string Description)> reloaded, bool applyOverModified = false)
        {
            foreach (var (key, value, description) in reloaded)
            {
                cache.TryGetValue(key, out var existing);
                if (!applyOverModified && ShouldSkipReload(existing))
                    continue;

                if (existing != null)
                {
                    existing.ReloadFrom(value, description);

                    // Boot only: the row just applied IS the DB's value for this key, so nothing is pending
                    // for it any more and the first flush must not write the code default back over it.
                    // Version is deliberately left alone - it tracks Modify* calls for DoWork's write path,
                    // and no Modify* can have run between LoadDefaultProperties and this merge.
                    if (applyOverModified)
                        existing.Modified = false;
                }
                else
                    cache[key] = new ConfigurationEntry<T>(false, value, description);
            }
        }

        /// <summary>
        /// Step 2 (NO lock - see ConfigSync's own doc comment): writes a step 1 snapshot of modified bools
        /// to the DB, returning the (key, version) pairs that succeeded. A single key's failure is caught
        /// and logged rather than aborting the whole batch, so one bad row does not also block every other
        /// pending write this cycle; a failed key is simply absent from the returned list, so step 3 (via
        /// ClearModifiedIfVersionUnchanged) leaves it Modified for the next cycle to retry - the same
        /// "keep retrying" outcome the code had before, now with an actual log line instead of
        /// System.Timers.Timer's silent Elapsed-exception swallow.
        /// </summary>
        private static List<(string Key, int Version)> WriteBoolSnapshotToDB(List<(string Key, bool Value, string Description, int Version)> snapshot)
        {
            var written = new List<(string Key, int Version)>();

            foreach (var (key, value, description, version) in snapshot)
            {
                try
                {
                    // this probably should be upsert. This does 2 queries per modified datapoint.
                    // perhaps run a transaction to queue all the queries at once.
                    if (DatabaseManager.ShardConfig.BoolExists(key))
                        DatabaseManager.ShardConfig.SaveBool(new Database.Models.Shard.ConfigPropertiesBoolean { Key = key, Value = value, Description = description });
                    else
                        DatabaseManager.ShardConfig.AddBool(key, value, description);

                    written.Add((key, version));
                }
                catch (Exception ex)
                {
                    log.Error($"PropertyManager DoWork: failed to write bool config property '{key}' to the DB this cycle; it stays pending and will be retried next cycle.", ex);
                }
            }

            return written;
        }

        /// <summary>
        /// See WriteBoolSnapshotToDB's doc comment - identical shape for longs.
        /// </summary>
        private static List<(string Key, int Version)> WriteLongSnapshotToDB(List<(string Key, long Value, string Description, int Version)> snapshot)
        {
            var written = new List<(string Key, int Version)>();

            foreach (var (key, value, description, version) in snapshot)
            {
                try
                {
                    if (DatabaseManager.ShardConfig.LongExists(key))
                        DatabaseManager.ShardConfig.SaveLong(new Database.Models.Shard.ConfigPropertiesLong { Key = key, Value = value, Description = description });
                    else
                        DatabaseManager.ShardConfig.AddLong(key, value, description);

                    written.Add((key, version));
                }
                catch (Exception ex)
                {
                    log.Error($"PropertyManager DoWork: failed to write long config property '{key}' to the DB this cycle; it stays pending and will be retried next cycle.", ex);
                }
            }

            return written;
        }

        /// <summary>
        /// See WriteBoolSnapshotToDB's doc comment - identical shape for doubles.
        /// </summary>
        private static List<(string Key, int Version)> WriteDoubleSnapshotToDB(List<(string Key, double Value, string Description, int Version)> snapshot)
        {
            var written = new List<(string Key, int Version)>();

            foreach (var (key, value, description, version) in snapshot)
            {
                try
                {
                    if (DatabaseManager.ShardConfig.DoubleExists(key))
                        DatabaseManager.ShardConfig.SaveDouble(new Database.Models.Shard.ConfigPropertiesDouble { Key = key, Value = value, Description = description });
                    else
                        DatabaseManager.ShardConfig.AddDouble(key, value, description);

                    written.Add((key, version));
                }
                catch (Exception ex)
                {
                    log.Error($"PropertyManager DoWork: failed to write double config property '{key}' to the DB this cycle; it stays pending and will be retried next cycle.", ex);
                }
            }

            return written;
        }

        /// <summary>
        /// See WriteBoolSnapshotToDB's doc comment - identical shape for strings.
        /// </summary>
        private static List<(string Key, int Version)> WriteStringSnapshotToDB(List<(string Key, string Value, string Description, int Version)> snapshot)
        {
            var written = new List<(string Key, int Version)>();

            foreach (var (key, value, description, version) in snapshot)
            {
                try
                {
                    if (DatabaseManager.ShardConfig.StringExists(key))
                        DatabaseManager.ShardConfig.SaveString(new Database.Models.Shard.ConfigPropertiesString { Key = key, Value = value, Description = description });
                    else
                        DatabaseManager.ShardConfig.AddString(key, value, description);

                    written.Add((key, version));
                }
                catch (Exception ex)
                {
                    log.Error($"PropertyManager DoWork: failed to write string config property '{key}' to the DB this cycle; it stays pending and will be retried next cycle.", ex);
                }
            }

            return written;
        }

        private static void DoWork(Object source, ElapsedEventArgs e)
        {
            var startTime = DateTime.UtcNow;

            // STEP 1 (under ConfigSync, in-memory only - no DB I/O while holding the lock, per its
            // invariant): snapshot every Modified entry of all four types, and capture world_closed's
            // value before this cycle touches anything.
            bool worldClosedBefore;
            List<(string Key, bool Value, string Description, int Version)> boolSnapshot;
            List<(string Key, long Value, string Description, int Version)> longSnapshot;
            List<(string Key, double Value, string Description, int Version)> doubleSnapshot;
            List<(string Key, string Value, string Description, int Version)> stringSnapshot;

            lock (ConfigSync)
            {
                worldClosedBefore = CachedBooleanSettings.TryGetValue(WorldClosedKey, out var worldClosedEntryBefore) && worldClosedEntryBefore.Item;

                boolSnapshot = SnapshotModified(CachedBooleanSettings);
                longSnapshot = SnapshotModified(CachedLongSettings);
                doubleSnapshot = SnapshotModified(CachedDoubleSettings);
                stringSnapshot = SnapshotModified(CachedStringSettings);
            }

            // STEP 2 (NO lock at all): the actual DB round trips - first write out step 1's snapshot,
            // then read every row back. Nothing here touches ConfigSync, so an in-game admin command
            // (@world, @modifybool, leaderboard-exempt, ip-limit - PropertyAdminService.cs:84, dispatched
            // on the world thread) taking that lock via a Modify* never waits on a DB hiccup's retry
            // sequence; it only ever waits on the brief in-memory step 1/step 3 sections below.
            var boolWritten = WriteBoolSnapshotToDB(boolSnapshot);
            var longWritten = WriteLongSnapshotToDB(longSnapshot);
            var doubleWritten = WriteDoubleSnapshotToDB(doubleSnapshot);
            var stringWritten = WriteStringSnapshotToDB(stringSnapshot);

            var reloadedBools = DatabaseManager.ShardConfig.GetAllBools().Select(i => (i.Key, i.Value, i.Description)).ToList();
            var reloadedLongs = DatabaseManager.ShardConfig.GetAllLongs().Select(i => (i.Key, i.Value, i.Description)).ToList();
            var reloadedDoubles = DatabaseManager.ShardConfig.GetAllDoubles().Select(i => (i.Key, i.Value, i.Description)).ToList();
            var reloadedStrings = DatabaseManager.ShardConfig.GetAllStrings().Select(i => (i.Key, i.Value, i.Description)).ToList();

            // STEP 3 (under ConfigSync again, in-memory only): clear Modified for exactly the keys both
            // successfully written above AND still at the version step 1 snapshotted (VersionUnchanged) -
            // a key whose version moved in step 2's no-lock window stays Modified, to be retried next
            // cycle rather than reverted. Then merge every reloaded row in place (MergeReloadedInPlace),
            // which itself skips any key that is (now) Modified. Order matters: the version-based clear
            // must run first, so the merge sees this cycle's own write reflected in Modified before it
            // decides whether to apply the DB's row on top of it.
            //
            // world_closed's after value is captured inside this same lock so the comparison is
            // consistent with the merge it is watching, but the hook itself
            // (WorldManager.HandleWorldClosedPropertyChanged) is deliberately called AFTER releasing the
            // lock below - never call into WorldManager while holding ConfigSync.
            //
            // NOT detected here: deleting the world_closed row outright. GetAllBools() simply stops
            // returning that key, the merge above never touches it, and the cache keeps whatever value it
            // last held. This is deliberate - the merge does not delete cache entries for deletions in
            // general, not just for this key - so a deleted row is silently a no-op here, not a live
            // switch to "unset".
            bool worldClosedAfter;

            lock (ConfigSync)
            {
                ClearModifiedIfVersionUnchanged(CachedBooleanSettings, boolWritten);
                ClearModifiedIfVersionUnchanged(CachedLongSettings, longWritten);
                ClearModifiedIfVersionUnchanged(CachedDoubleSettings, doubleWritten);
                ClearModifiedIfVersionUnchanged(CachedStringSettings, stringWritten);

                try
                {
                    MergeReloadedInPlace(CachedBooleanSettings, reloadedBools);
                    MergeReloadedInPlace(CachedLongSettings, reloadedLongs);
                    MergeReloadedInPlace(CachedDoubleSettings, reloadedDoubles);
                    MergeReloadedInPlace(CachedStringSettings, reloadedStrings);
                }
                finally
                {
                    // bumped even if a merge threw partway: some doubles may already have changed
                    System.Threading.Interlocked.Increment(ref doubleSettingsEpoch);
                }

                worldClosedAfter = CachedBooleanSettings.TryGetValue(WorldClosedKey, out var worldClosedEntryAfter) && worldClosedEntryAfter.Item;
            }

            if (worldClosedAfter != worldClosedBefore)
                WorldManager.HandleWorldClosedPropertyChanged(worldClosedBefore, worldClosedAfter);

            log.DebugFormat("PropertyManager DoWork took {0:N0} ms", (DateTime.UtcNow - startTime).TotalMilliseconds);
        }
        public static string ListProperties()
        {
            string props = "Boolean properties:\n";
            foreach (var item in DefaultPropertyManager.DefaultBooleanProperties)
                props += string.Format("\t{0}: {1} (current is {2}, default is {3})\n", item.Key, item.Value.Description, GetBool(item.Key).Item, item.Value.Item);

            props += "\nLong properties:\n";
            foreach (var item in DefaultPropertyManager.DefaultLongProperties)
                props += string.Format("\t{0}: {1} (current is {2}, default is {3})\n", item.Key, item.Value.Description, GetLong(item.Key).Item, item.Value.Item);

            props += "\nDouble properties:\n";
            foreach (var item in DefaultPropertyManager.DefaultDoubleProperties)
                props += string.Format("\t{0}: {1} (current is {2}, default is {3})\n", item.Key, item.Value.Description, GetDouble(item.Key).Item, item.Value.Item);

            props += "\nString properties:\n";
            foreach (var item in DefaultPropertyManager.DefaultStringProperties)
                props += string.Format("\t{0}: {1} (default is hidden)\n", item.Key, item.Value.Description);

            return props;
        }

        /// <summary>
        /// Every bool/long/double/string property whose key starts with <paramref name="prefix"/> (ordinal,
        /// case-sensitive - property keys are always lower_snake_case), across all four Default*Properties
        /// dictionaries, sorted by key. Used by "/worldevent properties" so an admin can see every
        /// world_events_ dial without reflecting over the dictionaries by hand.
        ///
        /// "current" reads through GetBool/GetLong/GetDouble/GetString (the same lookup ListProperties
        /// uses), so calling this also populates the cache for any key not yet touched this run - the same
        /// side effect GetX already has everywhere else. A read that throws - a unit test has no shard
        /// config at all, so DatabaseManager.ShardConfig NREs on the first uncached key - reads as "current
        /// equals default", the same safe direction every other PropertyManager-reading seam in World
        /// Events falls back to (see WorldEventOverrides, WorldEventRosterSelector.DialSource). "type" is
        /// "bool"/"long"/"double"/"string".
        /// </summary>
        public static IEnumerable<(string key, string type, string current, string @default, string description)> EnumerateWithPrefix(string prefix)
        {
            prefix ??= "";

            var rows = new List<(string key, string type, string current, string @default, string description)>();

            foreach (var item in DefaultPropertyManager.DefaultBooleanProperties)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var defaultText = FormatBool(item.Value.Item);
                    var currentText = SafeCurrent(item.Key, defaultText, k => FormatBool(GetBool(k).Item));
                    rows.Add((item.Key, "bool", currentText, defaultText, item.Value.Description));
                }
            }

            foreach (var item in DefaultPropertyManager.DefaultLongProperties)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var defaultText = FormatLong(item.Value.Item);
                    var currentText = SafeCurrent(item.Key, defaultText, k => FormatLong(GetLong(k).Item));
                    rows.Add((item.Key, "long", currentText, defaultText, item.Value.Description));
                }
            }

            foreach (var item in DefaultPropertyManager.DefaultDoubleProperties)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var defaultText = FormatDouble(item.Value.Item);
                    var currentText = SafeCurrent(item.Key, defaultText, k => FormatDouble(GetDouble(k).Item));
                    rows.Add((item.Key, "double", currentText, defaultText, item.Value.Description));
                }
            }

            foreach (var item in DefaultPropertyManager.DefaultStringProperties)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var defaultText = item.Value.Item;
                    var currentText = SafeCurrent(item.Key, defaultText, ReadStringCurrent);
                    rows.Add((item.Key, "string", currentText, defaultText, item.Value.Description));
                }
            }

            return rows.OrderBy(r => r.key, StringComparer.Ordinal);

            string ReadStringCurrent(string k) => GetString(k).Item;
        }

        /// <summary>Shared with PropertyAdminService.FormatCurrent (PLAN-P2.md section 2), so GET and the web POST render exactly the same text and a stale-value comparison can never false-409.</summary>
        internal static string FormatBool(bool value) => value.ToString();

        /// <summary>Explicitly invariant - today's EnumerateWithPrefix used a bare ToString(), which is culture-dependent; PLAN-P2.md ruling 6 makes this explicit so a POST's expected_current comparison can never differ from GET's rendering.</summary>
        internal static string FormatLong(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        internal static string FormatDouble(double value) => value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Runs <paramref name="read"/>(key); on any exception, logs and returns <paramref name="fallback"/>. See <see cref="EnumerateWithPrefix"/>.</summary>
        private static string SafeCurrent(string key, string fallback, Func<string, string> read)
        {
            try
            {
                return read(key);
            }
            catch (Exception ex)
            {
                log.Error($"could not read current value of property {key} while enumerating; reporting the default", ex);
                return fallback;
            }
        }
    }

    public struct Property<T>
    {
        public Property(T item, string description) : this()
        {
            Item = item;
            Description = description;
        }

        public T Item { get; }
        public string Description { get; }
    }

    class ConfigurationEntry<T>
    {
        public bool Modified;
        public T Item;
        public string Description;

        /// <summary>
        /// Bumped by Modify()/ModifyDescription() only (always under PropertyManager.ConfigSync - both
        /// callers are Modify*/Modify*Description). DoWork's step 1 snapshots this alongside the value it
        /// is about to write with no lock held; step 3 clears Modified for a written key only if this is
        /// still the same version, so a Modify* landing in that no-lock window (which bumps this again)
        /// is never silently reverted - see PropertyManager.ConfigSync's own doc comment.
        /// </summary>
        public int Version;

        public ConfigurationEntry(bool modified, T item)
        {
            Modified = modified;
            Item = item;
        }

        public ConfigurationEntry(bool modified, T item, string description)
        {
            Modified = modified;
            Item = item;
            Description = description;
        }

        public void Modify(T item)
        {
            Item = item;
            Modified = true;
            Version++;
        }

        public void ModifyDescription(string description)
        {
            Description = description;
            Modified = true;
            Version++;
        }

        /// <summary>
        /// Applies a freshly read DB row to this entry IN PLACE - never replaces the object, so a
        /// concurrent reader holding this same reference (e.g. mid-ModifyBool, between its own lock's
        /// ContainsKey check and the mutation that follows) always sees a consistent, live entry rather
        /// than one that got swapped out from under it. Deliberately does NOT touch Modified or Version:
        /// this is the DB catching the cache up to a value already agreed on, never a local change.
        ///
        /// Two callers, both in PropertyManager.MergeReloadedInPlace. DoWork's live reload calls this only
        /// after confirming the entry is not Modified (ShouldSkipReload). Initialize's boot merge calls it
        /// over a Modified entry on purpose - at boot the flag only records that LoadDefaultProperties just
        /// loaded a code default - and clears Modified itself immediately afterwards, since the row just
        /// applied is the DB's own value and nothing is left pending. Those are the only two shapes this is
        /// sanctioned for; it still must never be used to discard a real unwritten local change.
        /// </summary>
        public void ReloadFrom(T item, string description)
        {
            Item = item;
            Description = description;
        }

        public override string ToString()
        {
            return Item + " " + Modified;
        }
    }

    public static class DefaultPropertyManager
    {
        private static ReadOnlyDictionary<A,V> DictOf<A, V>()
        {
            return new ReadOnlyDictionary<A, V>(new Dictionary<A, V>());
        }

        /// <summary>
        /// Adds the generated PvP context tuning keys (pvp_{arena|bg}_{category}_{stat}, all 1.0) to the double defaults. They are generated from PvpContextTuning's token tables, never typed per key, so the key list has exactly one source.
        /// </summary>
        private static ReadOnlyDictionary<string, Property<double>> WithPvpContextTuning(ReadOnlyDictionary<string, Property<double>> baseline)
        {
            var all = new Dictionary<string, Property<double>>(baseline);

            foreach (var (key, description) in ACE.Server.Pvp.Rules.PvpContextTuning.RegisteredDefaults())
                all.Add(key, new Property<double>(ACE.Server.Pvp.Rules.PvpContextTuning.NeutralValue, description));

            return new ReadOnlyDictionary<string, Property<double>>(all);
        }

        private static ReadOnlyDictionary<A, V> DictOf<A, V>(params (A, V)[] pairs)
        {
            return new ReadOnlyDictionary<A, V>(pairs.ToDictionary
            (
                tup => tup.Item1,
                tup => tup.Item2
            ));
        }

        public static void LoadDefaultProperties()
        {
            // Place any default properties to load in here

            //bool
            foreach (var item in DefaultBooleanProperties)
                PropertyManager.ModifyBool(item.Key, item.Value.Item);

            //float
            foreach (var item in DefaultDoubleProperties)
                PropertyManager.ModifyDouble(item.Key, item.Value.Item, true);

            //int
            foreach (var item in DefaultLongProperties)
                PropertyManager.ModifyLong(item.Key, item.Value.Item);

            //string
            foreach (var item in DefaultStringProperties)
                PropertyManager.ModifyString(item.Key, item.Value.Item);
        }

        // ==================================================================================
        // To change these values for the server,
        // please use the /modifybool, /modifylong, /modifydouble, and /modifystring commands
        // ==================================================================================

        public static readonly ReadOnlyDictionary<string, Property<bool>> DefaultBooleanProperties =
            DictOf(
                ("account_creation_enabled", new Property<bool>(true, "WaffleACE: if FALSE, no new account is auto-created at login - an unknown account name is rejected with AccountDoesntExist instead. Existing accounts and their characters log in normally. This is a live toggle (/modifybool account_creation_enabled false) that needs no restart; it is ANDed with the restart-only Config.js Server.Accounts.AllowAutoAccountCreation, so it can only tighten that setting, never loosen it")),
                ("account_login_boots_in_use", new Property<bool>(true, "if FALSE, oldest connection to account is not booted when new connection occurs")),
                ("account_vault_class_storage", new Property<bool>(true, "WaffleACE Mule Vendor: master switch for the counted item-CLASS storage tier. On: a deposited item that is provably rebuildable from its weenie plus a small whitelist of per-instance properties (salvage bags, in practice) is stored as a COUNT on one account_vault_class row instead of as its own biota, and already-stored biotas are folded into those rows a few at a time as a vault loads. Off: nothing new collapses and nothing folds, every deposit keeps its biota exactly as before, and class rows that already exist stay readable and withdrawable - turning it off is a stop, never a rollback")),
                ("advanced_combat_pets", new Property<bool>(false, "(non-retail function) If enabled, Combat Pets can cast spells")),
                ("advocate_fane_auto_bestow", new Property<bool>(false, "If enabled, Advocate Fane will automatically bestow new advocates to advocate_fane_auto_bestow_level")),
                ("aetheria_heal_color", new Property<bool>(false, "If enabled, changes the aetheria healing over time messages from the default retail red color to green")),
                ("allow_combat_mode_crafting", new Property<bool>(false, "If enabled, allows players to do crafting (recipes) from all stances. Forces players to NonCombat first, then continues to recipe action.")),
                ("allow_door_hold", new Property<bool>(true, "enables retail behavior where standing on a door while it is closing keeps the door as ethereal until it is free from collisions, effectively holding the door open for other players")),
                ("anti_blink_debug", new Property<bool>(false, "(non-retail function) If enabled, logs every anti-blink door check, including the ones that pass. Very noisy - for tuning anti_blink_door_width and anti_blink_z_height_limit only")),
                ("anti_blink_door_detection", new Property<bool>(false, "(non-retail function) If enabled, rejects player movement whose path crosses a closed, non-ethereal door, rubber-bands the player back to their last valid position, and records the attempt on the audit channel. Counters client plugins that delete a door from the client's own world and walk through it")),
                ("allow_fast_chug", new Property<bool>(true, "enables retail behavior where a player can consume food and drink faster than normal by breaking animation")),
                ("allow_jump_loot", new Property<bool>(true, "enables retail behavior where a player can quickly loot items while jumping, bypassing the 'crouch down' animation")),
                ("allow_negative_dispel_resist", new Property<bool>(true, "enables retail behavior where #-# negative dispels can be resisted")),
                ("allow_negative_rating_curve", new Property<bool>(true, "enables retail behavior where negative DRR from void dots didn't switch to the reverse rating formula, resulting in a possibly unintended curve that quickly ramps up as -rating goes down, eventually approaching infinity / divide by 0 for -100 rating. less than -100 rating would produce negative numbers.")),
                ("allow_pkl_bump", new Property<bool>(true, "enables retail behavior where /pkl checks for entry collisions, bumping the player position over if standing on another PKLite. This effectively enables /pkl door skipping from retail")),
                ("allow_summoning_killtask_multicredit", new Property<bool>(true, "enables retail behavior where a summoner can get multiple killtask credits from a monster")),
                ("assess_creature_mod", new Property<bool>(false, "(non-retail function) If enabled, re-enables former skill formula, when assess creature skill is not trained or spec'ed")),
                ("attribute_augmentation_safety_cap", new Property<bool>(true, "if TRUE players are not able to use attribute augmentations if the innate value of the target attribute is >= 96. All normal restrictions to these augmentations still apply.")),
                ("chat_disable_general", new Property<bool>(false, "disable general global chat channel")),
                ("chat_disable_lfg", new Property<bool>(false, "disable lfg global chat channel")),
                ("chat_disable_olthoi", new Property<bool>(false, "disable olthoi global chat channel")),
                ("chat_disable_roleplay", new Property<bool>(false, "disable roleplay global chat channel")),
                ("chat_disable_trade", new Property<bool>(false, "disable trade global chat channel")),
                ("chat_echo_only", new Property<bool>(false, "global chat returns to sender only")),
                ("chat_echo_reject", new Property<bool>(false, "global chat returns to sender on reject")),
                ("chat_inform_reject", new Property<bool>(true, "global chat informs sender on reason for reject")),
                ("chat_log_abuse", new Property<bool>(false, "log abuse chat")),
                ("chat_log_admin", new Property<bool>(false, "log admin chat")),
                ("chat_log_advocate", new Property<bool>(false, "log advocate chat")),
                ("chat_log_allegiance", new Property<bool>(false, "log allegiance chat")),
                ("chat_log_audit", new Property<bool>(true, "log audit chat")),
                ("chat_log_debug", new Property<bool>(false, "log debug chat")),
                ("chat_log_fellow", new Property<bool>(false, "log fellow chat")),
                ("chat_log_general", new Property<bool>(false, "log general chat")),
                ("chat_log_global", new Property<bool>(false, "log global broadcasts")),
                ("chat_log_help", new Property<bool>(false, "log help chat")),
                ("chat_log_lfg", new Property<bool>(false, "log LFG chat")),
                ("chat_log_olthoi", new Property<bool>(false, "log olthoi chat")),
                ("chat_log_qa", new Property<bool>(false, "log QA chat")),
                ("chat_log_roleplay", new Property<bool>(false, "log roleplay chat")),
                ("chat_log_sentinel", new Property<bool>(false, "log sentinel chat")),
                ("chat_log_society", new Property<bool>(false, "log society chat")),
                ("chat_log_trade", new Property<bool>(false, "log trade chat")),
                ("chat_log_townchans", new Property<bool>(false, "log advocate town chat")),
                ("chat_requires_account_15days", new Property<bool>(false, "global chat privileges requires accounts to be 15 days or older")),
                ("discord_relay_enabled", new Property<bool>(false, "if TRUE, relay General chat, Trade chat, the staff Audit channel and World Events announcements to their per-channel Discord webhooks (discord_webhook_url_general / discord_webhook_url_trade / discord_webhook_url_audit / discord_webhook_url_events). Overridden at startup by the ACE_DISCORD_RELAY_ENABLED environment variable when set")),
                ("chess_enabled", new Property<bool>(true, "if FALSE then chess will be disabled")),
                ("class_abilities_enabled", new Property<bool>(true, "(non-retail function) enables the class ability system: the /abilities command, class ability point items, and all class ability combat effects")),
                ("class_ability_overhaul_respec_sweep_enabled", new Property<bool>(false, "(non-retail function) DEPLOYMENT MIGRATION, ships FALSE: when TRUE, each character's next login runs the one-shot Class Ability overhaul respec - every learned class ability is unlearned, AvailableClassAbilityPoints is set to TotalClassAbilityPointsEarned (lifetime earned is never touched), every stored facet build's ability set is emptied, and every unused class ability training token in the pack is consumed. Guarded per character by PropertyInt 9067 ClassAbilityOverhaulRespecDone, so a character already swept is untouched however often this is switched on. Setting it back to FALSE stops the sweep mid-deployment with no redeploy; characters not yet swept are simply left for a later login")),
                ("monster_effects_enabled", new Property<bool>(true, "(non-retail function) enables the monster combat effect system: effects authored on a monster weenie in PropertyString 9015 MonsterCombatEffects. Read at the dispatch sites, not at creature construction, so switching it back on takes effect without a relog or a landblock reset")),
                ("enlightenment_broadcast_enabled", new Property<bool>(true, "(non-retail function) if TRUE, each successful enlightenment is announced with a server-wide broadcast")),
                ("equipment_mods_enabled", new Property<bool>(true, "(non-retail function) enables the equipment mod system: applying salvage-based mods to eligible gear, their combat effects, and their appraisal display. Ships enabled as standard content. While FALSE, stored mod values are inert and invisible")),
                ("weapon_mods_enabled", new Property<bool>(true, "(non-retail function) THE SINGLE MASTER SWITCH for the whole weapon mod system: the Tourmaline reroll and the Amethyst swap on weapons, their appraisal display, AND every Tier B combat effect (the leeches, Ambush, Quickening, Overload and Second Wind). Ships enabled as standard content. While FALSE both salvage bags fall through to ordinary tinkering, no Tier B modifier can be rolled by any pool, and every Tier B combat effect reads zero - including on a weapon that already carries a record from when it was TRUE")),
                ("weapon_mod_guarantee_damage_special", new Property<bool>(true, "(non-retail function) weapon mods: if TRUE, the FIRST special of a rolled set is drawn from the damage-relevant subset of the class pool (the rows flagged AffectsSingleTargetDamage) instead of the whole pool, so a set is never entirely utility. Every later special still comes from the full pool. Best-effort: if a class has no damage-relevant rows left to draw, the full pool is used. RETUNED FROM FALSE TO TRUE on 2026-08-06 as part of the v3 magnitude pass")),
                ("serpentine_reroll_enabled", new Property<bool>(true, "(non-retail function) enables the Serpentine spell reroll: a Bag or Hammer of Serpentine salvage used on a piece of gear replaces its non-cantrip item enchantments with different ones of the SAME LEVEL. Defaults TRUE - this is standard player content, unlike the equipment/weapon mod systems. While FALSE the Serpentine salvage falls through to ordinary tinkering")),
                ("salvage_forge_enabled", new Property<bool>(true, "(non-retail function) enables the salvage forge: a Hollow Hammer (wcid 1002650) used on a full salvage bag consumes that bag plus enough other full bags of the same material - salvage_forge_skill_for_9/_8/_7 set the price band - and the Hollow Hammer itself, to create the material's existing 10-charge Hammer. Defaults TRUE - this is standard player content, like serpentine_reroll_enabled. While FALSE a Hollow Hammer falls through to ordinary recipe handling, which has no recipe for it and simply refuses the use")),
                ("workmanship_refire_enabled", new Property<bool>(true, "(non-retail function) enables the Refire Forge (wcid 1002750, Marketplace, PropertyBool.WorkmanshipRefireForge): giving it a piece of equipment offers to reroll that item's ItemWorkmanship on the TIER 6 chance table (WorkmanshipChance.Roll(6)) for 10 Trade Notes (250,000), pack-first then banked pyreals. Defaults TRUE - this is standard player content. While FALSE the forge refuses every give with a plain system-chat line instead of offering the confirmation")),
                ("use_cloak_proc_custom_scale", new Property<bool>(false, "If TRUE, the calculation for cloak procs will be based upon the values set by the server oeprator.")),
                ("client_movement_formula", new Property<bool>(false, "If enabled, server uses DoMotion/StopMotion self-client movement methods instead of apply_raw_movement")),
                ("container_opener_name", new Property<bool>(false, "If enabled, when a player tries to open a container that is already in use by someone else, replaces 'someone else' in the message with the actual name of the player")),
                ("corpse_decay_tick_logging", new Property<bool>(false, "If ENABLED then player corpse ticks will be logged")),
                ("corpse_destroy_pyreals", new Property<bool>(true, "If FALSE then pyreals will not be completely destroyed on player death")),
                ("craft_exact_msg", new Property<bool>(false, "If TRUE, and player has crafting chance of success dialog enabled, shows them an additional message in their chat window with exact %")),
                ("creature_name_check", new Property<bool>(true, "if enabled, creature names in world database restricts player names during character creation")),
                ("creatures_drop_createlist_wield", new Property<bool>(false, "If FALSE then Wielded items in CreateList will not drop. Retail defaulted to TRUE but there are currently data errors")),
                ("custom_aug_brokers_enabled", new Property<bool>(true, "Custom Dreamweave Augmentations: kill switch for the Fi / Bo / Nacci broker NPCs. When FALSE every broker politely refuses any give and no gems change hands. Does not affect augmentations already bought")),
                ("reward_claim_once_enabled", new Property<bool>(true, "Kill switch for the ClaimRewardOnce emote, the once-per-account and once-per-IP claim gate on the rewards in RewardClaimAllowlist (the five Assay Row hammers and every Proving Grounds herald tier). Defaults TRUE. While FALSE the emote makes no database call and records nothing: every claim branches straight to TestSuccess, so each reward is gated only by its per-character quest stamp again. Claims already recorded stay recorded and apply again when switched back on. (non-retail function)")),
                ("emote_give_preflight", new Property<bool>(false, "(non-retail) refuse an NPC turn-in up front when the forecast reward would not fit, instead of consuming the item and losing the reward")),
                ("fast_tick_all_players", new Property<bool>(false, "WaffleACE: if TRUE, every player runs the full server-side physics simulation (Player.FastTick) that upstream ACE reserves for PK/PKLite players. This is what gives PKs faster spell release (no legacy pre-windup rotate delay), retail fast-chug, melee stick-to-target and physics-driven jump validation. Costs more CPU per moving player. Defaults FALSE. This is now only a DEFAULT for players who have not chosen via /fasttick, and players who opt out run the legacy movement path, including the looser legacy airborne (IsJumping) check; an explicit player choice (on or off) overrides it, and PK/PKLite players are always fast tick. Read live, so flipping it mid-session takes effect without a relog; a player mid-cast or mid-drink at the moment of the flip may have that one action glitch.")),
                ("fastbuff", new Property<bool>(true, "If TRUE, enables the fast buffing trick from retail.")),
                ("fellow_busy_no_recruit", new Property<bool>(true, "if FALSE, fellows can be recruited while they are busy, different from retail")),
                ("fellow_kt_killer", new Property<bool>(true, "if FALSE, fellowship kill tasks will share with the fellowship, even if the killer doesn't have the quest")),
                ("fellowship_level_restrictions", new Property<bool>(true, "WaffleACE: if TRUE, a fellowship shares evenly only when every member is at or above 'fellowship_even_share_level'; otherwise XP is shared across the whole fellowship weighted by each present member's XP-to-next-level. Set FALSE to always share evenly regardless of level spread.")),
                ("fellowship_leech_check_default", new Property<bool>(false,"WaffleACE: default state of leech management for newly created fellowships. When enabled, members who do not contribute XP to the fellowship within the timeout window are auto-ejected. Leaders can toggle per-fellowship with /fship noleech on|off.")),
                ("fellowship_xp_tell_invite_enabled", new Property<bool>(true, "WaffleACE: if TRUE, sending a tell whose body is exactly \"xp\" (the standard vtank convention for requesting a fellowship invite) to a player who is in a fellowship, while you are not, free-joins you into their fellowship, through the same guards as /fship join.")),
                ("fix_chest_missing_inventory_window", new Property<bool>(false, "Very non-standard fix. This fixes an acclient bug where unlocking a chest, and then quickly opening it before the client has received the Locked=false update from server can result in the chest opening, but with the chest inventory window not displaying. Bug has a higher chance of appearing with more network latency.")),
                ("gateway_ties_summonable", new Property<bool>(true, "if disabled, players cannot summon ties from gateways. defaults to enabled, as in retail")),
                ("offline_bonus_enabled", new Property<bool>(true, "if TRUE, characters accrue 'offline bonus time' 1:1 while logged out (capped by offline_bonus_max_seconds) that boosts their own combat XP/Luminance by offline_bonus_multiplier while banked time remains")),
                ("quest_stamps_enabled", new Property<bool>(true, "WaffleACE: if TRUE, characters earn quest stamps (distinct quests completed, account-wide total) redeemable at the Quest Stamp Registrar NPC; disabling stops counting, login backfill and /quests, and the NPC reads 0.")),
                ("alt_character_bonus_enabled", new Property<bool>(true, "if TRUE, a character earns alt_character_bonus_multiplier extra leveling XP while its account has a further-along (higher enlightenment+level) character, until it catches up. Multiplicative with the offline bonus and aug/gear XP modifiers.")),
                ("gearknight_core_plating", new Property<bool>(true, "if disabled, Gear Knight players are not required to use core plating devices for armor and clothing. defaults to enabled, as in retail")),
                ("house_15day_account", new Property<bool>(true, "if disabled, houses can be purchased with accounts created less than 15 days old")),
                ("house_30day_cooldown", new Property<bool>(true, "if disabled, houses can be purchased without waiting 30 days between each purchase")),
                ("house_hook_limit", new Property<bool>(true, "if disabled, house hook limits are ignored")),
                ("house_hookgroup_limit", new Property<bool>(true, "if disabled, house hook group limits are ignored")),
                ("house_per_char", new Property<bool>(false, "if TRUE, allows 1 house per char instead of 1 house per account")),
                ("house_purchase_requirements", new Property<bool>(true, "if disabled, requirements to purchase/rent house are not checked")),
                ("house_rent_enabled", new Property<bool>(true, "If FALSE then rent is not required")),
                ("iou_trades", new Property<bool>(false, "(non-retail function) If enabled, IOUs can be traded for objects that are missing in DB but added/restored later on")),
                ("ip_limit_confine_proving_grounds", new Property<bool>(true, "WaffleACE: IP active-player limit - while TRUE, a character standing in a live Proving Grounds run (DPS, Survival, or Wave challenge) inside its bound run instance counts as CONFINED, the same as standing in a mule landblock. Speed runs are governed separately by ip_limit_confine_speed_runs. Defaults TRUE per the standing owner rule that Proving Grounds is exempt from the active-character limit")),
                ("ip_limit_confine_pvp_arena", new Property<bool>(true, "WaffleACE: IP active-player limit - while TRUE, a character standing in a live PvP arena match inside its bound arena map instance counts as CONFINED, the same as standing in a mule landblock. Defaults TRUE per the standing owner rule that arena activity should not spend a character's active-character slot")),
                ("ip_limit_confine_speed_runs", new Property<bool>(true, "WaffleACE: IP active-player limit - while TRUE, a character standing in a live Proving Grounds Speed run inside its bound run instance counts as CONFINED. Kept as its own switch, separate from ip_limit_confine_proving_grounds, so Speed can be turned off independently of the other three Proving Grounds disciplines. Defaults TRUE per the standing owner rule that Proving Grounds (Speed included) is exempt from the active-character limit")),
                ("ip_limit_enabled", new Property<bool>(false, "WaffleACE: master switch for the IP-based active-player limit. When TRUE, a non-exempt client IP may have at most ip_limit_max_free characters in-world outside the mule_landblocks set, plus up to ip_limit_max_confined additional characters as long as each of those is inside a mule landblock. Exempt accounts (ip_limit_exempt_accounts, ip_limit_exempt_access_level, loopback addresses, and the existing AllowUnlimitedSessionsFromIPAddresses allowlist) bypass the whole check. Defaults FALSE - this is a non-retail anti-multibox measure, not part of shipped behavior until enabled")),
                ("item_dispel", new Property<bool>(false, "if enabled, allows players to dispel items. defaults to end of retail, where item dispels could only target creatures")),
                ("lifestone_broadcast_death", new Property<bool>(true, "if true, player deaths are additionally broadcast to other players standing near the destination lifestone")),
                ("loot_quality_mod", new Property<bool>(true, "if FALSE then the loot quality modifier of a Death Treasure profile does not affect loot generation")),
                ("market_allow_class_listings", new Property<bool>(true, "WaffleACE Market: whether a counted item-CLASS vault line (a folded salvage pool, drawn as one panel line) may be listed on the market. Defaults TRUE. Turning it off makes MarketManager.List refuse a class_key with item_not_found; it does NOT close or alter class listings already Active, which stay buyable, and it does not affect barreling a class line")),
                ("market_allow_free_listings", new Property<bool>(true, "WaffleACE Market: whether a listing may carry a price of 0, which gives the item away to whoever claims it first. Defaults TRUE. Turning it off makes MarketManager.List refuse a zero price with invalid_price exactly as it did before giveaways existed; it does NOT close or alter listings already sitting at zero, so flipping it off leaves existing giveaways claimable. Negative prices are refused regardless of this setting")),
                ("market_buy_orders_enabled", new Property<bool>(false, "WaffleACE Market: whether players may PLACE and FILL Wanted buy orders (Docs/Market/WANTED-DESIGN.md). Cancelling an order, the expiry pass and boot recovery keep running with this off, so escrow is always recoverable. Ships OFF; flip on stage first")),
                ("market_enabled", new Property<bool>(false, "WaffleACE Market: the runtime kill switch. Config.js's Market.Enabled decides whether the API host STARTS at all; this decides whether MarketManager accepts listings, delistings and purchases, and can be flipped live without a restart. Both must be true for the market to work")),
                ("market_snapshot_backfill_on_start", new Property<bool>(true, "WaffleACE Market: whether the server repairs out-of-date listing snapshots by itself shortly after a world start. Defaults TRUE. It re-projects only ACTIVE listings whose stored snapshot predates MarketSnapshot.CurrentVersion, so a restart with nothing out of date does no work at all; it writes only the snapshot column and never status, count or custody, and it refuses any projection that comes out worse than what is stored. Turning it off leaves /marketbackfill run as the only way to repair legacy listings. The flag is read once, on the backfill's own background thread, so flipping it takes effect at the next world start rather than mid-pass")),
                ("market_reject_log_enabled", new Property<bool>(true, "WaffleACE Market: whether refused listing, delist and purchase attempts are recorded to market_rejected_attempt. Defaults TRUE - without these rows a player dispute of the form 'I tried to buy this and it failed' leaves no trace anywhere. Turning it off stops new rows being enqueued; it does not delete existing ones, and it does not affect market_transaction, which records everything that got as far as a Pending row. The flag is read on the audit writer's own background thread, never on the request path")),
                ("monster_conditional_spell_selection", new Property<bool>(ACE.Server.Entity.MonsterSpellSelector.DefaultConditionalSelectionEnabled, "(non-retail function) WaffleACE: whether a caster monster picks WHICH spell to cast by looking at what its target is already carrying. With this on, a debuff the target lacks (an elemental Vulnerability of that element, or an Imperil) and a damaging spell whose element the target is already vulnerable to are rolled first, everything else after. A debuff (Vulnerability, Imperil, or an attribute, vital or skill debuff) whose own spell category already holds an entry at least as strong on the target, from any caster, is skipped, and its share of the cast chance is handed to the remaining spells so the chance of casting at all is unchanged. If every spell would be skipped, the book is rolled exactly as with this off. Nothing about mana, animation, range or resist math is touched. Falls back to the flat single pass whenever there is no live target in the same landblock group. False reproduces the previous behaviour exactly")),
                ("mule_form_enabled", new Property<bool>(true, "WaffleACE: whether an account's earned Beast Effigy form is applied to its summoned /mule vendor. Gates the APPEARANCE SUBSTITUTION ONLY - tokens still attune, still fill on kills, and a completed token still saves the look with this off; the look simply shows once it is back on. Turning it off is therefore always safe and never loses a player's progress")),
                ("mule_system_enabled", new Property<bool>(true, "WaffleACE: master switch for the mule system. Gates ONLY the conversion path (the NPC emote / MuleConversion.HandleMuleRequest). The restriction guards always apply to any character already flagged IsMule regardless of this switch, so turning it off cannot unlock existing mules - see Player_Mule")),
                ("mule_upgrade_enabled", new Property<bool>(true, "WaffleACE: kill switch for /mule upgrade (CapacityUpgradeBroker, CapacityUpgradeKind.MuleVault). While FALSE the command refuses to start a new purchase; upgrades already bought keep applying to the account vault's cap regardless")),
                ("market_upgrade_enabled", new Property<bool>(true, "WaffleACE: kill switch for /market upgrade (CapacityUpgradeBroker, CapacityUpgradeKind.MarketListings). While FALSE the command refuses to start a new purchase; upgrades already bought keep applying to the account's listing cap regardless")),
                ("npc_hairstyle_fullrange", new Property<bool>(false, "if TRUE, allows generated creatures to use full range of hairstyles. Retail only allowed first nine (0-8) out of 51")),
                ("offline_xp_passup_limit", new Property<bool>(true, "if FALSE, allows unlimited xp to passup to offline characters in allegiances")),
                ("olthoi_play_disabled", new Property<bool>(false, "if false, allows players to create and play as olthoi characters")),
                ("override_encounter_spawn_rates", new Property<bool>(false, "if enabled, landblock encounter spawns are overidden by double properties below.")),
                ("permit_corpse_all", new Property<bool>(false, "If TRUE, /permit grants permittees access to all corpses of the permitter. Defaults to FALSE as per retail, where /permit only grants access to 1 locked corpse")),
                ("persist_movement", new Property<bool>(false, "If TRUE, persists autonomous movements such as turns and sidesteps through non-autonomous server actions. Retail didn't appear to do this, but some players may prefer this.")),
                ("pet_stow_replace", new Property<bool>(false, "pet stowing for different pet devices becomes a stow and replace. defaults to retail value of false")),
                ("player_config_command", new Property<bool>(false, "If enabled, players can use /config to change their settings via text commands")),
                ("player_touchdown_teleport", new Property<bool>(true, "if TRUE, fixes the long-standing rubber-band effect other players see when someone jumps or jump-runs: for a few seconds after a player jumps, each grounded position broadcast to OTHER clients carries a newer ObjectTeleport sequence, so their client places the remote copy on the landing point at once instead of queueing it as an interpolation node it can only walk to while its copy is grounded (a jump-running copy almost never is - it falls 20 m behind, walks backwards through stale positions and drifts for seconds after the run). The moving player's own client never sees the bumped sequence. PVP note: observers now see a jumping opponent where they actually are, so jumping no longer desyncs the copy - this may change how easily melee engagement can be broken by jumping. Set to FALSE to restore the previous behaviour")),
                ("player_config_command_resend_description", new Property<bool>(false, "DIAGNOSTIC ONLY. If enabled, /config resends the full GameEventPlayerDescription after a successful change, as it did historically. That resend reliably wedges the client and the session dies of Network Timeout about a minute later, so this defaults to FALSE. Only turn it on to reproduce the defect under a packet capture")),
                ("player_death_no_item_loss", new Property<bool>(false, "If TRUE, players drop nothing on death server-wide: no items, no coins, and no corpse is left behind. Vitae and the XP death penalty are unaffected. Defaults to FALSE, as per retail")),
                ("player_receive_immediate_save", new Property<bool>(false, "if enabled, when the player receives items from an NPC, they will be saved immediately")),
                ("pk_server", new Property<bool>(false, "set this to TRUE for darktide servers")),
                ("pk_server_safe_training_academy", new Property<bool>(false, "set this to TRUE to disable pk fighting in training academy and time to exit starter town safely")),
                ("pkl_server", new Property<bool>(false, "set this to TRUE for pink servers")),
                ("quest_info_enabled", new Property<bool>(true, "toggles the /myquests player command")),
                ("realm_announce_enabled", new Property<bool>(true, "(non-retail function) sends the [REALM] chat line automatically on login and whenever a teleport moves the player into a different realm or instance, so a Decal plugin can track it; the /realm command itself is never gated by this")),
                ("rares_real_time", new Property<bool>(true, "allow for second chance roll based on an rng seeded timestamp for a rare on rare eligible kills that do not generate a rare, rares_max_seconds_between defines maximum seconds before second chance kicks in")),
                ("rares_suppress_gear_drops", new Property<bool>(true, "kill-switch: when true, a rare roll that lands on a weapon, caster, armor or jewelry rare produces no rare at all (no re-roll, no second chance, no timer reset); gems and all other rares are unaffected. Decided by the weenie ItemType, not tier. With the retail tables this empties tiers 4 and 6 entirely (all 47 tier-4 and all 61 tier-6 rares are gear)")),
                ("rares_real_time_v2", new Property<bool>(false, "chances for a rare to be generated on rare eligible kills are modified by the last time one was found per each player, rares_max_days_between defines maximum days before guaranteed rare generation")),
                ("runrate_add_hooks", new Property<bool>(false, "if TRUE, adds some runrate hooks that were missing from retail (exhaustion done, raise skill/attribute")),
                ("reportbug_enabled", new Property<bool>(false, "toggles the /reportbug player command")),
                ("require_spell_comps", new Property<bool>(true, "if FALSE, spell components are no longer required to be in inventory to cast spells. defaults to enabled, as in retail")),
                ("safe_spell_comps", new Property<bool>(false, "if TRUE, disables spell component burning for everyone")),
                ("shield_applies_in_noncombat", new Property<bool>(true, "Combat: true = an equipped shield's damage reduction (shield armor level vs physical and missile damage) and a shield's magic-absorb (Aegis) apply even when the defender is in peace mode (NonCombat); false = retail behaviour, where a shield only counts in a combat stance")),
                ("show_ammo_buff", new Property<bool>(false, "shows active enchantments such as blood drinker on equipped missile ammo during appraisal")),
                ("show_aura_buff", new Property<bool>(false, "shows active aura enchantments on wielded items during appraisal")),
                ("show_dat_warning", new Property<bool>(false, "if TRUE, will alert player (dat_warning_msg) when client attempts to download from server and boot them from game, disabled by default")),
                ("show_dot_messages", new Property<bool>(false, "enabled, shows combat messages for DoT damage ticks. defaults to disabled, as in retail")),
                ("show_first_login_gift", new Property<bool>(false, "if TRUE, will show on first login that the player earned bonus item (Blackmoor's Favor and/or Asheron's Benediction), disabled by default because msg is kind of odd on an emulator")),
                ("show_mana_conv_bonus_0", new Property<bool>(true, "if disabled, only shows mana conversion bonus if not zero, during appraisal of casting items")),
                ("smite_uses_takedamage", new Property<bool>(false, "if enabled, smite applies damage via TakeDamage")),
                ("spellcast_recoil_queue", new Property<bool>(false, "if true, players can queue the next spell to cast during recoil animation")),
                ("spell_projectile_ethereal", new Property<bool>(false, "broadcasts all spell projectiles as ethereal to clients only, and manually send stop velocity on collision. can fix various issues with client missing target id.")),
                ("spell_projectile_ethereal_360", new Property<bool>(true, "(non-retail function) broadcasts spell projectiles from 360-degree spread spells (ring spells) as ethereal to clients only. server-side collision and damage are unaffected. fixes the third-person camera being pulled in every time a ring is cast, because one projectile always travels straight through the camera and the client's viewer transition treats a non-ethereal projectile as an obstruction")),
                ("stage_self_grants_enabled", new Property<bool>(false, "(non-retail function) TEST SHARDS ONLY. Lets EVERY player on the shard grant themselves progression via /myxp, /mylum, /myabilitypoints, /mymmd, /mypromnotes and /myauggem, and respec their skills with /myrespec. Sole gate on those commands - note this row lives in ace_shard, so it travels with a shard database dump")),
                ("suicide_instant_death", new Property<bool>(false, "if enabled, @die command kills player instantly. defaults to disabled, as in retail")),
                ("summoning_mastery_unrestricted", new Property<bool>(true, "if TRUE, any character can use any summoning essence regardless of Summoning Mastery (Primalist/Necromancer/Naturalist). Set to FALSE to restore the retail mastery requirement")),
                ("taboo_table", new Property<bool>(true, "if enabled, taboo table restricts player names during character creation")),
                ("tailoring_intermediate_uieffects", new Property<bool>(false, "If true, tailoring intermediate icons retain the magical/elemental highlight of the original item")),
                ("trajectory_alt_solver", new Property<bool>(false, "use the alternate trajectory solver for missiles and spell projectiles")),
                ("universal_masteries", new Property<bool>(true, "if TRUE, matches end of retail masteries - players wielding almost any weapon get +5 DR, except if the weapon \"seems tough to master\". " +
                                                                 "if FALSE, players start with mastery of 1 melee and 1 ranged weapon type based on heritage, and can later re-select these 2 masteries")),
                ("unlimited_sequence_gaps", new Property<bool>(false, "upon startup, allows server to find all unused guids in a range instead of a set hard limit")),
                ("use_generator_rotation_offset", new Property<bool>(true, "enables or disables using the generator's current rotation when offseting relative positions")),
                ("use_portal_max_level_requirement", new Property<bool>(true, "disable this to ignore the max level restriction on portals")),
                ("use_turbine_chat", new Property<bool>(true, "enables or disables global chat channels (General, LFG, Roleplay, Trade, Olthoi, Society, Allegience)")),
                ("use_wield_requirements", new Property<bool>(true, "disable this to bypass wield requirements. mostly for dev debugging")),
                ("facet_enabled", new Property<bool>(true, "Player Facets: master switch for the /facet command family and every behaviour it drives. Defaults TRUE - the feature has been verified in game and ships on. While false /facet refuses with a plain 'not available' line rather than being hidden, so a player who was told about it gets an answer")),
                ("facet_pk_enabled", new Property<bool>(true, "Player Facets: enables the PK facet (/facet pk), a separate facet slot on which the character is always a player killer and class abilities, class ability point spending, equipment mods, weapon mods and pickup/turn speed bonuses are suspended. facet_enabled stays the master switch: with it false, /facet pk refuses like every other facet command")),
                ("pvp_arena_enabled", new Property<bool>(true, "PvP Arena (Docs/Pvp/DESIGN.md): master switch for the whole arena system (queueing, matches, ratings). New setting, defaults ON per the standing owner rule. INERT until the coordinator and hooks land (PR C) - this key is reserved by the pure core and tunables PR only")),
                ("pvp_arena_1v1_enabled", new Property<bool>(true, "PvP Arena: per-mode switch for 1v1. Defaults ON")),
                ("pvp_arena_2v2_enabled", new Property<bool>(true, "PvP Arena: per-mode switch for 2v2. Defaults ON")),
                ("pvp_arena_ffa_enabled", new Property<bool>(true, "PvP Arena: per-mode switch for Tugak Brawl (the large-lobby brawl mode, formerly FFA). Defaults ON")),
                ("pvp_arena_block_same_ip", new Property<bool>(true, "PvP Arena: refuses to match same-IP opponents in 1v1/2v2 queueing, and excludes same-IP players from each other's Tugak Brawl pairwise rating comparison. Defaults ON")),
                ("pvp_arena_friendly_fire", new Property<bool>(false, "PvP Arena: whether teammates can damage each other inside a match. Defaults OFF, per design's Rulings")),
                ("pvp_arena_death_keeps_enchantments", new Property<bool>(true, "PvP Arena: whether an in-match death keeps the dying player's enchantments instead of purging them. Defaults ON")),
                ("pvp_arena_retire_combat_pets", new Property<bool>(true, "PvP Arena: whether combat pets are dismissed on match entry and summoning is refused while in a match. Defaults ON")),
                ("pvp_arena_suppress_class_abilities", new Property<bool>(true, "PvP Arena: whether class abilities are masked off inside a match. Defaults ON")),
                ("pvp_arena_suppress_equipment_mods", new Property<bool>(true, "PvP Arena: whether equipment mods are masked off inside a match. Defaults ON")),
                ("pvp_arena_suppress_weapon_mods", new Property<bool>(true, "PvP Arena: whether weapon mods are masked off inside a match, ORed into WeaponModSuppression (PR #1394) rather than replacing it. Defaults ON")),
                ("pvp_arena_suppress_pickup_boons", new Property<bool>(true, "PvP Arena: whether pickup-speed boons are suppressed inside a match. Defaults ON")),
                ("pvp_arena_suppress_turn_speed", new Property<bool>(true, "PvP Arena: whether turn-speed boons are suppressed inside a match. Defaults ON")),
                ("pvp_arena_overtime_enabled", new Property<bool>(true, "PvP Arena (Docs/Pvp/DESIGN.md \"Overtime\"): when a match reaches its regulation time limit (pvp_arena_time_limit_seconds_*) with no winner, it goes to pvp_arena_overtime_seconds of overtime instead of ending: healing is scaled by pvp_arena_overtime_healing_mod and damage optionally ramps. A kill in overtime is a normal win; at the end of overtime a 1v1/2v2 is an unrated draw and Tugak Brawl survivors share first. Off = exactly the old timeout. Defaults ON")),
                ("pvp_arena_blood_enabled", new Property<bool>(true, "PvP Arena (Docs/Pvp/DESIGN.md \"Rewards\"): arena matches pay the cosmetic Mark of the Hopeslayer currency (formerly named Blood; Bonded, Attuned, spent only at the Bloodwarden) - pvp_arena_blood_win / _loss / _draw per participant, exactly once per match, a forfeiter 0, nothing for a match that never went Live, a canceled match, or same-IP opponents, at most pvp_arena_blood_daily_cap paid matches per character per day. Off = no Marks of the Hopeslayer are paid at all. Defaults ON (owner ruling 2026-10-08, with the next deploy; it was OFF from 2026-10-01). Battlegrounds never pay regardless")),
                ("pvp_arena_crier_enabled", new Property<bool>(true, "PvP Arena Crier (Docs/Pvp/DESIGN.md \"Arena Crier\"): master switch for the periodic /lfg queue announcements and the last-call announcement. Defaults ON")),
                ("pvp_arena_crier_announce_on_join", new Property<bool>(true, "PvP Arena Crier: true = announce a queue's status line every time a player joins it (its count increases); false = announce on the pvp_arena_crier_interval_seconds timer instead. Switch to false if join/leave churn spams /lfg")),
                ("pvp_arena_restrict_spells", new Property<bool>(true, "PvP Arena: whether the Doctide-ported arena spell rules (Docs/Pvp/DESIGN.md \"Arena spell rules\") are enforced inside a match - Item Enchantment and most Creature Enchantment spells refused, Tugak Brawl refusing every harmful spell but CurseRavenFury, and arena DoT ticks dealing no damage unless the match is Live (none at all in Tugak Brawl). Defaults ON")),
                ("pvp_bg_enabled", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): master switch for battlegrounds, underneath pvp_arena_enabled. Defaults ON")),
                ("pvp_bg_koth_enabled", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): per-mode switch for King of the Hill. Defaults ON")),
                ("pvp_bg_ad_enabled", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): per-mode switch for Attack/Defend. Defaults ON")),
                ("pvp_bg_ad_sequential_crystals", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Sequential crystals\"): when ON the attackers must destroy the crystals in site order (1 Great Hall, 2 West Cavern, 3 Pit Hall; the first N sites when the crystal count is lower); only the lowest-numbered standing crystal takes damage and the rest refuse hits until the one before them falls. OFF is any-order play. Read once when a match forms, so it applies to the next match. Defaults ON")),
                ("pvp_bg_snake_draft", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Matchmaking\"): whether the battleground matchmaker prefers the team split whose solo players follow an Elo snake draft (highest rated to one team, the next two to the other, and so on), ahead of the clanmate split and the rating-sum difference. Same-IP and premade limits still win. Read each time a match is formed. Defaults ON")),
                ("pvp_bg_split_clanmates", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): whether the battleground matchmaker prefers to split solo queuers of the same allegiance across teams. Defaults ON")),
                ("pvp_bg_koth_dedupe_ip", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): whether each IP address counts once per team on the King of the Hill point. Defaults ON")),
                ("pvp_bg_koth_drain_lethal", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): whether the King of the Hill drain can kill a player standing on the point. A drain death credits no killer. Defaults ON")),
                ("pvp_bg_team_fellowship", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Team fellowships\"): whether each battleground team is put in a server-built fellowship at Countdown (teammates show as fellows on radar and in the fellowship panel; player fellowship changes are refused until the match ends). Read once per match, when it forms. Arena modes are never affected. Defaults ON")),
                ("pvp_bg_team_fellowship_restore", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Team fellowships\"): whether a player leaving a battleground with team fellowships is put back into the fellowship they arrived in (an outside fellowship is rejoined, a premade is re-formed) when that is possible. Defaults ON")),
                ("pvp_bg_koth_markers_enabled", new Property<bool>(true, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Zone markers\"): whether a King of the Hill match places a ring of marker objects on the edge of the control zone when its space is ready. Cosmetic only: scoring never reads the markers, and a marker that fails to place never delays or cancels the match. Read when a match forms. Defaults ON")),
                ("pvp_rules_enabled", new Property<bool>(true, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): master switch for every PvP balance lever (the pvp_damage_cap pair, the rating scales, the consumable rules, the dispel vuln lock). Levers act ONLY on player-vs-player interactions - never self, pets or PvE - in the open world and the arena alike, except the consumable rules and pvp_healing_mod, which are arena only. False = every lever hands its input back unchanged. Defaults ON")),
                ("pvp_block_airborne_consumables", new Property<bool>(true, "Arena only: while a player is in a Live arena match, refuses the COMPLETION of a kit, food or gem use while airborne and keeps the item, at choke points H1/H2. A PK timer alone does not apply it. Defaults ON")),
                ("pvp_cleave_enabled", new Property<bool>(true, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): whether a cleaving melee strike may hit player targets, at choke point CLV1. False skips player candidates for a non-admin attacker. Ported from Doctide `disable_pvp_cleave` (inverted sense). Defaults ON")),
                ("pvp_strip_rare_buffs", new Property<bool>(true, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): strips a player's rare-gem buffs (Player.StripRareGemBuffs) on every successful PvP hit, at choke point RB1. Ported from Doctide `dispel_rares_pvp`. Defaults ON")),
                ("pvp_template_enabled", new Property<bool>(true, "PvP Template Facets (Docs/Pvp/TEMPLATES.md): master switch for templated arena and battleground matches (every mode is templated). With it off, no template is applied and every arena and battleground join is refused (an already-templated player is still restored at every exit and at login). New setting, defaults ON per the standing owner rule")),
                ("pvp_template_keep_own_buffs", new Property<bool>(true, "PvP Template Facets: whether the player's own buffs (every non-item enchantment held at match entry) are returned after a templated match. Off returns only vitae and cooldowns. Decided at match entry and recorded, so a later change never alters a restore in flight. Defaults ON")),
                ("pvp_template_suppress_heritage_bonus", new Property<bool>(true, "PvP Template Facets: whether the heritage weapon bonus is masked off while a player is templated (owner ruling 2026-10-03). Defaults ON")),
                ("version_info_enabled", new Property<bool>(false, "toggles the /aceversion player command")),
                ("vendor_shop_uses_generator", new Property<bool>(false, "enables or disables vendors using generator system in addition to createlist to create artificial scarcity")),
                ("vendor_stock_weenie_warmup", new Property<bool>(true, "(non-retail function) at startup, on a background task, load into the world-database weenie cache every wcid a vendor's Shop create list can stock (one world-DB query collects them), so the first time a vendor is opened after a restart it does not run one world-DB read per stocked item inside the world tick. Skipped when Config.js WorldDatabasePrecaching is true. Read once at startup")),
                ("world_closed", new Property<bool>(false, "enable this to startup world as a closed to players world")),
                ("dynamic_dungeons_enabled", new Property<bool>(false, "(non-retail function) master switch for Threads; a Thread Gem refuses while false and /dd give still works for testing")),
                ("dynamic_dungeons_press_enabled", new Property<bool>(false, "(non-retail function) Threads: the Fragment Press accepts raw fragments and re-opens unbound gems; independent of dynamic_dungeons_enabled")),
                ("dynamic_dungeons_press_dose_log", new Property<bool>(true, "(non-retail function) Threads: the Fragment Press echoes a per-dose result line to the pressing player's chat, in addition to the always-shown pressed line and finished-gem summary; can reach 19 lines on a full pressing")),
                ("dynamic_dungeons_survey_level_scaling", new Property<bool>(true, "(non-retail function) Threads: the Survey-Archivist measures each daily-survey reward at the average GEM LEVEL of the newest surveys the tier covers (drawn from a rolling depth-10 ring): XP pays percent of the level-up at min(player level, avg gem level) and luminance scales by the ratio. False pays XP at the player's own level and pins the luminance ratio to 1.0 - a live rollback with no redeploy")),
                ("dynamic_dungeons_guide_enabled", new Property<bool>(true, "(non-retail function) Threads: the Thread-Guide issues guide fragments (the 14-rung solo ladder 50..375), regrants a fragment after a failed guide run and sends the one-time level-50 notice. All three ALSO require dynamic_dungeons_enabled and dynamic_dungeons_press_enabled. False makes the Thread-Guide refuse, blocks the regrant and suppresses the notice; guide items already issued keep working at the Press and as gems, and guide clears still advance the ladder. New setting, defaults ON per the standing owner rule")),
                ("dynamic_dungeons_reward_scaling", new Property<bool>(true, "(non-retail function) Threads: master switch for the gem-level reward-scale curve (dynamic_dungeons_reward_scale_anchor/exponent/floor/cap) applied to every run's XP scale, luminance scale, loot quantity, loot quality bonus, and salvage-affinity chance. False pins the ratio to 1.0, reproducing pre-scaling behaviour exactly - a live rollback with no redeploy")),
                ("dynamic_dungeons_health_curve", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveEnabled, "(non-retail function) Threads: master switch for the band-standard health curve T(L) = anchor_low * g^(L - 185), where g is DERIVED from dynamic_dungeons_health_curve_anchor_low/_high and the levels 185 and 375 they were measured at (about +1.43% per level, +15.2% per 10-level fragment rung). Every non-boss creature's authored health, and the boss's own authored base, is multiplied by T(gemLevel) divided by the measured cross-family band median, so the difficulty ladder is smooth and monotone instead of inheriting the authored content's lumpiness (measured 890 hp at level 185 climbing 11x to 10100 by 245, then nearly flat to 375). A RATIO, not a target, so the authored spread within a pack survives; the ratio is clamped to 25x as a guard against degenerate data. Above level 375 the curve EXTRAPOLATES on the same g up to dynamic_dungeons_health_curve_top_level (default 500, the run ceiling; about 18661 at 400, 26583 at 425, 37868 at 450, 53944 at 475 and 76845 at 500) and clamps at and above that level. False reproduces today's behaviour byte-for-byte - the authored health plus the dynamic_dungeons_trash_health_floor_ratio floor measured on the sampled median")),
                ("dynamic_dungeons_uplift_attributes", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultUpliftAttributes, "(non-retail function) Threads: raise an adaptive-band uplifted creature's primary attributes (upward only) to the band standard's medians, top its attack and defense skills up so their EFFECTIVE value lands on the band's effective median (defense capped at the dynamic_dungeons_defense_skill_cap_offset ceiling), and re-check that ceiling after the uplift. Max health is unchanged by the Endurance raise. The Quickness raise also makes uplifted monsters run faster. Without it an uplifted creature has only its InitLevels raised and lands below the band's effective skill. Does not affect bosses or World Events. False restores the InitLevel-only uplift")),
                ("dynamic_dungeons_reach_up", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultReachUp, "(non-retail function) Threads: reach-up projection for runs above the PIVOT (floor(authored top / band high), 326 on the shipped roster, the last run level whose band [L, 1.15L] sits wholly inside the authored data). Above the pivot every roster level is multiplied by k = run level / pivot before the band test, so the authored roster stretches to cover a run above level 375 instead of the band running out of creatures; a pick whose projected level is above its authored level is STAMPED to the projected level, scaled on the stat curve (dynamic_dungeons_stat_curve) and pays that level's XP. k is exactly 1 at and below the pivot, so every run at or below 326 plans byte-for-byte as it did. Owner ruling 2026-10-08 (run ceiling 500). False disables the projection: runs above the pivot draw from the authored band as before and rely on the adaptive band uplift")),
                ("dynamic_dungeons_stat_curve", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultStatCurve, "(non-retail function) Threads: above the reach-up pivot (326 on the shipped roster), take the band standard from the fitted stat curve instead of the measured band: the measured standard AT the pivot carried up by per-axis exponential slopes fitted to the authored data (damage about +1.0% per level, attributes about +0.14-0.23%, attack about +0.05%, defense about +0.09-0.16%), with effective defense above level 375 softened by dynamic_dungeons_defense_curve_rate_above_375. Normalized bosses, adaptive-band uplifts, the defense-skill cap and reach-up stamps all read it. Owner ruling 2026-10-08: every monster stat follows its natural curve past the authored data. False uses the measured band standard at every level, which above 375 stops growing")),
                ("dynamic_dungeons_uplift_rename", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultUpliftRename, "(non-retail function) Threads: prefix an adaptive-band uplifted creature's name with 'Threadbound ' so it is not mistaken for its base form in the examine panel. A creature reached by the band's downward extension has had its level, skills, melee damage, body armour and spell tier raised to the band standard, and the examine panel is the only place a player can see that before the fight. False leaves every name untouched")),
                ("dynamic_dungeons_boss_normalize", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossNormalize, "(non-retail function) Threads: master switch for boss normalization. On: the boss's health is max(dynamic_dungeons_boss_health_band_ratio x the band-standard health curve, dynamic_dungeons_boss_health_pack_margin x the toughest non-boss base in the run's pools) times its own modifiers, and never below the toughest placed creature + 1; its attack and defense skills, body-part damage and armour, spell tier and health regen are SET to the band standard; its authored damage/crit ratings are zeroed before the boss rating floors apply; its level stamp ignores the weenie's authored level and its XP prices the ladder at the stamped level. False reproduces the pre-normalization boss exactly")),
                ("dynamic_dungeons_strip_combat_traits", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultStripCombatTraits, "(non-retail function) Threads: strip bypass and immunity traits (IgnoreShield, IgnoreMagicResist/Armor, IgnoreShieldsBySkill, Invincible, NonProjectileMagicImmune, Overpower, IgnoreArmor, AbsorbMagicDamage, Ethereal, IgnoreCollisions) from every run creature and apply the category-immunity guard (dynamic_dungeons_category_resist_floor, _category_armor_mod_ceiling, _defense_skill_cap_offset). Runs before the gem's own shield/hollow modifiers, so those still apply. Single-element immunity is kept")),
                ("world_events_band_uplift", new Property<bool>(ACE.Server.WorldEvents.WorldEventRosterSelector.DefaultBandUpliftEnabled, "(non-retail function) World Events: when a wave slot's family pool is thin, widen the trash band's LOW edge down to world_events_band_low_floor before the nearest-member fallback, and normalize any creature drawn BELOW the natural band (level, per-skill InitLevel, body-part damage and armour, then a health floor at the band median before the crowd x pace multiplier) up to the standard that band carries. Authored XP is kept and no name prefix is added; named bosses are never uplifted. false restores the pre-uplift picks and spawns exactly")),
                ("world_events_strip_combat_traits", new Property<bool>(ACE.Server.WorldEvents.WorldEventCombatGuard.DefaultStripCombatTraits, "(non-retail function) World Events: strip bypass and immunity traits (IgnoreShield, IgnoreMagicResist/Armor, IgnoreShieldsBySkill, Invincible, NonProjectileMagicImmune, Overpower, IgnoreArmor, AbsorbMagicDamage, Ethereal, IgnoreCollisions) from every roster-drawn wave monster (trash, elite, overflow champion) and the family champion, and apply the category-immunity guard (world_events_category_resist_floor, _category_armor_mod_ceiling, _defense_skill_cap_offset) against the band standard at the slot participant level. Uplifted or not. Named bosses are exempt. Single-element immunity is kept")),
                ("dynamic_dungeons_boss_any_all_bands", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossAnyAllBands, "(non-retail function) Threads: while dynamic_dungeons_boss_normalize is also on, bosses.json rows with an empty families list skip the 1.0x-1.6x gem-level window and can headline a run at every gem level. Family rows keep the window")),
                ("dynamic_dungeons_boss_chest_enabled", new Property<bool>(true,"(non-retail function) Threads: a Thread Cache (wcid 1003603) forms at the boss's authored spawn anchor when the run boss dies, holding, in slot order, a Trade Note stack scaled by gem level, one full bag of the gem's targeted salvage material for each distinct salvage_affinity it carries (none if it carries none), and dynamic_dungeons_boss_chest_loot_count rolls drawn from the retail Legendary Chest's own item table (Weapon/Armor/Clothing/Jewelry/Cloak/PetDevice only - never gems, scrolls, or art objects) at the run's own tier, quality and loot-quantity multiplier. Only the run owner can open it, and it dies with the copy. False leaves a boss death exactly as it was")),
                ("dynamic_dungeons_loot_miss_attribution", new Property<bool>(true, "(non-retail function) Threads: record WHICH wcids miss the world-database weenie cache, so a pooled-loot delivery's finish line can name the weenies a shard is still reading at clear time (topMiss=). Bounded at 4096 distinct wcids process-wide; the recording is one dictionary update on a path that is already running a synchronous DB read. Read once per delivery chain. Off leaves the miss COUNTS (weenieMiss, scrollMiss) intact and only drops the per-wcid breakdown")),
                ("dynamic_dungeons_cache_display_sort", new Property<bool>(ACE.Server.ThreadDungeons.ThreadCacheSort.DefaultEnabled, "(non-retail function) Threads: order a SOLO Thread Cache's contents by the same composite display key the /mule vault panel uses (category, weapon class/armour slot/misc family, damage type or essence element, minimum requirement, material, name, workmanship, value, guid - see VaultDisplaySort). The reorder runs once, at the end of a delivery chain, by rewriting PlacementPosition; a cache a player has OPEN is left alone and reordered after they close it. Group runs are never touched: their caches keep the value ordering the per-member deal produces. Read at the start of every sort pass, so flipping it takes effect on the next delivery. Off leaves contents in arrival order")),
                ("dynamic_dungeons_plan_cache", new Property<bool>(ACE.Server.ThreadDungeons.ThreadPlanCache.DefaultPlanCacheEnabled, "(non-retail function) Threads: memoise the family-independent parts of a run's population plan (the physical fit projection, the eligibility-band ladder search, the eligible-family count, the cross-family band median health and the band standard, plus per-wcid level/health/stat-profile reads), keyed on the species tables, the gem level, the roster band, the band low floor and the dials that feed them. Purely a cost saving: the plan a given seed produces is identical either way, because nothing seed-, family- or pool-dependent is cached. Dropped whenever the species tables, the Thread dungeon store or the world weenie cache is reloaded. False recomputes everything on the world thread on every run start, as before")),
                ("dynamic_dungeons_loot_weenie_warmup", new Property<bool>(true, "(non-retail function) Threads: at startup, on a background task, load into the world-database weenie cache every wcid Threads loot delivery can create (the loot factory's wcid tables up to dynamic_dungeons_loot_tier_cap, plus the cache, exit, Trade Note and salvage bags, plus the scroll weenie of every spell the factory's scroll roll can pick), so the first clear after a restart does not run those world-DB reads inside the world tick. Skipped when Config.js WorldDatabasePrecaching is true. Read once at startup")),
                ("dynamic_dungeons_pooled_loot_enabled", new Property<bool>(true, "(non-retail function) Threads: run creatures leave no corpse and drop nothing. Everything they would have dropped, plus the boss cache's bonus, is pooled on the run and delivered in owner-only Thread Caches (wcid 1003603) that form near the owner when the run clears; /spawncache places or moves them, and voluntary exits ask before leaving loot behind. Read once per run at populate start, so a change applies from the next run. False keeps corpses and the boss-death cache exactly as before")),
                ("dynamic_dungeons_puzzle_gates_enabled", new Property<bool>(true, "(non-retail function) Threads: master switch for puzzle gates in runs. When true, a run whose dungeon has curated puzzle sites (Content/dungeons/dynamic/puzzle-gates.json) places up to dynamic_dungeons_puzzle_gates_per_run lever puzzles holding doorways shut, and the reward scene when its own switch allows. Read when the run opens, so a change applies from the next run. False places no puzzle of any kind")),
                ("dynamic_dungeons_puzzle_fail_policy_enabled", new Property<bool>(true, "(non-retail function) Threads: the IP-wide puzzle fail policy. When true, every scored wrong lever pull in a Thread run counts against the puller's connection (one key per address, IPv6 by /64; an account in ip_limit_exempt_accounts or an address in Config.js AllowUnlimitedSessionsFromIPAddresses is keyed per account instead). Reaching dynamic_dungeons_puzzle_fail_threshold within dynamic_dungeons_puzzle_fail_window_minutes bars the key from the Threads for dynamic_dungeons_puzzle_lockout_minutes and removes its characters from their runs. Access level never exempts. False records nothing and enforces no lockout. Applies within 15 seconds")),
                ("dynamic_dungeons_puzzle_reward_scene_enabled", new Property<bool>(true, "(non-retail function) Threads: when true (and dynamic_dungeons_puzzle_gates_enabled), a POOLED-loot run whose dungeon has a curated reward site seals its clear behind one more lever puzzle: the clear announcement and the loot caches wait until it is solved, and its levers refuse without penalty until the run's kills are done. Any loss of the puzzle without a solve unseals the run. Read when the run opens. False keeps every run clearing on its kills alone")),
                ("dynamic_dungeons_group_enabled", new Property<bool>(true, "(non-retail function) Threads: kill-switch for fellowship Threads; false = a gem never offers group mode and every run opens solo")),
                ("dynamic_dungeons_clear_effect_enabled", new Property<bool>(true, "(non-retail function) Threads: play the cloak level-up burst (PlayScript.AetheriaLevelUp) on the run owner when the run clears. Cosmetic only; false removes the effect and nothing else")),
                ("dynamic_dungeons_clear_portal_enabled", new Property<bool>(true, "(non-retail function) Threads: summon a click-to-enter Thread Exit (wcid 1003604) beside the run owner when the run clears, so a cleared run does not require walking back to a static exit. Skipped silently when the owner is offline or standing outside the copy. False leaves only the static walk-through exits, which are always present either way")),
                ("dynamic_dungeons_strip_aoe_spells", new Property<bool>(ACE.Server.ThreadDungeons.DungeonSpellFilter.DefaultStripAoeSpellsEnabled, "(non-retail function) Threads: master switch for stripping room-blanketing and named-drain spells from every run creature's spell book at spawn time (owner ruling 2026-09-08) - a spell is removed when its NumProjectiles is greater than 1 (the ring/wave/volley class), or its id is in dynamic_dungeons_banned_spell_ids. A creature that would be left with an empty book and no damaging body part is left untouched instead. False reproduces today's behaviour exactly")),
                ("dynamic_dungeons_fit_filter", new Property<bool>(ACE.Server.ThreadDungeons.DungeonFitFilter.DefaultFitFilterEnabled, "(non-retail function) Threads: master switch for excluding roster creatures that physically do not FIT the dungeon a run opened in (owner ruling: exclude by fit, never shrink the creature). A creature is excluded when its movement height - the top of the first two Setup collision spheres, scaled by DefaultScale - plus dynamic_dungeons_fit_headroom_margin exceeds that dungeon's measured maxCollisionHeightFullAccess in Content/dungeons/dynamic/clearance.json. Seven shipped creatures are too tall for ten of the thirteen dungeons, and a player can stand across a doorway and kill one that can never close. The filter is applied at ROSTER CONSTRUCTION, so band widening and boss promotion substitute alternatives rather than leaving the run short; if excluding would leave no eligible family at all, the whole projection is discarded for that run. A dungeon with no clearance row, or an unmeasured one, constrains nothing. False reproduces today's behaviour exactly and takes no dat read")),
                ("world_events_account_gate_enabled", new Property<bool>(true, "(non-retail function) World Events reward claim: one claim per account")),
                ("world_events_auto_enabled", new Property<bool>(false, "(non-retail function) World Events scheduler (P4); unused until WP-13")),
                ("world_events_boss_damage_scaling_enabled", new Property<bool>(true, "(non-retail function) World Events: scale a named boss's OUTGOING damage to the audience, by measuring what its hits actually do and steering its DamageRating so the toughest player present dies in world_events_boss_hits_to_kill ordinary hits. Measured, never predicted - armour, resistances, shields and body part move a boss's real damage by 2-3x from anything its weenie implies. While this is false the boss keeps its authored DamageRating untouched")),
                ("world_events_boss_health_milestones_enabled", new Property<bool>(true, "(non-retail function) World Events: broadcast a global line each time a Named boss's health first drops to or below 75%, 50%, 25% of its current max")),
                ("world_events_boss_tether_enabled", new Property<bool>(true, "(non-retail function) World Events: confine a named boss to world_events_boss_tether_radius metres of where it spawned. The spawner stamps TetherRadius, HomeRadius (twice the tether) and DisableSticky on the boss at spawn; targeting then only ever considers players inside the tether, and stepping outside it forces an immediate retarget onto the nearest player who is still at the event instead of the passive walk home. Aimed at one player kiting a boss out of a 20-player fight. While this is false the spawner stamps none of those properties and the boss behaves exactly like any other monster")),
                ("world_events_boss_throughput_scaling_enabled", new Property<bool>(true, "(non-retail function) World Events named-boss health: size the boss from the HP per second the group actually cleared during the wave phase (target kill time 300s) instead of the audience power curve. Ships enabled as standard content. Only bosses whose bosses.json entry carries a non-zero throughputFloorHealth take the new path; every other boss, and the whole system while this is false, keeps the power curve")),
                ("world_event_death_protection", new Property<bool>(true, "(non-retail function) while a world event is Active, players dying within its radius suffer no vitae, item loss, or enchantment purge")),
                ("world_events_enabled", new Property<bool>(true, "(non-retail function) master switch for the World Events system; /worldevent start refuses while false")),
                ("world_events_ip_gate_enabled", new Property<bool>(true, "(non-retail function) World Events reward claim: one claim per IP address; exemptions in world_events_ip_exempt")),
                ("world_events_participation_payout_enabled", new Property<bool>(true, "(non-retail function) World Events reward claim: a claimant with no damage or kill credit on the run receives the reward profile's coalWcid booby prize instead of the outcome crate. False restores the pre-change behaviour where every claimant gets the same crate. Inert for a reward profile whose coalWcid is 0")),
                ("ml_treasure_enabled", new Property<bool>(false, "(non-retail function) ML Treasure Hunt (Docs/Marae-Lassel/TREASURE-HUNT-PLAN.md): master switch. While false, a treasure map drop never rolls and TreasureMapHandler refuses every Use")),
                ("ml_relaria_pulse_effects_enabled", new Property<bool>(false, "(non-retail function) ML Treasure Hunt: master switch for the Aun Relaria repeat-kill aura's looping pulse (Player_RelariaAura) and the Tally of the Unburied's 100-charge glow pulse (Player_RelariaTallyGlow). Off by default per owner ruling 2026-09-24 - the effects were judged too visually intrusive. Gates only the recurring visual re-broadcast; the underlying quest stamps and rewards are unaffected either way. Flipping it off stops live pulses at their next scheduled tick; flipping it on takes effect on each player's next login or re-arm, not immediately for players already online")),
                ("charsheet_enabled", new Property<bool>(true, "WaffleACE Character Sheet: if TRUE, players can publish an opt-in web character sheet with /charsheet or from the market, served at charsheet_site_url.")),
                ("admin_web_enabled", new Property<bool>(true, "WaffleACE Web Admin: if FALSE, every /v1/admin market API route answers admin_disabled (503), Admin accounts included. Live kill switch, no restart. The panel is also unavailable whenever Config.js Market.Enabled is false.")),
                ("suit_builder_enabled", new Property<bool>(true, "WaffleACE Suit Builder: if FALSE, GET /v1/accounts/me/suit/inventory and GET /v1/accounts/me/characters/{guid}/suit-profile answer suit_builder_disabled (503). Live kill switch, no restart. Independent of Market.Enabled's runtime switch (MarketManager.Enabled) - the suit routes serve while the market is off - but, like every market API route, the host only starts when Config.js Market.Enabled is true.")),
                ("admin_web_commands_enabled", new Property<bool>(true, "WaffleACE Web Admin: if FALSE, the /v1/admin/commands routes answer commands_disabled (503). Live kill switch for the command console only; admin_web_enabled still gates the whole panel.")),
                ("ml_digsite_enabled", new Property<bool>(true, "(non-retail function) ML digsite encounters: master switch. Ships ON as a kill switch, not as a gate. While false a completed ordinary dig still pays its doubloons and still consumes the map - the encounter is strictly additive - it simply never opens one. Does not affect the Aun Relaria boss-variant dig, which is a separate mechanism tuned by ml_treasure_relaria_chance")),
                ("ml_digsite_bossrush_mechanics_enabled", new Property<bool>(true, "(non-retail function) ML digsite encounters: kill switch for the Boss Rush mechanic driver (volatile adds, drum cadence, immune phases, interrupt object, shifting safe zones). Ships ON. While false the driver is a no-op and a Boss Rush is the plain single-target fight it was before - the boss, its tether and the ml_digsite_bossrush_mechanic overlay are all unaffected")),
                ("ml_digsite_weenie_warmup", new Property<bool>(true, "(non-retail function) ML digsite encounters: at startup, on a background task, load into the world-database weenie cache every wcid the digsite system can create (every roster band plus the Kept Siraluun band, the Boss Rush prop wcids, the reward chest and its two currencies, the treasure map and the Aun Relaria boss and trophies, plus one level of create_list expansion for wielded gear and the Kept Siraluun feather), so the first wave after a restart does not run those world-DB reads on the world thread. Skipped when Config.js WorldDatabasePrecaching is true. Read once at startup")),
                ("ml_digsite_tether_heal_enabled", new Property<bool>(true, "(non-retail function) ML digsite encounters: whether a Boss Rush boss that was pulled past its tether is healed to full once it has walked home (RoZ round 13 'Open-area safeguard'). Ships ON. Scoped to the Boss Rush boss only - not every digsite creature, and emphatically not the shared monster navigation path, where it would make every monster on the server un-attritionable")),
                ("ml_mapevent_spread_scaling_enabled", new Property<bool>(true, "(non-retail function) RoZ round 19 level-spread scaling for Marae Lassel map events (digsite encounters and Aun Relaria; NOT island wildlife, the Bluespire ladder or World Events): master switch. Ships ON as a kill switch. While true a map-event creature is sized at spawn to the players present - defenses lowered only as far as a keyed player needs to reach ml_mapevent_min_hit_chance / ml_mapevent_min_spell_land_chance, trash offence, damage, health and XP scaled to one sampled player, and the Boss Rush boss and Aun Relaria health sized on the World Event power-sum curve. While false every creature fights as authored and the Boss Rush boss returns to headcount crowd health")),
                ("ml_mapevent_boss_damage_scaling_enabled", new Property<bool>(true, "(non-retail function) RoZ round 19: measured damage scaling for the digsite Boss Rush boss, the World Event rule - the TOUGHEST present player survives about ml_mapevent_boss_hits_to_kill ordinary hits, adjusted from observed hits through the boss DamageRating (WorldEventBossDamageController). Ships ON. Low-level one-shots are accepted. Not applied to Aun Relaria, which has no owning tick")),
                ("bluespire_ladder_enabled", new Property<bool>(true, "(non-retail function) Bluespire ladder (Marae Lassel, realm 1): master switch. Ships ON as a KILL SWITCH, never as a gate. While false every portal carrying PropertyInt 9069 BluespireLadderRung behaves as an ordinary portal (the level and prerequisite gate is not applied) and no rung clear pays its one-time currency reward. The reward stamp itself is unaffected: a quest row created while this is false is still created, and because the payout is once-per-character-ever it is never paid retroactively")),
                ("world_tick_slow_log_enabled", new Property<bool>(true, "(non-retail function) Monitoring: write one SLOW_TICK WARN line to the server log explaining where a slow world-loop iteration's time went (per-phase split, GC activity, players, shard queue, slowest landblocks). Independent of the ServerPerformanceMonitor. Ships ON; false skips the slow-tick detector entirely. The per-phase tick metrics (ace.world.tick.phase.*) are not affected by this switch")),
                ("weenie_bulk_load", new Property<bool>(ACE.Database.WeenieBulkLoadSettings.DefaultEnabled, "(non-retail function) World database: master switch for the bulk weenie loader. On: a weenie-cache miss reads its weenie through the shared bulk reader (one query per table), and bulk prefetches load many weenies with one query per table per chunk, each chunk self-checked against the legacy per-id read before it is published. Off: every miss uses the legacy per-id reader and every prefetch is a no-op, exactly as before the loader existed. Turning it off stops future bulk fills only; weenies already cached stay until a restart or /clearcache")),
                ("motion_stall_autoclear", new Property<bool>(ACE.Server.WorldObjects.MotionStallWatchdog.DefaultEnabled, "(non-retail function) Motion-stall watchdog: when TRUE, a FastTick player whose server-side animation queue has been continuously non-empty for longer than motion_stall_threshold_seconds AND whose turn-to / move-to has been blocked behind it for at least motion_stall_turn_blocked_seconds (on the ground, not teleporting, not casting) has the queue cleared server-side (as a portal does), so the corpse or object they clicked opens. Each clear writes one [MOTION_STALL] WARN line (rate-limited per player) as root-cause evidence. Mitigation only: why the queue sticks is unknown. Ships ON as a kill switch; FALSE restores the previous behaviour exactly. /motionstate <name> clear works regardless.")),
                ("landblock_weenie_prefetch", new Property<bool>(true, "(non-retail function) World database: when a landblock activates, bulk-load the weenies its placements and generators will create before creating them, so the first spawn of each is not a synchronous world-database read. Skipped when weenie_bulk_load is off. Declared ahead of its caller, which lands separately"))
                );

        public static readonly ReadOnlyDictionary<string, Property<long>> DefaultLongProperties =
            DictOf(
                ("account_vault_entry_cap", new Property<long>(500, "WaffleACE Mule Vendor: maximum ENTRIES an account may store, where one entry is one stored biota OR one collapsed-stackable ledger row - never the number of units held. 10,000 plain healing kits are ONE entry; ten individually tinkered arrows are ten. Enforced at deposit only and NEVER retroactively: lowering it below an account's existing holdings refuses further deposits but leaves everything withdrawable. This number also equals the number of lines the client vendor panel is asked to render, which is what live test 17.1 measures - set it from that result")),
                ("account_vault_landblock", new Property<long>(0x7FFF, "WaffleACE Mule Vendor: landblock id given as the Location of every vault CONTAINER biota. This landblock is never activated for players and holds no content. Its only purpose is risk R1: PurgeOrphanedBiotas keeps a biota only if it has a Container, Wielder or Location pointer, and a vault container has none of the first two. The purge keep-test exemption is the redundant second mitigation - both must exist, because a content edit can undo this one and an upstream merge can undo that one")),
                ("account_vault_summon_rot_seconds", new Property<long>(600, "WaffleACE Mule Vendor: seconds a summoned mule vendor survives without interaction. Refreshed on every interaction. One vendor per summoner; re-summoning destroys the old one")),
                ("alt_character_bonus_gap", new Property<long>(5, "Alt character bonus: minimum progression gap (1 level = 1 point at equal enlightenment) a character must be behind its account's furthest-along character before the bonus applies. 5 = the bonus is active only while the character is 5 or more levels behind. 0 restores the old behaviour of applying at any nonzero gap.")),
                ("char_delete_time", new Property<long>(3600, "the amount of time in seconds a deleted character can be restored")),
                ("charsheet_cache_seconds", new Property<long>(120, "WaffleACE Character Sheet: seconds a built sheet is served from memory before it is rebuilt from the character.")),
                ("chat_requires_account_time_seconds", new Property<long>(0, "the amount of time in seconds an account is required to have existed for for global chat privileges")),
                ("chat_requires_player_age", new Property<long>(0, "the amount of time in seconds a player is required to have played for global chat privileges")),
                ("chat_requires_player_level", new Property<long>(0, "the level a player is required to have for global chat privileges")),
                ("class_ability_acidproc_dot_ticks", new Property<long>(3, "class abilities: number of Acid Proc DoT ticks (3 ticks x 4s = 12s)")),
                ("class_ability_bloodmage_charge_stack_cap_r1", new Property<long>(3, "class abilities: Sanguine Reserve - maximum Blood Charge stacks at rank 1 (full ramp +21% life-magic damage)")),
                ("class_ability_bloodmage_charge_stack_cap_r2", new Property<long>(4, "class abilities: Sanguine Reserve - maximum Blood Charge stacks at rank 2 (full ramp +28% life-magic damage)")),
                ("class_ability_bloodmage_charge_stack_cap_r3", new Property<long>(5, "class abilities: Sanguine Reserve - maximum Blood Charge stacks at rank 3 (full ramp +35% life-magic damage)")),
                ("class_ability_crimsonharvest_max_targets", new Property<long>(4, "class abilities: Crimson Harvest - maximum EXTRA creatures one Drain strikes, beyond the primary target. Nearest first within class_ability_crimsonharvest_radius")),
                // Spellsword stack caps and guards. resonance/spellsurge max_stacks are the terms that bound
                // their respective ramps - Spellsurge's especially, since a proc-chance bonus earned by
                // proccing feeds itself and the cap is the only thing that closes the loop.
                ("class_ability_resonance_max_stacks", new Property<long>(5, "class abilities: Resonance - maximum stacks held at once (5, so the full ramp is +6/9/12% magic damage by rank)")),
                ("class_ability_pocketsand_puff_script", new Property<long>(0x330008B2, "class abilities: Pocket Sand - raw 0x33 PhysicsScript DataID played on the blinded creature at proc time (default 0x330008B2 = 855640242, a brown two-emitter splatter burst at the torso, owner pick 2026-08-17; renders in Content/preview/pocket_sand_vfx/). 0 = no puff, only the Dirty Fighting head marker")),
                ("class_ability_surefooted_stack_cap", new Property<long>(5, "class abilities: Surefooted - most stacks a player may hold. Rank-invariant on purpose: rank buys a bigger bonus per stack, not a deeper pool, so the ramp to full takes the same five evades at every rank. The other two Surefooted tunables are doubles and live in DefaultDoubleProperties")),
                ("class_ability_spellsurge_max_stacks", new Property<long>(5, "class abilities: Spellsurge - maximum stacks held at once (5, so the full ramp is +10 percentage points of war-proc chance). THE term that bounds the self-reinforcing loop")),
                // 2026-09-12 class ability overhaul: the two new abilities whose ladder is a stack COUNT
                // rather than a percentage. Registration only - see the matching entries in
                // DefaultDoubleProperties for the rest of Killer Instinct's and Kinetic Charge's tunables.
                ("class_ability_killerinstinct_max_stacks", new Property<long>(3, "class abilities: Killer Instinct - maximum Openings held at once")),
                ("class_ability_kineticcharge_charge_threshold", new Property<long>(5, "class abilities: Kinetic Charge - charges required before the next attack spends them all for the damage bonus")),
                ("class_ability_kineticcharge_cleave_targets", new Property<long>(2, "class abilities: Kinetic Charge - extra cleave targets on a MELEE attack that spends a full stack of charges (added to the weapon's own cleave and Whirlwind's; works on non-cleaving weapons)")),
                // Sundermark's level cap is a SAFETY VALVE at a permissive default, not part of the shipped
                // design: the user ruled the proc ships uncapped (level set by Life Magic alone), and 8 is
                // the top rung that exists. Lower it only to answer a live balance problem
                ("class_ability_sundermark_max_level", new Property<long>(8, "class abilities: Sundermark - highest Vulnerability spell level the proc may apply (8 = the Incantation, the top rung that exists). Safety valve; the level is otherwise set by Life Magic alone")),
                // Cascade's depth guard. A cascade child is itself a landed proc, so without this it could
                // cascade again; at 25% per link that converges rather than hanging, but it is unbounded in
                // the tail and no other class ability in the codebase chains at all. 1 = exactly one extra
                // hop, which keeps Echo Cast and Elemental Rend firing on the primary proc
                ("class_ability_cascade_max_generation", new Property<long>(1, "class abilities: Cascade - how many generations deep a proc may cascade (1 = the primary proc may fire one extra hop, and that hop may not cascade again)")),
                ("class_ability_lum_base_cost", new Property<long>(1000000, "class abilities: Luminance cost of the FIRST point bought with Luminance (/abilities buy); the piecewise curve (DESIGN sec 2b) escalates from here. There is NO cap on points bought - the rising price is the only limiter")),
                ("class_ability_lum_breakpoint_1", new Property<long>(10, "class abilities: the point number at which the Luminance-purchase curve switches from ratio_1 to the steeper ratio_2")),
                ("class_ability_lum_breakpoint_2", new Property<long>(20, "class abilities: the point number at which the Luminance-purchase curve switches from ratio_2 to the (uncapped) tail ratio_3")),
                ("class_ability_xp_base_cost", new Property<long>(200000000000, "class abilities: EXPERIENCE cost of the FIRST point bought with xp (/abilities buyxp, and the Drift Network exchange stone). Each point costs 1.4x the last, forever, with no cap - the rising price is the only limiter. Roughly one full 1-to-275 relevel (191,226,310,247) for the first point. See Docs/ClassAbilities/XP-LANE-SPEC.md sec 3")),
                ("class_ability_xp_breakpoint_1", new Property<long>(1, "class abilities: point number at which the xp-purchase curve switches from ratio_1 to ratio_2. INERT while all three ratios are equal - the curve is a single geometric run by default; this exists so it can be bent later without a code change")),
                ("class_ability_xp_breakpoint_2", new Property<long>(1, "class abilities: point number at which the xp-purchase curve switches from ratio_2 to ratio_3. INERT while all three ratios are equal")),
                ("class_ability_xp_min_level", new Property<long>(275, "class abilities: minimum character level to buy a class ability point with experience. Below this the exchange competes with skill and attribute training for the same AvailableExperience; at and above it skills are effectively maxed and the pool is otherwise idle")),
                ("class_ability_full_respec_lum_cost", new Property<long>(1000000, "class abilities: flat Luminance fee at the Drift Network full-respec NPC, which unlearns every learned class ability and refunds all spent points at once (tier unlocks are not refunded and not reset). Waived entirely below class_ability_full_respec_free_below_level")),
                ("class_ability_full_respec_free_below_level", new Property<long>(275, "class abilities: character level at and above which the Drift Network full-respec NPC starts charging class_ability_full_respec_lum_cost. Below this level the full respec is FREE - a character still levelling should be able to re-plan its build without paying an endgame Luminance price. Set to 0 to charge the flat fee at every level")),
                ("facet_slot2_level", new Property<long>(300, "Player Facets: character level at which the SECOND facet slot unlocks. Checked only when switching TO a slot, never re-checked afterwards, so raising this can never strand a character on a slot they already use")),
                ("facet_slot3_level", new Property<long>(400, "Player Facets: character level at which the THIRD facet slot unlocks. Same one-way check as facet_slot2_level")),
                ("facet_slot4_level", new Property<long>(500, "Player Facets: character level at which the FOURTH facet slot unlocks. Same one-way check as facet_slot2_level")),
                ("facet_pk_level", new Property<long>(150, "Player Facets: character level at which the PK facet (/facet pk) unlocks. Same one-way check as facet_slot2_level: checked only when switching TO the PK facet")),
                ("pvp_arena_accept_seconds", new Property<long>(20, "PvP Arena (Docs/Pvp/DESIGN.md): seconds a formed match's accept popup stays open. Must stay at or under 30, because ConfirmationManager's popup itself times out at 30 seconds")),
                ("pvp_arena_staging_timeout_seconds", new Property<long>(30, "PvP Arena: seconds allowed for the Staging phase (post-accept, pre-countdown) before the match is canceled")),
                ("pvp_arena_countdown_seconds", new Property<long>(10, "PvP Arena: seconds of countdown before a match goes Live")),
                ("pvp_arena_time_limit_seconds_1v1", new Property<long>(300, "PvP Arena: 1v1 regulation time limit in seconds. With pvp_arena_overtime_enabled the match then goes to overtime; otherwise (or when overtime also runs out) it ends in an unrated timeout draw")),
                ("pvp_arena_time_limit_seconds_2v2", new Property<long>(300, "PvP Arena: 2v2 regulation time limit in seconds. With pvp_arena_overtime_enabled the match then goes to overtime; otherwise (or when overtime also runs out) it ends in an unrated timeout draw")),
                ("pvp_arena_time_limit_seconds_ffa", new Property<long>(300, "PvP Arena: Tugak Brawl regulation time limit in seconds. With pvp_arena_overtime_enabled the match then goes to overtime; otherwise (or when overtime also runs out) the survivors share first place, rated")),
                ("pvp_arena_overtime_seconds", new Property<long>(180, "PvP Arena (Docs/Pvp/DESIGN.md \"Overtime\"): how long overtime lasts once a match reaches its regulation time limit with no winner, in every mode. Snapshotted onto the match when overtime starts. 0 or negative = overtime off. Defaults 180")),
                ("pvp_arena_blood_win", new Property<long>(2, "PvP Arena (Docs/Pvp/DESIGN.md \"Rewards\"): Marks of the Hopeslayer paid to each winner of a match (Tugak Brawl: first place, shared first included). A forfeiter is never paid. 0 or negative pays nothing and does not count toward the daily cap. Defaults 2")),
                ("pvp_arena_blood_loss", new Property<long>(1, "PvP Arena (Docs/Pvp/DESIGN.md \"Rewards\"): Marks of the Hopeslayer paid to each loser who did not forfeit (Tugak Brawl: every non-forfeiter below first place). 0 or negative pays nothing and does not count toward the daily cap. Defaults 1")),
                ("pvp_arena_blood_draw", new Property<long>(1, "PvP Arena (Docs/Pvp/DESIGN.md \"Rewards\"): Marks of the Hopeslayer paid to each participant of a drawn match (timeout draw, double knockout) who did not forfeit. 0 or negative pays nothing and does not count toward the daily cap. Defaults 1")),
                ("pvp_arena_blood_daily_cap", new Property<long>(10, "PvP Arena (Docs/Pvp/DESIGN.md \"Rewards\"): at most this many PAID matches per character per arena day (pvp_arena_blood_reset_timezone / _hour); further matches that day pay 0 and the player is told once per match. Marks owed for a full pack counts when earned, not again when delivered. 0 or negative = no daily cap. Defaults 10")),
                ("pvp_arena_blood_reset_hour", new Property<long>(0, "PvP Arena (Docs/Pvp/DESIGN.md \"Rewards\"): the hour (0-23) in pvp_arena_blood_reset_timezone at which the arena day - and with it the Mark of the Hopeslayer daily cap - turns over. Clamped to [0, 23] at read. Defaults 0")),
                ("pvp_bg_marks_win", new Property<long>(2, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Rewards\"): Marks of the Hopeslayer paid to each member of the winning team of a battleground match who did not forfeit. Paid only while pvp_arena_blood_enabled (the shared master switch) is on; nothing for a match that never went Live or for opposing teams sharing an IP. 0 or negative pays nothing and does not count toward pvp_bg_marks_daily_cap. Multiplied by pvp_bg_marks_scale. Defaults 2")),
                ("pvp_bg_marks_loss", new Property<long>(1, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Rewards\"): Marks of the Hopeslayer paid to each member of the losing team of a battleground match who did not forfeit. 0 or negative pays nothing and does not count toward pvp_bg_marks_daily_cap. Multiplied by pvp_bg_marks_scale. Defaults 1")),
                ("pvp_bg_marks_draw", new Property<long>(1, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Rewards\"): Marks of the Hopeslayer paid to each participant of a drawn battleground match (a timeout tie) who did not forfeit. 0 or negative pays nothing and does not count toward pvp_bg_marks_daily_cap. Multiplied by pvp_bg_marks_scale. Defaults 1")),
                ("pvp_bg_marks_daily_cap", new Property<long>(10, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Rewards\"): at most this many PAID battleground matches per character per day, on its own count independent of pvp_arena_blood_daily_cap (an arena day and a battleground day never consume each other). The day turns over at pvp_arena_blood_reset_timezone / pvp_arena_blood_reset_hour. Further matches that day pay 0 and the player is told once per match. 0 or negative = no daily cap. Defaults 10")),
                ("pvp_arena_post_match_seconds", new Property<long>(10, "PvP Arena: seconds a resolved match stays in Resolving before the instance is released")),
                ("pvp_arena_max_concurrent_matches", new Property<long>(10, "PvP Arena: maximum number of arena matches allowed to be live at once, across every mode")),
                ("pvp_arena_min_level", new Property<long>(50, "PvP Arena: minimum character level to join any arena queue")),
                ("pvp_arena_decline_lockout_seconds", new Property<long>(120, "PvP Arena: seconds a player who declined or no-showed an accept popup is locked out of requeueing (1v1/2v2 only)")),
                ("pvp_arena_mm_window_initial", new Property<long>(100, "PvP Arena: PairMatchmaker's initial 1v1 rating window")),
                ("pvp_arena_mm_window_growth_per_minute", new Property<long>(50, "PvP Arena: PairMatchmaker's 1v1 rating window growth per minute waited")),
                ("pvp_arena_mm_window_max", new Property<long>(400, "PvP Arena: PairMatchmaker's 1v1 rating window ceiling, however long a player has waited")),
                ("pvp_arena_duo_vs_solo_after_seconds", new Property<long>(120, "PvP Arena: seconds a queued 2v2 duo waits for another duo before DuoMatchmaker will pair it against two solo queuers instead")),
                ("pvp_arena_ffa_target_players", new Property<long>(5, "PvP Arena: LobbyMatchmaker's target Tugak Brawl lobby size before wait-time decay shrinks it")),
                ("pvp_arena_ffa_min_players", new Property<long>(5, "PvP Arena: LobbyMatchmaker's minimum Tugak Brawl lobby size - the floor wait-time decay shrinks the needed size down to")),
                ("pvp_arena_ffa_max_players", new Property<long>(15, "PvP Arena: LobbyMatchmaker's maximum Tugak Brawl lobby size - at most this many are ever taken into one Tugak Brawl match")),
                ("pvp_arena_ffa_min_decay_seconds", new Property<long>(60, "PvP Arena: seconds of oldest-entrant wait time per one point of Tugak Brawl needed-size decay")),
                ("pvp_arena_crier_interval_seconds", new Property<long>(900, "PvP Arena Crier: seconds between periodic /lfg queue announcements (one line per non-empty, enabled queue that still needs players)")),
                ("pvp_arena_crier_last_call_delay_seconds", new Property<long>(15, "PvP Arena Crier: seconds after a queue's needed count grows to exactly 1 before the last-call announcement fires, if the queue still needs exactly 1 then")),
                ("pvp_arena_crier_last_call_cooldown_seconds", new Property<long>(120, "PvP Arena Crier: seconds a queue must wait after firing a last call before it can arm another one, so join/leave/rejoin churn in 1v1 cannot spam it")),
                ("pvp_rating_initial", new Property<long>(1500, "PvP Arena: initial Elo rating for a character with no rating row yet, on any of the three ladders")),
                ("pvp_rating_k_provisional", new Property<long>(40, "PvP Arena: Elo K-factor for a player's first pvp_rating_provisional_games games")),
                ("pvp_rating_k_established", new Property<long>(24, "PvP Arena: Elo K-factor once a player has played pvp_rating_provisional_games games or more")),
                ("pvp_rating_k_ffa", new Property<long>(32, "PvP Arena: K-factor used by the Tugak Brawl pairwise placement rating model")),
                ("pvp_rating_provisional_games", new Property<long>(10, "PvP Arena: number of games at which a player's K-factor switches from pvp_rating_k_provisional to pvp_rating_k_established")),
                ("pvp_rating_decay_grace_days", new Property<long>(14, "PvP Arena: days of inactivity before rating decay begins (read-time only, never a background job)")),
                ("pvp_rating_decay_points_per_week", new Property<long>(25, "PvP Arena: rating points lost per full week of inactivity past the grace period")),
                ("pvp_rating_decay_floor", new Property<long>(1500, "PvP Arena: rating decay never drops a player below this value, and a player already at or below it does not decay")),
                ("pvp_arena_healkit_skill_cap_1v1", new Property<long>(150, "PvP Arena (Docs/Pvp/DESIGN.md \"Tunables\"): the healing-skill bonus cap applied to a healer or target bound to the same Live 1v1 match. Ported from Doctide `arena_1v1_healkit_skill_bonus_cap`. Defaults 150")),
                ("pvp_bg_min_players", new Property<long>(4, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): minimum queued players before a battleground match can form once the fill window closes. Defaults 4")),
                ("pvp_bg_max_players", new Property<long>(12, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): queued player count at which a battleground match forms at once, and the most players in one match. Defaults 12")),
                ("pvp_bg_fill_window_seconds", new Property<long>(120, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): seconds after the queue reaches pvp_bg_min_players before a battleground match forms, unless pvp_bg_max_players is reached first. Defaults 120")),
                ("pvp_bg_max_premade_size", new Property<long>(6, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): largest premade group (fellowship) that may queue together. Clamped to 6 and to the fellowship member cap at read. Defaults 6")),
                ("pvp_bg_premade_imbalance_tolerance", new Property<long>(2, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): most premade players by which the two teams of a battleground match may differ. Defaults 2")),
                ("pvp_bg_premade_vs_solo_after_seconds", new Property<long>(120, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): seconds an unbalanced premade waits before the battleground matchmaker lets it face teams with fewer premade players than the tolerance allows. Defaults 120")),
                ("pvp_bg_max_concurrent_matches", new Property<long>(2, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): most battleground matches live at once. Counted inside the global pvp_arena_max_concurrent_matches cap. Defaults 2")),
                ("pvp_bg_respawn_seconds", new Property<long>(30, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): seconds a dead battleground player waits in their team pen before returning to a team spawn at full vitals. Defaults 30")),
                ("pvp_bg_spawn_protection_seconds", new Property<long>(3, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Spawn protection\"): protection from other players (melee, missile, harmful spells, projectiles landing while protected and damage-over-time ticks). Granted when the match goes Live and at each confirmed respawn. On a map with spawn rooms (Attack/Defend) it holds for as long as the player stands in their own team's room, and these N seconds start when they leave it; on a map without rooms it is a plain N seconds. Attacking or a harmful cast ends it at once. Never in an arena mode. 0 disables it. Defaults 3. Read once per match, at formation")),
                ("pvp_bg_time_limit_seconds_koth", new Property<long>(900, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): King of the Hill match time limit in seconds. At the limit the higher score wins, then more team kills, else a rated draw. Defaults 900")),
                ("pvp_bg_koth_score_target", new Property<long>(300, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): King of the Hill score at which a team wins. 300 is 5 minutes of uncontested hold at the default hold points and tick. Defaults 300")),
                ("pvp_bg_koth_tick_seconds", new Property<long>(5, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): seconds between King of the Hill scoring and drain ticks. Defaults 5")),
                ("pvp_bg_koth_hold_points", new Property<long>(5, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): points the holding team gains each King of the Hill tick. Defaults 5")),
                ("pvp_bg_koth_decay_points", new Property<long>(2, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): points the non-holding team loses each King of the Hill tick while the other team holds, floored at 0. Defaults 2")),
                ("pvp_bg_koth_min_holders", new Property<long>(1, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): players a team needs on the King of the Hill point to hold it. Defaults 1")),
                ("pvp_bg_koth_drain_health", new Property<long>(50, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): health drained from every counted player on the King of the Hill point each tick. Defaults 50")),
                ("pvp_bg_koth_drain_stamina", new Property<long>(50, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): stamina drained from every counted player on the King of the Hill point each tick. Defaults 50")),
                ("pvp_bg_koth_drain_mana", new Property<long>(50, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): mana drained from every counted player on the King of the Hill point each tick. Defaults 50")),
                ("pvp_bg_koth_score_announce_seconds", new Property<long>(10, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): seconds between King of the Hill score announcements. Defaults 10")),
                ("pvp_bg_koth_hill_moves", new Property<long>(2, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Moving hill\"): how many times the King of the Hill zone moves over a match. Move k fires when the leading team's score reaches k/(moves+1) of pvp_bg_koth_score_target, or the match time reaches k/(moves+1) of the time limit, whichever comes first, and sends the hill to the trailing team's room (the freed spawn room on its side); if it is already there it returns to the centre. A tie goes to the opposite room, or a coin flip from the centre. 0 keeps the hill at the centre all match. Clamped to 0..3. Read when a match forms. Defaults 2")),
                ("pvp_bg_time_limit_seconds_ad", new Property<long>(600, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): Attack/Defend match time limit in seconds, at least 60. When it runs out the defenders win (a rated win, never a draw). Applies to the next match that forms. Defaults 600")),
                ("pvp_bg_ad_crystal_count", new Property<long>(0, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): crystals the defenders guard per match. 0 uses the map default; otherwise clamped to 1..4 and to the number of crystal sites on the map. Applies to the next match that forms. Defaults 0")),
                ("pvp_bg_ad_crystal_health_per_attacker", new Property<long>(5000, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): health of each crystal per attacker on the attacking team at formation, at least 1. Applies to the next match that forms. Defaults 5000")),
                ("pvp_bg_ad_respawn_seconds_attackers", new Property<long>(30, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): seconds a dead attacker waits in the attacker pen before returning to a spawn, at least 0. Applies to the next match that forms. Defaults 30")),
                ("pvp_bg_ad_respawn_seconds_defenders", new Property<long>(30, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): seconds a dead defender waits in the defender pen before returning to a spawn, at least 0. Applies to the next match that forms. Defaults 30")),
                ("pvp_bg_ad_alert_step_percent", new Property<long>(25, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): a defender alert fires each time a crystal loses another step of this many percent of its health, clamped to 5..100. Applies to the next match that forms. Defaults 25")),
                ("pvp_bg_ad_alert_min_interval_seconds", new Property<long>(10, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): fewest seconds between two crystal-under-attack alerts to the defenders, at least 1. Applies to the next match that forms. Defaults 10")),
                ("pvp_bg_ad_status_announce_seconds", new Property<long>(60, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Settings\"): seconds between the periodic Attack/Defend status lines (crystals destroyed and time left), at least 5. Applies to the next match that forms. Defaults 60")),
                ("pvp_damage_cap", new Property<long>(0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): absolute per-hit ceiling on player-vs-player damage, applied to the attacker's final number before the defender's cloak and ward reductions, at melee/missile hits, war, void and life health projectiles, Harm and Drain Health (a capped Drain also heals the caster less). DoT ticks are not capped. The smaller of this and pvp_damage_cap_max_health_fraction wins. 0 = off")),
                ("pvp_health_floor", new Property<long>(0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): effective-HP normalization lower bound. A player-vs-player hit on a defender whose max health H is below this becomes hit x H / floor, so a low-health defender takes proportionally less. Pre-cap at M1/M2 (melee, missile, projectiles) and N3/N4 (Harm, Drain Health). Heals are not scaled. 0 = off. If both bounds are set and floor > ceiling the pair is ignored (logged once)")),
                ("pvp_health_ceiling", new Property<long>(0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): effective-HP normalization upper bound. A player-vs-player hit on a defender whose max health H is above this becomes hit x H / ceiling, so a high-health defender takes proportionally more. Pre-cap at M1/M2 (melee, missile, projectiles) and N3/N4 (Harm, Drain Health). Heals are not scaled. 0 = off. If both bounds are set and floor > ceiling the pair is ignored (logged once)")),
                ("pvp_consumable_min_interval_ms", new Property<long>(0, "Arena only: minimum milliseconds between consumable uses while a player is in a Live arena match (a PK timer alone does not apply it); a use inside the interval is refused and the item kept, at choke point H2. Food and animated gems only; healing kits and instant gems are not throttled. 0 = off")),
                ("pvp_dispel_vuln_lock_seconds", new Property<long>(300, "PvP rules: a vulnerability cast by ANOTHER player (not self, not a monster) cannot be dispelled or Cleansed while the target's last PK attack is within this many seconds, at choke points D1/D2. 0 = off")),
                ("pvp_template_backstop_seconds", new Property<long>(10, "PvP Template Facets (Docs/Pvp/TEMPLATES.md): a templated player with no live arena or battleground match for this many seconds is restored by the heartbeat backstop. 0 or negative falls back to 10")),
                ("class_ability_respec_lum_cost", new Property<long>(1000000, "class abilities: Luminance fee to unlearn a class ability (/abilities unlearn)")),
                ("class_ability_token_lum_per_point", new Property<long>(100000, "class abilities: Luminance cost of a skill token, per class ability point that token's rank costs (/abilities token buy)")),
                ("class_ability_tier2_cap_required", new Property<long>(3, "class abilities: total class ability points a character must have earned before any Tier 2 skill can be learned")),
                ("class_ability_tier2_spent_required", new Property<long>(5, "class abilities: points a character must have spent within a class before that class's Tier 2 skills unlock")),
                ("class_ability_tier3_cap_required", new Property<long>(8, "class abilities: total class ability points a character must have earned before any Tier 3 skill can be learned")),
                ("class_ability_tier3_spent_required", new Property<long>(15, "class abilities: points a character must have spent within a class before that class's Tier 3 skills unlock")),
                ("corpse_spam_limit", new Property<long>(15, "the number of corpses a player is allowed to leave on a landblock at one time")),
                ("custom_aug_cost_index_cap", new Property<long>(20, "Custom Dreamweave Augmentations: Fibonacci index the broker price plateaus at. A buyer who already owns more than this many of one augmentation keeps paying the price at this index rather than a still-doubling one. 20 = 10,946 gems. Also the overflow guard - fib(47) already exceeds int.MaxValue")),
                ("custom_aug_gem_wcid", new Property<long>(29295, "Custom Dreamweave Augmentations: wcid of the currency the Fi / Bo / Nacci brokers take, default 29295 'Blank Augmentation Gem'. The gem carries no MaxStackSize row, so N gems are N discrete items and the broker's have-versus-cost check counts items")),
                ("player_corpse_max_lifetime_seconds", new Property<long>(604800, "Maximum wall-clock lifetime in seconds for a player corpse, enforced both as a cap on the level-scaled decay timer and as an absolute deadline checked when the corpse's landblock loads. Default 604800 = 7 days. 0 disables the cap.")),
                ("default_subscription_level", new Property<long>(1, "retail defaults to 1, 1 = standard subscription (same as 2 and 3), 4 grants ToD pre-order bonus item Asheron's Benediction")),
                ("fellowship_addlandblock_cooldown", new Property<long>(60, "WaffleACE: seconds a player must wait between /fship addlandblock uses. The command sends an invite to everyone unfellowed on the landblock, so it is rate-limited to stop invite spam.")),
                ("fellowship_recruit_reject_cooldown", new Property<long>(30, "WaffleACE: seconds before a repeat fellowship recruit rejection ('X is already a member of a Fellowship', 'X is busy') is shown again for the same recruiter/target pair. Client-side auto-recruiters retry a doomed recruit on a timer, which spammed the recruiter. 0 disables the throttle.")),
                ("fellowship_even_share_level", new Property<long>(50, "level when fellowship XP sharing is no longer restricted. Only has effect while 'fellowship_level_restrictions' is true.")),
                ("fellowship_leech_rejoin_lockout", new Property<long>(3600, "WaffleACE: seconds a player ejected by /fship noleech leech management must wait before rejoining that same fellowship. Defaults to 3600 (1 hour).")),
                ("fellowship_leech_timeout", new Property<long>(1200, "WaffleACE: seconds a member may go without contributing XP to the fellowship before /fship noleech ejection removes them. Defaults to 1200 (20 minutes).")),
                ("fellowship_max_members", new Property<long>(20, "WaffleACE: maximum number of members in a fellowship. Retail is 9. Clamped to [1, 100]. Total group XP is held flat past 9 by 'fellowship_share_group_plateau', so a larger fellowship never out-earns a smaller one.")),
                ("fellowship_password_max_attempts", new Property<long>(5, "WaffleACE: wrong join-password guesses (/fship join, /fship joinlandblock, the 'xp' tell) allowed against one fellowship before the guesser is locked out for 'fellowship_password_lockout' seconds. <= 0 disables the lockout entirely.")),
                ("fellowship_password_lockout", new Property<long>(300, "WaffleACE: seconds a player is locked out of guessing a fellowship's join password after 'fellowship_password_max_attempts' wrong guesses. Defaults to 300 (5 minutes).")),
                ("ip_limit_max_free", new Property<long>(1, "WaffleACE: IP active-player limit - maximum characters from one non-exempt IP that may be in-world OUTSIDE the mule_landblocks set. Only takes effect while ip_limit_enabled is TRUE")),
                ("ip_limit_max_confined", new Property<long>(2, "WaffleACE: IP active-player limit - ADDITIONAL characters from one non-exempt IP allowed beyond ip_limit_max_free, each of which must currently be inside a mule landblock (mule_landblocks). The total in-world cap for an IP is ip_limit_max_free + ip_limit_max_confined. Only takes effect while ip_limit_enabled is TRUE")),
                ("ip_limit_exempt_access_level", new Property<long>(2, "WaffleACE: IP active-player limit - accounts whose access level is at or above this value (2 = AccessLevel.Sentinel) are automatically exempt from the limit. Set to -1 to disable auto-exemption by access level entirely")),
                ("ip_limit_grace_seconds", new Property<long>(30, "WaffleACE: IP active-player limit - seconds of warning grace given to a character that comes to violate the limit after login (e.g. it walks out of a mule landblock) before it is logged off")),
                ("ip_limit_sweep_seconds", new Property<long>(10, "WaffleACE: IP active-player limit - how often, in seconds, the periodic re-check sweep runs to catch characters that have moved into or out of mule_landblocks since login")),
                ("ip_limit_activity_tail_seconds", new Property<long>(15, "WaffleACE: IP active-player limit - seconds a character keeps counting as CONFINED after a live arena match or Proving Grounds run last saw it, as long as it is still inside an ephemeral realm instance. Covers the gap between a match/run ending and the exit teleport actually landing the character back outside. 0 disables the tail entirely")),
                ("mansion_min_rank", new Property<long>(6, "overrides the default allegiance rank required to own a mansion")),
                ("market_max_listings_per_account", new Property<long>(100, "WaffleACE Market: the most ACTIVE listings one account may hold at once. Bounds the in-memory index and the delta feed, which every web client mirrors in full, rather than anything about fairness. 0 or less means one")),
                ("market_buy_order_max_days", new Property<long>(30, "WaffleACE Market: how many days a new Wanted buy order stays open before the expiry pass closes it and refunds the remaining escrow. Read at placement, so changing it affects new orders only. Clamped to [1, 365]")),
                ("market_buy_order_max_count", new Property<long>(100, "WaffleACE Market: the most bags one Wanted buy order may ask for. Bounds the escrow a single order can hold. Clamped to [1, 10000]")),
                ("market_max_price_mmd", new Property<long>(1000000, "WaffleACE Market: the highest per-unit price in Trade Notes (250,000 pyreals each) a listing may carry. Exists to make a fat-finger listing refuse rather than sit at an absurd number. It layers UNDER MarketManager.MaxPriceMmd, the hard bound above which a bank credit is impossible; 0 or less disables this soft cap and leaves only the hard one. Not an economic control")),
                ("market_reject_retention_days", new Property<long>(30, "WaffleACE Market: how many days of market_rejected_attempt rows to keep. A pruning pass runs hourly on the audit writer's own background thread and deletes anything older. Clamped to [1, 3650]. Unlike account_vault_log, whose retention is unbounded, this table records refusals rather than movements of value, so it is bounded on purpose")),
                ("market_ad_interval_hours", new Property<long>(0, "WaffleACE Market: how often, in hours, MarketAdvertiserJob posts a short advert to Trade chat naming the newest active listing and pointing at the web market. 0 disables it entirely. The schedule is wall-clock UTC hour spacing measured from UTC midnight (an interval of 6 fires at 00:00, 06:00, 12:00, 18:00), not from whenever the server started. Clamped to at most 24, so a larger value collapses to one post a day at 00:00 UTC. Read fresh every 30 seconds by the job's own tick, so changing it takes effect live without a restart")),
                ("vault_barrel_retention_days", new Property<long>(30, "WaffleACE Barrel: how many days a barreled vault item stays restorable by an administrator before it is permanently destroyed. A sweep runs hourly; rows past this age have their item destroyed and purged_At stamped, and the account_vault_barrel ROW is kept so an investigation can still see what was thrown away. Clamped to [1, 3650]. Restoring is impossible after the purge, by design - this is the bound on how long a soft delete stays soft")),
                ("offline_bonus_max_seconds", new Property<long>(86400, "Offline bonus: maximum banked offline bonus time, in seconds, a character can accrue. Defaults to 86400 (24 hours).")),
                ("offline_bonus_idle_timeout_seconds", new Property<long>(300, "Offline bonus: seconds of no qualifying combat XP/Luminance before an online character is considered idle for offline-bonus purposes. While active (earning qualifying XP, plus this trailing window after) the bank drains; once idle it accrues again instead. Defaults to 300 (5 minutes).")),
                ("max_chars_per_account", new Property<long>(11, "retail defaults to 11, client supports up to 20")),
                ("mule_strength", new Property<long>(3300, "Mule system: the Strength a mule is given at conversion, which is what buys its carrying capacity (retail formula, 150 * Strength, so 3300 gives 495,000). Capacity must be bought this way rather than overridden: verified in game 2026-08-02, the client derives the burden bar from Strength and ignores a server-sent EncumbranceCapacity, so an override reads as a permanently overloaded mule. Raising this also raises Jump, which is why mules are exempt from fall damage - see Player_Mule and Player_Move.TakeDamage_Falling. Clamped to 1-9999 at conversion")),
                ("mule_level", new Property<long>(180, "Mule system: the level a character is set to on mule conversion. TotalExperience is set to this level's threshold on the same curve CheckForLevelup reads, so level and XP cannot desync")),
                ("mule_max_convert_level", new Property<long>(50, "Mule system: highest character level still eligible for mule conversion. This is the ONLY progress gate - a character is level 8+ on leaving the training hall, and every character holds quest registry rows from the stamp ledger, so level is the only workable measure. Raising it lets more developed characters be converted; conversion is irreversible either way")),
                ("mule_pack_wcid", new Property<long>(1001941, "Mule system: wcid of the Bearer's Pack handed to a character by MuleConversion.HandleMuleRequest, once on conversion and again on every subsequent interaction with an already-converted mule. No limit on repeat grants")),
                ("capacity_upgrade_base_cost_mmd", new Property<long>(500, "WaffleACE: MMD price of the FIRST account capacity upgrade of either kind (CapacityUpgradePricing.CostMmd, owned=0). Read by CapacityUpgradeBroker only - the pricing math itself never reads PropertyManager")),
                ("capacity_upgrade_cost_growth_percent", new Property<long>(20, "WaffleACE: percent the account capacity upgrade price grows per upgrade already owned of that kind (CapacityUpgradePricing.CostMmd's growthPct). 20 means each successive upgrade of the same kind costs 120% of the previous one's price, compounded exactly rather than stepped")),
                ("mule_upgrade_max_count", new Property<long>(100, "WaffleACE: the most /mule upgrade purchases one account may make (CapacityUpgradeKind.MuleVault), clamped to CapacityUpgradePricing.HardMaxPurchases (100). Lowering it only blocks NEW purchases - upgrades already bought keep applying")),
                ("market_upgrade_max_count", new Property<long>(100, "WaffleACE: the most /market upgrade purchases one account may make (CapacityUpgradeKind.MarketListings), clamped to CapacityUpgradePricing.HardMaxPurchases (100). Lowering it only blocks NEW purchases - upgrades already bought keep applying")),
                ("pk_timer", new Property<long>(20, "the number of seconds where a player cannot perform certain actions (ie. teleporting) after becoming involved in a PK battle")),
                ("player_save_interval", new Property<long>(300, "the number of seconds between automatic player saves")),
                ("rares_max_days_between", new Property<long>(45, "for rares_real_time_v2: the maximum number of days a player can go before a rare is generated on rare eligible creature kills")),
                ("rares_max_seconds_between", new Property<long>(5256000, "for rares_real_time: the maximum number of seconds a player can go before a second chance at a rare is allowed on rare eligible creature kills that did not generate a rare")),
                ("salvage_bag_value_cap", new Property<long>(150000, "WaffleACE: maximum pyreal Value one salvage bag can reach, applied in Player.AddSalvage to both fresh salvaging and bag combining. Retail cap was 75000. Clamped to [0, int.MaxValue] at read")),
                // Salvage forge (WaffleACE) price bands. BagsRequired reads all three: skill < _9 costs 10
                // bags, [skillFor9, skillFor8) costs 9, [skillFor8, skillFor7) costs 8, skill >= skillFor7
                // costs 7. Compared against the player's CURRENT (buffed) value in the material's tinkering
                // skill. See ACE.Server/Entity/SalvageForge.cs.
                ("salvage_forge_skill_for_9", new Property<long>(500, "salvage forge: tinkering skill value at which forging a Hammer drops from 10 required full bags to 9")),
                ("salvage_forge_skill_for_8", new Property<long>(700, "salvage forge: tinkering skill value at which forging a Hammer drops from 9 required full bags to 8")),
                ("salvage_forge_skill_for_7", new Property<long>(900, "salvage forge: tinkering skill value at which forging a Hammer drops from 8 required full bags to 7, the cheapest band")),
                ("summoning_killtask_multicredit_cap", new Property<long>(2, "if allow_summoning_killtask_multicredit is enabled, the maximum # of killtask credits a player can receive from 1 kill")),
                ("survival_stall_eject_seconds", new Property<long>(60, "(non-retail function) Proving Grounds (Defense) survival arena: seconds with no arena creature attacking the player (a hit, an evade or a resisted spell all count) before the run is treated as broken - logged as an ERROR, forfeited with NO score and the player sent home. Checked on each escalation tick, so the real window is this plus up to one tick interval. Catches an arena whose creatures exist server-side but never engage (the 2026-09-30 double-entry incident). 0 disables the stall test; the no-creatures and no-exit-portal tests always run")),
                ("teleport_visibility_fix", new Property<long>(0, "Fixes some possible issues with invisible players and mobs. 0 = default / disabled, 1 = players only, 2 = creatures, 3 = all world objects")),
                ("dynamic_dungeons_max_concurrent_runs", new Property<long>(16, "(non-retail function) Threads: server-wide cap on live runs, a load safety valve; 0 = unlimited")),
                ("dynamic_dungeons_max_runs_per_account", new Property<long>(1, "(non-retail function) Threads: live runs one account may hold at once; 0 = unlimited")),
                ("dynamic_dungeons_run_ttl_minutes", new Property<long>(180, "(non-retail function) Threads: minutes from gem use until the run is reaped regardless of state; minimum 5")),
                ("dynamic_dungeons_empty_grace_minutes", new Property<long>(10, "(non-retail function) Threads: minutes a Starting or Active run's private copy survives with nobody inside before it unloads and the run ends; the owner is warned at 5m, 2m, 1m and 30s. Cleared runs are unaffected. Read when the run opens; clamped to [5, 60]")),
                ("dynamic_dungeons_loot_tier_cap", new Property<long>(8, "(non-retail function) Threads: highest TreasureDeath tier a gem may roll into a run's loot profile; gear ratings only roll at 8")),
                ("dynamic_dungeons_max_monsters_per_run", new Property<long>(120, "(non-retail function) Threads: hard cap on non-boss creatures placed in one run")),
                ("dynamic_dungeons_min_monsters_per_run", new Property<long>(40, "(non-retail function) Threads: floor on non-boss creatures placed in one run; when a dungeon has fewer curated points than this, extra creatures share curated points; 0 disables the floor; never exceeds dynamic_dungeons_max_monsters_per_run")),
                ("dynamic_dungeons_spawn_batch_size", new Property<long>(20, "(non-retail function) Threads: HARD CAP on creatures placed per landblock action-queue step while populating. Since dynamic_dungeons_spawn_step_budget_ms, time decides a step and this only caps it")),
                ("dynamic_dungeons_spawn_step_budget_ms", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonSpawner.DefaultSpawnStepBudgetMs, "(non-retail function) Threads: wall-clock milliseconds one creature-placement step may spend; the step stops after the creature during which it crosses this, then re-enqueues itself onto the run landblock's action queue for the next world tick. dynamic_dungeons_spawn_batch_size still caps the creatures per step. Every step places at least one creature. Read when the run's population starts; clamped to [1, 100]. 100 is generous enough that the batch size normally ends a step first, approximating the pre-budget behaviour")),
                ("dynamic_dungeons_max_unloads_per_tick", new Property<long>(ACE.Server.Realms.EphemeralUnloadThrottle.DefaultMaxUnloadsPerTick, "(non-retail function) Threads: most EPHEMERAL landblocks (Thread copies and other private instances) the world thread unloads in one tick; the rest stay queued, still registered, for later ticks. Ordinary world landblocks are never capped, and shutdown drains everything. Read every tick; clamped to [1, 100]")),
                ("dynamic_dungeons_cache_roll_batch_size", new Property<long>(ACE.Server.ThreadDungeons.ThreadLootRollBudget.DefaultRollsPerStep, "(non-retail function) Threads: HARD CAP on pooled loot ROLLS one cache-delivery step materialises before it re-enqueues itself onto the run landblock's action queue, so clear-time loot generation is spread across world ticks instead of freezing one. Read when the run opens; clamped to [1, 1000]. Raising it makes a clear finish in fewer, longer steps; 1000 is effectively the old unbatched single pass. Since dynamic_dungeons_cache_step_budget_ms, time decides a step and this only caps it")),
                ("dynamic_dungeons_cache_step_budget_ms", new Property<long>(ACE.Server.ThreadDungeons.ThreadLootRollBudget.DefaultStepBudgetMs, "(non-retail function) Threads: wall-clock milliseconds one pooled-loot cache-delivery step may spend materialising loot; the step stops after the roll during which it crosses this, then re-enqueues itself onto the run landblock's action queue for the next world tick. dynamic_dungeons_cache_roll_batch_size still caps the rolls per step. Every step materialises at least one roll. Read when the run opens; clamped to [1, 250]. 250 lets the roll cap alone decide, which is the pre-budget behaviour")),
                ("dynamic_dungeons_loot_trickle_budget_ms", new Property<long>(ACE.Server.ThreadDungeons.ThreadLootTrickle.DefaultBudgetMs, "(non-retail function) Threads: wall-clock milliseconds one loot-trickle step may spend building pooled-loot ledger rolls WHILE a run is Active, on the run landblock's action queue, so most rolls are already built when the run clears and the clear only deals and places. The trickle runs only while a roster member is inside and the copy is not dormant, and only up to dynamic_dungeons_loot_trickle_max_prebuilt objects. Read when the run opens; clamped to [0, 50]. 0 turns the trickle off (every roll is built at the clear, the pre-trickle behaviour)")),
                ("dynamic_dungeons_loot_trickle_max_prebuilt", new Property<long>(ACE.Server.ThreadDungeons.ThreadLootTrickle.DefaultMaxPrebuilt, "(non-retail function) Threads: the most out-of-world loot objects the loot trickle may hold built ahead of a run's clear; past it the trickle pauses and the rest is built at the clear. Bounds the memory a run holds (roughly 8 KB per object measured, so the default 1500 is about 12 MB per run). Read when the run opens; clamped to [0, 20000]. 0 lets nothing be built ahead")),
                ("dynamic_dungeons_puzzle_gates_per_run", new Property<long>(1, "(non-retail function) Threads: how many gate puzzles a run places, at most, from its dungeon's curated gate sites (the reward scene is separate and not counted). Each uses a different puzzle type. Read when the run opens; clamped to [0, 8]. 0 places no gate puzzle")),
                ("dynamic_dungeons_puzzle_fail_threshold", new Property<long>(3, "(non-retail function) Threads: scored wrong lever pulls from one connection key, within dynamic_dungeons_puzzle_fail_window_minutes, that trigger a puzzle lockout (see dynamic_dungeons_puzzle_fail_policy_enabled). Clamped to at least 1. Applies within 15 seconds")),
                ("dynamic_dungeons_puzzle_fail_window_minutes", new Property<long>(60, "(non-retail function) Threads: the rolling window, in minutes, over which puzzle fails count toward dynamic_dungeons_puzzle_fail_threshold. Clamped to [1, 1440] (the fail ledger keeps 24 hours). Applies within 15 seconds")),
                ("dynamic_dungeons_puzzle_lockout_minutes", new Property<long>(120, "(non-retail function) Threads: how long, in minutes, a connection key that reached the puzzle fail threshold is barred from the Threads. Clamped to at least 1. A lockout already running keeps its end time when this changes. Applies within 15 seconds")),
                ("dynamic_dungeons_boss_chest_loot_count", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultBossCacheLootCount, "(non-retail function) Threads: loot rolls a boss cache takes before the run's own loot-quantity multiplier is applied; the cache holds ceil(this * that). Rolled from the RUN's stamped loot profile, so the tier and quality are the same ones its creatures dropped at. Capped at 100 items after scaling; 0 leaves a cache holding only its Trade Notes")),
                ("dynamic_dungeons_boss_chest_mmd_low_level", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdLowLevel, "(non-retail function) Threads: the LOW anchor gem level of the boss cache's Trade Note line. Below it the stack top is pinned at dynamic_dungeons_boss_chest_mmd_low_max rather than falling further")),
                ("dynamic_dungeons_boss_chest_mmd_low_max", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdLowMax, "(non-retail function) Threads: the boss cache's Trade Note stack TOP at the low anchor level, and the floor of that top at every level below it. The stack itself is a uniform 1..top")),
                ("dynamic_dungeons_boss_chest_mmd_high_level", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdHighLevel, "(non-retail function) Threads: the HIGH anchor gem level of the boss cache's Trade Note line. Set to 300 while the gem ceiling was 275; the line is NOT capped there, so the 2026-09-09 raise to 375 keeps the note stack scaling past the anchor instead of flattening")),
                ("dynamic_dungeons_boss_chest_mmd_high_max", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdHighMax, "(non-retail function) Threads: the boss cache's Trade Note stack TOP at the high anchor level. Scaled by gem LEVEL alone - never by the loot-quantity multiplier or the gem-level reward-scale ratio, both of which already move the cache's loot half")),
                ("dynamic_dungeons_survey_min_level", new Property<long>(0, "(non-retail function) Threads: minimum gem (monster) level for a cleared run to count as a filed survey. Default 0 files a survey for every cleared run that spawned something, including gems a Lead or Copper Scarab pressed below the level-185 fragment rung (owner ruling 2026-09-11, replacing ruling B3's floor of 185)")),
                ("dynamic_dungeons_survey_reference_level", new Property<long>(ACE.Server.ThreadDungeons.DungeonGemSpec.MaxGemLevel, "(non-retail function) Threads: the level the daily-survey reward curve measures a player's run levels against - the denominator of (avgGemLevel / this)^dynamic_dungeons_survey_ratio_exponent, which scales the award's LUMINANCE only. The XP is a share of a level-up measured at min(player level, avg gem level) and does not use this level. Defaults to DungeonGemSpec.MaxGemLevel (375, the GEM ceiling - not the 500 run ceiling, which the survey never sees: a run pressed above 375 files at 375), so raising the gem ceiling rescales the luminance with it (owner ruling R6); pinning it BELOW the ceiling keeps the pre-raise luminance curve instead. Clamped at read into the character XP chart's own index range")),
                ("dynamic_dungeons_health_curve_top_level", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveTopLevel, "(non-retail function) Threads: the level at and above which the band-standard health curve (dynamic_dungeons_health_curve) stops extrapolating and clamps. Default 500, the run ceiling (DungeonGemSpec.MaxRunLevel): T(500) is about 76845. 375 restores the pre-2026-10-08 clamp at dynamic_dungeons_health_curve_anchor_high exactly. Values outside [375, 500] read as the nearest edge")),
                ("dynamic_dungeons_survey_reset_hour", new Property<long>(0, "(non-retail function) Threads: the hour (0-23) in dynamic_dungeons_survey_reset_timezone at which the survey day turns over. The survey window, count and all three payout tiers turn over together at this hour. Clamped to [0, 23] at read")),
                ("dynamic_dungeons_min_player_level", new Property<long>(150, "(non-retail function) Threads: minimum player level to use any dungeon gem")),
                ("dynamic_dungeons_group_max_monsters_per_run", new Property<long>(180, "(non-retail function) Threads: count cap for runs with 2+ roster members; never lowers a group run below its solo count")),
                ("dynamic_dungeons_group_damage_rating_per_member", new Property<long>(15, "(non-retail function) Threads: damage rating added to every run creature per roster member past the first")),
                ("dynamic_dungeons_group_damage_rating_cap", new Property<long>(300, "(non-retail function) Threads: cap on that added rating; 0 = uncapped")),
                ("dynamic_dungeons_group_loot_hold_minutes", new Property<long>(30, "(non-retail function) Threads: a cleared fellowship Thread still owing loot is held open only while a roster member was inside within this many minutes; 0 = no hold. Values 1-4 read as 5, the empty-grace floor.")),
                ("dynamic_dungeons_press_max_mods", new Property<long>(4, "(non-retail function) Threads: most MONSTER/BOSS modifiers a pressed gem may carry - the four taper slots' shared budget. The bonus (herb) and salvage affinity (powder) budgets are 1 each and are compiled constants rather than tunables, because each is the arithmetic consequence of there being one such slot on the press board (RawFragmentRules.SlotCounts)")),
                ("dynamic_dungeons_press_fee_notes", new Property<long>(1, "(non-retail function) Threads: Trade Notes charged per pressing; re-opening is free")),
                ("dynamic_dungeons_placement_fallback_attempts", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultPlacementFallbackAttempts, "(non-retail function) Threads: when the world refuses a run creature at its curated spawn point (EnterWorld false - the body does not fit there), retry it at up to this many other curated points of the same dungeon: the boss nearest its anchor first, trash and elites at unused points first, then nearest. Each retry is a freshly created creature. 0 disables the fallback; a negative value reads as the default; values above 50 read as 50")),
                ("dynamic_dungeons_boss_damage_rating_floor", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossDamageRatingFloor, "(non-retail function) Threads: how far a run boss's DamageRating must exceed the pack's; a FLOOR, so a gem's own boss modifiers win when they are larger. 0 disables the axis; a negative value reads as the default")),
                ("dynamic_dungeons_boss_damage_resist_floor", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossDamageResistFloor, "(non-retail function) Threads: how far a run boss's DamageResistRating must exceed the pack's; a FLOOR, so a gem's own boss modifiers win when they are larger. 0 disables the axis; a negative value reads as the default")),
                ("dynamic_dungeons_min_eligible_families", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultMinEligibleFamilies, "(non-retail function) Threads: how many species tables must be able to field an in-band trash member before the roster band stops widening its LOW edge (down to dynamic_dungeons_band_low_floor). Families rather than members, because family count is what a player experiences as variety across runs. 0 disables family widening; a negative value reads as the default")),
                ("dynamic_dungeons_min_family_pool", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultMinFamilyPool, "(non-retail function) Threads: how many DISTINCT trash wcids the CHOSEN family's pool must hold before its own band stops widening its LOW edge. Family eligibility only needs one in-band member, so without this a run can pass the family threshold and still stand copies of a single wcid on every spawn point. 0 disables per-family widening; a negative value reads as the default")),
                ("world_events_min_slot_pool", new Property<long>(ACE.Server.WorldEvents.WorldEventRosterSelector.DefaultMinSlotPool, "(non-retail function) World Events: how many DISTINCT trash wcids a wave slot's family pool should hold before the trash band stops widening its LOW edge (world_events_band_uplift, world_events_band_low_floor). 0 or less disables the widening; values above 1000 read as 1000")),
                ("world_events_announce_chat_type", new Property<long>(0x05, "(non-retail function) ChatMessageType used for World Events global announcements; 5=magenta/System, 18=orange, 13=teal")),
                ("world_events_boss_damage_rating_max", new Property<long>(300, "(non-retail function) World Events: upper bound on the DamageRating the boss damage controller may set. A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_damage_rating_min", new Property<long>(-80, "(non-retail function) World Events: lower bound on the DamageRating the boss damage controller may set. Negative is meaningful and is the direction taken when a boss is hitting too hard - rating -80 is a 0.56x damage multiplier (Creature.GetNegativeRatingMod, 100/(100+80)). A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_damage_sample_hits", new Property<long>(4, "(non-retail function) World Events: how many NON-CRIT hits the boss must land on counted participants before the damage controller will propose an adjustment. Crits are excluded from the mean because the requirement is about an ordinary hit; they are counted separately in the log. A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_damage_step_cap", new Property<long>(50, "(non-retail function) World Events: the largest DamageRating CHANGE one boss damage adjustment may make. The FIRST adjustment of a run is exempt, so a badly-seeded boss converges in one step; every later one is capped so a single unlucky sample window cannot swing the fight. A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_throughput_cap_health", new Property<long>(0, "(non-retail function) World Events: overrides bosses.json.throughputCapHealth for every boss when positive; 0 or negative = use the JSON value. The absolute health ceiling on the C16 throughput sizing path")),
                ("world_events_boss_throughput_floor_health", new Property<long>(0, "(non-retail function) World Events: overrides bosses.json.throughputFloorHealth for every boss when positive; 0 or negative = use the JSON value. The health a boss is re-based to before the throughput multiplier, and the per-boss switch that turns the throughput sizing path on at all - setting this here FORCE-ENABLES throughput sizing (subject to world_events_boss_throughput_scaling_enabled) even for a boss authored with throughputFloorHealth 0, i.e. one whose own bosses.json entry never opted in. That is intended, not a bug: every shipped boss already carries 100000 there, so in practice this override only matters for a boss a content author deliberately left at 0")),
                ("world_events_cache_effect_repeat_seconds", new Property<long>(30, "(non-retail function) World Events: seconds between replays of the reward cache particle effect while the claim window is open; 0 or negative = play once only, on appearance")),
                ("world_events_cache_effect_script", new Property<long>(141, "(non-retail function) World Events: PlayScript type played on each reward cache, resolved through the player PhysicsScriptTable 0x34000004 and sent as a raw script DataID; default 141 (0x8D WeddingBliss, the skill-max sparkle); 0 = disabled; an undefined value falls back to 141 with one warning, and a type the player table lacks logs a warning and plays nothing")),
                ("world_events_cache_wcid", new Property<long>(0, "(non-retail function) overrides the World Events reward cache weenie; 0 = use the reward profile's cacheWcid")),
                ("world_events_claim_window_seconds", new Property<long>(300, "(non-retail function) World Events reward claim window in seconds; rewards.json claimWindowSeconds wins when present")),
                ("world_events_crowd_health_start_at", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.crowdHealth.startAt for every source when positive; 0 or negative = use the JSON value. Participant count at which the crowd health multiplier starts to move")),
                ("world_events_family_floor_level", new Property<long>(50, "(non-retail function) World Events: a randomly composed run's FIRST family must have a catalog member at or below this level, so the pair always reaches down to a low-level crowd; an explicit --family bypasses it. A non-positive value falls back to the built-in default")),
                ("world_events_max_alive", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.maxAlive for every source when positive; 0 or negative = use the JSON value. Wave-pressure ceiling a source's live trash/elites are budgeted against")),
                ("world_events_overflow_per_champion", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.overflowPerChampion for every source when positive; 0 or negative = use the JSON value. How much unplaceable wave demand buys one overflow champion")),
                ("world_events_pace_quantity_step", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.pace.quantityStep for every source when positive; 0 or negative = use the JSON value. How many extra creatures one pace-controller speed-up step adds to the next wave pick")),
                ("world_events_progress_interval_seconds", new Property<long>(120, "(non-retail function) World Events: seconds between global progress announcements while a run is Active; 0 disables them")),
                ("world_events_teaser_lead_seconds", new Property<long>(60, "(non-retail function) seconds of teaser warning broadcast before a World Event stages; 0 disables the teaser. --teaser on /worldevent start overrides it for one run")),
                ("world_events_wave_count_base", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.waveCount.base for every source when positive; 0 or negative = use the JSON value. The floor of the per-wave trash count formula")),
                ("world_events_wave_count_cap", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.waveCount.cap for every source when positive; 0 or negative = use the JSON value. The ceiling of the per-wave trash count formula")),
                // Monster combat effects: the COUNT caps of the monster_effect_* block. The fractional and
                // multiplier caps of the same block are doubles and live in DefaultDoubleProperties below.
                ("monster_effect_ramp_stack_cap", new Property<long>(20, "monster effects: maximum stacks any one ramp record may hold, whatever its own max= arg says")),
                ("monster_effect_max_per_monster", new Property<long>(12, "monster effects: maximum effects one monster may carry. An authored list longer than this is truncated at creature construction with a warning naming the wcid")),
                ("monster_effect_recast_cap", new Property<long>(2, "monster effects: maximum extra casts one completed cast may chain into, bounding a recast record that would otherwise re-enter itself")),
                ("ml_treasure_payout_min", new Property<long>(0, "(non-retail function) ML Treasure Hunt: minimum treasure currency paid by an ordinary map's final dig step (ThreadSafeRandom.Next(min, max), inclusive). Round 17 owner ruling: 0 - a map's doubloons now come only from the digsite chest (ml_digsite_chest_doubloons). A roll of 0 pays nothing and sends no coin line; the map is still consumed and the digsite encounter still opens")),
                ("ml_treasure_payout_max", new Property<long>(0, "(non-retail function) ML Treasure Hunt: maximum treasure currency paid by an ordinary map's final dig step (ThreadSafeRandom.Next(min, max), inclusive). Round 17 owner ruling: 0 (see ml_treasure_payout_min)")),
                ("ml_treasure_dig_steps", new Property<long>(3, "(non-retail function) ML Treasure Hunt: Use presses required on-site (PropertyInt.TreasureMapDigProgress) to complete a dig and reach the payout/boss step")),
                ("ml_treasure_boss_ttl_seconds", new Property<long>(1800, "(non-retail function) ML Treasure Hunt: seconds an Aun Relaria dug up by a Relaria-variant map survives before the landblock decay pass reaps it (stamped as TimeToRot at spawn). The reap is a bare Destroy with no Die, so no corpse, no XP and no trophy - it exists so an abandoned boss does not linger, and the default sits far above any plausible fight length because the decay pass does not know the boss is being fought. 0 or less falls back to MlRelariaSpawner.DefaultTtlSeconds rather than producing an immortal boss, because 0 and -1 mean instant-rot and never-rot to WorldObject_Decay")),
                ("ml_treasure_reroll_max_metres", new Property<long>(1000, "(non-retail function) ML Treasure Hunt: /rrtm refuses a reroll when the player is farther than this from the map's currently stored site, in metres")),
                ("ml_relaria_repeat_doubloons", new Property<long>(MlRelariaTrophy.DefaultRepeatDoubloons, "(non-retail function) ML Treasure Hunt: ML Doubloons (TreasureMapHandler.TreasureCurrencyWcid) paid on every REPEAT Aun Relaria kill (a killer who has already claimed the one-time CAP trophy) - unconditional, on top of the XP/luminance the ordinary death path already pays. Paid again as a bonus when the ml_relaria_repeat_aura_chance roll succeeds but the killer already has the aura (MlRelariaTrophy.TryAwardRepeatKillRewards)")),
                ("ml_relaria_aura_pulse_seconds", new Property<long>(10, "(non-retail function) ML Treasure Hunt: seconds between re-broadcasts of the Relaria repeat-kill aura's visual effect on a character who has earned it (Player_RelariaAura). Re-armed at every login; a value below 1 is treated as 1")),
                ("ml_relaria_aura_script", new Property<long>((long)PlayScript.LayingofHands, "(non-retail function) ML Treasure Hunt: the PlayScript the Relaria repeat-kill aura pulses (Player_RelariaAura.ResolveRelariaAuraScript). A value that does not name a defined PlayScript member falls back to the shipped default (LayingofHands). Set live with the admin probe /relariaprobe before changing this")),
                // ML digsite encounters. The three weights are NORMALISED rather than required to total
                // anything, so one can be retuned without rebalancing the other two.
                ("ml_digsite_weight_waves", new Property<long>(40, "(non-retail function) ML digsite encounters: relative weight of the Waves shape (8 fixed waves, ending in a WIN at wave 8; otherwise bail, wipe, a wave clock or the TTL, with checkpoint mini-bosses). Normalised against the other two weights, so these need not total 100. Ratified by the tester at 40/30/30 (round 16 feedback), down from the round-13 70/25/5")),
                ("ml_digsite_weight_corruption", new Property<long>(30, "(non-retail function) ML digsite encounters: relative weight of the Corruption shape. Normalised against the other two weights. Ratified by the tester at 40/30/30 (round 16 feedback), up from the round-13 70/25/5")),
                ("ml_digsite_weight_bossrush", new Property<long>(30, "(non-retail function) ML digsite encounters: relative weight of the Boss Rush shape, a single hard target with no waves. Normalised against the other two weights. Ratified by the tester at 40/30/30 (round 16 feedback), up from the round-13 70/25/5")),
                ("ml_digsite_max_concurrent", new Property<long>(20, "(non-retail function) ML digsite encounters: server-wide cap on live encounters. A completed dig past the cap is refused, which costs the player nothing - the doubloon payout and the map consumption both already happened. 0 or less means no encounters at all, never unlimited")),
                ("wave_encounter_ttl_seconds", new Property<long>(2700, "(non-retail function) Wave encounters (object-anchored, e.g. the D6 Sounding Drum): seconds an encounter may run before the reap ends it as a failure - remaining creatures are destroyed without dying (no loot, XP or credit) and the anchor's cooldown starts. 0 disables the TTL")),
                ("wave_encounter_wipe_grace_seconds", new Property<long>(20, "(non-retail function) Wave encounters: seconds with no living player within wave_encounter_presence_radius of the anchor (same landblock instance) before the reap ends the encounter as a failure (a wipe or a walk-away). Checked on the 15 s reap, so the real window is this plus up to 15 s. 0 disables the test")),
                ("wave_encounter_cooldown_seconds", new Property<long>(300, "(non-retail function) Wave encounters: seconds after an encounter ENDS (win, wipe, walk-away, TTL or unload) before the same anchor can be started again. Per anchor, held in memory, so a restart clears it. 0 disables the cooldown")),
                ("ml_digsite_ttl_seconds", new Property<long>(600, "(non-retail function) ML digsite encounters: seconds an encounter may live before the reap ends it - a hard 10-minute cap on EVERY shape (round 16 owner ruling), down from 1800 (30 min). Each shape then pays its own progress fraction times ml_digsite_fail_payout_multiplier. 0 disables the TTL and leaves only the landblock and wipe tests")),
                ("ml_digsite_wipe_grace_seconds", new Property<long>(30, "(non-retail function) ML digsite encounters: seconds with nobody ALIVE inside ml_digsite_audience_radius_metres before the reap ends the encounter as wiped. A wipe pays what the encounter reached, so this only has to cover a corpse run back. 0 disables the test")),
                ("ml_digsite_presence_window_seconds", new Property<long>(90, "(non-retail function) ML digsite encounters: a helper (anyone but the digger) is paid only if they dealt damage, are online, and were seen alive at the site within this many seconds of the end. Stamped by the 15 s reap, deliberately NOT present-at-the-end, so a wipe still pays the group that fought. 0 turns the presence test off")),
                ("ml_digsite_inter_wave_seconds", new Property<long>(1, "(non-retail function) ML digsite encounters: breather between a cleared wave and the next. Round 16 owner ruling: 1s, down from 10s, now that the Waves shape has a fixed 8-wave finish line rather than being endless")),
                ("ml_digsite_checkpoint_every", new Property<long>(3, "(non-retail function) ML digsite encounters: every Nth wave of the 8-wave Waves shape places a checkpoint mini-boss alongside it (skipped while the previous one still lives). It never holds a wave open and its death is not a win; each one killed adds ml_digsite_checkpoint_bonus to the tier. 0 or less turns checkpoints off")),
                ("ml_digsite_forced_miniboss_seconds", new Property<long>(180, "(non-retail function) ML digsite encounters: after this many seconds of a Waves run, ONE checkpoint mini-boss is forced whatever wave the run is on, instead of waiting for the ml_digsite_checkpoint_every cadence. Deferred, not skipped, while a checkpoint mini-boss is already standing. It is an ordinary checkpoint - it never holds a wave open and its death is not a win - so it escalates the run rather than ending it. 0 turns the forced spawn off")),
                ("ml_digsite_wave_count_per_wave", new Property<long>(1, "(non-retail function) ML digsite encounters: extra creatures per wave after the first, on top of the player-count curve, capped at 40 per wave")),
                ("ml_digsite_wave_dr_per_wave", new Property<long>(5, "(non-retail function) ML digsite encounters: DamageRating added to wave and checkpoint creatures per wave after the first, on top of crowd scaling, up to ml_digsite_wave_dr_cap")),
                ("ml_digsite_wave_dr_cap", new Property<long>(60, "(non-retail function) ML digsite encounters: ceiling on the per-wave DamageRating addend")),
                ("ml_digsite_wave_time_limit_seconds", new Property<long>(300, "(non-retail function) ML digsite encounters: absolute deadline for one live wave of the endless Waves shape, matching the shipped wave portal (weenie 1001550, PropertyFloat 9006). On expiry the encounter ENDS (reason wave-time-limit) and pays the tier it reached. Not checked during the breather between waves. 0 disables this clock")),
                ("ml_digsite_stall_timeout_seconds", new Property<long>(150, "(non-retail function) ML digsite encounters: seconds with no encounter kill before a live wave is treated as stalled, matching the shipped wave portal (weenie 1001550, PropertyFloat 9005). On expiry the encounter ENDS (reason stalled) and pays the tier it reached. Runs alongside the wave time limit: this one catches a group that stopped fighting, that one a wave being fought but not finishable. 0 disables this clock")),
                ("ml_digsite_wave_count_base", new Property<long>(3, "(non-retail function) ML digsite encounters: floor of the per-wave creature count, on the shipped player-count curve max(base, min(cap, base + ceil(perParticipant * players)))")),
                ("ml_digsite_wave_count_cap", new Property<long>(12, "(non-retail function) ML digsite encounters: ceiling of the per-wave creature count on the player-count curve")),
                ("ml_digsite_corruption_kills_required", new Property<long>(3, "(non-retail function) ML digsite encounters: round 16 redesign - the meter is retired. Corrupted mobs the players must kill, one at a time (the next spawns only once the previous dies), to win a Corruption encounter outright")),
                ("ml_digsite_corruption_field_spawn_seconds", new Property<long>(20, "(non-retail function) ML digsite encounters: how often more field mobs spill into a live Corruption encounter")),
                ("ml_digsite_corruption_power_tick_seconds", new Property<long>(5, "(non-retail function) ML digsite encounters: while a Corruption encounter's Corrupted mob is alive, every this many seconds the rest of the field gains ml_digsite_corruption_power_per_tick more damage (via DamageRating) and the gain is announced to the participants. Resets to nothing when that Corrupted mob dies, so each Corrupted mob starts fresh. 0 turns the power gain off")),
                ("ml_digsite_corruption_field_spawn_count", new Property<long>(5, "(non-retail function) ML digsite encounters: how many more field mobs spawn per ml_digsite_corruption_field_spawn_seconds interval, capped by whatever room is left under the 40-live-body landblock safety ceiling")),
                ("ml_digsite_chest_loot_count", new Property<long>(15, "(non-retail function) ML digsite encounters: loot rolls in a reward chest at a payout fraction of 1, scaled by the encounter payout fraction (Waves tier, Boss Rush health removed, Corruption win/loss) and, for a helper's chest, by the group share. For scale, the Thread boss cache rolls 10. Raising this is the single knob for digsite loot volume")),
                ("ml_digsite_chest_doubloons", new Property<long>(2, "(non-retail function) ML digsite encounters: Marae Lassel Doubloons (wcid 1004101) in a reward chest at a payout fraction of 1, scaled like the loot count but floored at 1 whenever the fraction is positive (MlDigsiteRules.ScaleCurrency). Round 17 owner ruling: 2 per map, down from 5, now that the dig step itself pays none")),
                ("ml_digsite_chest_trade_notes", new Property<long>(5, "(non-retail function) ML digsite encounters: Trade Notes (the genuine retail wcid 20630, never a clone) in a reward chest at a payout fraction of 1, scaled like the loot count")),
                ("ml_digsite_chest_treasure_death_id", new Property<long>(2001, "(non-retail function) ML digsite encounters: the treasure_death profile the reward chest rolls its loot from, as a treasure_death.treasure_Type (the column GetCachedDeathTreasure and DeathTreasureType key on), NOT the row id. 2001 is the retail Legendary Chest's own profile (row id 241, tier 8) - Weapon/Armor/Clothing/Jewelry/Cloak/PetDevice only - so a digsite cache never pays the Scroll/Gem/ArtObject types an ordinary corpse can roll. A value no row carries as its treasure_Type leaves the chest holding currency only")),
                ("ml_digsite_chest_ttl_seconds", new Property<long>(600, "(non-retail function) ML digsite encounters: seconds the reward chest stands before the landblock decay pass reaps it. MUST stay finite. A digsite sits on a live, shared, PERSISTENT outdoor landblock, so the -1 the Thread boss cache uses would leave an immortal chest on the island; the Thread cache can only use -1 because its whole ephemeral copy is destroyed around it")),
                ("ml_digsite_full_xp", new Property<long>(425000000, "(non-retail function) ML digsite encounters: XP for one completion, paid to EVERY eligible player (the owner, and each helper who dealt damage and was seen within ml_digsite_presence_window_seconds) - the group share scales chests only. A full clear pays exactly this, flat (round 15 owner ruling); any other outcome pays it scaled by the encounter payout fraction. Paid through Player.EarnXP as XpType.Quest, so BOTH xp_modifier and quest_xp_modifier multiply it")),
                ("ml_digsite_full_luminance", new Property<long>(80000, "(non-retail function) ML digsite encounters: luminance for one completion, paid like ml_digsite_full_xp to every eligible player - flat on a full clear (round 15 owner ruling), scaled by the payout fraction otherwise. Paid through Player.GrantLuminance, which applies NO luminance modifiers, so this number is paid exactly as configured - unlike the XP")),
                ("ml_digsite_crowd_dr_per_player", new Property<long>(10, "(non-retail function) ML digsite encounters: DamageRating added per participant above zero to every digsite-spawned creature, resolved once at that creature's own spawn moment from the digsite audience scan - the same shape as bluespire_ladder_d6_crowd_dr_per_player, but its OWN key so the two never share a dial. 0 or a non-positive ml_digsite_crowd_dr_cap turns this axis off")),
                ("ml_digsite_crowd_dr_cap", new Property<long>(100, "(non-retail function) ML digsite encounters: ceiling on the DamageRating ml_digsite_crowd_dr_per_player may add to one creature, however large the crowd")),
                ("ml_digsite_status_interval_seconds", new Property<long>(30, "(non-retail function) ML digsite encounters: seconds between the periodic compact status line sent to every participant (wave/meter/boss progress plus time left), independent of the wave-start/meter-band/boss-HP state-change lines. Same line the /digsite command prints on request")),
                ("ml_digsite_bossrush_set_force", new Property<long>(0, "(non-retail function) ML digsite encounters: pins which Boss Rush mechanic set every new encounter runs, by the set= id authored in ml_digsite_bossrush_sets. 0 is the shipped value and means ROLL, which is the live behaviour the tester asked for; 1-5 pin one set so a tester driving a client can reach a specific one without seeding an rng. An id the table does not carry falls back to the roll rather than refusing")),
                ("ml_digsite_bossrush_hazard_marker_wcid", new Property<long>(1002665, "(non-retail function) ML digsite encounters: the world object a Boss Rush mechanic places to telegraph ground that is ABOUT TO BE HIT (a drum shape, a volatile add's corpse). Default 1002665 'Pillar of Fire', an already-shipped Generic/UiHidden particle column with PhysicsState Static|Ethereal|IgnoreCollisions|LightingOn - visible, no collision, no cursor. 0 places no markers and leaves the chat telegraph, which every mechanic sends as well")),
                ("ml_digsite_bossrush_safe_marker_wcid", new Property<long>(1002667, "(non-retail function) ML digsite encounters: the world object the shifting safe zones mechanic places to mark ground that is SAFE. Default 1002667 'Pillar of Frost', the same shipped scenery recipe as ml_digsite_bossrush_hazard_marker_wcid in a different colour, so safe and lethal ground never read as the same thing. 0 places no markers and leaves the chat telegraph")),
                ("ml_digsite_bossrush_interrupt_wcid", new Property<long>(1005950, "(non-retail function) ML digsite encounters: the usable object the interrupt mechanic places for a player to run to and use. Default 1005950 'Aun Signal Drum' (Content/sql/weenies), a Generic object with ItemUseable Remote whose use is handled by MlDigsiteInterruptObject. 0 turns the interrupt mechanic off entirely rather than leaving an unavoidable hit, since the hit only exists to be cancelled")),
                ("ml_digsite_bossrush_drum_motion", new Property<long>(268435554, "(non-retail function) ML digsite encounters: the MotionCommand the Boss Rush boss plays on each drum beat (DrumCadenceMechanic), as its raw uint value. Default 268435554 = MotionCommand.AttackHigh1, chosen by probing every Boss Rush boss's own MotionTable id (client_portal.dat, via ACE.Content.Tools motionlength) in MotionStance.HandCombat - the stance every boss and Kept Siraluun substitute actually fights in - and confirming a non-zero animation length in all six distinct MotionTable ids the roster carries. Skipped at runtime (no broadcast) whenever the boss's own MotionTable has no non-zero animation for the configured value in its current stance, so a bad id never breaks anything. 0 disables the broadcast entirely")),
                ("ml_digsite_bossrush_drum_ring_spell", new Property<long>(3993, "(non-retail function) ML digsite encounters: the retail spell id the drum cadence's Ring shape casts at resolve (RoZ playtest feedback: the ring and wall damage read as invisible, nothing was visibly cast). Default 3993 'Heavy Blade Ring' (wcid 33727 'whirlingblade', 9 projectiles, spread_Angle 360, Slash damage) - verified against the local ace_world spell table, already carried in weenie_properties_spell_book by two apex Boss Rush-tier monsters (ace72538-exarchnanjoushoujen, ace72216-shadeoflordrytheran), so this is a spell the roster already casts in retail, not a repurposed one. Must classify as SpellProjectile.GetProjectileSpellType == Ring at runtime; 0 or any id that does not falls back to the original geometric RadialHit damage for the Ring shape and logs a warning - never throws")),
                ("ml_digsite_bossrush_drum_wall_spell", new Property<long>(1844, "(non-retail function) ML digsite encounters: the retail spell id the drum cadence's Wall shape casts at resolve, the Wall counterpart to ml_digsite_bossrush_drum_ring_spell. Default 1844 \"Os' Wall\" (wcid 7280 'lightningwall', 10 projectiles, spread_Angle 0, Electric damage) - verified against the local ace_world spell table, carried in weenie_properties_spell_book by five monsters including portalbossinfiltration and ace45698-galvanicguard. Must classify as SpellProjectile.GetProjectileSpellType == Wall at runtime; 0 or any id that does not falls back to the original geometric InsideWall/Hit damage for the Wall shape and logs a warning - never throws")),
                ("ml_digsite_bossrush_drum_max_casters", new Property<long>(8, "(non-retail function) ML digsite encounters: the most creatures that cast the drum cadence's Ring/Wall spell in one resolve - the boss counts as one of the total and the encounter's live Boss Rush adds (MlDigsiteEncounter.LiveAddSnapshot, never the wave list) fill the rest, in tracking order. This is what caps the projectile count when several adds are alive alongside the boss. Clamped to [1, 50]")),
                ("ml_digsite_priority_script", new Property<long>(0x56, "(non-retail function) ML digsite encounters: PropertyDataId.PhysicsScript (30) stamped on the Corruption shape's Corrupted mob so it reads as visibly distinct from the field around it, and re-broadcast on the same cadence the field grows on (ml_digsite_corruption_field_spawn_seconds) so a player who walks out of range and back still sees it tagged. Default 0x56 is PlayScript.BreatheAcid, already this fork's looping-aura tag for 'corrupted/toxic' on Mirebound/Drift-Touched creatures")),
                ("ml_digsite_radar_color", new Property<long>(3, "(non-retail function) ML Digsite: PropertyInt.RadarBlipColor (RadarColor enum) stamped on every digsite-spawned creature (round 17 tester feedback: digsite creatures share wcids and names with the island's own wildlife and could not be told apart on radar). Default 3 = RadarColor.White, against the RadarColor.Creature default of Gold (0x02) every ordinary monster carries unstamped. 0 stamps nothing and leaves the creature's own default")),
                ("ml_mapevent_trash_reference_max_health", new Property<long>(500, "(non-retail function) RoZ round 19 level-spread scaling: the max health at and above which a map-event trash creature (digsite Wave/Add) keeps its authored damage. Below it the creature DamageRating is lowered by keyedPlayerMaxHealth / this, floored at ml_mapevent_trash_damage_floor")),
                ("ml_mapevent_boss_base_health", new Property<long>(0, "(non-retail function) RoZ round 19 level-spread scaling: the base health the Boss Rush boss and Aun Relaria are rebased to before the power-sum multiplier. 0 = keep the authored health as the floor (owner ruling 2026-09-25); the power-sum curve only ever raises above it")),
                // Bluespire ladder. Every one of these is read LIVE on each portal use, which is the whole
                // point: raising a requirement re-locks players who unlocked at the old one, because nothing
                // about the unlock is ever baked into a stamp. The rung -> quest-name mapping is the one
                // thing that is NOT a tunable - it is a compiled constant table in BluespireLadder.
                //
                // There is no per-rung min-level tunable: owner ruling 2026-09-24 (round 19 A2) removed the
                // level gate from every depth entirely (BluespireLadderGate no longer has a BelowLevel
                // decision, and BluespireLadder.MinLevel no longer exists) rather than leaving it in place
                // zeroed out, so a future default retune cannot silently reintroduce it.
                ("beta_gift_wcid", new Property<long>(1006600, "Dreamweave beta-tester gift: wcid of the item auto-granted to every character created on an eligible account (see beta_gift_account_cutoff_unix). 0 disables the gift entirely - CharacterHandler.GrantBetaGiftIfEligible skips it and character creation is unaffected either way. See ACE.Server.Entity.BetaGift.BetaGiftRules")),
                ("beta_gift_account_cutoff_unix", new Property<long>(ACE.Server.Entity.BetaGift.BetaGiftRules.DefaultAccountCutoffUnix, "Dreamweave beta-tester gift: Unix seconds (UTC). An account whose ace_auth.account.create_Time is before this instant has every new character get one copy of beta_gift_wcid; at or after, none. Default is 2026-10-02T00:00:00Z, the repo owner's beta shut-off (8pm EDT 10/1/2026). See ACE.Server.Entity.BetaGift.BetaGiftRules.IsEligible")),
                ("bluespire_ladder_d1_requires", new Property<long>(0, "(non-retail function) Bluespire ladder: the rung that must ALREADY be cleared before rung 1 may be entered. 0 means no rung prerequisite; rung 1 instead requires the bluespire_ladder_d1_entry_quest stamp. A value outside 1..(own rung - 1) is treated as 0 and logged once, so a typo can neither lock the ladder shut nor create a cycle")),
                ("bluespire_ladder_d2_requires", new Property<long>(1, "(non-retail function) Bluespire ladder: the rung that must ALREADY be cleared before rung 2 may be entered. Evidence is the BluespireLadderD<k>Cleared quest stamp, ever-stamped and cooldown-blind, so re-running a cleared rung is always allowed. A value outside 1..(own rung - 1) is treated as 0 and logged once")),
                ("bluespire_ladder_d3_requires", new Property<long>(2, "(non-retail function) Bluespire ladder: the rung that must ALREADY be cleared before rung 3 may be entered. A value outside 1..(own rung - 1) is treated as 0 and logged once")),
                ("bluespire_ladder_d4_requires", new Property<long>(3, "(non-retail function) Bluespire ladder: the rung that must ALREADY be cleared before rung 4 may be entered. A value outside 1..(own rung - 1) is treated as 0 and logged once")),
                ("bluespire_ladder_d5_requires", new Property<long>(4, "(non-retail function) Bluespire ladder: the rung that must ALREADY be cleared before rung 5 may be entered. A value outside 1..(own rung - 1) is treated as 0 and logged once")),
                ("bluespire_ladder_d6_requires", new Property<long>(5, "(non-retail function) Bluespire ladder: the rung that must ALREADY be cleared before rung 6 may be entered. A value outside 1..(own rung - 1) is treated as 0 and logged once")),
                ("bluespire_ladder_currency_wcid", new Property<long>(1005490, "(non-retail function) Bluespire ladder: the ONE currency weenie paid by every rung's first clear - one currency for the whole ladder, not one per dungeon. 0 turns every payout off without disabling the gate")),
                ("bluespire_ladder_d1_reward", new Property<long>(3, "(non-retail function) Bluespire ladder: currency paid the FIRST time this character ever clears rung 1, and never again (owner ruling 2026-09-24, round 19 A2: cap the ladder at 35 sigils per full D1-D6 clear, weighted deeper, 3/4/5/6/7/10). Re-running the dungeon is allowed and pays nothing. 0 or less means no payout, which is a legitimate off state rather than an error")),
                ("bluespire_ladder_d2_reward", new Property<long>(4, "(non-retail function) Bluespire ladder: currency paid the FIRST time this character ever clears rung 2, and never again (owner ruling 2026-09-24, round 19 A2: 3/4/5/6/7/10 across D1-D6). 0 or less means no payout")),
                ("bluespire_ladder_d3_reward", new Property<long>(5, "(non-retail function) Bluespire ladder: currency paid the FIRST time this character ever clears rung 3, and never again (owner ruling 2026-09-24, round 19 A2: 3/4/5/6/7/10 across D1-D6). 0 or less means no payout")),
                ("bluespire_ladder_d4_reward", new Property<long>(6, "(non-retail function) Bluespire ladder: currency paid the FIRST time this character ever clears rung 4, and never again (owner ruling 2026-09-24, round 19 A2: 3/4/5/6/7/10 across D1-D6). 0 or less means no payout")),
                ("bluespire_ladder_d5_reward", new Property<long>(7, "(non-retail function) Bluespire ladder: currency paid the FIRST time this character ever clears rung 5, and never again (owner ruling 2026-09-24, round 19 A2: 3/4/5/6/7/10 across D1-D6). 0 or less means no payout")),
                ("bluespire_ladder_d6_reward", new Property<long>(10, "(non-retail function) Bluespire ladder: currency paid the FIRST time this character ever clears rung 6, and never again (owner ruling 2026-09-24, round 19 A2: 3/4/5/6/7/10 across D1-D6, total 35 per full clear). 0 or less means no payout")),
                ("bluespire_ladder_d6_crowd_start_at", new Property<long>(1, "(non-retail function) Bluespire ladder rung 6: player count at or below which crowd scaling is a no-op. Above it, every extra body in the dungeon INSTANCE raises health and damage. Feeds CrowdHealthDef.Resolve unchanged, so 1 means a solo run is exactly what the weenie authored")),
                ("bluespire_ladder_d6_crowd_dr_per_player", new Property<long>(10, "(non-retail function) Bluespire ladder rung 6: DamageRating ADDED per player above bluespire_ladder_d6_crowd_start_at, clamped by bluespire_ladder_d6_crowd_dr_cap. DamageRating is an additive percentage rating, so 10 is roughly +10 percent damage per extra body. 0 turns the damage axis off and leaves the health axis alone")),
                ("bluespire_ladder_d6_crowd_dr_cap", new Property<long>(100, "(non-retail function) Bluespire ladder rung 6: ceiling on the total DamageRating addend crowd scaling may apply, however large the crowd gets. 0 turns the damage axis off; a negative value is treated as 0")),
                ("world_tick_slow_log_threshold_ms", new Property<long>(100, "(non-retail function) Monitoring: absolute floor, in ms, below which a world-loop iteration is never logged as SLOW_TICK, whatever the median says. The idle sleep is not counted. A negative value is treated as 0. Re-read every 5 seconds")),
                ("world_tick_slow_log_min_interval_seconds", new Property<long>(10, "(non-retail function) Monitoring: least time between two SLOW_TICK lines. Slow iterations inside the window are not logged; the next line reports how many were suppressed and the slowest of them. 0 logs every slow iteration; a negative value is treated as 0")),
                ("weenie_bulk_selfcheck_sample", new Property<long>(ACE.Database.WeenieBulkLoadSettings.DefaultSelfCheckSample, "(non-retail function) World database: how many weenies of each bulk-loaded chunk (250 wcids) are compared member by member against a legacy per-id read BEFORE the chunk is published. Any difference logs a WARN naming the wcid and the first differing member, and the whole chunk is discarded (its weenies load one at a time instead). 0 or less disables the check; 250 or more checks every weenie. Each checked weenie costs one legacy per-id read"))
                );

        public static readonly ReadOnlyDictionary<string, Property<double>> DefaultDoubleProperties =
            WithPvpContextTuning(DictOf(

                ("account_vault_summon_leash", new Property<double>(30.0, "WaffleACE Mule Vendor: metres from the summoner beyond which a summoned mule vendor despawns, checked on a slow tick. Mirrors the passive pet leash")),
                ("anti_blink_door_width", new Property<double>(3.0, "(non-retail function) MINIMUM width in units of the blocking plane anti-blink builds across a closed door, centered on the door's origin and perpendicular to its facing. Each door is measured by its own collision shape and uses max(this value, that width), so wide gates no longer need this raised; lower it only if legitimate movement beside a narrow door is being rejected")),
                ("anti_blink_z_height_limit", new Property<double>(2.0, "(non-retail function) Anti-blink ignores closed doors whose Z differs from the player's by more than this, so a door on the storey above or below is not tested against the path. 2.0 replaces the ported 3.5 after a stage false positive on 2026-09-01: a player on a Bluespire upper floor 2.8 above a closed ground-floor door was rubber-banded. A door origin sits at floor level and a jump clears about 1, so 2.0 still catches a blink through the door itself")),
                ("allegiance_passup_patron_rate", new Property<double>(0.10, "DreamWeave allegiance passup: flat share of a vassal's earned XP passed up to their patron, independent of how many vassals that patron holds. XP only; luminance never passes up.")),
                ("allegiance_passup_grandpatron_rate", new Property<double>(0.02, "DreamWeave allegiance passup: flat share of a vassal's earned XP passed up to their grandpatron, independent of how many vassals sit anywhere in the chain. Passup stops at the grandpatron. XP only; luminance never passes up.")),
                ("cantrip_drop_rate", new Property<double>(1.0, "Scales the chance for cantrips to drop in each tier. Defaults to 1.0, as per end of retail")),
                ("cloak_cooldown_seconds", new Property<double>(5.0, "The number of seconds between possible cloak procs.")),
                ("cloak_max_proc_base", new Property<double>(0.25, "The max proc chance of a cloak.")),
                ("cloak_max_proc_damage_percentage", new Property<double>(0.30, "The damage percentage at which cloak proc chance plateaus.")),
                ("cloak_min_proc", new Property<double>(0, "The minimum proc chance of a cloak.")),
                ("class_ability_multishot_damage_mult", new Property<double>(0.6, "class abilities: base damage multiplier for extra arrows granted by the Multishot class ability, before the Assess Creature rider and Splitshot gear mod (weapon-granted extra arrows use the weapon's own multiplier). Scales up toward class_ability_multishot_damage_mult_cap")),
                ("class_ability_multishot_damage_mult_cap", new Property<double>(1.0, "class abilities: upper cap on the Multishot extra-arrow damage multiplier after the Assess Creature rider and Splitshot gear mod (1.0 = 100%)")),
                ("class_ability_thorns_percent_per_rank", new Property<double>(0.05, "class abilities: fraction of the equipped shield's effective armor level that the Thorns class ability reflects back per rank")),
                ("class_ability_thorns_max_range", new Property<double>(8.0, "class abilities: Thorns (and the Thorns equipment mod) reflects only when the attacker is within this many metres of the player when the hit lands (edge-to-edge cylinder distance, the melee reach measure) - melee, missile and direct spell hits alike, blocked hits included; DoT ticks never reflect at any range")),
                ("class_ability_poisonweapon_damage_per_rank", new Property<double>(12.0, "class abilities: flat bonus damage per rank added by the Poison Weapon class ability to every landed weapon hit against monsters")),
                ("class_ability_taunt_radius", new Property<double>(15.0, "class abilities: radius (in units) around the player affected by the Taunt class ability")),
                ("class_ability_taunt_duration", new Property<double>(11.0, "class abilities: how long (in seconds) monsters stay forced onto the taunter")),
                // class_ability_taunt_loyalty_per_trained / _per_spec RETIRED 2026-09-12: Taunt's affinity
                // skill moved from Loyalty to Assess Person and from an additive quotient rider to the
                // shared multiplicative primitive (class_ability_affinity_rate_per_trained/_per_spec).
                ("class_ability_taunt_loyalty_bonus_cap_seconds", new Property<double>(10.0, "class abilities: hard cap on the extra seconds the Assess Person affinity bonus can add to Taunt's hold duration")),
                ("class_ability_battlehardened_reduction_per_strength", new Property<double>(0.0005, "class abilities: fraction of all incoming damage reduced per point of Strength by the Battle Hardened class ability (0.0005 = 0.05% per point)")),
                ("class_ability_battlehardened_max_reduction", new Property<double>(0.5, "class abilities: hard cap on the total fraction of incoming damage the Battle Hardened class ability can reduce, regardless of Strength")),
                ("class_ability_frenzy_percent_per_stack", new Property<double>(0.05, "class abilities: attack-speed increase per Frenzy stack (0.05 = +5% per stack; nominal +50% at 10 stacks no longer saturates the raised anim ceiling)")),
                ("class_ability_frenzy_expire_seconds", new Property<double>(10.0, "class abilities: seconds without landing a weapon hit before Frenzy stacks reset")),
                // Recklessness affinity MULTIPLIES the per-stack bonus above (migrated 2026-09-12); the two
                // dead class_ability_frenzy_reckless_* divisors were deleted with the migration, Frenzy
                // having been their only reader. The old divisors were deliberately inflated (250/185) to
                // offset being paid out up to 10 times; the shared rate carries no such offset.
                ("class_ability_attack_speed_ceiling", new Property<double>(4.5, "class abilities: hard ceiling on buffed attack animation speed when class abilities push past the normal 2.0x cap (raised 3.5 -> 4.5 on 2026-07-30: Frenzy and Attack Speed compose MULTIPLICATIVELY in GetAnimSpeed and both carry skill riders, so the deepest ability-only cross reaches 4.08 anim at trained riders and 4.32 specialized - it was being clipped at 3.5. At 4.5 the specialized cross plus a 4% weapon-mod attack-speed roll fits at 4.46, while gear stacking still clips around 5.15, which is deliberate. This is the only lever on the axis: the 2.0 base is MaxAttackSpeed, a private static)")),
                ("class_ability_netherrush_percent_per_rank", new Property<double>(0.05, "class abilities: void-cast-speed increase per Nether Rush stack, per rank (0.05 = +5%/stack at rank 1, +15%/stack at rank 3)")),
                ("class_ability_netherrush_expire_seconds", new Property<double>(20.0, "class abilities: seconds without casting a void spell before Nether Rush stacks reset")),
                // LOYALTY affinity (moved off Arcane Lore) MULTIPLIES the per-stack rank bonus above
                // (migrated 2026-09-12); the two dead class_ability_netherrush_arcanelore_* divisors were
                // deleted with the migration, Nether Rush having been their only reader. Those divisors
                // were deliberately tight (125/92) because this per-stack bonus pays out up to 5 times on
                // an axis with no ceiling, unlike attack speed; the SHARED rate carries no such tightening,
                // so this is the one migrated buff whose affinity term gets stronger rather than weaker.
                ("class_ability_spellaoe_radius", new Property<double>(5.0, "class abilities: radius (in meters) around an Arc-spell-struck monster within which the Spell AOE class ability radiates a copy of the same spell at every other hostile creature (no upper bound on count)")),
                ("class_ability_spellaoe_damage_mult_r1", new Property<double>(0.15, "class abilities: base fraction of normal spell damage dealt by each Spell AOE secondary (radiated) blast at RANK 1 (0.15 = 15%). Scales up toward class_ability_spellaoe_damage_mult_cap with Mana Conversion")),
                ("class_ability_spellaoe_damage_mult_r2", new Property<double>(0.30, "class abilities: base fraction of normal spell damage dealt by each Spell AOE secondary (radiated) blast at RANK 2 (0.30 = 30%). Scales up toward class_ability_spellaoe_damage_mult_cap with Mana Conversion")),
                ("class_ability_spellaoe_damage_mult_r3", new Property<double>(0.50, "class abilities: base fraction of normal spell damage dealt by each Spell AOE secondary (radiated) blast at RANK 3 (0.50 = 50%). Scales up toward class_ability_spellaoe_damage_mult_cap with Mana Conversion")),
                ("class_ability_lum_ratio_1", new Property<double>(1.25, "class abilities: per-point cost multiplier of the Luminance-purchase curve for points up to breakpoint_1 (the early handoff segment, DESIGN sec 2b)")),
                ("class_ability_lum_ratio_2", new Property<double>(1.5, "class abilities: per-point cost multiplier of the Luminance-purchase curve between breakpoint_1 and breakpoint_2 (the mid-game)")),
                ("class_ability_lum_ratio_3", new Property<double>(1.75, "class abilities: per-point cost multiplier of the Luminance-purchase curve past breakpoint_2 (the uncapped tail, forever)")),
                ("class_ability_xp_ratio_1", new Property<double>(1.4, "class abilities: per-point cost multiplier of the EXPERIENCE-purchase curve up to breakpoint_1. All three xp ratios are 1.4 by default, which collapses the piecewise curve to a single geometric run (base * 1.4^(n-1)) regardless of the breakpoints")),
                ("class_ability_xp_ratio_2", new Property<double>(1.4, "class abilities: per-point cost multiplier of the EXPERIENCE-purchase curve between breakpoint_1 and breakpoint_2")),
                ("class_ability_xp_ratio_3", new Property<double>(1.4, "class abilities: per-point cost multiplier of the EXPERIENCE-purchase curve past breakpoint_2 (the uncapped tail, forever)")),
                ("class_ability_spellaoe_damage_mult_cap", new Property<double>(0.75, "class abilities: upper cap on the Spell AOE radiated-blast damage fraction after the Mana Conversion rider (0.75 = 75%)")),
                // Spell AOE's Mana Conversion rider MULTIPLIES its own rank base (migrated 2026-09-12); its
                // two dead per_trained / per_spec divisors were deleted with the migration, Spell AOE having
                // been their only reader.
                ("class_ability_spellaoe_mana_surcharge", new Property<double>(1.0, "class abilities: extra mana cost fraction added to a qualifying Arc cast of any school when Spell AOE is learned (1.0 = +100%); FLAT - it does not scale with rank, so rank 1 pays the full surcharge for the smallest blast. Stacks additively with class_ability_overchannel_mana_surcharge")),
                // class_ability_poisonweapon_alchemy_per_trained / _per_spec / _threshold RETIRED 2026-09-12:
                // Poison Weapon's Alchemy rider moved from a thresholded additive quotient to the shared
                // multiplicative primitive (class_ability_affinity_rate_per_trained/_per_spec), and the
                // threshold was dropped entirely - the design document specifies no threshold for this
                // ability under the new model, so a character with sub-100 Alchemy now gets a (smaller than
                // before) bonus instead of nothing.
                ("class_ability_poisonweapon_proc_delay", new Property<double>(0.1, "class abilities: seconds the Poison Weapon proc is deferred after the weapon strike so its damage + combat line land just after the main hit (0 = next tick)")),
                // Multishot's Assess Creature rider migrated to the multiplicative affinity primitive
                // (2026-09-12 overhaul); its two dead per_trained / per_spec divisors were deleted with the
                // migration, Multishot having been their only reader.
                ("class_ability_thorns_shield_per_trained", new Property<double>(50.0, "class abilities: points of effective Shield per +1% of shield armor level added to the Thorns reflect fraction, Trained source; smaller = stronger")),
                ("class_ability_thorns_shield_per_spec", new Property<double>(36.0, "class abilities: points of effective Shield per +1% of shield armor level added to the Thorns reflect fraction, Specialized source (tighter dual-ratio divisor)")),
                // MULTIPLICATIVE affinity rates (2026-09-12 overhaul). A rate is the added fraction of an
                // ability's OWN rank bonus per 100 points of its affinity skill, applied through
                // ClassAbilityAffinity.Multiplier / Player.GetClassAbilityAffinityMultiplier.
                //
                // These are RATES: larger is STRONGER. That is the opposite sense of the legacy
                // *_per_trained / *_per_spec keys elsewhere in this block, which are DIVISORS feeding
                // ClassAbilityScaling.Compute. The two have identical shapes and no compile-time
                // distinction, so never feed one primitive's tunables to the other.
                //
                // One shared pair covers every affinity ability except Bloodlust. Void Damage and Soul
                // Jump used to carry their own off-standard pair here rather than a rate field on
                // ClassAbilityDefinition - that field set is exported verbatim to the planner catalog, so
                // a rate tweak there would force a catalog regen. The 2026-10-02 owner ruling retired
                // both per-ability pairs instead of standardizing their VALUES, specifically so a future
                // change to the shared pair needs no per-ability follow-up:
                // class_ability_affinity_voiddamage_rate_per_trained / _per_spec (was 0.20 / 0.28) and
                // class_ability_affinity_souljump_rate_per_trained / _per_spec (was 0.05 / 0.07) are gone;
                // both abilities now call the single-argument GetClassAbilityAffinityMultiplier overload,
                // which reads this shared pair directly. See
                // Database/Updates/Shard/2026-10-02-01-Retire-SoulJump-VoidDamage-Affinity-Rate-Keys.sql
                // for the migration that deletes any persisted row for the four retired keys. Bloodlust
                // is now the ONLY ability with its own rate pair, off-standard in SHAPE rather than
                // magnitude - its two rates pinned EQUAL to each other so that specializing pays no more
                // than training - see its own pair below for why.
                ("class_ability_affinity_rate_per_trained", new Property<double>(0.09, "class abilities: SHARED multiplicative affinity rate - added fraction of an ability's own rank bonus per 100 points of its affinity skill, Trained source (0.09 = +9% of the bonus per 100 points). Larger = stronger. Covers every affinity ability except Bloodlust")),
                ("class_ability_affinity_rate_per_spec", new Property<double>(0.14, "class abilities: SHARED multiplicative affinity rate per 100 points of the affinity skill, Specialized source (0.14 = +14% of the ability's own bonus per 100 points)")),
                ("class_ability_affinity_bloodlust_rate_per_trained", new Property<double>(0.12, "class abilities: Bloodlust's own multiplicative affinity rate per 100 points of Salvaging, Trained source (0.12, deliberately ABOVE the shared 0.09; its SPEC half is pinned equal to this trained rate rather than taking the shared spec rate - see the pair below for why)")),
                ("class_ability_affinity_bloodlust_rate_per_spec", new Property<double>(0.12, "class abilities: Bloodlust's own multiplicative affinity rate per 100 points of Salvaging, Specialized source, deliberately PINNED EQUAL to Bloodlust's own Trained rate (0.12) rather than taking the shared spec rate (0.14). Salvaging reaches Specialized through a free retail augmentation (AugmentationSpecializeSalvaging) rather than by spending a skill credit, so a tighter spec rate would be power no build pays for. This restores a pin the 2026-09-12 migration had dropped; do not 'fix' the two to differ")),
                // Deadeye (Archer T2): +8%/rank missile damage, Fletching affinity MULTIPLIES that rank
                // bonus (migrated 2026-09-12; its two dead per_trained / per_spec divisors were deleted with
                // the migration, Deadeye having been their only reader)
                ("class_ability_deadeye_percent_per_rank", new Property<double>(0.095, "class abilities: missile damage bonus per rank of Deadeye (0.095 = +9.5%/rank), before the Fletching affinity multiplier")),
                // Heavy Draw (Archer T2): +6/12/18% missile damage, stamina surcharge
                ("class_ability_heavydraw_percent_per_rank", new Property<double>(0.06, "class abilities: missile damage bonus per rank of Heavy Draw (0.06 = +6%/rank)")),
                ("class_ability_heavydraw_stamina_cost", new Property<double>(10.0, "class abilities: extra stamina consumed per Heavy Draw shot; if the player lacks it the shot lands with no bonus")),
                ("class_ability_heavydraw_run_per_trained", new Property<double>(25.0, "class abilities: points of effective Run per -1% Heavy Draw stamina surcharge, Trained source")),
                ("class_ability_heavydraw_run_per_spec", new Property<double>(18.0, "class abilities: points of effective Run per -1% Heavy Draw stamina surcharge, Specialized source")),
                ("class_ability_heavydraw_run_reduction_cap", new Property<double>(0.50, "class abilities: hard cap on the fraction the Run rider can reduce the Heavy Draw stamina surcharge by (0.50 = the surcharge can never be reduced by more than 50%)")),
                // Long Draw (Archer T3): distance-scaled missile damage
                ("class_ability_longdraw_percent_per_rank", new Property<double>(0.10, "class abilities: peak missile damage bonus per rank of Long Draw at max range (0.10 = +10%/rank)")),
                ("class_ability_longdraw_min_distance", new Property<double>(15.0, "class abilities: distance (meters) below which Long Draw gives no bonus")),
                ("class_ability_longdraw_max_distance", new Property<double>(50.0, "class abilities: distance (meters) at which Long Draw reaches its full per-rank bonus")),
                // Savage Blows (Berserker T1): +6/12/18% melee damage, stamina surcharge, Weapon Tinkering rider
                ("class_ability_savageblows_percent_per_rank", new Property<double>(0.08, "class abilities: melee damage bonus per rank of Savage Blows (0.08 = +8%/rank)")),
                ("class_ability_savageblows_stamina_surcharge", new Property<double>(0.25, "class abilities: extra stamina cost fraction of a Savage Blows melee swing, charged ONCE per swing on the swing's base attack stamina (0.25 = +25%, at least 1 when > 0); stacks additively with Whirlwind's surcharge; if unaffordable the swing lands with no Savage Blows bonus")),
                // Savage Blows' Weapon Tinkering rider migrated to the multiplicative affinity primitive
                // (2026-09-12 overhaul); its two dead per_trained / per_spec divisors were deleted with the
                // migration, Savage Blows having been their only reader.
                // Blood Fury (was Berserker T2): melee damage rose as the attacker's health fell. RETIRED
                // 2026-08-17; nothing reads these three any more. They stay REGISTERED, not deleted, so a
                // stage or prod config row someone set for them still resolves to a known key instead of
                // becoming an orphaned override. Delete them only alongside a sweep of those config rows.
                ("class_ability_bloodfury_peak_per_rank", new Property<double>(0.10, "class abilities: peak melee damage bonus per rank of Blood Fury at/below the low-health threshold (0.10 = +10%/rank)")),
                ("class_ability_bloodfury_start_hp_fraction", new Property<double>(0.75, "class abilities: attacker health fraction at/below which Blood Fury begins to ramp up")),
                ("class_ability_bloodfury_peak_hp_fraction", new Property<double>(0.25, "class abilities: attacker health fraction at/below which Blood Fury reaches its full bonus")),
                // Break Armor (Berserker T2, 2026-08-17, replaces Blood Fury): flat proc chance to cast
                // Imperil III/V/VII (by rank) on a landed melee hit. Rank-invariant chance (rank buys the
                // Imperil rung, not the odds); Weapon Tinkering rider clamped by class_ability_affinity_chance_cap.
                ("class_ability_breakarmor_chance", new Property<double>(0.15, "class abilities: Break Armor proc chance, flat and rank-invariant (0.15 = 15%)")),
                // Break Armor's Weapon Tinkering rider migrated to the multiplicative affinity primitive
                // (2026-09-12 overhaul); its two dead per_trained / per_spec divisors were deleted with the
                // migration, Break Armor having been their only reader.
                // Executioner (Berserker T2): +4/8/12% melee damage vs low-health targets
                ("class_ability_executioner_percent_per_rank", new Property<double>(0.07, "class abilities: melee damage bonus per rank of Executioner vs targets below the execute threshold (0.07 = +7%/rank)")),
                ("class_ability_executioner_hp_fraction", new Property<double>(0.25, "class abilities: target health fraction below which Executioner applies")),
                // Executioner's affinity source moved Dirty Fighting -> Recklessness and its rider migrated
                // to the multiplicative affinity primitive (2026-09-12 overhaul); its two dead per_trained /
                // per_spec divisors were deleted with the migration, Executioner having been their only reader.
                // Bloodlust (Berserker T3): melee lifesteal
                ("class_ability_bloodlust_percent_per_rank", new Property<double>(0.015, "class abilities: fraction of melee damage dealt healed back per rank of Bloodlust (0.015 = 1.5%/rank)")),
                // Bloodlust's Salvaging rider migrated to the multiplicative affinity primitive (2026-09-12
                // overhaul); its two dead per_trained / per_spec divisors were deleted with the migration,
                // Bloodlust having been their only reader. The spec-equals-trained pin those divisors
                // encoded was NOT lost with them - it is restored above as
                // class_ability_affinity_bloodlust_rate_per_trained / _per_spec, both 0.12, on the repo
                // owner's call. That is why Bloodlust takes the three-argument affinity overload.
                // Attack Speed (Rogue T3): constant +5%/rank attack animation speed, shares the Frenzy ceiling, Lockpick rider
                ("class_ability_attackspeed_percent_per_rank", new Property<double>(0.06, "class abilities: constant attack-speed increase per rank of the Attack Speed skill (0.06 = +6%/rank), before the Lockpick affinity multiplier, stacking with Frenzy under class_ability_attack_speed_ceiling")),
                // Flat Cast Speed (Archmage T2): constant +10%/rank war-magic cast speed
                ("class_ability_flatcastspeed_percent_per_rank", new Property<double>(0.10, "class abilities: constant war-magic cast-speed increase per rank of Flat Cast Speed (0.10 = +10%/rank)")),
                // Overchannel (Archmage T2): +8%/rank spell damage in EVERY school + Arcane Lore rider, +100% mana surcharge
                ("class_ability_overchannel_percent_per_rank", new Property<double>(0.095, "class abilities: damaging-spell damage bonus per rank of Overchannel, in every school (0.095 = +9.5%/rank), before the Arcane Lore affinity multiplier")),
                ("class_ability_overchannel_mana_surcharge", new Property<double>(1.0, "class abilities: extra mana cost fraction added to every qualifying cast of any school when Overchannel is learned (1.0 = +100%); stacks additively with class_ability_spellaoe_mana_surcharge on Arc casts")),
                // Echo Cast (Archmage T3): chance a landed damaging spell projectile of any school re-casts itself for free
                ("class_ability_echocast_chance_base", new Property<double>(0.08, "class abilities: Echo Cast recast chance at rank 1 (0.08 = 8%)")),
                ("class_ability_echocast_chance_step", new Property<double>(0.08, "class abilities: Echo Cast recast chance added per rank above 1 (8/16/24% at ranks 1-3)")),
                // Echo Cast's Magic Item Tinkering rider MULTIPLIES its own rank chance (migrated
                // 2026-09-12); its two dead per_trained / per_spec divisors were deleted with the migration,
                // Echo Cast having been their only reader.
                // Elemental Rend (Archmage T3): chance a landed war spell inflicts the matching element Vulnerability
                ("class_ability_elementalrend_chance_base", new Property<double>(0.10, "class abilities: Elemental Rend proc chance at rank 1 (0.10 = 10%)")),
                ("class_ability_elementalrend_chance_step", new Property<double>(0.08, "class abilities: Elemental Rend proc chance added per rank above 1 (10/18/26% at ranks 1-3)")),
                // Elemental Rend's Arcane Lore rider MULTIPLIES its own rank chance (migrated 2026-09-12,
                // AffinitySkill also moved from Life Magic to Arcane Lore); its two dead per_trained /
                // per_spec divisors were deleted with the migration, Elemental Rend having been their only
                // reader.
                // Avoidance tier: Parry + Shield Block pooled roll, resolved BEFORE evade
                ("class_ability_avoidance_cap", new Property<double>(0.50, "class abilities: combined cap on the pooled Shield Block + Parry avoidance chance (0.50 = 50%)")),
                ("class_ability_parry_percent_per_rank", new Property<double>(0.08, "class abilities: Parry negate chance per rank (0.08 = +8%/rank)")),
                ("class_ability_shieldblock_base", new Property<double>(0.08, "class abilities: Shield Block chance at rank 1 (0.08 = 8%)")),
                ("class_ability_shieldblock_step", new Property<double>(0.06, "class abilities: Shield Block chance added per rank above 1 (8/14/20% at ranks 1-3)")),
                ("class_ability_shieldcheck_strength_base", new Property<double>(0.40, "class abilities: Shield Check Thorns reflect strength at rank 1 (0.40 = 40%)")),
                ("class_ability_shieldcheck_strength_step", new Property<double>(0.30, "class abilities: Shield Check reflect strength added per rank above 1 (40/70/100% at ranks 1-3)")),
                ("class_ability_riposte_fraction_base", new Property<double>(0.40, "class abilities: Riposte counter-strike damage fraction at rank 1 (0.40 = 40%)")),
                ("class_ability_riposte_fraction_step", new Property<double>(0.30, "class abilities: Riposte counter-strike fraction added per rank above 1 (40/70/100% at ranks 1-3)")),
                // Void Damage (Void T3): flat void-spell damage bonus
                ("class_ability_voiddamage_percent_per_rank", new Property<double>(0.08, "class abilities: void-spell damage bonus per rank of Void Damage (0.08 = +8%/rank)")),
                // Acid Proc (Rogue T3): chance a landed weapon hit applies an acid DoT = share of Poison Weapon flat.
                // REWORKED 2026-08-17 (Berserker/Rogue balance pass): the chance is now flat (see the
                // default below for the current value, raised from 0.25 by the 2026-09-12 overhaul),
                // rank-invariant, with NO rider - the owner moved the affinity to the poison-damage bonus
                // below. class_ability_acidproc_chance_step is RETIRED and no longer read by Chance(); it
                // stays registered so an existing stage/prod override row does not orphan.
                ("class_ability_acidproc_chance_base", new Property<double>(0.30, "class abilities: Acid Proc proc chance, flat and rank-invariant (0.30 = 30%)")),
                ("class_ability_acidproc_chance_step", new Property<double>(0.06, "RETIRED 2026-08-17: no longer read. class abilities: was the Acid Proc chance added per rank above 1; the chance is now flat. Left registered so a stage/prod override row does not orphan")),
                ("class_ability_acidproc_dot_fraction", new Property<double>(1.0, "class abilities: Acid Proc per-tick damage as a fraction of the current Poison Weapon flat bonus (1.0 = 100%)")),
                ("class_ability_acidproc_dot_interval", new Property<double>(4.0, "class abilities: seconds between Acid Proc DoT ticks (4s)")),
                // class_ability_acidproc_itemtink_per_trained / _per_spec RETIRED 2026-09-12: Acid Proc's
                // poison-damage affinity skill moved from Item Tinkering to Alchemy and from an additive
                // quotient rider to the shared multiplicative primitive
                // (class_ability_affinity_rate_per_trained/_per_spec).
                // Acid Proc poison-damage bonus (new 2026-08-17): +25/50/75% by rank, applied inside
                // PoisonWeaponAbility.FlatBonus so both the per-hit poison proc and every Acid tick inherit
                // it. Its own affinity cap - NOT class_ability_affinity_chance_cap, which is documented as a
                // PROC CHANCE cap and explicitly out of scope for a damage fraction (see that tunable's doc
                // comment, which lists Spell AOE's damage-fraction rider as the same kind of exclusion).
                ("class_ability_acidproc_damage_base", new Property<double>(0.25, "class abilities: Acid Proc poison-damage bonus at rank 1 (0.25 = +25%)")),
                ("class_ability_acidproc_damage_step", new Property<double>(0.25, "class abilities: Acid Proc poison-damage bonus added per rank above 1 (+25/50/75% at ranks 1-3)")),
                ("class_ability_acidproc_damage_affinity_cap", new Property<double>(0.20, "class abilities: maximum poison-damage bonus the Alchemy affinity rider may contribute, as a fraction (0.20 = +20 percentage points). Acid Proc's affinity skill moved from Item Tinkering to Alchemy in the 2026-09-12 overhaul; this cap bounds whatever that rider adds and was never tied to the old skill. Separate from class_ability_affinity_chance_cap, which bounds PROC CHANCE riders only. 0 = uncapped")),
                // Fast Aim (Archer T2, was Eagle Eye - redefined in place 2026-09-29): lift to the LOW end
                // of the missile accuracy range (Player.GetAccuracyMod), not a flat skill percentage
                ("class_ability_fastaim_low_end_per_rank", new Property<double>(0.10, "class abilities: accuracy-mod low-end lift per rank of Fast Aim, before the Run affinity multiplier (0.10 = +0.10/rank, +0.10/0.20/0.30)")),
                // Double Volley (Archer T3): chance a missile volley re-fires a full second volley for free
                ("class_ability_doublevolley_chance_base", new Property<double>(0.06, "class abilities: Double Volley re-fire chance at rank 1 (0.06 = 6%)")),
                ("class_ability_doublevolley_chance_step", new Property<double>(0.06, "class abilities: Double Volley re-fire chance added per rank above 1 (6/12/18% at ranks 1-3)")),
                // Empowered Summons (Void T2): combat-pet stat boost (health/damage/defenses)
                ("class_ability_empoweredsummons_percent_per_rank", new Property<double>(0.0833, "class abilities: combat-pet stat bonus per rank of Empowered Summons (0.0833 = +8.33%/rank), before the Leadership affinity multiplier")),
                ("class_ability_empoweredsummons_loyalty_per_trained", new Property<double>(10.0, "class abilities: points of effective Loyalty per +1% Empowered Summons combat-pet DURATION, Trained source (SKILL-TABLES-PREVIEW's long-standing proposal)")),
                ("class_ability_empoweredsummons_loyalty_per_spec", new Property<double>(7.5, "class abilities: points of effective Loyalty per +1% Empowered Summons combat-pet DURATION, Specialized source")),
                ("class_ability_empoweredsummons_max_duration_bonus", new Property<double>(1.00, "class abilities: hard cap on the Empowered Summons combat-pet duration bonus (1.00 = +100%, i.e. a 43s pet cannot exceed 86s). Pet uptime was already ~96% against the flat 45s essence cooldown, so this mostly bounds how few charges a summoner burns per minute")),
                // Empowered Summons leech, added by the 2026-09-12 widening: a combat pet's landed hits
                // return a fraction of the damage dealt as health. Registered centrally rather than by the
                // mechanic slice, which correctly refused to touch this file while six sibling slices were
                // running against it.
                ("class_ability_empoweredsummons_leech_percent_per_rank", new Property<double>(0.03, "class abilities: fraction of the damage a combat pet lands that is leeched back as health per rank of Empowered Summons (0.03 = 3%/rank, so 3/6/9% at ranks 1-3), before the Leadership affinity multiplier")),
                // Whirlwind (Berserker T3): stamina surcharge per 360°/+1-target cleaving swing
                ("class_ability_whirlwind_stamina_surcharge", new Property<double>(1.0, "class abilities: extra stamina cost fraction of a Whirlwind cleaving swing (1.0 = +100%); if unaffordable the swing falls back to a normal frontal cleave")),
                // Withering (Void T3): void DoT tick damage bonus
                ("class_ability_withering_percent_per_rank", new Property<double>(0.095, "class abilities: void (nether) DoT tick damage bonus per rank of Withering (0.095 = +9.5%/rank), before the Leadership affinity multiplier")),
                // Mana Barrier (Archmage T2): a share of incoming damage is paid from Mana instead of Health
                ("class_ability_manabarrier_base", new Property<double>(0.06, "class abilities: fraction of incoming damage Mana Barrier diverts to Mana at rank 1 (0.06 = 6%)")),
                ("class_ability_manabarrier_step", new Property<double>(0.06, "class abilities: Mana Barrier diverted fraction added per rank above 1 (6/12/18% at ranks 1-3)")),
                ("class_ability_manabarrier_max_share", new Property<double>(0.25, "class abilities: hard cap on TOTAL Mana Barrier diverted share - the rank share after its Magic Defense affinity multiplier, plus gear (0.25 = 25%). Not an affinity cap")),
                ("class_ability_manabarrier_mana_per_health", new Property<double>(1.0, "class abilities: Mana spent per point of Health Mana Barrier restores (1.0 = one for one; higher makes the barrier more expensive)")),
                // Nether Bloom (Void T2 game-changer): a killed target's void DoT spreads to nearby enemies
                ("class_ability_netherbloom_jumps_per_rank", new Property<double>(1.0, "class abilities: how many other enemies a Nether Bloom DoT spreads to per rank (1.0 = 1/2/3 at ranks 1-3)")),
                ("class_ability_netherbloom_radius", new Property<double>(10.0, "class abilities: radius (in meters) around the slain target within which Nether Bloom looks for creatures to spread the void DoT to")),
                ("class_ability_netherbloom_duration_retained", new Property<double>(1.0, "class abilities: fraction of the slain target's REMAINING DoT duration a Nether Bloom copy carries (1.0 = all of it)")),
                // Soul Tether (Void T2): combat-pet damage reduction + a free resummon after the pet dies
                ("class_ability_soultether_base", new Property<double>(0.105, "class abilities: fraction of combat-pet incoming damage Soul Tether removes at rank 1 (0.105 = 10.5%)")),
                ("class_ability_soultether_step", new Property<double>(0.085, "class abilities: Soul Tether pet damage reduction added per rank above 1 (10.5/19/27.5% at ranks 1-3)")),
                ("class_ability_soultether_max_reduction", new Property<double>(0.50, "class abilities: hard cap on TOTAL Soul Tether combat-pet damage reduction - the rank reduction after its Loyalty affinity multiplier, plus gear (0.50 = 50%). Not an affinity cap")),
                // Sanguine Reserve (Blood Mage T1 GC, 3 ranks, cost 1/2/3): every landed harmful life spell
                // grants a Blood Charge, up to class_ability_bloodmage_charge_stack_cap_rN (3/4/5, long
                // defaults below), each worth +7% life-magic damage. class_ability_bloodmage_charge_per_stack
                // is SHARED with Exsanguinate on purpose - the burst multiplier is the containment lever,
                // and that only holds while both read the same per-charge value.
                ("class_ability_bloodmage_charge_per_stack", new Property<double>(0.07, "class abilities: Sanguine Reserve - life-magic damage added per held Blood Charge (0.07 = +7%). Also the value Exsanguinate multiplies when it spends the pool")),
                ("class_ability_bloodmage_charge_expire_seconds", new Property<double>(15.0, "class abilities: Blood Mage - seconds without adding a Blood Charge stack before the stacks reset")),
                // Exsanguinate (Blood Mage T3 GC, 1 rank, cost 5): a Martyr's Hecatomb or Curse of Raven Fury
                // cast at a FULL pool spends the whole pool at 3x its per-charge value, and ignores part of a
                // Weakened Blood target's HealthDrain RESISTANCE (not the vulnerability term - see
                // ExsanguinateMath).
                //
                // THERE IS NO COOLDOWN TUNABLE, and that is deliberate rather than an omission.
                // class_ability_exsanguinate_cooldown_seconds existed until 2026-08-03 and was removed with
                // the code that read it: the burst used to fire on any non-empty pool with a 10s clock as the
                // only limiter, which made the same cast resolve differently for a reason the player could
                // not see. The rank's stack cap is the gate now. Do not re-add a time gate here without a
                // ruling - it puts the invisible condition straight back.
                ("class_ability_exsanguinate_multiplier", new Property<double>(3.0, "class abilities: Exsanguinate - multiple of Sanguine Reserve's per-charge value applied when the pool is spent (3.0 = a 5-charge pool becomes +105% instead of +35%). THE containment lever if the burst overshoots")),
                ("class_ability_exsanguinate_resist_ignore", new Property<double>(0.25, "class abilities: Exsanguinate - fraction of a Weakened Blood target's HealthDrain resistance ignored on the spending strike (0.25 = 25% of the way to unresisted)")),                // Weakened Blood (Blood Mage T2 GC, 3 ranks, cost 3 flat): a TARGET-SIDE life vulnerability
                // every life caster reads, fed into Creature.GetLifeVulnerabilityMod, which takes MAX against
                // weapon rending rather than multiplying. The three values are the top rungs of the retail
                // vulnerability ladder - Vulnerability V, VI, and the Incantation.
                ("class_ability_weakenedblood_resist_r1", new Property<double>(2.00, "class abilities: Weakened Blood - life resistance modifier on the marked target at rank 1 (retail Vulnerability V)")),
                ("class_ability_weakenedblood_resist_r2", new Property<double>(2.50, "class abilities: Weakened Blood - life resistance modifier on the marked target at rank 2 (retail Vulnerability VI)")),
                ("class_ability_weakenedblood_resist_r3", new Property<double>(3.10, "class abilities: Weakened Blood - life resistance modifier on the marked target at rank 3 (the retail Incantation value exactly - the same ceiling war elements get)")),
                ("class_ability_weakenedblood_duration_seconds", new Property<double>(20.0, "class abilities: Weakened Blood - seconds the mark holds on a target before it lapses. Refreshed by each landed Harm or Drain")),
                ("life_drain_resist_floor", new Property<double>(0.75, "life magic: floor applied to a creature's ResistHealthDrain (float 125) before it multiplies a Life drain. Retail sets 116 creatures to 0 (bloodless constructs, wisps, crystals, plated tuskers) which made a Blood Mage do 0 damage to them; 0.75 keeps a visible resistance while never zeroing the class. Set to 0 to restore retail.")),
                // Crimson Harvest (Blood Mage T2, 1 rank, cost 5): Drain strikes extra creatures near the
                // primary target. Each secondary strike re-enters the transfer path independently, so it
                // keeps its own TransferCap - the target count is the whole effect. Max targets is a long
                // default below. The recoup is DELIBERATELY UNCAPPED pending live observation.
                ("class_ability_crimsonharvest_radius", new Property<double>(8.0, "class abilities: Crimson Harvest - radius in metres around the PRIMARY drain target within which extra creatures are struck")),
                // Blood Price (Blood Mage T3, 3 ranks, cost 3 flat): damaging spells of any school also cost
                // health. The cost is the brake and it grows with the payoff. Below the health floor a cast
                // lands normally - no cost AND no bonus, always both.
                ("class_ability_bloodprice_health_cost_r1", new Property<double>(0.03, "class abilities: Blood Price - fraction of CURRENT health one damaging cast spends at rank 1 (0.03 = 3%)")),
                ("class_ability_bloodprice_health_cost_r2", new Property<double>(0.04, "class abilities: Blood Price - fraction of CURRENT health one damaging cast spends at rank 2 (0.04 = 4%)")),
                ("class_ability_bloodprice_health_cost_r3", new Property<double>(0.05, "class abilities: Blood Price - fraction of CURRENT health one damaging cast spends at rank 3 (0.05 = 5%)")),
                ("class_ability_bloodprice_damage_r1", new Property<double>(0.10, "class abilities: Blood Price - spell damage added at rank 1 (0.10 = +10%), any school")),
                ("class_ability_bloodprice_damage_r2", new Property<double>(0.20, "class abilities: Blood Price - spell damage added at rank 2 (0.20 = +20%), any school")),
                ("class_ability_bloodprice_damage_r3", new Property<double>(0.30, "class abilities: Blood Price - spell damage added at rank 3 (0.30 = +30%), any school")),
                ("class_ability_bloodprice_min_health_fraction", new Property<double>(0.20, "class abilities: Blood Price - fraction of MAXIMUM health below which a cast is free and unbuffed (0.20 = 20%). All-or-nothing: never a cost without the bonus")),
                // Transfusion (Blood Mage T1, 3 ranks, cost 1/1/1) - the FIRST Blood Mage skill with live
                // behaviour: rank sets what fraction of a Drain Health surplus reaches nearby fellows.
                // These scale DELIVERY ONLY - drain damage is byte-identical at every rank.
                ("class_ability_transfusion_share_r1", new Property<double>(0.40, "class abilities: Transfusion - fraction of the Drain Health surplus delivered to fellows at rank 1 (0.40 = 40%). Delivery only; the drain itself is unchanged")),
                ("class_ability_transfusion_share_r2", new Property<double>(0.70, "class abilities: Transfusion - fraction of the Drain Health surplus delivered to fellows at rank 2 (0.70 = 70%). Delivery only; the drain itself is unchanged")),
                ("class_ability_transfusion_share_r3", new Property<double>(1.00, "class abilities: Transfusion - fraction of the Drain Health surplus delivered to fellows at rank 3 (1.00 = 100%). Delivery only; the drain itself is unchanged")),
                // Transfusion's Healing rider MULTIPLIES its own rank share fraction (migrated 2026-09-12,
                // shared 0.09 Trained / 0.14 Specialized rate); its two dead per_trained / per_spec divisors
                // were deleted with the migration, Transfusion having been their only reader. Still
                // deliberately UNCAPPED and allowed past 100% - see DrainSurplusDistribution.ShareFraction
                // for why that is safe.
                // Malediction (Blood Mage T2, 3 ranks, cost 2/2/2) - Vulnerability and Imperil enchantments
                // the player applies land harder and last longer, for EVERY attacker on that target. The
                // intensity bonus scales the debuff's distance from its identity value (1.0 multiplicative,
                // 0.0 additive), not the raw StatModValue - see MaledictionAbility.ScaleStatModValue
                ("class_ability_malediction_intensity_base", new Property<double>(0.10, "class abilities: Malediction Vulnerability/Imperil intensity bonus at rank 1 (0.10 = +10%)")),
                ("class_ability_malediction_intensity_step", new Property<double>(0.10, "class abilities: Malediction intensity bonus added per rank above 1 (+10/20/30% at ranks 1-3)")),
                ("class_ability_malediction_duration_bonus", new Property<double>(0.25, "class abilities: how much longer a Malediction-boosted Vulnerability/Imperil lasts (0.25 = 25% longer). Flat - it does NOT scale with rank")),
                // Malediction's Creature Enchantment rider MULTIPLIES its own rank intensity bonus (migrated
                // 2026-09-12, AffinitySkill also moved from Life Magic to Creature Enchantment); its two
                // dead per_trained / per_spec divisors were deleted with the migration, Malediction having
                // been their only reader.
                // Sanguine Ward (Blood Mage T3, 3 ranks, cost 3/3/3) - Martyr's Hecatomb and Curse of Raven
                // Fury still spend their FULL health basis for damage; these only change what the caster
                // actually pays and what comes back as absorb. selfcost is the share of the basis actually
                // lost (so LOWER IS STRONGER); absorb is the ward, as a share of the amount actually lost.
                // The ward REFRESHES rather than stacks and EXPIRES UNUSED - see SanguineWardMath.
                ("class_ability_sanguine_ward_selfcost_r1", new Property<double>(0.75, "class abilities: Sanguine Ward - fraction of the Hecatomb/Raven Fury health basis the caster actually loses at rank 1 (0.75 = 75%). LOWER IS STRONGER; the damage basis is unchanged")),
                ("class_ability_sanguine_ward_selfcost_r2", new Property<double>(0.55, "class abilities: Sanguine Ward - fraction of the Hecatomb/Raven Fury health basis the caster actually loses at rank 2 (0.55 = 55%). LOWER IS STRONGER; the damage basis is unchanged")),
                ("class_ability_sanguine_ward_selfcost_r3", new Property<double>(0.35, "class abilities: Sanguine Ward - fraction of the Hecatomb/Raven Fury health basis the caster actually loses at rank 3 (0.35 = 35%). LOWER IS STRONGER; the damage basis is unchanged")),
                ("class_ability_sanguine_ward_absorb_r1", new Property<double>(0.60, "class abilities: Sanguine Ward - damage-absorbing ward granted at rank 1, as a fraction of the health actually lost to the cast (0.60 = 60%). Absorb only - it never restores health")),
                ("class_ability_sanguine_ward_absorb_r2", new Property<double>(0.90, "class abilities: Sanguine Ward - damage-absorbing ward granted at rank 2, as a fraction of the health actually lost to the cast (0.90 = 90%). Absorb only - it never restores health")),
                ("class_ability_sanguine_ward_absorb_r3", new Property<double>(1.20, "class abilities: Sanguine Ward - damage-absorbing ward granted at rank 3, as a fraction of the health actually lost to the cast (1.20 = 120%). Absorb only - it never restores health")),
                ("class_ability_sanguine_ward_duration_seconds", new Property<double>(15.0, "class abilities: Sanguine Ward - seconds the ward lasts. A recast REFRESHES it rather than stacking, and an unspent ward expires with no refund")),
                // Spellblade (Spellsword T1 GC, 3 ranks, cost 1/2/3): a landed LIGHT WEAPON hit casts a
                // Streak war spell matching the damage type actually dealt. The chance is deliberately
                // RANK-INVARIANT - rank buys spell LEVEL (III / V / the level-8 Incantation), which is the
                // shared shape of all three war procs. The Item Enchantment rider is what makes the chance
                // skill-sensitive.
                ("class_ability_affinity_chance_cap", new Property<double>(0.20, "class abilities: maximum PROC CHANCE an affinity rider may contribute, as a fraction (0.20 = +20 percentage points). ClassAbilityAffinity.Multiplier grows without bound as the source skill rises (1.0 + skill/100 * rate), so the amount a rider adds - chanceBase * (skill/100) * rate - has no ceiling of its own, and without this a high source skill saturates a proc to a literal 100%. Observed live 2026-08-04 at Item Enchantment 5226 (+209 points) under the legacy additive quotient, which was unbounded in the same way; the 2026-09-12 migration to the multiplicative primitive changed the shape of the rider but not the need for this cap. The skill at which the cap binds is now PER-ABILITY rather than one number, because it scales with each ability's own base chance (Spellblade at chanceBase 0.25 and the Specialized rate 0.14 reaches it near 571 points; a lower base chance needs more). Applies to every chance-on-hit ability carrying an affinity: Spellblade, Runeblade, Sundermark, Break Armor, Elemental Rend, Dispelling Edge and Shield Block. Spellstorm has no affinity and is unaffected. Spell AOE's rider and Acid Proc's poison-damage rider (class_ability_acidproc_damage_affinity_cap, 2026-08-17) are damage FRACTIONS rather than a chance and are deliberately out of scope. 0 = uncapped")),
            ("class_ability_spellblade_chance", new Property<double>(0.25,"class abilities: Spellblade - chance a landed light weapon hit casts a matching Streak war spell (0.25 = 25%). Rank does NOT raise this; rank raises the spell level cap")),
                // class_ability_spellblade_itemench_per_trained / _per_spec RETIRED 2026-09-12: Spellblade's
                // Item Enchantment rider moved from an additive quotient to the shared multiplicative
                // primitive (class_ability_affinity_rate_per_trained/_per_spec).
                // Runeblade (Spellsword T2 GC, 3 ranks, cost 1/2/3): the same shape as Spellblade but firing
                // a Blast cone. A Blast lands exactly ONE projectile on the centre target, so its
                // single-target damage EQUALS Spellblade's at the same level and everything it adds is
                // cleave. 0.22 is priced against that: below Spellblade's 0.25 on single targets, paying for
                // itself against a pack. Do not re-price it as though a Blast were a bigger Streak.
                // 2026-09-14: Spellblade 0.22 -> 0.25 and Runeblade 0.19 -> 0.22 so the multiplicative
                // affinity is at or above the pre-overhaul chance at every affinity skill (the tightest point
                // is 500 Trained / 360 Specialized, where the old additive rider hit its 0.20 cap)
                ("class_ability_runeblade_chance", new Property<double>(0.22, "class abilities: Runeblade - chance a landed light weapon hit casts a matching Blast war spell (0.22 = 22%). Rank does NOT raise this; rank raises the spell level cap")),
                // class_ability_runeblade_magicitemtink_per_trained / _per_spec RETIRED 2026-09-12: Runeblade's
                // Magic Item Tinkering rider moved from an additive quotient to the shared multiplicative
                // primitive (class_ability_affinity_rate_per_trained/_per_spec).
                // Spellstorm (Spellsword T3 GC, 1 rank, cost 5): a landed light weapon hit fires the tier-I
                // Ring - 9 projectiles at 360 degrees. Single rank and a single fixed spell level, so there
                // is no level tunable and no affinity rider here: it is a shape change, not a ladder
                ("class_ability_spellstorm_chance", new Property<double>(0.14, "class abilities: Spellstorm - chance a landed light weapon hit fires a matching 360-degree war Ring (0.14 = 14%)")),
                // Sundermark (Spellsword T2, 3 ranks, cost 3 flat): the class's ANY-WEAPON entry. Unlike the
                // three war procs its chance DOES scale with rank, and it stays low - but the low chance is
                // not the brake. Vulnerability durations are long against ~1 swing/sec, so uptime after the
                // opener is effectively 100%. It ships that way by ruling: a rending weapon already occupies
                // the enchantment slot this writes to (vulnMod = max(vulnMod, weaponResistanceMod)), and any
                // player with the Life Magic can cast the level-8 vulnerability directly
                ("class_ability_sundermark_chance_base", new Property<double>(0.07, "class abilities: Sundermark - chance a landed hit with ANY weapon applies the matching elemental Vulnerability, at rank 1 (0.07 = 7%)")),
                ("class_ability_sundermark_chance_step", new Property<double>(0.06, "class abilities: Sundermark - chance added per rank above 1 (7/13/19% at ranks 1-3)")),
                // class_ability_sundermark_lifemagic_per_trained / _per_spec RETIRED 2026-09-12: Sundermark's
                // Life Magic rider moved from an additive quotient to the shared multiplicative primitive
                // (class_ability_affinity_rate_per_trained/_per_spec).
                // Shared by all four Spellsword procs. Spell level is chosen by comparing the relevant magic
                // skill's Current against SpellFormula.MinPower (1/50/100/150/200/250/300/400 for levels
                // 1-8); this scales those thresholds. LARGER IS STRICTER - 2.0 means you need twice the
                // skill for the same rung. It is the single lever if procs reach their top rung too early
                ("class_ability_spellsword_level_skill_scale", new Property<double>(1.0, "class abilities: Spellsword - multiplier on the SpellFormula.MinPower thresholds that decide which spell level a proc fires (1.0 = the engine's own thresholds; larger is stricter)")),
                // Dispelling Edge (Spellsword T3, 3 ranks, cost 3 flat): a landed hit strips ONE beneficial
                // enchantment from the target. Beneficial only - stripping a harmful one would remove the
                // party's own debuffs, including this class's Sundermark
                ("class_ability_dispellingedge_chance_base", new Property<double>(0.10, "class abilities: Dispelling Edge - chance a landed hit strips one of the target's beneficial enchantments, at rank 1 (0.10 = 10%)")),
                ("class_ability_dispellingedge_chance_step", new Property<double>(0.06, "class abilities: Dispelling Edge - chance added per rank above 1 (10/16/22% at ranks 1-3)")),
                // class_ability_dispellingedge_arcanelore_per_trained / _per_spec RETIRED 2026-09-12:
                // Dispelling Edge's affinity skill moved from Arcane Lore to Item Enchantment and from an
                // additive quotient rider to the shared multiplicative primitive
                // (class_ability_affinity_rate_per_trained/_per_spec).
                // Resonance (Spellsword T1, 3 ranks, cost 3 flat): landing ANY magic damage grants a stack,
                // and each stack adds to ALL your magic damage. Both halves are deliberately broad - this is
                // the entry that makes Spellsword a magic class rather than a melee class with sparks, and
                // it is what lets an Archmage/Spellsword splash ramp in either direction. Because it is a
                // general magic-damage multiplier it MULTIPLIES with Overchannel and Void Damage; the short
                // window is the brake. The per-stack value was halved from the original sketch when the
                // scope widened from proc damage to all magic damage
                ("class_ability_resonance_per_stack_r1", new Property<double>(0.012, "class abilities: Resonance - magic damage added per held stack at rank 1 (0.012 = +1.2%, so +6% at the 5-stack cap)")),
                ("class_ability_resonance_per_stack_r2", new Property<double>(0.018, "class abilities: Resonance - magic damage added per held stack at rank 2 (0.018 = +1.8%, so +9% at the 5-stack cap)")),
                ("class_ability_resonance_per_stack_r3", new Property<double>(0.024, "class abilities: Resonance - magic damage added per held stack at rank 3 (0.024 = +2.4%, so +12% at the 5-stack cap)")),
                ("class_ability_resonance_window_seconds", new Property<double>(6.0, "class abilities: Resonance - seconds without landing magic damage before the stacks lapse. Short on purpose: it is the real brake now that the bonus covers all magic damage")),
                // Spellsurge (Spellsword T2, 1 rank, cost 5): landed WAR procs stack a bonus to proc chance.
                // A chance bonus on a chance mechanic feeds itself, so the stack cap - not the per-stack
                // value - is the term that bounds it. At the defaults the ceiling is Spellblade 20 -> 30%,
                // Runeblade 15 -> 25%, Spellstorm 10 -> 20%. Sundermark neither feeds it nor reads it, so an
                // any-weapon entry can never ramp the light-weapon-only ones
                ("class_ability_spellsurge_per_stack", new Property<double>(0.02, "class abilities: Spellsurge - war-proc chance added per held stack (0.02 = +2 percentage points, so +10pp at the 5-stack cap)")),
                ("class_ability_spellsurge_window_seconds", new Property<double>(10.0, "class abilities: Spellsurge - seconds without landing a war proc before the stacks decay")),
                // Cascade (Spellsword T3, 3 ranks, cost 3 flat): a landed proc fires again at a second
                // nearby enemy. Pure AoE column. The generation guard below is what stops it chaining
                ("class_ability_cascade_chance", new Property<double>(0.25, "class abilities: Cascade - chance a landed Spellsword proc immediately fires again at a second nearby enemy (0.25 = 25%)")),
            ("class_ability_cascade_radius", new Property<double>(8.0, "class abilities: Cascade - how far from the struck creature the second target may be, in meters. Matches class_ability_crimsonharvest_radius, the other 'spread to a creature near the one I just struck' selection, rather than Spell AOE's 5.0m whole-pack radiate")),

                // Berserker/Rogue balance pass 2026-08-17 - Rogue T2 Surefooted. Evading a melee attack
                // builds a stack (cap 5); each stack is worth rank x 1% parry chance, so a rank-3 Rogue at a
                // full pool holds +15% parry. That figure lands INSIDE the pooled 50% Shield Block + Parry
                // cap (class_ability_avoidance_cap), which is what bounds the whole avoidance tier
                ("class_ability_surefooted_percent_per_rank_per_stack", new Property<double>(0.01, "class abilities: Surefooted - parry chance added per held stack PER RANK (0.01 = +1%/stack at rank 1, +2% at rank 2, +3% at rank 3; +15% at rank 3 with a full 5-stack pool)")),
                ("class_ability_surefooted_expire_seconds", new Property<double>(10.0, "class abilities: Surefooted - seconds without evading a melee attack before the stacks lapse. A landed melee hit knocks the pool off immediately regardless of this timer")),

                // Berserker/Rogue balance pass 2026-08-17 - Rogue T3 Pocket Sand. Any avoided attack (evade,
                // class-ability parry or block, or a resisted spell) has a 10/20/30% chance by rank to lower
                // ALL of the attacker's attack skills by 30 for 20 seconds. Written in a fork-reserved
                // synthetic SpellCategory so it SUMS with retail Dirty Fighting's attack debuff instead of
                // replacing it; a re-proc refreshes the existing entry rather than adding a second
                ("class_ability_pocketsand_chance_base", new Property<double>(0.15, "class abilities: Pocket Sand - chance an avoided attack blinds the attacker, at rank 1 (0.15 = 15%)")),
                ("class_ability_pocketsand_chance_step", new Property<double>(0.15, "class abilities: Pocket Sand - chance added per rank above 1 (15/30/45% at ranks 1-3)")),
                ("class_ability_pocketsand_magnitude", new Property<double>(40.0, "class abilities: Pocket Sand - points subtracted from every one of the blinded creature's attack skills (40.0 = -40). Rank-invariant; rank buys chance, not magnitude")),
                ("class_ability_pocketsand_duration_seconds", new Property<double>(20.0, "class abilities: Pocket Sand - how long the blind lasts, in seconds. A re-proc on the same creature refreshes this window rather than stacking a second debuff")),

                // 2026-09-12 class ability overhaul, wave of 15 new REGISTRATION-ONLY entries. Implemented is
                // false on every one of these; nothing reads these tunables yet. Grouped by class below in
                // the same order as the design table. Every ability that owes an affinity rider reads the
                // SHARED multiplicative primitive (class_ability_affinity_rate_per_trained/_per_spec) unless
                // noted otherwise - none of these fifteen needs its own rate pair or its own affinity cap.
                //
                // Hunter's Mark (Archer T1): +1/2/3/4/5% damage to a marked target from every source
                ("class_ability_huntersmark_percent_per_rank", new Property<double>(0.01, "class abilities: Hunter's Mark - damage bonus taken by a marked target from every source, per rank (0.01 = +1%/rank, so +5% at rank 5). Higher Assess Creature multiplies the bonus")),
                ("class_ability_huntersmark_duration_seconds", new Property<double>(10.0, "class abilities: Hunter's Mark - seconds a mark lasts on a target after being applied by a landed attack")),
                // Pinning Shot (Archer T2): 10/20/30% chance to pin the target for 5s, 15s immunity per target
                ("class_ability_pinningshot_chance_base", new Property<double>(0.10, "class abilities: Pinning Shot - chance a missile hit pins the target in place, at rank 1 (0.10 = 10%)")),
                ("class_ability_pinningshot_chance_step", new Property<double>(0.10, "class abilities: Pinning Shot - chance added per rank above 1 (10/20/30% at ranks 1-3). Higher Assess Creature multiplies the chance, capped by class_ability_affinity_chance_cap")),
                ("class_ability_pinningshot_duration_seconds", new Property<double>(5.0, "class abilities: Pinning Shot - seconds a pinned target is held in place. A pin is not a stun: the target can still attack and cast")),
                ("class_ability_pinningshot_immunity_seconds", new Property<double>(15.0, "class abilities: Pinning Shot - seconds a target is immune to being pinned again after a pin ends, so the effect cannot be chained into a permanent lock")),
                ("class_ability_pinningshot_slow_seconds", new Property<double>(10.0, "class abilities: Pinning Shot - seconds the movement slow lasts. The window starts when the PIN ENDS, not when the shot lands. Zero disables the slow outright, leaving the pin unchanged")),
                ("class_ability_pinningshot_slow_factor", new Property<double>(0.2, "class abilities: Pinning Shot - movement speed MULTIPLIER applied for class_ability_pinningshot_slow_seconds after the pin ends (0.2 = a fifth of normal speed, an 80% slow). Applied identically to the wire value the client animates from and to server physics. A value of 1.0 - or anything outside the open interval 0.0 to 1.0 - disables the slow")),
                // Opportunist (Rogue T1): +2/4/6/8/10% damage vs a target carrying a debuff or CC, from you or a fellow
                ("class_ability_opportunist_percent_per_rank", new Property<double>(0.02, "class abilities: Opportunist - damage bonus vs a target carrying a debuff or crowd control applied by you or a fellow, per rank (0.02 = +2%/rank, so +10% at rank 5). Higher Sneak Attack multiplies the bonus")),
                // Killer Instinct (Rogue T3): a crit (or any landed Sneak Attack) grants an 8s Opening, stacking
                // to 3; each Opening is worth +4/7/10% crit damage by rank plus a flat +2% crit chance
                ("class_ability_killerinstinct_critdamage_base", new Property<double>(0.04, "class abilities: Killer Instinct - critical damage added per held Opening, at rank 1 (0.04 = +4%/opening)")),
                ("class_ability_killerinstinct_critdamage_step", new Property<double>(0.03, "class abilities: Killer Instinct - critical damage per Opening added per rank above 1 (4/7/10% per opening at ranks 1-3, so up to +30% at rank 3 with 3 stacked Openings). Higher Sneak Attack multiplies the critical damage")),
                ("class_ability_killerinstinct_critchance_per_stack", new Property<double>(0.02, "class abilities: Killer Instinct - flat critical chance added per held Opening, rank-invariant (0.02 = +2%/opening, so +6% with 3 stacked Openings)")),
                ("class_ability_killerinstinct_opening_duration_seconds", new Property<double>(8.0, "class abilities: Killer Instinct - seconds one Opening lasts before it expires")),
                // Rallying Presence (Vanguard T1): fellows/pets within 15m take 1/2/3/4/5% less damage by rank,
                // the caster gets half that. Damage reduction is a SHARED ADDITIVE AXIS (Mana Barrier, Battle
                // Hardened) - the mechanic slice owes the cross-class ceiling check; no cap number is signed
                // off yet, so none is registered here
                ("class_ability_rallyingpresence_percent_per_rank", new Property<double>(0.01, "class abilities: Rallying Presence - damage reduction granted to fellows and combat pets in range, per rank (0.01 = -1%/rank, so -5% at rank 5). Higher Assess Person multiplies the reduction")),
                ("class_ability_rallyingpresence_self_share", new Property<double>(0.50, "class abilities: Rallying Presence - fraction of the ally reduction the caster receives for themselves (0.50 = half). Deliberate asymmetry: worth more to a grouped Vanguard than a solo one")),
                ("class_ability_rallyingpresence_radius", new Property<double>(15.0, "class abilities: Rallying Presence - radius in metres within which fellows and combat pets receive the reduction")),
                // Kinetic Charge (Vanguard T2): every attack that reaches the Vanguard builds a charge; at 5
                // charges the next attack spends them all for +20/40/60% damage by rank; charges fade after 10s
                ("class_ability_kineticcharge_percent_per_rank", new Property<double>(0.20, "class abilities: Kinetic Charge - damage bonus on the attack that spends a full stack of charges, per rank (0.20 = +20%/rank, so +60% at rank 3). Higher Armor Tinkering multiplies the bonus")),
                ("class_ability_kineticcharge_expire_seconds", new Property<double>(10.0, "class abilities: Kinetic Charge - seconds without being hit before accumulated charges fade")),
                // Reflect (Vanguard T3, enum/token ReflectMagic - displays as "Reflect", see ReflectMagicAbility's
                // doc comment for the deliberate name asymmetry): 6/12/18% chance to bounce an incoming
                // PROJECTILE back at its firer. Projectiles only
                ("class_ability_reflectmagic_chance_base", new Property<double>(0.06, "class abilities: Reflect - chance an incoming projectile bounces back at its firer, at rank 1 (0.06 = 6%). Projectiles only - melee, non-projectile spells, and DoT ticks are untouched")),
                ("class_ability_reflectmagic_chance_step", new Property<double>(0.06, "class abilities: Reflect - chance added per rank above 1 (6/12/18% at ranks 1-3). Higher Missile Defense multiplies the chance, capped by class_ability_affinity_chance_cap")),
                // Cloaked in Power (Vanguard T2, added 2026-09-29): a FLOOR under the wearer's cloak
                // spell-proc chance. Single rank, so this tunable is the whole magnitude
                ("class_ability_cloakedinpower_floor", new Property<double>(0.10, "class abilities: Cloaked in Power - minimum chance the wearer's equipped cloak fires its spell proc on a hit that reached them, as a fraction (0.10 = 10%). Applied in Cloak.RollProc as an extra term inside the existing min-proc Math.Max, so it can never LOWER a chance already above it, and strictly AFTER the 5 second per-cloak cooldown and the ItemLevel >= 1 refusal, so it bypasses neither. Higher Armor Tinkering multiplies the floor, capped by class_ability_affinity_chance_cap. Does not apply to the damage-reduction (CloakWeaveProc = 2) roll, which keeps cloak_min_proc")),
                // Adrenaline (Berserker T1): taking damage arms the next attack or spell within 6s for +2/4/6/8/10%
                ("class_ability_adrenaline_percent_per_rank", new Property<double>(0.02, "class abilities: Adrenaline - damage bonus on the next attack or spell after taking damage, per rank (0.02 = +2%/rank, so +10% at rank 5). Higher Recklessness multiplies the bonus")),
                ("class_ability_adrenaline_window_seconds", new Property<double>(6.0, "class abilities: Adrenaline - seconds after taking damage during which the next attack or spell gets the bonus")),
                // Vengeance (Berserker T2): +4/8/12% damage vs enemies that hit you in the last 15s
                ("class_ability_vengeance_percent_per_rank", new Property<double>(0.04, "class abilities: Vengeance - damage bonus dealt to an enemy that damaged you within the window, per rank (0.04 = +4%/rank, so +12% at rank 3). Higher Recklessness multiplies the bonus")),
                ("class_ability_vengeance_window_seconds", new Property<double>(15.0, "class abilities: Vengeance - seconds since that enemy last damaged you during which the bonus applies. Refreshes on every further hit it lands on you")),
                // Quickened Casting (Archmage T1): buff/debuff spells of every school cast 20/40/60/80/100%
                // faster by rank; damaging spells unaffected. NO AFFINITY AT ALL - the one entry in this wave
                // with none; see QuickenedCastingAbility's doc comment
                ("class_ability_quickenedcasting_percent_per_rank", new Property<double>(0.20, "class abilities: Quickened Casting - cast-speed increase for buff and debuff spells of every school, per rank (0.20 = +20%/rank, so +100% at rank 5). Damaging spells are unaffected. No affinity")),
                // Umbral Siphon (Void/Summon T1): a combat pet heals for 2/4/6/8/10% of the damage it deals
                ("class_ability_umbralsiphon_percent_per_rank", new Property<double>(0.02, "class abilities: Umbral Siphon - fraction of a combat pet's dealt damage healed back to that same pet, per rank (0.02 = +2%/rank, so +10% at rank 5). The heal goes to the pet, never the caster. Higher Loyalty multiplies the leech")),
                // Soul Jump (Void/Summon T3): the pet dies in the caster's place, restoring 10% max health, once
                // every 6/4/2 minutes by rank. AFFINITY DIRECTION IS UNSETTLED (Jump, non-standard rate) - see
                // SoulJumpAbility's doc comment. Do not register a rate pair or apply the shared/per-ability
                // rate to any of these three until that ruling lands
                ("class_ability_souljump_cooldown_seconds_base", new Property<double>(360.0, "class abilities: Soul Jump - seconds between saves at rank 1 (360 = 6 minutes)")),
                ("class_ability_souljump_cooldown_seconds_step", new Property<double>(-120.0, "class abilities: Soul Jump - seconds added per rank above 1, NEGATIVE because higher rank shortens the cooldown (360/240/120 seconds = 6/4/2 minutes at ranks 1-3)")),
                ("class_ability_souljump_restore_fraction", new Property<double>(0.10, "class abilities: Soul Jump - fraction of maximum health restored when the pet dies in the caster's place. Flat; does not scale with rank")),
                // Hemomancy (Blood Mage T1): DoT ticks 2/4/6/8/10% harder by rank, each tick heals 1% of its own
                // damage, Drain spells drain 2/4/6/8/10% more by rank
                ("class_ability_hemomancy_tick_percent_per_rank", new Property<double>(0.02, "class abilities: Hemomancy - damage-over-time tick strength bonus, per rank (0.02 = +2%/rank, so +10% at rank 5). Higher Healing multiplies the bonus")),
                ("class_ability_hemomancy_heal_fraction", new Property<double>(0.01, "class abilities: Hemomancy - fraction of a DoT tick's own damage healed back to the caster (0.01 = 1%). Flat; does not scale with rank")),
                ("class_ability_hemomancy_drain_percent_per_rank", new Property<double>(0.02, "class abilities: Hemomancy - Drain spell damage bonus, per rank (0.02 = +2%/rank, so +10% at rank 5). Higher Healing multiplies the bonus")),
                // Spellweave (Spellsword T1): casting any spell (including a proc) arms the next weapon hit for
                // +3/6/9/12/15% by rank, and a landed weapon hit arms the next spell for the same
                ("class_ability_spellweave_percent_per_rank", new Property<double>(0.03, "class abilities: Spellweave - damage bonus on the next weapon hit after casting any spell (including a proc), and on the next spell after landing a weapon hit, per rank (0.03 = +3%/rank, so +15% at rank 5). Higher Item Enchantment multiplies the bonus")),
                // Runic Ward (Spellsword T2): each landed weapon hit adds 3/5/7% of that hit's damage to a ward
                // by rank, capped at 15% of max health, lapsing after 12s. A landed war spell spends the ward
                ("class_ability_runicward_gain_base", new Property<double>(0.03, "class abilities: Runic Ward - fraction of a landed weapon hit's damage added to the ward, at rank 1 (0.03 = 3%)")),
                ("class_ability_runicward_gain_step", new Property<double>(0.02, "class abilities: Runic Ward - fraction added per rank above 1 (3/5/7% at ranks 1-3). Higher Item Tinkering multiplies the ward")),
                ("class_ability_runicward_cap_fraction", new Property<double>(0.15, "class abilities: Runic Ward - hard cap on the ward pool, as a fraction of the Spellsword's maximum health (0.15 = 15%). Flat; does not scale with rank")),
                ("class_ability_runicward_duration_seconds", new Property<double>(12.0, "class abilities: Runic Ward - seconds the ward persists without a further landed weapon hit before it lapses unused. Landing a war spell spends the entire ward into that spell's damage rather than waiting for this timer")),

                // Monster combat effects: per-effect MAGNITUDES are authored on the monster weenie
                // (PropertyString 9015 MonsterCombatEffects), because they vary per monster. The tunables
                // below are the CEILINGS that bound whatever any weenie asks for, so the repo owner can move
                // the whole layer live without touching content. Accessors: MonsterEffects.MonsterEffectCaps.
                ("monster_effect_avoidance_cap", new Property<double>(0.50, "monster effects: maximum total chance a monster may avoid an incoming attack through this layer (0.50 = 50%), before its normal evade roll")),
                ("monster_effect_proc_chance_cap", new Property<double>(0.75, "monster effects: maximum chance for any one on-hit proc after every ramp and bonus (0.75 = 75%)")),
                ("monster_effect_leech_cap", new Property<double>(0.50, "monster effects: maximum fraction of the damage a monster deals that it may convert back into its own vitals (0.50 = 50%)")),
                ("monster_effect_reflect_cooldown", new Property<double>(5.0, "monster effects: minimum seconds between two reflects from the same reflect record on one monster (on=hit and on=avoid share the timer), across all attackers; 0 or less disables the cooldown")),
                ("monster_effect_reflect_per_level", new Property<double>(0.25, "monster effects: reflect on=hit damage per level of the reflecting monster (0.25 = a level 220 monster reflects 55 per hit), before the record's optional mult= and monster_effect_damage_rider_cap. A magnitude rate, not a ceiling - the only reflect number that is not authored on the weenie")),
                ("monster_effect_reflect_max_range", new Property<double>(5.0, "monster effects: reflect on=hit fires only when the attacker is within this many metres of the monster when the hit lands (edge-to-edge cylinder distance, the melee reach measure); DoT ticks, procs and splash never reflect at any range")),
                ("monster_effect_speed_cap", new Property<double>(2.00, "monster effects: maximum attack- or cast-speed multiplier from this layer (2.00 = twice as fast), after every speed and ramp record composes")),
                ("monster_effect_damage_rider_cap", new Property<double>(10000.0, "monster effects: maximum extra damage one flat rider may add to a single hit. High on purpose - it is a runaway guard, not a balance dial; the balance dial is the amount= arg on the weenie")),
                ("monster_effect_manabarrier_cap", new Property<double>(0.60, "monster effects: maximum fraction of incoming damage a manabarrier effect may divert to its own Mana (0.60 = 60%), mirroring class_ability_manabarrier_max_share")),
                ("monster_effect_ward_cap", new Property<double>(1.0, "monster effects: maximum ward grant a ward effect may author, as a multiple of the carrying monster's own maximum health (1.0 = a ward may not exceed 100% of max health)")),
                ("monster_effect_debuff_cap", new Property<double>(500.0, "monster effects: maximum magnitude a debuff effect may apply on its POINT-valued shapes - what=imperil (armor points removed) and what=attackskills (skill points removed). High on purpose - a runaway guard, not a balance dial (the dial is mag= on the weenie); for scale, the player-side equivalent class_ability_pocketsand_magnitude is 40.0. The FRACTION-valued shape what=vuln has its own cap, because the two are not the same unit")),
                ("monster_effect_debuff_vuln_cap", new Property<double>(1.0, "monster effects: maximum magnitude a debuff effect may apply on what=vuln, which is a FRACTION added to a resist multiplier rather than a point count - 1.0 means an authored mag= may not push the victim past taking 2.00x that element. Separate from monster_effect_debuff_cap because one number cannot bound both units: a ceiling loose enough for armor points would let a mag=50 typo through as a 51x resist multiplier")),
                // The three COUNT caps of this block - monster_effect_ramp_stack_cap,
                // monster_effect_max_per_monster and monster_effect_recast_cap - are longs and live in
                // DefaultLongProperties above.
                // Equipment mods: per-mod maximum magnitudes live in EquipmentModRegistry as constants; an item
                // stores only a potency scalar 0-1, so this tunable moves the whole layer at once.
                // equipment_mod_lowtier_potency was removed with the TigerEye low-tier path - every potency is
                // a roll now, floored by the mod's own MinPotency (EquipmentModRoller.RollPotency)
                ("equipment_mod_potency_scale", new Property<double>(1.0, "equipment mods: global multiplier on every mod's resolved magnitude (applied = stored potency x registry max x this). 1.0 = registry values as designed. Dials the entire power layer live, with no item or schema impact")),
                // Weapon mods: per-modifier maximum rolls live in WeaponModRegistry as constants. The three
                // special_chance values are CUMULATIVE - P(at least one), P(at least two), P(at least three),
                // P(all four) - not conditional gates, and must decrease monotonically. Defaults give none 65%,
                // one 25%, two 8%, three 2%, four 0%.
                //
                // _4 ARRIVED WITH THE CAP RAISE TO 4 ON 2026-08-06 AND DEFAULTS TO 0 ON PURPOSE. That change was
                // structural, not a tuning pass: the mechanism for a fourth special exists but is unreachable
                // until this is set, so live behaviour is unchanged by the raise alone.
                ("weapon_mod_special_chance_1", new Property<double>(1.00, "weapon mods: P(a Tourmaline reroll produces AT LEAST ONE special modifier). Cumulative, so it must be >= weapon_mod_special_chance_2. RETUNED FROM 0.35 TO 1.00 on 2026-08-06 as part of the v3 magnitude pass")),
                ("weapon_mod_special_chance_2", new Property<double>(0.60, "weapon mods: P(a Tourmaline reroll produces AT LEAST TWO special modifiers). Cumulative, so it must sit between weapon_mod_special_chance_1 and weapon_mod_special_chance_3. RETUNED FROM 0.10 TO 0.60 on 2026-08-06 as part of the v3 magnitude pass")),
                ("weapon_mod_special_chance_3", new Property<double>(0.30, "weapon mods: P(a Tourmaline reroll produces AT LEAST THREE special modifiers). Cumulative, so it must sit between weapon_mod_special_chance_2 and weapon_mod_special_chance_4. RETUNED FROM 0.02 TO 0.30 on 2026-08-06 as part of the v3 magnitude pass")),
                ("weapon_mod_special_chance_4", new Property<double>(0.10, "weapon mods: P(a Tourmaline reroll produces ALL FOUR special modifiers). Cumulative, and four is a permanent per-weapon bound - raising this can never produce a fifth. RETUNED FROM 0.00 TO 0.10 on 2026-08-06 as part of the v3 magnitude pass, which is also what first makes the fourth band reachable")),
                ("weapon_mod_magnitude_scale", new Property<double>(1.0, "weapon mods: global multiplier on a special modifier's magnitude, across every row (magnitude = max roll x potency x workmanship/10 x this). RETROACTIVE FOR TIER B, NOT FOR TIER A: a Tier B row stores the roll fraction and re-reads this on every combat read, so changing it immediately retunes Tier B modifiers already on existing weapons; a Tier A row (Devastation, Weak Point, Bloodthirst, Shield Bypass, Swift Flight) stored its magnitude at roll time and is unaffected until rerolled. 0 mutes the layer for Tier B outright. Per-modifier maximums are constants in WeaponModRegistry.cs, not tunables - retuning one is a code change, and it is retroactive on the same terms")),
                ("weapon_mod_swap_special_chance", new Property<double>(0.35, "weapon mods: P(an Amethyst swap adds a special modifier rather than a random class tinker). Improves the RATE only - the identity that arrives is never chosen")),
                ("minor_cantrip_drop_rate", new Property<double>(1.0, "Scales the chance for minor cantrips to drop, relative to other cantrip levels in the tier. Defaults to 1.0, as per end of retail")),
                ("major_cantrip_drop_rate", new Property<double>(1.0, "Scales the chance for major cantrips to drop, relative to other cantrip levels in the tier. Defaults to 1.0, as per end of retail")),
                ("epic_cantrip_drop_rate", new Property<double>(1.0, "Scales the chance for epic cantrips to drop, relative to other cantrip levels in the tier. Defaults to 1.0, as per end of retail")),
                ("legendary_cantrip_drop_rate", new Property<double>(1.0, "Scales the chance for legendary cantrips to drop, relative to other cantrip levels in the tier. Defaults to 1.0, as per end of retail")),

                ("advocate_fane_auto_bestow_level", new Property<double>(1, "the level that advocates are automatically bestowed by Advocate Fane if advocate_fane_auto_bestow is true")),
                ("aetheria_drop_rate", new Property<double>(1.0, "Modifier for Aetheria drop rate, 1 being normal")),
                ("chess_ai_start_time", new Property<double>(-1.0, "the number of seconds for the chess ai to start. defaults to -1 (disabled)")),
                ("encounter_delay", new Property<double>(1800, "the number of seconds a generator profile for regions is delayed from returning to free slots")),
                ("encounter_regen_interval", new Property<double>(600, "the number of seconds a generator for regions at which spawns its next set of objects")),
                ("fast_missile_modifier", new Property<double>(1.2, "The speed multiplier applied to fast missiles. Defaults to retail value of 1.2")),
                ("ignore_magic_armor_pvp_scalar", new Property<double>(1.0, "Scales the effectiveness of IgnoreMagicArmor (ie. hollow weapons) in pvp battles. 1.0 = full effectiveness / ignore all enchantments on armor (default), 0.5 = half effectiveness / use half enchantments from armor, 0.0 = no effectiveness / use full enchantments from armor")),
                ("ignore_magic_resist_pvp_scalar", new Property<double>(1.0, "Scales the effectiveness of IgnoreMagicResist (ie. hollow weapons) in pvp battles. 1.0 = full effectiveness / ignore all resistances from life enchantments (default), 0.5 = half effectiveness / use half resistances from life enchantments, 0.0 = no effectiveness / use full resistances from life enchantments")),
                ("fellowship_vital_update_interval", new Property<double>(1.0, "WaffleACE: seconds between fellowship-panel vital update flushes. Vitals traffic is O(n^2) in fellowship size (each changed member x each member with the panel open), so this bounds it to a fixed rate instead of the 60Hz world tick. Raise toward 5.0 to trade panel freshness for bandwidth; clamped to a 0.1s floor.")),
                ("fellowship_share_group_plateau", new Property<double>(2.7,"WaffleACE: total group XP multiplier for fellowships larger than the retail table (9). Per-member EvenShare = plateau / member count, so total group XP stays flat while each head's share keeps falling. 2.7 (= 9 * 0.3) is continuous with retail at 9, so fellowships of 9 or fewer are unaffected. Raise it to make big fellowships out-earn small ones.")),
                ("luminance_modifier", new Property<double>(1.0, "Scales the amount of luminance received by players")),
                ("offline_bonus_multiplier", new Property<double>(1.0, "Offline bonus: fractional boost added to a character's own combat XP/Luminance, including the item XP from their own kills, while they have banked offline bonus time. 1.0 = +100% (double). Does not apply to quest, allegiance passup, or the fellowship-shared portion of another fellow's kill.")),
                ("offline_bonus_idle_accrual_rate", new Property<double>(1.0, "Offline bonus: rate the bank accrues, per idle second, while a character is online but idle (no qualifying combat XP/Luminance for longer than offline_bonus_idle_timeout_seconds). 1.0 = accrues at the same 1:1 rate as being logged out, still capped by offline_bonus_max_seconds.")),
                ("alt_character_bonus_multiplier", new Property<double>(1.0, "Alt character bonus: fractional boost added to a character's leveling XP (kills, quest turn-ins, and its share of a fellow's kill) while it is below the highest enlightenment+level character on its account. 1.0 = +100% (double). Multiplicative with the offline bonus. Does not apply to allegiance passup or item XP.")),
                ("melee_max_angle", new Property<double>(0.0, "for melee players, the maximum angle before a TurnTo is required. retail appeared to have required a TurnTo even for the smallest of angle offsets.")),
                ("mob_awareness_range", new Property<double>(1.0, "Scales the distance the monsters become alerted and aggro the players")),
                ("mule_form_max_effect_scale", new Property<double>(8.0, "WaffleACE: the largest particle-emitter scale (StartScale/FinalScale in the donor's baked DefaultScript) a creature may bake before the Beast Effigy refuses to attune to it. Particle effects are absolute dat data and do NOT scale with the object like the mesh does - verified in game 2026-09-07, a captured Shadow Vortex (scale 30) rendered a full-height cloud around a correctly mesh-scaled vendor - so an oversized donor cannot be fixed server-side and must be refused at attunement instead. A scan of every creature Setup that bakes a script found most under 8 and a long thin tail above it (up to 30), so this is a threshold rather than an all-or-nothing ban on baked scripts. Raising it re-permits larger donors; setting it very high effectively disables the gate. Gates ATTUNEMENT ONLY - forms already earned before this shipped keep rendering, by design")),
                ("pickup_animation_speed", new Property<double>(1.0, "WaffleACE: playback speed multiplier for the player pick-up animation (reach and return). 1.0 = retail")),
                ("pickup_speed_quest_bonus", new Property<double>(0.5, "WaffleACE: additive pick-up animation speed bonus per permanent quest boon a character has claimed (see PickupSpeed.Compute). 0.5 = +50% per boon")),
                ("pickup_animation_speed_max", new Property<double>(3.0, "WaffleACE: cap on the composed pick-up animation speed (base * quest boons), clamped to [1.0, 10.0]")),
                ("player_turnto_speed_per_quickness", new Property<double>(0.004, "WaffleACE: turn speed bonus per point of BUFFED Quickness for server-initiated player turns (auto-face before a cast, attack or use). 0.004 = +0.4% per point, so ~500 Quickness reaches the default cap. Manual turning is client-side and unaffected. 0 = retail. Clamped to [0, 1] at read")),
                ("player_turnto_speed_bonus_max", new Property<double>(2.0, "WaffleACE: cap on the Quickness turn speed bonus, as a fraction added to 1.0. 2.0 = +200%, a 3.0x turn. 0 = retail. Clamped to [0, 9] at read")),
                ("custom_aug_pickup_speed_bonus", new Property<double>(0.10, "WaffleACE: additive pick-up animation speed bonus per Custom Dreamweave pick-up augmentation a character holds (PropertyInt.AugmentationPickupSpeed, bought from Bo). 0.10 = +10% each. Added to the quest-boon term inside the SAME pickup_animation_speed_max clamp, so with the shipped defaults four quest boons alone already reach the cap and further augmentations are inert until pickup_animation_speed_max is raised - /pickupspeed says so when it happens")),
                ("custom_aug_spell_duration_bonus", new Property<double>(0.10, "WaffleACE: additive spell duration bonus per Custom Dreamweave spell duration augmentation a character holds (PropertyInt.AugmentationSpellDurationCustom, bought from Fi). 0.10 = +10% each. Added to the retail AugmentationIncreasedSpellDuration term rather than multiplied by it, because WeaponModId.Longevity already multiplies a second factor at the same three sites")),
                ("pk_new_character_grace_period", new Property<double>(300, "the number of seconds, in addition to pk_respite_timer, that a player killer is set to non-player killer status after first exiting training academy")),
                ("pk_respite_timer", new Property<double>(300, "the number of seconds that a player killer is set to non-player killer status after dying to another player killer")),
                ("pvp_damage_cap_max_health_fraction", new Property<double>(0.5, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): per-hit ceiling on player-vs-player damage as a fraction of the DEFENDER's max health - 0.5 means no single hit takes more than half their health bar. Same choke points as pvp_damage_cap, and the smaller of the two wins. 0 = off")),
                ("pvp_damage_rating_scale", new Property<double>(1.0, "PvP rules: multiplier on the summed damage rating and damage resist rating of a player-vs-player direct hit (melee, missile, spell projectile), applied before the rating becomes a modifier, at choke points R1/R2. PK damage ratings are not scaled. 1.0 = unchanged")),
                ("pvp_crit_damage_rating_scale", new Property<double>(1.0, "PvP rules: multiplier on the crit damage rating and crit damage resist rating of a player-vs-player critical hit, applied before the rating becomes a modifier, at choke points R1/R2. 1.0 = unchanged")),
                ("pvp_cloak_damage_reduction", new Property<double>(100.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): flat amount of damage a cloak proc mitigates on a player-vs-player hit, at choke point CLK1. Ported from Doctide `pvp_cloak_max_dmg_mitigation`. Defaults 100, reproducing the fork's prior hardcoded PvP halving of the 200-point base")),
                ("pvp_war_magic_damage_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on a player-vs-player WAR magic projectile hit (bolts, streaks, arcs, blasts, volleys; never void or life projectiles - void_pvp_modifier covers void), applied before the PvP damage cap at choke point M2. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_melee_damage_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on a player-vs-player MELEE hit, applied before the PvP damage cap at choke point M1. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_missile_damage_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on a player-vs-player MISSILE hit (bow, crossbow, atlatl, thrown), applied before the PvP damage cap at choke point M1. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_crit_damage_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on the WHOLE of a player-vs-player critical hit (melee, missile, and war/void/life projectile crits), applied before the PvP damage cap at choke points M1/M2. Distinct from pvp_crit_damage_rating_scale, which scales rating terms only. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_magic_absorb_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on the magic absorb REDUCTION fraction (shield, launcher or wand AbsorbMagicDamage) on a player-vs-player projectile hit, after the retail PvP 0.72, at choke point AB1: absorb = 1 - (1 - absorb) x mod, clamped to [0, 1]. 0 = absorption does nothing, 2 = twice the fraction. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_healing_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): Arena only: scales Health heals received by a player in a Live arena match, at choke points HL1-HL5F: heal spells and heal gems, Health healing kits (after the arena 1v1 kit caps), Health food and potions, heal-over-time ticks (including Aetheria), and each recipient's share of a Drain or Transfer's Health gain (never the drained source's loss). Open-world PK heals are never scaled, even with a PK timer running. Natural regen, class-ability and weapon-mod heals, Stamina and Mana are not scaled. A scaled heal is floored to a whole point. 0 = heals land for 0. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_cs_crit_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on the Critical Strike imbue's crit CHANCE bonus above the unimbued baseline on a player-vs-player hit, physical (CS1) and magic (CS2): base + (cs - base) x mod. 0 = the imbue adds nothing. The retail PvP halving of magic Critical Strike still applies first. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_cb_crit_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on the Crippling Blow imbue's crit-damage multiplier above 1.0 on a player-vs-player hit, at choke point CB1: 1 + (cb - 1) x mod. Crippling Blow imbue only, never the tinkered CriticalMultiplier (Crushing Blow). 0 = the imbue adds nothing. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_melee_defense_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on a player-vs-player DEFENDER's effective melee defense skill in the evade roll only (choke point DF1); 0.5 halves it, so the defender evades less. Nothing else reads the scaled value. PvE is never affected. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_missile_defense_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on a player-vs-player DEFENDER's effective missile defense skill in the evade roll only (choke point DF1); 0.5 halves it, so the defender evades less. Nothing else reads the scaled value. PvE is never affected. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_magic_defense_mod", new Property<double>(1.0, "PvP rules (Docs/Pvp/DESIGN.md \"PvP rules (levers)\"): multiplier on a player-vs-player TARGET's effective magic defense in the spell resist roll only (choke point DF2); 0.5 halves it, so the target resists less. Nothing else reads the scaled value. PvE is never affected. NaN, infinite or negative = unchanged. 1.0 = unchanged")),
                ("pvp_arena_dmg_mod_1v1", new Property<double>(1.0, "PvP Arena (Docs/Pvp/DESIGN.md \"Tunables\"): multiplies melee, missile, war and void projectile damage between two players bound to the same Live 1v1 match, applied before the PvP rules damage cap. Ported from Doctide `arenas_dmg_mod_1v1`. Defaults 1.0 (unchanged)")),
                ("pvp_arena_dmg_mod_ffa", new Property<double>(1.0, "PvP Arena (Docs/Pvp/DESIGN.md \"Tunables\"): the Tugak Brawl counterpart of pvp_arena_dmg_mod_1v1: multiplies melee, missile, war and void projectile damage between two players bound to the same Live Tugak Brawl match, applied before the PvP rules damage cap, at the same choke point (AM1). NaN, infinite or negative = unchanged. Defaults 1.0 (unchanged)")),
                ("pvp_arena_healkit_restoration_cap_1v1", new Property<double>(1.5, "PvP Arena (Docs/Pvp/DESIGN.md \"Tunables\"): the healing kit restoration-bonus multiplier cap applied to a healer or target bound to the same Live 1v1 match. Ported from Doctide `arena_1v1_healkit_restoration_bonus_cap`. Defaults 1.5")),
                ("pvp_arena_overtime_healing_mod", new Property<double>(0.0, "PvP Arena (Docs/Pvp/DESIGN.md \"Overtime\"): multiplier on Health heals received by a player in arena overtime, at every pvp_healing_mod site (HL1-HL5), on top of pvp_healing_mod. 0 = no healing: heal spells land for 0, and Health kits and Health potions are refused with the item kept. Snapshotted onto the match when overtime starts. NaN, infinite or negative = unchanged. Defaults 0.0")),
                ("pvp_arena_overtime_damage_ramp_per_minute", new Property<double>(0.0, "PvP Arena (Docs/Pvp/DESIGN.md \"Overtime\"): damage between two players in the same arena match during overtime is multiplied by 1 + ramp x (whole minutes of overtime elapsed), after the PvP damage cap (C1-C4) and before the cloak proc. Damage-over-time ticks and open-world PvP never ramp. Snapshotted onto the match when overtime starts. 0 = off. NaN, infinite or negative = off. Defaults 0.0")),
                ("pvp_bg_koth_zone_radius", new Property<double>(6.0, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): King of the Hill control zone radius in metres, measured horizontally from the zone centre. Defaults 6.0")),
                ("pvp_bg_koth_zone_height", new Property<double>(3.0, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Settings\"): King of the Hill control zone height tolerance in metres above or below the zone centre. Defaults 3.0")),
                ("pvp_bg_koth_marker_spacing", new Property<double>(2.5, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Zone markers\"): target metres between neighbouring King of the Hill zone markers. The ring holds ceil(2 x pi x radius / spacing) markers, never fewer than 8 or more than 32. Values below 0.5 are raised to 0.5. Read when a match forms. Defaults 2.5")),
                ("pvp_bg_koth_marker_z_offset", new Property<double>(-0.15, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Zone markers\"): metres added to the height of every King of the Hill zone marker (negative sinks the wisp toward the floor). The marker weenie is scaled 0.35, which hangs its glow about 0.56 m above the marker origin, so a negative value lowers it. Clamped to -0.15..5: below about -0.16 the marker's collision sphere leaves the floor cell and the server refuses to place it. Read when a match forms (and by /arenaadmin testspace). Defaults -0.15")),
                ("pvp_arena_ffa_ring_dmg", new Property<double>(1.0, "PvP Arena (Docs/Pvp/DESIGN.md \"Tunables\"): Tugak Brawl's own ring scaler. Multiplies the damage of ring-shape spell projectiles (a 360-degree ring, any school) between two players bound to the same Live Tugak Brawl match, applied before the PvP rules damage cap. Today the only ring spell Tugak allows is Curse of Raven Fury (the Blood Mage life ring), so this is the dial that scales Curse of Raven Fury in Tugak - the pvp_arena_war_ring_dmg context keys only know War Magic and never reach it. Does not touch bolts, 1v1, 2v2, battlegrounds, open-world PvP or PvE. NaN, infinite or negative = unchanged. Defaults 1.0 (unchanged)")),
                ("pvp_bg_dmg_mod", new Property<double>(1.0, "PvP Battlegrounds (Docs/Pvp/DESIGN.md \"Tunables\"): the battleground counterpart of pvp_arena_dmg_mod_ffa: multiplies melee, missile, war and void projectile damage between two players bound to the same Live battleground match (every battleground mode: King of the Hill, Attack/Defend and any later one), applied before the PvP rules damage cap, at the same choke point (AM1). Read on every hit. Never applies to a crystal, an arena match, open-world PvP or PvE. NaN, infinite or negative = unchanged. Defaults 1.0 (unchanged)")),
                ("pvp_bg_marks_scale", new Property<double>(1.0, "PvP Battlegrounds (Docs/Pvp/BATTLEGROUNDS.md \"Rewards\"): multiplies the pvp_bg_marks_win / _loss / _draw amounts: paid = max(0, round(base x scale)), halves rounding away from zero (a base of 0 or less stays 0). NaN or infinite = 1.0 (unchanged); negative = 0 (pays nothing). Read when the match resolves. Defaults 1.0")),
                ("pvp_bg_ad_kill_chip_pct", new Property<double>(1.0, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Kill chip and heal\"): when an attacker kills a defender in a Live Attack/Defend match, and the defender died within pvp_bg_ad_kill_range metres of the vulnerable crystal, that crystal loses this percent of its MAXIMUM health (through the normal crystal damage path, so a chip to 0 destroys it). Snapshotted when a match forms. 0, NaN, infinite or negative = off. Values above 100 count as 100. Defaults 1.0")),
                ("pvp_bg_ad_kill_heal_pct", new Property<double>(0.5, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Kill chip and heal\"): when a defender kills an attacker in a Live Attack/Defend match, and the attacker died within pvp_bg_ad_kill_range metres of the vulnerable crystal, that crystal regains this percent of its MAXIMUM health, never above its maximum and never on a destroyed crystal. Snapshotted when a match forms. 0, NaN, infinite or negative = off. Values above 100 count as 100. Defaults 0.5")),
                ("pvp_bg_ad_kill_range", new Property<double>(25.0, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Kill chip and heal\"): a kill chips or heals the vulnerable crystal only when the VICTIM died within this many metres (3D, landblock frame) of it. Snapshotted when a match forms. 0, NaN, infinite or negative = no kill counts. Defaults 25.0")),
                ("pvp_bg_ad_defender_dr_per", new Property<double>(0.125, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Engaged defenders\"): in a Live Attack/Defend match, each ENGAGED defender reduces an attacker's damage to the crystal by this fraction, up to pvp_bg_ad_defender_dr_cap. Engaged = alive, on the defending team, within pvp_bg_ad_defender_dr_radius metres of that crystal, and dealt or took damage against an opposing player of the match within the last pvp_bg_ad_defender_dr_window_s seconds. Never reduces the kill chip. Snapshotted when a match forms. 0, NaN, infinite or negative = off. Defaults 0.125 (12.5%)")),
                ("pvp_bg_ad_defender_dr_cap", new Property<double>(0.5, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Engaged defenders\"): the most the engaged-defender reduction can take off an attacker's hit on a crystal, however many defenders are engaged. Snapshotted when a match forms. 0, NaN, infinite or negative = off; above 0.95 counts as 0.95, so a hit always keeps at least 5%. Defaults 0.5 (50%)")),
                ("pvp_bg_ad_defender_dr_radius", new Property<double>(5.0, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Engaged defenders\"): a defender counts toward the engaged-defender reduction only within this many metres (3D, landblock frame, centre to centre) of the crystal being hit. Snapshotted when a match forms. 0, NaN, infinite or negative = no defender counts. Defaults 5.0")),
                ("pvp_bg_ad_defender_dr_window_s", new Property<double>(10.0, "PvP Battlegrounds (Docs/Pvp/ATTACK-DEFEND.md \"Engaged defenders\"): a defender counts toward the engaged-defender reduction only when they dealt or took damage against an opposing player of the match within this many seconds. Damage to or from a crystal or an NPC does not count. Snapshotted when a match forms. 0, NaN, infinite or negative = no defender counts. Defaults 10")),
                ("quest_lum_modifier", new Property<double>(1.0, "Scale multiplier for amount of quest luminance received by players.  Quest lum is also modified by 'luminance_modifier'.")),
                ("quest_mindelta_rate", new Property<double>(1.0, "scales all quest min delta time between solves, 1 being normal")),
                ("quest_xp_modifier", new Property<double>(1.0, "Scale multiplier for amount of quest XP received by players.  Quest XP is also modified by 'xp_modifier'.")),
                ("rare_drop_rate_percent", new Property<double>(0.04, "Adjust the chance of a rare to spawn as a percentage. Default is 0.04, or 1 in 2,500. Max is 100, or every eligible drop.")),
                ("spellcast_max_angle", new Property<double>(20.0, "for advanced player spell casting, the maximum angle to target release a spell projectile. retail seemed to default to value of around 20, although some players seem to prefer a higher 45 degree angle")),
                ("trophy_drop_rate", new Property<double>(1.0, "Modifier for trophies dropped on creature death")),
                ("unlocker_window", new Property<double>(10.0, "The number of seconds a player unlocking a chest has exclusive access to first opening the chest.")),
                ("vendor_unique_rot_time", new Property<double>(300, "the number of seconds before unique items sold to vendors disappear")),
                ("vitae_penalty", new Property<double>(0.05, "the amount of vitae penalty a player gets per death")),
                ("vitae_penalty_max", new Property<double>(0.40, "the maximum vitae penalty a player can have")),
                ("void_pvp_modifier", new Property<double>(0.5, "Scales the amount of damage players take from Void Magic. Defaults to 0.5, as per retail. For earlier content where DRR isn't as readily available, this can be adjusted for balance.")),
                ("dynamic_dungeons_xp_mult_cap", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultXpCap, "(non-retail function) Threads: ceiling on the product of a gem's XP modifiers. Does NOT bound dynamic_dungeons_xp_scale, which is applied outside the product")),
                ("dynamic_dungeons_xp_scale", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultXpScale, "(non-retail function) Threads: how many times what killing the same creature outside a dungeon pays a run kill is worth. Applied OUTSIDE the modifier product, so dynamic_dungeons_xp_mult_cap cannot clip it. 0 disables run XP entirely; a negative or garbled value reads as the default; values above 20 read as 20")),
                ("dynamic_dungeons_lum_scale", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultLumScale, "(non-retail function) Threads: the same rate for luminance, applied outside dynamic_dungeons_lum_mult_cap. 0 disables run luminance entirely; a negative or garbled value reads as the default; values above 20 read as 20")),
                ("dynamic_dungeons_lum_mult_cap", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultLumCap, "(non-retail function) Threads: ceiling on the product of a gem's luminance modifiers")),
                ("dynamic_dungeons_loot_quality_cap", new Property<double>(0.5, "(non-retail function) Threads: ceiling on the run loot profile's LootQualityMod")),
                ("dynamic_dungeons_loot_quantity_cap", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultLootQuantityCap, "(non-retail function) Threads: ceiling on the product of a gem's loot-quantity modifiers; scales the run loot profile's MAXIMUM item counts only, never the minimums. 1.0 disables the axis")),
                ("dynamic_dungeons_modifier_level_exponent", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultModifierLevelExponent, "(non-retail function) Threads: the k of the modifier magnitude curve s = min(1, (gemLevel / 185)^k). Below level 185 every modifier is pulled toward no effect by s (ratings, ignore-shield and hollow x s; health, run speed and monster count 1 + s(m - 1); elite share toward the 0.15 default; each modifier XP/luminance factor 1 + s(f - 1)). 1.76 gives s(50) = 0.10. 185 and above are unchanged. 0 or less turns the curve off; NaN or Infinity reads as the default. Loot quantity, loot quality and salvage affinity are not affected")),
                ("dynamic_dungeons_modifier_reward_scale", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultModifierRewardScale, "(non-retail function) Threads: shrinks ONLY the bonus above neutral of a gem's rolled reward-modifier products - XpMultiplier, BossXpMultiplier, LumMultiplier, LootQuantityMultiplier - via effective = 1 + s * (product - 1), applied AFTER each axis's own cap (dynamic_dungeons_xp_mult_cap / _lum_mult_cap / _loot_quantity_cap). Does NOT touch dynamic_dungeons_xp_scale/_lum_scale, the gem-level reward-scale ratio, salvage-affinity chances, the loot QUALITY bonus, survey rewards, or a modifier's monster-side effect. 1.0 (default) is neutral - a no-op; 0.0 makes every modifier pay exactly retail on these four axes alone. NaN, Infinity or a negative value reads as the default; values above 1.0 read as 1.0")),
                ("dynamic_dungeons_clear_fraction", new Property<double>(0.9, "(non-retail function) Threads: target weighted clear progress (owner ruling R28) a run must reach to count as cleared. The boss alone is worth dynamic_dungeons_boss_clear_weight of that progress; the run's trash shares the rest evenly. 1.0 = every point of progress available, boss and trash both. Clamped to [0.0, 1.0] at read")),
                ("dynamic_dungeons_survey_ratio_exponent", new Property<double>(ACE.Server.ThreadDungeons.SurveyRewardLimits.DefaultExponent, "(non-retail function) Threads: the k of the daily-survey luminance ratio (avgGemLevel / DungeonGemSpec.MaxGemLevel)^k. Higher k punishes low-level farming harder; 1.0 is linear, 0 disables the curve (every ratio reads 1.0). NaN, a negative or a garbled value reads as the default; values above 50 read as 50")),
                ("dynamic_dungeons_survey_ratio_floor", new Property<double>(ACE.Server.ThreadDungeons.SurveyRewardLimits.DefaultFloor, "(non-retail function) Threads: lower bound on the daily-survey reward ratio, so a cleared run at the minimum gem level still pays something. Clamped to [0.0, 1.0]; a NaN or garbled value reads as the default")),
                ("dynamic_dungeons_guide_reward_percent", new Property<double>(1.0, "(non-retail function) Threads: the percent argument of the Thread-Guide clear reward, GrantLevelProportionalXp(this, 0, cap), where cap is the next-level XP of a player AT the rung cleared (ThreadGuideLadder.RewardCap). 1.0 pays min(the player's own next-level XP, the rung's). Paid once per rung, on the clear that advances the ladder; 0 or less pays no XP but the ladder still advances")),
                ("dynamic_dungeons_press_aim_chance", new Property<double>(ACE.Server.ThreadDungeons.PressLimits.DefaultAimChance, "(non-retail function) Threads: how often a Fragment Press op that NAMES a modifier - a taper, a herb, a mapped powder, a targeted talisman - actually lands that modifier. The rest of the time it lands a uniformly random other modifier from the SAME budget class (the other salvage affinities for a powder, the other monster/boss modifiers for a taper). 1.0 restores deterministic aiming; 0.0 makes every aimed component a wildcard. Clamped to [0.0, 1.0] at read; a NaN or garbled value reads as the default")),
                // dynamic_dungeons_press_wild_share and dynamic_dungeons_press_fracture_rate were removed here
                // with the Threads instability mechanic (owner ruling, 2026-09-07). Removing the
                // registration is safe on a shard whose config table still holds a row for either name:
                // Initialize's and DoWork's reload merges (MergeReloadedInPlace) copy every DB row into
                // the cache without checking it against this default table, so an orphaned row is cached
                // harmlessly and simply never read.
                ("dynamic_dungeons_boss_clear_weight", new Property<double>(0.2, "(non-retail function) Threads: share of a run's weighted clear progress (owner ruling R28) that a run's boss is worth on its own when one is present; the remaining share is split evenly across the run's trash. Killing only the boss no longer clears a run outright. Bossless runs renormalize their trash to the full share. Clamped to [0.0, 1.0] at read")),
                ("dynamic_dungeons_boss_health_floor_ratio", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossHealthFloorRatio, "(non-retail function) Threads: multiple of the toughest non-boss creature's health a run boss must reach. A FLOOR, not a multiplier stacked on the gem's modifiers: the boss gets max(what its modifiers give, this ratio times the observed pack maximum). 0 disables the axis; a negative value reads as the default; values above 50 read as 50")),
                ("dynamic_dungeons_trash_health_floor_ratio", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultTrashHealthFloorRatio, "(non-retail function) Threads: multiple of the in-band trash pool's MEDIAN authored health (cross-family, at the gem's own level) a non-boss creature's health must reach, applied BEFORE the gem's health multiplier. A FLOOR, not a multiplier stacked on the gem's modifiers, and never lowers a creature already above it. 0 disables the axis; a negative value reads as the default; values above 5 read as 5")),
                ("dynamic_dungeons_health_curve_anchor_low", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorLow, "(non-retail function) Threads: the health the band-standard curve (dynamic_dungeons_health_curve) passes through EXACTLY at level 185, the lowest Raw Fragment rung. 890 is the measured cross-family band median there, so the default reproduces today's level-185 standard exactly. The curve's per-level ratio is DERIVED from this and dynamic_dungeons_health_curve_anchor_high - there is no separate ratio dial, so the two endpoints cannot disagree with it. NaN, Infinity or a value at or below 0 reads as the default (there is no 'disable' value for an anchor - use the master switch); values above 100000 read as 100000")),
                ("dynamic_dungeons_health_curve_anchor_high", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultHealthCurveAnchorHigh, "(non-retail function) Threads: the health the band-standard curve passes through EXACTLY at level 375 (the gem ceiling and the top of the authored data). Above 375 the curve extrapolates on the per-level ratio these two anchors derive, up to dynamic_dungeons_health_curve_top_level (default 500), and clamps there; set that dial to 375 to restore the old clamp at this anchor. 13100 is the measured cross-family band median at 375. If this ends up at or below dynamic_dungeons_health_curve_anchor_low the curve goes FLAT at the low anchor rather than inverting, since a falling difficulty ladder is the one reading an admin cannot have meant. NaN, Infinity or a value at or below 0 reads as the default; values above 1000000 read as 1000000")),
                ("dynamic_dungeons_defense_curve_rate_above_375", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultDefenseCurveRateAbove375, "(non-retail function) Threads: multiplier on the stat curve's fitted EFFECTIVE defense slope (melee, missile and magic defense) above monster level 375 (owner ruling 2026-10-08, default 0.5: defense grows at half its fitted rate above 375, so player attack skill - which stops growing with the content - can still land hits on a 500 run). Continuous at 375 by construction. 0 freezes effective defense at its 375 value; 1 is the unsoftened curve. NaN or Infinity reads as the default; values below 0 read as 0 and above 1 read as 1")),
                ("dynamic_dungeons_roster_band_low", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRosterBandLow, "(non-retail function) Threads: the LOW edge multiplier of the roster level band [gemLevel * this, gemLevel * dynamic_dungeons_roster_band_high] a gem draws its trash, elite and (absent a curated boss) promoted-boss candidates from. Drives BOTH the trash and elite bands, not trash alone - the name has no 'trash' in it for that reason. A value of zero or below, or a negative, reads as the default (1.0); values above 5 read as 5")),
                ("dynamic_dungeons_roster_band_high", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRosterBandHigh, "(non-retail function) Threads: the HIGH edge multiplier of the same roster level band. Narrowed from 1.5 to 1.15 (owner ruling 2026-09-08): World Events feeds the same arithmetic a player audience's median level, where a wide spread reaches an already-varied crowd, while Threads feeds it the level printed on the gem, which players read as the dungeon's own advertised difficulty - at 1.5x a level-185 gem could draw a level-278 monster. A value of zero or below, or a negative, reads as the default (1.15); values above 5 read as 5")),
                ("dynamic_dungeons_band_low_floor", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBandLowFloorRatio, "(non-retail function) Threads: the lowest the roster band's LOW edge may step to, as a fraction of the gem level, when the natural band cannot field enough families (dynamic_dungeons_min_eligible_families) or enough distinct wcids in the chosen family (dynamic_dungeons_min_family_pool). The HIGH edge never moves. A creature reached below the natural low edge is normalized back up to the band standard - level, per-skill InitLevel, body-part damage and body armour - and pays at least the gem level's ladder XP, so widening lowers neither a run's difficulty nor its rewards. 1.0 DISABLES the whole feature. NaN, Infinity or a value at or below 0 reads as the default; values above 1.0 read as 1.0")),
                ("dynamic_dungeons_fit_headroom_margin", new Property<double>(ACE.Server.ThreadDungeons.DungeonFitFilter.DefaultFitHeadroomMargin, "(non-retail function) Threads: metres of extra headroom demanded on top of a creature's own movement height by the dynamic_dungeons_fit_filter predicate. Defaults to 0.0, which reproduces the measured verdict exactly - over 1040 creature-dungeon pairs, movement height against maxCollisionHeightFullAccess predicted pass/fail with zero disagreements, and radius never bound. The knob exists because the clearance tool's known gaps all err toward 'too passable', so a survivor found in the field can be excluded by tightening one number rather than by re-measuring the table. NaN, Infinity or a negative value reads as the default; values above 2.0 read as 2.0")),
                ("dynamic_dungeons_boss_health_band_ratio", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossHealthBandRatio, "(non-retail function) Threads: R in a normalized boss's base health max(R x the band-standard health curve at the gem level, M x the toughest non-boss base in the run's pools). 0 uses the pack term only; a negative or garbled value reads as the default; values above 50 read as 50. Only read while dynamic_dungeons_boss_normalize is on")),
                ("dynamic_dungeons_boss_health_pack_margin", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossHealthPackMargin, "(non-retail function) Threads: M in the same formula - how far above the toughest trash or elite the run's pools could field a normalized boss must sit. A positive value below 1.0 reads as 1.0 so the boss always outranks; 0 uses the band term only; a negative or garbled value reads as the default; values above 10 read as 10")),
                ("dynamic_dungeons_boss_offense_band_ratio", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossOffenseBandRatio, "(non-retail function) Threads: multiple of the band standard a normalized boss's attack skills and body-part damage are SET to (two-way, so an over-tuned boss comes down). 0 leaves the boss's authored offense; a negative or garbled value reads as the default; values above 5 read as 5")),
                ("dynamic_dungeons_boss_defense_band_ratio", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossDefenseBandRatio, "(non-retail function) Threads: multiple of the band standard a normalized boss's melee/missile/magic defense skills and body armour are SET to (two-way). 0 leaves the boss's authored defense; a negative or garbled value reads as the default; values above 5 read as 5")),
                ("dynamic_dungeons_category_resist_floor", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultCategoryResistFloor, "(non-retail function) Threads: category-immunity guard. When even the BEST of a run creature's slash/pierce/bludgeon resists is below this, those below it are raised to it; the same for fire/cold/acid/electric/nether. An absent resist counts as 1.0. Single-element immunity is kept. 0 disables; a negative or garbled value reads as the default; values above 1.0 read as 1.0")),
                ("dynamic_dungeons_category_armor_mod_ceiling", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultCategoryArmorModCeiling, "(non-retail function) Threads: category-immunity guard. When the SMALLEST of a run creature's ArmorModVs slash/pierce/bludgeon exceeds this, all three are scaled by one factor so the smallest equals it. 0 disables; a negative or garbled value reads as the default; values above 10 read as 10")),
                ("dynamic_dungeons_defense_skill_cap_offset", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultDefenseSkillCapOffset, "(non-retail function) Threads: category-immunity guard. A non-boss run creature's EFFECTIVE melee, missile or magic defense (attribute-formula contribution plus authored InitLevel, the same terms CreatureSkill.Base/Current sum) is capped at the band's effective median plus this offset, before buffs or debuffs. 0 is a valid cap (exactly at the median); a negative value disables the axis; NaN/Infinity reads as the default; values above 5000 read as 5000")),
                ("dynamic_dungeons_boss_family_row_weight", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossFamilyRowWeight, "(non-retail function) Threads: draw weight of an eligible bosses.json row whose families list names the run's family; any-family rows weigh 1. 1 keeps the plain uniform draw; values below 1 read as 1; a negative or garbled value reads as the default; values above 100 read as 100")),
                ("dynamic_dungeons_boss_level_margin", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossLevelMargin, "(non-retail function) Threads: multiple of the GEM level a run boss's stamped level must reach. One of four floors on the boss level; the pack maximum usually dominates it at the high rungs. Values below 1.0 read as 1.0; a negative value reads as the default; values above 5 read as 5")),
                ("dynamic_dungeons_reward_scale_anchor", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleAnchor, "(non-retail function) Threads: the gem level at which the gem-level reward-scale ratio (level/anchor)^exponent equals 1.0. Set to 300 while the gem ceiling was 275, so a ceiling raise would not silently devalue every existing gem; the 2026-09-09 raise to 375 put the ceiling ABOVE it, and since dynamic_dungeons_reward_scale_cap defaults to uncapped, gems above 300 scale past 1.0 rather than flattening. NaN, Infinity or a non-positive value reads as the default")),
                ("dynamic_dungeons_reward_scale_exponent", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleExponent, "(non-retail function) Threads: the exponent of the gem-level reward-scale ratio (level/anchor)^exponent. 2.87 is chosen so ratio(185) = 0.25 with the default anchor of 300. NaN or Infinity reads as the default")),
                ("dynamic_dungeons_reward_scale_floor", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleFloor, "(non-retail function) Threads: lower bound on the gem-level reward-scale ratio, so a low-level Thread still pays something on every scaled axis. NaN, Infinity or a negative value reads as the default")),
                ("dynamic_dungeons_reward_scale_cap", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleCap, "(non-retail function) Threads: upper bound on the gem-level reward-scale ratio. 0 or less means UNCAPPED - deliberate, so growth above the anchor is unbounded until an admin sets an explicit safety valve")),
                ("dynamic_dungeons_salvage_affinity_chance_cap", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultSalvageAffinityChanceCap, "(non-retail function) Threads: ceiling on a run's per-kill salvage-affinity chance, applied AFTER that axis's own gem-level reward-scale ratio - which uses the same anchor/exponent/floor as dynamic_dungeons_reward_scale_anchor/_exponent/_floor but is NEVER capped by dynamic_dungeons_reward_scale_cap, so affinity keeps growing with gem level above the anchor regardless of what that cap is set to for XP/luminance/loot-quantity. This dial is the only ceiling on the result. Does NOT touch dynamic_dungeons_modifier_reward_scale. 1.0 (default) is neutral - a no-op against the chance's own [0, 1] domain. NaN, Infinity or a negative value reads as the default; values above 1.0 read as 1.0")),
                ("dynamic_dungeons_group_effort_per_member", new Property<double>(1.0, "(non-retail function) Threads: g in E = 1 + g(N-1), total work a group run demands relative to solo")),
                ("dynamic_dungeons_group_count_share", new Property<double>(0.5, "(non-retail function) Threads: s in C = 1 + s(E-1), the share of E paid as extra monsters, the rest becomes health")),
                ("dynamic_dungeons_group_reward_bonus_per_member", new Property<double>(0.05, "(non-retail function) Threads: b in B = min(1 + b(N-1), cap) on XP, luminance and loot of a group run; 0 = strict solo parity")),
                ("dynamic_dungeons_group_reward_bonus_cap", new Property<double>(1.5, "(non-retail function) Threads: maximum B; values below 1.0 read as 1.0")),
                ("world_events_boss_health_cap", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.cap for every boss when positive; 0 or negative = use the JSON value. The ceiling on the power-curve boss health multiplier (BossDef.ResolveHealthMult)")),
                ("world_events_boss_hits_to_kill", new Property<double>(3.0, "(non-retail function) World Events: how many ORDINARY (non-crit) hits from a named boss the TOP player present - the highest Health.MaxValue among counted participants, staff excluded - should survive. 3 means roughly 2 to 4. A low-level player being one-shot is accepted by design; the boss is sized against the toughest player in the fight, not the softest. A non-finite or non-positive value falls back to the built-in default")),
                ("world_events_boss_min_sample_seconds", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.minSampleSeconds for every boss when positive; 0 or negative = use the JSON value. The shortest wave phase that produces a usable throughput measurement")),
                ("world_events_boss_per_player", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.perPlayer for every boss when positive; 0 or negative = use the JSON value. Added to the power-curve boss health multiplier per unit of audience power")),
                ("world_events_boss_retarget_margin", new Property<double>(1.0, "(non-retail function) World Events: hysteresis, in metres, on boss retargeting. A boss keeps its current player target while that player is no more than this much farther away than the nearest player, so it does not flip between two players at similar range; past the margin it switches to the nearest. A player the boss is in melee range of is kept regardless (engagement lock). 0 switches as soon as anyone is strictly nearer. A non-finite or negative value falls back to 1")),
                ("world_events_boss_retarget_seconds", new Property<double>(1.0, "(non-retail function) World Events: how often, in seconds, a boss re-picks its target. Every boss targets the nearest player (the nearest combat pet only when no player is in range), overriding the weenie's authored targeting tactic, so a short interval lets it turn onto whoever is now closest instead of grinding through a crowd toward its old target. It keeps a player it is in melee range of, and keeps its current target while within world_events_boss_retarget_margin of the nearest. Ordinary monsters keep the retail 5 s. A non-finite or non-positive value falls back to 5")),
                ("world_events_boss_scale_multiplier", new Property<double>(0.67, "(non-retail function) World Events: multiplier applied to a boss's ObjScale, and to every item it wields or carries, at spawn (before it enters the world). 0.67 shrinks bosses by a third. Items keep their size relative to the body and get their original scale back when they drop as loot. A non-finite or non-positive value means no change (1.0)")),
                ("world_events_boss_target_kill_seconds", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.targetKillSeconds for every boss when positive; 0 or negative = use the JSON value. How long the boss should take to kill at the group's demonstrated throughput rate")),
                ("world_events_boss_tether_radius", new Property<double>(50.0, "(non-retail function) World Events: radius in metres of the boss tether stamped by world_events_boss_tether_enabled. The boss's HomeRadius is set to twice this, so the ordinary leash still sits well outside the tether and only catches a boss the retarget could not pull back. A non-finite or non-positive value disables the stamp for that spawn, the same as turning the flag off")),
                ("world_events_boss_throughput_calibration", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.throughputCalibration for every boss when positive; 0 or negative = use the JSON value. Multiplied into the measured wave-phase rate before it is turned into boss health")),
                ("world_events_crowd_health_cap", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.crowdHealth.cap for every source when positive; 0 or negative = use the JSON value. The ceiling on the crowd health multiplier (CrowdHealthDef.Resolve)")),
                ("world_events_crowd_health_per_participant", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.crowdHealth.perParticipant for every source when positive; 0 or negative = use the JSON value. Added to the crowd health multiplier per participant above startAt")),
                ("world_events_elite_band_low", new Property<double>(1.25, "(non-retail function) World Events: multiplier applied to the sampled participant level to get the LOW edge of the elite/champion band (WorldEventRosterSelector.EliteBand). A non-finite or non-positive value falls back to the built-in default")),
                ("world_events_elite_band_high", new Property<double>(1.75, "(non-retail function) World Events: multiplier applied to the sampled participant level to get the HIGH edge of the elite/champion band (WorldEventRosterSelector.EliteBand), before the edge is clamped to level 275. A non-finite or non-positive value falls back to the built-in default")),
                ("world_events_pace_health_cap", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.pace.healthCap for every source when positive; 0 or negative = use the JSON value. The ceiling on the pace controller's half of the spawn health multiplier")),
                ("world_events_pace_health_step", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.pace.healthStep for every source when positive; 0 or negative = use the JSON value. How much one pace-controller step moves the health multiplier, in either direction")),
                ("world_events_pace_max_wave_seconds", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.pace.maxWaveSeconds for every source when positive; 0 or negative = use the JSON value. A wave standing longer than this eases the pace controller off")),
                ("world_events_pace_min_wave_seconds", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.pace.minWaveSeconds for every source when positive; 0 or negative = use the JSON value. A wave cleared faster than this pushes the pace controller harder")),
                ("world_events_synthetic_champion_health_mult", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.syntheticChampionHealthMult for every source when positive; 0 or negative = use the JSON value. Health multiplier applied to a role-0 member promoted into an overflow champion slot")),
                ("world_events_synthetic_elite_health_mult", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.syntheticEliteHealthMult for every source when positive; 0 or negative = use the JSON value. Health multiplier applied to a role-0 member promoted into the every-third-wave elite slot")),
                ("world_events_trash_band_low", new Property<double>(1.0, "(non-retail function) World Events: multiplier applied to the sampled participant level to get the LOW edge of the trash band (WorldEventRosterSelector.TrashBand). A non-finite or non-positive value falls back to the built-in default")),
                ("world_events_trash_band_high", new Property<double>(1.5, "(non-retail function) World Events: multiplier applied to the sampled participant level to get the HIGH edge of the trash band (WorldEventRosterSelector.TrashBand), before the edge is clamped to level 275. A non-finite or non-positive value falls back to the built-in default")),
                ("world_events_category_resist_floor", new Property<double>(ACE.Server.WorldEvents.WorldEventCombatGuard.DefaultCategoryResistFloor, "(non-retail function) World Events: category-immunity guard (world_events_strip_combat_traits). When even the BEST of a roster monster slash/pierce/bludgeon resists is below this, those below it are raised to it; the same for fire/cold/acid/electric/nether. An absent resist counts as 1.0. Single-element immunity is kept. 0 disables; a negative or garbled value reads as the default; values above 1.0 read as 1.0")),
                ("world_events_category_armor_mod_ceiling", new Property<double>(ACE.Server.WorldEvents.WorldEventCombatGuard.DefaultCategoryArmorModCeiling, "(non-retail function) World Events: category-immunity guard (world_events_strip_combat_traits). When the SMALLEST of a roster monster ArmorModVs slash/pierce/bludgeon exceeds this, all three are scaled by one factor so the smallest equals it. 0 disables; a negative or garbled value reads as the default; values above 10 read as 10")),
                ("world_events_defense_skill_cap_offset", new Property<double>(ACE.Server.WorldEvents.WorldEventCombatGuard.DefaultDefenseSkillCapOffset, "(non-retail function) World Events: category-immunity guard (world_events_strip_combat_traits). A roster monster EFFECTIVE melee, missile or magic defense (attribute-formula contribution plus authored InitLevel) is capped at the effective median of the band standard at its slot level plus this offset, before buffs or debuffs. 0 is a valid cap (exactly at the median); a negative value disables the axis; NaN/Infinity reads as the default; values above 5000 read as 5000")),
                ("world_events_band_low_floor", new Property<double>(ACE.Server.WorldEvents.WorldEventRosterSelector.DefaultBandLowFloor, "(non-retail function) World Events: the lowest the trash band's LOW edge may step to, as a fraction of the slot's participant level, when the slot's family pool holds fewer than world_events_min_slot_pool distinct wcids (world_events_band_uplift). The HIGH edge never moves. 1.0 disables the widening. NaN, Infinity or a value at or below 0 reads as the default; values above 1.0 read as 1.0")),
                ("world_events_wave_count_per_participant", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.waveCount.perParticipant for every source when positive; 0 or negative = use the JSON value. The per-participant term of the per-wave trash count formula")),
                ("world_events_wave_interval_seconds", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.waveIntervalSeconds for every source when positive; 0 or negative = use the JSON value. Seconds between wave spawns")),
                ("xp_modifier", new Property<double>(1.0, "scales the amount of xp received by players")),
                ("ml_treasure_drop_chance", new Property<double>(0.01, "(non-retail function) ML Treasure Hunt: chance a Marae Lassel kill drops a treasure map, checked in Creature_Death.CreateCorpse after the realm/box/ml_treasure_enabled gates. Raised from 0.008 to 0.01 (1 percent) alongside the digsite encounter system, which makes each map worth more to find")),
                ("ml_treasure_relaria_chance", new Property<double>(0.01, "(non-retail function) ML Treasure Hunt: chance a rolled treasure map is the rare Aun Relaria boss variant (PropertyInt.TreasureMapBossWcid) instead of an ordinary payout map. Lowered from 0.05 to 0.01 alongside the digsite encounter system: Relaria is now the 1 percent outcome of a dig rather than the 5 percent one, which is how it takes its place as the rarest of the four things a map can produce. A Relaria map pays no treasure currency at all - its final dig step summons the boss instead (MlRelariaSpawner)")),
                ("ml_treasure_boss_tether_radius", new Property<double>(40.0, "(non-retail function) ML Treasure Hunt: metres from where the Aun Relaria boss was dug up inside which it may hold a target (PropertyFloat 9009 TetherRadius). Its HomeRadius is stamped at twice this, so the ordinary 192 m leash still sits outside the tether. A non-finite or non-positive value stamps nothing and leaves the boss behaving like any other monster")),
                ("ml_treasure_far_metres", new Property<double>(500, "(non-retail function) ML Treasure Hunt: beyond this map-space distance from the rolled site, TreasureMapHandler names only the nearest town and a rough bearing")),
                ("ml_treasure_near_metres", new Property<double>(40, "(non-retail function) ML Treasure Hunt: within this map-space distance of the rolled site, TreasureMapHandler switches from bearing-only reveal to digging")),
                ("ml_treasure_site_radius_metres", new Property<double>(720, "(non-retail function) ML Treasure Hunt: the HARD cap, in straight-line metres, on how far a map's dig site may be from the point it was picked up (the corpse it dropped on; for a map with no site yet, the position it is first read at). A random draw among every catalogue site within this distance, never the nearest one; when none is that close the dig site is the pickup point itself. No widening and no island-wide fallback. 720 = three map units (round 15 owner ruling); 0 puts every site at its pickup point")),
                ("ml_relaria_repeat_aura_chance", new Property<double>(MlRelariaTrophy.DefaultRepeatAuraChance, "(non-retail function) ML Treasure Hunt: chance a REPEAT Aun Relaria kill (a killer who has already claimed the one-time CAP trophy) rolls the permanent cosmetic aura (MlRelariaTrophy.DecideRepeatKillAuraOutcome). A character who already has the aura is paid ml_relaria_repeat_doubloons bonus doubloons on a successful roll instead of stamping an already-stamped quest row")),
                ("ml_digsite_separation_metres", new Property<double>(80.0, "(non-retail function) ML digsite encounters: metres between two live encounter anchors below which a second one is refused, so two fights on the same hillside do not merge into one unreadable pile. Compared within a landblock instance only - the same coordinates in two realm copies are not near each other")),
                ("wave_encounter_presence_radius", new Property<double>(80.0, "(non-retail function) Wave encounters (object-anchored, e.g. the D6 Sounding Drum): radius in metres around the anchor, same landblock instance, of the ONE scan that decides who hears the encounter's lines and whether anyone living is still there (wipe / walk-away test). Dead players do not count; staff do")),
                ("ml_digsite_audience_radius_metres", new Property<double>(60.0, "(non-retail function) ML digsite encounters: radius of the ONE scan that decides wave size, who hears an announcement, whether the group has wiped, and whose presence is stamped for group rewards. Standing in it does not by itself earn a payout: a player must also have dealt damage. Online, alive and non-staff only")),
                ("ml_digsite_spawn_radius_metres", new Property<double>(12.0, "(non-retail function) ML digsite encounters: radius of the disc encounter creatures are scattered across around the dig, sampled uniformly by area and then snapped to the terrain")),
                ("ml_digsite_wave_count_per_participant", new Property<double>(1.5, "(non-retail function) ML digsite encounters: per-participant term of the per-wave creature count on the shipped player-count curve max(base, min(cap, base + ceil(perParticipant * players))). Re-sampled at every wave, so players arriving mid-fight are noticed")),
                ("ml_digsite_corrupted_health_multiplier", new Property<double>(6.0, "(non-retail function) ML digsite encounters: how much more health the Corruption shape's Corrupted mob has than an ordinary field mob of the same wcid/scaling. Round 17 owner ruling: 6, up from 3")),
                ("ml_digsite_corruption_power_per_tick", new Property<double>(0.03, "(non-retail function) ML digsite encounters: damage the rest of a Corruption encounter's field gains per ml_digsite_corruption_power_tick_seconds tick while its Corrupted mob is alive, as a fraction (0.03 = +3% damage, i.e. +3 DamageRating). The accumulated gain is capped at +300% (MlDigsiteRules.MaxDamageRatingGrowth) and resets when the Corrupted mob dies. 0 turns the power gain off")),
                ("ml_digsite_fail_payout_multiplier", new Property<double>(0.85, "(non-retail function) ML digsite encounters: round 16 owner ruling - every non-WIN outcome (a timer expiry, a wipe, or a bail with some progress) pays its progress fraction times this, instead of the progress fraction straight. A WIN (FullClear - wave 8 cleared, the Boss Rush boss killed, or the required Corrupted-mob count reached) always pays 100%, never multiplied by this")),
                ("ml_digsite_partial_payout_fraction", new Property<double>(0.35, "(non-retail function) ML digsite encounters: DEAD as of round 16 - EncounterPayoutFraction's Corruption branch no longer reads this; Corruption stopped being binary and now pays by Corrupted-mob progress like the other two shapes. Left registered only because MlDigsitePayoutTunables.PartialFraction is still wired from it")),
                ("ml_digsite_checkpoint_bonus", new Property<double>(0.05, "(non-retail function) ML digsite encounters: payout fraction added per checkpoint mini-boss killed in the endless Waves shape, on top of the ml_digsite_wave_tiers tier; the total is capped at 1")),
                ("ml_digsite_wave_health_per_wave", new Property<double>(0.10, "(non-retail function) ML digsite encounters: health multiplier added to wave and checkpoint creatures per wave after the first (1 + this x (wave - 1)), multiplied into crowd scaling, up to ml_digsite_wave_health_cap")),
                ("ml_digsite_wave_health_cap", new Property<double>(3.0, "(non-retail function) ML digsite encounters: ceiling on the per-wave health multiplier")),
                ("ml_digsite_bossrush_min_fraction", new Property<double>(0.10, "(non-retail function) ML digsite encounters: the least a Boss Rush pays when its boss is not killed. Otherwise it pays the share of the boss health removed (1 minus the lowest health fraction sampled); a kill pays in full")),
                ("ml_digsite_bossrush_secondary_cadence_mult", new Property<double>(2.0, "(non-retail function) ML digsite encounters: how much longer a SECONDARY Boss Rush mechanic's cadence is than the same mechanic's would be as its set's main one. Not a different code path - the same module on a slower clock, so a set's secondary is present without competing with its main for the player's attention. Clamped into [1, 10]")),
                ("ml_digsite_group_share_pool", new Property<double>(1.5, "(non-retail function) ML digsite encounters: each helper's chest pays the encounter fraction x clamp(this / helpers, ml_digsite_group_share_min, ml_digsite_group_share_max). The digger's chest is never scaled by it")),
                ("ml_digsite_group_share_max", new Property<double>(0.75, "(non-retail function) ML digsite encounters: the most one helper's chest pays, as a share of the encounter fraction")),
                ("ml_digsite_group_share_min", new Property<double>(0.20, "(non-retail function) ML digsite encounters: the least one helper's chest pays, as a share of the encounter fraction")),
                ("ml_digsite_boss_tether_radius", new Property<double>(40.0, "(non-retail function) ML digsite encounters: metres from the dig inside which an encounter creature may hold a target (PropertyFloat 9009 TetherRadius), with HomeRadius stamped at twice it. Matters more here than in an arena: a digsite is in the open world, and the audience scan that sizes and pays the fight is anchored to the hole in the ground. A non-finite or non-positive value stamps nothing and leaves creatures behaving like any other monster")),
                ("ml_digsite_kept_siraluun_chance", new Property<double>(0.05, "(non-retail function) ML digsite encounters: chance that the Boss Rush boss, or an endless Waves encounter's FIRST checkpoint mini-boss, is replaced by a rare Kept Siraluun (wcids 1005470-1005477). At most ONE roll per encounter, so a long endless run does not multiply the feather rate. Preference goes to whichever of the eight shares the ordinary pick's authored level; falls back to a uniform draw across all eight when none does. Its feather goes into the owner's chest only, unscaled")),
                ("ml_mapevent_boss_magic_defense_scale", new Property<double>(0.90, "(non-retail function) RoZ round 19 owner ruling M1: Multiplier on the authored MagicDefense skill of Marae Lassel map-event bosses (digsite Boss/MiniBoss/Checkpoint/Priority roles and Aun Relaria), applied at spawn by lowering InitLevel (MlMapEventBossMagicDefense.ScaleInitLevel), never by rewriting weenie SQL. 1.0 = authored (no scale-down). Clamped to [0, 1] on read")),
                ("ml_mapevent_min_hit_chance", new Property<double>(0.60, "(non-retail function) RoZ round 19 level-spread scaling: the minimum chance the keyed player best trained melee (or missile) attack must have to land on a map-event creature. Its MeleeDefense (MissileDefense) is lowered only as far as needed to reach this, never raised. Trash is keyed to one sampled present player, objectives and bosses to the weakest present player. Clamped to [0.01, 0.99]")),
                ("ml_mapevent_min_spell_land_chance", new Property<double>(0.70, "(non-retail function) RoZ round 19 level-spread scaling: the minimum chance the keyed player best War/Void/Life/Creature skill must have to beat a map-event creature MagicDefense (applied after the M1 scale). Lowered only, never raised. Clamped to [0.01, 0.99]")),
                ("ml_mapevent_trash_max_hit_chance", new Property<double>(0.75, "(non-retail function) RoZ round 19 level-spread scaling: the most often a map-event trash creature (digsite Wave/Add) may land a melee or missile attack on the sampled player it was sized for. Its attack skills are lowered only, never raised. Clamped to [0.01, 0.99]")),
                ("ml_mapevent_trash_damage_floor", new Property<double>(0.25, "(non-retail function) RoZ round 19 level-spread scaling: the smallest damage multiplier ml_mapevent_trash_reference_max_health may apply to a map-event trash creature. Clamped to [0.01, 1]")),
                ("ml_mapevent_trash_health_exponent", new Property<double>(1.0, "(non-retail function) RoZ round 19 level-spread scaling: map-event trash health is multiplied by (sampledPlayerLevel / creatureLevel)^this when the player is the lower level, never raised. 0 turns the health scale off")),
                ("ml_mapevent_xp_level_exponent", new Property<double>(2.0, "(non-retail function) RoZ round 19 level-spread scaling: map-event trash XpOverride and LuminanceAward are multiplied by (sampledPlayerLevel / creatureLevel)^this when the player is the lower level, so a low character cannot farm trash authored with a level-275 XP award. 0 turns the XP scale off")),
                ("ml_mapevent_boss_power_per_player", new Property<double>(0.15, "(non-retail function) RoZ round 19 level-spread scaling: the Boss Rush boss and Aun Relaria health multiplier is clamp(1 + this * sum((level/275)^2), 1, ml_mapevent_boss_health_cap) over the present players - the World Event power-sum curve (BossDef.ResolveHealthMult). Replaces headcount crowd health for the Boss Rush boss")),
                ("ml_mapevent_boss_health_cap", new Property<double>(3.0, "(non-retail function) RoZ round 19 level-spread scaling: ceiling on the map-event boss power-sum health multiplier. Floored at 1")),
                ("ml_mapevent_boss_hits_to_kill", new Property<double>(3.0, "(non-retail function) RoZ round 19: how many ordinary hits the TOUGHEST present player should survive from the digsite Boss Rush boss once ml_mapevent_boss_damage_scaling_enabled has converged. Clamped to [0.1, 100]")),
                ("ml_digsite_crowd_per_player", new Property<double>(0.15, "(non-retail function) ML digsite encounters: health multiplier added per participant to every digsite-spawned creature except the Boss Rush boss (Wave, Add, Priority, MiniBoss, Checkpoint), resolved once at that creature's own spawn moment from the digsite audience scan - the same formula bluespire_ladder_d6_crowd_per_player uses, but its OWN key so the two never share a dial. Since RoZ round 19 the Boss Rush boss takes the ml_mapevent_boss_power_per_player power-sum curve instead, while ml_mapevent_spread_scaling_enabled is on")),
                ("ml_digsite_crowd_health_cap", new Property<double>(3.0, "(non-retail function) ML digsite encounters: ceiling on the health multiplier ml_digsite_crowd_per_player may reach, however large the crowd")),
                ("ml_digsite_priority_scale", new Property<double>(1.25, "(non-retail function) ML digsite encounters: ObjScale multiplier stamped on the Corruption Meter shape's priority mob, alongside ml_digsite_priority_script, so it reads as visibly bigger than the field around it")),
                ("bluespire_ladder_d6_crowd_per_player", new Property<double>(0.15, "(non-retail function) Bluespire ladder rung 6: health multiplier added per player above bluespire_ladder_d6_crowd_start_at. Feeds CrowdHealthDef.Resolve unchanged: min(cap, 1 + perPlayer * max(0, players - startAt)), floored at 1.0. Resolved at each creature's own spawn moment, so a creature spawned into an empty room keeps its empty-room health until it dies")),
                ("bluespire_ladder_d6_crowd_health_cap", new Property<double>(3.0, "(non-retail function) Bluespire ladder rung 6: ceiling on the crowd health multiplier, however large the crowd gets. CrowdHealthDef.Resolve floors the cap at 1.0 before applying it, so a value below 1 turns the health axis OFF rather than making creatures weaker than their weenie authored them")),
                ("world_tick_slow_log_median_multiplier", new Property<double>(3.0, "(non-retail function) Monitoring: a world-loop iteration over world_tick_slow_log_threshold_ms is logged as SLOW_TICK only if it is also at least this many times the median of the last 1024 updated iterations. 0 means the floor alone decides; so does a median of fewer than 32 samples. A negative value is treated as 0")),
                // Combat pet follow and reach (CombatPet_Follow / CombatPet.CheckTargetReachable, PetFollowSettings). Read once per pet tick.
                ("pet_follow_teleport_distance", new Property<double>(40.0, "(non-retail function) Combat pets: a pet with no target that is farther than this many metres (cylinder distance) from its owner is teleported to them at once instead of running the whole way. Only while the two share a landblock instance and tick thread. 0 or less disables the distance trigger")),
                ("pet_follow_max_seconds", new Property<double>(15.0, "(non-retail function) Combat pets: a follow run back to the owner that has not arrived within this many seconds ends in a teleport to the owner, however much progress it appears to be making. 0 or less disables the max-time trigger")),
                ("pet_follow_stuck_seconds", new Property<double>(5.0, "(non-retail function) Combat pets: a following pet that closes no net distance on its owner (0.5 m) for this many seconds is teleported to them. Net distance means side-to-side or back-and-forth grinding against a wall counts as zero. 0 or less disables the stuck trigger")),
                ("pet_combat_unreachable_seconds", new Property<double>(8.0, "(non-retail function) Combat pets: a pet that has neither closed on its target (0.5 m net) nor attacked it for this many seconds drops the target as unreachable and goes back to following its owner. Not a leash - the owner's distance plays no part. 0 or less disables the rule")),
                ("motion_stall_threshold_seconds", new Property<double>(ACE.Server.WorldObjects.MotionStallWatchdog.DefaultThresholdSeconds, "(non-retail function) Motion-stall watchdog, gate 1: seconds a FastTick player's animation queue must have stayed continuously non-empty before motion_stall_autoclear may clear it. Default 10 (owner ruling). For reference, player animation lengths in the human motion table 0x09000001 (ACE.Content.Tools motionlength): Marketplace / PK Arena recall 18.4 s, other recalls 15.1 s, EnterPKLite 10.3 s, UseMagicStaff 6.0 s, AFKState 5.7 s; 10 s is below the recalls and EnterPKLite. Values below 1 are treated as 1. Measured from when the queue last became non-empty, so a queue stuck for minutes is caught on the first click after it sticks.")),
                ("motion_stall_turn_blocked_seconds", new Property<double>(ACE.Server.WorldObjects.MotionStallWatchdog.DefaultTurnBlockedSeconds, "(non-retail function) Motion-stall watchdog, gate 2: seconds a turn-to must have been continuously blocked behind the animation queue before motion_stall_autoclear may clear it. A healthy queue releases a waiting turn as soon as the animation ahead of it ends and a stuck one never does; this is also the delay a stuck player sees on the click that gets cleared. Default 5 (owner ruling), which is below the recalls (15.1 / 18.4 s), EnterPKLite (10.3 s), UseMagicStaff (6.0 s) and AFKState (5.7 s) in the human motion table 0x09000001 (ACE.Content.Tools motionlength). Values below 0.5 are treated as 0.5.")),
                ("pet_combat_unreachable_ignore_seconds", new Property<double>(10.0, "(non-retail function) Combat pets: how long a target dropped by pet_combat_unreachable_seconds is skipped by the pet's target selection (nearest-monster and summon-assist picks), so it is not re-picked at once. A negative value is treated as 0"))
                ));

        public static readonly ReadOnlyDictionary<string, Property<string>> DefaultStringProperties =
            DictOf(
                ("account_vault_denylist", new Property<string>("", "WaffleACE Mule Vendor: comma-separated landblock entries (LLLL = that landblock in any realm, LLLL@R = realm R only) where summoning a mule vendor is refused. Empty by default. This is the emergency lever - a specific problem area - and it OUTRANKS account_vault_allowlist, so a landblock named in both is refused. Parsed exactly like mule_landblocks; a malformed entry is skipped with a logged warning, never thrown")),
                ("dynamic_dungeons_banned_spell_ids", new Property<string>(ACE.Server.ThreadDungeons.DungeonSpellFilter.DefaultBannedSpellIds, "(non-retail function) Threads: comma-separated spell ids removed by id from every run creature's spell book regardless of projectile count, while dynamic_dungeons_strip_aoe_spells is true. Defaults to Exsanguinating Wave under both of its live ids (3940, 3999) and Poisoned Vitality (6167) - owner ruling 2026-09-08. Ban by ID ONLY: spell names repeat across ids in ace_world, so a name match would be ambiguous. Empty means the id list is off and only the projectile rule applies; a malformed entry is skipped with a logged warning, never thrown")),
                ("dynamic_dungeons_survey_reset_timezone", new Property<string>("America/New_York", "(non-retail function) Threads: IANA or Windows time zone id whose wall clock defines the survey day. The survey window, count and all three payout tiers turn over together at dynamic_dungeons_survey_reset_hour in this zone. An unknown id falls back to a fixed UTC-5 offset with a logged warning")),
                ("account_vault_allowlist", new Property<string>("01F5@1", "WaffleACE Mule Vendor: comma-separated landblock entries (LLLL = that landblock in any realm, LLLL@R = realm R only) where summoning a mule vendor is the ONLY place it is permitted. Defaults to 01F5@1, the Marketplace in realm 1's copy of Aerfalle Keep (formerly 016C in realm 0), on the repo owner's ruling after the first live test - it supersedes DESIGN 11.2's original summonable-anywhere decision. EMPTY means no allowlist, i.e. permitted everywhere subject only to account_vault_denylist; empty is NOT 'nowhere'. Checked against the summoner's landblock and again against each placement rung, since the spawn offset can cross a landblock line. Parsed exactly like account_vault_denylist")),
                ("account_vault_allowlist_name", new Property<string>("the Marketplace", "WaffleACE Mule Vendor: the player-facing name of the account_vault_allowlist area, used only in the refusal message ('You can only summon your vault vendor in {this}.'). A separate row because a landblock id means nothing to a player and this message would otherwise go stale the moment account_vault_allowlist was pointed elsewhere. Blank degrades to 'the permitted area'")),
                ("facet_allowlist", new Property<string>("01F5@1", "Player Facets: comma-separated landblock entries (LLLL = that landblock in any realm, LLLL@R = realm R only) where /facet N may be used. Defaults to 01F5@1, the Marketplace in realm 1's copy of Aerfalle Keep. EMPTY means no restriction, i.e. permitted everywhere - empty is NOT 'nowhere'. Parsed exactly like account_vault_allowlist")),
                ("facet_allowlist_name", new Property<string>("the Marketplace", "Player Facets: the player-facing name of the facet_allowlist area, used only in the refusal message ('You can only change facets in {this}.'). A separate row because a landblock id means nothing to a player and this message would otherwise go stale the moment facet_allowlist was pointed elsewhere. Blank degrades to 'the permitted area'")),
                ("bluespire_ladder_d1_entry_quest", new Property<string>("BluespireTheFirstDrum", "(non-retail function) Bluespire ladder: the quest registry name a character must have been stamped with before rung 1 will open. Rung 1 is the ONLY rung with an entry quest; every other rung is gated by the previous rung's clear stamp instead. Checked with QuestManager.HasQuest - EVER stamped, cooldown-blind - so it can never be un-earned. Blank turns the entry-quest requirement off and leaves rung 1 gated by level alone")),
                ("content_folder", new Property<string>("Content", "for content creators to live edit weenies. defaults to Content folder found in same directory as ACE.Server.dll")),
                ("mule_landblocks", new Property<string>("01F5@1", "WaffleACE: comma-separated list of landblock entries (LLLL = that landblock in any realm, e.g. 016C; LLLL@R = realm R only, e.g. 01F5@1) that count as 'mule landblocks' for the IP active-player limit (ip_limit_enabled). This key belongs ONLY to the IP active-player limit system - it does NOT gate, and is unrelated to, the separate IsMule character-conversion system (mule_system_enabled, mule_level, mule_bind_position, etc). Defaults to 01F5@1, the Marketplace in realm 1's copy of Aerfalle Keep. Managed with the /iplimit landblock admin command; a hand edit must keep the comma-separated form")),
                ("realm_portal_recall_allowlist", new Property<string>(ACE.Server.Realms.RealmPortalRecallTunables.DefaultAllowlist, "WaffleACE: comma-separated portal wcids exempt from the realm-routing recall lock. Every portal carrying PortalRealm (9022), PortalInstancing (9021) or PortalExitInstance (9025) is NoRecall / NoTie / NoSummon whatever its PortalBitmask says; a wcid listed here is exempt ONLY while it is routed by a persistent PortalRealm alone (9022 set, no 9021, no 9025), so recall, tie and summon of it land in its realm. Defaults to 23032,1001007 - the two Marketplace portals - on the owner's 2026-09-26 ruling that the Marketplace stay recallable after its move into realm 1. Empty means no realm portal is recallable. A malformed entry is skipped with a logged warning, never thrown")),
                ("pvp_safe_landblocks", new Property<string>("01F5@1", "WaffleACE: comma-separated list of landblock entries (LLLL = that landblock in any persistent realm, e.g. 016C; LLLL@R = realm R only, e.g. 01F5@1; never an ephemeral instance) where PvP harm is refused (Player.CheckPKStatusVsTarget), checked ahead of the retail PK rules. Either the attacker or the target standing in a listed landblock is enough to refuse - no shooting in from outside, no shooting out. Defaults to 01F5@1, the Marketplace in realm 1's copy of Aerfalle Keep, after the owner report of being shot there. Parsed exactly like mule_landblocks; a malformed entry is skipped with a logged warning, never thrown. Empty means no safe zone at all")),
                ("ip_limit_exempt_accounts", new Property<string>("", "WaffleACE: comma-separated ACCOUNT names (not character names) exempt from the IP active-player limit (ip_limit_enabled), in addition to the access-level exemption (ip_limit_exempt_access_level), loopback addresses, and the existing AllowUnlimitedSessionsFromIPAddresses allowlist. Managed with the ip-limit-exempt admin command; a hand edit must keep the comma-separated form")),
                ("mule_bind_position", new Property<string>("0x01F50262 39.971748 -118.261452 6.005000 0.663589 0.000000 0.000000 0.748097 @1", "Mule system: loc string (cell x y z qw qx qy qz - same token order as /teleloc, plus an optional trailing @R naming the realm) that ApplyMuleConversion parses via AdminCommands.TryParseLocPosition and sets as PositionType.Sanctuary, mirroring a real lifestone /use. With @R the bind lands in realm R's default instance, and an unregistered realm skips the bind with a logged warning; without it the bind keeps the player's current instance. Defaults to the Marketplace lifestone in realm 1's copy of Aerfalle Keep. A parse failure logs a warning and skips the bind rather than throwing - a bad config string must never break the conversion itself")),
                ("discord_webhook_url_general", new Property<string>("", "Discord webhook URL that General chat is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_GENERAL environment variable when set")),
                ("discord_webhook_url_trade", new Property<string>("", "Discord webhook URL that Trade chat is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_TRADE environment variable when set")),
                ("discord_webhook_url_audit", new Property<string>("", "Discord webhook URL that the staff Audit channel (admin command records) is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_AUDIT environment variable when set")),
                ("discord_webhook_url_events", new Property<string>("", "Discord webhook URL that World Events announcements (the teaser, the run going live, and the outcome - not the countdown warnings or per-wave flavour) are relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_EVENTS environment variable when set")),
                ("discord_webhook_url_pvp", new Property<string>("", "Discord webhook URL that the PvP feed (an open-world PK kill, and a finished arena match's mode/winners/losers) is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_PVP environment variable when set. Empty = no posts and no work")),
                ("dat_older_warning_msg", new Property<string>("Your DAT files are incomplete.\nThis server does not support dynamic DAT updating at this time.\nPlease visit https://emulator.ac/how-to-play to download the complete DAT files.", "Warning message displayed (if show_dat_warning is true) to player if client attempts DAT download from server")),
                ("dat_newer_warning_msg", new Property<string>("Your DAT files are newer than expected.\nPlease visit https://emulator.ac/how-to-play to download the correct DAT files.", "Warning message displayed (if show_dat_warning is true) to player if client connects to this server")),
                ("popup_header", new Property<string>("Welcome to Asheron's Call!", "Welcome message displayed when you log in")),
                ("popup_welcome", new Property<string>("To begin your training, speak to the Society Greeter. Walk up to the Society Greeter using the 'W' key, then double-click on her to initiate a conversation.", "Welcome message popup in training halls")),
                ("popup_welcome_olthoi", new Property<string>("Welcome to the Olthoi hive! Be sure to talk to the Olthoi Queen to receive the Olthoi protections granted by the energies of the hive.", "Welcome message displayed on the first login for an Olthoi Player")),
                ("popup_motd", new Property<string>("", "Popup message of the day")),
                ("server_motd", new Property<string>("", "Server message of the day")),
                ("dynamic_dungeons_data_folder", new Property<string>("", "(non-retail function) overrides where Threads JSON files are read from; empty = <content_folder>/dungeons/dynamic, then exe-adjacent Content/dungeons/dynamic")),
                ("world_events_axis_folder", new Property<string>("", "(non-retail function) overrides where World Events axis JSON files are read from; empty = <content_folder>/events/axes, then exe-adjacent Content/events/axes")),
                ("ml_treasure_sites_folder", new Property<string>("", "(non-retail function) ML Treasure Hunt: overrides where dig-sites.tsv is read from; empty = <content_folder>/marae-lassel, then exe-adjacent Content/marae-lassel. See MlTreasureSiteStore")),
                ("world_events_ip_exempt", new Property<string>("", "(non-retail function) comma-separated IP addresses exempt from the World Events per-IP claim gate (shared households)")),
                ("top_exempt_accounts", new Property<string>("", "WaffleACE: comma-separated ACCOUNT names (not character names) excluded from every /top leaderboard and from the Proving Grounds new-record broadcasts. Accounts at AccessLevel Sentinel or above, and characters carrying the IsAdmin/IsArch/IsEnvoy/IsSentinel bools, are exempt inherently and do not need listing here - this list is for staff alternate accounts that run at normal Player access. Managed with the top-exempt admin command; a hand edit must keep the comma-separated form.")),
                ("fellowship_leech_warn_seconds", new Property<string>("600,300,60", "WaffleACE: comma-separated list of seconds-remaining marks at which a fellowship member is privately warned before /fship noleech leech management would eject them. Only the tightest mark crossed since the member's last contribution fires, so one warning is sent per idle window per mark. Empty disables warnings entirely.")),
                ("market_ad_sender_name", new Property<string>("Market Crier", "WaffleACE Market: the name shown as the speaker on the periodic Trade advert line (market_ad_interval_hours). The wire field this fills is a speaker guid slot with no real player behind it - see MarketAdvertiser's doc comment for why that is an open, live-client question. Keep it under 128 characters, matching every other Trade chat sender name")),
                ("market_ad_site_url", new Property<string>("trade.acdreamweave.com", "WaffleACE Market: the web address printed in the periodic Trade advert (market_ad_interval_hours), e.g. 'The Market is open at {this} - sign in with your game account name and password.'")),
                ("pvp_arena_crier_sender_name", new Property<string>("Arena Crier", "PvP Arena Crier: the name shown as the speaker on the periodic /lfg queue announcements and the last-call line. Same wire caveat as market_ad_sender_name - no real player is behind it. Keep it under 128 characters")),
                ("pvp_arena_blood_reset_timezone", new Property<string>("America/New_York", "PvP Arena (Docs/Pvp/DESIGN.md \"Rewards\"): IANA or Windows time zone id whose wall clock defines the arena day for the Mark of the Hopeslayer daily cap (pvp_arena_blood_daily_cap), turning over at pvp_arena_blood_reset_hour. An unknown id falls back to a fixed UTC-5 offset with a logged warning. Defaults America/New_York, matching the Threads survey reset")),
                ("pvp_arena_denylist", new Property<string>("", "PvP Arena (Docs/Pvp/DESIGN.md \"Joining the queue\"): CSV of character ids refused at queue-join time, regardless of every other admission check. Ported from Doctide `arenas_blacklist`. Edit through the settings panel, not an admin command. Empty = nobody denied")),
                ("pvp_template_account", new Property<string>("", "PvP Template Facets (Docs/Pvp/TEMPLATES.md): the account name whose characters /pvptemplate snapshot may read, and whose characters are refused arena admission. Empty refuses every snapshot")),
                ("charsheet_site_url", new Property<string>("char.acdreamweave.com", "WaffleACE Character Sheet: host (no scheme) that sheet links are printed with, e.g. https://{this}/<slug>.")),
                ("ml_digsite_wave_tiers", new Property<string>("1:0.15,2:0.27,3:0.39,4:0.51,5:0.63,6:0.75,7:0.87", "(non-retail function) ML digsite encounters: the Waves shape's payout table, comma-separated wave:fraction pairs, priced for the round-16 fixed 8-wave run. A wave counts once it is CLEARED (its field emptied); the highest wave cleared pays the fraction of the highest row at or below it, plus ml_digsite_checkpoint_bonus per checkpoint kill, times ml_digsite_fail_payout_multiplier. Clearing wave 8 is not a table row at all - it is an outright WIN paying 100% unmultiplied. Before any wave is cleared, a /digsite bail pays nothing and every other end (wipe, wave clock, TTL, unload) pays the first row. A value that parses to nothing falls back to this default")),
                ("ml_digsite_bossrush_mechanic", new Property<string>("ward on=hpbelow trigger=0.5 pcthp=0.2 secs=10", "(non-retail function) ML digsite encounters: monster-effect records (PropertyString 9015 grammar) the Boss Rush boss carries on top of its own authored effects. Default: a one-shot ward absorbing 20 percent of max health for 10 s, raised the first heartbeat the boss is below half health (pcthp is a fraction, not a percent). Empty means no overlay")),
                ("ml_digsite_bossrush_sets", new Property<string>(ACE.Server.MlDigsite.MlDigsiteBossMechanicRules.DefaultSets, "(non-retail function) ML digsite encounters: the Boss Rush mechanic SET TABLE, one record per set, records separated by ';' and tokens within a record whitespace-separated key=value - deliberately the same shape as PropertyString 9015. Tokens: set=<id> main=<mechanic> secondary=<mechanic> addhp=<x> addspeed=<x>. Mechanics are volatile, drums, immunephases, interrupt, safezones, immune50 (immunephases narrowed to 0.50 alone) and none. addhp/addspeed scale the ONE shared add weenie per set. Which set an encounter runs is ROLLED, never keyed to which boss spawned, so sets are reused across bosses once the pool exceeds five. Per-mechanic NUMBERS live in ml_digsite_bossrush_volatile/_drums/_immune/_interrupt/_safezones, not here. A value that parses to nothing falls back to this default")),
                ("ml_digsite_bossrush_volatile", new Property<string>(ACE.Server.MlDigsite.MlDigsiteBossMechanicRules.DefaultVolatile, "(non-retail function) ML digsite encounters: numbers for the Boss Rush 'volatile adds' mechanic, whitespace-separated key=value. every=<seconds between waves of adds> count=<adds per wave> radius=<metres of the detonation> damage=<pre-resistance fire damage> fuse=<seconds between an add dying and its detonation>. The add weenie itself is MlDigsiteRole.Add in MlDigsiteRoster; its health and speed come from the SET's addhp/addspeed")),
                ("ml_digsite_bossrush_drums", new Property<string>(ACE.Server.MlDigsite.MlDigsiteBossMechanicRules.DefaultDrums, "(non-retail function) ML digsite encounters: numbers for the Boss Rush 'drum cadence' mechanic, whitespace-separated key=value. every=<seconds between cadences> beatgap=<seconds between beats> resolve=<seconds from the last beat to the hit> damage=<pre-resistance bludgeon damage> ringradius=<metres, the 1-beat ring> wallwidth=<metres, the 2-beat wall> volleyradius=<metres, each 3-beat mark> volleytargets=<marks placed> burndamage=<Fire damage per tick to each player standing in a Volley mark after it lands> burnseconds=<how long each Volley mark burns> burninterval=<seconds between burn ticks> (round 17: every Volley mark becomes burning ground, 150 every 1 s for 8 s; burndamage=0 or burnseconds=0 turns the burn off). beatgap, resolve and burninterval are quantised to the digsite's 1 s tick; anything finer is not expressible")),
                ("ml_digsite_bossrush_immune", new Property<string>(ACE.Server.MlDigsite.MlDigsiteBossMechanicRules.DefaultImmune, "(non-retail function) ML digsite encounters: numbers for the Boss Rush 'immune phases with adds' mechanic, whitespace-separated key=value. thresholds=<comma-separated health fractions, each in (0,1]> adds=<adds spawned per phase> timeout=<seconds after which the phase ends anyway>. The timeout is a failsafe, not a mechanic: without it one add lost to terrain would hard-lock the fight until the encounter TTL. A slot authored as immune50 ignores thresholds= and uses 0.50 alone")),
                ("ml_digsite_bossrush_interrupt", new Property<string>(ACE.Server.MlDigsite.MlDigsiteBossMechanicRules.DefaultInterrupt, "(non-retail function) ML digsite encounters: numbers for the Boss Rush 'interrupt object' mechanic, whitespace-separated key=value. every=<seconds between windups> window=<seconds a player has to use the object> distance=<metres from the dig the object is placed> damage=<fraction of each nearby player's MAX health the missed hit deals> pulse=<seconds between repeats of the drum's visibility flare while the window is open>. Round 17: window 20 (was 12) and distance 15 (was 30). distance is clamped to [5, ml_digsite_boss_tether_radius - 5]: at or past the tether, running to the object would pull the boss off its leash and the interrupt would become a reset button. DAMAGE UNITS, the same convention every Boss Rush damage= uses: a value AT OR BELOW 1.0 is a FRACTION of each hit player's own max health, and a value above 1.0 is a flat pre-resistance number. The shipped 0.35 is therefore 35 percent of max health, which scales the same on a level 205 and a level 275 boss; the hit is additionally capped at one point short of current health, so it can never kill")),
                ("ml_digsite_bossrush_safezones", new Property<string>(ACE.Server.MlDigsite.MlDigsiteBossMechanicRules.DefaultSafeZones, "(non-retail function) ML digsite encounters: numbers for the Boss Rush 'shifting safe zones' mechanic, whitespace-separated key=value. every=<seconds between cycles> telegraph=<seconds the marks are up before the hit> zones=<safe spots per cycle> saferadius=<metres around a mark that counts as safe> ring=<metres from the dig the marks are placed on> forgiveness=<misses that CANNOT kill> decay=<seconds without a miss that clears one> damage=<the hit a miss takes>. DAMAGE UNITS, the same convention every Boss Rush damage= uses: a value AT OR BELOW 1.0 is a FRACTION of the hit player's own max health, and a value above 1.0 is a flat pre-resistance number. The shipped 1.0 is therefore the player's WHOLE health bar, deliberately - the tester asked for 'room for 2 mistakes on instant-death hits', and a survivable number would make the forgiveness decorative. The first forgiveness misses are capped at one point short of the player's current health by construction, not by tuning; the next one is lethal"))
                );
    }
}
