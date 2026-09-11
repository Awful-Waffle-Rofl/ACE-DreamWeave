using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Timers;

using log4net;

using ACE.Database;

namespace ACE.Server.Managers
{
    public static class PropertyManager
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // caching internally to the server
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<bool>> CachedBooleanSettings = new ConcurrentDictionary<string, ConfigurationEntry<bool>>();
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<long>> CachedLongSettings = new ConcurrentDictionary<string, ConfigurationEntry<long>>();
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<double>> CachedDoubleSettings = new ConcurrentDictionary<string, ConfigurationEntry<double>>();
        private static readonly ConcurrentDictionary<string, ConfigurationEntry<string>> CachedStringSettings = new ConcurrentDictionary<string, ConfigurationEntry<string>>();

        private static Timer _workerThread;

        /// <summary>
        /// Initializes the PropertyManager.
        /// Run this only once per server instance.
        /// </summary>
        /// <param name="loadDefaultValues">Should we use the DefaultPropertyManager to load the default properties for keys?</param>
        public static void Initialize(bool loadDefaultValues = true)
        {
            if (loadDefaultValues)
                DefaultPropertyManager.LoadDefaultProperties();

            LoadPropertiesFromDB();

            if (Program.IsRunningInContainer && !GetString("content_folder").Equals("/ace/Content"))
                ModifyString("content_folder", "/ace/Content");

            _workerThread = new Timer(300000);
            _workerThread.Elapsed += DoWork;
            _workerThread.AutoReset = true;
            _workerThread.Start();
        }


        /// <summary>
        /// Loads the variables from the database directly into the cache.
        /// </summary>
        private static void LoadPropertiesFromDB()
        {
            foreach (var i in DatabaseManager.ShardConfig.GetAllBools())
                CachedBooleanSettings[i.Key] = new ConfigurationEntry<bool>(false, i.Value, i.Description);

            foreach (var i in DatabaseManager.ShardConfig.GetAllLongs())
                CachedLongSettings[i.Key] = new ConfigurationEntry<long>(false, i.Value, i.Description);

            foreach (var i in DatabaseManager.ShardConfig.GetAllDoubles())
                CachedDoubleSettings[i.Key] = new ConfigurationEntry<double>(false, i.Value, i.Description);

            foreach (var i in DatabaseManager.ShardConfig.GetAllStrings())
                CachedStringSettings[i.Key] = new ConfigurationEntry<string>(false, i.Value, i.Description);
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

            if (CachedBooleanSettings.ContainsKey(key))
                CachedBooleanSettings[key].Modify(newVal);
            else
                CachedBooleanSettings[key] = new ConfigurationEntry<bool>(true, newVal, DefaultPropertyManager.DefaultBooleanProperties[key].Description);

            return true;
        }

        public static void ModifyBoolDescription(string key, string description)
        {
            if (CachedBooleanSettings.ContainsKey(key))
                CachedBooleanSettings[key].ModifyDescription(description);
            else
                log.Warn($"Attempted to modify {key} which did not exist in the BOOL cache.");
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

            if (CachedLongSettings.ContainsKey(key))
                CachedLongSettings[key].Modify(newVal);
            else
                CachedLongSettings[key] = new ConfigurationEntry<long>(true, newVal, DefaultPropertyManager.DefaultLongProperties[key].Description);
            return true;
        }

        public static void ModifyLongDescription(string key, string description)
        {
            if (CachedLongSettings.ContainsKey(key))
                CachedLongSettings[key].ModifyDescription(description);
            else
                log.Warn($"Attempted to modify {key} which did not exist in the LONG cache.");
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
            if (CachedDoubleSettings.ContainsKey(key))
                CachedDoubleSettings[key].Modify(newVal);
            else
                CachedDoubleSettings[key] = new ConfigurationEntry<double>(true, newVal, DefaultPropertyManager.DefaultDoubleProperties[key].Description);

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
            if (CachedDoubleSettings.ContainsKey(key))
                CachedDoubleSettings[key].ModifyDescription(description);
            else
                log.Warn($"Attempted to modify the description of {key} which did not exist in the DOUBLE cache.");
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

            if (CachedStringSettings.ContainsKey(key))
                CachedStringSettings[key].Modify(newVal);
            else
                CachedStringSettings[key] = new ConfigurationEntry<string>(true, newVal, DefaultPropertyManager.DefaultStringProperties[key].Description);
            return true;
        }

        public static void ModifyStringDescription(string key, string description)
        {
            if (CachedStringSettings.ContainsKey(key))
                CachedStringSettings[key].ModifyDescription(description);
            else
                log.Warn($"Attempted to modify {key} which did not exist in the STRING cache.");
        }


        /// <summary>
        /// Writes all of the updated boolean values from the cache into the database.
        /// </summary>
        private static void WriteBoolToDB()
        {
            foreach (var i in CachedBooleanSettings.Where(r => r.Value.Modified))
            {
                // this probably should be upsert. This does 2 queries per modified datapoint.
                // perhaps run a transaction to queue all the queries at once.
                if (DatabaseManager.ShardConfig.BoolExists(i.Key))
                    DatabaseManager.ShardConfig.SaveBool(new Database.Models.Shard.ConfigPropertiesBoolean { Key = i.Key, Value = i.Value.Item, Description = i.Value.Description });
                else
                    DatabaseManager.ShardConfig.AddBool(i.Key, i.Value.Item, i.Value.Description);
            }
        }

        /// <summary>
        /// Writes all of the updated integer values from the cache into the database.
        /// </summary>
        private static void WriteLongToDB()
        {
            foreach (var i in CachedLongSettings.Where(r => r.Value.Modified))
            {
                // todo: see boolean section for caveat in this approach
                if (DatabaseManager.ShardConfig.LongExists(i.Key))
                    DatabaseManager.ShardConfig.SaveLong(new Database.Models.Shard.ConfigPropertiesLong { Key = i.Key, Value = i.Value.Item, Description = i.Value.Description });
                else
                    DatabaseManager.ShardConfig.AddLong(i.Key, i.Value.Item, i.Value.Description);
            }
        }

        /// <summary>
        /// Writes all of the updated float values from the cache into the database.
        /// </summary>
        private static void WriteDoubleToDB()
        {
            foreach (var i in CachedDoubleSettings.Where(r => r.Value.Modified))
            {
                // todo: see boolean section for caveat in this approach
                if (DatabaseManager.ShardConfig.DoubleExists(i.Key))
                    DatabaseManager.ShardConfig.SaveDouble(new Database.Models.Shard.ConfigPropertiesDouble { Key = i.Key, Value = i.Value.Item, Description = i.Value.Description });
                else
                    DatabaseManager.ShardConfig.AddDouble(i.Key, i.Value.Item, i.Value.Description);
            }
        }

        /// <summary>
        /// Writes all of the updated string values from the cache into the database.
        /// </summary>
        private static void WriteStringToDB()
        {
            foreach (var i in CachedStringSettings.Where(r => r.Value.Modified))
            {
                // todo: see boolean section for caveat in this approach
                if (DatabaseManager.ShardConfig.StringExists(i.Key))
                    DatabaseManager.ShardConfig.SaveString(new Database.Models.Shard.ConfigPropertiesString { Key = i.Key, Value = i.Value.Item, Description = i.Value.Description });
                else
                    DatabaseManager.ShardConfig.AddString(i.Key, i.Value.Item, i.Value.Description);
            }
        }

