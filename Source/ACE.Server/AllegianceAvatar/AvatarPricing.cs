using System;

using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.Avatars;

/// <summary>
/// The Favor prices of one Avatar's event purchases and its Favor cap, as pure functions of the tunables and the two account
/// counts: R, the Avatar's active accounts (<see cref="AvatarState.ActiveAccounts"/>), and S, the server's active accounts
/// (the figure the contribution feed's roster scale uses). Each price is max(floor, ceil(perAccount x effective accounts)),
/// see <see cref="AvatarCurves.ScaledPrice"/>. The Allegiance Blessing keeps its own formula (<see cref="AvatarBlessing.Price"/>).
/// </summary>
public static class AvatarPricing
{
    public static ulong Quiet(AvatarTuning t, int avatarActiveAccounts, int serverActiveAccounts) =>
        AvatarCurves.ScaledPrice(t.EventPriceQuiet, t.EventPriceQuietPerAccount, avatarActiveAccounts, serverActiveAccounts, t);

    public static ulong Announced(AvatarTuning t, int avatarActiveAccounts, int serverActiveAccounts) =>
        AvatarCurves.ScaledPrice(t.EventPriceAnnounced, t.EventPriceAnnouncedPerAccount, avatarActiveAccounts, serverActiveAccounts, t);

    public static ulong Gauntlet(AvatarTuning t, GauntletTuning g, int avatarActiveAccounts, int serverActiveAccounts) =>
        AvatarCurves.ScaledPrice(g.Price, g.PricePerAccount, avatarActiveAccounts, serverActiveAccounts, t);

    /// <summary>The largest of the quiet event, announced event and Gauntlet prices.</summary>
    public static ulong Largest(AvatarTuning t, GauntletTuning g, int avatarActiveAccounts, int serverActiveAccounts) =>
        Math.Max(Quiet(t, avatarActiveAccounts, serverActiveAccounts),
            Math.Max(Announced(t, avatarActiveAccounts, serverActiveAccounts), Gauntlet(t, g, avatarActiveAccounts, serverActiveAccounts)));

    /// <summary>The per-Avatar Favor cap: <see cref="AvatarCurves.FavorCapFor"/> of the largest price.</summary>
    public static ulong FavorCap(AvatarTuning t, GauntletTuning g, int avatarActiveAccounts, int serverActiveAccounts) =>
        AvatarCurves.FavorCapFor(Largest(t, g, avatarActiveAccounts, serverActiveAccounts), t);
}
