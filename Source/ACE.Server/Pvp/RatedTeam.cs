using System.Collections.Generic;

namespace ACE.Server.Pvp
{
    /// <summary>
    /// One player's rating inputs for <see cref="IRatingModel.Deltas"/>. K is resolved by the caller
    /// (<see cref="Rating.EloRating.SelectK"/>) from that player's own game count, per design's "using
    /// their own K" rule for 2v2. ForceZeroScore is the 2v2 forfeiter override: "a forfeiter always takes
    /// S = 0 against the opposing team's average", even when their own team goes on to win.
    /// </summary>
    public sealed record RatedPlayer(uint CharacterId, int Rating, int K, string IpKey = null, bool ForceZeroScore = false);

    /// <summary>One team's rating inputs, indexed by the same TeamIndex as <see cref="PvpTeam"/>.</summary>
    public sealed record RatedTeam(int TeamIndex, IReadOnlyList<RatedPlayer> Players);
}
