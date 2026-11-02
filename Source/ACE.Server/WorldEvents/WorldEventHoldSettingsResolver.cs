using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using ACE.Server.Managers;
using ACE.Server.WorldEvents.Defs;

using K = ACE.Server.WorldEvents.HoldKeys;

namespace ACE.Server.WorldEvents
{
    /// <summary>One resolved Hold setting: its value (number, flag or text) and where it came from.</summary>
    public sealed class WorldEventHoldSetting
    {
        public WorldEventSettingRow Row { get; init; }
        public SettingSource Source { get; init; }

        /// <summary>A Long or Double row's value; null when not set and there is neither a goal value nor a code default (no preference).</summary>
        public double? Number { get; init; }

        /// <summary>A Bool row's value.</summary>
        public bool? Flag { get; init; }

        /// <summary>A Choice or Text row's value (lower case for a Choice); null when not set and there is no goal value or code default.</summary>
        public string Text { get; init; }

        /// <summary>True when the property's own value was outside the row's range and was clamped.</summary>
        public bool Clamped { get; init; }
    }

    /// <summary>
    /// An immutable snapshot of every Hold setting for one goal: what a Hold run reads. Built by
    /// <see cref="WorldEventHoldSettingsResolver"/>; a consumer that applies "next wave" or "next spawn" resolves again at that moment.
    /// </summary>
    public sealed class WorldEventHoldSettings
    {
        private readonly IReadOnlyDictionary<string, WorldEventHoldSetting> values;

        internal WorldEventHoldSettings(IReadOnlyDictionary<string, WorldEventHoldSetting> values)
        {
            this.values = values;
        }

        public IEnumerable<string> Keys => values.Keys;

        public WorldEventHoldSetting this[string key] =>
            key != null && values.TryGetValue(key, out var v) ? v : throw new ArgumentException($"'{key}' is not a Hold setting", nameof(key));

        public SettingSource SourceOf(string key) => this[key].Source;

        /// <summary>A number row's value, or null (no preference).</summary>
        public double? Number(string key) => this[key].Number;

        /// <summary>A Long row's value rounded to a whole number, or null (no preference).</summary>
        public long? Whole(string key) => this[key].Number is double d ? (long)Math.Round(d, MidpointRounding.AwayFromZero) : (long?)null;

        /// <summary>A Bool row's value.</summary>
        public bool Flag(string key) => this[key].Flag ?? false;

        /// <summary>An on/off Choice row as a bool, or null when nothing decides it.</summary>
        public bool? OnOff(string key)
        {
            var t = this[key].Text;
            return t == WorldEventSettingsRegistry.On ? true : t == WorldEventSettingsRegistry.Off ? false : (bool?)null;
        }

        /// <summary>A Choice or Text row's value, or null.</summary>
        public string Text(string key) => this[key].Text;

        /// <summary>world_events_hold_enabled: Hold goals may start.</summary>
        public bool Enabled => Flag(K.Enabled);
    }

    /// <summary>
    /// Resolves the Hold settings for a goal. Pure over its inputs: the goal def and a property lookup.
    ///
    /// Per row: a SET property wins, clamped to the row's Min and Max. A row that is not set (by its unset rule) takes the
    /// goal def's value at the row's JsonPath, NOT clamped (the axis loader has already validated it; e.g. goals.json's
    /// shrineCooldownSeconds keeps its own 1 to 600 while the setting allows 0). With no JsonPath, or no goal value, it takes the
    /// row's code default - unless that default is itself "not set" under the rule (-1, 0 or empty), which resolves to null:
    /// "no preference", the consumer's own behaviour.
    /// </summary>
    public static class WorldEventHoldSettingsResolver
    {
        /// <summary>Resolves against the live PropertyManager values.</summary>
        public static WorldEventHoldSettings Resolve(GoalDef goal) => Resolve(goal, ReadProperty);

        /// <summary>
        /// Resolves with <paramref name="lookup"/>: property name to its stored value as invariant text, or null when the property
        /// holds nothing (its shipped default is used).
        /// </summary>
        public static WorldEventHoldSettings Resolve(GoalDef goal, Func<string, string> lookup) =>
            Resolve(goal, lookup, WorldEventSettingsRegistry.Default);

        public static WorldEventHoldSettings Resolve(GoalDef goal, Func<string, string> lookup, WorldEventSettingsRegistry registry)
        {
            if (lookup == null)
                throw new ArgumentNullException(nameof(lookup));

            var result = new Dictionary<string, WorldEventHoldSetting>(StringComparer.Ordinal);

            foreach (var row in registry.Rows.Where(r => r.Scope.Equals(WorldEventSettingsRegistry.HoldScope)))
                result[row.Key] = ResolveStored(row, goal, lookup(row.PropertyName));

            return new WorldEventHoldSettings(result);
        }

