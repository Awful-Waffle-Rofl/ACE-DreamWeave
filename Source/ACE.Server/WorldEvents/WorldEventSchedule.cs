using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ACE.Server.WorldEvents
{
    /// <summary>One daily slot: a local wall-clock time in a zone.</summary>
    public sealed class WorldEventSlot
    {
        public WorldEventSlot(int hour, int minute, string zoneId, TimeZoneInfo zone)
        {
            Hour = hour;
            Minute = minute;
            ZoneId = zoneId;
            Zone = zone;
        }

        public int Hour { get; }
        public int Minute { get; }

        /// <summary>The zone id as written (or the default zone's id); used in the occurrence key.</summary>
        public string ZoneId { get; }

        public TimeZoneInfo Zone { get; }

        public string TimeText => $"{Hour:00}:{Minute:00}";
    }

    /// <summary>One slot on one local calendar date, resolved to the UTC instant its teaser goes out.</summary>
    public sealed class WorldEventOccurrence
    {
        public WorldEventOccurrence(WorldEventSlot slot, DateTime localDate, DateTime startUtc)
        {
            Slot = slot;
            LocalDate = localDate;
            StartUtc = startUtc;
        }

        public WorldEventSlot Slot { get; }

        /// <summary>The local calendar date (time of day zero) the slot was written for.</summary>
        public DateTime LocalDate { get; }

        /// <summary>UTC instant of the slot. A nonexistent local time is shifted forward by the gap; an ambiguous one takes the first occurrence.</summary>
        public DateTime StartUtc { get; }

        /// <summary>"&lt;zone&gt; &lt;HH:mm&gt; &lt;yyyy-MM-dd local&gt;".</summary>
        public string Key => $"{Slot.ZoneId} {Slot.TimeText} {LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

        public DateTime LateUntilUtc(int lateMinutes) => StartUtc.AddMinutes(lateMinutes);

        /// <summary>This occurrence's start in the slot's own zone, for display.</summary>
        public DateTime StartLocal => TimeZoneInfo.ConvertTimeFromUtc(StartUtc, Slot.Zone);
    }

    /// <summary>
    /// The pure half of the World Events daily scheduler: parses the schedule text and answers which slot
    /// occurrences are due. No PropertyManager, no clock, no world - every input is an argument.
    /// </summary>
    public static class WorldEventSchedule
    {
        public const int MaxLateMinutes = 120;

        private static readonly Regex TimePattern = new Regex(@"^([01]?[0-9]|2[0-3]):([0-5][0-9])$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static int ClampLateMinutes(long raw) => (int)Math.Max(0, Math.Min(MaxLateMinutes, raw));

        /// <summary>
        /// Parses comma separated "HH:mm" or "HH:mm zone-id" entries. <paramref name="resolveZone"/> maps a zone id to a zone and
        /// never returns null (the live resolver falls back to a fixed offset); an entry naming no zone uses
        /// <paramref name="defaultZoneId"/>. A malformed entry is skipped and its text added to <paramref name="malformed"/>.
        /// </summary>
        public static List<WorldEventSlot> Parse(string spec, string defaultZoneId, Func<string, TimeZoneInfo> resolveZone, List<string> malformed = null)
        {
            var slots = new List<WorldEventSlot>();

            if (string.IsNullOrWhiteSpace(spec))
                return slots;

            foreach (var rawEntry in spec.Split(','))
            {
                var entry = rawEntry.Trim();

                if (entry.Length == 0)
                    continue;

                var firstSpace = entry.IndexOfAny(new[] { ' ', '\t' });
                var timeText = firstSpace < 0 ? entry : entry.Substring(0, firstSpace);
                var zoneText = firstSpace < 0 ? null : entry.Substring(firstSpace + 1).Trim();

                var match = TimePattern.Match(timeText);

                if (!match.Success)
                {
                    malformed?.Add(entry);
                    continue;
                }

                var zoneId = string.IsNullOrEmpty(zoneText) ? (defaultZoneId ?? string.Empty) : zoneText;

                slots.Add(new WorldEventSlot(
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    zoneId,
                    resolveZone(zoneId)));
            }

            return slots;
        }

        /// <summary>The UTC instant of <paramref name="slot"/> on the local calendar date <paramref name="localDate"/>.</summary>
        public static DateTime ToUtc(WorldEventSlot slot, DateTime localDate)
        {
            var local = DateTime.SpecifyKind(localDate.Date.AddHours(slot.Hour).AddMinutes(slot.Minute), DateTimeKind.Unspecified);
            var zone = slot.Zone;

            TimeSpan offset;

            if (zone.IsInvalidTime(local))
            {
                // Spring-forward gap: the clock never reads this time. Shift forward by the gap, which is the same
                // instant as reading the wall time under the offset in force before the jump.
                offset = zone.GetUtcOffset(local.AddHours(-3));
            }
            else if (zone.IsAmbiguousTime(local))
            {
                // Fall-back overlap: the wall time happens twice. The first occurrence is under the larger (daylight) offset.
                offset = zone.GetAmbiguousTimeOffsets(local).Max();
            }
            else
            {
                offset = zone.GetUtcOffset(local);
            }

            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        public static WorldEventOccurrence Occurrence(WorldEventSlot slot, DateTime localDate)
            => new WorldEventOccurrence(slot, localDate.Date, ToUtc(slot, localDate));

        /// <summary>
        /// Every occurrence whose window contains <paramref name="nowUtc"/>: start &lt;= now &lt;= start + late. The comparison runs on
        /// <paramref name="nowUtc"/> truncated to the whole minute, so a late window of 0 still means "during the slot's minute" and a
        /// one second tick cannot step over it. Yesterday's and today's local date are both considered, so a late window that crosses
        /// local midnight still fires.
        /// </summary>
        public static List<WorldEventOccurrence> Due(IEnumerable<WorldEventSlot> slots, DateTime nowUtc, int lateMinutes)
        {
            var due = new List<WorldEventOccurrence>();
            var now = TruncateToMinute(nowUtc);

            foreach (var slot in slots)
            {
                var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, slot.Zone).Date;

                foreach (var date in new[] { localToday.AddDays(-1), localToday })
                {
                    var occurrence = Occurrence(slot, date);

                    if (occurrence.StartUtc <= now && now <= occurrence.LateUntilUtc(lateMinutes))
                        due.Add(occurrence);
                }
            }

            return due.OrderBy(o => o.StartUtc).ToList();
        }

        /// <summary>The earliest occurrence starting strictly after <paramref name="nowUtc"/>, or null with no slots.</summary>
        public static WorldEventOccurrence Next(IEnumerable<WorldEventSlot> slots, DateTime nowUtc)
        {
            WorldEventOccurrence best = null;

            foreach (var slot in slots)
            {
                var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, slot.Zone).Date;

                foreach (var date in new[] { localToday.AddDays(-1), localToday, localToday.AddDays(1) })
                {
                    var occurrence = Occurrence(slot, date);

                    if (occurrence.StartUtc > nowUtc && (best == null || occurrence.StartUtc < best.StartUtc))
                        best = occurrence;
                }
            }

            return best;
        }

        private static DateTime TruncateToMinute(DateTime utc)
            => new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }
}
