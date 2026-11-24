using System.Collections.Generic;

namespace ACE.Server.Avatars;

/// <summary>One allegiance member as the Keeper resolver sees it.</summary>
public sealed record KeeperCandidate(uint Guid, uint AccountId, uint AllegianceRank, int OfficerLevel, int Depth);

/// <summary>The resolved Keeper. IsActing is true when someone other than the monarch holds the seat.</summary>
public sealed record KeeperResult(uint KeeperGuid, bool IsActing, bool MonarchIdle);

/// <summary>
/// Pure Keeper rules. The monarch keeps the seat unless their ACCOUNT has been unseen for keeper_idle_days;
/// then an active heir, the highest active officer, or the highest-ranked active member acts. Contribution
/// is never an input.
/// </summary>
public static class AvatarKeeperResolver
{
    public static KeeperResult Resolve(
        uint monarchGuid,
        uint monarchAccountId,
        bool monarchDeleted,
        uint? heirGuid,
        IReadOnlyList<KeeperCandidate> members,
        AccountActivityIndex activity,
        double nowUnix,
        AvatarTuning t)
    {
        // A deleted monarch counts as idle whatever their account did.
        if (!monarchDeleted && activity.IsActive(monarchAccountId, nowUnix, (int)t.KeeperIdleDays))
            return new KeeperResult(monarchGuid, false, false);

        var window = (int)t.ActiveWindowDays;
        KeeperCandidate heir = null;
        KeeperCandidate bestOfficer = null;
        KeeperCandidate bestMember = null;

        foreach (var m in members)
        {
            if (m.Guid == monarchGuid || m.AccountId == monarchAccountId)
                continue;
            if (!activity.IsActive(m.AccountId, nowUnix, window))
                continue;

            if (heirGuid.HasValue && m.Guid == heirGuid.Value)
                heir = m;
            if (m.OfficerLevel >= 1 && (bestOfficer == null || OfficerBeats(m, bestOfficer)))
                bestOfficer = m;
            if (bestMember == null || MemberBeats(m, bestMember))
                bestMember = m;
        }

        var keeper = heir ?? bestOfficer ?? bestMember;
        if (keeper == null)
            return new KeeperResult(monarchGuid, false, true);
        return new KeeperResult(keeper.Guid, true, true);
    }

    private static bool OfficerBeats(KeeperCandidate a, KeeperCandidate b)
    {
        if (a.OfficerLevel != b.OfficerLevel) return a.OfficerLevel > b.OfficerLevel;
        if (a.Depth != b.Depth) return a.Depth < b.Depth;
        return a.Guid < b.Guid;
    }

    private static bool MemberBeats(KeeperCandidate a, KeeperCandidate b)
    {
        if (a.AllegianceRank != b.AllegianceRank) return a.AllegianceRank > b.AllegianceRank;
        // Unreachable by construction: if any active officer exists, step (b) already chose among officers, so every
        // candidate reaching this comparison has OfficerLevel 0. Kept because the brief mandates the tiebreak.
        if (a.OfficerLevel != b.OfficerLevel) return a.OfficerLevel > b.OfficerLevel;
        if (a.Depth != b.Depth) return a.Depth < b.Depth;
        return a.Guid < b.Guid;
    }
}
