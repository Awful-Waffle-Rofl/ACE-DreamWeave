using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The pure half of the World Events daily scheduler: schedule parsing and which slot occurrences are due.
    /// No PropertyManager, no clock, no world.
    /// </summary>
    [TestClass]
    public class WorldEventScheduleTests
    {
        private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
        private static readonly TimeZoneInfo FixedMinus8 = TimeZoneInfo.CreateCustomTimeZone("Fallback", TimeSpan.FromHours(-8), "UTC-8", "UTC-8");

        /// <summary>
        /// Resolved lazily, per test: a host without tzdata then fails only the tests that need the zone (Inconclusive), not the
        /// whole class with a TypeInitializationException.
        /// </summary>
        private static TimeZoneInfo Zone(string id)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException || ex is InvalidTimeZoneException)
            {
                Assert.Inconclusive($"time zone '{id}' is not available on this host: {ex.Message}");
                return null;
            }
        }

        private static TimeZoneInfo ResolveKnown(string id)
        {
            if (id == "UTC")
                return Utc;

            return id == "America/Los_Angeles" || id == "Europe/London" ? Zone(id) : FixedMinus8;
        }

        private static List<WorldEventSlot> Slots(string text, string defaultZone = "America/Los_Angeles")
            => WorldEventSchedule.Parse(text, defaultZone, ResolveKnown);

        private static DateTime U(int y, int mo, int d, int h, int mi, int s = 0) => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc);

        // ---- parser ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Parse_SingleSlot_UsesDefaultZone()
        {
            var slots = Slots("17:30");

            Assert.AreEqual(1, slots.Count);
            Assert.AreEqual(17, slots[0].Hour);
            Assert.AreEqual(30, slots[0].Minute);
            Assert.AreEqual("America/Los_Angeles", slots[0].ZoneId);
            Assert.AreSame(Zone("America/Los_Angeles"), slots[0].Zone);
        }

        [TestMethod]
        public void Parse_PerSlotZone_AndWhitespace()
        {
            var slots = Slots("  17:30 ,  19:00   Europe/London , 7:05 ");

            Assert.AreEqual(3, slots.Count);
            Assert.AreEqual("America/Los_Angeles", slots[0].ZoneId);
            Assert.AreEqual("Europe/London", slots[1].ZoneId);
            Assert.AreEqual(19, slots[1].Hour);
            Assert.AreEqual(7, slots[2].Hour);
            Assert.AreEqual(5, slots[2].Minute);
        }

        [TestMethod]
        public void Parse_EmptyAndBlank_NoSlots()
        {
            Assert.AreEqual(0, Slots("").Count);
            Assert.AreEqual(0, Slots("   ").Count);
            Assert.AreEqual(0, Slots(null).Count);
            Assert.AreEqual(0, Slots(" , ,").Count);
        }

        [TestMethod]
        public void Parse_MalformedEntries_SkippedAndReported()
        {
            var malformed = new List<string>();
            var slots = WorldEventSchedule.Parse("25:00, 7:5, 17:30, noon, 12:60, 19:00 Europe/London", "America/Los_Angeles", ResolveKnown, malformed);

            Assert.AreEqual(2, slots.Count);
            CollectionAssert.AreEqual(new[] { "25:00", "7:5", "noon", "12:60" }, malformed);
        }

        [TestMethod]
        public void Parse_UnknownZoneId_UsesTheResolversFallback()
        {
            var slots = Slots("17:30 Not/AZone");

            Assert.AreEqual(1, slots.Count);
            Assert.AreEqual("Not/AZone", slots[0].ZoneId);
            Assert.AreSame(FixedMinus8, slots[0].Zone);
        }

        [TestMethod]
        public void ResolveZoneLive_UnknownId_FallsBackToFixedUtcMinus8()
        {
            var zone = WorldEventScheduler.ResolveZoneLive("Not/AZone_ForSure");

            Assert.AreEqual(TimeSpan.FromHours(-8), zone.BaseUtcOffset);
            Assert.IsFalse(zone.SupportsDaylightSavingTime);
        }

        [TestMethod]
        public void ClampLateMinutes_ClampsToZeroThroughOneTwenty()
        {
            Assert.AreEqual(0, WorldEventSchedule.ClampLateMinutes(-5));
            Assert.AreEqual(15, WorldEventSchedule.ClampLateMinutes(15));
            Assert.AreEqual(120, WorldEventSchedule.ClampLateMinutes(9999));
        }

        // ---- due ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Due_ExactTime_Fires()
        {
            var slots = Slots("10:00", "UTC");

            var due = WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 0), 15);

            Assert.AreEqual(1, due.Count);
            Assert.AreEqual("UTC 10:00 2027-05-05", due[0].Key);
        }

        [TestMethod]
        public void Due_BeforeTime_NotDue()
        {
            Assert.AreEqual(0, WorldEventSchedule.Due(Slots("10:00", "UTC"), U(2027, 5, 5, 9, 59, 59), 15).Count);
        }

        [TestMethod]
        public void Due_InsideLateWindow_Fires_AfterWindow_Not()
        {
            var slots = Slots("10:00", "UTC");

            Assert.AreEqual(1, WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 14, 59), 15).Count);
            Assert.AreEqual(1, WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 15, 30), 15).Count, "the window counts whole minutes");
            Assert.AreEqual(0, WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 16, 0), 15).Count);
        }

        [TestMethod]
        public void Due_ZeroLateWindow_StillFiresDuringTheSlotMinute()
        {
            var slots = Slots("10:00", "UTC");

            Assert.AreEqual(1, WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 0, 40), 0).Count);
            Assert.AreEqual(0, WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 1, 0), 0).Count);
        }

        [TestMethod]
        public void Due_SameOccurrence_HasTheSameKeyAcrossTheWindow()
        {
            var slots = Slots("10:00", "UTC");

            var first = WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 0), 15).Single().Key;
            var later = WorldEventSchedule.Due(slots, U(2027, 5, 5, 10, 9), 15).Single().Key;
            var nextDay = WorldEventSchedule.Due(slots, U(2027, 5, 6, 10, 0), 15).Single().Key;

            Assert.AreEqual(first, later, "one occurrence, one key, so a once-only set can hold it");
            Assert.AreNotEqual(first, nextDay);
        }

        [TestMethod]
        public void Due_WindowCrossingMidnight_StillFiresWithYesterdaysKey()
        {
            var slots = Slots("23:55", "UTC");

            var due = WorldEventSchedule.Due(slots, U(2027, 5, 6, 0, 5), 15);

            Assert.AreEqual(1, due.Count);
            Assert.AreEqual("UTC 23:55 2027-05-05", due[0].Key);
        }

        [TestMethod]
        public void ToUtc_PacificDaylightTimeJuly_IsHalfPastMidnightUtcNextDay()
        {
            var slot = Slots("17:30").Single();

            Assert.AreEqual(U(2027, 7, 16, 0, 30), WorldEventSchedule.ToUtc(slot, new DateTime(2027, 7, 15)));
        }

        [TestMethod]
        public void ToUtc_PacificStandardTimeJanuary_IsHalfPastOneUtcNextDay()
        {
            var slot = Slots("17:30").Single();

            Assert.AreEqual(U(2027, 1, 16, 1, 30), WorldEventSchedule.ToUtc(slot, new DateTime(2027, 1, 15)));
        }

        [TestMethod]
        public void ToUtc_SpringForwardGap_ShiftsForwardByTheGap()
        {
            // 2027-03-14: 02:00 jumps to 03:00 in America/Los_Angeles, so 02:30 never happens. It fires at 03:30 PDT = 10:30 UTC.
            var slot = Slots("02:30").Single();

            Assert.AreEqual(U(2027, 3, 14, 10, 30), WorldEventSchedule.ToUtc(slot, new DateTime(2027, 3, 14)));
        }

        [TestMethod]
        public void Due_SpringForwardGap_FiresOnceAtTheShiftedInstant()
        {
            var slots = Slots("02:30");

            Assert.AreEqual(0, WorldEventSchedule.Due(slots, U(2027, 3, 14, 10, 29), 15).Count);
            Assert.AreEqual(1, WorldEventSchedule.Due(slots, U(2027, 3, 14, 10, 30), 15).Count);
        }

        [TestMethod]
        public void ToUtc_FallBackOverlap_TakesTheFirstOccurrence()
        {
            // 2026-11-01: 02:00 PDT falls back to 01:00 PST, so 01:30 happens twice. First = PDT (UTC-7) = 08:30 UTC.
            var slot = Slots("01:30").Single();

            Assert.AreEqual(U(2026, 11, 1, 8, 30), WorldEventSchedule.ToUtc(slot, new DateTime(2026, 11, 1)));
        }

        [TestMethod]
        public void Due_FallBackOverlap_FiresOncePerOccurrenceKey()
        {
            var slots = Slots("01:30");

            var first = WorldEventSchedule.Due(slots, U(2026, 11, 1, 8, 30), 15);
            Assert.AreEqual(1, first.Count);

            // The second 01:30 (PST) is an hour later; it is outside the window, and were it inside it would carry the same key.
            Assert.AreEqual(0, WorldEventSchedule.Due(slots, U(2026, 11, 1, 9, 30), 15).Count);

            var keys = new HashSet<string>();

            for (var minute = 0; minute < 180; minute++)
                foreach (var occurrence in WorldEventSchedule.Due(slots, U(2026, 11, 1, 8, 0).AddMinutes(minute), 15))
                    keys.Add(occurrence.Key);

            Assert.AreEqual(1, keys.Count);
        }

        [TestMethod]
        public void Next_IsTheEarliestUpcomingOccurrence_AcrossZones()
        {
            var slots = Slots("17:30, 19:00 Europe/London");

            // 2027-07-15 20:00 UTC: Pacific 17:30 PDT (00:30 UTC on the 16th) is later than London 19:00 BST (18:00 UTC, already past),
            // so the next one is the Pacific slot on the 15th local date.
            var next = WorldEventSchedule.Next(slots, U(2027, 7, 15, 20, 0));

            Assert.IsNotNull(next);
            Assert.AreEqual(U(2027, 7, 16, 0, 30), next.StartUtc);
            Assert.AreEqual("America/Los_Angeles 17:30 2027-07-15", next.Key);
        }

        [TestMethod]
        public void Next_NoSlots_IsNull()
        {
            Assert.IsNull(WorldEventSchedule.Next(new List<WorldEventSlot>(), U(2027, 7, 15, 20, 0)));
        }
    }
}
