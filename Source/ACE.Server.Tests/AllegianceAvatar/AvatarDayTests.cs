using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars;
using ACE.Server.ThreadDungeons;

namespace ACE.Server.Tests.Avatars
{
    /// <summary>
    /// The Avatar day (owner ruling 2026-11-25): it turns over at 12:00 AM US Eastern, DST-aware, and a stamp dated in the
    /// future counts as used. Pure: the zone and hour come from the tuning record, so nothing here reads PropertyManager.
    /// Every expected instant is written out in UTC: Eastern midnight is 05:00Z in winter (EST) and 04:00Z in summer (EDT).
    /// </summary>
    [TestClass]
    public class AvatarDayTests
    {
        private static readonly AvatarTuning Eastern = AvatarTuning.Defaults;

        private static DateTime Utc(int y, int mo, int d, int h, int mi = 0, int s = 0) => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc);

        private static uint Unix(DateTime utc) => (uint)new DateTimeOffset(utc).ToUnixTimeSeconds();

        [TestInitialize]
        public void RequireTzData()
        {
            // Control: the runtime must resolve the real zone; the UTC-5 fallback has no DST and would fail the summer cases
            // for a reason that has nothing to do with this code.
            var zone = SurveyArchivistStation.ResolveZone("America/New_York");
            if (!zone.SupportsDaylightSavingTime)
                Assert.Inconclusive("This runtime has no tz data for America/New_York (fell back to fixed UTC-5).");
        }

