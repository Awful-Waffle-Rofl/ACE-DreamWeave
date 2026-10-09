using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// 1v1 matchmaker: a rating window around the oldest waiting solo player, widening with wait time
    /// (Docs/Pvp/DESIGN.md "Modes" table). window = min(initial + growthPerMinute * minutesWaited, max).
    /// </summary>
    public sealed class PairMatchmaker : IMatchmaker
    {
        public MatchProposal TryForm(IReadOnlyList<QueueEntrant> waiting, MatchmakingContext ctx)
        {
            var solo = waiting.Where(w => w.CharacterIds.Count == 1).OrderBy(w => w.QueuedAtUtc).ToList();

            for (var i = 0; i < solo.Count; i++)
            {
                var a = solo[i];
                var waitedMinutes = (ctx.UtcNow - a.QueuedAtUtc).TotalMinutes;
                var window = Math.Min(ctx.MmWindowInitial + ctx.MmWindowGrowthPerMinute * waitedMinutes, ctx.MmWindowMax);

                for (var j = 0; j < solo.Count; j++)
                {
                    if (i == j)
                        continue;

                    var b = solo[j];

                    if (ctx.BlockSameIp && a.IpKey != null && a.IpKey == b.IpKey)
                        continue;

                    if (Math.Abs(a.Ratings[0] - b.Ratings[0]) > window)
                        continue;

                    var teamA = new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(a.CharacterIds[0], a.Ratings[0], a.IpKey) });
                    var teamB = new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(b.CharacterIds[0], b.Ratings[0], b.IpKey) });

                    return new MatchProposal(new List<PvpTeam> { teamA, teamB }, Rated: true);
                }
            }

            return null;
        }
    }
}
