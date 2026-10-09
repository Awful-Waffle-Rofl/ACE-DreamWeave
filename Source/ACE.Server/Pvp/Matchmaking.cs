using System;
using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One queued unit waiting for a match: a solo player (one CharacterId/Rating) or a premade duo (two,
    /// queued together per design's "A premade duo is simply a 2-person fellowship that queued together").
    /// </summary>
    public sealed record QueueEntrant(
        Guid EntrantId,
        IReadOnlyList<uint> CharacterIds,
        IReadOnlyList<int> Ratings,
        string IpKey,
        DateTime QueuedAtUtc,
        IReadOnlyList<uint> MonarchIds = null,
        IReadOnlyList<string> IpKeys = null)
    {
        /// <summary>
        /// The same-IP key of member <paramref name="index"/>: the per-member IpKeys entry when supplied,
        /// otherwise the unit-wide IpKey (every pre-battleground caller).
        /// </summary>
        public string IpKeyOf(int index) => IpKeys != null && index < IpKeys.Count ? IpKeys[index] : IpKey;
    }

    /// <summary>Everything a matchmaker needs that isn't in the waiting list itself - the clock and the live tunables.</summary>
    public sealed record MatchmakingContext(
        DateTime UtcNow,
        int MmWindowInitial,
        int MmWindowGrowthPerMinute,
        int MmWindowMax,
        int DuoVsSoloAfterSeconds,
        int FfaTargetPlayers,
        int FfaMinPlayers,
        int FfaMaxPlayers,
        int FfaMinDecaySeconds,
        bool BlockSameIp,
        Battlegrounds.BattlegroundDials Bg = null);

    /// <summary>A formed-but-not-yet-accepted match: the teams a matchmaker proposed, and whether it will be rated.</summary>
    public sealed record MatchProposal(IReadOnlyList<PvpTeam> Teams, bool Rated);
}