        // ---- one row: for a per-hit consumer that must not resolve every row ------------------------------------------

        /// <summary>One Hold setting against the live PropertyManager values.</summary>
        public static WorldEventHoldSetting ResolveRow(GoalDef goal, string key) => ResolveRow(goal, key, ReadProperty);

        /// <summary>One Hold setting with <paramref name="lookup"/> (as <see cref="Resolve(GoalDef, Func{string, string})"/>); an unknown or non-Hold key throws.</summary>
        public static WorldEventHoldSetting ResolveRow(GoalDef goal, string key, Func<string, string> lookup)
        {
            if (lookup == null)
                throw new ArgumentNullException(nameof(lookup));

            var row = HoldRow(key);
            return ResolveStored(row, goal, lookup(row.PropertyName));
        }

        /// <summary>A number row's value for one key (null = no preference).</summary>
        public static double? Number(GoalDef goal, string key) => ResolveRow(goal, key).Number;

        /// <summary>A Bool row's value for one key.</summary>
        public static bool Flag(GoalDef goal, string key) => ResolveRow(goal, key).Flag ?? false;

        /// <summary>A Choice or Text row's value for one key.</summary>
        public static string Text(GoalDef goal, string key) => ResolveRow(goal, key).Text;

        private static WorldEventSettingRow HoldRow(string key)
        {
            if (key != null && WorldEventSettingsRegistry.Default.TryFind(key, out var row) && row.Scope.Equals(WorldEventSettingsRegistry.HoldScope) &&
                string.Equals(row.Key, key, StringComparison.Ordinal))
                return row;

            throw new ArgumentException($"'{key}' is not a Hold setting", nameof(key));
        }

        internal static WorldEventHoldSetting ResolveStored(WorldEventSettingRow row, GoalDef goal, string stored)
        {
            var raw = stored ?? WorldEventSettingsRegistry.PropertyDefaultText(row);

            switch (row.Kind)
            {
                case SettingKind.Bool:
                {
                    var def = TryBool(row.CodeDefault, out var d) && d;
                    var b = TryBool(raw, out var v) ? v : def;
                    return new WorldEventHoldSetting { Row = row, Source = b == def ? SettingSource.Default : SettingSource.Property, Flag = b };
                }

                case SettingKind.Long:
                case SettingKind.Double:
                {
                    if (TryNumber(raw, out var n) && !WorldEventSettingsService.IsUnsetNumber(row, n))
                    {
                        var c = Math.Min(Math.Max(n, row.Min), row.Max);
                        return new WorldEventHoldSetting { Row = row, Source = SettingSource.Property, Number = c, Clamped = c != n };
                    }

                    if (row.JsonPath != null && TryNumber(GoalValue(row, goal), out var j))
                        return new WorldEventHoldSetting { Row = row, Source = SettingSource.Json, Number = j };

                    double? def = TryNumber(row.CodeDefault, out var cd) && !WorldEventSettingsService.IsUnsetNumber(row, cd) ? cd : (double?)null;
                    return new WorldEventHoldSetting { Row = row, Source = SettingSource.Default, Number = def };
                }

                default:
                {
                    var text = (raw ?? "").Trim();

                    if (row.Kind == SettingKind.Choice)
                        text = text.ToLowerInvariant();

                    // A Choice value that is not one of its choices is ignored (not set), as the settings service warns.
                    if (text.Length > 0 && (row.Kind == SettingKind.Text || row.Choices.Contains(text)))
                        return new WorldEventHoldSetting { Row = row, Source = SettingSource.Property, Text = text };

                    var json = row.JsonPath != null ? GoalValue(row, goal) : null;

                    if (!string.IsNullOrEmpty(json))
                        return new WorldEventHoldSetting { Row = row, Source = SettingSource.Json, Text = json };

                    return new WorldEventHoldSetting { Row = row, Source = SettingSource.Default, Text = string.IsNullOrEmpty(row.CodeDefault) ? null : row.CodeDefault };
                }
            }
        }

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

        private static bool TryBool(string text, out bool value)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "true": case "on": case "1": case "yes":
                    value = true;
                    return true;
                case "false": case "off": case "0": case "no":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        private static string GoalValue(WorldEventSettingRow row, GoalDef goal)
        {
            return goal != null && GoalValues.TryGetValue(row.Key, out var read) ? read(goal) : null;
        }

