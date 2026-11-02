using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One seat's contribution C (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"). <see cref="Contribution"/> is null for a seat that was never
    /// dispatched, or when every dimension's reference average is 0 (nothing to compare against). <see cref="Dimension"/> names the dimension
    /// that produced C (the highest ratio), null with it.
    /// </summary>
    public readonly record struct SeatContribution(uint CharacterId, int TeamIndex, double? Contribution, string Dimension);

    /// <summary>
    /// The contribution share of each seat of a battleground or Tugak Brawl match (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): C is the
    /// HIGHEST ratio, across dimensions, of the seat's stat to that stat's average over the reference seats. One dimension the seat carries
    /// is enough, so a healer who deals no damage is measured by healing, a hill-sitter by hill seconds and a brawler by damage.
    ///
    /// <para/>
    /// Dimensions. A battleground: the role stats the performance modifier weighs (<see cref="BattlegroundPerformanceModifier.StatsFor"/>,
    /// reused, never copied; a stat whose weight is 0 is left out there and so here), plus HealingDone and EngagedSeconds. Tugak Brawl:
    /// player damage, killshots and EngagedSeconds. Any other mode: none (every C is null).
    ///
    /// <para/>
    /// Reference. The average over ELIGIBLE seats (dispatched and not a forfeiter, the same rule as the performance modifier): the seat's own
    /// team in a battleground, the whole field in Tugak Brawl. A dimension whose reference average is 0 is skipped. Every dispatched seat gets
    /// a C (a forfeiter too, measured against the eligible average); an undispatched one gets null.
    ///
    /// <para/>
    /// Pure: no clock, no settings read, no state. The contribution floor (<see cref="PvpConductFloor"/>) is its consumer.
    /// </summary>
    public static class PvpContribution
    {
        public const string DimensionHealing = "healing";
        public const string DimensionEngaged = "engaged_s";
        public const string DimensionDamage = "player_dmg";
        public const string DimensionKills = "kills";

        private readonly record struct Dim(string Name, Func<SeatPerformanceSnapshot, double> Value);

        /// <param name="dials">The performance dials whose weights pick the battleground role stats; null uses the shipped defaults.</param>
        public static IReadOnlyDictionary<uint, SeatContribution> Compute(string modeKey, IEnumerable<PerformanceSeat> seats, PerformanceDials dials = null)
        {
            var all = (seats ?? Enumerable.Empty<PerformanceSeat>()).ToList();
            var result = new Dictionary<uint, SeatContribution>();

            foreach (var s in all)
                result[s.CharacterId] = new SeatContribution(s.CharacterId, s.TeamIndex, null, null);

            var isBattleground = modeKey == BattlegroundModes.AttackDefendModeKey || modeKey == BattlegroundModes.KothModeKey;
            var isTugak = modeKey == ArenaMapCatalog.FfaKey;

            if (!isBattleground && !isTugak)
                return result;

            var d = dials ?? PerformanceDials.From(BattlegroundTunables.Defaults);

            // Battleground: one reference group per team. Tugak: the whole field is one group.
            var groups = isBattleground ? all.GroupBy(s => s.TeamIndex).Select(g => g.ToList()).ToList() : new List<List<PerformanceSeat>> { all };

            foreach (var group in groups)
            {
                var eligible = group.Where(IsEligible).ToList();

                if (eligible.Count == 0)
                    continue;

                var dims = isBattleground ? BattlegroundDimensions(modeKey, group[0].TeamIndex, d) : TugakDimensions();
                var averages = dims.Select(dim => eligible.Average(s => dim.Value(s.Stats))).ToArray();

                foreach (var s in group.Where(s => s.Dispatched))
                {
                    double? best = null;
                    string bestName = null;

                    for (var i = 0; i < dims.Count; i++)
                    {
                        if (!(averages[i] > 0.0) || double.IsInfinity(averages[i]))
                            continue;

                        var ratio = dims[i].Value(s.Stats) / averages[i];

                        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
                            continue;

                        if (best == null || ratio > best.Value)
                        {
                            best = ratio;
                            bestName = dims[i].Name;
                        }
                    }

                    result[s.CharacterId] = new SeatContribution(s.CharacterId, s.TeamIndex, best, bestName);
                }
            }

            return result;
        }

        /// <summary>Eligible for the reference average: dispatched and not a forfeiter (the performance modifier's rule).</summary>
        public static bool IsEligible(PerformanceSeat s) => s.Dispatched && !s.Forfeited;

        private static List<Dim> BattlegroundDimensions(string modeKey, int teamIndex, PerformanceDials d)
        {
            var list = new List<Dim>();
            var role = BattlegroundPerformanceModifier.StatsFor(modeKey, teamIndex, d);

            foreach (var stat in role)
                list.Add(new Dim(stat.Name, stat.Value));

            list.Add(new Dim(DimensionHealing, s => s.HealingDone));
            list.Add(new Dim(DimensionEngaged, s => s.EngagedSeconds));

            return list;
        }

        private static List<Dim> TugakDimensions() => new List<Dim>
        {
            new Dim(DimensionDamage, s => s.PlayerDamage),
            new Dim(DimensionKills, s => s.Killshots),
            new Dim(DimensionEngaged, s => s.EngagedSeconds),
        };
    }
}
