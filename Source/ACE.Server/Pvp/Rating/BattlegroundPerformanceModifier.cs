using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Pvp.Rating
{
    /// <summary>
    /// The pvp_bg_perf_* settings the performance modifier reads, sanitized (Docs/Pvp/BATTLEGROUNDS.md "Rating and performance").
    /// Gain: NaN, infinite or negative = 0 (no modifier). Cap: a percent, NaN, infinite or negative = 0, above 100 = 100, held here as a
    /// fraction. Each weight: negative = 0. Near-hill kill weight: NaN, infinite or negative = 1 (a near-hill kill counts like any other), and
    /// at most <see cref="MaxKothNearKillWeight"/>, so a huge value cannot overflow the weighted kill count to infinity.
    /// </summary>
    public sealed record PerformanceDials(
        bool Enabled,
        double Gain,
        double Cap,
        double AdAtkCrystalDmg,
        double AdAtkPlayerDmg,
        double AdAtkKills,
        double AdDefDmgNear,
        double AdDefKillsNear,
        double AdDefDmgFar,
        double AdDefKillsFar,
        double KothHillSeconds,
        double KothDmg,
        double KothKills,
        double KothNearKillWeight)
    {
        public static PerformanceDials From(BattlegroundDials d)
        {
            if (d == null)
                return null;

            static double W(long w) => Math.Max(0L, w);

            return new PerformanceDials(
                Enabled: d.PerfEnabled,
                Gain: Finite(d.PerfGain) && d.PerfGain > 0.0 ? d.PerfGain : 0.0,
                Cap: Finite(d.PerfCapPct) ? Math.Clamp(d.PerfCapPct, 0.0, 100.0) / 100.0 : 0.0,
                AdAtkCrystalDmg: W(d.PerfWeightAdAtkCrystalDmg),
                AdAtkPlayerDmg: W(d.PerfWeightAdAtkPlayerDmg),
                AdAtkKills: W(d.PerfWeightAdAtkKills),
                AdDefDmgNear: W(d.PerfWeightAdDefDmgNear),
                AdDefKillsNear: W(d.PerfWeightAdDefKillsNear),
                AdDefDmgFar: W(d.PerfWeightAdDefDmgFar),
                AdDefKillsFar: W(d.PerfWeightAdDefKillsFar),
                KothHillSeconds: W(d.PerfWeightKothHillSeconds),
                KothDmg: W(d.PerfWeightKothDmg),
                KothKills: W(d.PerfWeightKothKills),
                KothNearKillWeight: Finite(d.PerfKothNearKillWeight) && d.PerfKothNearKillWeight >= 0.0 ? Math.Min(d.PerfKothNearKillWeight, MaxKothNearKillWeight) : 1.0);
        }

        /// <summary>The largest pvp_bg_perf_koth_near_kill_weight honoured; a larger setting counts as this.</summary>
        public const double MaxKothNearKillWeight = 1000.0;

        internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    /// <summary>One seat's inputs to the performance modifier: its stats and the facts the eligibility rules need.</summary>
    public readonly record struct PerformanceSeat(uint CharacterId, int TeamIndex, SeatPerformanceSnapshot Stats, string IpKey, bool Forfeited, bool Dispatched);

    /// <summary>
    /// One seat's result. <see cref="Score"/> is P_i, or null for a seat that was not scored (a forfeiter, an undispatched seat, or every seat of
    /// a team that is not compared). <see cref="Modifier"/> is m_i after the clamp and BEFORE the team's zero-sum pass, which needs the rating
    /// base and so runs in <see cref="PerformanceTeamEloModel"/>.
    /// </summary>
    public readonly record struct SeatPerformanceModifier(uint CharacterId, int TeamIndex, double? Score, double Modifier);

    /// <summary>
    /// The battleground performance modifier (Docs/Pvp/BATTLEGROUNDS.md "Rating and performance"; owner rulings 2026-10-08). Win or loss
    /// drives the rating change (the team-Elo base B_i); individual performance scales it by at most +/- the cap:
    ///
    /// <para/>
    /// For each stat of the seat's role, ratio = the seat's value / the average of that stat over its OWN team's eligible seats (1 when that
    /// average is 0). P_i = the weighted average of those ratios, weights normalised. m_i = clamp(gain x (P_i - 1), -cap, +cap).
    ///
    /// <para/>
    /// Eligible = dispatched and not a forfeiter. A forfeiter gets m = -cap and is left out of the averages (and of the zero-sum pass). An
    /// undispatched seat gets 0. A team with one or fewer eligible seats, or with two seats on one IP (anti-alt-farming), gets 0 for every
    /// non-forfeiter. Pure: no clock, no settings read, no state.
    /// </summary>
    public static class BattlegroundPerformanceModifier
    {
        /// <summary>m_i (clamped, before the zero-sum pass) and P_i for every seat. Every seat is 0 when <paramref name="dials"/> is off or null.</summary>
        public static IReadOnlyDictionary<uint, SeatPerformanceModifier> Modifiers(string modeKey, IEnumerable<PerformanceSeat> seats, PerformanceDials dials)
        {
            var all = (seats ?? Enumerable.Empty<PerformanceSeat>()).ToList();
            var result = new Dictionary<uint, SeatPerformanceModifier>();

            foreach (var s in all)
                result[s.CharacterId] = new SeatPerformanceModifier(s.CharacterId, s.TeamIndex, null, 0.0);

            if (dials == null || !dials.Enabled)
                return result;

            foreach (var team in all.GroupBy(s => s.TeamIndex))
            {
                var members = team.ToList();

                // A forfeiter always takes -cap, whatever the team rules below say: it is a penalty, never compared and never redistributed.
                foreach (var s in members.Where(s => s.Dispatched && s.Forfeited))
                    result[s.CharacterId] = new SeatPerformanceModifier(s.CharacterId, s.TeamIndex, null, -dials.Cap);

                var eligible = members.Where(s => s.Dispatched && !s.Forfeited).ToList();

                if (eligible.Count <= 1 || HasSharedIp(members))
                    continue;

                var stats = StatsFor(modeKey, team.Key, dials);

                if (stats.Count == 0)
                    continue;

                var averages = stats.Select(st => eligible.Average(s => st.Value(s.Stats))).ToArray();

                foreach (var s in eligible)
                {
                    double weighted = 0.0, weights = 0.0;

                    for (var i = 0; i < stats.Count; i++)
                    {
                        var ratio = averages[i] > 0.0 ? stats[i].Value(s.Stats) / averages[i] : 1.0;
                        weighted += stats[i].Weight * ratio;
                        weights += stats[i].Weight;
                    }

                    var p = weights > 0.0 ? weighted / weights : 1.0;
                    var m = Math.Clamp(dials.Gain * (p - 1.0), -dials.Cap, dials.Cap);

                    // A NaN passes Math.Clamp unchanged and would poison the team's zero-sum pass: anything non-finite is neutral (m = 0, unscored).
                    if (!PerformanceDials.Finite(p) || !PerformanceDials.Finite(m))
                    {
                        result[s.CharacterId] = new SeatPerformanceModifier(s.CharacterId, s.TeamIndex, null, 0.0);
                        continue;
                    }

                    result[s.CharacterId] = new SeatPerformanceModifier(s.CharacterId, s.TeamIndex, p, m);
                }
            }

            return result;
        }

        /// <summary>True when two seats of one team share a (non-null) IP key.</summary>
        private static bool HasSharedIp(IReadOnlyList<PerformanceSeat> members)
        {
            var seen = new HashSet<string>();

            foreach (var s in members)
                if (s.IpKey != null && !seen.Add(s.IpKey))
                    return true;

            return false;
        }

        internal readonly record struct Stat(string Name, double Weight, Func<SeatPerformanceSnapshot, double> Value);

        /// <summary>
        /// The weighted stats of a team's role: Attack/Defend attackers (team 0) crystal damage, player damage and killshots; defenders (team 1)
        /// damage and kills near the vulnerable crystal and elsewhere; King of the Hill hill seconds, damage and killshots with a near-hill kill
        /// counting the near-kill weight. A stat whose weight is 0 is left out. Empty for any other mode. Internal so the conduct contribution
        /// (<see cref="PvpContribution"/>) reads the SAME role stats instead of a copy of this list.
        /// </summary>
        internal static List<Stat> StatsFor(string modeKey, int teamIndex, PerformanceDials d)
        {
            var list = new List<Stat>();

            void Add(string name, double weight, Func<SeatPerformanceSnapshot, double> value)
            {
                if (weight > 0.0)
                    list.Add(new Stat(name, weight, value));
            }

            if (modeKey == BattlegroundModes.AttackDefendModeKey)
            {
                if (teamIndex == CrystalWinCondition.AttackerTeam)
                {
                    Add("crystal_dmg", d.AdAtkCrystalDmg, s => s.CrystalDamage);
                    Add("player_dmg", d.AdAtkPlayerDmg, s => s.PlayerDamage);
                    Add("kills", d.AdAtkKills, s => s.Killshots);
                }
                else if (teamIndex == CrystalWinCondition.DefenderTeam)
                {
                    Add("def_dmg_near", d.AdDefDmgNear, s => s.DefenderDamageNear);
                    Add("kills_near_crystal", d.AdDefKillsNear, s => s.KillshotsNearCrystal);
                    Add("def_dmg_far", d.AdDefDmgFar, s => s.DefenderDamageFar);
                    Add("kills_far_crystal", d.AdDefKillsFar, s => s.KillshotsFarCrystal);
                }
            }
            else if (modeKey == BattlegroundModes.KothModeKey)
            {
                var nearWeight = d.KothNearKillWeight;

                Add("hill_s", d.KothHillSeconds, s => s.HillSeconds);
                Add("player_dmg", d.KothDmg, s => s.PlayerDamage);
                Add("kills", d.KothKills, s => (s.Killshots - s.KillshotsNearHill) + nearWeight * s.KillshotsNearHill);
            }

            return list;
        }

        /// <summary>
        /// The team zero-sum pass: performance moves rating within a team and never creates it. Over the given (weight = |B_i|, m_i) pairs,
        /// when sum(weight x m) is positive the positive m's are scaled down, when negative the negative m's, until the sum is 0. Scaling only
        /// shrinks, so the cap still holds. Returns the balanced m's in input order. Pure.
        /// </summary>
        public static double[] BalanceZeroSum(IReadOnlyList<(double Weight, double Modifier)> seats)
        {
            var result = seats.Select(s => s.Modifier).ToArray();
            var sum = seats.Sum(s => s.Weight * s.Modifier);

            if (sum == 0.0)
                return result;

            var positive = sum > 0.0;
            var side = seats.Where(s => positive ? s.Modifier > 0.0 : s.Modifier < 0.0).Sum(s => s.Weight * s.Modifier);

            if (side == 0.0)
                return result;

            // side has the same sign as sum and |side| >= |sum|, so the factor is in [0, 1).
            var factor = (side - sum) / side;

            for (var i = 0; i < result.Length; i++)
                if (positive ? result[i] > 0.0 : result[i] < 0.0)
                    result[i] *= factor;

            return result;
        }

        /// <summary>
        /// The signed whole percent of an applied m_i for the battleground rating line ("+18", "-25"), halves away from zero; null when it
        /// rounds to 0, so the line shows nothing.
        /// </summary>
        public static string ChatPercent(double modifier)
        {
            var pct = (int)Math.Round(modifier * 100.0, MidpointRounding.AwayFromZero);

            return pct == 0 ? null : pct.ToString("+0;-0");
        }
    }
}
