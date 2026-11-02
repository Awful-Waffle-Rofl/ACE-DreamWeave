using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The pure Double Marks schedule arithmetic (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"): parsing, the half-open
    /// window test, and DST-correct transitions in America/Los_Angeles. No PropertyManager value is read.
    /// </summary>
    [TestClass]
    public class BgBonusWindowTests
    {
        private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

        private const string Shipped = "06:00-07:00,18:00-19:00";

        private static DateTime Utc(int y, int mo, int d, int h, int mi = 0, int s = 0) => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc);

        private static IReadOnlyList<BgBonusSpan> Spans(string spec) => BgBonusWindow.Parse(spec);

        // ---------------- parsing ----------------

        [TestMethod]
        public void Parse_ShippedSpec_GivesTwoSpans()
        {
            var spans = Spans(Shipped);

            Assert.AreEqual(2, spans.Count);
            Assert.AreEqual(new BgBonusSpan(TimeSpan.FromHours(6), TimeSpan.FromHours(7)), spans[0]);
            Assert.AreEqual(new BgBonusSpan(TimeSpan.FromHours(18), TimeSpan.FromHours(19)), spans[1]);
            Assert.IsFalse(spans[0].CrossesMidnight);
        }

        [TestMethod]
        public void Parse_ToleratesWhitespace_AndSingleDigitHours()
        {
            var spans = Spans("  6:00 - 7:30 ,\t18:15-19:00 , ");

            Assert.AreEqual(2, spans.Count);
            Assert.AreEqual(new BgBonusSpan(new TimeSpan(6, 0, 0), new TimeSpan(7, 30, 0)), spans[0]);
            Assert.AreEqual(new BgBonusSpan(new TimeSpan(18, 15, 0), new TimeSpan(19, 0, 0)), spans[1]);
        }

        [TestMethod]
        public void Parse_SkipsMalformedEntries_AndReportsThem()
        {
            var bad = new List<string>();
            var spans = BgBonusWindow.Parse("06:00-07:00, nonsense, 25:00-26:00, 06:61-07:00, 6-7, 08:00-09:00-10:00, 18:00-19:00, 07:00-", bad);

            Assert.AreEqual(2, spans.Count, "only the two well-formed entries survive");
            CollectionAssert.AreEqual(new[] { "nonsense", "25:00-26:00", "06:61-07:00", "6-7", "08:00-09:00-10:00", "07:00-" }, bad);
        }

        [TestMethod]
        public void Parse_EmptyNullAndBlank_AreNoWindows()
        {
            Assert.AreEqual(0, Spans("").Count);
            Assert.AreEqual(0, Spans(null).Count);
            Assert.AreEqual(0, Spans("   ,  ,").Count);
            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 5, 13, 30), Spans(""), Pacific), "no windows is never active");

            var none = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 13, 30), Spans(""), Pacific);
            Assert.IsNull(none.NextStartUtc);
            Assert.IsNull(none.NextEndUtc);
        }

        // ---------------- IsActive ----------------

        /// <summary>2026-10-05 is PDT (UTC-7): 06:00 local is 13:00Z, 18:00 local is 01:00Z next day. Start inclusive, end exclusive.</summary>
        [TestMethod]
        public void IsActive_BothEdges_OfEachWindow()
        {
            var spans = Spans(Shipped);

            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 5, 12, 59, 59), spans, Pacific), "one second before the morning window");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 5, 13, 0, 0), spans, Pacific), "the start is inclusive");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 5, 13, 59, 59), spans, Pacific), "the last second");
            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 5, 14, 0, 0), spans, Pacific), "the end is exclusive");

            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 6, 0, 59, 59), spans, Pacific), "one second before the evening window");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 6, 1, 0, 0), spans, Pacific));
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 6, 1, 59, 59), spans, Pacific));
            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 6, 2, 0, 0), spans, Pacific));

            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 5, 20, 0, 0), spans, Pacific), "1 PM local is between the windows");
        }

        [TestMethod]
        public void IsActive_UnspecifiedKind_IsReadAsUtc()
        {
            var spans = Spans(Shipped);

            Assert.IsTrue(BgBonusWindow.IsActive(new DateTime(2026, 10, 5, 13, 30, 0, DateTimeKind.Unspecified), spans, Pacific));
        }

        [TestMethod]
        public void IsActive_WindowCrossingMidnight()
        {
            var spans = Spans("22:00-02:00");

            Assert.IsTrue(spans[0].CrossesMidnight);

            // 2026-10-05 PDT: 22:00 local is 05:00Z on the 6th; 02:00 local is 09:00Z on the 6th.
            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 6, 4, 59, 59), spans, Pacific));
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 6, 5, 0, 0), spans, Pacific), "opens at 22:00");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 6, 7, 0, 0), spans, Pacific), "midnight local is inside");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 6, 8, 59, 59), spans, Pacific));
            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 10, 6, 9, 0, 0), spans, Pacific), "closes at 02:00");
        }

        [TestMethod]
        public void Parse_StartEqualToEnd_IsMalformed_AndReported()
        {
            var bad = new List<string>();
            var spans = BgBonusWindow.Parse("00:00-00:00, 06:00-06:00, 18:00-19:00", bad);

            Assert.AreEqual(1, spans.Count, "only the real window survives");
            CollectionAssert.AreEqual(new[] { "00:00-00:00", "06:00-06:00" }, bad);
            Assert.AreEqual(0, Spans("00:00-00:00").Count);
        }

        /// <summary>Spans that merge around the clock never end: the end the search horizon would report is not real, so there is none.</summary>
        [TestMethod]
        public void AlwaysOnMergedSpans_AreActive_WithNoNextEnd()
        {
            // The last two are nested: a long window contains later short ones, so the latest END is not the last window's.
            foreach (var spec in new[] { "00:00-12:00,12:00-00:00", "00:00-23:00,22:00-01:00,23:00-00:00", "00:00-23:30,23:00-01:00,23:30-00:00" })
            {
                var spans = Spans(spec);

                Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 5, 13, 0), spans, Pacific), spec);
                Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 10, 9, 3, 0), spans, Pacific), spec);

                var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 13, 0), spans, Pacific);
                Assert.IsNull(start, spec);
                Assert.IsNull(end, "no real end exists: " + spec);
            }
        }

        [TestMethod]
        public void OverlappingAndTouchingSpans_MergeIntoOneWindow()
        {
            var spans = Spans("06:00-07:00,07:00-08:00,07:30-08:30");

            // 06:00 PDT = 13:00Z; the merged window ends at 08:30 local = 15:30Z, with no seam at 07:00 or 08:00.
            var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 12, 0), spans, Pacific);

            Assert.AreEqual(Utc(2026, 10, 5, 13, 0), start);
            Assert.AreEqual(Utc(2026, 10, 5, 15, 30), end);

            var (_, endInside) = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 14, 30), spans, Pacific);
            Assert.AreEqual(Utc(2026, 10, 5, 15, 30), endInside, "the first end after 07:30 local is the merged one");
        }

        // ---------------- NextTransition ----------------

        [TestMethod]
        public void NextTransition_BeforeAWindow_AndInsideIt()
        {
            var spans = Spans(Shipped);

            var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 12, 0), spans, Pacific);
            Assert.AreEqual(Utc(2026, 10, 5, 13, 0), start, "next open is 06:00 local");
            Assert.AreEqual(Utc(2026, 10, 5, 14, 0), end, "next close is 07:00 local");

            var (startIn, endIn) = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 13, 30), spans, Pacific);
            Assert.AreEqual(Utc(2026, 10, 6, 1, 0), startIn, "inside the morning window the next open is the evening one");
            Assert.AreEqual(Utc(2026, 10, 5, 14, 0), endIn);
        }

        [TestMethod]
        public void NextTransition_IsStrictlyAfter_TheInstant()
        {
            var spans = Spans(Shipped);

            var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 13, 0), spans, Pacific);

            Assert.AreEqual(Utc(2026, 10, 6, 1, 0), start, "at exactly the open instant, that open is not 'next'");
            Assert.AreEqual(Utc(2026, 10, 5, 14, 0), end);

            var (_, endAtClose) = BgBonusWindow.NextTransition(Utc(2026, 10, 5, 14, 0), spans, Pacific);
            Assert.AreEqual(Utc(2026, 10, 6, 2, 0), endAtClose, "at exactly the close instant, that close is not 'next'");
        }

        [TestMethod]
        public void NextTransition_AcrossADayBoundary()
        {
            var spans = Spans(Shipped);

            // 2026-10-05 20:00 local (03:00Z on the 6th): both next transitions are tomorrow morning.
            var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 10, 6, 3, 0), spans, Pacific);

            Assert.AreEqual(Utc(2026, 10, 6, 13, 0), start, "tomorrow 06:00 local");
            Assert.AreEqual(Utc(2026, 10, 6, 14, 0), end, "tomorrow 07:00 local");

            // Just after the evening window closes on the 6th (19:00 local = 02:00Z on the 7th).
            var (nextStart, _) = BgBonusWindow.NextTransition(Utc(2026, 10, 7, 2, 0), spans, Pacific);
            Assert.AreEqual(Utc(2026, 10, 7, 13, 0), nextStart);
        }

        [TestMethod]
        public void NextTransition_MidnightCrossingWindow_EndsOnTheNextLocalDay()
        {
            var spans = Spans("22:00-02:00");

            // 2026-10-05 21:00 local = 04:00Z on the 6th.
            var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 10, 6, 4, 0), spans, Pacific);

            Assert.AreEqual(Utc(2026, 10, 6, 5, 0), start);
            Assert.AreEqual(Utc(2026, 10, 6, 9, 0), end);
        }

        // ---------------- DST ----------------

        /// <summary>Spring forward 2026-03-08 (02:00 PST -> 03:00 PDT): the local windows keep their wall-clock hours, so their UTC instants move one hour earlier.</summary>
        [TestMethod]
        public void Dst_SpringForward_WindowsLandAtTheRightUtcInstants()
        {
            var spans = Spans(Shipped);

            // Saturday the 7th, still PST (UTC-8): 06:00 = 14:00Z, 18:00 = 02:00Z on the 8th.
            Assert.AreEqual(Utc(2026, 3, 7, 14, 0), BgBonusWindow.NextTransition(Utc(2026, 3, 7, 12, 0), spans, Pacific).NextStartUtc);
            Assert.AreEqual(Utc(2026, 3, 8, 2, 0), BgBonusWindow.NextTransition(Utc(2026, 3, 7, 15, 0), spans, Pacific).NextStartUtc);

            // The 8th, PDT (UTC-7) from 03:00 local: 06:00 = 13:00Z, 18:00 = 01:00Z on the 9th.
            Assert.AreEqual(Utc(2026, 3, 8, 13, 0), BgBonusWindow.NextTransition(Utc(2026, 3, 8, 3, 0), spans, Pacific).NextStartUtc, "the 6 AM window on the day of the change");
            Assert.AreEqual(Utc(2026, 3, 8, 14, 0), BgBonusWindow.NextTransition(Utc(2026, 3, 8, 13, 30), spans, Pacific).NextEndUtc);
            Assert.AreEqual(Utc(2026, 3, 9, 1, 0), BgBonusWindow.NextTransition(Utc(2026, 3, 8, 14, 0), spans, Pacific).NextStartUtc, "the 6 PM window on the day of the change");
            Assert.AreEqual(Utc(2026, 3, 9, 2, 0), BgBonusWindow.NextTransition(Utc(2026, 3, 9, 1, 30), spans, Pacific).NextEndUtc);

            // The 9th: 06:00 PDT = 13:00Z.
            Assert.AreEqual(Utc(2026, 3, 9, 13, 0), BgBonusWindow.NextTransition(Utc(2026, 3, 9, 2, 0), spans, Pacific).NextStartUtc);

            // The membership tests agree with the instants on both sides of the change.
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 3, 7, 14, 0), spans, Pacific), "6 AM PST");
            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 3, 8, 12, 59), spans, Pacific), "5:59 AM PDT");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 3, 8, 13, 0), spans, Pacific), "6 AM PDT");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 3, 9, 1, 0), spans, Pacific), "6 PM PDT on the 8th");
        }

        /// <summary>Fall back 2026-11-01 (02:00 PDT -> 01:00 PST): UTC instants move one hour later after the change.</summary>
        [TestMethod]
        public void Dst_FallBack_WindowsLandAtTheRightUtcInstants()
        {
            var spans = Spans(Shipped);

            // Saturday Oct 31, still PDT (UTC-7): 06:00 = 13:00Z, 18:00 = 01:00Z on Nov 1.
            Assert.AreEqual(Utc(2026, 10, 31, 13, 0), BgBonusWindow.NextTransition(Utc(2026, 10, 31, 12, 0), spans, Pacific).NextStartUtc);
            Assert.AreEqual(Utc(2026, 11, 1, 1, 0), BgBonusWindow.NextTransition(Utc(2026, 10, 31, 15, 0), spans, Pacific).NextStartUtc);

            // Nov 1, PST (UTC-8) after the change: 06:00 = 14:00Z, 18:00 = 02:00Z on Nov 2.
            Assert.AreEqual(Utc(2026, 11, 1, 14, 0), BgBonusWindow.NextTransition(Utc(2026, 11, 1, 3, 0), spans, Pacific).NextStartUtc, "the 6 AM window on the day of the change");
            Assert.AreEqual(Utc(2026, 11, 1, 15, 0), BgBonusWindow.NextTransition(Utc(2026, 11, 1, 14, 30), spans, Pacific).NextEndUtc);
            Assert.AreEqual(Utc(2026, 11, 2, 2, 0), BgBonusWindow.NextTransition(Utc(2026, 11, 1, 15, 0), spans, Pacific).NextStartUtc, "the 6 PM window on the day of the change");
            Assert.AreEqual(Utc(2026, 11, 2, 3, 0), BgBonusWindow.NextTransition(Utc(2026, 11, 2, 2, 30), spans, Pacific).NextEndUtc);

            // Nov 2: 06:00 PST = 14:00Z.
            Assert.AreEqual(Utc(2026, 11, 2, 14, 0), BgBonusWindow.NextTransition(Utc(2026, 11, 2, 3, 0), spans, Pacific).NextStartUtc);

            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 11, 1, 13, 30), spans, Pacific), "13:30Z is 5:30 AM PST, not yet");
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 11, 1, 14, 0), spans, Pacific), "6 AM PST");
        }

        /// <summary>A local start inside the spring-forward gap steps forward an hour at a time, like SurveyDay.NextReset.</summary>
        [TestMethod]
        public void Dst_StartInTheSpringForwardGap_StepsForward()
        {
            var spans = Spans("02:30-04:00");

            // 2026-03-08: 02:30 does not exist; it steps to 03:30 PDT = 10:30Z. The end, 04:00 PDT, is 11:00Z.
            var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 3, 8, 9, 0), spans, Pacific);

            Assert.AreEqual(Utc(2026, 3, 8, 10, 30), start);
            Assert.AreEqual(Utc(2026, 3, 8, 11, 0), end);
            Assert.IsTrue(BgBonusWindow.IsActive(Utc(2026, 3, 8, 10, 45), spans, Pacific));
            Assert.IsFalse(BgBonusWindow.IsActive(Utc(2026, 3, 8, 11, 0), spans, Pacific));
        }

        /// <summary>A local time that happens twice on the fall-back day resolves to its FIRST occurrence (the daylight one).</summary>
        [TestMethod]
        public void Dst_AmbiguousFallBackTime_ResolvesToTheFirstOccurrence()
        {
            var spans = Spans("01:30-03:00");

            // 2026-11-01: 01:30 happens at 08:30Z (PDT) and again at 09:30Z (PST). The first is taken. 03:00 PST is 11:00Z.
            var (start, end) = BgBonusWindow.NextTransition(Utc(2026, 11, 1, 7, 0), spans, Pacific);

            Assert.AreEqual(Utc(2026, 11, 1, 8, 30), start);
            Assert.AreEqual(Utc(2026, 11, 1, 11, 0), end);
        }

        [TestMethod]
        public void LocalToUtc_OrdinaryGapAndAmbiguousTimes()
        {
            Assert.AreEqual(Utc(2026, 10, 5, 13, 0), BgBonusWindow.LocalToUtc(new DateTime(2026, 10, 5, 6, 0, 0), Pacific));
            Assert.AreEqual(Utc(2026, 3, 8, 10, 0), BgBonusWindow.LocalToUtc(new DateTime(2026, 3, 8, 2, 0, 0), Pacific), "02:00 does not exist on 2026-03-08; it steps to 03:00 PDT");
            Assert.AreEqual(Utc(2026, 11, 1, 8, 15), BgBonusWindow.LocalToUtc(new DateTime(2026, 11, 1, 1, 15, 0), Pacific), "ambiguous: the first occurrence");
        }

        [TestMethod]
        public void FixedOffsetZone_HasNoDst()
        {
            var utcMinus8 = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(-8), "t", "t");
            var spans = Spans(Shipped);

            Assert.AreEqual(Utc(2026, 10, 5, 14, 0), BgBonusWindow.NextTransition(Utc(2026, 10, 5, 12, 0), spans, utcMinus8).NextStartUtc, "06:00 at UTC-8 is 14:00Z in October too");
        }
    }
}
