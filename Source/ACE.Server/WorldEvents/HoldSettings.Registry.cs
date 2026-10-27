using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    /// <summary>
    /// The Hold settings table. One row per admin-tunable value, grouped as HOLD-SUMMARY.md "SETTINGS" lists them. A row's
    /// <see cref="HoldSetting.Effective"/> reads the value through the same accessor the game code uses, so
    /// "/worldevent hold settings" shows what a run actually gets.
    /// </summary>
    public static partial class HoldSettings
    {
        public const string GroupKnights = "knights";
        public const string GroupHealing = "healing";
        public const string GroupMonsters = "monsters";
        public const string GroupWaves = "waves";
        public const string GroupFinale = "finale";
        public const string GroupChampion = "champion";
        public const string GroupChain = "chain";
        public const string GroupRewards = "rewards";
        public const string GroupParticipation = "participation";
        public const string GroupMessages = "messages";
        public const string GroupInternals = "internals";

        /// <summary>Each group adds its rows here, in display order.</summary>
        private static void Register(List<HoldSetting> r)
        {
            RegisterKnights(r);
            RegisterHealing(r);
            RegisterMonsters(r);
            RegisterWaves(r);
            RegisterBosses(r);
            RegisterChain(r);
            RegisterRewards(r);
            RegisterParticipation(r);
            RegisterMessages(r);
            RegisterInternals(r);
            RegisterReused(r);
        }

        static partial void RegisterKnights(List<HoldSetting> r);
        static partial void RegisterHealing(List<HoldSetting> r);
        static partial void RegisterMonsters(List<HoldSetting> r);
        static partial void RegisterWaves(List<HoldSetting> r);
        static partial void RegisterBosses(List<HoldSetting> r);
        static partial void RegisterChain(List<HoldSetting> r);
        static partial void RegisterRewards(List<HoldSetting> r);
        static partial void RegisterParticipation(List<HoldSetting> r);
        static partial void RegisterMessages(List<HoldSetting> r);
        static partial void RegisterInternals(List<HoldSetting> r);

        // ---- row builders -------------------------------------------------------------------------------------

        private static string F(double v) => Format(v);
        private static string F(bool v) => Format(v);
        private static string F(int v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>A number setting: property, then goals.json at <paramref name="goalsPath"/> (or none), then <paramref name="def"/>.</summary>
        private static HoldSetting Num(string group, string key, double def, double min, double max, HoldApplies applies, string goalsPath, bool isNew,
            string description, Func<GoalDef, string> effective = null, string otherFile = null) =>
            new HoldSetting
            {
                Key = key, PropertyName = PropertyFor(key), Group = group, Kind = HoldSettingKind.Number, CodeDefault = F(def), Min = min, Max = max,
                Applies = applies, GoalsPath = goalsPath, OtherFile = otherFile, IsNew = isNew, Description = description, Effective = effective
            };

        private static HoldSetting Int(string group, string key, int def, int min, int max, HoldApplies applies, string goalsPath, bool isNew,
            string description, Func<GoalDef, string> effective = null, string otherFile = null, IReadOnlyDictionary<string, string> aliases = null) =>
            new HoldSetting
            {
                Key = key, PropertyName = PropertyFor(key), Group = group, Kind = HoldSettingKind.Integer, CodeDefault = F(def), Min = min, Max = max,
                Applies = applies, GoalsPath = goalsPath, OtherFile = otherFile, IsNew = isNew, Description = description, Effective = effective, Aliases = aliases
            };

        private static HoldSetting Tog(string group, string key, bool def, HoldApplies applies, string goalsPath, bool isNew, string description,
            Func<GoalDef, string> effective = null) =>
            new HoldSetting
            {
                Key = key, PropertyName = PropertyFor(key), Group = group, Kind = HoldSettingKind.Toggle, CodeDefault = F(def),
                Applies = applies, GoalsPath = goalsPath, IsNew = isNew, Description = description, Effective = effective
            };

        private static HoldSetting Pick(string group, string key, string def, string[] choices, HoldApplies applies, string goalsPath, bool isNew,
            string description, Func<GoalDef, string> effective = null) =>
            new HoldSetting
            {
                Key = key, PropertyName = PropertyFor(key), Group = group, Kind = HoldSettingKind.Choice, CodeDefault = def, Choices = choices,
                Applies = applies, GoalsPath = goalsPath, IsNew = isNew, Description = description, Effective = effective
            };

        private static HoldSetting TextSetting(string group, string key, string def, HoldApplies applies, string goalsPath, bool isNew, string description,
            Func<GoalDef, string> effective = null, string otherFile = null) =>
            new HoldSetting
            {
                Key = key, PropertyName = PropertyFor(key), Group = group, Kind = HoldSettingKind.Text, CodeDefault = def,
                Applies = applies, GoalsPath = goalsPath, OtherFile = otherFile, IsNew = isNew, Description = description, Effective = effective
            };

        /// <summary>An older typed property reused as is: listed and settable here, read by its own code as before.</summary>
        private static HoldSetting Reused(string group, string key, string property, string type, string def, double min, double max, HoldApplies applies,
            bool global, string description, string goalsPath = null, Func<GoalDef, string> effective = null, string otherFile = null) =>
            new HoldSetting
            {
                Key = key, PropertyName = property, Group = group, ExistingType = type,
                Kind = type == "bool" ? HoldSettingKind.Toggle : type == "long" ? HoldSettingKind.Integer : HoldSettingKind.Number,
                CodeDefault = def, Min = min, Max = max, Applies = applies, Global = global, Description = description, GoalsPath = goalsPath,
                Effective = effective, OtherFile = otherFile
            };

        // ---- List 1: the older properties reused as they are ---------------------------------------------------

        private static void RegisterReused(List<HoldSetting> r)
        {
            r.Add(Reused(GroupMonsters, "seek_share", "world_events_hold_seek_share", "double", "0", 0, 1, HoldApplies.NextWave, false,
                "Share of each wave that goes straight for the knights. 0 = use goals.json protect.seekShare.",
                "protect.seekShare", g => F(g.Protect?.EffectiveSeekShare ?? 0)));

            r.Add(Reused(GroupWaves, "max_alive", "world_events_max_alive", "long", "0", 0, 200, HoldApplies.NextWave, true,
                "Most wave monsters alive at once. 0 = use goals.json protect.maxAlive (else the source's).",
                "protect.maxAlive"));

            r.Add(Reused(GroupWaves, "teaser_lead_seconds", "world_events_teaser_lead_seconds", "long", "60", 0, 3600, HoldApplies.NextEvent, true,
                "Teaser countdown before a run is staged; 0 = no teaser. --teaser wins for one run."));

            r.Add(Reused(GroupMessages, "progress_interval_seconds", "world_events_progress_interval_seconds", "long", "120", 0, 3600, HoldApplies.Live, true,
                "Seconds between the global progress callouts while a run is active; 0 = none."));

            r.Add(Reused(GroupMessages, "announce_chat_type", "world_events_announce_chat_type", "long", "5", 0, 100, HoldApplies.Live, true,
                "Chat channel (ChatMessageType) of the global World Event lines."));

            r.Add(Reused(GroupChampion, "boss_scale_multiplier", "world_events_boss_scale_multiplier", "double", "0.67", 0.1, 5, HoldApplies.NextSpawn, true,
                "Size of every named boss (the champion included): its ObjScale times this."));

            r.Add(Reused(GroupChampion, "boss_hits_to_kill", "world_events_boss_hits_to_kill", "double", "3", 1, 50, HoldApplies.Live, true,
                "A named boss's damage to players is steered so the toughest player dies in this many ordinary hits."));

            r.Add(Reused(GroupChampion, "boss_damage_rating_min", "world_events_boss_damage_rating_min", "long", "-80", -200, 0, HoldApplies.Live, true,
                "Lowest DamageRating the named-boss damage steering may set."));

            r.Add(Reused(GroupChampion, "boss_damage_rating_max", "world_events_boss_damage_rating_max", "long", "300", 0, 1000, HoldApplies.Live, true,
                "Highest DamageRating the named-boss damage steering may set."));

            r.Add(Reused(GroupChampion, "boss_per_player", "world_events_boss_per_player", "double", "0", 0, 10, HoldApplies.NextSpawn, true,
                "Named-boss health per player (power curve). 0 = use bosses.json.", otherFile: "bosses.json"));

            r.Add(Reused(GroupChampion, "boss_health_cap", "world_events_boss_health_cap", "double", "0", 0, 1000, HoldApplies.NextSpawn, true,
                "Named-boss health multiplier cap (power curve). 0 = use bosses.json.", otherFile: "bosses.json"));

            r.Add(Reused(GroupChampion, "boss_throughput_floor_health", "world_events_boss_throughput_floor_health", "long", "0", 0, 100000000, HoldApplies.NextSpawn, true,
                "Named-boss health floor when sized from the wave phase. 0 = use bosses.json.", otherFile: "bosses.json"));

            r.Add(Reused(GroupChampion, "boss_throughput_cap_health", "world_events_boss_throughput_cap_health", "long", "0", 0, 100000000, HoldApplies.NextSpawn, true,
                "Named-boss health cap when sized from the wave phase. 0 = use bosses.json.", otherFile: "bosses.json"));

            r.Add(Reused(GroupRewards, "participation_payout_enabled", "world_events_participation_payout_enabled", "bool", "on", 0, 1, HoldApplies.Live, true,
                "A claimant with no damage or kill credit gets the coal prize instead of the crate."));
        }
    }
}
