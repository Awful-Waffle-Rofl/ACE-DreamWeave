using System;
using System.Collections.Generic;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Avatars;

/// <summary>What an Avatar can buy once (or a tunable number of times) per Avatar day.</summary>
public enum AvatarDailyKind
{
    /// <summary>A private World Event, quiet or announced; they share one count (allegiance_avatar_event_daily_limit).</summary>
    Event,

    /// <summary>The Allegiance Blessing: at most one per day, stored as a stamp only (blessing_Bought_At).</summary>
    Blessing,
}

/// <summary>
/// Daily purchase claims. The order every daily purchase follows is: claim -> reserve Favor -> start -> commit both, or
/// release both. A claim is held from the moment a buyer is accepted until the purchase either takes (CommitDaily turns it
/// into a used slot, persisted through the dirty/flush path) or fails (ReleaseDaily gives it back), so two buyers racing for
/// the last slot cannot both be accepted.
///
/// Used slots are persisted on the Avatar (blessing_Bought_At, last_Event_At + events_On_Day) and are measured against the
/// Avatar day (AvatarDay). Pending claims are memory only, like Favor reservations: a restart before the purchase takes
/// frees them, because the run or purchase they were held for does not survive the restart either.
///
/// Every method takes the day clock as an argument (built by the caller from AvatarDay.BuildClock outside the lock), so
/// the lock is never held across a time-zone lookup.
/// </summary>
public static partial class AvatarManager
{
    /// <summary>Claims taken but neither committed nor released. Guarded by sync. Reset by Initialize.</summary>
    private static Dictionary<(uint AvatarId, AvatarDailyKind Kind), int> pendingDaily = new Dictionary<(uint, AvatarDailyKind), int>();

    /// <summary>
    /// Takes one daily slot for a purchase in flight. True when used-today plus already-pending claims is below
    /// <paramref name="limit"/>, or when <paramref name="limit"/> is 0 or less (no limit; the claim is still tracked so a
    /// commit and a release stay balanced). False for an unknown Avatar or a disabled manager. A Blessing's limit is
    /// capped at 1: its storage is a stamp, which can only say "bought today" or not.
    /// </summary>
    public static bool TryClaimDaily(uint avatarId, AvatarDailyKind kind, long limit, SurveyDayClock day)
    {
        if (!enabled || day == null)
            return false;

        if (kind == AvatarDailyKind.Blessing && (limit <= 0 || limit > 1))
            limit = 1;

        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s))
                return false;

            var key = (avatarId, kind);
            pendingDaily.TryGetValue(key, out var held);

            if (limit > 0 && UsedToday(s, kind, day) + held >= limit)
                return false;

            pendingDaily[key] = held + 1;
            return true;
        }
    }

    /// <summary>
    /// Turns one pending claim into a used slot on the clock's Avatar day, and marks the Avatar dirty so the next flush
    /// writes it. The slot is recorded even when no claim is pending (one lost to a restart cannot reach here, so this only
    /// happens to a caller that skipped the claim), because an event that reached Active must never be free. A stamp
    /// already later than now (a clock that stepped back) is kept, so it goes on counting as used.
    /// </summary>
    public static void CommitDaily(uint avatarId, AvatarDailyKind kind, SurveyDayClock day)
    {
        if (day == null)
            return;

        var now = AvatarDay.NowUtc(day);

        lock (sync)
        {
            TakePending(avatarId, kind);

            if (!byId.TryGetValue(avatarId, out var s))
                return;

            switch (kind)
            {
                case AvatarDailyKind.Event:
                    var today = AvatarDay.IsToday(s.LastEventUtc, day) ? Math.Max((int)s.EventsOnDay, 1) : 0;
                    s.EventsOnDay = (byte)Math.Min(byte.MaxValue, today + 1);
                    s.LastEventUtc = Later(s.LastEventUtc, now);
                    break;

                case AvatarDailyKind.Blessing:
                    s.BlessingBoughtUtc = Later(s.BlessingBoughtUtc, now);
                    break;
            }

            dirtyAvatars.Add(avatarId);
        }
    }

    /// <summary>Gives a pending claim back unused (a failed reserve, a failed start, a run that ended before Active). Saturates at 0.</summary>
    public static void ReleaseDaily(uint avatarId, AvatarDailyKind kind)
    {
        lock (sync)
            TakePending(avatarId, kind);
    }

    /// <summary>
    /// The day's count for display and for the pre-check: slots used on the clock's Avatar day, and claims still in flight.
    /// (0, 0) for an unknown Avatar.
    /// </summary>
    public static (int Used, int Pending) DailyCount(uint avatarId, AvatarDailyKind kind, SurveyDayClock day)
    {
        if (day == null)
            return (0, 0);

        lock (sync)
        {
            if (!byId.TryGetValue(avatarId, out var s))
                return (0, 0);

            pendingDaily.TryGetValue((avatarId, kind), out var held);
            return (UsedToday(s, kind, day), held);
        }
    }

    /// <summary>Caller holds sync. Slots used on the clock's day: a stamp on today OR LATER counts (AvatarDay.IsToday).</summary>
    private static int UsedToday(AvatarState s, AvatarDailyKind kind, SurveyDayClock day) => kind switch
    {
        // A stamp on today with a zero count can only come from a hand-edited row; it still means one event was called.
        AvatarDailyKind.Event => AvatarDay.IsToday(s.LastEventUtc, day) ? Math.Max((int)s.EventsOnDay, 1) : 0,
        AvatarDailyKind.Blessing => AvatarDay.IsToday(s.BlessingBoughtUtc, day) ? 1 : 0,
        _ => 0,
    };

    /// <summary>Caller holds sync.</summary>
    private static void TakePending(uint avatarId, AvatarDailyKind kind)
    {
        var key = (avatarId, kind);

        if (!pendingDaily.TryGetValue(key, out var held))
            return;

        if (held <= 1)
            pendingDaily.Remove(key);
        else
            pendingDaily[key] = held - 1;
    }

    private static DateTime Later(DateTime? stamp, DateTime now) => stamp.HasValue && stamp.Value > now ? stamp.Value : now;
}
