using System;

namespace ACE.Server.ThreadDungeons
{
    /// <summary>
    /// Pure day-boundary arithmetic for the daily-survey reset (owner design 2026-09-17): the window, the
    /// count and all three payout tiers turn over together at one server-wide wall-clock boundary, rather
    /// than each carrying its own independent 20 h cooldown from whenever it was last paid.
    ///
    /// Reads no PropertyManager tunable and touches no live clock: every method takes the instant and the
    /// zone/hour it is measured against as plain arguments, so this is unit testable without a live Player
    /// or the test-harness PropertyManager throw.
    /// </summary>
    public static class SurveyDay
    {
        /// <summary>
        /// The calendar day (as a day number, comparable and orderable) that <paramref name="unixSeconds"/>
        /// falls on, measured in <paramref name="zone"/>'s wall clock with the boundary shifted back by
        /// <paramref name="resetHour"/> hours. Two instants share a DayIndex exactly when they fall in the
        /// same survey day.
        /// </summary>
        public static long DayIndex(uint unixSeconds, TimeZoneInfo zone, int resetHour)
        {
            var instant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);

            // Shifting the wall clock back by the reset hour turns "the day that starts at resetHour:00"
            // into an ordinary midnight-to-midnight day, so .Date is the day index directly.
            var shifted = local.AddHours(-resetHour);

            return shifted.Date.Ticks / TimeSpan.TicksPerDay;
        }

        /// <summary>
        /// The unix time of the next survey-day boundary strictly after <paramref name="nowUnix"/>. DST-aware:
        /// a boundary that lands in a spring-forward gap (zone.IsInvalidTime) is not a real local time, so the
        /// candidate steps forward an hour at a time until it lands on one that exists.
        /// </summary>
        public static uint NextReset(uint nowUnix, TimeZoneInfo zone, int resetHour)
        {
            var instant = DateTimeOffset.FromUnixTimeSeconds(nowUnix).UtcDateTime;
            var local = TimeZoneInfo.ConvertTimeFromUtc(instant, zone);
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            var candidate = DateTime.SpecifyKind(local.Date.AddHours(resetHour), DateTimeKind.Unspecified);

            if (candidate <= local)
                candidate = DateTime.SpecifyKind(local.Date.AddDays(1).AddHours(resetHour), DateTimeKind.Unspecified);

            while (zone.IsInvalidTime(candidate))
                candidate = candidate.AddHours(1);

            var utc = TimeZoneInfo.ConvertTimeToUtc(candidate, zone);

            return (uint)new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();
        }

        /// <summary>
        /// Renders a duration as "{h}h {m}m", or just "{m}m" under one hour. Rounds UP to the next whole
        /// minute and floors at 1m, so a boundary a few seconds away never reads as "0m" (which would read to
        /// a player as "come back now", the opposite of what Held means).
        /// </summary>
        public static string FormatDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
                span = TimeSpan.Zero;

            var totalMinutes = (long)Math.Ceiling(span.TotalMinutes);
            if (totalMinutes < 1)
                totalMinutes = 1;

            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;

            return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
        }
    }

    /// <summary>
    /// One visit's frozen view of "now", for the daily-survey reset. Built once per visit (or once per
    /// filed survey) so every read in that visit measures against the same instant - see
    /// <see cref="SurveyArchivistStation.BuildClock"/> and the delegate in
    /// <see cref="ThreadDungeonManager.RecordSurvey"/>.
    /// </summary>
    public sealed class SurveyDayClock
    {
        public uint Now { get; }
        public TimeZoneInfo Zone { get; }
        public int ResetHour { get; }

        /// <summary>This clock's own day index, computed once at construction.</summary>
        public long Today { get; }

        public SurveyDayClock(uint now, TimeZoneInfo zone, int resetHour)
        {
            Now = now;
            Zone = zone ?? throw new ArgumentNullException(nameof(zone));
            ResetHour = resetHour;
            Today = SurveyDay.DayIndex(now, zone, resetHour);
        }

        /// <summary>
        /// True when <paramref name="stamp"/> falls on today's survey day OR LATER. The "or later" half
        /// matters only for a stamp from the future - the clock stepped back, or the two disagree for some
        /// other reason - which must read as paid/live, never as free to pay again: the alternative is a
        /// repeatable-reward exploit every time the server clock moves backward.
        /// </summary>
        public bool IsToday(uint stamp) => SurveyDay.DayIndex(stamp, Zone, ResetHour) >= Today;

        public uint NextReset() => SurveyDay.NextReset(Now, Zone, ResetHour);
    }
}
