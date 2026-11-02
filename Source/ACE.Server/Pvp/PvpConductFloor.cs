using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One seat's contribution-floor decision (Docs/Pvp/BATTLEGROUNDS.md "Contribution floor"). <see cref="Reason"/> is "relative",
    /// "absolute" or "both" when <see cref="BelowFloor"/>, else null.
    /// </summary>
    public readonly record struct FloorDecision(uint CharacterId, bool BelowFloor, double? Contribution, string Dimension, long EngagedSeconds, string Reason);

    /// <summary>
    /// The contribution floor on payouts: a seat that did too little is denied the match pay. Pure: no clock, no settings read.
    ///
    /// <para/>
    /// A seat is below the floor when EITHER test fails. Relative: its contribution C (<see cref="PvpContribution"/>) is under
    /// pvp_conduct_floor_share; a seat with no C (no usable dimension) passes this test, since there is nothing to compare against.
    /// Absolute: EngagedSeconds is under the threshold AND it has no contribution of any kind: no player damage, crystal damage, healing,
    /// killshots or hill seconds (a crystal attacker or a hill-sitter is working even with few engaged seconds). The absolute test exists
    /// because the relative one is meaningless when the whole team is idle (every average is 0) or only one seat is eligible (its ratio to
    /// itself is 1). The threshold is min(pvp_conduct_floor_min_engaged_seconds, liveSeconds / 2): a match the opponents forfeit early
    /// leaves the winners little time to be engaged, so the bar shrinks with the match.
    ///
    /// <para/>
    /// Only dispatched, non-forfeiting seats are judged (a forfeiter already gets 0, an undispatched seat has no stats). Nothing is
    /// redistributed and no rating is touched. Off (an empty result) when the floor is disabled or the dials are null.
    /// </summary>
    public static class PvpConductFloor
    {
        public static IReadOnlyDictionary<uint, FloorDecision> Evaluate(string modeKey, IEnumerable<PerformanceSeat> seats, PvpConductDials conduct, PerformanceDials perfDials = null, double liveSeconds = double.PositiveInfinity)
        {
            var result = new Dictionary<uint, FloorDecision>();

            if (conduct == null || !conduct.FloorEnabled)
                return result;

            var all = (seats ?? Enumerable.Empty<PerformanceSeat>()).ToList();
            var contributions = PvpContribution.Compute(modeKey, all, perfDials);
            var share = double.IsNaN(conduct.FloorShare) ? 0.0 : Math.Min(1.0, Math.Max(0.0, conduct.FloorShare));
            // A NaN or negative Live duration leaves no time to be engaged: the threshold is 0, so the absolute test cannot fire.
            var minEngaged = Math.Max(0.0, Math.Min((double)Math.Max(0L, conduct.FloorMinEngagedSeconds), double.IsNaN(liveSeconds) ? 0.0 : liveSeconds / 2.0));

            foreach (var s in all)
            {
                if (!s.Dispatched || s.Forfeited)
                    continue;

                double? c = contributions.TryGetValue(s.CharacterId, out var sc) ? sc.Contribution : null;
                var dimension = contributions.TryGetValue(s.CharacterId, out var sd) ? sd.Dimension : null;

                var relative = c.HasValue && c.Value < share;
                var st = s.Stats;
                var absolute = st.EngagedSeconds < minEngaged && st.PlayerDamage == 0 && st.CrystalDamage == 0 && st.HealingDone == 0 && st.Killshots == 0 && st.HillSeconds == 0;
                var reason = relative && absolute ? "both" : relative ? "relative" : absolute ? "absolute" : null;

                result[s.CharacterId] = new FloorDecision(s.CharacterId, reason != null, c, dimension, s.Stats.EngagedSeconds, reason);
            }

            return result;
        }

        /// <summary>The log line for one decision, next to the perf lines.</summary>
        public static string FormatLogLine(Guid matchId, string name, FloorDecision d, bool nothingOwed = false) =>
            $"[PVP] match {matchId} conduct_floor: {name} (0x{d.CharacterId:X8}) C={(d.Contribution.HasValue ? d.Contribution.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "-")} via {d.Dimension ?? "-"}, engaged_s={d.EngagedSeconds}, {(d.BelowFloor ? (nothingOwed ? "below floor (" : "WITHHELD (") + d.Reason + ")" : "ok")}";
    }
}
