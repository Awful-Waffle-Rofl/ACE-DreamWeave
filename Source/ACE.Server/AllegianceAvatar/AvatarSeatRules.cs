using System;

using ACE.Entity;
using ACE.Entity.Enum;

namespace ACE.Server.Avatars;

/// <summary>
/// The pure rules of the Seat and the summon: which mansion may hold a seated Avatar, the reseat cooldown, which
/// landblock a Seat belongs to, and the player-facing lines. No world, no PropertyManager: the tunables arrive as
/// an <see cref="AvatarTuning"/> value.
/// </summary>
public static class AvatarSeatRules
{
    public const string NoAvatarMessage = "Your allegiance has no Avatar.";

    /// <summary>The refusal while allegiance_avatar_enabled is off.</summary>
    public const string SwitchedOffMessage = "The Allegiance Avatar is switched off.";
    public const string SummonNotAllowedMessage = "Only the Monarch, the Keeper or a Seneschal of the Avatar's allegiance may summon it.";
    public const string SummonDeadMessage = "You cannot summon the Avatar while dead.";
    public const string SummonOutdoorsMessage = "The Avatar can only be summoned outdoors.";
    public const string SummonDenylistedMessage = "You cannot summon the Avatar here.";
    public const string SummonFailedMessage = "The Avatar could not be summoned.";
    public const string SeatNotKeeperMessage = "Only the Keeper can seat the Avatar.";
    public const string SeatNoMansionMessage = "You must be at a mansion to seat the Avatar.";
    public const string SeatNotOnPropertyMessage = "You must be on the grounds of the mansion to seat the Avatar.";
    public const string SeatNotQualifiedMessage = "The mansion must belong to the monarch or the Keeper.";
    public const string SeatChangedMessage = "the Avatar has changed since you were asked";
    public const string SeatedMessage = "The Avatar is seated here.";

    /// <summary>
    /// True when a house can hold the seated Avatar: a mansion owned by the monarch or the acting Keeper. SlumLord
    /// (the allegiance-housing gate) checks rank only, which is why the owner check lives here.
    /// </summary>
    public static bool Qualifies(HouseType type, uint? houseOwner, uint monarchGuid, uint keeperGuid) =>
        type == HouseType.Mansion && houseOwner != null && (houseOwner == monarchGuid || houseOwner == keeperGuid);

    /// <summary>True when the reseat cooldown allows a (re)seat: never seated, or seated at least reseatCooldownDays ago.</summary>
    public static bool CanReseat(DateTime? seatedAtUtc, DateTime nowUtc, AvatarTuning t)
    {
        if (seatedAtUtc == null)
            return true;

        return nowUtc - seatedAtUtc.Value >= TimeSpan.FromDays(Math.Max(0, t.ReseatCooldownDays));
    }

    /// <summary>Whole days (rounded up, at least 1) until <see cref="CanReseat"/> allows a reseat; 0 when it already does.</summary>
    public static long DaysUntilReseat(DateTime? seatedAtUtc, DateTime nowUtc, AvatarTuning t)
    {
        if (CanReseat(seatedAtUtc, nowUtc, t))
            return 0;

        var left = seatedAtUtc.Value + TimeSpan.FromDays(Math.Max(0, t.ReseatCooldownDays)) - nowUtc;
        return Math.Max(1L, (long)Math.Ceiling(left.TotalDays));
    }

    /// <summary>True when the Seat stands on that landblock (the high 16 bits of the cell) in that instance.</summary>
    public static bool SeatBelongsTo(AvatarSeat seat, ushort landblock, uint instance) =>
        seat?.Position != null && seat.Position.LandblockShort == landblock && seat.Position.Instance == instance;

    /// <summary>
    /// The owner to test with <see cref="Qualifies"/> when the house itself is not at hand: the monarch when the
    /// monarch's character owns that house, else the Keeper when the Keeper's does, else null (nobody who could
    /// qualify). <paramref name="houseInstanceOf"/> returns a character's owned house guid.
    /// </summary>
    public static uint? OwnerAmong(uint houseGuid, uint monarchGuid, uint keeperGuid, Func<uint, uint?> houseInstanceOf)
    {
        if (houseInstanceOf(monarchGuid) == houseGuid)
            return monarchGuid;
        if (keeperGuid != 0 && keeperGuid != monarchGuid && houseInstanceOf(keeperGuid) == houseGuid)
            return keeperGuid;
        return null;
    }

    public static string SeatPrompt(AvatarTuning t) =>
        $"Seat the Avatar here? You can't move it again for {Math.Max(0, t.ReseatCooldownDays)} days.";

    public static string ReseatCooldownMessage(long days) =>
        $"The Avatar can't be moved again for {days} more {(days == 1 ? "day" : "days")}.";

    public static string UnseatLine(string place) => $"The Avatar has left {place} and wanders again.";

    /// <summary>
    /// The refusal for a summon at <paramref name="location"/>, or null when it may go ahead: outdoors only (an event
    /// starts where the Avatar stands, and the World Event engine needs an outdoor cell), and never in a landblock on
    /// the account_vault_denylist emergency lever. The mule allowlist is deliberately not applied (spec correction 4).
    /// </summary>
    public static string SummonAreaRefusal(Position location, Func<Position, bool> isDenylisted)
    {
        if (location == null || location.Indoors)
            return SummonOutdoorsMessage;
        if (isDenylisted(location))
            return SummonDenylistedMessage;
        return null;
    }

    /// <summary>The per-rung placement rule for the summoned Avatar: outdoors and not denylisted.</summary>
    public static bool IsPlacementRungPermitted(Position location, Func<Position, bool> isDenylisted) =>
        location != null && !location.Indoors && !isDenylisted(location);
}
