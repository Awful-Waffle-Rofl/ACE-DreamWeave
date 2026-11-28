using System;

using ACE.Server.ThreadDungeons;

namespace ACE.Server.Avatars;

/// <summary>
/// The Avatar day: the once-per-day window the Avatar's daily purchases (the private World Event limit, and later the
/// Allegiance Blessing) are counted in. It turns over at allegiance_avatar_day_reset_hour in
/// allegiance_avatar_day_reset_timezone (12:00 AM US Eastern by default), DST-aware, using the same day arithmetic as the
/// Threads survey reset and the PvP arena Blood cap (SurveyDay / SurveyDayClock) and the same zone resolver
/// (SurveyArchivistStation.ResolveZone, which falls back to a fixed UTC-5 offset when the runtime has no tz data).
///
/// ONE DAY DECISION. Every "already used today" question goes through <see cref="IsToday"/> on a clock from
/// <see cref="BuildClock"/>, and every countdown through <see cref="UntilReset"/>. A stamp dated in the future counts as
/// used (SurveyDayClock.IsToday's "or later" rule), so a clock that steps back never frees a second purchase.
///
/// Pure: reads no PropertyManager value. The zone id and hour come in through the tuning record.
/// </summary>
public static class AvatarDay
{
    /// <summary>A frozen view of "now" for one decision, measured against the tuning's zone and hour (hour clamped to [0, 23]).</summary>
    public static SurveyDayClock BuildClock(AvatarTuning t, DateTime nowUtc)
    {
        var zone = SurveyArchivistStation.ResolveZone(t?.DayResetTimezone);
        var hour = (int)Math.Clamp(t?.DayResetHour ?? 0L, 0L, 23L);

        return new SurveyDayClock(ToUnix(nowUtc), zone, hour);
    }

    /// <summary>
    /// True when <paramref name="stampUtc"/> falls on the clock's Avatar day or later. Null (never stamped) is false. A
    /// stamp before the unix epoch is false (it cannot be today); one past the uint range is true (it is in the future).
    /// </summary>
    public static bool IsToday(DateTime? stampUtc, SurveyDayClock clock)
    {
        if (!stampUtc.HasValue)
            return false;

        var seconds = UnixSeconds(stampUtc.Value);

        if (seconds < 0)
            return false;

        if (seconds > uint.MaxValue)
            return true;

        return clock.IsToday((uint)seconds);
    }

    /// <summary>The time left until the next Avatar day starts, as the survey reset renders it ("5h 12m", never "0m").</summary>
    public static string UntilReset(SurveyDayClock clock) =>
        SurveyDay.FormatDuration(TimeSpan.FromSeconds((double)clock.NextReset() - clock.Now));

    /// <summary>The clock's instant as a UTC DateTime, to the second.</summary>
    public static DateTime NowUtc(SurveyDayClock clock) => DateTimeOffset.FromUnixTimeSeconds(clock.Now).UtcDateTime;

    internal static uint ToUnix(DateTime utc)
    {
        var seconds = UnixSeconds(utc);
        return seconds <= 0 ? 0u : seconds >= uint.MaxValue ? uint.MaxValue : (uint)seconds;
    }

    private static long UnixSeconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return (long)Math.Floor((utc - DateTime.UnixEpoch).TotalSeconds);
    }
}