        [TestMethod]
        public void Winter_DayTurnsOverAt0500Z()
        {
            var stamp = Utc(2026, 11, 25, 5, 0, 0); // 12:00 AM EST on Nov 25

            Assert.IsTrue(AvatarDay.IsToday(stamp, AvatarDay.BuildClock(Eastern, Utc(2026, 11, 26, 4, 59, 59))), "11:59:59 PM EST is still the same day");
            Assert.IsFalse(AvatarDay.IsToday(stamp, AvatarDay.BuildClock(Eastern, Utc(2026, 11, 26, 5, 0, 0))), "12:00 AM EST is the next day");

            // The stamp's own day starts at 05:00Z: one second earlier belongs to the day before.
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 11, 25, 4, 59, 59), AvatarDay.BuildClock(Eastern, Utc(2026, 11, 25, 12, 0, 0))));
        }

        [TestMethod]
        public void Summer_DayTurnsOverAt0400Z()
        {
            var stamp = Utc(2026, 7, 15, 4, 0, 0); // 12:00 AM EDT on Jul 15

            Assert.IsTrue(AvatarDay.IsToday(stamp, AvatarDay.BuildClock(Eastern, Utc(2026, 7, 16, 3, 59, 59))), "11:59:59 PM EDT is still the same day");
            Assert.IsFalse(AvatarDay.IsToday(stamp, AvatarDay.BuildClock(Eastern, Utc(2026, 7, 16, 4, 0, 0))), "12:00 AM EDT is the next day");
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 7, 15, 3, 59, 59), AvatarDay.BuildClock(Eastern, Utc(2026, 7, 15, 12, 0, 0))));
        }

        /// <summary>2026-03-08, spring forward: the day starts at 05:00Z (EST) and ends at 04:00Z (EDT), 23 hours long.</summary>
        [TestMethod]
        public void SpringForward_2026_03_08_IsA23HourDay()
        {
            var noon = AvatarDay.BuildClock(Eastern, Utc(2026, 3, 8, 17, 0, 0));

            Assert.AreEqual(Unix(Utc(2026, 3, 9, 4, 0, 0)), noon.NextReset(), "the next midnight is EDT");
            Assert.IsTrue(AvatarDay.IsToday(Utc(2026, 3, 8, 5, 0, 0), noon), "the day began at the EST midnight");
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 3, 8, 4, 59, 59), noon));

            var lastSecond = AvatarDay.BuildClock(Eastern, Utc(2026, 3, 9, 3, 59, 59));
            Assert.IsTrue(AvatarDay.IsToday(Utc(2026, 3, 8, 5, 0, 0), lastSecond), "03:59:59Z on Mar 9 is still Mar 8 Eastern");
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 3, 8, 5, 0, 0), AvatarDay.BuildClock(Eastern, Utc(2026, 3, 9, 4, 0, 0))));
            Assert.AreEqual("1h 0m", AvatarDay.UntilReset(AvatarDay.BuildClock(Eastern, Utc(2026, 3, 9, 3, 0, 0))));
        }

        /// <summary>2026-11-01, fall back: the day starts at 04:00Z (EDT) and ends at 05:00Z (EST), 25 hours long.</summary>
        [TestMethod]
        public void FallBack_2026_11_01_IsA25HourDay()
        {
            var noon = AvatarDay.BuildClock(Eastern, Utc(2026, 11, 1, 17, 0, 0));

            Assert.AreEqual(Unix(Utc(2026, 11, 2, 5, 0, 0)), noon.NextReset(), "the next midnight is EST");
            Assert.IsTrue(AvatarDay.IsToday(Utc(2026, 11, 1, 4, 0, 0), noon), "the day began at the EDT midnight");
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 11, 1, 3, 59, 59), noon));

            var lastSecond = AvatarDay.BuildClock(Eastern, Utc(2026, 11, 2, 4, 59, 59));
            Assert.IsTrue(AvatarDay.IsToday(Utc(2026, 11, 1, 4, 0, 0), lastSecond), "04:59:59Z on Nov 2 is still Nov 1 Eastern");
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 11, 1, 4, 0, 0), AvatarDay.BuildClock(Eastern, Utc(2026, 11, 2, 5, 0, 0))));
        }

        [TestMethod]
        public void FutureStamp_CountsAsUsed()
        {
            var clock = AvatarDay.BuildClock(Eastern, Utc(2026, 11, 25, 12, 0, 0));

            Assert.IsTrue(AvatarDay.IsToday(Utc(2026, 11, 27, 12, 0, 0), clock), "a stamp two days ahead is used, never free");
            Assert.IsTrue(AvatarDay.IsToday(new DateTime(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc), clock), "past the uint range is still the future");
        }

        [TestMethod]
        public void NoStamp_OrAnAncientOne_IsNotToday()
        {
            var clock = AvatarDay.BuildClock(Eastern, Utc(2026, 11, 25, 12, 0, 0));

            Assert.IsFalse(AvatarDay.IsToday(null, clock));
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 11, 24, 12, 0, 0), clock));
            Assert.IsFalse(AvatarDay.IsToday(new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc), clock));
        }

        /// <summary>The countdown is the survey's renderer over NextReset - now (rounded up to the minute).</summary>
        [TestMethod]
        public void UntilReset_IsTheTimeToTheNextEasternMidnight()
        {
            Assert.AreEqual("17h 0m", AvatarDay.UntilReset(AvatarDay.BuildClock(Eastern, Utc(2027, 1, 15, 12, 0, 0))));
            Assert.AreEqual("5h 12m", AvatarDay.UntilReset(AvatarDay.BuildClock(Eastern, Utc(2026, 11, 25, 23, 48, 0))));
            Assert.AreEqual("1m", AvatarDay.UntilReset(AvatarDay.BuildClock(Eastern, Utc(2026, 11, 26, 4, 59, 59))));
        }

        [TestMethod]
        public void ResetHour_IsHonouredAndClamped()
        {
            // 6 AM EST = 11:00Z.
            var six = Eastern with { DayResetHour = 6 };
            var stamp = Utc(2026, 11, 25, 11, 0, 0);
            Assert.IsTrue(AvatarDay.IsToday(stamp, AvatarDay.BuildClock(six, Utc(2026, 11, 26, 10, 59, 59))));
            Assert.IsFalse(AvatarDay.IsToday(stamp, AvatarDay.BuildClock(six, Utc(2026, 11, 26, 11, 0, 0))));

            Assert.AreEqual(23, AvatarDay.BuildClock(Eastern with { DayResetHour = 99 }, Utc(2026, 11, 25, 12, 0, 0)).ResetHour);
            Assert.AreEqual(0, AvatarDay.BuildClock(Eastern with { DayResetHour = -4 }, Utc(2026, 11, 25, 12, 0, 0)).ResetHour);
        }

        /// <summary>The zone comes from the tuning: UTC moves the boundary to 00:00Z, an unknown id falls back to fixed UTC-5 (no DST).</summary>
        [TestMethod]
        public void Timezone_IsReadFromTheTuning_WithTheSurveyFallback()
        {
            var utc = Eastern with { DayResetTimezone = "UTC" };
            Assert.IsFalse(AvatarDay.IsToday(Utc(2026, 11, 25, 23, 0, 0), AvatarDay.BuildClock(utc, Utc(2026, 11, 26, 0, 0, 0))), "UTC midnight");

            var unknown = Eastern with { DayResetTimezone = "Not/AZone" };
            var summer = Utc(2026, 7, 15, 5, 0, 0); // fixed UTC-5: midnight is 05:00Z even in July
            Assert.IsTrue(AvatarDay.IsToday(summer, AvatarDay.BuildClock(unknown, Utc(2026, 7, 16, 4, 59, 59))));
            Assert.IsFalse(AvatarDay.IsToday(summer, AvatarDay.BuildClock(unknown, Utc(2026, 7, 16, 5, 0, 0))));
        }
    }
}
