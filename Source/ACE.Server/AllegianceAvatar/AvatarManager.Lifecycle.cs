using System;
using System.Collections.Generic;
using System.Linq;

namespace ACE.Server.Avatars;

/// <summary>AvatarManager's lifecycle primitives (Task 8). Same lock and state as AvatarManager.cs.</summary>
public static partial class AvatarManager
{
    // ---- lifecycle (Task 8) ----------------------------------------------------------------------------------
    //
    // AvatarLifecycle decides and announces; these primitives are the only writers of the binding. Each takes the
    // lock, re-checks the state it was decided on (expectedVersion), bumps Version, and marks the row dirty so the
    // next flush persists it. None of them touches the NPCs, the announcer or the store: the caller does the side
    // effects after the lock is released.

    /// <summary>Patches the live activity index for one account (a login), so the Keeper rules see it at once.</summary>
    public static void TouchAccount(uint accountId)
    {
        if (!enabled)
            return;

        var now = clock();
        lock (sync)
            liveActivity.Touch(accountId, now);
    }

    internal static AvatarTuning CurrentTuning() => tuning();

    internal static double NowUnix() => clock();

    /// <summary>The roster of the allegiance led by <paramref name="monarchGuid"/>, or null.</summary>
    internal static AvatarRoster RosterFor(uint monarchGuid) => rosterForSource?.Invoke(monarchGuid);

    /// <summary>Distinct accounts among <paramref name="members"/> active within active_window_days, by the live index.</summary>
    internal static int ActiveAccountsAmong(IEnumerable<KeeperCandidate> members)
    {
        var now = clock();
        var window = (int)Math.Clamp(tuning().ActiveWindowDays, 0L, int.MaxValue);
        lock (sync)
        {
            var accounts = new HashSet<uint>();
            foreach (var m in members)
            {
                if (liveActivity.IsActive(m.AccountId, now, window))
                    accounts.Add(m.AccountId);
            }
            return accounts.Count;
        }
    }

    internal static double? AccountLastSeen(uint accountId)
    {
        lock (sync)
            return liveActivity.LastSeen(accountId);
    }

    /// <summary>True when founding is blocked for that monarch for the rest of the session.</summary>
    internal static bool FoundingBlocked(uint monarchGuid)
    {
        lock (sync)
            return blockedFoundings.Contains(monarchGuid);
    }

    /// <summary>True while a founding Insert for that monarch is in flight; binding to it then would race the new row.</summary>
    internal static bool FoundingPending(uint monarchGuid)
    {
        lock (sync)
            return pendingFoundings.Contains(monarchGuid);
    }

    /// <summary>The monarch guids of Bound Avatars whose monarch account, at the last Keeper resolve, is <paramref name="accountId"/>.</summary>
    internal static List<uint> MonarchsOfAccount(uint accountId)
    {
        lock (sync)
        {
            return byId.Values
                .Where(s => s.State == AvatarBindState.Bound && s.MonarchGuid.HasValue && s.MonarchAccountId == accountId)
                .Select(s => s.MonarchGuid.Value)
                .ToList();
        }
    }

    /// <summary>Resolves a Keeper for <paramref name="roster"/> against the live index without writing anything.</summary>
    internal static KeeperResult ResolveKeeper(AvatarRoster roster, uint? heirGuid)
    {
        var t = tuning();
        var now = clock();
        lock (sync)
            return AvatarKeeperResolver.Resolve(roster.MonarchGuid, roster.MonarchAccountId, roster.MonarchDeleted, heirGuid, roster.Members, liveActivity, now, t);
    }

    /// <summary>One Keeper change. Announce is false for the silent first settle after a load or a founding.</summary>
    internal readonly record struct KeeperChange(uint AvatarId, uint MonarchGuid, uint OldKeeper, uint NewKeeper, bool NowActing, bool Announce);

    /// <summary>
    /// Resolves the Keeper of the Bound Avatar of <paramref name="monarchGuid"/> against <paramref name="roster"/>
    /// (null: no allegiance, so the monarch keeps it) and the live index. Returns the change, or null when nothing changed.
    /// </summary>
    internal static KeeperChange? ApplyKeeper(uint monarchGuid, AvatarRoster roster)
    {
        if (!enabled)
            return null;

        var t = tuning();
        var now = clock();

        lock (sync)
        {
            if (!byMonarch.TryGetValue(monarchGuid, out var id) || !byId.TryGetValue(id, out var s) || s.State != AvatarBindState.Bound)
                return null;

            var keeper = roster == null || roster.MonarchGuid != monarchGuid
                ? new KeeperResult(monarchGuid, false, false)
                : AvatarKeeperResolver.Resolve(roster.MonarchGuid, roster.MonarchAccountId, roster.MonarchDeleted, s.HeirGuid, roster.Members, liveActivity, now, t);

            if (roster != null && roster.MonarchGuid == monarchGuid)
                s.MonarchAccountId = roster.MonarchAccountId;

            var first = !s.KeeperResolved;
            s.KeeperResolved = true;

            if (s.KeeperGuid == keeper.KeeperGuid && s.KeeperIsActing == keeper.IsActing)
                return null;

            var old = s.KeeperGuid;
            s.KeeperGuid = keeper.KeeperGuid;
            s.KeeperIsActing = keeper.IsActing;
            s.Version++;

            return new KeeperChange(id, monarchGuid, old, keeper.KeeperGuid, keeper.IsActing, !first);
        }
    }

