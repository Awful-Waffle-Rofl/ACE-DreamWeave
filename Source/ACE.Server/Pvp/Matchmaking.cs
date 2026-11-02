using System;
using System.Collections.Generic;
using System.Linq;

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
        IReadOnlyList<string> IpKeys = null,
        IReadOnlyList<uint> AccountKeys = null,
        IReadOnlyList<uint> RecentOpponents = null,
        bool RefuseUnpaidRematch = false,
        IReadOnlyList<bool> RefuseByMember = null)
    {
        /// <summary>
        /// Whether member <paramref name="index"/> refuses an unpaid rematch: the per-member RefuseByMember entry when supplied,
        /// otherwise the unit-wide RefuseUnpaidRematch (a caller that knows nothing per member).
        /// </summary>
        public bool RefusesAt(int index) => RefuseByMember != null ? index < RefuseByMember.Count && RefuseByMember[index] : RefuseUnpaidRematch;

        /// <summary>
        /// The same-IP key of member <paramref name="index"/>: the per-member IpKeys entry when supplied,
        /// otherwise the unit-wide IpKey (every pre-battleground caller).
        /// </summary>
        public string IpKeyOf(int index) => IpKeys != null && index < IpKeys.Count ? IpKeys[index] : IpKey;
    }

    /// <summary>
    /// Everything a matchmaker needs that isn't in the waiting list itself - the clock and the live tunables.
    /// <c>PickIndex</c> (optional, for tests) draws an index in [0, n) for random team assignment; null uses ThreadSafeRandom.
    /// </summary>
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
        Battlegrounds.BattlegroundDials Bg = null,
        Func<QueueEntrant, QueueEntrant, (bool Recent, IReadOnlyList<bool> UnpaidA, IReadOnlyList<bool> UnpaidB)> Probe = null,
        Action<Guid, IReadOnlyList<uint>> OnRefusedUnpaid = null,
        Func<int, int> PickIndex = null);

    /// <summary>
    /// The fresh-opponent tier rule shared by <see cref="PairMatchmaker"/> and <see cref="DuoMatchmaker"/>: a valid candidate is
    /// rated Fresh (never played recently, would pay), Recent (played recently, would pay) or Unpaid (a side would be flagged an
    /// unpaid rematch). Tiers only REORDER valid candidates; they never remove one, except that an Unpaid pairing is skipped when
    /// the unpaid side refuses unpaid rematches. With no <see cref="MatchmakingContext.Probe"/> every candidate is Fresh, which is
    /// exactly the legacy first-valid-candidate behavior.
    /// </summary>
    internal static class PairingTiers
    {
        public const int Fresh = 1;
        public const int Recent = 2;
        public const int Unpaid = 3;

        /// <summary>The best possible tier: once a candidate reaches it, no later candidate can beat it.</summary>
        public const int Best = Fresh;

        /// <summary>
        /// The tier of putting <paramref name="sideA"/> against <paramref name="sideB"/> (every cross pair is probed), or 0 when the
        /// pairing is blocked because an unpaid side refuses unpaid rematches (<see cref="MatchmakingContext.OnRefusedUnpaid"/> then
        /// fires for each refusing entrant).
        /// </summary>
        public static int Rate(MatchmakingContext ctx, IReadOnlyList<QueueEntrant> sideA, IReadOnlyList<QueueEntrant> sideB)
        {
            if (ctx.Probe == null)
                return Fresh;

            var recent = false;
            var unpaidA = sideA.Select(e => new bool[e.CharacterIds.Count]).ToArray();
            var unpaidB = sideB.Select(e => new bool[e.CharacterIds.Count]).ToArray();

            for (var i = 0; i < sideA.Count; i++)
            {
                for (var j = 0; j < sideB.Count; j++)
                {
                    var probe = ctx.Probe(sideA[i], sideB[j]);

                    recent |= probe.Recent;
                    Merge(unpaidA[i], probe.UnpaidA);
                    Merge(unpaidB[j], probe.UnpaidB);
                }
            }

            // Refusal is per MEMBER: a pairing is blocked only when a member who refuses is itself unpaid against the opponent.
            var blocked = Report(ctx, sideA, unpaidA) | Report(ctx, sideB, unpaidB);

            if (blocked)
                return 0;

            if (unpaidA.Any(u => u.Any(x => x)) || unpaidB.Any(u => u.Any(x => x)))
                return Unpaid;

            return recent ? Recent : Fresh;
        }

        private static void Merge(bool[] into, IReadOnlyList<bool> from)
        {
            if (from == null)
                return;

            for (var m = 0; m < into.Length && m < from.Count; m++)
                into[m] |= from[m];
        }

        /// <summary>Reports every entrant with a refusing, unpaid member (with those members' ids); true when any exists.</summary>
        private static bool Report(MatchmakingContext ctx, IReadOnlyList<QueueEntrant> side, bool[][] unpaid)
        {
            var any = false;

            for (var i = 0; i < side.Count; i++)
            {
                var refusing = new List<uint>();

                for (var m = 0; m < side[i].CharacterIds.Count; m++)
                    if (unpaid[i][m] && side[i].RefusesAt(m))
                        refusing.Add(side[i].CharacterIds[m]);

                if (refusing.Count == 0)
                    continue;

                ctx.OnRefusedUnpaid?.Invoke(side[i].EntrantId, refusing);
                any = true;
            }

            return any;
        }
    }
    /// <summary>A formed-but-not-yet-accepted match: the teams a matchmaker proposed, and whether it will be rated.</summary>
    public sealed record MatchProposal(IReadOnlyList<PvpTeam> Teams, bool Rated);
}
