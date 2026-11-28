using System;

using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Avatars;

/// <summary>
/// Who may spend an Avatar's Favor. A spender must belong to the Avatar's bound allegiance and be its monarch, its acting
/// Keeper, or an officer of at least Seneschal rank (retail officer level 2; a Speaker, level 1, cannot spend).
/// The pure rule takes plain values; the Player overloads read them from the live allegiance.
/// </summary>
public static class AvatarRights
{
    /// <summary>Retail officer level of a Seneschal.</summary>
    public const int SeneschalLevel = 2;

    /// <summary>Test seam: the viewer's monarch guid (their own guid when they lead or are solo) and whether they are a Seneschal or better.</summary>
    internal static Func<Player, (uint MonarchGuid, bool IsSeneschal)> AllegianceFacts = DefaultAllegianceFacts;

    internal static void ResetSeams() => AllegianceFacts = DefaultAllegianceFacts;

    private static (uint, bool) DefaultAllegianceFacts(Player p)
    {
        var allegiance = p.Allegiance;
        var rank = 0;

        if (allegiance?.Officers != null && allegiance.Officers.TryGetValue(p.Guid, out var node))
            rank = node.Player.AllegianceOfficerRank ?? 0;

        return FactsFrom(p.Guid.Full, allegiance?.MonarchId, rank);
    }

    /// <summary>The pure mapping behind the production reader: the monarch is the allegiance's (the player's own guid with none), and a Seneschal is officer rank 2 or better.</summary>
    internal static (uint MonarchGuid, bool IsSeneschal) FactsFrom(uint ownGuid, uint? allegianceMonarchId, int officerRank) =>
        (allegianceMonarchId ?? ownGuid, officerRank >= SeneschalLevel);

    public static bool CanSpend(bool isMember, bool isMonarch, bool isActingKeeper, int officerLevel) =>
        isMember && (isMonarch || isActingKeeper || officerLevel >= SeneschalLevel);

    /// <summary>True when the player's monarch is the Avatar's bound monarch.</summary>
    public static bool IsMember(Player p, AvatarState a)
    {
        if (p == null || a == null || a.MonarchGuid == null)
            return false;

        return AllegianceFacts(p).MonarchGuid == a.MonarchGuid.Value;
    }

    /// <summary>
    /// The player's monarch guid for a check that can run BEFORE the player is online: the login reroute validates a saved
    /// instance (WorldManager, ValidateInstanceDestination -> EphemeralRealm.Accepts) before PlayerEnterWorld, and
    /// <see cref="Player.Allegiance"/> is only set by the online switch (PlayerManager -> AllegianceManager.LoadPlayer).
    /// So this reads the guid-keyed node map first (AllegianceManager.Players, filled for every member when the allegiance
    /// loads and rebuilt on every join or break), falls back to <see cref="Player.Allegiance"/> when the player has no node,
    /// and to the player's own guid with neither. Read live on every call, so a former member is refused once the map is rebuilt.
    /// </summary>
    internal static uint AdmissionMonarchGuid(Player p)
    {
        var node = AllegianceManager.GetAllegianceNode(p);

        if (node != null)
            return (node.Monarch ?? node).PlayerGuid.Full;

        return p.Allegiance?.MonarchId ?? p.Guid.Full;
    }

    /// <summary>The guid-keyed form of <see cref="AdmissionMonarchGuid"/> for a character that may be offline: the node map's monarch, else the character's own guid.</summary>
    internal static uint AdmissionMonarchGuidOf(uint characterGuid)
    {
        if (AllegianceManager.Players.TryGetValue(new ACE.Entity.ObjectGuid(characterGuid), out var node) && node != null)
            return (node.Monarch ?? node).PlayerGuid.Full;

        return characterGuid;
    }

    /// <summary>
    /// <see cref="IsMember"/> for an admission check (the Gauntlet arena): the same rule, but keyed by guid through
    /// <see cref="AdmissionMonarchGuid"/> instead of the online-only allegiance reader, so a member relogging inside the
    /// arena is still admitted. Does not go through the <see cref="AllegianceFacts"/> test seam.
    /// </summary>
    public static bool IsMemberForAdmission(Player p, AvatarState a)
    {
        if (p == null || a == null || a.MonarchGuid == null)
            return false;

        return AdmissionMonarchGuid(p) == a.MonarchGuid.Value;
    }

    public static bool IsKeeper(Player p, AvatarState a) => p != null && a != null && a.KeeperGuid == p.Guid.Full;

    public static bool CanSpend(Player p, AvatarState a)
    {
        if (p == null || a == null)
            return false;

        var facts = AllegianceFacts(p);
        var isMember = IsMember(p, a);
        var isMonarch = a.MonarchGuid == p.Guid.Full;

        return CanSpend(isMember, isMonarch, IsKeeper(p, a), facts.IsSeneschal ? SeneschalLevel : 0);
    }
}
