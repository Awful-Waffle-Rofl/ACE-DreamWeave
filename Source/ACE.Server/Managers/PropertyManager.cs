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
                ("account_login_boots_in_use", new Property<bool>(true, "if FALSE, oldest connection to account is not booted when new connection occurs")),
                ("advanced_combat_pets", new Property<bool>(false, "(non-retail function) If enabled, Combat Pets can cast spells")),
                ("advocate_fane_auto_bestow", new Property<bool>(false, "If enabled, Advocate Fane will automatically bestow new advocates to advocate_fane_auto_bestow_level")),
                ("aetheria_heal_color", new Property<bool>(false, "If enabled, changes the aetheria healing over time messages from the default retail red color to green")),
                ("allow_combat_mode_crafting", new Property<bool>(false, "If enabled, allows players to do crafting (recipes) from all stances. Forces players to NonCombat first, then continues to recipe action.")),
                ("allow_door_hold", new Property<bool>(true, "enables retail behavior where standing on a door while it is closing keeps the door as ethereal until it is free from collisions, effectively holding the door open for other players")),
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
                ("discord_relay_enabled", new Property<bool>(false, "if TRUE, relay General and Trade chat to their per-channel Discord webhooks (discord_webhook_url_general / discord_webhook_url_trade). Overridden at startup by the ACE_DISCORD_RELAY_ENABLED environment variable when set")),
                ("chess_enabled", new Property<bool>(true, "if FALSE then chess will be disabled")),
                ("class_abilities_enabled", new Property<bool>(false, "(non-retail function) enables the class ability system: the /abilities command, class ability point items, and all class ability combat effects")),
                ("enlightenment_broadcast_enabled", new Property<bool>(true, "(non-retail function) if TRUE, each successful enlightenment is announced with a server-wide broadcast")),
                ("equipment_mods_enabled", new Property<bool>(false, "(non-retail function) enables the equipment mod system: applying salvage-based mods to eligible gear, their combat effects, and their appraisal display. While FALSE, stored mod values are inert and invisible")),
                ("weapon_mods_enabled", new Property<bool>(false, "(non-retail function) THE SINGLE MASTER SWITCH for the whole weapon mod system: the Tourmaline reroll and the Amethyst swap on weapons, their appraisal display, AND every Tier B combat effect (the leeches, Ambush, Quickening, Overload and Second Wind). While FALSE both salvage bags fall through to ordinary tinkering, no Tier B modifier can be rolled by any pool, and every Tier B combat effect reads zero - including on a weapon that already carries a record from when it was TRUE")),
                ("use_cloak_proc_custom_scale", new Property<bool>(false, "If TRUE, the calculation for cloak procs will be based upon the values set by the server oeprator.")),
                ("client_movement_formula", new Property<bool>(false, "If enabled, server uses DoMotion/StopMotion self-client movement methods instead of apply_raw_movement")),
                ("container_opener_name", new Property<bool>(false, "If enabled, when a player tries to open a container that is already in use by someone else, replaces 'someone else' in the message with the actual name of the player")),
                ("corpse_decay_tick_logging", new Property<bool>(false, "If ENABLED then player corpse ticks will be logged")),
                ("corpse_destroy_pyreals", new Property<bool>(true, "If FALSE then pyreals will not be completely destroyed on player death")),
                ("craft_exact_msg", new Property<bool>(false, "If TRUE, and player has crafting chance of success dialog enabled, shows them an additional message in their chat window with exact %")),
                ("creature_name_check", new Property<bool>(true, "if enabled, creature names in world database restricts player names during character creation")),
                ("creatures_drop_createlist_wield", new Property<bool>(false, "If FALSE then Wielded items in CreateList will not drop. Retail defaulted to TRUE but there are currently data errors")),
                ("fastbuff", new Property<bool>(true, "If TRUE, enables the fast buffing trick from retail.")),
                ("fellow_busy_no_recruit", new Property<bool>(true, "if FALSE, fellows can be recruited while they are busy, different from retail")),
                ("fellow_kt_killer", new Property<bool>(true, "if FALSE, fellowship kill tasks will share with the fellowship, even if the killer doesn't have the quest")),
                ("fellow_kt_landblock", new Property<bool>(false, "if TRUE, fellowship kill tasks will share with landblock range (192 distance radius, or entire dungeon)")),
                ("fellow_quest_bonus", new Property<bool>(false, "if TRUE, applies EvenShare formula to fellowship quest reward XP (300% max bonus, defaults to false in retail)")),
                ("fellowship_level_restrictions", new Property<bool>(false, "WaffleACE: if TRUE, restores retail level-based fellowship XP restrictions (even share only within 5 levels of the leader or all >= 'fellowship_even_share_level'; proportional to 10; no sharing beyond). Defaults FALSE - content is gated by portal restrictions instead, so fellowships always share evenly regardless of level spread.")),
                ("fellowship_leech_check_default", new Property<bool>(false,"WaffleACE: default state of leech management for newly created fellowships. When enabled, members who do not contribute XP to the fellowship within the timeout window are auto-ejected. Leaders can toggle per-fellowship with /fship noleech on|off.")),
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
                ("item_dispel", new Property<bool>(false, "if enabled, allows players to dispel items. defaults to end of retail, where item dispels could only target creatures")),
                ("lifestone_broadcast_death", new Property<bool>(true, "if true, player deaths are additionally broadcast to other players standing near the destination lifestone")),
                ("loot_quality_mod", new Property<bool>(true, "if FALSE then the loot quality modifier of a Death Treasure profile does not affect loot generation")),
                ("mule_system_enabled", new Property<bool>(false, "WaffleACE: master switch for the mule system. Gates ONLY the conversion path (the NPC emote / MuleConversion.HandleMuleRequest). The restriction guards always apply to any character already flagged IsMule regardless of this switch, so turning it off cannot unlock existing mules - see Player_Mule")),
                ("npc_hairstyle_fullrange", new Property<bool>(false, "if TRUE, allows generated creatures to use full range of hairstyles. Retail only allowed first nine (0-8) out of 51")),
                ("offline_xp_passup_limit", new Property<bool>(true, "if FALSE, allows unlimited xp to passup to offline characters in allegiances")),
                ("olthoi_play_disabled", new Property<bool>(false, "if false, allows players to create and play as olthoi characters")),
                ("override_encounter_spawn_rates", new Property<bool>(false, "if enabled, landblock encounter spawns are overidden by double properties below.")),
                ("permit_corpse_all", new Property<bool>(false, "If TRUE, /permit grants permittees access to all corpses of the permitter. Defaults to FALSE as per retail, where /permit only grants access to 1 locked corpse")),
                ("persist_movement", new Property<bool>(false, "If TRUE, persists autonomous movements such as turns and sidesteps through non-autonomous server actions. Retail didn't appear to do this, but some players may prefer this.")),
                ("pet_stow_replace", new Property<bool>(false, "pet stowing for different pet devices becomes a stow and replace. defaults to retail value of false")),
                ("player_config_command", new Property<bool>(false, "If enabled, players can use /config to change their settings via text commands")),
                ("player_config_command_resend_description", new Property<bool>(false, "DIAGNOSTIC ONLY. If enabled, /config resends the full GameEventPlayerDescription after a successful change, as it did historically. That resend reliably wedges the client and the session dies of Network Timeout about a minute later, so this defaults to FALSE. Only turn it on to reproduce the defect under a packet capture")),
                ("player_death_no_item_loss", new Property<bool>(false, "If TRUE, players drop nothing on death server-wide: no items, no coins, and no corpse is left behind. Vitae and the XP death penalty are unaffected. Defaults to FALSE, as per retail")),
                ("player_receive_immediate_save", new Property<bool>(false, "if enabled, when the player receives items from an NPC, they will be saved immediately")),
                ("pk_server", new Property<bool>(false, "set this to TRUE for darktide servers")),
                ("pk_server_safe_training_academy", new Property<bool>(false, "set this to TRUE to disable pk fighting in training academy and time to exit starter town safely")),
                ("pkl_server", new Property<bool>(false, "set this to TRUE for pink servers")),
                ("quest_info_enabled", new Property<bool>(false, "toggles the /myquests player command")),
                ("rares_real_time", new Property<bool>(true, "allow for second chance roll based on an rng seeded timestamp for a rare on rare eligible kills that do not generate a rare, rares_max_seconds_between defines maximum seconds before second chance kicks in")),
                ("rares_real_time_v2", new Property<bool>(false, "chances for a rare to be generated on rare eligible kills are modified by the last time one was found per each player, rares_max_days_between defines maximum days before guaranteed rare generation")),
                ("runrate_add_hooks", new Property<bool>(false, "if TRUE, adds some runrate hooks that were missing from retail (exhaustion done, raise skill/attribute")),
                ("reportbug_enabled", new Property<bool>(false, "toggles the /reportbug player command")),
                ("require_spell_comps", new Property<bool>(true, "if FALSE, spell components are no longer required to be in inventory to cast spells. defaults to enabled, as in retail")),
                ("safe_spell_comps", new Property<bool>(false, "if TRUE, disables spell component burning for everyone")),
                ("salvage_handle_overages", new Property<bool>(false, "in retail, if 2 salvage bags were combined beyond 100 structure, the overages would be lost")),
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
                ("version_info_enabled", new Property<bool>(false, "toggles the /aceversion player command")),
                ("vendor_shop_uses_generator", new Property<bool>(false, "enables or disables vendors using generator system in addition to createlist to create artificial scarcity")),
                ("world_closed", new Property<bool>(false, "enable this to startup world as a closed to players world"))
                );

        public static readonly ReadOnlyDictionary<string, Property<long>> DefaultLongProperties =
            DictOf(
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
                ("class_ability_full_respec_lum_cost", new Property<long>(1000000, "class abilities: flat Luminance fee at the Drift Network full-respec NPC, which unlearns every learned class ability and refunds all spent points at once (tier unlocks are not refunded and not reset)")),
                ("class_ability_respec_lum_cost", new Property<long>(1000000, "class abilities: Luminance fee to unlearn a class ability (/abilities unlearn)")),
                ("class_ability_token_lum_per_point", new Property<long>(100000, "class abilities: Luminance cost of a skill token, per class ability point that token's rank costs (/abilities token buy)")),
                ("class_ability_tier2_cap_required", new Property<long>(3, "class abilities: total class ability points a character must have earned before any Tier 2 skill can be learned")),
                ("class_ability_tier2_spent_required", new Property<long>(5, "class abilities: points a character must have spent within a class before that class's Tier 2 skills unlock")),
                ("class_ability_tier3_cap_required", new Property<long>(8, "class abilities: total class ability points a character must have earned before any Tier 3 skill can be learned")),
                ("class_ability_tier3_spent_required", new Property<long>(15, "class abilities: points a character must have spent within a class before that class's Tier 3 skills unlock")),
                ("corpse_spam_limit", new Property<long>(15, "the number of corpses a player is allowed to leave on a landblock at one time")),
                ("default_subscription_level", new Property<long>(1, "retail defaults to 1, 1 = standard subscription (same as 2 and 3), 4 grants ToD pre-order bonus item Asheron's Benediction")),
                ("fellowship_even_share_level", new Property<long>(50, "level when fellowship XP sharing is no longer restricted")),
                ("fellowship_leech_rejoin_lockout", new Property<long>(3600, "WaffleACE: seconds a player ejected by /fship noleech leech management must wait before rejoining that same fellowship. Defaults to 3600 (1 hour).")),
                ("fellowship_leech_timeout", new Property<long>(600, "WaffleACE: seconds a member may go without contributing XP to the fellowship before /fship noleech ejection removes them. Defaults to 600 (10 minutes).")),
                ("fellowship_max_members", new Property<long>(20, "WaffleACE: maximum number of members in a fellowship. Retail is 9. Clamped to [1, 100]. Total group XP is held flat past 9 by 'fellowship_share_group_plateau', so a larger fellowship never out-earns a smaller one.")),
                ("mansion_min_rank", new Property<long>(6, "overrides the default allegiance rank required to own a mansion")),
                ("offline_bonus_max_seconds", new Property<long>(86400, "Offline bonus: maximum banked offline bonus time, in seconds, a character can accrue. Defaults to 86400 (24 hours).")),
                ("max_chars_per_account", new Property<long>(11, "retail defaults to 11, client supports up to 20")),
                ("mule_strength", new Property<long>(3300, "Mule system: the Strength a mule is given at conversion, which is what buys its carrying capacity (retail formula, 150 * Strength, so 3300 gives 495,000). Capacity must be bought this way rather than overridden: verified in game 2026-08-02, the client derives the burden bar from Strength and ignores a server-sent EncumbranceCapacity, so an override reads as a permanently overloaded mule. Raising this also raises Jump, which is why mules are exempt from fall damage - see Player_Mule and Player_Move.TakeDamage_Falling. Clamped to 1-9999 at conversion")),
                ("mule_level", new Property<long>(180, "Mule system: the level a character is set to on mule conversion. TotalExperience is set to this level's threshold on the same curve CheckForLevelup reads, so level and XP cannot desync")),
                ("mule_max_convert_level", new Property<long>(50, "Mule system: highest character level still eligible for mule conversion. This is the ONLY progress gate - a character is level 8+ on leaving the training hall, and every character holds quest registry rows from the stamp ledger, so level is the only workable measure. Raising it lets more developed characters be converted; conversion is irreversible either way")),
                ("mule_max_per_account", new Property<long>(2, "Mule system: how many mules one account may own. Checked against the account's other characters carrying the IsMule property at conversion time")),
                ("mule_pack_wcid", new Property<long>(1001941, "Mule system: wcid of the Bearer's Pack handed to a character by MuleConversion.HandleMuleRequest, once on conversion and again on every subsequent interaction with an already-converted mule. No limit on repeat grants")),
                ("pk_timer", new Property<long>(20, "the number of seconds where a player cannot perform certain actions (ie. teleporting) after becoming involved in a PK battle")),
                ("player_save_interval", new Property<long>(300, "the number of seconds between automatic player saves")),
                ("rares_max_days_between", new Property<long>(45, "for rares_real_time_v2: the maximum number of days a player can go before a rare is generated on rare eligible creature kills")),
                ("rares_max_seconds_between", new Property<long>(5256000, "for rares_real_time: the maximum number of seconds a player can go before a second chance at a rare is allowed on rare eligible creature kills that did not generate a rare")),
                ("summoning_killtask_multicredit_cap", new Property<long>(2, "if allow_summoning_killtask_multicredit is enabled, the maximum # of killtask credits a player can receive from 1 kill")),
                ("teleport_visibility_fix", new Property<long>(0, "Fixes some possible issues with invisible players and mobs. 0 = default / disabled, 1 = players only, 2 = creatures, 3 = all world objects"))
                );

        public static readonly ReadOnlyDictionary<string, Property<double>> DefaultDoubleProperties =
            DictOf(

                ("cantrip_drop_rate", new Property<double>(1.0, "Scales the chance for cantrips to drop in each tier. Defaults to 1.0, as per end of retail")),
                ("cloak_cooldown_seconds", new Property<double>(5.0, "The number of seconds between possible cloak procs.")),
                ("cloak_max_proc_base", new Property<double>(0.25, "The max proc chance of a cloak.")),
                ("cloak_max_proc_damage_percentage", new Property<double>(0.30, "The damage percentage at which cloak proc chance plateaus.")),
                ("cloak_min_proc", new Property<double>(0, "The minimum proc chance of a cloak.")),
                ("class_ability_multishot_damage_mult", new Property<double>(0.75, "class abilities: damage multiplier for extra arrows granted by the Multishot class ability (weapon-granted extra arrows use the weapon's own multiplier)")),
                ("class_ability_thorns_percent_per_rank", new Property<double>(0.10, "class abilities: fraction of the equipped shield's effective armor level that the Thorns class ability reflects back per rank")),
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
                ("class_ability_spellaoe_damage_mult", new Property<double>(0.5, "class abilities: base fraction of normal spell damage dealt by each Spell AOE secondary (radiated) blast; 0.5 = half damage. Scales up toward class_ability_spellaoe_damage_mult_cap with Mana Conversion")),
                ("class_ability_lum_ratio_1", new Property<double>(1.25, "class abilities: per-point cost multiplier of the Luminance-purchase curve for points up to breakpoint_1 (the early handoff segment, DESIGN sec 2b)")),
                ("class_ability_lum_ratio_2", new Property<double>(1.5, "class abilities: per-point cost multiplier of the Luminance-purchase curve between breakpoint_1 and breakpoint_2 (the mid-game)")),
                ("class_ability_lum_ratio_3", new Property<double>(1.75, "class abilities: per-point cost multiplier of the Luminance-purchase curve past breakpoint_2 (the uncapped tail, forever)")),
                ("class_ability_spellaoe_damage_mult_cap", new Property<double>(0.75, "class abilities: upper cap on the Spell AOE radiated-blast damage fraction after the Mana Conversion rider (0.75 = 75%)")),
                ("class_ability_spellaoe_manaconv_per_trained", new Property<double>(20.0, "class abilities: points of effective Mana Conversion per +1% Spell AOE radiated-damage fraction, Trained source")),
                ("class_ability_spellaoe_manaconv_per_spec", new Property<double>(15.0, "class abilities: points of effective Mana Conversion per +1% Spell AOE radiated-damage fraction, Specialized source")),
                ("class_ability_spellaoe_mana_surcharge", new Property<double>(1.0, "class abilities: extra mana cost fraction added to a qualifying Arc war cast when Spell AOE is learned (1.0 = +100%); stacks additively with class_ability_overchannel_mana_surcharge")),
                ("class_ability_poisonweapon_alchemy_per_trained", new Property<double>(12.0, "class abilities: points of effective Alchemy (above the threshold) per +1 flat Poison Weapon damage, Trained source; smaller = stronger")),
                ("class_ability_poisonweapon_alchemy_per_spec", new Property<double>(9.0, "class abilities: points of effective Alchemy (above the threshold) per +1 flat Poison Weapon damage, Specialized source (tighter dual-ratio divisor)")),
                ("class_ability_poisonweapon_alchemy_threshold", new Property<double>(100.0, "class abilities: effective Alchemy below this contributes nothing to Poison Weapon's flat bonus (back-loads the curve to the late game)")),
                ("class_ability_poisonweapon_proc_delay", new Property<double>(0.1, "class abilities: seconds the Poison Weapon proc is deferred after the weapon strike so its damage + combat line land just after the main hit (0 = next tick)")),
                ("class_ability_multishot_assess_per_trained", new Property<double>(20.0, "class abilities: points of effective Assess Creature per +1% Multishot volley damage, Trained source; smaller = stronger")),
                ("class_ability_multishot_assess_per_spec", new Property<double>(15.0, "class abilities: points of effective Assess Creature per +1% Multishot volley damage, Specialized source (tighter dual-ratio divisor)")),
                ("class_ability_thorns_shield_per_trained", new Property<double>(25.0, "class abilities: points of effective Shield per +1% of shield armor level added to the Thorns reflect fraction, Trained source; smaller = stronger")),
                ("class_ability_thorns_shield_per_spec", new Property<double>(18.0, "class abilities: points of effective Shield per +1% of shield armor level added to the Thorns reflect fraction, Specialized source (tighter dual-ratio divisor)")),
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
                // Blood Fury (Berserker T2): melee damage rises as the attacker's health falls
                ("class_ability_bloodfury_peak_per_rank", new Property<double>(0.10, "class abilities: peak melee damage bonus per rank of Blood Fury at/below the low-health threshold (0.10 = +10%/rank)")),
                ("class_ability_bloodfury_start_hp_fraction", new Property<double>(0.75, "class abilities: attacker health fraction at/below which Blood Fury begins to ramp up")),
                ("class_ability_bloodfury_peak_hp_fraction", new Property<double>(0.25, "class abilities: attacker health fraction at/below which Blood Fury reaches its full bonus")),
                // Executioner (Berserker T2): +4/8/12% melee damage vs low-health targets
                ("class_ability_executioner_percent_per_rank", new Property<double>(0.04, "class abilities: melee damage bonus per rank of Executioner vs targets below the execute threshold (0.04 = +4%/rank)")),
                ("class_ability_executioner_hp_fraction", new Property<double>(0.25, "class abilities: target health fraction below which Executioner applies")),
                ("class_ability_executioner_dirty_per_trained", new Property<double>(25.0, "class abilities: points of effective Dirty Fighting per +1% Executioner damage, Trained source")),
                ("class_ability_executioner_dirty_per_spec", new Property<double>(18.0, "class abilities: points of effective Dirty Fighting per +1% Executioner damage, Specialized source")),
                // Bloodlust (Berserker T3): melee lifesteal
                ("class_ability_bloodlust_percent_per_rank", new Property<double>(0.01, "class abilities: fraction of melee damage dealt healed back per rank of Bloodlust (0.01 = 1%/rank)")),
                ("class_ability_bloodlust_salvage_per_trained", new Property<double>(150.0, "class abilities: points of effective Salvaging per +1 percentage point of Bloodlust lifesteal, Trained source")),
                ("class_ability_bloodlust_salvage_per_spec", new Property<double>(150.0, "class abilities: points of effective Salvaging per +1 percentage point of Bloodlust lifesteal (Salvaging cannot specialize, so same as Trained)")),
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
                // Acid Proc (Rogue T3): chance a landed weapon hit applies an acid DoT = share of Poison Weapon flat
                ("class_ability_acidproc_chance_base", new Property<double>(0.08, "class abilities: Acid Proc chance at rank 1 (0.08 = 8%)")),
                ("class_ability_acidproc_chance_step", new Property<double>(0.06, "class abilities: Acid Proc chance added per rank above 1 (8/14/20% at ranks 1-3)")),
                ("class_ability_acidproc_dot_fraction", new Property<double>(1.0, "class abilities: Acid Proc per-tick damage as a fraction of the current Poison Weapon flat bonus (1.0 = 100%)")),
                ("class_ability_acidproc_dot_interval", new Property<double>(4.0, "class abilities: seconds between Acid Proc DoT ticks (4s)")),
                ("class_ability_acidproc_itemtink_per_trained", new Property<double>(25.0, "class abilities: points of effective Item Tinkering per +1% Acid Proc chance, Trained source")),
                ("class_ability_acidproc_itemtink_per_spec", new Property<double>(18.0, "class abilities: points of effective Item Tinkering per +1% Acid Proc chance, Specialized source")),
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
                ("class_ability_affinity_chance_cap", new Property<double>(0.20, "class abilities: maximum PROC CHANCE an affinity rider may contribute, as a fraction (0.20 = +20 percentage points, reached at 500 points of the source skill). GetClassAbilityScaling returns a raw quotient (skill / divisor) with no bound of its own, so without this a high source skill saturates a proc to a literal 100% - observed live 2026-08-04 at Item Enchantment 5226 (+209 points). Applies to every chance-on-hit ability carrying an affinity: Spellblade, Runeblade, Sundermark, Acid Proc, Elemental Rend, Dispelling Edge and Shield Block. Spellstorm has no affinity and is unaffected. Spell AOE's rider is a damage FRACTION rather than a chance and is deliberately out of scope. 0 = uncapped")),
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
                // Equipment mods: per-mod maximum magnitudes live in EquipmentModRegistry as constants; an item
                // stores only a potency scalar 0-1, so these two tunables move the whole layer at once
                ("equipment_mod_lowtier_potency", new Property<double>(0.2, "equipment mods: the potency a low-tier (single-mod) application grants, as a fraction of the mod's registry maximum (0.2 = 20% of max). Not a roll - the low tier is a guaranteed floor")),
                ("equipment_mod_potency_scale", new Property<double>(1.0, "equipment mods: global multiplier on every mod's resolved magnitude (applied = stored potency x registry max x this). 1.0 = registry values as designed. Dials the entire power layer live, with no item or schema impact")),
                // Weapon mods: per-modifier maximum rolls live in WeaponModRegistry as constants. The three
                // special_chance values are CUMULATIVE - P(at least one), P(at least two), P(at least three) -
                // not conditional gates, and must decrease monotonically. Defaults give none 65%, one 25%,
                // two 8%, three 2%.
                ("weapon_mod_special_chance_1", new Property<double>(0.35, "weapon mods: P(a Tourmaline reroll produces AT LEAST ONE special modifier). Cumulative, so it must be >= weapon_mod_special_chance_2")),
                ("weapon_mod_special_chance_2", new Property<double>(0.10, "weapon mods: P(a Tourmaline reroll produces AT LEAST TWO special modifiers). Cumulative, so it must sit between weapon_mod_special_chance_1 and weapon_mod_special_chance_3")),
                ("weapon_mod_special_chance_3", new Property<double>(0.02, "weapon mods: P(a Tourmaline reroll produces ALL THREE special modifiers). Cumulative, and three is a permanent per-weapon bound - raising this can never produce a fourth")),
                ("weapon_mod_magnitude_scale", new Property<double>(1.0, "weapon mods: global multiplier on a special modifier's rolled magnitude (applied = max roll x potency x workmanship/10 x this). Applied AT ROLL TIME only: the applied magnitude is stored, so changing this never retunes modifiers already on existing weapons")),
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
                ("alt_character_bonus_multiplier", new Property<double>(1.0, "Alt character bonus: fractional boost added to a character's leveling XP (kills, quest turn-ins, and its share of a fellow's kill) while it is below the highest enlightenment+level character on its account. 1.0 = +100% (double). Multiplicative with the offline bonus. Does not apply to allegiance passup or item XP.")),
                ("melee_max_angle", new Property<double>(0.0, "for melee players, the maximum angle before a TurnTo is required. retail appeared to have required a TurnTo even for the smallest of angle offsets.")),
                ("mob_awareness_range", new Property<double>(1.0, "Scales the distance the monsters become alerted and aggro the players")),
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
                ("xp_modifier", new Property<double>(1.0, "scales the amount of xp received by players"))
                );

        public static readonly ReadOnlyDictionary<string, Property<string>> DefaultStringProperties =
            DictOf(
                ("content_folder", new Property<string>("Content", "for content creators to live edit weenies. defaults to Content folder found in same directory as ACE.Server.dll")),
                ("mule_bind_position", new Property<string>("0x016C019E 28.850000 -40.738000 0.005000 -0.394134 0.000000 0.000000 -0.919053", "Mule system: loc string (cell x y z qw qx qy qz - same token order as /teleloc) that ApplyMuleConversion parses via AdminCommands.TryParseLocPosition and sets as PositionType.Sanctuary, mirroring a real lifestone /use. Defaults to the Marketplace mule-conversion lifestone (wcid 509 instance at wcid-registry guid 0x7016C081, realm 0). A parse failure logs a warning and skips the bind rather than throwing - a bad config string must never break the conversion itself")),
                ("discord_webhook_url_general", new Property<string>("", "Discord webhook URL that General chat is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_GENERAL environment variable when set")),
                ("discord_webhook_url_trade", new Property<string>("", "Discord webhook URL that Trade chat is relayed to when discord_relay_enabled is set. Treat as a secret - overridden at startup by the ACE_DISCORD_WEBHOOK_URL_TRADE environment variable when set")),
                ("dat_older_warning_msg", new Property<string>("Your DAT files are incomplete.\nThis server does not support dynamic DAT updating at this time.\nPlease visit https://emulator.ac/how-to-play to download the complete DAT files.", "Warning message displayed (if show_dat_warning is true) to player if client attempts DAT download from server")),
                ("dat_newer_warning_msg", new Property<string>("Your DAT files are newer than expected.\nPlease visit https://emulator.ac/how-to-play to download the correct DAT files.", "Warning message displayed (if show_dat_warning is true) to player if client connects to this server")),
                ("popup_header", new Property<string>("Welcome to Asheron's Call!", "Welcome message displayed when you log in")),
                ("popup_welcome", new Property<string>("To begin your training, speak to the Society Greeter. Walk up to the Society Greeter using the 'W' key, then double-click on her to initiate a conversation.", "Welcome message popup in training halls")),
                ("popup_welcome_olthoi", new Property<string>("Welcome to the Olthoi hive! Be sure to talk to the Olthoi Queen to receive the Olthoi protections granted by the energies of the hive.", "Welcome message displayed on the first login for an Olthoi Player")),
                ("popup_motd", new Property<string>("", "Popup message of the day")),
                ("server_motd", new Property<string>("", "Server message of the day"))
                );
    }
}
