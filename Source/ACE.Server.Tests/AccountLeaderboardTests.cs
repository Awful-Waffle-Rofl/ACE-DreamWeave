using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The account collapse behind /top bank (<see cref="AccountLeaderboard"/>). Each account contributes
    /// exactly one row, and which row that is has to be the HIGHEST-LEVEL character regardless of score,
    /// because the caller applies its "score > 0" filter AFTER the collapse - so an account whose main has
    /// an empty bank drops off the board entirely instead of being represented by a richer alt. These tests
    /// assert the collapse output rather than a rendered board, since the collapse is where that ruling lives.
    ///
    /// Driven by a local stand-in type rather than Player: the helper is generic precisely because the test
    /// harness cannot construct a live Player, and IPlayer cannot be stubbed cheaply enough to be worth it.
    /// </summary>
    [TestClass]
    public class AccountLeaderboardTests
    {
        /// <summary>Minimal stand-in for a leaderboard candidate: account, level, tie-break and guid.</summary>
        private sealed class Candidate
        {
            public readonly string Name;
            public readonly uint Account;
            public readonly int Level;
            public readonly long TieBreak;
            public readonly uint Ordinal;

            public Candidate(string name, uint account, int level, long tieBreak, uint ordinal)
            {
                Name = name;
                Account = account;
                Level = level;
                TieBreak = tieBreak;
                Ordinal = ordinal;
            }
        }

        private static List<Candidate> Collapse(params Candidate[] candidates)
        {
            return AccountLeaderboard.CollapseToAccountRepresentatives(candidates,
                c => c.Account,
                c => c.Level,
                c => c.TieBreak,
                c => c.Ordinal);
        }

        [TestMethod]
        public void Collapse_OneAccount_KeepsOnlyTheHigherLevelCharacter()
        {
            var result = Collapse(
                new Candidate("Alt", 7, 40, 0, 0x50000001),
                new Candidate("Main", 7, 220, 0, 0x50000002));

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Main", result[0].Name);
        }

        /// <summary>
        /// The ruling, stated as a test: the representative is the highest-level character even when its
        /// score is zero and a lower-level character on the same account holds everything. The caller's
        /// later "> 0" filter then drops the whole account, which is the intended outcome.
        /// </summary>
        [TestMethod]
        public void Collapse_HighestLevelWins_EvenWhenItIsTheOneWithNothingBanked()
        {
            // TieBreak stands in for the score-adjacent tiebreaker here; the balance itself never
            // participates in choosing the representative, which is the whole point.
            var main = new Candidate("BrokeMain", 7, 275, 0, 0x50000001);
            var alt = new Candidate("RichAlt", 7, 12, 0, 0x50000002);

            var result = Collapse(alt, main);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("BrokeMain", result[0].Name);
        }

        [TestMethod]
        public void Collapse_EqualLevels_BreaksOnTieBreakDescending()
        {
            var result = Collapse(
                new Candidate("Behind", 7, 150, 1_000, 0x50000001),
                new Candidate("Ahead", 7, 150, 9_000, 0x50000002));

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("Ahead", result[0].Name);
        }

        /// <summary>
        /// With level and tie-break both equal the choice must still be stable, so it falls to the LOWEST
        /// ordinal (the guid). Run both input orders: a result that depended on list order would pass one
        /// and fail the other.
        /// </summary>
        [TestMethod]
        public void Collapse_EqualLevelAndTieBreak_BreaksOnLowestOrdinal()
        {
            var lower = new Candidate("Older", 7, 150, 500, 0x50000001);
            var higher = new Candidate("Newer", 7, 150, 500, 0x50000009);

            var forward = Collapse(lower, higher);
            var reversed = Collapse(higher, lower);

            Assert.AreEqual(1, forward.Count);
            Assert.AreEqual(1, reversed.Count);
            Assert.AreEqual("Older", forward[0].Name);
            Assert.AreEqual("Older", reversed[0].Name);
        }

        [TestMethod]
        public void Collapse_DistinctAccounts_AreNeverMerged()
        {
            var result = Collapse(
                new Candidate("A1", 1, 100, 0, 0x50000001),
                new Candidate("A2", 1, 90, 0, 0x50000002),
                new Candidate("B1", 2, 80, 0, 0x50000003),
                new Candidate("C1", 3, 70, 0, 0x50000004));

            Assert.AreEqual(3, result.Count);
            CollectionAssert.AreEquivalent(
                new[] { "A1", "B1", "C1" },
                result.Select(c => c.Name).ToArray());
        }

        [TestMethod]
        public void Collapse_EmptyInput_ReturnsEmptyList()
        {
            var result = Collapse();

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [TestMethod]
        public void Collapse_NullInput_ReturnsEmptyList()
        {
            var result = AccountLeaderboard.CollapseToAccountRepresentatives<Candidate>(null,
                c => c.Account,
                c => c.Level,
                c => c.TieBreak,
                c => c.Ordinal);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }
    }
}