        private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static string N(double? v) => v is double d ? N(d) : null;
        private static string B(bool v) => v ? WorldEventSettingsRegistry.On : WorldEventSettingsRegistry.Off;

        /// <summary>
        /// The goal def's value for each row that has a JsonPath, as invariant text (null = the def has none). Keyed by row key;
        /// a test pins that every JsonPath row has exactly one entry here and nothing else does.
        /// </summary>
        internal static readonly IReadOnlyDictionary<string, Func<GoalDef, string>> GoalValues = new Dictionary<string, Func<GoalDef, string>>
        {
            // knights
            [K.KnightCount] = g => N(g.Protect?.Count?.Base),
            [K.UndefendedSeconds] = g => N(g.Protect?.UndefendedSeconds),
            [K.UndefendedSecondsPerParticipant] = g => N(g.Protect?.UndefendedSecondsPerParticipant),
            [K.UndefendedSecondsFloor] = g => N(g.Protect?.UndefendedSecondsFloor),
            [K.MinDeathSeconds] = g => N(g.Protect?.MinDeathSeconds),
            [K.MinSurvivors] = g => N(g.Protect?.MinSurvivors),
            [K.DefendPose] = g => g.Protect == null ? null : B(g.Protect.DefendPose != null),
            // As the contributor's defs: a pose forced on with no goals.json block uses a default DefendPoseDef's numbers.
            [K.DefendSwingSeconds] = g => g.Protect == null ? null : N((g.Protect.DefendPose ?? new DefendPoseDef()).SwingSeconds),
            [K.DefendSwingMaxSeconds] = g => g.Protect == null ? null : N((g.Protect.DefendPose ?? new DefendPoseDef()).SwingMaxSeconds),
            [K.HealthMilestones] = g => g.Protect == null ? null : B(g.Protect.HealthMilestones),

            // healing
            [K.Shrine] = g => g.Protect == null ? null : B(g.Protect.Shrine != null),
            [K.ShrineHeal] = g => N(g.Protect?.ShrineHeal),
            [K.ShrineCooldownSeconds] = g => N(g.Protect?.ShrineCooldownSeconds),
            // As the contributor's defs: a shrine forced on with no goals.json block uses a default ShrineDef's numbers.
            [K.ShrineUseRadius] = g => g.Protect == null ? null : N((g.Protect.Shrine ?? new ShrineDef()).UseRadius),
            [K.CorruptedKillHeal] = g => N(g.Protect?.CorruptedKillHeal),
            [K.RescueHeal] = g => N(g.Protect?.RescueHeal),
            [K.RescueCapPerWave] = g => N(g.Protect?.RescueCapPerWave),
            [K.RegenPerSecond] = g => N(g.Protect?.ClearRegenPerSecond),
            [K.RegenClearRadius] = g => N(g.Protect?.ClearRadius),
            [K.PlayerHealCap] = g => N(g.Protect?.MaxHealPerTenSeconds),
            [K.HealReductionAfterHit] = g => N(g.Protect?.HealReductionAfterHit),
            [K.HealReductionSeconds] = g => N(g.Protect?.HealReductionSeconds),
            [K.HealBetweenWaves] = g => N(g.Protect?.HealBetweenWaves),
            [K.HealBudgetPerWave] = g => N(g.Protect?.HealBudgetPerWave),

            // monsters
            [K.SeekShare] = g => N(g.Protect?.SeekShare),
            [K.ShareBreach] = g => N(g.Protect?.WaveMix?.Breach),
            [K.ShareRunners] = g => N(g.Protect?.WaveMix?.Runners),
            [K.ShareRanged] = g => N(g.Protect?.WaveMix?.Ranged),
            [K.ShareHunters] = g => N(g.Protect?.WaveMix?.Hunters),
            [K.ShareSeekers] = g => N(g.Protect?.WaveMix?.Seekers),
            [K.ShareCorrupted] = g => N(g.Protect?.WaveMix?.Corrupted),
            [K.MinBreach] = g => N(g.Protect?.WaveMix?.MinBreach),
            [K.MinRanged] = g => N(g.Protect?.WaveMix?.MinRanged),
            [K.MinCorrupted] = g => N(g.Protect?.WaveMix?.MinCorrupted),
            [K.MaxCorrupted] = g => N(g.Protect?.WaveMix?.MaxCorrupted),
            [K.RunnerHealthMult] = g => N(g.Protect?.SpecialWave(HoldWaveType.Runners)?.HealthMult),
            [K.RunnerSpeedMult] = g => N(g.Protect?.SpecialWave(HoldWaveType.Runners)?.SpeedMult),
            [K.RunnerAttackSpeedMult] = g => N(g.Protect?.SpecialWave(HoldWaveType.Runners)?.AttackSpeedMult),
            [K.RangedKeepDistance] = g => N(g.Protect?.SpecialWave(HoldWaveType.Ranged)?.HoldDistance),
            [K.CorruptedHealthMult] = g => N(g.Protect?.EffectiveCorrupted.HealthMult),
            [K.CorruptedSelfMult] = g => N(g.Protect?.EffectiveCorrupted.SelfMult),
            [K.CorruptedEmpowerMult] = g => N(g.Protect?.EffectiveCorrupted.EmpowerMult),
            [K.CorruptedAssaultEveryWaves] = g => N(g.Protect?.EffectiveCorrupted.AssaultEveryWaves),
            [K.CorruptedAssaultFromWave] = g => N(g.Protect?.EffectiveCorrupted.AssaultFromWave),
            [K.CorruptedAssaultCount] = g => N(g.Protect?.EffectiveCorrupted.AssaultCount),
            [K.CorruptedTint] = g => g.Protect?.EffectiveCorrupted.TintPalette,

            // waves
            [K.WaveCountBase] = g => N(g.Protect?.Waves?.Base),
            [K.WaveCountPerPlayer] = g => N(g.Protect?.Waves?.PerParticipant),
            [K.WaveCountCap] = g => N(g.Protect?.Waves?.Cap),
            [K.WaveSizeBase] = g => N(g.Protect?.WaveSize?.Base),
            [K.WaveSizePerPlayer] = g => N(g.Protect?.WaveSize?.PerParticipant),
            [K.WaveSizeCap] = g => N(g.Protect?.WaveSize?.Cap),
            [K.WaveIntervalSeconds] = g => N(g.Protect?.WaveIntervalSeconds),
            [K.BreatherSeconds] = g => N(g.Protect?.MinWaveGapSeconds),
            [K.MaxWaveIntervalSeconds] = g => N(g.Protect?.MaxWaveIntervalSeconds),
            [K.NextWaveAliveShare] = g => N(g.Protect?.NextWaveAliveShare),
            [K.SpawnRadius] = g => N(g.Protect?.SpawnRadius),
            [K.PortalCount] = g => N(g.Protect?.Portals?.Count),
            [K.PortalRadius] = g => N(g.Protect?.Portals?.Radius),
            [K.HoldSeconds] = g => g.HoldSeconds > 0 ? N(g.HoldSeconds) : null,

            // the finale boss
            [K.FinaleHealthMult] = g => N(g.Protect?.FinaleBossHealthMult),
            [K.FinaleScale] = g => N(g.Protect?.FinaleBossScale),
            [K.FinaleMinKillSeconds] = g => N(g.Protect?.BossMinKillSeconds),
            [K.FinaleLeadSeconds] = g => N(g.Protect?.FinaleBossLeadSeconds),

            // goals.json's bossTargetsProtected is a bool: true is "npcs"; false is the boss's own seeking, which no choice names.
            [K.FinaleTarget] = g => g.Protect?.BossTargetsProtected == true ? ProtectDef.FinaleTargetNpcs : null,
            [K.FinaleSwapSeconds] = g => N(g.Protect?.BossSwapSeconds),
            [K.FinaleSwapMaxSeconds] = g => N(g.Protect?.BossSwapMaxSeconds),
            [K.FinalePullShare] = g => N(g.Protect?.BossPullShare),
            [K.FinalePullWindowSeconds] = g => N(g.Protect?.BossPullWindowSeconds),
            [K.FinalePullSeconds] = g => N(g.Protect?.BossPullSeconds),
        };

        // ---- the live lookup ----------------------------------------------------------------------------------------

        private static string ReadProperty(string property)
        {
            if (!WorldEventSettingsRegistry.Default.TryFind(property, out var row))
                return null;

            // The registered default is the fallback, so a key missing from the cache reads as "shipped", never as 0 / false.
            var shipped = WorldEventSettingsRegistry.PropertyDefaultText(row);

            switch (row.Kind)
            {
                case SettingKind.Long:
                    return PropertyManager.GetLong(property, long.Parse(shipped, CultureInfo.InvariantCulture)).Item.ToString(CultureInfo.InvariantCulture);
                case SettingKind.Double:
                    return N(PropertyManager.GetDouble(property, double.Parse(shipped, NumberStyles.Float, CultureInfo.InvariantCulture)).Item);
                case SettingKind.Bool:
                    return PropertyManager.GetBool(property, bool.Parse(shipped)).Item ? "True" : "False";
                default:
                    return PropertyManager.GetString(property, shipped).Item ?? "";
            }
        }
    }
}
