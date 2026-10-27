using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ACE.Server.WorldEvents
{
    /// <summary>How Hold runs and their chain talk.</summary>
    public static partial class HoldSettings
    {
        public const string BroadcastWorld = "world", BroadcastLocal = "local";

        private static readonly string[] Broadcasts = { BroadcastWorld, BroadcastLocal };

        static partial void RegisterMessages(List<HoldSetting> r)
        {
            r.Add(Pick(GroupMessages, HoldKeys.Broadcast, BroadcastWorld, Broadcasts, HoldApplies.Live, null, true,
                "World Event lines during a Hold run or chain stage (waves, knights, bosses, progress, the story): world (as today) or local " +
                "(only players within the participant radius). The teaser, start, outcome and time-left lines stay world-wide."));
            r.Add(Tog(GroupMessages, HoldKeys.Story, true, HoldApplies.Live, null, true,
                "The chain's story beats (reveal, tie-back, closing). Off drops them; the thanks that points to the cache stays."));
            r.Add(TextSetting(GroupMessages, HoldKeys.MilestonePercents, "75,50,25", HoldApplies.Live, null, true,
                "The knights' health milestone lines, in percent (e.g. \"80,60,40,20\"; \"none\" = no lines). Named bosses keep 75, 50, 25."));
        }

        private static string milestoneRaw;
        private static IReadOnlyList<int> milestoneParsed;

        /// <summary>The knights' milestone tiers: the setting when set and valid, else 75/50/25 (the boss tiers), as before.</summary>
        public static IReadOnlyList<int> KnightMilestonePercents()
        {
            var raw = Text(HoldKeys.MilestonePercents, null);

            if (raw == null)
                return WorldEvent.BossHealthMilestonePercents;

            if (ReferenceEquals(raw, milestoneRaw) && milestoneParsed != null)
                return milestoneParsed;

            var parsed = ParseMilestones(raw, out _) ?? (IReadOnlyList<int>)WorldEvent.BossHealthMilestonePercents;
            milestoneRaw = raw;
            milestoneParsed = parsed;

            return parsed;
        }

        /// <summary>"80,60,40" (D6 - pure): whole percents 1 to 99, highest first; "none" = none. Null (with a reason) when unusable.</summary>
        public static List<int> ParseMilestones(string raw, out string problem)
        {
            problem = null;

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            if (raw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                return new List<int>();

            var result = new List<int>();

            foreach (var part in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part.Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct) || pct < 1 || pct > 99)
                {
                    problem = $"{HoldKeys.MilestonePercents}: '{part}' is not a percent from 1 to 99; the setting is ignored";
                    return null;
                }

                result.Add(pct);
            }

            return result.Distinct().OrderByDescending(p => p).ToList();
        }
    }
}
