using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// FFA matchmaker (Docs/Pvp/DESIGN.md "LobbyMatchmaker (FFA)"): needed size =
    /// max(min, target - floor(oldestWait / decay)), taking at most FfaMaxPlayers. No rating window;
    /// same-IP players may share a lobby (excluded only from each other's rating comparison, in
    /// <see cref="Rating.PairwisePlacementEloModel"/>).
    /// </summary>
    public sealed class LobbyMatchmaker : IMatchmaker
    {
        public MatchProposal TryForm(IReadOnlyList<QueueEntrant> waiting, MatchmakingContext ctx)
        {
            var solos = waiting.Where(w => w.CharacterIds.Count == 1).OrderBy(w => w.QueuedAtUtc).ToList();

            if (solos.Count == 0)
                return null;

            // The ONE shared formula (Docs/Pvp/DESIGN.md "Arena Crier"): also used by AnnounceFfaLobby's progress
            // line and the Arena Crier, so none of the three can ever quote a different FFA lobby size.
            var needed = ArenaMapCatalog.FfaDecayedTargetSize(ctx.UtcNow - solos[0].QueuedAtUtc, ctx.FfaMinPlayers, ctx.FfaTargetPlayers, ctx.FfaMaxPlayers, ctx.FfaMinDecaySeconds);

            if (solos.Count < needed)
                return null;

            var take = solos.Take(Math.Min(solos.Count, ctx.FfaMaxPlayers)).ToList();

            var teams = take
                .Select((entrant, index) => new PvpTeam(index, new List<PvpParticipant> { new PvpParticipant(entrant.CharacterIds[0], entrant.Ratings[0], entrant.IpKey) }))
                .ToList<PvpTeam>();

            return new MatchProposal(teams, Rated: true);
        }
    }
}
