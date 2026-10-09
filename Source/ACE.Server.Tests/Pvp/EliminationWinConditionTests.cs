using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The shared elimination win condition (Docs/Pvp/DESIGN.md "Win condition"): last team standing,
    /// placements, same-tick ties, a forfeiter placed last, and several forfeiters tying.
    /// </summary>
    [TestClass]
    public class EliminationWinConditionTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch BuildSoloMatch(int teamCount)
        {
            var teams = new List<PvpTeam>();
            for (var i = 0; i < teamCount; i++)
                teams.Add(new PvpTeam(i, new List<PvpParticipant> { new PvpParticipant((uint)(i + 1), 1500) }));

            return new PvpMatch(Guid.NewGuid(), "test", teams, Now);
        }

        [TestMethod]
        public void LastTeamStanding_IsTheWinner()
        {
            var match = BuildSoloMatch(2);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.Died);

            var outcome = wc.Evaluate(match, Now);

            Assert.IsNotNull(outcome);
            CollectionAssert.AreEquivalent(new[] { 0 }, outcome.WinningTeams.ToList());
            Assert.AreEqual(1, outcome.Placements[0]);
            Assert.AreEqual(2, outcome.Placements[1]);
        }

        [TestMethod]
        public void MatchContinues_WhileMoreThanOneTeamAlive()
        {
            var match = BuildSoloMatch(4);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            var outcome = wc.Evaluate(match, Now);

            Assert.IsNull(outcome);
        }

        [TestMethod]
        public void SequentialEliminations_GetStrictlyIncreasingPlacements()
        {
            var match = BuildSoloMatch(4);
            var wc = new EliminationWinCondition();

            // Each elimination is its own tick (its own Evaluate call in between).
            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, Now);

            wc.OnParticipantOut(match, match.Teams[2].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, Now);

            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.Died);
            var outcome = wc.Evaluate(match, Now);

            Assert.IsNotNull(outcome);
            Assert.AreEqual(1, outcome.Placements[0]); // winner
            Assert.AreEqual(2, outcome.Placements[1]); // eliminated last among the three
            Assert.AreEqual(3, outcome.Placements[2]);
            Assert.AreEqual(4, outcome.Placements[3]); // eliminated first
        }

        [TestMethod]
        public void SameTickEliminations_Tie()
        {
            var match = BuildSoloMatch(4);
            var wc = new EliminationWinCondition();

            // Teams 2 and 3 both go out in the SAME tick (no Evaluate call between them).
            wc.OnParticipantOut(match, match.Teams[2].Members[0], ParticipantExit.Died);
            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, Now);

            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.Died);
            var outcome = wc.Evaluate(match, Now);

            Assert.IsNotNull(outcome);
            Assert.AreEqual(1, outcome.Placements[0]);
            Assert.AreEqual(2, outcome.Placements[1]);
            Assert.AreEqual(outcome.Placements[2], outcome.Placements[3]); // tied
        }

        [TestMethod]
        public void Forfeiter_IsPlacedLast_EvenIfForfeitedFirst()
        {
            var match = BuildSoloMatch(3);
            var wc = new EliminationWinCondition();

            // Team 1 forfeits FIRST (should still end up placed last).
            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.ForfeitCommand);
            wc.Evaluate(match, Now);

            // Team 2 is eliminated afterward by ordinary combat.
            wc.OnParticipantOut(match, match.Teams[2].Members[0], ParticipantExit.Died);
            var outcome = wc.Evaluate(match, Now);

            Assert.IsNotNull(outcome);
            Assert.AreEqual(1, outcome.Placements[0]); // winner
            Assert.AreEqual(2, outcome.Placements[2]); // combat elimination outranks the forfeit
            Assert.AreEqual(3, outcome.Placements[1]); // forfeiter: worst placement
        }

        [TestMethod]
        public void SimultaneousDoubleKo_NoForfeits_IsElimination_NotAllForfeited()
        {
            // Two solo teams, both eliminated by DEATH in the same tick (no Evaluate call between them),
            // with zero teams alive afterward - a double KO, never a forfeit.
            var match = BuildSoloMatch(2);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[0].Members[0], ParticipantExit.Died);
            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.Died);

            var outcome = wc.Evaluate(match, Now);

            Assert.IsNotNull(outcome);
            Assert.AreEqual(EndReason.Elimination, outcome.Reason);
            Assert.AreEqual(0, outcome.WinningTeams.Count);
            Assert.AreEqual(outcome.Placements[0], outcome.Placements[1]); // both tied for 1st
        }

        [TestMethod]
        public void MixedForfeitAndCombatDeath_SameTick_ZeroAlive_IsElimination()
        {
            // One team forfeits, the other is eliminated by combat, both in the same tick, leaving zero
            // teams alive. NOT every eliminated team was forfeit-tainted, so this reads as Elimination
            // (the forfeiter still ends up placed worse than the combat elimination via BuildEliminatedPlacements,
            // since forfeits always take the raw "totalTeams" placement value).
            var match = BuildSoloMatch(2);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[0].Members[0], ParticipantExit.ForfeitCommand);
            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.Died);

            var outcome = wc.Evaluate(match, Now);

            Assert.IsNotNull(outcome);
            Assert.AreEqual(EndReason.Elimination, outcome.Reason);
            Assert.AreEqual(0, outcome.WinningTeams.Count);
            Assert.AreEqual(1, outcome.Placements[1]); // sole combat elimination outranks the forfeit
            Assert.AreEqual(2, outcome.Placements[0]); // forfeiter: worst placement
        }

        [TestMethod]
        public void SeveralForfeiters_Tie()
        {
            var match = BuildSoloMatch(4);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.ForfeitLogout);
            wc.Evaluate(match, Now);

            wc.OnParticipantOut(match, match.Teams[2].Members[0], ParticipantExit.ForfeitLeft);
            var outcome = wc.Evaluate(match, Now);

            Assert.IsNull(outcome); // team 3 (index 3) is still alive with only two of four teams eliminated? No - only two eliminated, two alive (0 and 3) - continues.

            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            outcome = wc.Evaluate(match, Now);

            Assert.IsNotNull(outcome);
            Assert.AreEqual(1, outcome.Placements[0]); // winner
            Assert.AreEqual(2, outcome.Placements[3]); // sole combat elimination
            Assert.AreEqual(outcome.Placements[1], outcome.Placements[2]); // both forfeiters tie
            Assert.AreEqual(4, outcome.Placements[1]); // worst placement
        }
    }
}
