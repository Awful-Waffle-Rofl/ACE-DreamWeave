namespace ACE.Server.Avatars;

/// <summary>What a swear does to the Avatars involved.</summary>
public enum SwearAvatarEffect
{
    /// <summary>No Avatar travels: the plain swear, unchanged.</summary>
    None,

    /// <summary>The swearing monarch's Avatar becomes the target allegiance's, whole.</summary>
    Adopt,

    /// <summary>The swearing monarch's Avatar is absorbed into the target allegiance's at absorb_fraction.</summary>
    Absorb,
}

/// <summary>What a break or a boot does to the Avatar of the allegiance it leaves.</summary>
public enum BreakAvatarEffect
{
    /// <summary>No Avatar is involved.</summary>
    None,

    /// <summary>The Avatar stays where it is; the actor gets an info line and the break goes ahead at once.</summary>
    InfoStays,

    /// <summary>The acting Keeper leaves while the monarch is idle and takes the Avatar with them, permanently.</summary>
    Inheritance,

    /// <summary>The monarch's break leaves fewer than min_active_accounts active accounts: the Avatar goes inactive.</summary>
    GoesInactive,
}

/// <summary>
/// The pure lifecycle classifiers behind the spec's action table. They decide WHICH effect applies; AvatarLifecycle
/// asks the actor, re-checks and applies it.
/// </summary>
public static class AvatarLifecycleRules
{
    /// <summary>
    /// A swear moves an Avatar only when the swearer is a monarch holding a Bound Avatar. Into an allegiance whose
    /// monarch holds a Bound Avatar it is absorbed; otherwise it is adopted whole.
    /// </summary>
    public static SwearAvatarEffect ClassifySwear(bool swearerIsMonarch, AvatarState swearerAvatar, AvatarState targetAvatar)
    {
        if (!swearerIsMonarch || swearerAvatar == null || swearerAvatar.State != AvatarBindState.Bound)
            return SwearAvatarEffect.None;

        if (targetAvatar != null && targetAvatar.State == AvatarBindState.Bound && targetAvatar.AvatarId != swearerAvatar.AvatarId)
            return SwearAvatarEffect.Absorb;

        return SwearAvatarEffect.Adopt;
    }

    /// <summary>
    /// Classifies a break or boot against an allegiance that HAS an Avatar (the caller returns None otherwise).
    /// actorIsActingKeeper must be true only when the actor holds the acting Keeper's rights AND is the one leaving
    /// (breaking from their own patron): a Keeper who drops a vassal or boots a member stays, and so does the Avatar.
    /// Inheritance wins over GoesInactive when both apply (pre-flight 22).
    /// </summary>
    public static BreakAvatarEffect ClassifyBreak(bool actorIsActingKeeper, bool monarchIdle, bool actorIsMonarch, int activeAccountsAfter, AvatarTuning t)
    {
        if (actorIsActingKeeper && monarchIdle && !actorIsMonarch)
            return BreakAvatarEffect.Inheritance;

        if (actorIsMonarch && activeAccountsAfter < t.MinActiveAccounts)
            return BreakAvatarEffect.GoesInactive;

        return BreakAvatarEffect.InfoStays;
    }
}
