using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The alternating-result audit (Docs/Pvp/DESIGN.md "Alternating-result audit"), pure class. No PropertyManager reads:
    /// the window and threshold are passed in.
    /// </summary>
    [TestClass]
    public class PvpPairAuditTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        private const long Window = 24, MinSwaps = 3;

        private static AuditTeam Solo(uint account, string name, bool forfeit = false) =>
            new AuditTeam(new[] { account }, new[] { name }, forfeit ? new[] { name } : new string[0]);

        private static string OneVOne(PvpPairAudit audit, DateTime utc, int? winner, bool aFirst = true, bool forfeit = false, long window = Window, long min = MinSwaps)
        {
            var a = Solo(10, "Alpha", forfeit && winner == 1);
            var b = Solo(20, "Bravo", forfeit && winner == 0);
            var teams = aFirst ? new[] { a, b } : new[] { b, a };
            var w = winner.HasValue ? (aFirst ? winner : 1 - winner) : null;

            return audit.Record(utc, teams, w, TimeSpan.FromSeconds(42), forfeit, window, min);
        }

        /// <summary>Drives: winners A,B,A,B for one pair. The first three results hold 0..2 swaps and stay quiet; the fourth is the third swap and flags.</summary>
        [TestMethod]
        public void Alternating_AbAb_IsThreeSwaps_AndFlagsOnTheThird()
        {
            var audit = new PvpPairAudit();

            Assert.IsNull(OneVOne(audit, T0, 0));
            Assert.IsNull(OneVOne(audit, T0.AddMinutes(10), 1));
            Assert.IsNull(OneVOne(audit, T0.AddMinutes(20), 0));
            var line = OneVOne(audit, T0.AddMinutes(30), 1);

            Assert.IsNotNull(line);
            StringAssert.StartsWith(line, "[PVP-AUDIT]");
            StringAssert.Contains(line, "3 winner swaps");
            StringAssert.Contains(line, "Alpha");
            StringAssert.Contains(line, "Bravo");
            StringAssert.Contains(line, "42s");
            Assert.AreEqual(3, PvpPairAudit.CountSwaps(new[] { "a", "b", "a", "b" }));
            Assert.AreEqual(0, PvpPairAudit.CountSwaps(new[] { "a", "a", "a", "a" }), "one side winning repeatedly is not alternating");
        }

        /// <summary>Drives: A wins four times in a row. No swaps, no line.</summary>
        [TestMethod]
        public void SameWinnerEveryTime_NeverFlags()
        {
            var audit = new PvpPairAudit();

            for (var i = 0; i < 8; i++)
                Assert.IsNull(OneVOne(audit, T0.AddMinutes(i), 0));
        }

        /// <summary>Drives: A,B,A, then a draw, then B. The draw neither adds a swap nor breaks the run: swaps are A-B, B-A, A-B = 3 and the line says draw.</summary>
        [TestMethod]
        public void Draws_AreNotSwaps_AndDoNotBreakTheRun()
        {
            var audit = new PvpPairAudit();

            Assert.IsNull(OneVOne(audit, T0, 0));
            Assert.IsNull(OneVOne(audit, T0.AddMinutes(1), 1));
            Assert.IsNull(OneVOne(audit, T0.AddMinutes(2), 0));
            Assert.IsNull(OneVOne(audit, T0.AddMinutes(3), null), "a draw alone is not a swap");
            var line = OneVOne(audit, T0.AddMinutes(4), 1);

            Assert.IsNotNull(line);
            StringAssert.Contains(line, "draw");
            Assert.AreEqual(2, PvpPairAudit.CountSwaps(new[] { "a", null, "b", null, "a" }));
            Assert.AreEqual(0, PvpPairAudit.CountSwaps(new string[] { null, null, null }));
        }

        /// <summary>Drives: A,B,A at t=0..2h, then B at t=25h. The first results fell out of the 24 h window, so the swaps are gone.</summary>
        [TestMethod]
        public void Window_TrimsOldResults()
        {
            var audit = new PvpPairAudit();

            OneVOne(audit, T0, 0);
            OneVOne(audit, T0.AddHours(1), 1);
            OneVOne(audit, T0.AddHours(2), 0);

            Assert.IsNull(OneVOne(audit, T0.AddHours(25), 1), "results older than the window no longer count toward the swaps");
        }

        /// <summary>Drives: a flag at the 4th result, two more swaps in the same window (no second line), then a fresh alternating run a full window later (a second line).</summary>
        [TestMethod]
        public void OneFlagPerWindow_ThenReflagsInALaterWindow()
        {
            var audit = new PvpPairAudit();

            OneVOne(audit, T0, 0);
            OneVOne(audit, T0.AddMinutes(1), 1);
            OneVOne(audit, T0.AddMinutes(2), 0);
            Assert.IsNotNull(OneVOne(audit, T0.AddMinutes(3), 1), "first flag");
            Assert.IsNull(OneVOne(audit, T0.AddMinutes(4), 0), "more swaps inside the window stay quiet");
            Assert.IsNull(OneVOne(audit, T0.AddMinutes(5), 1));

            var later = T0.AddHours(30);

            Assert.IsNull(OneVOne(audit, later, 0));
            Assert.IsNull(OneVOne(audit, later.AddMinutes(1), 1));
            Assert.IsNull(OneVOne(audit, later.AddMinutes(2), 0));
            Assert.IsNotNull(OneVOne(audit, later.AddMinutes(3), 1), "a new window re-flags");
        }

        /// <summary>Drives: the same pair listed A-first and B-first alternately, with the same winners per account. One key, so it flags exactly as if the order never changed.</summary>
        [TestMethod]
        public void Key_IgnoresTeamOrder_AndPartnerOrder()
        {
            var audit = new PvpPairAudit();

            OneVOne(audit, T0, 0, aFirst: true);
            OneVOne(audit, T0.AddMinutes(1), 1, aFirst: false);
            OneVOne(audit, T0.AddMinutes(2), 0, aFirst: true);

            Assert.IsNotNull(OneVOne(audit, T0.AddMinutes(3), 1, aFirst: false));
            Assert.AreEqual(1, audit.TrackedLineups);

            var x = new AuditTeam(new uint[] { 3, 1 }, new[] { "X", "W" }, new string[0]);
            var y = new AuditTeam(new uint[] { 2, 4 }, new[] { "Y", "Z" }, new string[0]);
            var y2 = new AuditTeam(new uint[] { 4, 2 }, new[] { "Z", "Y" }, new string[0]);

            Assert.AreEqual(PvpPairAudit.LineupKey(new[] { x, y }), PvpPairAudit.LineupKey(new[] { y2, x }));
            Assert.AreEqual("1+3|2+4", PvpPairAudit.LineupKey(new[] { x, y }));
            Assert.AreEqual("10|20", PvpPairAudit.LineupKey(new[] { Solo(20, "b"), Solo(10, "a") }));
        }

        /// <summary>Drives: a 2v2 where one partner changes. A different partner is a different lineup, so its swaps do not accumulate onto the first.</summary>
        [TestMethod]
        public void DifferentPartner_IsADifferentLineup()
        {
            var audit = new PvpPairAudit();
            var enemy = new AuditTeam(new uint[] { 5, 6 }, new[] { "E1", "E2" }, new string[0]);

            for (var i = 0; i < 6; i++)
            {
                var mine = new AuditTeam(new uint[] { 1, (uint)(2 + 100 * (i % 2)) }, new[] { "P", "Q" }, new string[0]);

                Assert.IsNull(audit.Record(T0.AddMinutes(i), new[] { mine, enemy }, i % 2, TimeSpan.FromSeconds(30), false, Window, MinSwaps));
            }
        }

        /// <summary>Drives: alternating results with the threshold at 0 (and a window of 0). Both mean "nothing to report".</summary>
        [TestMethod]
        public void ThresholdOrWindowOfZero_EmitsNothing()
        {
            var audit = new PvpPairAudit();

            for (var i = 0; i < 6; i++)
            {
                Assert.IsNull(OneVOne(audit, T0.AddMinutes(i), i % 2, min: 0));
                Assert.IsNull(OneVOne(audit, T0.AddMinutes(i), i % 2, window: 0));
            }
        }

        /// <summary>Drives: a forfeiting loser. The line names who forfeited.</summary>
        [TestMethod]
        public void Line_ListsForfeits()
        {
            var audit = new PvpPairAudit();

            OneVOne(audit, T0, 0, forfeit: true);
            OneVOne(audit, T0.AddMinutes(1), 1);
            OneVOne(audit, T0.AddMinutes(2), 0);
            var line = OneVOne(audit, T0.AddMinutes(3), 1);

            StringAssert.Contains(line, "forfeit by Bravo");
        }

        /// <summary>Drives: results for many different pairs, each older than the window by the time the last arrives. The table forgets them.</summary>
        [TestMethod]
        public void StaleLineups_AreForgotten()
        {
            var audit = new PvpPairAudit();

            for (uint i = 0; i < 20; i++)
                audit.Record(T0, new[] { Solo(100 + i, "a"), Solo(200 + i, "b") }, 0, TimeSpan.FromSeconds(1), false, Window, MinSwaps);

            Assert.AreEqual(20, audit.TrackedLineups);

            audit.Record(T0.AddHours(48), new[] { Solo(1, "a"), Solo(2, "b") }, 0, TimeSpan.FromSeconds(1), false, Window, MinSwaps);

            Assert.AreEqual(1, audit.TrackedLineups);
        }

        /// <summary>Drives: 30 alternating results inside one window with the threshold too high to flag until the 30th. The line lists only the newest 10, says "(+20 earlier)" and still reports the true 30.</summary>
        [TestMethod]
        public void Line_IsBounded_ToTheNewestTen()
        {
            var audit = new PvpPairAudit();
            string line = null;

            for (var i = 0; i < 30; i++)
                line = OneVOne(audit, T0.AddMinutes(i), i % 2, min: 29);

            Assert.IsNotNull(line);
            StringAssert.Contains(line, "29 winner swaps in 30 matches");
            StringAssert.Contains(line, "(+20 earlier)");
            Assert.AreEqual(10, System.Text.RegularExpressions.Regex.Matches(line, "winner (Alpha|Bravo),").Count, "ten results are listed");
            StringAssert.Contains(line, T0.AddMinutes(29).ToString("HH:mm:ss") + "Z");
            Assert.IsFalse(line.Contains(T0.AddMinutes(19).ToString("HH:mm:ss") + "Z"), "the 20th result is cut");

            var short4 = new PvpPairAudit();
            string small = null;

            for (var i = 0; i < 4; i++)
                small = OneVOne(short4, T0.AddMinutes(i), i % 2);

            Assert.IsFalse(small.Contains("earlier"), "no prefix when nothing was cut");
        }

        /// <summary>
        /// Drives: a flag, alternating results through the quiet period (suppressed), then one more alternating result just past it.
        /// The suppressed results stayed in the history, so that first result after the quiet period flags again on its own.
        /// </summary>
        [TestMethod]
        public void SuppressedResults_StayInHistory_AndReflagAfterTheQuietPeriod()
        {
            var audit = new PvpPairAudit();

            OneVOne(audit, T0, 0);
            OneVOne(audit, T0.AddMinutes(1), 1);
            OneVOne(audit, T0.AddMinutes(2), 0);
            Assert.IsNotNull(OneVOne(audit, T0.AddMinutes(3), 1), "flag at t+3m");

            Assert.IsNull(OneVOne(audit, T0.AddHours(23), 0), "suppressed, but kept");
            Assert.IsNull(OneVOne(audit, T0.AddHours(23).AddMinutes(1), 1));
            Assert.IsNull(OneVOne(audit, T0.AddHours(23).AddMinutes(2), 0));

            Assert.IsNull(OneVOne(audit, T0.AddHours(24), 1), "still inside the quiet period (24h after t+0, only 23h57m after the flag)");
            var line = OneVOne(audit, T0.AddHours(24).AddMinutes(10), 0);

            Assert.IsNotNull(line, "the first result past the quiet period re-flags from the retained results");
            StringAssert.Contains(line, T0.AddHours(23).ToString("HH:mm:ss") + "Z");
        }
    }
}