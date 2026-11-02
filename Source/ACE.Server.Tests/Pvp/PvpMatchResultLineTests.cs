using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>The pure Arena Crier result-line builder (PvpMatchResultLine): every format, the plural rule, and the 250-character clamp.</summary>
    [TestClass]
    public class PvpMatchResultLineTests
    {
        private static PvpResultSide Side(string label, params string[] names) => new PvpResultSide(label, names);

        private static List<string> Names(string prefix, int count, int length)
            => Enumerable.Range(1, count).Select(i => (prefix + i).PadRight(length, 'x').Substring(0, length)).ToList();

        [TestMethod]
        public void OneVOneWin_PrintsBareNames()
        {
            Assert.AreEqual("1v1: Alpha defeated Bravo.", PvpMatchResultLine.Build("1v1", PvpResultKind.Win, new[] { Side(null, "Alpha"), Side(null, "Bravo") }));
        }

        [TestMethod]
        public void TeamWin_UsesTeamLabelsAndNameLists()
        {
            Assert.AreEqual("2v2: Red team (Alpha, Bravo) defeated Blue team (Charlie, Delta).",
                PvpMatchResultLine.Build("2v2", PvpResultKind.Win, new[] { Side("Red team", "Alpha", "Bravo"), Side("Blue team", "Charlie", "Delta") }));
        }

        [TestMethod]
        public void TwoVTwo_OneMemberSide_StillUsesTheTeamForm()
        {
            Assert.AreEqual("2v2: Red team (Alpha) defeated Blue team (Charlie, Delta).",
                PvpMatchResultLine.Build("2v2", PvpResultKind.Win, new[] { Side("Red team", "Alpha"), Side("Blue team", "Charlie", "Delta") }));
        }

        [TestMethod]
        public void AttackDefend_UsesTheModeTeamNames()
        {
            Assert.AreEqual("Attack/Defend: Defenders (Alpha, Bravo) defeated Attackers (Charlie, Delta).",
                PvpMatchResultLine.Build("Attack/Defend", PvpResultKind.Win, new[] { Side("Defenders", "Alpha", "Bravo"), Side("Attackers", "Charlie", "Delta") }));
        }

        [TestMethod]
        public void KingOfTheHill_Win()
        {
            Assert.AreEqual("King of the Hill: Blue team (Alpha, Bravo) defeated Red team (Charlie).",
                PvpMatchResultLine.Build("King of the Hill", PvpResultKind.Win, new[] { Side("Blue team", "Alpha", "Bravo"), Side("Red team", "Charlie") }));
        }

        [TestMethod]
        public void Draw_TwoSides_OneVOneAndTeams()
        {
            Assert.AreEqual("1v1 ended in a draw: Alpha vs Bravo.", PvpMatchResultLine.Build("1v1", PvpResultKind.Draw, new[] { Side(null, "Alpha"), Side(null, "Bravo") }));
            Assert.AreEqual("2v2 ended in a draw: Red team (Alpha, Bravo) vs Blue team (Charlie, Delta).",
                PvpMatchResultLine.Build("2v2", PvpResultKind.Draw, new[] { Side("Red team", "Alpha", "Bravo"), Side("Blue team", "Charlie", "Delta") }));
        }

        [TestMethod]
        public void Draw_WithNoSides_IsTheFfaDraw()
        {
            Assert.AreEqual("Tugak Brawl ended in a draw.", PvpMatchResultLine.Build("Tugak Brawl", PvpResultKind.Draw));
        }

        [TestMethod]
        public void NoWinner_IsAllForfeitedText()
        {
            Assert.AreEqual("Tugak Brawl ended with no winner.", PvpMatchResultLine.Build("Tugak Brawl", PvpResultKind.NoWinner));
            Assert.AreEqual("1v1 ended with no winner.", PvpMatchResultLine.Build("1v1", PvpResultKind.NoWinner));
        }

        [TestMethod]
        public void Ffa_SingularAndPlural()
        {
            Assert.AreEqual("Tugak Brawl: Alpha won against 1 other fighter.", PvpMatchResultLine.Build("Tugak Brawl", PvpResultKind.FfaWin, new[] { Side(null, "Alpha") }, 1));
            Assert.AreEqual("Tugak Brawl: Alpha won against 4 other fighters.", PvpMatchResultLine.Build("Tugak Brawl", PvpResultKind.FfaWin, new[] { Side(null, "Alpha") }, 4));
        }

        [TestMethod]
        public void Ffa_SharedFirst_JoinsWithAnd()
        {
            Assert.AreEqual("Tugak Brawl: Alpha and Bravo won against 3 other fighters.", PvpMatchResultLine.Build("Tugak Brawl", PvpResultKind.FfaWin, new[] { Side(null, "Alpha", "Bravo") }, 3));
        }

        /// <summary>A 15-seat Tugak Brawl with 20-character names: every winner of a 5-way shared first fits or is shortened, and the clamp holds.</summary>
        [TestMethod]
        public void Ffa_FifteenSeats_LongNames_StaysUnderTheLimitAndKeepsNamesWhole()
        {
            var winners = Names("Winner", 15, 20);
            var line = PvpMatchResultLine.Build("Tugak Brawl", PvpResultKind.FfaWin, new[] { Side(null, winners.ToArray()) }, 0);

            Assert.IsTrue(line.Length <= PvpMatchResultLine.MaxLength, $"{line.Length}: {line}");
            StringAssert.Contains(line, ", and ");
            StringAssert.Contains(line, " more");
            AssertNamesWhole(line, winners);
        }

        /// <summary>A 6v6 with 20-character names: both lists shortened to fit, the line within 250, no name cut mid-word.</summary>
        [TestMethod]
        public void SixVSix_LongNames_StaysUnderTheLimitAndNeverCutsAName()
        {
            var red = Names("Redfighter", 6, 20);
            var blue = Names("Bluefighter", 6, 20);
            var line = PvpMatchResultLine.Build("King of the Hill", PvpResultKind.Win, new[] { Side("Red team", red.ToArray()), Side("Blue team", blue.ToArray()) });

            Assert.IsTrue(line.Length <= PvpMatchResultLine.MaxLength, $"{line.Length}: {line}");
            StringAssert.Contains(line, " more)");
            AssertNamesWhole(line, red.Concat(blue).ToList());
        }

        [TestMethod]
        public void ShortLine_IsNeverShortened()
        {
            var line = PvpMatchResultLine.Build("2v2", PvpResultKind.Win, new[] { Side("Red team", "Alpha", "Bravo"), Side("Blue team", "Charlie", "Delta") });

            Assert.IsFalse(line.Contains("more"));
        }

        [TestMethod]
        public void Output_IsAscii()
        {
            var line = PvpMatchResultLine.Build("2v2", PvpResultKind.Draw, new[] { Side("Red team", "Alpha"), Side("Blue team", "Bravo") });

            Assert.IsTrue(line.All(c => c < 128));
        }

        /// <summary>Every name token appearing in the line equals a complete name from the input (a cut name would not).</summary>
        private static void AssertNamesWhole(string line, List<string> all)
        {
            var tokens = System.Text.RegularExpressions.Regex.Matches(line, "(Redfighter|Bluefighter|Winner)[0-9x]*").Select(m => m.Value).ToList();

            Assert.IsTrue(tokens.Count > 0, "fixture: the line names someone");

            foreach (var token in tokens)
                Assert.IsTrue(all.Contains(token), $"'{token}' is not a whole name in: {line}");
        }    }
}