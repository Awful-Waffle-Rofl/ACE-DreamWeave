using System;
using System.Collections.Generic;
using System.Globalization;

using ACE.Server.WorldEvents.Defs;

namespace ACE.Server.WorldEvents
{
    public static partial class HoldSettings
    {
        public const int KnightHealthMin = 1000;
        public const int KnightHealthMax = 100000;

        private static readonly IReadOnlyDictionary<string, string> FailConditionWords = new Dictionary<string, string>
        {
            ["all"] = "1",
            ["any"] = "3"
        };

        static partial void RegisterKnights(List<HoldSetting> r)
        {
            r.Add(Int(GroupKnights, HoldKeys.KnightCount, 3, 1, ProtectDef.MaxProtectedTargets, HoldApplies.NextEvent, "protect.count.base", false,
                "How many knights are placed.", g => F(g.Protect?.ResolveCount(1) ?? 0)));

            r.Add(Int(GroupKnights, HoldKeys.KnightMaxHealth, ProtectDef.TimeScaledMaxHealth, KnightHealthMin, KnightHealthMax, HoldApplies.Live, null, true,
                "Each knight's max health (time-based damage). A change rescales standing knights and keeps their health percentage. " +
                "How long a knight lasts is set by undefended_seconds and min_death_seconds, not by this.",
                g => F(g.Protect?.KnightMaxHealth ?? 0)));

            r.Add(TextSetting(GroupKnights, HoldKeys.KnightMaxHealthByWcid, "", HoldApplies.Live, null, true,
                "Max health per named knight, by wcid: \"2000005=12000,2000007=8000\" (Sir Draithon 2000005, Sir Donovan 2000006, Sir Adarl 2000007). " +
                $"A knight not listed uses knight_max_health. Each value {KnightHealthMin} to {KnightHealthMax}."));

            r.Add(Num(GroupKnights, HoldKeys.KnightDamageTakenMult, 1, 0, 5, HoldApplies.Live, null, true,
                "Multiplies every monster hit on a knight (the finale boss's included), before the min_death_seconds cap. 1 = as today."));

            r.Add(Num(GroupKnights, HoldKeys.UndefendedSeconds, 200, 20, 3600, HoldApplies.Live, "protect.undefendedSeconds", false,
                "Seconds one attacker alone takes to destroy a knight (solo).", g => F(g.Protect?.UndefendedSeconds ?? 0)));

            r.Add(Num(GroupKnights, HoldKeys.UndefendedSecondsPerParticipant, 65, 0, 600, HoldApplies.Live, "protect.undefendedSecondsPerParticipant", false,
                "Seconds taken off undefended_seconds per player beyond the first.", g => F(g.Protect?.UndefendedSecondsPerParticipant ?? 0)));

            r.Add(Num(GroupKnights, HoldKeys.UndefendedSecondsFloor, 100, 0, 3600, HoldApplies.Live, "protect.undefendedSecondsFloor", false,
                "The lowest undefended_seconds can go with more players.", g => F(g.Protect?.UndefendedSecondsFloor ?? 0)));

            r.Add(Num(GroupKnights, HoldKeys.MinDeathSeconds, 60, 0, 600, HoldApplies.Live, "protect.minDeathSeconds", false,
                "However many monsters pile on, a knight lasts at least this long from full health (0 = no floor).", g => F(g.Protect?.MinDeathSeconds ?? 0)));

            r.Add(Int(GroupKnights, HoldKeys.MinSurvivors, 1, 1, ProtectDef.MaxProtectedTargets, HoldApplies.Live, "protect.minSurvivors", false,
                "The fail condition: the run fails when fewer knights than this stand. \"all\" (1) = fails when all are dead; " +
                "\"any\" (3, clamped to the knights placed) = fails at the first death.",
                g => F(g.Protect?.MinSurvivors ?? 0), aliases: FailConditionWords));

            r.Add(Tog(GroupKnights, HoldKeys.DefendPose, true, HoldApplies.Live, "protect.defendPose", false,
                "The knights draw weapons and swing at monsters in reach (cosmetic only). Not set: goals.json decides.",
                g => F(g.Protect?.DefendPose != null)));

            r.Add(Num(GroupKnights, HoldKeys.DefendSwingSeconds, 2, 1, 10, HoldApplies.Live, "protect.defendPose.swingSeconds", false,
                "Shortest time between two cosmetic swings.", g => F(g.Protect?.DefendPose?.SwingSeconds ?? 0)));

            r.Add(Num(GroupKnights, HoldKeys.DefendSwingMaxSeconds, 3, 1, 10, HoldApplies.Live, "protect.defendPose.swingMaxSeconds", false,
                "Longest time between two cosmetic swings (never below defend_swing_seconds).", g => F(g.Protect?.DefendPose?.SwingMaxSeconds ?? 0)));

            r.Add(Tog(GroupKnights, HoldKeys.HealthMilestones, true, HoldApplies.Live, "protect.healthMilestones", false,
                "A world line as each knight first drops to 75%, 50% and 25%.", g => F(g.Protect?.HealthMilestones ?? false)));
        }

        private static string knightHealthRaw;
        private static Dictionary<uint, int> knightHealthParsed;

        /// <summary>
        /// world_events_hold_knight_max_health_by_wcid as wcid to max health ("2000005=12000,2000007=8000"), each value
        /// clamped to the knight health range; null when not set. A bad entry is skipped (and reported by <see cref="CheckKnightHealthByWcid"/>).
        /// </summary>
        public static IReadOnlyDictionary<uint, int> KnightHealthByWcid()
        {
            var raw = Text(HoldKeys.KnightMaxHealthByWcid, "");

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (ReferenceEquals(raw, knightHealthRaw) && knightHealthParsed != null)
                return knightHealthParsed;

            var parsed = ParseKnightHealthByWcid(raw, out _);
            knightHealthParsed = parsed;
            knightHealthRaw = raw;

            return parsed;
        }

        /// <summary>Parses "wcid=health,..." (D6 - pure). Values are clamped; <paramref name="problems"/> names each bad or clamped entry.</summary>
        public static Dictionary<uint, int> ParseKnightHealthByWcid(string raw, out List<string> problems)
        {
            problems = new List<string>();
            var result = new Dictionary<uint, int>();

            if (string.IsNullOrWhiteSpace(raw))
                return result;

            foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');

                if (eq <= 0 || !uint.TryParse(part.Substring(0, eq).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wcid) ||
                    !double.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var hp) || !double.IsFinite(hp))
                {
                    problems.Add($"'{part}' is not wcid=health; skipped");
                    continue;
                }

                var clamped = (int)Math.Clamp(Math.Round(hp), KnightHealthMin, KnightHealthMax);

                if (clamped != hp)
                    problems.Add($"{wcid}={Format(hp)} is out of range {KnightHealthMin}..{KnightHealthMax}; using {clamped}");

                result[wcid] = clamped;
            }

            return result;
        }

        /// <summary>The warning for a knight_max_health_by_wcid value, or null.</summary>
        internal static string CheckKnightHealthByWcid(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            ParseKnightHealthByWcid(raw, out var problems);

            return problems.Count == 0 ? null : $"{HoldKeys.KnightMaxHealthByWcid}: {string.Join("; ", problems)}";
        }
    }
}
