using System;
using System.Collections.Generic;

using ACE.Entity;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>
/// One allegiance flattened to what the Avatar rules need. MonarchDeleted is true when the monarch's
/// character is deleted; the Keeper rules then treat the monarch as idle whatever their account did.
/// </summary>
public sealed record AvatarRoster(uint MonarchGuid, uint MonarchAccountId, bool MonarchDeleted, IReadOnlyList<KeeperCandidate> Members);

/// <summary>The single roster source for founding, roster scale and the Keeper.</summary>
public static class AvatarRosters
{
    /// <summary>
    /// Memory-only deleted check: an online Player's flag is read, an offline character counts as not
    /// deleted (OfflinePlayer.IsDeleted is a database query). Deleted characters leave the roster through
    /// the HandlePlayerDelete hook.
    /// </summary>
    public static readonly Func<IPlayer, bool> InMemoryIsDeleted = p => p is Player online && online.IsDeleted;

    /// <summary>
    /// Walks AllegianceManager.Allegiances. Must run on the world thread: that dictionary is an unlocked
    /// Dictionary, so any other thread can race its mutations.
    /// </summary>
    public static IReadOnlyList<AvatarRoster> FromAllegianceManager(Func<IPlayer, bool> isDeleted = null)
    {
        isDeleted ??= InMemoryIsDeleted;
        var rosters = new List<AvatarRoster>();

        foreach (var allegiance in AllegianceManager.Allegiances.Values)
        {
            if (allegiance.Monarch == null || allegiance.Members == null)
                continue;

            var roster = BuildRoster(allegiance.Monarch, allegiance.Members.Values, n => n.Player, isDeleted);
            if (roster != null)
                rosters.Add(roster);
        }

        return rosters;
    }

    /// <summary>
    /// The roster of the one allegiance led by <paramref name="monarchGuid"/>, from AllegianceManager's cached tree.
    /// Null when that guid leads no loaded allegiance (a solo character has none). Callers: on the world thread, the
    /// Tick, DoHandlePlayerDelete, HandleAllegianceOnLogin, the break, boot and officer handlers and the plain
    /// Confirmation_SwearAllegiance confirm; on the player's action queue, the unconfirmed swear (MoveTo chain) when it
    /// classifies. Confirmation_Choice Yes callbacks hand their allegiance work to the world thread first.
    /// </summary>
    public static AvatarRoster ForMonarch(uint monarchGuid, Func<IPlayer, bool> isDeleted = null)
    {
        if (!AllegianceManager.Players.TryGetValue(new ObjectGuid(monarchGuid), out var node))
            return null;

        var allegiance = node?.Allegiance;
        if (allegiance?.Monarch == null || allegiance.Members == null || allegiance.Monarch.PlayerGuid.Full != monarchGuid)
            return null;

        return BuildRoster(allegiance.Monarch, allegiance.Members.Values, n => n.Player, isDeleted ?? InMemoryIsDeleted);
    }

    /// <summary>
    /// Flattens one allegiance. Returns null when the monarch has no account. Deleted members (per the seam)
    /// are left out, and a deleted monarch sets MonarchDeleted.
    /// </summary>
    internal static AvatarRoster BuildRoster(AllegianceNode monarchNode, IEnumerable<AllegianceNode> nodes,
        Func<AllegianceNode, IPlayer> playerOf, Func<IPlayer, bool> isDeleted)
    {
        var monarch = playerOf(monarchNode);
        var monarchAccount = monarch?.Account;
        if (monarchAccount == null)
            return null;

        var members = new List<KeeperCandidate>();
        foreach (var node in nodes)
        {
            var player = playerOf(node);
            if (player?.Account == null || isDeleted(player))
                continue;

            members.Add(new KeeperCandidate(
                node.PlayerGuid.Full,
                player.Account.AccountId,
                node.Rank,
                player.AllegianceOfficerRank ?? 0,
                DepthOf(node)));
        }

        return new AvatarRoster(monarchNode.PlayerGuid.Full, monarchAccount.AccountId, isDeleted(monarch), members);
    }

    /// <summary>Steps up the Patron chain to the monarch (the monarch is 0).</summary>
    public static int DepthOf(AllegianceNode node)
    {
        var depth = 0;
        for (var n = node.Patron; n != null; n = n.Patron)
            depth++;
        return depth;
    }
}