    /// <summary>
    /// Moves a Bound Avatar to <paramref name="newMonarchGuid"/>. Refused when another Avatar is already bound
    /// there (the caller absorbs instead) or a founding for that monarch is in flight. The heir is cleared and the
    /// new monarch keeps it until the next resolve.
    /// </summary>
    internal static bool TryRebind(uint avatarId, uint newMonarchGuid, int? expectedVersion)
    {
        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s) || s.State != AvatarBindState.Bound || !s.MonarchGuid.HasValue)
                return false;
            if (expectedVersion.HasValue && s.Version != expectedVersion.Value)
                return false;
            if (s.MonarchGuid.Value == newMonarchGuid)
                return true;
            if ((byMonarch.TryGetValue(newMonarchGuid, out var other) && other != avatarId) || pendingFoundings.Contains(newMonarchGuid))
                return false;

            if (byMonarch.TryGetValue(s.MonarchGuid.Value, out var mine) && mine == avatarId)
                byMonarch.Remove(s.MonarchGuid.Value);
            vacatedUnflushed[s.MonarchGuid.Value] = avatarId;
            vacatedUnflushed.Remove(newMonarchGuid);

            s.MonarchGuid = newMonarchGuid;
            byMonarch[newMonarchGuid] = avatarId;
            s.HeirGuid = null;
            s.KeeperGuid = newMonarchGuid;
            s.KeeperIsActing = false;
            s.MonarchAccountId = 0;
            s.Version++;
            dirtyAvatars.Add(avatarId);
            return true;
        }
    }

    /// <summary>
    /// Absorbs <paramref name="sourceId"/> into <paramref name="targetId"/>: the target gains AbsorbXp(source) Avatar
    /// XP, the source's Favor is dropped, and the source is left Absorbed with no monarch. Both must be Bound and at
    /// their expected Versions. Returns the Avatar XP added, or null when refused.
    /// </summary>
    internal static ulong? TryAbsorb(uint sourceId, uint targetId, int? expectedSourceVersion, int? expectedTargetVersion)
    {
        var t = tuning();
        ulong? result;

        lock (sync)
        {
            if (sourceId == targetId || !byId.TryGetValue(sourceId, out var source) || !byId.TryGetValue(targetId, out var target))
                return null;
            if (source.State != AvatarBindState.Bound || target.State != AvatarBindState.Bound)
                return null;
            if ((expectedSourceVersion.HasValue && source.Version != expectedSourceVersion.Value)
                || (expectedTargetVersion.HasValue && target.Version != expectedTargetVersion.Value))
                return null;

            // Credit what the source earned since the last flush first, so up to one interval of contribution is not lost.
            CreditPending(clock(), t, sourceId);
            // Likewise the level pool XP still queued for the source (and everyone else: the drain is cheap and order free).
            DrainLevelFeed(t);

            var added = AvatarCurves.AbsorbXp(source.LifetimeXp, t);
            var targetLevelBefore = target.Level(t);
            target.LifetimeXp = target.LifetimeXp > ulong.MaxValue - added ? ulong.MaxValue : target.LifetimeXp + added;
            var addedLevelXp = AvatarCurves.AbsorbXp(source.LevelXp, t);
            target.LevelXp = target.LevelXp > ulong.MaxValue - addedLevelXp ? ulong.MaxValue : target.LevelXp + addedLevelXp;
            NoteLevelUp(target, targetLevelBefore, t);

            Unbind(source, AvatarBindState.Absorbed);
            source.AbsorbedIntoId = targetId;

            // The source is leaving: a rise its last credit caused is not announced to an allegiance it no longer serves.
            levelUps.RemoveAll(e => e.AvatarId == sourceId);

            target.Version++;
            dirtyAvatars.Add(targetId);
            result = added;
        }

        // Outside the lock: the dispatch touches NPCs, the world and chat.
        DispatchLevelUps(t);
        return result;
    }

    /// <summary>Retires a Bound Avatar (its monarch was deleted with nobody left to inherit). Its level is kept on the row.</summary>
    internal static bool TryRetire(uint avatarId, int? expectedVersion)
    {
        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s) || s.State != AvatarBindState.Bound)
                return false;
            if (expectedVersion.HasValue && s.Version != expectedVersion.Value)
                return false;

            Unbind(s, AvatarBindState.Retired);
            return true;
        }
    }

    /// <summary>Names (or with null clears) the heir of a Bound Avatar.</summary>
    internal static bool TrySetHeir(uint avatarId, uint? heirGuid, int? expectedVersion)
    {
        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s) || s.State != AvatarBindState.Bound)
                return false;
            if (expectedVersion.HasValue && s.Version != expectedVersion.Value)
                return false;
            if (s.HeirGuid == heirGuid)
                return true;

            s.HeirGuid = heirGuid;
            s.Version++;
            dirtyAvatars.Add(avatarId);
            return true;
        }
    }

    /// <summary>
    /// Stores a Bound Avatar's look (null clears it to never dressed) and caches the parsed look beside it. Refused when
    /// the Avatar is not Bound, when it has changed since <paramref name="expectedVersion"/>, or when a non-null
    /// <paramref name="lookJson"/> does not parse (corrupt, an unknown version, or over AvatarLookJson.MaxBytes), so
    /// LookJson and Look never disagree. Bumps Version and marks the row dirty. Touches no NPC: the caller re-dresses.
    /// </summary>
    internal static bool TrySetLook(uint avatarId, string lookJson, int? expectedVersion)
    {
        var look = AvatarLookJson.Parse(lookJson);
        if (lookJson != null && look == null)
            return false;

        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s) || s.State != AvatarBindState.Bound)
                return false;
            if (expectedVersion.HasValue && s.Version != expectedVersion.Value)
                return false;
            if (s.LookJson == lookJson)
                return true;

            s.LookJson = lookJson;
            s.Look = look;
            s.Version++;
            dirtyAvatars.Add(avatarId);
            return true;
        }
    }

    // ---- seat (Task 11) ----------------------------------------------------------------------------------------
    //
    // The Seat is part of the binding: each write bumps Version and marks the row dirty. AvatarNpcs owns the NPCs
    // and the side effects; these only move the fields.

    /// <summary>
    /// Seats a Bound Avatar: stores <paramref name="seat"/> and stamps SeatedAtUtc. Refused when the Avatar is not
    /// Bound or, with <paramref name="expectedVersion"/>, has changed since the caller looked.
    /// </summary>
    internal static bool TrySetSeat(uint avatarId, AvatarSeat seat, DateTime seatedAtUtc, int? expectedVersion)
    {
        if (seat == null)
            return false;

        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s) || s.State != AvatarBindState.Bound)
                return false;
            if (expectedVersion.HasValue && s.Version != expectedVersion.Value)
                return false;

            s.Seat = seat;
            s.SeatedAtUtc = DateTime.SpecifyKind(seatedAtUtc, DateTimeKind.Utc);
            s.Version++;
            dirtyAvatars.Add(avatarId);
            return true;
        }
    }

    /// <summary>
    /// Clears the Seat of an Avatar in any state (an absorbed or retired one keeps its Seat until this runs) and returns
    /// the Seat it held, or null when it had none. SeatedAtUtc is kept, so the reseat cooldown still runs from the last
    /// seat and losing a mansion cannot be used to skip it.
    /// </summary>
    internal static AvatarSeat TryClearSeat(uint avatarId)
    {
        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s) || s.Seat == null)
                return null;

            var old = s.Seat;
            s.Seat = null;
            s.Version++;
            dirtyAvatars.Add(avatarId);
            return old;
        }
    }

    /// <summary>A snapshot of every Avatar, in any state, that holds a Seat.</summary>
    internal static List<AvatarState> SeatedAvatars()
    {
        lock (sync)
            return byId.Values.Where(s => s.Seat != null).ToList();
    }

    /// <summary>A snapshot of the Bound Avatars seated on that landblock and instance, with the Seat each held when read.</summary>
    internal static List<(uint AvatarId, AvatarSeat Seat)> SeatsOn(ushort landblock, uint instance)
    {
        if (!enabled)
            return new List<(uint, AvatarSeat)>();

        lock (sync)
        {
            return byId.Values
                .Where(s => s.State == AvatarBindState.Bound && AvatarSeatRules.SeatBelongsTo(s.Seat, landblock, instance))
                .Select(s => (s.AvatarId, s.Seat))
                .ToList();
        }
    }

    /// <summary>
    /// Caller holds the lock. Ends a binding: no monarch, no Favor, no reservation, no Keeper, and any not yet
    /// credited contribution is dropped (it would only add Favor to an Avatar nobody can spend). LifetimeXp and the
    /// ledger are kept. The Seat fields are left for AvatarNpcs.RevalidateSeat to clear when it unseats.
    /// </summary>
    private static void Unbind(AvatarState s, AvatarBindState newState)
    {
        if (s.MonarchGuid.HasValue && byMonarch.TryGetValue(s.MonarchGuid.Value, out var mine) && mine == s.AvatarId)
            byMonarch.Remove(s.MonarchGuid.Value);
        if (s.MonarchGuid.HasValue)
            vacatedUnflushed[s.MonarchGuid.Value] = s.AvatarId;

        foreach (var key in pending.Keys.Where(k => k.AvatarId == s.AvatarId).ToList())
            pending.Remove(key);

        s.State = newState;
        s.MonarchGuid = null;
        s.Favor = 0;
        s.ReservedFavor = 0;
        s.HeirGuid = null;
        s.KeeperGuid = 0;
        s.KeeperIsActing = false;
        s.Active = false;
        s.Version++;
        dirtyAvatars.Add(s.AvatarId);
    }
}
