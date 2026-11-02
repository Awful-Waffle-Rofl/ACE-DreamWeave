using System;
using System.Collections.Generic;
using System.Globalization;

namespace ACE.Server.Pvp.Battlegrounds
{
    /// <summary>One daily window in local wall-clock time: <see cref="Start"/> inclusive, <see cref="End"/> exclusive, End on the next day when <see cref="CrossesMidnight"/>.</summary>
    public readonly record struct BgBonusSpan(TimeSpan Start, TimeSpan End)
    {
        /// <summary>An end before the start runs into the next day (Parse rejects Start == End).</summary>
        public bool CrossesMidnight => End <= Start;
    }

    /// <summary>
    /// The Double Marks windows (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"): PURE schedule arithmetic. Reads no
    /// PropertyManager tunable and no live clock; the spec, the instant and the zone are all arguments, so it is unit
    /// testable. Overlapping or touching spans merge into one window, so a transition is always a real edge.
    /// </summary>
    public static class BgBonusWindow
    {
        /// <summary>
        /// Parses "06:00-07:00,18:00-19:00": 24-hour local HH:mm-HH:mm ranges, comma separated, whitespace tolerant. A
        /// malformed entry (including an equal start and end, which would be an unbounded 24 hours) is skipped and its trimmed text added to <paramref name="malformed"/> (when given); the caller
        /// logs it. A null, empty or blank spec is no windows.
        /// </summary>
        public static IReadOnlyList<BgBonusSpan> Parse(string spec, List<string> malformed = null)
        {
            var spans = new List<BgBonusSpan>();

            if (string.IsNullOrWhiteSpace(spec))
                return spans;

            foreach (var raw in spec.Split(','))
            {
                var entry = raw.Trim();

                if (entry.Length == 0)
                    continue;

                var halves = entry.Split('-');

                if (halves.Length == 2 && TryParseClock(halves[0], out var start) && TryParseClock(halves[1], out var end) && start != end)
                    spans.Add(new BgBonusSpan(start, end));
                else
                    malformed?.Add(entry);
            }

            return spans;
        }

        private static bool TryParseClock(string text, out TimeSpan value)
        {
            value = default;
            var parts = text.Trim().Split(':');

            if (parts.Length != 2 || parts[0].Length is < 1 or > 2 || parts[1].Length != 2)
                return false;

            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
                return false;

            if (hour > 23 || minute > 59)
                return false;

            value = new TimeSpan(hour, minute, 0);
            return true;
        }

        /// <summary>Whether <paramref name="utc"/> falls inside any window: start inclusive, end exclusive.</summary>
        public static bool IsActive(DateTime utc, IReadOnlyList<BgBonusSpan> spans, TimeZoneInfo zone)
        {
            utc = AsUtc(utc);

            foreach (var (start, end) in Windows(utc, spans, zone))
                if (utc >= start && utc < end)
                    return true;

            return false;
        }

        /// <summary>
        /// The next window start and the next window end strictly after <paramref name="utc"/>, in UTC, DST-correct (null
        /// when there are no windows, or the only window never closes because the spans cover every instant). A local time in a
        /// spring-forward gap steps forward an hour at a time like SurveyDay.NextReset; an ambiguous fall-back time takes
        /// its first occurrence.
        /// </summary>
        public static (DateTime? NextStartUtc, DateTime? NextEndUtc) NextTransition(DateTime utc, IReadOnlyList<BgBonusSpan> spans, TimeZoneInfo zone)
        {
            utc = AsUtc(utc);

            DateTime? nextStart = null;
            DateTime? nextEnd = null;

            foreach (var (start, end) in Windows(utc, spans, zone))
            {
                if (start > utc && (nextStart == null || start < nextStart))
                    nextStart = start;

                if (end > utc && end != DateTime.MaxValue && (nextEnd == null || end < nextEnd))
                    nextEnd = end;
            }

            return (nextStart, nextEnd);
        }

        /// <summary>
        /// The merged UTC windows around <paramref name="utc"/>: every span instance starting on the local dates from three
        /// days before to three days after. Spans are under a day long, so that always includes the window containing
        /// <paramref name="utc"/> and the next start and end after it.
        /// </summary>
        private static List<(DateTime Start, DateTime End)> Windows(DateTime utc, IReadOnlyList<BgBonusSpan> spans, TimeZoneInfo zone)
        {
            var merged = new List<(DateTime Start, DateTime End)>();

            if (spans == null || spans.Count == 0 || zone == null)
                return merged;

            var localNow = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            var firstDay = localNow.Date.AddDays(-3);
            var raw = new List<(DateTime Start, DateTime End)>();

            for (var day = 0; day <= 6; day++)
            {
                var date = firstDay.AddDays(day);

                foreach (var span in spans)
                {
                    var startLocal = date.Add(span.Start);
                    var endLocal = span.CrossesMidnight ? date.AddDays(1).Add(span.End) : date.Add(span.End);
                    var startUtc = LocalToUtc(startLocal, zone);
                    var endUtc = LocalToUtc(endLocal, zone);

                    if (endUtc > startUtc)
                        raw.Add((startUtc, endUtc));
                }
            }

            raw.Sort((a, b) => a.Start.CompareTo(b.Start));

            foreach (var window in raw)
            {
                if (merged.Count > 0 && window.Start <= merged[^1].End)
                {
                    if (window.End > merged[^1].End)
                        merged[^1] = (merged[^1].Start, window.End);
                }
                else
                {
                    merged.Add(window);
                }
            }

            // Spans that merge into one window covering the whole search horizon never end: that end is an artifact of the horizon,
            // so the window is unbounded (open on both sides) and NextTransition reports no end for it.
            // raw is sorted by Start, so raw[0].Start is the earliest start; the latest END is not necessarily the last window's (a long
            // window can contain later short ones), so take the maximum.
            var latestEnd = DateTime.MinValue;

            foreach (var window in raw)
                if (window.End > latestEnd)
                    latestEnd = window.End;

            if (merged.Count == 1 && merged[0].Start == raw[0].Start && merged[0].End == latestEnd)
                merged[0] = (DateTime.MinValue, DateTime.MaxValue);

            return merged;
        }

        /// <summary>Local wall clock to UTC: a gap time steps forward an hour at a time, an ambiguous time takes its first occurrence (the larger offset).</summary>
        internal static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

            while (zone.IsInvalidTime(local))
                local = local.AddHours(1);

            if (zone.IsAmbiguousTime(local))
            {
                var offset = TimeSpan.MinValue;

                foreach (var candidate in zone.GetAmbiguousTimeOffsets(local))
                    if (candidate > offset)
                        offset = candidate;

                return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
            }

            return TimeZoneInfo.ConvertTimeToUtc(local, zone);
        }

        private static DateTime AsUtc(DateTime utc) => utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }
}
