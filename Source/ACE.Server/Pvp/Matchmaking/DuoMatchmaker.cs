using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// 2v2 matchmaker: "duo vs duo first, then duo vs two solos after duo_vs_solo_after_seconds"
    /// (Docs/Pvp/DESIGN.md "Modes" table). No rating window - 2v2 pairs whichever duo/solo combination is
    /// available, since a rating-window search across duo averages was not specified.
    /// </summary>
    public sealed class DuoMatchmaker : IMatchmaker
    {
        public MatchProposal TryForm(IReadOnlyList<QueueEntrant> waiting, MatchmakingContext ctx)
        {
            var duos = waiting.Where(w => w.CharacterIds.Count == 2).OrderBy(w => w.QueuedAtUtc).ToList();
            var solos = waiting.Where(w => w.CharacterIds.Count == 1).OrderBy(w => w.QueuedAtUtc).ToList();

            for (var i = 0; i < duos.Count; i++)
            {
                for (var j = i + 1; j < duos.Count; j++)
                {
                    if (ctx.BlockSameIp && SharesIp(duos[i], duos[j]))
                        continue;

                    var teamA = new PvpTeam(0, DuoParticipants(duos[i]));
                    var teamB = new PvpTeam(1, DuoParticipants(duos[j]));

                    return new MatchProposal(new List<PvpTeam> { teamA, teamB }, Rated: true);
                }
            }

            foreach (var duo in duos)
            {
                var waitedSeconds = (ctx.UtcNow - duo.QueuedAtUtc).TotalSeconds;

                if (waitedSeconds < ctx.DuoVsSoloAfterSeconds)
                    continue;

                for (var i = 0; i < solos.Count; i++)
                {
                    for (var j = i + 1; j < solos.Count; j++)
                    {
                        var s1 = solos[i];
                        var s2 = solos[j];

                        if (ctx.BlockSameIp && (SharesIp(duo, s1) || SharesIp(duo, s2) || s1.IpKey != null && s1.IpKey == s2.IpKey))
                            continue;

                        var teamDuo = new PvpTeam(0, DuoParticipants(duo));
                        var teamSolos = new PvpTeam(1, new List<PvpParticipant>
                        {
                            new PvpParticipant(s1.CharacterIds[0], s1.Ratings[0], s1.IpKey),
                            new PvpParticipant(s2.CharacterIds[0], s2.Ratings[0], s2.IpKey)
                        });

                        return new MatchProposal(new List<PvpTeam> { teamDuo, teamSolos }, Rated: true);
                    }
                }
            }

            return null;
        }

        private static List<PvpParticipant> DuoParticipants(QueueEntrant duo)
        {
            return new List<PvpParticipant>
            {
                new PvpParticipant(duo.CharacterIds[0], duo.Ratings[0], duo.IpKey),
                new PvpParticipant(duo.CharacterIds[1], duo.Ratings[1], duo.IpKey)
            };
        }

        private static bool SharesIp(QueueEntrant a, QueueEntrant b)
        {
            return a.IpKey != null && a.IpKey == b.IpKey;
        }
    }
}
