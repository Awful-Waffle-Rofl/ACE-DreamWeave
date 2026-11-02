using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// The Double Marks Arena Crier lines (Docs/Pvp/BATTLEGROUNDS.md "Double Marks windows"): edge-triggered, exactly once per
    /// transition, driven by a fake clock. 2026-10-05 is PDT (UTC-7), so 06:00 local is 13:00Z.
    /// </summary>
    [TestClass]
    public class BgBonusCrierTests
    {
        private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        private static readonly IReadOnlyList<BgBonusSpan> Shipped = BgBonusWindow.Parse("06:00-07:00,18:00-19:00");

        /// <summary>A local Pacific time on 2026-10-05 as UTC.</summary>
        private static DateTime Local(int h, int m, int s = 0) => new DateTime(2026, 10, 5, h, m, s, DateTimeKind.Utc).AddHours(7);

        private static List<(DateTime At, string Line)> Run(BgBonusCrier crier, DateTime from, DateTime to, TimeSpan step, bool enabled = true, int warn = 15, IReadOnlyList<BgBonusSpan> spans = null)
        {
            var seen = new List<(DateTime, string)>();

            for (var t = from; t <= to; t += step)
                foreach (var line in crier.Tick(t, enabled, spans ?? Shipped, Pacific, warn))
                    seen.Add((t, line));

            return seen;
        }

        private static string Warn(int minutes) => BgBonusCrier.WarnLine(minutes);

        [TestMethod]
        public void Lines_AreTheExactOwnerText()
        {
            Assert.AreEqual("Battleground Double Marks begins in 15 minutes! Every battleground match that starts during the window pays double Marks of the Hopeslayer.", BgBonusCrier.WarnLine(15));
            Assert.AreEqual("Battleground Double Marks is now live for the next 1h 0m! Queue for battlegrounds now. Use /arena join bg.", BgBonusCrier.OpenLine("1h 0m"));
            Assert.AreEqual("Battleground Double Marks has ended. The next window opens in 11h 0m.", BgBonusCrier.EndLine("11h 0m"));
            Assert.AreEqual("Battleground Double Marks has ended.", BgBonusCrier.EndLine(null));
            Assert.AreEqual("Battleground Double Marks begins in 1 minute! Every battleground match that starts during the window pays double Marks of the Hopeslayer.", BgBonusCrier.WarnLine(1));
        }

        [TestMethod]
        public void EveryLine_FitsAChatLine()
        {
            foreach (var line in new[] { BgBonusCrier.WarnLine(180), BgBonusCrier.OpenLine("23h 59m"), BgBonusCrier.EndLine("23h 59m") })
            {
                Assert.IsTrue(line.Length < 250, line);
                Assert.IsTrue(line.All(c => c < 128), "ASCII only: " + line);
                Assert.IsFalse(line.Contains((char)0x2013) || line.Contains((char)0x2014), "no en or em dash");
            }
        }

        /// <summary>Every 30 s through 05:40..07:05 local: the heads-up at 05:45, the open at 06:00, the end at 07:00, each exactly once.</summary>
        [TestMethod]
        public void MorningWindow_HeadsUpOpenEnd_EachFireExactlyOnce_At30SecondSteps()
        {
            var seen = Run(new BgBonusCrier(), Local(5, 40), Local(7, 5), TimeSpan.FromSeconds(30));

            Assert.AreEqual(3, seen.Count, string.Join(" | ", seen.Select(s => s.Line)));
            Assert.AreEqual((Local(5, 45), Warn(15)), seen[0]);
            Assert.AreEqual((Local(6, 0), BgBonusCrier.OpenLine("1h 0m")), seen[1]);
            Assert.AreEqual((Local(7, 0), BgBonusCrier.EndLine("11h 0m")), seen[2]);
        }

        [TestMethod]
        public void MorningWindow_EachLineOnceAtOneSecondSteps()
        {
            var seen = Run(new BgBonusCrier(), Local(5, 40), Local(7, 5), TimeSpan.FromSeconds(1));

            Assert.AreEqual(3, seen.Count);
            Assert.AreEqual(1, seen.Count(s => s.Line == Warn(15)));
            Assert.AreEqual(1, seen.Count(s => s.Line == BgBonusCrier.OpenLine("1h 0m")));
            Assert.AreEqual(1, seen.Count(s => s.Line == BgBonusCrier.EndLine("11h 0m")));
        }

        [TestMethod]
        public void EveningWindow_FiresAndNamesTheNextMorning()
        {
            var seen = Run(new BgBonusCrier(), Local(17, 40), Local(19, 5), TimeSpan.FromSeconds(30));

            Assert.AreEqual(3, seen.Count);
            Assert.AreEqual((Local(17, 45), Warn(15)), seen[0]);
            Assert.AreEqual((Local(18, 0), BgBonusCrier.OpenLine("1h 0m")), seen[1]);
            Assert.AreEqual((Local(19, 0), BgBonusCrier.EndLine("11h 0m")), seen[2], "19:00 to 06:00 tomorrow is 11 hours");
        }

        [TestMethod]
        public void AWholeDay_FiresSixLines_InOrder()
        {
            var seen = Run(new BgBonusCrier(), Local(4, 0), Local(4, 0).AddHours(24), TimeSpan.FromSeconds(30));

            Assert.AreEqual(6, seen.Count, string.Join(" | ", seen.Select(s => s.Line)));
            CollectionAssert.AreEqual(
                new[] { Warn(15), BgBonusCrier.OpenLine("1h 0m"), BgBonusCrier.EndLine("11h 0m"), Warn(15), BgBonusCrier.OpenLine("1h 0m"), BgBonusCrier.EndLine("11h 0m") },
                seen.Select(s => s.Line).ToArray());
        }

        [TestMethod]
        public void WarnZero_SuppressesTheHeadsUp_OnlyOpenAndEnd()
        {
            var seen = Run(new BgBonusCrier(), Local(5, 0), Local(7, 5), TimeSpan.FromSeconds(30), warn: 0);

            Assert.AreEqual(2, seen.Count);
            Assert.AreEqual(BgBonusCrier.OpenLine("1h 0m"), seen[0].Line);
            Assert.AreEqual(BgBonusCrier.EndLine("11h 0m"), seen[1].Line);
        }

        [TestMethod]
        public void WarnLeadIsClamped_To180Minutes()
        {
            var seen = Run(new BgBonusCrier(), Local(2, 0), Local(5, 59), TimeSpan.FromMinutes(1), warn: 100000);

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual((Local(3, 0), Warn(180)), seen[0], "a huge lead reads as 180 minutes");
        }

        [TestMethod]
        public void BootInsideAWindow_GivesExactlyOneOpen_WithTheRemainingTime()
        {
            var seen = Run(new BgBonusCrier(), Local(6, 20), Local(6, 50), TimeSpan.FromSeconds(30));

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual((Local(6, 20), BgBonusCrier.OpenLine("40m")), seen[0]);
        }

        [TestMethod]
        public void BootInsideTheHeadsUpPeriod_GivesExactlyOneHeadsUp_RoundedUp()
        {
            var seen = Run(new BgBonusCrier(), Local(5, 52, 30), Local(5, 59), TimeSpan.FromSeconds(30));

            Assert.AreEqual(1, seen.Count);
            Assert.AreEqual(Warn(8), seen[0].Line, "7m30s to go reads as 8 minutes");
        }

        [TestMethod]
        public void BootOutsideAnyWindow_SaysNothing()
        {
            var seen = Run(new BgBonusCrier(), Local(10, 0), Local(12, 0), TimeSpan.FromSeconds(30));

            Assert.AreEqual(0, seen.Count);
        }

        [TestMethod]
        public void BootRightAtTheEnd_SaysNothing_NotAnEndLine()
        {
            var seen = Run(new BgBonusCrier(), Local(7, 0), Local(7, 5), TimeSpan.FromSeconds(30));

            Assert.AreEqual(0, seen.Count, "no window was open on the first evaluation, so there is nothing to end");
        }

        [TestMethod]
        public void Disabled_GivesNothing_AtAnyTime()
        {
            var seen = Run(new BgBonusCrier(), Local(5, 0), Local(8, 0), TimeSpan.FromSeconds(30), enabled: false);

            Assert.AreEqual(0, seen.Count);
        }

        [TestMethod]
        public void NoWindows_GivesNothing()
        {
            var seen = Run(new BgBonusCrier(), Local(5, 0), Local(8, 0), TimeSpan.FromSeconds(30), spans: BgBonusWindow.Parse(""));

            Assert.AreEqual(0, seen.Count);
        }

        [TestMethod]
        public void ReEnabledMidWindow_AnnouncesTheOpenOnce_AndNoEndForTheDisabledGap()
        {
            var crier = new BgBonusCrier();
            var seen = new List<string>();

            seen.AddRange(crier.Tick(Local(6, 10), true, Shipped, Pacific, 15));
            seen.AddRange(crier.Tick(Local(6, 20), false, Shipped, Pacific, 15));
            seen.AddRange(crier.Tick(Local(6, 30), false, Shipped, Pacific, 15));
            seen.AddRange(crier.Tick(Local(6, 40), true, Shipped, Pacific, 15));
            seen.AddRange(crier.Tick(Local(6, 41), true, Shipped, Pacific, 15));

            CollectionAssert.AreEqual(new[] { BgBonusCrier.OpenLine("50m"), BgBonusCrier.OpenLine("20m") }, seen,
                "the enable tick is a first evaluation, so it speaks once for the window already open");
        }

        [TestMethod]
        public void DisabledAcrossAWholeWindow_SaysNothingOnReEnable()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(0, crier.Tick(Local(5, 0), false, Shipped, Pacific, 15).Count);
            Assert.AreEqual(0, crier.Tick(Local(9, 0), true, Shipped, Pacific, 15).Count, "re-enabled after the window: nothing to announce");
        }

        /// <summary>A pause that skips the whole window: the heads-up was the last thing said, the state is now idle, and the stale end line is NOT replayed.</summary>
        [TestMethod]
        public void MissedWholeTransition_HeadsUpThenIdle_ReplaysNothing()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(Warn(10), crier.Tick(Local(5, 50), true, Shipped, Pacific, 15).Single());
            Assert.AreEqual(0, crier.Tick(Local(8, 0), true, Shipped, Pacific, 15).Count, "the open and end that passed unseen are not replayed");
        }

        /// <summary>A pause across a whole window and into the next heads-up: only the CURRENT state's line is said.</summary>
        [TestMethod]
        public void MissedWholeTransition_OpenThenNextHeadsUp_SaysOnlyTheHeadsUp()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(BgBonusCrier.OpenLine("30m"), crier.Tick(Local(6, 30), true, Shipped, Pacific, 15).Single());

            var lines = crier.Tick(Local(17, 50), true, Shipped, Pacific, 15);

            Assert.AreEqual(1, lines.Count, "no end line for the window that closed during the pause");
            Assert.AreEqual(Warn(10), lines[0]);
        }

        /// <summary>A pause from one open window straight into the next: only the new Open is said.</summary>
        [TestMethod]
        public void MissedWholeTransition_OpenThenNextOpen_SaysOnlyTheNewOpen()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(BgBonusCrier.OpenLine("30m"), crier.Tick(Local(6, 30), true, Shipped, Pacific, 15).Single());
            Assert.AreEqual(BgBonusCrier.OpenLine("45m"), crier.Tick(Local(18, 15), true, Shipped, Pacific, 15).Single());
        }

        /// <summary>Open then a tick after it closed, even if the tick is late: the end line is the current state's line and goes out once.</summary>
        [TestMethod]
        public void LateTickAfterTheClose_SaysTheEndOnce()
        {
            var crier = new BgBonusCrier();

            crier.Tick(Local(6, 30), true, Shipped, Pacific, 15);

            Assert.AreEqual(BgBonusCrier.EndLine("10h 55m"), crier.Tick(Local(7, 5), true, Shipped, Pacific, 15).Single());
            Assert.AreEqual(0, crier.Tick(Local(7, 6), true, Shipped, Pacific, 15).Count);
        }

        /// <summary>
        /// Swapping the windows string and then the zone mid-window re-keys the window, but it is the same moment of the same day:
        /// the new config is adopted silently (zero lines), and the next real transition under it fires exactly once.
        /// </summary>
        [TestMethod]
        public void ConfigEditMidWindow_IsAdoptedSilently_AndTheNextRealTransitionFiresOnce()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(BgBonusCrier.OpenLine("30m"), crier.Tick(Local(6, 30), true, Shipped, Pacific, 15).Single());

            // Windows swapped: 06:00-08:00 Pacific, the same zone. The end moved from 07:00 to 08:00.
            var longer = BgBonusWindow.Parse("06:00-08:00");
            Assert.AreEqual(0, crier.Tick(Local(6, 31), true, longer, Pacific, 15).Count, "a windows edit mid-window is not a new Open");
            Assert.AreEqual(0, crier.Tick(Local(6, 32), true, longer, Pacific, 15).Count);

            // Zone swapped to a fixed UTC-8 offset with 05:00-06:00 there: 13:00Z-14:00Z, which is 06:00-07:00 Pacific (PDT), so the
            // clock is still inside the window.
            var utcMinus8 = TimeZoneInfo.CreateCustomTimeZone("t8", TimeSpan.FromHours(-8), "t8", "t8");
            var swapped = BgBonusWindow.Parse("05:00-06:00");
            Assert.AreEqual(0, crier.Tick(Local(6, 33), true, swapped, utcMinus8, 15).Count, "a zone edit mid-window is not a new Open");
            Assert.AreEqual(0, crier.Tick(Local(6, 59), true, swapped, utcMinus8, 15).Count);

            var end = crier.Tick(Local(7, 0), true, swapped, utcMinus8, 15);
            Assert.AreEqual(1, end.Count, "the next real transition (the swapped window closing) fires once");
            StringAssert.StartsWith(end[0], "Battleground Double Marks has ended.");
            Assert.AreEqual(0, crier.Tick(Local(7, 1), true, swapped, utcMinus8, 15).Count);
        }

        /// <summary>The warn lead is part of the config signature too: changing it mid-heads-up is not a new heads-up.</summary>
        [TestMethod]
        public void WarnLeadEditMidHeadsUp_IsAdoptedSilently()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(Warn(10), crier.Tick(Local(5, 50), true, Shipped, Pacific, 15).Single());
            Assert.AreEqual(0, crier.Tick(Local(5, 51), true, Shipped, Pacific, 30).Count);
            Assert.AreEqual(BgBonusCrier.OpenLine("1h 0m"), crier.Tick(Local(6, 0), true, Shipped, Pacific, 30).Single());
        }

        /// <summary>A config edit (warn 0 -> 15) on the exact Idle -> Open tick must not swallow the Open.</summary>
        [TestMethod]
        public void ConfigEditOnTheIdleToOpenTick_StillFiresTheOpen()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(0, crier.Tick(Local(5, 59, 30), true, Shipped, Pacific, 0).Count, "fixture: Idle, no heads-up");

            var lines = crier.Tick(Local(6, 0), true, Shipped, Pacific, 15);

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual(BgBonusCrier.OpenLine("1h 0m"), lines[0]);
            Assert.AreEqual(0, crier.Tick(Local(6, 1), true, Shipped, Pacific, 15).Count);
        }

        /// <summary>A config edit (warn 15 -> 10) on the exact Open -> Idle tick must not swallow the End.</summary>
        [TestMethod]
        public void ConfigEditOnTheOpenToIdleTick_StillFiresTheEnd()
        {
            var crier = new BgBonusCrier();

            Assert.AreEqual(BgBonusCrier.OpenLine("1m"), crier.Tick(Local(6, 59), true, Shipped, Pacific, 15).Single());

            var lines = crier.Tick(Local(7, 0), true, Shipped, Pacific, 10);

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual(BgBonusCrier.EndLine("11h 0m"), lines[0]);
            Assert.AreEqual(0, crier.Tick(Local(7, 1), true, Shipped, Pacific, 10).Count);
        }

        /// <summary>Spans that merge into an always-on window have no end: over two days at most one Open is sent, and no End.</summary>
        [TestMethod]
        public void AlwaysOnMergedConfig_Over48Hours_SendsAtMostOneOpen_AndNoEnd()
        {
            foreach (var spec in new[] { "00:00-12:00,12:00-00:00", "00:00-23:00,22:00-01:00,23:00-00:00", "00:00-23:30,23:00-01:00,23:30-00:00" })
            {
                var alwaysOn = BgBonusWindow.Parse(spec);
                var seen = Run(new BgBonusCrier(), Local(0, 0), Local(0, 0).AddHours(48), TimeSpan.FromMinutes(10), spans: alwaysOn);

                Assert.AreEqual(0, seen.Count(s => s.Line.Contains("is now live")), spec + ": " + string.Join(" | ", seen.Select(s => s.Line)));
                Assert.AreEqual(0, seen.Count(s => s.Line.Contains("has ended")), spec);
            }
        }

        /// <summary>Back-to-back windows merge, so the seam is neither an end nor an open.</summary>
        [TestMethod]
        public void TouchingWindows_AnnounceOneOpenAndOneEnd()
        {
            var spans = BgBonusWindow.Parse("06:00-07:00,07:00-08:00");
            var seen = Run(new BgBonusCrier(), Local(5, 0), Local(8, 30), TimeSpan.FromSeconds(30), spans: spans);

            Assert.AreEqual(3, seen.Count, string.Join(" | ", seen.Select(s => s.Line)));
            Assert.AreEqual(BgBonusCrier.OpenLine("2h 0m"), seen[1].Line);
            Assert.AreEqual(Local(8, 0), seen[2].At);
        }
    }
}