        private static void DoWork(Object source, ElapsedEventArgs e)
        {
            var startTime = DateTime.UtcNow;

            // first, check for variables updated on the server-side. Write those to the DB.
            // then, compare variables to DB and update from DB as necessary. (needs to minimize r/w)

            WriteBoolToDB();
            WriteLongToDB();
            WriteDoubleToDB();
            WriteStringToDB();

            // next, we need to fetch all of the variables from the DB and compare them quickly.
            LoadPropertiesFromDB();

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
                    var defaultText = item.Value.Item.ToString();
                    var currentText = SafeCurrent(item.Key, defaultText, k => GetBool(k).Item.ToString());
                    rows.Add((item.Key, "bool", currentText, defaultText, item.Value.Description));
                }
            }

            foreach (var item in DefaultPropertyManager.DefaultLongProperties)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var defaultText = item.Value.Item.ToString();
                    var currentText = SafeCurrent(item.Key, defaultText, k => GetLong(k).Item.ToString());
                    rows.Add((item.Key, "long", currentText, defaultText, item.Value.Description));
                }
            }

            foreach (var item in DefaultPropertyManager.DefaultDoubleProperties)
            {
                if (item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var defaultText = item.Value.Item.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
                    var currentText = SafeCurrent(item.Key, defaultText, k => GetDouble(k).Item.ToString("G", System.Globalization.CultureInfo.InvariantCulture));
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
        }

        public void ModifyDescription(string description)
        {
            Description = description;
            Modified = true;
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
                ("emote_give_preflight", new Property<bool>(false, "(non-retail) refuse an NPC turn-in up front when the forecast reward would not fit, instead of consuming the item and losing the reward")),
                ("fast_tick_all_players", new Property<bool>(false, "WaffleACE: if TRUE, every player runs the full server-side physics simulation (Player.FastTick) that upstream ACE reserves for PK/PKLite players. This is what gives PKs faster spell release (no legacy pre-windup rotate delay), retail fast-chug, melee stick-to-target and physics-driven jump validation. Costs more CPU per moving player. Defaults FALSE. Read live, so flipping it mid-session takes effect without a relog; a player mid-cast or mid-drink at the moment of the flip may have that one action glitch.")),
                ("fastbuff", new Property<bool>(true, "If TRUE, enables the fast buffing trick from retail.")),
                ("fellow_busy_no_recruit", new Property<bool>(true, "if FALSE, fellows can be recruited while they are busy, different from retail")),
                ("fellow_kt_killer", new Property<bool>(true, "if FALSE, fellowship kill tasks will share with the fellowship, even if the killer doesn't have the quest")),
                ("fellow_kt_landblock", new Property<bool>(false, "if TRUE, fellowship kill tasks will share with landblock range (192 distance radius, or entire dungeon)")),
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
                ("ip_limit_enabled", new Property<bool>(false, "WaffleACE: master switch for the IP-based active-player limit. When TRUE, a non-exempt client IP may have at most ip_limit_max_free characters in-world outside the mule_landblocks set, plus up to ip_limit_max_confined additional characters as long as each of those is inside a mule landblock. Exempt accounts (ip_limit_exempt_accounts, ip_limit_exempt_access_level, loopback addresses, and the existing AllowUnlimitedSessionsFromIPAddresses allowlist) bypass the whole check. Defaults FALSE - this is a non-retail anti-multibox measure, not part of shipped behavior until enabled")),
                ("item_dispel", new Property<bool>(false, "if enabled, allows players to dispel items. defaults to end of retail, where item dispels could only target creatures")),
                ("lifestone_broadcast_death", new Property<bool>(true, "if true, player deaths are additionally broadcast to other players standing near the destination lifestone")),
                ("loot_quality_mod", new Property<bool>(true, "if FALSE then the loot quality modifier of a Death Treasure profile does not affect loot generation")),
                ("market_allow_free_listings", new Property<bool>(true, "WaffleACE Market: whether a listing may carry a price of 0, which gives the item away to whoever claims it first. Defaults TRUE. Turning it off makes MarketManager.List refuse a zero price with invalid_price exactly as it did before giveaways existed; it does NOT close or alter listings already sitting at zero, so flipping it off leaves existing giveaways claimable. Negative prices are refused regardless of this setting")),
                ("market_buy_orders_enabled", new Property<bool>(false, "WaffleACE Market: whether players may PLACE and FILL Wanted buy orders (Docs/Market/WANTED-DESIGN.md). Cancelling an order, the expiry pass and boot recovery keep running with this off, so escrow is always recoverable. Ships OFF; flip on stage first")),
                ("market_enabled", new Property<bool>(false, "WaffleACE Market: the runtime kill switch. Config.js's Market.Enabled decides whether the API host STARTS at all; this decides whether MarketManager accepts listings, delistings and purchases, and can be flipped live without a restart. Both must be true for the market to work")),
                ("market_snapshot_backfill_on_start", new Property<bool>(true, "WaffleACE Market: whether the server repairs out-of-date listing snapshots by itself shortly after a world start. Defaults TRUE. It re-projects only ACTIVE listings whose stored snapshot predates MarketSnapshot.CurrentVersion, so a restart with nothing out of date does no work at all; it writes only the snapshot column and never status, count or custody, and it refuses any projection that comes out worse than what is stored. Turning it off leaves /marketbackfill run as the only way to repair legacy listings. The flag is read once, on the backfill's own background thread, so flipping it takes effect at the next world start rather than mid-pass")),
                ("market_reject_log_enabled", new Property<bool>(true, "WaffleACE Market: whether refused listing, delist and purchase attempts are recorded to market_rejected_attempt. Defaults TRUE - without these rows a player dispute of the form 'I tried to buy this and it failed' leaves no trace anywhere. Turning it off stops new rows being enqueued; it does not delete existing ones, and it does not affect market_transaction, which records everything that got as far as a Pending row. The flag is read on the audit writer's own background thread, never on the request path")),
                ("monster_conditional_spell_selection", new Property<bool>(ACE.Server.Entity.MonsterSpellSelector.DefaultConditionalSelectionEnabled, "(non-retail function) WaffleACE: whether a caster monster picks WHICH spell to cast by looking at what its target is already carrying. With this on, the spell book is walked in three passes instead of one - a debuff the target lacks (an elemental Vulnerability of that element, or an Imperil) and a damaging spell whose element the target is already vulnerable to go first, a debuff the target already carries at equal or greater strength goes last, everything else sits between. HOW OFTEN a monster casts is unchanged: no candidate is ever dropped, every book entry is still rolled exactly once at exactly its authored probability, and the chance of casting nothing at all is a product over the whole book and so is order-independent. Nothing about mana, animation, range or resist math is touched. Falls back to the flat single pass whenever there is no live target in the same landblock group. False reproduces the previous behaviour exactly")),
                ("mule_form_enabled", new Property<bool>(true, "WaffleACE: whether an account's earned Beast Effigy form is applied to its summoned /mule vendor. Gates the APPEARANCE SUBSTITUTION ONLY - tokens still attune, still fill on kills, and a completed token still saves the look with this off; the look simply shows once it is back on. Turning it off is therefore always safe and never loses a player's progress")),
                ("mule_system_enabled", new Property<bool>(true, "WaffleACE: master switch for the mule system. Gates ONLY the conversion path (the NPC emote / MuleConversion.HandleMuleRequest). The restriction guards always apply to any character already flagged IsMule regardless of this switch, so turning it off cannot unlock existing mules - see Player_Mule")),
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
                ("rares_real_time_v2", new Property<bool>(false, "chances for a rare to be generated on rare eligible kills are modified by the last time one was found per each player, rares_max_days_between defines maximum days before guaranteed rare generation")),
                ("runrate_add_hooks", new Property<bool>(false, "if TRUE, adds some runrate hooks that were missing from retail (exhaustion done, raise skill/attribute")),
                ("reportbug_enabled", new Property<bool>(false, "toggles the /reportbug player command")),
                ("require_spell_comps", new Property<bool>(true, "if FALSE, spell components are no longer required to be in inventory to cast spells. defaults to enabled, as in retail")),
                ("safe_spell_comps", new Property<bool>(false, "if TRUE, disables spell component burning for everyone")),
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
                ("facet_enabled", new Property<bool>(false, "Player Facets: master switch for the /facet command family and every behaviour it drives. Defaults FALSE - turn it on only once the feature has been verified in game. While false /facet refuses with a plain 'not available' line rather than being hidden, so a player who was told about it gets an answer")),
                ("version_info_enabled", new Property<bool>(false, "toggles the /aceversion player command")),
                ("vendor_shop_uses_generator", new Property<bool>(false, "enables or disables vendors using generator system in addition to createlist to create artificial scarcity")),
                ("world_closed", new Property<bool>(false, "enable this to startup world as a closed to players world")),
                ("dynamic_dungeons_enabled", new Property<bool>(false, "(non-retail function) master switch for Threads; a Thread Gem refuses while false and /dd give still works for testing")),
                ("dynamic_dungeons_press_enabled", new Property<bool>(false, "(non-retail function) Threads: the Fragment Press accepts raw fragments and re-opens unbound gems; independent of dynamic_dungeons_enabled")),
                ("dynamic_dungeons_press_dose_log", new Property<bool>(true, "(non-retail function) Threads: the Fragment Press echoes a per-dose result line to the pressing player's chat, in addition to the always-shown pressed line and finished-gem summary; can reach 19 lines on a full pressing")),
                ("dynamic_dungeons_survey_level_scaling", new Property<bool>(true, "(non-retail function) Threads: the Survey-Archivist scales each daily-survey reward by the average GEM LEVEL of the newest surveys the tier covers, drawn from a rolling depth-10 ring. False pins the ratio to 1.0, paying the unscaled caps the emote rig used - a live rollback with no redeploy")),
                ("dynamic_dungeons_reward_scaling", new Property<bool>(true, "(non-retail function) Threads: master switch for the gem-level reward-scale curve (dynamic_dungeons_reward_scale_anchor/exponent/floor/cap) applied to every run's XP scale, luminance scale, loot quantity, loot quality bonus, and salvage-affinity chance. False pins the ratio to 1.0, reproducing pre-scaling behaviour exactly - a live rollback with no redeploy")),
                ("dynamic_dungeons_uplift_rename", new Property<bool>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultUpliftRename, "(non-retail function) Threads: prefix an adaptive-band uplifted creature's name with 'Threadbound ' so it is not mistaken for its base form in the examine panel. A creature reached by the band's downward extension has had its level, skills, melee damage, body armour and spell tier raised to the band standard, and the examine panel is the only place a player can see that before the fight. False leaves every name untouched")),
                ("dynamic_dungeons_boss_chest_enabled", new Property<bool>(true, "(non-retail function) Threads: a Thread Cache (wcid 1003603) forms at the boss's authored spawn anchor when the run boss dies, holding a Trade Note stack scaled by gem level plus dynamic_dungeons_boss_chest_loot_count rolls of the run's own loot profile. Only the run owner can open it, and it dies with the copy. False leaves a boss death exactly as it was")),
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
                ("world_events_participation_payout_enabled", new Property<bool>(true, "(non-retail function) World Events reward claim: a claimant with no damage or kill credit on the run receives the reward profile's coalWcid booby prize instead of the outcome crate. False restores the pre-change behaviour where every claimant gets the same crate. Inert for a reward profile whose coalWcid is 0"))
                );

        public static readonly ReadOnlyDictionary<string, Property<long>> DefaultLongProperties =
            DictOf(
                ("account_vault_entry_cap", new Property<long>(500, "WaffleACE Mule Vendor: maximum ENTRIES an account may store, where one entry is one stored biota OR one collapsed-stackable ledger row - never the number of units held. 10,000 plain healing kits are ONE entry; ten individually tinkered arrows are ten. Enforced at deposit only and NEVER retroactively: lowering it below an account's existing holdings refuses further deposits but leaves everything withdrawable. This number also equals the number of lines the client vendor panel is asked to render, which is what live test 17.1 measures - set it from that result")),
                ("account_vault_landblock", new Property<long>(0x7FFF, "WaffleACE Mule Vendor: landblock id given as the Location of every vault CONTAINER biota. This landblock is never activated for players and holds no content. Its only purpose is risk R1: PurgeOrphanedBiotas keeps a biota only if it has a Container, Wielder or Location pointer, and a vault container has none of the first two. The purge keep-test exemption is the redundant second mitigation - both must exist, because a content edit can undo this one and an upstream merge can undo that one")),
                ("account_vault_summon_rot_seconds", new Property<long>(600, "WaffleACE Mule Vendor: seconds a summoned mule vendor survives without interaction. Refreshed on every interaction. One vendor per summoner; re-summoning destroys the old one")),
                ("alt_character_bonus_gap", new Property<long>(5, "Alt character bonus: minimum progression gap (1 level = 1 point at equal enlightenment) a character must be behind its account's furthest-along character before the bonus applies. 5 = the bonus is active only while the character is 5 or more levels behind. 0 restores the old behaviour of applying at any nonzero gap.")),
                ("char_delete_time", new Property<long>(3600, "the amount of time in seconds a deleted character can be restored")),
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
                ("class_ability_resonance_max_stacks", new Property<long>(5, "class abilities: Resonance - maximum stacks held at once (5, so the full ramp is +10/15/20% magic damage by rank)")),
                ("class_ability_pocketsand_puff_script", new Property<long>(0x330008B2, "class abilities: Pocket Sand - raw 0x33 PhysicsScript DataID played on the blinded creature at proc time (default 0x330008B2 = 855640242, a brown two-emitter splatter burst at the torso, owner pick 2026-08-17; renders in Content/preview/pocket_sand_vfx/). 0 = no puff, only the Dirty Fighting head marker")),
                ("class_ability_surefooted_stack_cap", new Property<long>(5, "class abilities: Surefooted - most stacks a player may hold. Rank-invariant on purpose: rank buys a bigger bonus per stack, not a deeper pool, so the ramp to full takes the same five evades at every rank. The other two Surefooted tunables are doubles and live in DefaultDoubleProperties")),
                ("class_ability_spellsurge_max_stacks", new Property<long>(5, "class abilities: Spellsurge - maximum stacks held at once (5, so the full ramp is +10 percentage points of war-proc chance). THE term that bounds the self-reinforcing loop")),
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
                ("ip_limit_max_free", new Property<long>(1, "WaffleACE: IP active-player limit - maximum characters from one non-exempt IP that may be in-world OUTSIDE the mule_landblocks set. Only takes effect while ip_limit_enabled is TRUE")),
                ("ip_limit_max_confined", new Property<long>(2, "WaffleACE: IP active-player limit - ADDITIONAL characters from one non-exempt IP allowed beyond ip_limit_max_free, each of which must currently be inside a mule landblock (mule_landblocks). The total in-world cap for an IP is ip_limit_max_free + ip_limit_max_confined. Only takes effect while ip_limit_enabled is TRUE")),
                ("ip_limit_exempt_access_level", new Property<long>(2, "WaffleACE: IP active-player limit - accounts whose access level is at or above this value (2 = AccessLevel.Sentinel) are automatically exempt from the limit. Set to -1 to disable auto-exemption by access level entirely")),
                ("ip_limit_grace_seconds", new Property<long>(30, "WaffleACE: IP active-player limit - seconds of warning grace given to a character that comes to violate the limit after login (e.g. it walks out of a mule landblock) before it is logged off")),
                ("ip_limit_sweep_seconds", new Property<long>(10, "WaffleACE: IP active-player limit - how often, in seconds, the periodic re-check sweep runs to catch characters that have moved into or out of mule_landblocks since login")),
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
                ("teleport_visibility_fix", new Property<long>(0, "Fixes some possible issues with invisible players and mobs. 0 = default / disabled, 1 = players only, 2 = creatures, 3 = all world objects")),
                ("dynamic_dungeons_max_concurrent_runs", new Property<long>(16, "(non-retail function) Threads: server-wide cap on live runs, a load safety valve; 0 = unlimited")),
                ("dynamic_dungeons_max_runs_per_account", new Property<long>(1, "(non-retail function) Threads: live runs one account may hold at once; 0 = unlimited")),
                ("dynamic_dungeons_run_ttl_minutes", new Property<long>(180, "(non-retail function) Threads: minutes from gem use until the run is reaped regardless of state; minimum 5")),
                ("dynamic_dungeons_loot_tier_cap", new Property<long>(8, "(non-retail function) Threads: highest TreasureDeath tier a gem may roll into a run's loot profile; gear ratings only roll at 8")),
                ("dynamic_dungeons_max_monsters_per_run", new Property<long>(120, "(non-retail function) Threads: hard cap on non-boss creatures placed in one run")),
                ("dynamic_dungeons_spawn_batch_size", new Property<long>(20, "(non-retail function) Threads: creatures placed per landblock action-queue step while populating")),
                ("dynamic_dungeons_boss_chest_loot_count", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultBossCacheLootCount, "(non-retail function) Threads: loot rolls a boss cache takes before the run's own loot-quantity multiplier is applied; the cache holds ceil(this * that). Rolled from the RUN's stamped loot profile, so the tier and quality are the same ones its creatures dropped at. Capped at 100 items after scaling; 0 leaves a cache holding only its Trade Notes")),
                ("dynamic_dungeons_boss_chest_mmd_low_level", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdLowLevel, "(non-retail function) Threads: the LOW anchor gem level of the boss cache's Trade Note line. Below it the stack top is pinned at dynamic_dungeons_boss_chest_mmd_low_max rather than falling further")),
                ("dynamic_dungeons_boss_chest_mmd_low_max", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdLowMax, "(non-retail function) Threads: the boss cache's Trade Note stack TOP at the low anchor level, and the floor of that top at every level below it. The stack itself is a uniform 1..top")),
                ("dynamic_dungeons_boss_chest_mmd_high_level", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdHighLevel, "(non-retail function) Threads: the HIGH anchor gem level of the boss cache's Trade Note line. Set to 300 while the gem ceiling was 275; the line is NOT capped there, so the 2026-09-09 raise to 375 keeps the note stack scaling past the anchor instead of flattening")),
                ("dynamic_dungeons_boss_chest_mmd_high_max", new Property<long>(ACE.Server.ThreadDungeons.ThreadDungeonRewardSpawner.DefaultMmdHighMax, "(non-retail function) Threads: the boss cache's Trade Note stack TOP at the high anchor level. Scaled by gem LEVEL alone - never by the loot-quantity multiplier or the gem-level reward-scale ratio, both of which already move the cache's loot half")),
                ("dynamic_dungeons_survey_min_level", new Property<long>(185, "(non-retail function) Threads: minimum gem (monster) level for a cleared run to count as a filed survey (owner ruling B3)")),
                ("dynamic_dungeons_survey_reference_level", new Property<long>(ACE.Server.ThreadDungeons.DungeonGemSpec.MaxLevel, "(non-retail function) Threads: the level the daily-survey reward curve measures a player's run levels against - the denominator of (avgGemLevel / this)^dynamic_dungeons_survey_ratio_exponent, and the level whose character-XP delta becomes the award's cap base. Defaults to DungeonGemSpec.MaxLevel, so raising the gem ceiling rescales the payout with it (owner ruling R6); pinning it BELOW the ceiling keeps the pre-raise payout curve instead. Clamped at read into the character XP chart's own index range, since it is used as an index into that chart")),
                ("dynamic_dungeons_min_player_level", new Property<long>(150, "(non-retail function) Threads: minimum player level to use any dungeon gem")),
                ("dynamic_dungeons_press_max_mods", new Property<long>(4, "(non-retail function) Threads: most MONSTER/BOSS modifiers a pressed gem may carry - the four taper slots' shared budget. The bonus (herb) and salvage affinity (powder) budgets are 1 each and are compiled constants rather than tunables, because each is the arithmetic consequence of there being one such slot on the press board (RawFragmentRules.SlotCounts)")),
                ("dynamic_dungeons_press_fee_notes", new Property<long>(1, "(non-retail function) Threads: Trade Notes charged per pressing; re-opening is free")),
                ("dynamic_dungeons_boss_damage_rating_floor", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossDamageRatingFloor, "(non-retail function) Threads: how far a run boss's DamageRating must exceed the pack's; a FLOOR, so a gem's own boss modifiers win when they are larger. 0 disables the axis; a negative value reads as the default")),
                ("dynamic_dungeons_boss_damage_resist_floor", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossDamageResistFloor, "(non-retail function) Threads: how far a run boss's DamageResistRating must exceed the pack's; a FLOOR, so a gem's own boss modifiers win when they are larger. 0 disables the axis; a negative value reads as the default")),
                ("dynamic_dungeons_min_eligible_families", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultMinEligibleFamilies, "(non-retail function) Threads: how many species tables must be able to field an in-band trash member before the roster band stops widening its LOW edge (down to dynamic_dungeons_band_low_floor). Families rather than members, because family count is what a player experiences as variety across runs. 0 disables family widening; a negative value reads as the default")),
                ("dynamic_dungeons_min_family_pool", new Property<long>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultMinFamilyPool, "(non-retail function) Threads: how many DISTINCT trash wcids the CHOSEN family's pool must hold before its own band stops widening its LOW edge. Family eligibility only needs one in-band member, so without this a run can pass the family threshold and still stand copies of a single wcid on every spawn point. 0 disables per-family widening; a negative value reads as the default")),
                ("world_events_announce_chat_type", new Property<long>(0x05, "(non-retail function) ChatMessageType used for World Events global announcements; 5=magenta/System, 18=orange, 13=teal")),
                ("world_events_boss_damage_rating_max", new Property<long>(300, "(non-retail function) World Events: upper bound on the DamageRating the boss damage controller may set. A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_damage_rating_min", new Property<long>(-80, "(non-retail function) World Events: lower bound on the DamageRating the boss damage controller may set. Negative is meaningful and is the direction taken when a boss is hitting too hard - rating -80 is a 0.56x damage multiplier (Creature.GetNegativeRatingMod, 100/(100+80)). A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_damage_sample_hits", new Property<long>(4, "(non-retail function) World Events: how many NON-CRIT hits the boss must land on counted participants before the damage controller will propose an adjustment. Crits are excluded from the mean because the requirement is about an ordinary hit; they are counted separately in the log. A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_damage_step_cap", new Property<long>(50, "(non-retail function) World Events: the largest DamageRating CHANGE one boss damage adjustment may make. The FIRST adjustment of a run is exempt, so a badly-seeded boss converges in one step; every later one is capped so a single unlucky sample window cannot swing the fight. A real default, not the 0-means-JSON convention the wave dials use")),
                ("world_events_boss_throughput_cap_health", new Property<long>(0, "(non-retail function) World Events: overrides bosses.json.throughputCapHealth for every boss when positive; 0 or negative = use the JSON value. The absolute health ceiling on the C16 throughput sizing path")),
                ("world_events_boss_throughput_floor_health", new Property<long>(0, "(non-retail function) World Events: overrides bosses.json.throughputFloorHealth for every boss when positive; 0 or negative = use the JSON value. The health a boss is re-based to before the throughput multiplier, and the per-boss switch that turns the throughput sizing path on at all - setting this here FORCE-ENABLES throughput sizing (subject to world_events_boss_throughput_scaling_enabled) even for a boss authored with throughputFloorHealth 0, i.e. one whose own bosses.json entry never opted in. That is intended, not a bug: every shipped boss already carries 100000 there, so in practice this override only matters for a boss a content author deliberately left at 0")),
                ("world_events_cache_wcid", new Property<long>(0, "(non-retail function) overrides the World Events reward cache weenie; 0 = use the reward profile's cacheWcid")),
                ("world_events_claim_window_seconds", new Property<long>(300, "(non-retail function) World Events reward claim window in seconds; rewards.json claimWindowSeconds wins when present")),
                ("world_events_crowd_health_start_at", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.crowdHealth.startAt for every source when positive; 0 or negative = use the JSON value. Participant count at which the crowd health multiplier starts to move")),
                ("world_events_family_floor_level", new Property<long>(50, "(non-retail function) World Events: a randomly composed run's FIRST family must have a catalog member at or below this level, so the pair always reaches down to a low-level crowd; an explicit --family bypasses it. A non-positive value falls back to the built-in default")),
                ("world_events_max_alive", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.maxAlive for every source when positive; 0 or negative = use the JSON value. Wave-pressure ceiling a source's live trash/elites are budgeted against")),
                ("world_events_overflow_per_champion", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.overflowPerChampion for every source when positive; 0 or negative = use the JSON value. How much unplaceable wave demand buys one overflow champion")),
                ("world_events_pace_quantity_step", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.pace.quantityStep for every source when positive; 0 or negative = use the JSON value. How many extra creatures one pace-controller speed-up step adds to the next wave pick")),
                ("world_events_progress_interval_seconds", new Property<long>(120, "(non-retail function) World Events: seconds between global progress announcements while a run is Active; 0 disables them")),
                ("world_events_teaser_lead_seconds", new Property<long>(180, "(non-retail function) seconds of teaser warning broadcast before a World Event stages; 0 disables the teaser. --teaser on /worldevent start overrides it for one run")),
                ("world_events_wave_count_base", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.waveCount.base for every source when positive; 0 or negative = use the JSON value. The floor of the per-wave trash count formula")),
                ("world_events_wave_count_cap", new Property<long>(0, "(non-retail function) World Events: overrides sources.json.waveCount.cap for every source when positive; 0 or negative = use the JSON value. The ceiling of the per-wave trash count formula")),
                // Monster combat effects: the COUNT caps of the monster_effect_* block. The fractional and
                // multiplier caps of the same block are doubles and live in DefaultDoubleProperties below.
                ("monster_effect_ramp_stack_cap", new Property<long>(20, "monster effects: maximum stacks any one ramp record may hold, whatever its own max= arg says")),
                ("monster_effect_max_per_monster", new Property<long>(12, "monster effects: maximum effects one monster may carry. An authored list longer than this is truncated at creature construction with a warning naming the wcid")),
                ("monster_effect_recast_cap", new Property<long>(2, "monster effects: maximum extra casts one completed cast may chain into, bounding a recast record that would otherwise re-enter itself"))
                );

        public static readonly ReadOnlyDictionary<string, Property<double>> DefaultDoubleProperties =
            DictOf(

                ("account_vault_summon_leash", new Property<double>(30.0, "WaffleACE Mule Vendor: metres from the summoner beyond which a summoned mule vendor despawns, checked on a slow tick. Mirrors the passive pet leash")),
                ("anti_blink_door_width", new Property<double>(3.0, "(non-retail function) Width in units of the blocking plane anti-blink builds across a closed door, centered on the door's origin and perpendicular to its facing. Raise for wide gates, lower if legitimate movement beside a door is being rejected")),
                ("anti_blink_z_height_limit", new Property<double>(2.0, "(non-retail function) Anti-blink ignores closed doors whose Z differs from the player's by more than this, so a door on the storey above or below is not tested against the path. 2.0 replaces the ported 3.5 after a stage false positive on 2026-09-01: a player on a Bluespire upper floor 2.8 above a closed ground-floor door was rubber-banded. A door origin sits at floor level and a jump clears about 1, so 2.0 still catches a blink through the door itself")),
                ("allegiance_passup_first_vassal", new Property<double>(0.25, "DreamWeave allegiance passup: share of a vassal's earned XP passed up to their patron when that patron holds exactly ONE vassal. Passup is diminishing - the per-vassal share shrinks as vassals are added - so this is the high-water mark for a single vassal, not a flat rate. XP only; luminance never passes up.")),
                ("allegiance_passup_max_total", new Property<double>(1.0, "DreamWeave allegiance passup: combined share a patron receives across ALL of their vassals once they hold the maximum number of direct vassals. 1.0 = a full stable of vassals is worth one extra vassal's XP in total. Together with allegiance_passup_first_vassal this fixes the diminishing curve; the grandpatron always receives a fifth of whatever the patron receives.")),
                ("cantrip_drop_rate", new Property<double>(1.0, "Scales the chance for cantrips to drop in each tier. Defaults to 1.0, as per end of retail")),
                ("cloak_cooldown_seconds", new Property<double>(5.0, "The number of seconds between possible cloak procs.")),
                ("cloak_max_proc_base", new Property<double>(0.25, "The max proc chance of a cloak.")),
                ("cloak_max_proc_damage_percentage", new Property<double>(0.30, "The damage percentage at which cloak proc chance plateaus.")),
                ("cloak_min_proc", new Property<double>(0, "The minimum proc chance of a cloak.")),
                ("class_ability_multishot_damage_mult", new Property<double>(0.75, "class abilities: damage multiplier for extra arrows granted by the Multishot class ability (weapon-granted extra arrows use the weapon's own multiplier)")),
                ("class_ability_thorns_percent_per_rank", new Property<double>(0.05, "class abilities: fraction of the equipped shield's effective armor level that the Thorns class ability reflects back per rank")),
                ("class_ability_poisonweapon_damage_per_rank", new Property<double>(5.0, "class abilities: flat bonus damage per rank added by the Poison Weapon class ability to every landed weapon hit against monsters")),
                ("class_ability_taunt_radius", new Property<double>(15.0, "class abilities: radius (in units) around the player affected by the Taunt class ability")),
                ("class_ability_taunt_duration", new Property<double>(10.0, "class abilities: how long (in seconds) monsters stay forced onto the taunter")),
                ("class_ability_taunt_loyalty_per_trained", new Property<double>(25.0, "class abilities: points of effective Loyalty per +1 second added to Taunt's hold duration, Trained source")),
                ("class_ability_taunt_loyalty_per_spec", new Property<double>(18.0, "class abilities: points of effective Loyalty per +1 second added to Taunt's hold duration, Specialized source")),
                ("class_ability_taunt_loyalty_bonus_cap_seconds", new Property<double>(10.0, "class abilities: hard cap on the extra seconds the Loyalty rider can add to Taunt's hold duration")),
                ("class_ability_battlehardened_reduction_per_strength", new Property<double>(0.0005, "class abilities: fraction of all incoming damage reduced per point of Strength by the Battle Hardened class ability (0.0005 = 0.05% per point)")),
                ("class_ability_battlehardened_max_reduction", new Property<double>(0.5, "class abilities: hard cap on the total fraction of incoming damage the Battle Hardened class ability can reduce, regardless of Strength")),
                ("class_ability_frenzy_percent_per_stack", new Property<double>(0.05, "class abilities: attack-speed increase per Frenzy stack (0.05 = +5% per stack; nominal +50% at 10 stacks no longer saturates the raised anim ceiling)")),
                ("class_ability_frenzy_expire_seconds", new Property<double>(10.0, "class abilities: seconds without landing a weapon hit before Frenzy stacks reset")),
                // PER-STACK rider: the divisor is scaled by the rank-3 stack cap (10) so the rider's TOTAL
                // contribution at a maxed source is comparable to a flat rider like Deadeye's, not 10x it.
                ("class_ability_frenzy_reckless_per_trained", new Property<double>(250.0, "class abilities: points of effective Recklessness per +1% Frenzy per-stack attack speed, Trained source (per-stack: multiply by up to 10 stacks for the total)")),
                ("class_ability_frenzy_reckless_per_spec", new Property<double>(185.0, "class abilities: points of effective Recklessness per +1% Frenzy per-stack attack speed, Specialized source (per-stack: multiply by up to 10 stacks for the total)")),
                ("class_ability_attack_speed_ceiling", new Property<double>(4.5, "class abilities: hard ceiling on buffed attack animation speed when class abilities push past the normal 2.0x cap (raised 3.5 -> 4.5 on 2026-07-30: Frenzy and Attack Speed compose MULTIPLICATIVELY in GetAnimSpeed and both carry skill riders, so the deepest ability-only cross reaches 4.08 anim at trained riders and 4.32 specialized - it was being clipped at 3.5. At 4.5 the specialized cross plus a 4% weapon-mod attack-speed roll fits at 4.46, while gear stacking still clips around 5.15, which is deliberate. This is the only lever on the axis: the 2.0 base is MaxAttackSpeed, a private static)")),
                ("class_ability_netherrush_percent_per_rank", new Property<double>(0.05, "class abilities: void-cast-speed increase per Nether Rush stack, per rank (0.05 = +5%/stack at rank 1, +15%/stack at rank 3)")),
                ("class_ability_netherrush_expire_seconds", new Property<double>(20.0, "class abilities: seconds without casting a void spell before Nether Rush stacks reset")),
                // PER-STACK rider, and the cast-speed axis has no ceiling today (unlike attack speed), so
                // this divisor is scaled by the 5-stack cap AND kept deliberately tight. Nether Rush's base
                // +75% is already the largest built multiplier in the system; the rider must not double it.
                ("class_ability_netherrush_arcanelore_per_trained", new Property<double>(125.0, "class abilities: points of effective Arcane Lore per +1% Nether Rush per-stack cast speed, Trained source (per-stack: multiply by up to 5 stacks for the total)")),
                ("class_ability_netherrush_arcanelore_per_spec", new Property<double>(92.0, "class abilities: points of effective Arcane Lore per +1% Nether Rush per-stack cast speed, Specialized source (per-stack: multiply by up to 5 stacks for the total)")),
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
                ("class_ability_spellaoe_manaconv_per_trained", new Property<double>(40.0, "class abilities: points of effective Mana Conversion per +1% Spell AOE radiated-damage fraction, Trained source; larger = weaker (doubled from 20 when Spell AOE became a 3-rank ladder, so the rank curve carries the growth and the rider stays a garnish)")),
                ("class_ability_spellaoe_manaconv_per_spec", new Property<double>(30.0, "class abilities: points of effective Mana Conversion per +1% Spell AOE radiated-damage fraction, Specialized source (tighter dual-ratio divisor); doubled from 15 alongside the Trained divisor")),
                ("class_ability_spellaoe_mana_surcharge", new Property<double>(1.0, "class abilities: extra mana cost fraction added to a qualifying Arc war cast when Spell AOE is learned (1.0 = +100%); FLAT - it does not scale with rank, so rank 1 pays the full surcharge for the smallest blast. Stacks additively with class_ability_overchannel_mana_surcharge")),
                ("class_ability_poisonweapon_alchemy_per_trained", new Property<double>(12.0, "class abilities: points of effective Alchemy (above the threshold) per +1 flat Poison Weapon damage, Trained source; smaller = stronger")),
                ("class_ability_poisonweapon_alchemy_per_spec", new Property<double>(9.0, "class abilities: points of effective Alchemy (above the threshold) per +1 flat Poison Weapon damage, Specialized source (tighter dual-ratio divisor)")),
                ("class_ability_poisonweapon_alchemy_threshold", new Property<double>(100.0, "class abilities: effective Alchemy below this contributes nothing to Poison Weapon's flat bonus (back-loads the curve to the late game)")),
                ("class_ability_poisonweapon_proc_delay", new Property<double>(0.1, "class abilities: seconds the Poison Weapon proc is deferred after the weapon strike so its damage + combat line land just after the main hit (0 = next tick)")),
                ("class_ability_multishot_assess_per_trained", new Property<double>(20.0, "class abilities: points of effective Assess Creature per +1% Multishot volley damage, Trained source; smaller = stronger")),
                ("class_ability_multishot_assess_per_spec", new Property<double>(15.0, "class abilities: points of effective Assess Creature per +1% Multishot volley damage, Specialized source (tighter dual-ratio divisor)")),
                ("class_ability_thorns_shield_per_trained", new Property<double>(50.0, "class abilities: points of effective Shield per +1% of shield armor level added to the Thorns reflect fraction, Trained source; smaller = stronger")),
                ("class_ability_thorns_shield_per_spec", new Property<double>(36.0, "class abilities: points of effective Shield per +1% of shield armor level added to the Thorns reflect fraction, Specialized source (tighter dual-ratio divisor)")),
                // Deadeye (Archer T2): +8%/rank missile damage, Fletching rider
                ("class_ability_deadeye_percent_per_rank", new Property<double>(0.08, "class abilities: missile damage bonus per rank of Deadeye (0.08 = +8%/rank)")),
                ("class_ability_deadeye_fletching_per_trained", new Property<double>(25.0, "class abilities: points of effective Fletching per +1% Deadeye missile damage, Trained source")),
                ("class_ability_deadeye_fletching_per_spec", new Property<double>(18.0, "class abilities: points of effective Fletching per +1% Deadeye missile damage, Specialized source")),
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
                ("class_ability_savageblows_percent_per_rank", new Property<double>(0.06, "class abilities: melee damage bonus per rank of Savage Blows (0.06 = +6%/rank)")),
                ("class_ability_savageblows_stamina_cost", new Property<double>(10.0, "class abilities: extra stamina consumed per Savage Blows swing; if the player lacks it the swing lands with no bonus")),
                ("class_ability_savageblows_wtink_per_trained", new Property<double>(25.0, "class abilities: points of effective Weapon Tinkering per +1% Savage Blows damage, Trained source")),
                ("class_ability_savageblows_wtink_per_spec", new Property<double>(18.0, "class abilities: points of effective Weapon Tinkering per +1% Savage Blows damage, Specialized source")),
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
                ("class_ability_breakarmor_weptink_per_trained", new Property<double>(50.0, "class abilities: points of effective Weapon Tinkering per +1% Break Armor chance, Trained source")),
                ("class_ability_breakarmor_weptink_per_spec", new Property<double>(37.0, "class abilities: points of effective Weapon Tinkering per +1% Break Armor chance, Specialized source")),
                // Executioner (Berserker T2): +4/8/12% melee damage vs low-health targets
                ("class_ability_executioner_percent_per_rank", new Property<double>(0.04, "class abilities: melee damage bonus per rank of Executioner vs targets below the execute threshold (0.04 = +4%/rank)")),
                ("class_ability_executioner_hp_fraction", new Property<double>(0.25, "class abilities: target health fraction below which Executioner applies")),
                ("class_ability_executioner_dirty_per_trained", new Property<double>(25.0, "class abilities: points of effective Dirty Fighting per +1% Executioner damage, Trained source")),
                ("class_ability_executioner_dirty_per_spec", new Property<double>(18.0, "class abilities: points of effective Dirty Fighting per +1% Executioner damage, Specialized source")),
                // Bloodlust (Berserker T3): melee lifesteal
                ("class_ability_bloodlust_percent_per_rank", new Property<double>(0.01, "class abilities: fraction of melee damage dealt healed back per rank of Bloodlust (0.01 = 1%/rank)")),
                ("class_ability_bloodlust_salvage_per_trained", new Property<double>(150.0, "class abilities: points of effective Salvaging per +1 percentage point of Bloodlust lifesteal, Trained source")),
                ("class_ability_bloodlust_salvage_per_spec", new Property<double>(150.0, "class abilities: points of effective Salvaging per +1 percentage point of Bloodlust lifesteal, Specialized source. Salvaging CAN be Specialized - via the retail augmentation only (AugmentationSpecializeSalvaging) - and this divisor is deliberately pinned EQUAL to the Trained one so the rider pays the same per point either way. Do not 'restore' a tighter spec ratio here")),
                // Attack Speed (Rogue T3): constant +5%/rank attack animation speed, shares the Frenzy ceiling, Lockpick rider
                ("class_ability_attackspeed_percent_per_rank", new Property<double>(0.05, "class abilities: constant attack-speed increase per rank of the Attack Speed skill (0.05 = +5%/rank), stacking with Frenzy under class_ability_attack_speed_ceiling")),
                ("class_ability_attackspeed_lockpick_per_trained", new Property<double>(50.0, "class abilities: points of effective Lockpick per +1% Attack Speed, Trained source")),
                ("class_ability_attackspeed_lockpick_per_spec", new Property<double>(37.0, "class abilities: points of effective Lockpick per +1% Attack Speed, Specialized source")),
                // Flat Cast Speed (Archmage T2): constant +5%/rank war-magic cast speed
                ("class_ability_flatcastspeed_percent_per_rank", new Property<double>(0.05, "class abilities: constant war-magic cast-speed increase per rank of Flat Cast Speed (0.05 = +5%/rank)")),
                // Overchannel (Archmage T2): +8%/rank war-magic damage + Arcane Lore rider, +100% mana surcharge
                ("class_ability_overchannel_percent_per_rank", new Property<double>(0.08, "class abilities: war-spell damage bonus per rank of Overchannel (0.08 = +8%/rank)")),
                ("class_ability_overchannel_arcanelore_per_trained", new Property<double>(25.0, "class abilities: points of effective Arcane Lore per +1% Overchannel damage, Trained source")),
                ("class_ability_overchannel_arcanelore_per_spec", new Property<double>(18.0, "class abilities: points of effective Arcane Lore per +1% Overchannel damage, Specialized source")),
                ("class_ability_overchannel_mana_surcharge", new Property<double>(1.0, "class abilities: extra mana cost fraction added to every war cast when Overchannel is learned (1.0 = +100%); stacks additively with class_ability_spellaoe_mana_surcharge on Arc war casts")),
                // Echo Cast (Archmage T3): chance a landed war spell re-casts itself for free
                ("class_ability_echocast_chance_base", new Property<double>(0.06, "class abilities: Echo Cast recast chance at rank 1 (0.06 = 6%)")),
                ("class_ability_echocast_chance_step", new Property<double>(0.06, "class abilities: Echo Cast recast chance added per rank above 1 (6/12/18% at ranks 1-3)")),
                ("class_ability_echocast_tinker_per_trained", new Property<double>(50.0, "class abilities: points of effective Magic Item Tinkering per +1% Echo Cast chance, Trained source")),
                ("class_ability_echocast_tinker_per_spec", new Property<double>(37.0, "class abilities: points of effective Magic Item Tinkering per +1% Echo Cast chance, Specialized source")),
                // Elemental Rend (Archmage T3): chance a landed war spell inflicts the matching element Vulnerability
                ("class_ability_elementalrend_chance_base", new Property<double>(0.08, "class abilities: Elemental Rend proc chance at rank 1 (0.08 = 8%)")),
                ("class_ability_elementalrend_chance_step", new Property<double>(0.06, "class abilities: Elemental Rend proc chance added per rank above 1 (8/14/20% at ranks 1-3)")),
                ("class_ability_elementalrend_lifemagic_per_trained", new Property<double>(25.0, "class abilities: points of effective Life Magic per +1% Elemental Rend chance, Trained source")),
                ("class_ability_elementalrend_lifemagic_per_spec", new Property<double>(18.0, "class abilities: points of effective Life Magic per +1% Elemental Rend chance, Specialized source")),
                // Avoidance tier: Parry + Shield Block pooled roll, resolved BEFORE evade
                ("class_ability_avoidance_cap", new Property<double>(0.50, "class abilities: combined cap on the pooled Shield Block + Parry avoidance chance (0.50 = 50%)")),
                ("class_ability_parry_percent_per_rank", new Property<double>(0.05, "class abilities: Parry negate chance per rank (0.05 = +5%/rank)")),
                ("class_ability_parry_deception_per_trained", new Property<double>(50.0, "class abilities: points of effective Deception per +1% Parry chance, Trained source")),
                ("class_ability_parry_deception_per_spec", new Property<double>(37.0, "class abilities: points of effective Deception per +1% Parry chance, Specialized source")),
                ("class_ability_shieldblock_base", new Property<double>(0.08, "class abilities: Shield Block chance at rank 1 (0.08 = 8%)")),
                ("class_ability_shieldblock_step", new Property<double>(0.06, "class abilities: Shield Block chance added per rank above 1 (8/14/20% at ranks 1-3)")),
                ("class_ability_shieldblock_armortink_per_trained", new Property<double>(50.0, "class abilities: points of effective Armor Tinkering per +1% Shield Block chance, Trained source")),
                ("class_ability_shieldblock_armortink_per_spec", new Property<double>(37.0, "class abilities: points of effective Armor Tinkering per +1% Shield Block chance, Specialized source")),
                ("class_ability_shieldcheck_strength_base", new Property<double>(0.40, "class abilities: Shield Check Thorns reflect strength at rank 1 (0.40 = 40%)")),
                ("class_ability_shieldcheck_strength_step", new Property<double>(0.30, "class abilities: Shield Check reflect strength added per rank above 1 (40/70/100% at ranks 1-3)")),
                ("class_ability_riposte_fraction_base", new Property<double>(0.40, "class abilities: Riposte counter-strike damage fraction at rank 1 (0.40 = 40%)")),
                ("class_ability_riposte_fraction_step", new Property<double>(0.30, "class abilities: Riposte counter-strike fraction added per rank above 1 (40/70/100% at ranks 1-3)")),
                // Void Damage (Void T3): flat void-spell damage bonus
                ("class_ability_voiddamage_percent_per_rank", new Property<double>(0.08, "class abilities: void-spell damage bonus per rank of Void Damage (0.08 = +8%/rank)")),
                // Acid Proc (Rogue T3): chance a landed weapon hit applies an acid DoT = share of Poison Weapon flat.
                // REWORKED 2026-08-17 (Berserker/Rogue balance pass): the chance is now a flat 25%,
                // rank-invariant, with NO rider - the owner moved the affinity to the poison-damage bonus
                // below. class_ability_acidproc_chance_step is RETIRED and no longer read by Chance(); it
                // stays registered so an existing stage/prod override row does not orphan.
                ("class_ability_acidproc_chance_base", new Property<double>(0.25, "class abilities: Acid Proc proc chance, flat and rank-invariant (0.25 = 25%)")),
                ("class_ability_acidproc_chance_step", new Property<double>(0.06, "RETIRED 2026-08-17: no longer read. class abilities: was the Acid Proc chance added per rank above 1; the chance is now flat. Left registered so a stage/prod override row does not orphan")),
                ("class_ability_acidproc_dot_fraction", new Property<double>(1.0, "class abilities: Acid Proc per-tick damage as a fraction of the current Poison Weapon flat bonus (1.0 = 100%)")),
                ("class_ability_acidproc_dot_interval", new Property<double>(4.0, "class abilities: seconds between Acid Proc DoT ticks (4s)")),
                ("class_ability_acidproc_itemtink_per_trained", new Property<double>(25.0, "class abilities: points of effective Item Tinkering per +1% Acid Proc poison damage bonus, Trained source (moved from proc chance in the 2026-08-17 rework)")),
                ("class_ability_acidproc_itemtink_per_spec", new Property<double>(18.0, "class abilities: points of effective Item Tinkering per +1% Acid Proc poison damage bonus, Specialized source (moved from proc chance in the 2026-08-17 rework)")),
                // Acid Proc poison-damage bonus (new 2026-08-17): +25/50/75% by rank, applied inside
                // PoisonWeaponAbility.FlatBonus so both the per-hit poison proc and every Acid tick inherit
                // it. Its own affinity cap - NOT class_ability_affinity_chance_cap, which is documented as a
                // PROC CHANCE cap and explicitly out of scope for a damage fraction (see that tunable's doc
                // comment, which lists Spell AOE's damage-fraction rider as the same kind of exclusion).
                ("class_ability_acidproc_damage_base", new Property<double>(0.25, "class abilities: Acid Proc poison-damage bonus at rank 1 (0.25 = +25%)")),
                ("class_ability_acidproc_damage_step", new Property<double>(0.25, "class abilities: Acid Proc poison-damage bonus added per rank above 1 (+25/50/75% at ranks 1-3)")),
                ("class_ability_acidproc_damage_affinity_cap", new Property<double>(0.20, "class abilities: maximum poison-damage bonus the Item Tinkering rider may contribute, as a fraction (0.20 = +20 percentage points). Separate from class_ability_affinity_chance_cap, which bounds PROC CHANCE riders only. 0 = uncapped")),
                // Eagle Eye (Archer T2): flat % to effective missile attack skill
                ("class_ability_eagleeye_percent_per_rank", new Property<double>(0.04, "class abilities: bonus to effective missile attack skill per rank of Eagle Eye (0.04 = +4%/rank, +4/8/12%)")),
                // Double Volley (Archer T3): chance a missile volley re-fires a full second volley for free
                ("class_ability_doublevolley_chance_base", new Property<double>(0.06, "class abilities: Double Volley re-fire chance at rank 1 (0.06 = 6%)")),
                ("class_ability_doublevolley_chance_step", new Property<double>(0.06, "class abilities: Double Volley re-fire chance added per rank above 1 (6/12/18% at ranks 1-3)")),
                // Empowered Summons (Void T2): combat-pet stat boost (health/damage/defenses)
                ("class_ability_empoweredsummons_percent_per_rank", new Property<double>(0.10, "class abilities: combat-pet stat bonus per rank of Empowered Summons (0.10 = +10%/rank)")),
                ("class_ability_empoweredsummons_leadership_per_trained", new Property<double>(20.0, "class abilities: points of effective Leadership per +1% Empowered Summons pet stats, Trained source")),
                ("class_ability_empoweredsummons_leadership_per_spec", new Property<double>(15.0, "class abilities: points of effective Leadership per +1% Empowered Summons pet stats, Specialized source")),
                // Whirlwind (Berserker T3): stamina surcharge per 360°/+1-target cleaving swing
                ("class_ability_whirlwind_stamina_surcharge", new Property<double>(1.0, "class abilities: extra stamina cost fraction of a Whirlwind cleaving swing (1.0 = +100%); if unaffordable the swing falls back to a normal frontal cleave")),
                // Withering (Void T3): void DoT tick damage bonus
                ("class_ability_withering_percent_per_rank", new Property<double>(0.08, "class abilities: void (nether) DoT tick damage bonus per rank of Withering (0.08 = +8%/rank)")),
                ("class_ability_withering_creatureench_per_trained", new Property<double>(25.0, "class abilities: points of effective Creature Enchantment per +1% Withering DoT damage, Trained source")),
                ("class_ability_withering_creatureench_per_spec", new Property<double>(18.0, "class abilities: points of effective Creature Enchantment per +1% Withering DoT damage, Specialized source")),
                // Mana Barrier (Archmage T2): a share of incoming damage is paid from Mana instead of Health
                ("class_ability_manabarrier_base", new Property<double>(0.10, "class abilities: fraction of incoming damage Mana Barrier diverts to Mana at rank 1 (0.10 = 10%)")),
                ("class_ability_manabarrier_step", new Property<double>(0.075, "class abilities: Mana Barrier diverted fraction added per rank above 1 (10/17.5/25% at ranks 1-3)")),
                ("class_ability_manabarrier_max_share", new Property<double>(0.60, "class abilities: hard cap on the total fraction of incoming damage Mana Barrier can divert to Mana, rank plus the Magic Defense rider (0.60 = 60%)")),
                ("class_ability_manabarrier_mana_per_health", new Property<double>(1.0, "class abilities: Mana spent per point of Health Mana Barrier restores (1.0 = one for one; higher makes the barrier more expensive)")),
                ("class_ability_manabarrier_magicdef_per_trained", new Property<double>(25.0, "class abilities: points of effective Magic Defense per +1% Mana Barrier diverted share, Trained source")),
                ("class_ability_manabarrier_magicdef_per_spec", new Property<double>(18.0, "class abilities: points of effective Magic Defense per +1% Mana Barrier diverted share, Specialized source")),
                // Nether Bloom (Void T2 game-changer): a killed target's void DoT spreads to nearby enemies
                ("class_ability_netherbloom_jumps_per_rank", new Property<double>(1.0, "class abilities: how many other enemies a Nether Bloom DoT spreads to per rank (1.0 = 1/2/3 at ranks 1-3)")),
                ("class_ability_netherbloom_radius", new Property<double>(10.0, "class abilities: radius (in meters) around the slain target within which Nether Bloom looks for creatures to spread the void DoT to")),
                ("class_ability_netherbloom_duration_retained", new Property<double>(1.0, "class abilities: fraction of the slain target's REMAINING DoT duration a Nether Bloom copy carries (1.0 = all of it)")),
                // Soul Tether (Void T2): combat-pet damage reduction + a free resummon after the pet dies
                ("class_ability_soultether_base", new Property<double>(0.10, "class abilities: fraction of combat-pet incoming damage Soul Tether removes at rank 1 (0.10 = 10%)")),
                ("class_ability_soultether_step", new Property<double>(0.075, "class abilities: Soul Tether pet damage reduction added per rank above 1 (10/17.5/25% at ranks 1-3)")),
                ("class_ability_soultether_max_reduction", new Property<double>(0.50, "class abilities: hard cap on total Soul Tether combat-pet damage reduction, rank plus the Loyalty rider (0.50 = 50%)")),
                ("class_ability_soultether_loyalty_per_trained", new Property<double>(25.0, "class abilities: points of effective Loyalty per +1% Soul Tether pet damage reduction, Trained source")),
                ("class_ability_soultether_loyalty_per_spec", new Property<double>(18.0, "class abilities: points of effective Loyalty per +1% Soul Tether pet damage reduction, Specialized source")),
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
                ("class_ability_bloodprice_damage_r1", new Property<double>(0.08, "class abilities: Blood Price - spell damage added at rank 1 (0.08 = +8%), any school")),
                ("class_ability_bloodprice_damage_r2", new Property<double>(0.16, "class abilities: Blood Price - spell damage added at rank 2 (0.16 = +16%), any school")),
                ("class_ability_bloodprice_damage_r3", new Property<double>(0.24, "class abilities: Blood Price - spell damage added at rank 3 (0.24 = +24%), any school")),
                ("class_ability_bloodprice_min_health_fraction", new Property<double>(0.20, "class abilities: Blood Price - fraction of MAXIMUM health below which a cast is free and unbuffed (0.20 = 20%). All-or-nothing: never a cost without the bonus")),
                // Transfusion (Blood Mage T1, 3 ranks, cost 1/1/1) - the FIRST Blood Mage skill with live
                // behaviour: rank sets what fraction of a Drain Health surplus reaches nearby fellows.
                // These scale DELIVERY ONLY - drain damage is byte-identical at every rank.
                ("class_ability_transfusion_share_r1", new Property<double>(0.40, "class abilities: Transfusion - fraction of the Drain Health surplus delivered to fellows at rank 1 (0.40 = 40%). Delivery only; the drain itself is unchanged")),
                ("class_ability_transfusion_share_r2", new Property<double>(0.70, "class abilities: Transfusion - fraction of the Drain Health surplus delivered to fellows at rank 2 (0.70 = 70%). Delivery only; the drain itself is unchanged")),
                ("class_ability_transfusion_share_r3", new Property<double>(1.00, "class abilities: Transfusion - fraction of the Drain Health surplus delivered to fellows at rank 3 (1.00 = 100%). Delivery only; the drain itself is unchanged")),
                // DIVISORS, like every other peripheral-skill rider: "points of skill per +1%", so LARGER IS
                // WEAKER. 50.0 = +0.02 percentage points per point of Healing (400 Healing -> +8%, i.e. a
                // 108% share at rank 3); 33.3 = +0.03 per point (400 Healing -> +12%). Deliberately UNCAPPED
                // and allowed past 100% - see DrainSurplusDistribution.ShareFraction for why that is safe
                ("class_ability_transfusion_healing_per_trained", new Property<double>(50.0, "class abilities: points of effective Healing per +1% Transfusion surplus share, Trained source. Additive on top of the per-rank fraction and deliberately uncapped")),
                ("class_ability_transfusion_healing_per_spec", new Property<double>(33.3, "class abilities: points of effective Healing per +1% Transfusion surplus share, Specialized source. Additive on top of the per-rank fraction and deliberately uncapped")),
                // Malediction (Blood Mage T2, 3 ranks, cost 2/2/2) - Vulnerability and Imperil enchantments
                // the player applies land harder and last longer, for EVERY attacker on that target. The
                // intensity bonus scales the debuff's distance from its identity value (1.0 multiplicative,
                // 0.0 additive), not the raw StatModValue - see MaledictionAbility.ScaleStatModValue
                ("class_ability_malediction_intensity_base", new Property<double>(0.10, "class abilities: Malediction Vulnerability/Imperil intensity bonus at rank 1 (0.10 = +10%)")),
                ("class_ability_malediction_intensity_step", new Property<double>(0.10, "class abilities: Malediction intensity bonus added per rank above 1 (+10/20/30% at ranks 1-3)")),
                ("class_ability_malediction_duration_bonus", new Property<double>(0.25, "class abilities: how much longer a Malediction-boosted Vulnerability/Imperil lasts (0.25 = 25% longer). Flat - it does NOT scale with rank")),
                // DIVISORS, like every other peripheral-skill rider: "points of skill per +1%", so LARGER IS
                // WEAKER. Half rate against the other Life Magic rider (Elemental Rend, 25/18) because
                // Malediction is party-wide. 50.0 -> +8% at 400 Life Magic Trained; 37.0 -> +10.8% Specialized
                ("class_ability_malediction_lifemagic_per_trained", new Property<double>(50.0, "class abilities: points of effective Life Magic per +1% Malediction intensity, Trained source")),
                ("class_ability_malediction_lifemagic_per_spec", new Property<double>(37.0, "class abilities: points of effective Life Magic per +1% Malediction intensity, Specialized source")),
                // Sanguine Ward (Blood Mage T3, 3 ranks, cost 3/3/3) - Martyr's Hecatomb and Curse of Raven
                // Fury still spend their FULL health basis for damage; these only change what the caster
                // actually pays and what comes back as absorb. selfcost is the share of the basis actually
                // lost (so LOWER IS STRONGER); absorb is the ward, as a share of the amount actually lost.
                // The ward REFRESHES rather than stacks and EXPIRES UNUSED - see SanguineWardMath.
                ("class_ability_sanguine_ward_selfcost_r1", new Property<double>(0.80, "class abilities: Sanguine Ward - fraction of the Hecatomb/Raven Fury health basis the caster actually loses at rank 1 (0.80 = 80%). LOWER IS STRONGER; the damage basis is unchanged")),
                ("class_ability_sanguine_ward_selfcost_r2", new Property<double>(0.65, "class abilities: Sanguine Ward - fraction of the Hecatomb/Raven Fury health basis the caster actually loses at rank 2 (0.65 = 65%). LOWER IS STRONGER; the damage basis is unchanged")),
                ("class_ability_sanguine_ward_selfcost_r3", new Property<double>(0.50, "class abilities: Sanguine Ward - fraction of the Hecatomb/Raven Fury health basis the caster actually loses at rank 3 (0.50 = 50%). LOWER IS STRONGER; the damage basis is unchanged")),
                ("class_ability_sanguine_ward_absorb_r1", new Property<double>(0.50, "class abilities: Sanguine Ward - damage-absorbing ward granted at rank 1, as a fraction of the health actually lost to the cast (0.50 = 50%). Absorb only - it never restores health")),
                ("class_ability_sanguine_ward_absorb_r2", new Property<double>(0.75, "class abilities: Sanguine Ward - damage-absorbing ward granted at rank 2, as a fraction of the health actually lost to the cast (0.75 = 75%). Absorb only - it never restores health")),
                ("class_ability_sanguine_ward_absorb_r3", new Property<double>(1.00, "class abilities: Sanguine Ward - damage-absorbing ward granted at rank 3, as a fraction of the health actually lost to the cast (1.00 = 100%). Absorb only - it never restores health")),
                ("class_ability_sanguine_ward_duration_seconds", new Property<double>(15.0, "class abilities: Sanguine Ward - seconds the ward lasts. A recast REFRESHES it rather than stacking, and an unspent ward expires with no refund")),
                // Spellblade (Spellsword T1 GC, 3 ranks, cost 1/2/3): a landed LIGHT WEAPON hit casts a
                // Streak war spell matching the damage type actually dealt. The chance is deliberately
                // RANK-INVARIANT - rank buys spell LEVEL (III / V / the level-8 Incantation), which is the
                // shared shape of all three war procs. The Item Enchantment rider is what makes the chance
                // skill-sensitive.
                ("class_ability_affinity_chance_cap", new Property<double>(0.20, "class abilities: maximum PROC CHANCE an affinity rider may contribute, as a fraction (0.20 = +20 percentage points, reached at 500 points of the source skill). GetClassAbilityScaling returns a raw quotient (skill / divisor) with no bound of its own, so without this a high source skill saturates a proc to a literal 100% - observed live 2026-08-04 at Item Enchantment 5226 (+209 points). Applies to every chance-on-hit ability carrying an affinity: Spellblade, Runeblade, Sundermark, Break Armor, Elemental Rend, Dispelling Edge and Shield Block. Spellstorm has no affinity and is unaffected. Spell AOE's rider and Acid Proc's poison-damage rider (class_ability_acidproc_damage_affinity_cap, 2026-08-17) are damage FRACTIONS rather than a chance and are deliberately out of scope. 0 = uncapped")),
            ("class_ability_spellblade_chance", new Property<double>(0.20,"class abilities: Spellblade - chance a landed light weapon hit casts a matching Streak war spell (0.20 = 20%). Rank does NOT raise this; rank raises the spell level cap")),
                ("class_ability_spellblade_itemench_per_trained", new Property<double>(25.0, "class abilities: points of effective Item Enchantment per +1% Spellblade proc chance, Trained source")),
                ("class_ability_spellblade_itemench_per_spec", new Property<double>(18.0, "class abilities: points of effective Item Enchantment per +1% Spellblade proc chance, Specialized source")),
                // Runeblade (Spellsword T2 GC, 3 ranks, cost 1/2/3): the same shape as Spellblade but firing
                // a Blast cone. A Blast lands exactly ONE projectile on the centre target, so its
                // single-target damage EQUALS Spellblade's at the same level and everything it adds is
                // cleave. 0.15 is priced against that: below Spellblade's 0.20 on single targets, paying for
                // itself against a pack. Do not re-price it as though a Blast were a bigger Streak
                ("class_ability_runeblade_chance", new Property<double>(0.15, "class abilities: Runeblade - chance a landed light weapon hit casts a matching Blast war spell (0.15 = 15%). Rank does NOT raise this; rank raises the spell level cap")),
                ("class_ability_runeblade_magicitemtink_per_trained", new Property<double>(25.0, "class abilities: points of effective Magic Item Tinkering per +1% Runeblade proc chance, Trained source")),
                ("class_ability_runeblade_magicitemtink_per_spec", new Property<double>(18.0, "class abilities: points of effective Magic Item Tinkering per +1% Runeblade proc chance, Specialized source")),
                // Spellstorm (Spellsword T3 GC, 1 rank, cost 5): a landed light weapon hit fires the tier-I
                // Ring - 9 projectiles at 360 degrees. Single rank and a single fixed spell level, so there
                // is no level tunable and no affinity rider here: it is a shape change, not a ladder
                ("class_ability_spellstorm_chance", new Property<double>(0.10, "class abilities: Spellstorm - chance a landed light weapon hit fires a matching 360-degree war Ring (0.10 = 10%)")),
                // Sundermark (Spellsword T2, 3 ranks, cost 3 flat): the class's ANY-WEAPON entry. Unlike the
                // three war procs its chance DOES scale with rank, and it stays low - but the low chance is
                // not the brake. Vulnerability durations are long against ~1 swing/sec, so uptime after the
                // opener is effectively 100%. It ships that way by ruling: a rending weapon already occupies
                // the enchantment slot this writes to (vulnMod = max(vulnMod, weaponResistanceMod)), and any
                // player with the Life Magic can cast the level-8 vulnerability directly
                ("class_ability_sundermark_chance_base", new Property<double>(0.05, "class abilities: Sundermark - chance a landed hit with ANY weapon applies the matching elemental Vulnerability, at rank 1 (0.05 = 5%)")),
                ("class_ability_sundermark_chance_step", new Property<double>(0.05, "class abilities: Sundermark - chance added per rank above 1 (5/10/15% at ranks 1-3)")),
                ("class_ability_sundermark_lifemagic_per_trained", new Property<double>(25.0, "class abilities: points of effective Life Magic per +1% Sundermark proc chance, Trained source")),
                ("class_ability_sundermark_lifemagic_per_spec", new Property<double>(18.0, "class abilities: points of effective Life Magic per +1% Sundermark proc chance, Specialized source")),
                // Shared by all four Spellsword procs. Spell level is chosen by comparing the relevant magic
                // skill's Current against SpellFormula.MinPower (1/50/100/150/200/250/300/400 for levels
                // 1-8); this scales those thresholds. LARGER IS STRICTER - 2.0 means you need twice the
                // skill for the same rung. It is the single lever if procs reach their top rung too early
                ("class_ability_spellsword_level_skill_scale", new Property<double>(1.0, "class abilities: Spellsword - multiplier on the SpellFormula.MinPower thresholds that decide which spell level a proc fires (1.0 = the engine's own thresholds; larger is stricter)")),
                // Dispelling Edge (Spellsword T3, 3 ranks, cost 3 flat): a landed hit strips ONE beneficial
                // enchantment from the target. Beneficial only - stripping a harmful one would remove the
                // party's own debuffs, including this class's Sundermark
                ("class_ability_dispellingedge_chance_base", new Property<double>(0.10, "class abilities: Dispelling Edge - chance a landed hit strips one of the target's beneficial enchantments, at rank 1 (0.10 = 10%)")),
                ("class_ability_dispellingedge_chance_step", new Property<double>(0.05, "class abilities: Dispelling Edge - chance added per rank above 1 (10/15/20% at ranks 1-3)")),
                ("class_ability_dispellingedge_arcanelore_per_trained", new Property<double>(25.0, "class abilities: points of effective Arcane Lore per +1% Dispelling Edge chance, Trained source")),
                ("class_ability_dispellingedge_arcanelore_per_spec", new Property<double>(18.0, "class abilities: points of effective Arcane Lore per +1% Dispelling Edge chance, Specialized source")),
                // Resonance (Spellsword T1, 3 ranks, cost 3 flat): landing ANY magic damage grants a stack,
                // and each stack adds to ALL your magic damage. Both halves are deliberately broad - this is
                // the entry that makes Spellsword a magic class rather than a melee class with sparks, and
                // it is what lets an Archmage/Spellsword splash ramp in either direction. Because it is a
                // general magic-damage multiplier it MULTIPLIES with Overchannel and Void Damage; the short
                // window is the brake. The per-stack value was halved from the original sketch when the
                // scope widened from proc damage to all magic damage
                ("class_ability_resonance_per_stack_r1", new Property<double>(0.02, "class abilities: Resonance - magic damage added per held stack at rank 1 (0.02 = +2%, so +10% at the 5-stack cap)")),
                ("class_ability_resonance_per_stack_r2", new Property<double>(0.03, "class abilities: Resonance - magic damage added per held stack at rank 2 (0.03 = +3%, so +15% at the 5-stack cap)")),
                ("class_ability_resonance_per_stack_r3", new Property<double>(0.04, "class abilities: Resonance - magic damage added per held stack at rank 3 (0.04 = +4%, so +20% at the 5-stack cap)")),
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
                ("class_ability_pocketsand_chance_base", new Property<double>(0.10, "class abilities: Pocket Sand - chance an avoided attack blinds the attacker, at rank 1 (0.10 = 10%)")),
                ("class_ability_pocketsand_chance_step", new Property<double>(0.10, "class abilities: Pocket Sand - chance added per rank above 1 (10/20/30% at ranks 1-3)")),
                ("class_ability_pocketsand_magnitude", new Property<double>(30.0, "class abilities: Pocket Sand - points subtracted from every one of the blinded creature's attack skills (30.0 = -30). Rank-invariant; rank buys chance, not magnitude")),
                ("class_ability_pocketsand_duration_seconds", new Property<double>(20.0, "class abilities: Pocket Sand - how long the blind lasts, in seconds. A re-proc on the same creature refreshes this window rather than stacking a second debuff")),
                ("class_ability_pocketsand_deception_per_trained", new Property<double>(50.0, "class abilities: points of effective Deception per +1% Pocket Sand chance, Trained source")),
                ("class_ability_pocketsand_deception_per_spec", new Property<double>(37.0, "class abilities: points of effective Deception per +1% Pocket Sand chance, Specialized source")),
                // Monster combat effects: per-effect MAGNITUDES are authored on the monster weenie
                // (PropertyString 9015 MonsterCombatEffects), because they vary per monster. The tunables
                // below are the CEILINGS that bound whatever any weenie asks for, so the repo owner can move
                // the whole layer live without touching content. Accessors: MonsterEffects.MonsterEffectCaps.
                ("monster_effect_avoidance_cap", new Property<double>(0.50, "monster effects: maximum total chance a monster may avoid an incoming attack through this layer (0.50 = 50%), before its normal evade roll")),
                ("monster_effect_proc_chance_cap", new Property<double>(0.75, "monster effects: maximum chance for any one on-hit proc after every ramp and bonus (0.75 = 75%)")),
                ("monster_effect_leech_cap", new Property<double>(0.50, "monster effects: maximum fraction of the damage a monster deals that it may convert back into its own vitals (0.50 = 50%)")),
                ("monster_effect_reflect_cap", new Property<double>(0.50, "monster effects: maximum fraction of incoming damage a monster may send back at its attacker (0.50 = 50%)")),
                ("monster_effect_speed_cap", new Property<double>(2.00, "monster effects: maximum attack- or cast-speed multiplier from this layer (2.00 = twice as fast), after every speed and ramp record composes")),
                ("monster_effect_damage_rider_cap", new Property<double>(10000.0, "monster effects: maximum extra damage one flat rider may add to a single hit. High on purpose - it is a runaway guard, not a balance dial; the balance dial is the amount= arg on the weenie")),
                ("monster_effect_manabarrier_cap", new Property<double>(0.60, "monster effects: maximum fraction of incoming damage a manabarrier effect may divert to its own Mana (0.60 = 60%), mirroring class_ability_manabarrier_max_share")),
                ("monster_effect_ward_cap", new Property<double>(1.0, "monster effects: maximum ward grant a ward effect may author, as a multiple of the carrying monster's own maximum health (1.0 = a ward may not exceed 100% of max health)")),
                ("monster_effect_debuff_cap", new Property<double>(500.0, "monster effects: maximum magnitude a debuff effect may apply on its POINT-valued shapes - what=imperil (armor points removed) and what=attackskills (skill points removed). High on purpose - a runaway guard, not a balance dial (the dial is mag= on the weenie); for scale, the player-side equivalent class_ability_pocketsand_magnitude is 30.0. The FRACTION-valued shape what=vuln has its own cap, because the two are not the same unit")),
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
                ("offline_bonus_multiplier", new Property<double>(1.0, "Offline bonus: fractional boost added to a character's own combat XP/Luminance while they have banked offline bonus time. 1.0 = +100% (double). Does not apply to quest, allegiance passup, or the fellowship-shared portion of another fellow's kill.")),
                ("offline_bonus_idle_accrual_rate", new Property<double>(1.0, "Offline bonus: rate the bank accrues, per idle second, while a character is online but idle (no qualifying combat XP/Luminance for longer than offline_bonus_idle_timeout_seconds). 1.0 = accrues at the same 1:1 rate as being logged out, still capped by offline_bonus_max_seconds.")),
                ("alt_character_bonus_multiplier", new Property<double>(1.0, "Alt character bonus: fractional boost added to a character's leveling XP (kills, quest turn-ins, and its share of a fellow's kill) while it is below the highest enlightenment+level character on its account. 1.0 = +100% (double). Multiplicative with the offline bonus. Does not apply to allegiance passup or item XP.")),
                ("melee_max_angle", new Property<double>(0.0, "for melee players, the maximum angle before a TurnTo is required. retail appeared to have required a TurnTo even for the smallest of angle offsets.")),
                ("mob_awareness_range", new Property<double>(1.0, "Scales the distance the monsters become alerted and aggro the players")),
                ("mule_form_max_effect_scale", new Property<double>(8.0, "WaffleACE: the largest particle-emitter scale (StartScale/FinalScale in the donor's baked DefaultScript) a creature may bake before the Beast Effigy refuses to attune to it. Particle effects are absolute dat data and do NOT scale with the object like the mesh does - verified in game 2026-09-07, a captured Shadow Vortex (scale 30) rendered a full-height cloud around a correctly mesh-scaled vendor - so an oversized donor cannot be fixed server-side and must be refused at attunement instead. A scan of every creature Setup that bakes a script found most under 8 and a long thin tail above it (up to 30), so this is a threshold rather than an all-or-nothing ban on baked scripts. Raising it re-permits larger donors; setting it very high effectively disables the gate. Gates ATTUNEMENT ONLY - forms already earned before this shipped keep rendering, by design")),
                ("pickup_animation_speed", new Property<double>(1.0, "WaffleACE: playback speed multiplier for the player pick-up animation (reach and return). 1.0 = retail")),
                ("pickup_speed_quest_bonus", new Property<double>(0.5, "WaffleACE: additive pick-up animation speed bonus per permanent quest boon a character has claimed (see PickupSpeed.Compute). 0.5 = +50% per boon")),
                ("pickup_animation_speed_max", new Property<double>(3.0, "WaffleACE: cap on the composed pick-up animation speed (base * quest boons), clamped to [1.0, 10.0]")),
                ("custom_aug_pickup_speed_bonus", new Property<double>(0.10, "WaffleACE: additive pick-up animation speed bonus per Custom Dreamweave pick-up augmentation a character holds (PropertyInt.AugmentationPickupSpeed, bought from Bo). 0.10 = +10% each. Added to the quest-boon term inside the SAME pickup_animation_speed_max clamp, so with the shipped defaults four quest boons alone already reach the cap and further augmentations are inert until pickup_animation_speed_max is raised - /pickupspeed says so when it happens")),
                ("custom_aug_spell_duration_bonus", new Property<double>(0.10, "WaffleACE: additive spell duration bonus per Custom Dreamweave spell duration augmentation a character holds (PropertyInt.AugmentationSpellDurationCustom, bought from Fi). 0.10 = +10% each. Added to the retail AugmentationIncreasedSpellDuration term rather than multiplied by it, because WeaponModId.Longevity already multiplies a second factor at the same three sites")),
                ("pk_new_character_grace_period", new Property<double>(300, "the number of seconds, in addition to pk_respite_timer, that a player killer is set to non-player killer status after first exiting training academy")),
                ("pk_respite_timer", new Property<double>(300, "the number of seconds that a player killer is set to non-player killer status after dying to another player killer")),
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
                ("dynamic_dungeons_clear_fraction", new Property<double>(0.9, "(non-retail function) Threads: target weighted clear progress (owner ruling R28) a run must reach to count as cleared. The boss alone is worth dynamic_dungeons_boss_clear_weight of that progress; the run's trash shares the rest evenly. 1.0 = every point of progress available, boss and trash both. Clamped to [0.0, 1.0] at read")),
                ("dynamic_dungeons_survey_ratio_exponent", new Property<double>(ACE.Server.ThreadDungeons.SurveyRewardLimits.DefaultExponent, "(non-retail function) Threads: the k of the daily-survey reward ratio (avgGemLevel / DungeonGemSpec.MaxLevel)^k. Higher k punishes low-level farming harder; 1.0 is linear, 0 disables the curve (every ratio reads 1.0). NaN, a negative or a garbled value reads as the default; values above 50 read as 50")),
                ("dynamic_dungeons_survey_ratio_floor", new Property<double>(ACE.Server.ThreadDungeons.SurveyRewardLimits.DefaultFloor, "(non-retail function) Threads: lower bound on the daily-survey reward ratio, so a cleared run at the minimum gem level still pays something. Clamped to [0.0, 1.0]; a NaN or garbled value reads as the default")),
                ("dynamic_dungeons_press_aim_chance", new Property<double>(ACE.Server.ThreadDungeons.PressLimits.DefaultAimChance, "(non-retail function) Threads: how often a Fragment Press op that NAMES a modifier - a taper, a herb, a mapped powder, a targeted talisman - actually lands that modifier. The rest of the time it lands a uniformly random other modifier from the SAME budget class (the other salvage affinities for a powder, the other monster/boss modifiers for a taper). 1.0 restores deterministic aiming; 0.0 makes every aimed component a wildcard. Clamped to [0.0, 1.0] at read; a NaN or garbled value reads as the default")),
                // dynamic_dungeons_press_wild_share and dynamic_dungeons_press_fracture_rate were removed here
                // with the Threads instability mechanic (owner ruling, 2026-09-07). Removing the
                // registration is safe on a shard whose config table still holds a row for either name:
                // LoadPropertiesFromDB (:51-64) copies every DB row into the cache without checking it against
                // this default table, so an orphaned row is cached harmlessly and simply never read.
                ("dynamic_dungeons_boss_clear_weight", new Property<double>(0.2, "(non-retail function) Threads: share of a run's weighted clear progress (owner ruling R28) that a run's boss is worth on its own when one is present; the remaining share is split evenly across the run's trash. Killing only the boss no longer clears a run outright. Bossless runs renormalize their trash to the full share. Clamped to [0.0, 1.0] at read")),
                ("dynamic_dungeons_boss_health_floor_ratio", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossHealthFloorRatio, "(non-retail function) Threads: multiple of the toughest non-boss creature's health a run boss must reach. A FLOOR, not a multiplier stacked on the gem's modifiers: the boss gets max(what its modifiers give, this ratio times the observed pack maximum). 0 disables the axis; a negative value reads as the default; values above 50 read as 50")),
                ("dynamic_dungeons_trash_health_floor_ratio", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultTrashHealthFloorRatio, "(non-retail function) Threads: multiple of the in-band trash pool's MEDIAN authored health (cross-family, at the gem's own level) a non-boss creature's health must reach, applied BEFORE the gem's health multiplier. A FLOOR, not a multiplier stacked on the gem's modifiers, and never lowers a creature already above it. 0 disables the axis; a negative value reads as the default; values above 5 read as 5")),
                ("dynamic_dungeons_roster_band_low", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRosterBandLow, "(non-retail function) Threads: the LOW edge multiplier of the roster level band [gemLevel * this, gemLevel * dynamic_dungeons_roster_band_high] a gem draws its trash, elite and (absent a curated boss) promoted-boss candidates from. Drives BOTH the trash and elite bands, not trash alone - the name has no 'trash' in it for that reason. A value of zero or below, or a negative, reads as the default (1.0); values above 5 read as 5")),
                ("dynamic_dungeons_roster_band_high", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRosterBandHigh, "(non-retail function) Threads: the HIGH edge multiplier of the same roster level band. Narrowed from 1.5 to 1.15 (owner ruling 2026-09-08): World Events feeds the same arithmetic a player audience's median level, where a wide spread reaches an already-varied crowd, while Threads feeds it the level printed on the gem, which players read as the dungeon's own advertised difficulty - at 1.5x a level-185 gem could draw a level-278 monster. A value of zero or below, or a negative, reads as the default (1.15); values above 5 read as 5")),
                ("dynamic_dungeons_band_low_floor", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBandLowFloorRatio, "(non-retail function) Threads: the lowest the roster band's LOW edge may step to, as a fraction of the gem level, when the natural band cannot field enough families (dynamic_dungeons_min_eligible_families) or enough distinct wcids in the chosen family (dynamic_dungeons_min_family_pool). The HIGH edge never moves. A creature reached below the natural low edge is normalized back up to the band standard - level, per-skill InitLevel, body-part damage and body armour - and pays at least the gem level's ladder XP, so widening lowers neither a run's difficulty nor its rewards. 1.0 DISABLES the whole feature. NaN, Infinity or a value at or below 0 reads as the default; values above 1.0 read as 1.0")),
                ("dynamic_dungeons_fit_headroom_margin", new Property<double>(ACE.Server.ThreadDungeons.DungeonFitFilter.DefaultFitHeadroomMargin, "(non-retail function) Threads: metres of extra headroom demanded on top of a creature's own movement height by the dynamic_dungeons_fit_filter predicate. Defaults to 0.0, which reproduces the measured verdict exactly - over 1040 creature-dungeon pairs, movement height against maxCollisionHeightFullAccess predicted pass/fail with zero disagreements, and radius never bound. The knob exists because the clearance tool's known gaps all err toward 'too passable', so a survivor found in the field can be excluded by tightening one number rather than by re-measuring the table. NaN, Infinity or a negative value reads as the default; values above 2.0 read as 2.0")),
                ("dynamic_dungeons_boss_level_margin", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultBossLevelMargin, "(non-retail function) Threads: multiple of the GEM level a run boss's stamped level must reach. One of four floors on the boss level; the pack maximum usually dominates it at the high rungs. Values below 1.0 read as 1.0; a negative value reads as the default; values above 5 read as 5")),
                ("dynamic_dungeons_reward_scale_anchor", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleAnchor, "(non-retail function) Threads: the gem level at which the gem-level reward-scale ratio (level/anchor)^exponent equals 1.0. Set to 300 while the gem ceiling was 275, so a ceiling raise would not silently devalue every existing gem; the 2026-09-09 raise to 375 put the ceiling ABOVE it, and since dynamic_dungeons_reward_scale_cap defaults to uncapped, gems above 300 scale past 1.0 rather than flattening. NaN, Infinity or a non-positive value reads as the default")),
                ("dynamic_dungeons_reward_scale_exponent", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleExponent, "(non-retail function) Threads: the exponent of the gem-level reward-scale ratio (level/anchor)^exponent. 2.87 is chosen so ratio(185) = 0.25 with the default anchor of 300. NaN or Infinity reads as the default")),
                ("dynamic_dungeons_reward_scale_floor", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleFloor, "(non-retail function) Threads: lower bound on the gem-level reward-scale ratio, so a low-level Thread still pays something on every scaled axis. NaN, Infinity or a negative value reads as the default")),
                ("dynamic_dungeons_reward_scale_cap", new Property<double>(ACE.Server.ThreadDungeons.DungeonPopulationLimits.DefaultRewardScaleCap, "(non-retail function) Threads: upper bound on the gem-level reward-scale ratio. 0 or less means UNCAPPED - deliberate, so growth above the anchor is unbounded until an admin sets an explicit safety valve")),
                ("world_events_boss_health_cap", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.cap for every boss when positive; 0 or negative = use the JSON value. The ceiling on the power-curve boss health multiplier (BossDef.ResolveHealthMult)")),
                ("world_events_boss_hits_to_kill", new Property<double>(3.0, "(non-retail function) World Events: how many ORDINARY (non-crit) hits from a named boss the TOP player present - the highest Health.MaxValue among counted participants, staff excluded - should survive. 3 means roughly 2 to 4. A low-level player being one-shot is accepted by design; the boss is sized against the toughest player in the fight, not the softest. A non-finite or non-positive value falls back to the built-in default")),
                ("world_events_boss_min_sample_seconds", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.minSampleSeconds for every boss when positive; 0 or negative = use the JSON value. The shortest wave phase that produces a usable throughput measurement")),
                ("world_events_boss_per_player", new Property<double>(0, "(non-retail function) World Events: overrides bosses.json.perPlayer for every boss when positive; 0 or negative = use the JSON value. Added to the power-curve boss health multiplier per unit of audience power")),
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
                ("world_events_wave_count_per_participant", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.waveCount.perParticipant for every source when positive; 0 or negative = use the JSON value. The per-participant term of the per-wave trash count formula")),
                ("world_events_wave_interval_seconds", new Property<double>(0, "(non-retail function) World Events: overrides sources.json.waveIntervalSeconds for every source when positive; 0 or negative = use the JSON value. Seconds between wave spawns")),
                ("xp_modifier", new Property<double>(1.0, "scales the amount of xp received by players"))
                );

        public static readonly ReadOnlyDictionary<string, Property<string>> DefaultStringProperties =
            DictOf(
                ("account_vault_denylist", new Property<string>("", "WaffleACE Mule Vendor: comma-separated 4-hex-digit landblock ids where summoning a mule vendor is refused. Empty by default. This is the emergency lever - a specific problem area - and it OUTRANKS account_vault_allowlist, so a landblock named in both is refused. Parsed exactly like mule_landblocks; a malformed entry is skipped with a logged warning, never thrown")),
                ("dynamic_dungeons_banned_spell_ids", new Property<string>(ACE.Server.ThreadDungeons.DungeonSpellFilter.DefaultBannedSpellIds, "(non-retail function) Threads: comma-separated spell ids removed by id from every run creature's spell book regardless of projectile count, while dynamic_dungeons_strip_aoe_spells is true. Defaults to Exsanguinating Wave under both of its live ids (3940, 3999) and Poisoned Vitality (6167) - owner ruling 2026-09-08. Ban by ID ONLY: spell names repeat across ids in ace_world, so a name match would be ambiguous. Empty means the id list is off and only the projectile rule applies; a malformed entry is skipped with a logged warning, never thrown")),
                ("account_vault_allowlist", new Property<string>("016C", "WaffleACE Mule Vendor: comma-separated 4-hex-digit landblock ids where summoning a mule vendor is the ONLY place it is permitted. Defaults to 016C, the Marketplace, on the repo owner's ruling after the first live test - it supersedes DESIGN 11.2's original summonable-anywhere decision. EMPTY means no allowlist, i.e. permitted everywhere subject only to account_vault_denylist; empty is NOT 'nowhere'. Checked against the summoner's landblock and again against each placement rung, since the spawn offset can cross a landblock line. Parsed exactly like account_vault_denylist")),
                ("account_vault_allowlist_name", new Property<string>("the Marketplace", "WaffleACE Mule Vendor: the player-facing name of the account_vault_allowlist area, used only in the refusal message ('You can only summon your vault vendor in {this}.'). A separate row because a landblock id means nothing to a player and this message would otherwise go stale the moment account_vault_allowlist was pointed elsewhere. Blank degrades to 'the permitted area'")),
                ("facet_allowlist", new Property<string>("016C", "Player Facets: comma-separated 4-hex-digit landblock ids where /facet N may be used. Defaults to 016C, the Marketplace. EMPTY means no restriction, i.e. permitted everywhere - empty is NOT 'nowhere'. Parsed exactly like account_vault_allowlist")),
                ("facet_allowlist_name", new Property<string>("the Marketplace", "Player Facets: the player-facing name of the facet_allowlist area, used only in the refusal message ('You can only change facets in {this}.'). A separate row because a landblock id means nothing to a player and this message would otherwise go stale the moment facet_allowlist was pointed elsewhere. Blank degrades to 'the permitted area'")),
                ("content_folder", new Property<string>("Content", "for content creators to live edit weenies. defaults to Content folder found in same directory as ACE.Server.dll")),
                ("mule_landblocks", new Property<string>("016C", "WaffleACE: comma-separated list of 4-hex-digit landblock ids (e.g. 016C) that count as 'mule landblocks' for the IP active-player limit (ip_limit_enabled). This key belongs ONLY to the IP active-player limit system - it does NOT gate, and is unrelated to, the separate IsMule character-conversion system (mule_system_enabled, mule_level, mule_bind_position, etc). Defaults to 016C, the Marketplace. Managed with the mule-landblocks admin command; a hand edit must keep the comma-separated form")),
                ("ip_limit_exempt_accounts", new Property<string>("", "WaffleACE: comma-separated ACCOUNT names (not character names) exempt from the IP active-player limit (ip_limit_enabled), in addition to the access-level exemption (ip_limit_exempt_access_level), loopback addresses, and the existing AllowUnlimitedSessionsFromIPAddresses allowlist. Managed with the ip-limit-exempt admin command; a hand edit must keep the comma-separated form")),
                ("mule_bind_position", new Property<string>("0x016C019E 28.850000 -40.738000 0.005000 -0.394134 0.000000 0.000000 -0.919053", "Mule system: loc string (cell x y z qw qx qy qz - same token order as /teleloc) that ApplyMuleConversion parses via AdminCommands.TryParseLocPosition and sets as PositionType.Sanctuary, mirroring a real lifestone /use. Defaults to the Marketplace mule-conversion lifestone (wcid 509 instance at wcid-registry guid 0x7016C081, realm 0). A parse failure logs a warning and skips the bind rather than throwing - a bad config string must never break the conversion itself")),
                ("discord_webhook_url_general", new Property<string>("", "Discord webhook URL that General chat is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_GENERAL environment variable when set")),
                ("discord_webhook_url_trade", new Property<string>("", "Discord webhook URL that Trade chat is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_TRADE environment variable when set")),
                ("discord_webhook_url_audit", new Property<string>("", "Discord webhook URL that the staff Audit channel (admin command records) is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_AUDIT environment variable when set")),
                ("discord_webhook_url_events", new Property<string>("", "Discord webhook URL that World Events announcements (the teaser, the run going live, and the outcome - not the countdown warnings or per-wave flavour) are relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_EVENTS environment variable when set")),
                ("dat_older_warning_msg", new Property<string>("Your DAT files are incomplete.\nThis server does not support dynamic DAT updating at this time.\nPlease visit https://emulator.ac/how-to-play to download the complete DAT files.", "Warning message displayed (if show_dat_warning is true) to player if client attempts DAT download from server")),
                ("dat_newer_warning_msg", new Property<string>("Your DAT files are newer than expected.\nPlease visit https://emulator.ac/how-to-play to download the correct DAT files.", "Warning message displayed (if show_dat_warning is true) to player if client connects to this server")),
                ("popup_header", new Property<string>("Welcome to Asheron's Call!", "Welcome message displayed when you log in")),
                ("popup_welcome", new Property<string>("To begin your training, speak to the Society Greeter. Walk up to the Society Greeter using the 'W' key, then double-click on her to initiate a conversation.", "Welcome message popup in training halls")),
                ("popup_welcome_olthoi", new Property<string>("Welcome to the Olthoi hive! Be sure to talk to the Olthoi Queen to receive the Olthoi protections granted by the energies of the hive.", "Welcome message displayed on the first login for an Olthoi Player")),
                ("popup_motd", new Property<string>("", "Popup message of the day")),
                ("server_motd", new Property<string>("", "Server message of the day")),
                ("dynamic_dungeons_data_folder", new Property<string>("", "(non-retail function) overrides where Threads JSON files are read from; empty = <content_folder>/dungeons/dynamic, then exe-adjacent Content/dungeons/dynamic")),
                ("world_events_axis_folder", new Property<string>("", "(non-retail function) overrides where World Events axis JSON files are read from; empty = <content_folder>/events/axes, then exe-adjacent Content/events/axes")),
                ("world_events_ip_exempt", new Property<string>("", "(non-retail function) comma-separated IP addresses exempt from the World Events per-IP claim gate (shared households)")),
                ("top_exempt_accounts", new Property<string>("", "WaffleACE: comma-separated ACCOUNT names (not character names) excluded from every /top leaderboard and from the Proving Grounds new-record broadcasts. Accounts at AccessLevel Sentinel or above, and characters carrying the IsAdmin/IsArch/IsEnvoy/IsSentinel bools, are exempt inherently and do not need listing here - this list is for staff alternate accounts that run at normal Player access. Managed with the top-exempt admin command; a hand edit must keep the comma-separated form.")),
                ("fellowship_leech_warn_seconds", new Property<string>("600,300,60", "WaffleACE: comma-separated list of seconds-remaining marks at which a fellowship member is privately warned before /fship noleech leech management would eject them. Only the tightest mark crossed since the member's last contribution fires, so one warning is sent per idle window per mark. Empty disables warnings entirely.")),
                ("market_ad_sender_name", new Property<string>("Market Crier", "WaffleACE Market: the name shown as the speaker on the periodic Trade advert line (market_ad_interval_hours). The wire field this fills is a speaker guid slot with no real player behind it - see MarketAdvertiser's doc comment for why that is an open, live-client question. Keep it under 128 characters, matching every other Trade chat sender name")),
                ("market_ad_site_url", new Property<string>("trade.acdreamweave.com", "WaffleACE Market: the web address printed in the periodic Trade advert (market_ad_interval_hours), e.g. 'The Market is open at {this} - sign in with your game account name and password.'"))
                );
    }
}
