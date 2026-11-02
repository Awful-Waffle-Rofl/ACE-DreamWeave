using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// EliminationWinCondition.ResolveTimeout (Docs/Pvp/DESIGN.md "Modes" table's Timeout column, added
    /// per code review on PR #1396): null before the time limit; 1v1/2v2 always resolve to an unrated
    /// draw at the limit; Tugak Brawl ranks its survivors by enemy-player damage (owner ruling G1, 2026-10-15),
    /// with already-eliminated teams keeping their placement, rated per the mode's own TimeoutRated.
    /// </summary>
    [TestClass]
    public class EliminationWinConditionTimeoutTests
    {
        private static readonly DateTime LiveSince = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static PvpMatch BuildSoloMatch(int teamCount)
        {
            var teams = new List<PvpTeam>();
            for (var i = 0; i < teamCount; i++)
                teams.Add(new PvpTeam(i, new List<PvpParticipant> { new PvpParticipant((uint)(i + 1), 1500) }));

            var match = new PvpMatch(Guid.NewGuid(), "test", teams, LiveSince);
            match.GoLive(LiveSince);
            return match;
        }

        [TestMethod]
        public void BeforeTheLimit_ReturnsNull_1v1Style()
        {
            var match = BuildSoloMatch(2);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(599), timeLimitSeconds: 600, rankSurvivors: false, rated: false);

            Assert.IsNull(outcome);
        }

        [TestMethod]
        public void BeforeTheLimit_ReturnsNull_FfaStyle()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(899), timeLimitSeconds: 900, rankSurvivors: true, rated: true);

            Assert.IsNull(outcome);
        }

        [TestMethod]
        public void AtTheLimit_1v1Style_IsAnUnratedDraw()
        {
            var match = BuildSoloMatch(2);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(600), timeLimitSeconds: 600, rankSurvivors: false, rated: false);

            Assert.IsNotNull(outcome);
            Assert.IsTrue(outcome.IsDraw);
            Assert.IsFalse(outcome.Rated);
            Assert.AreEqual(EndReason.Timeout, outcome.Reason);
            Assert.AreEqual(0, outcome.WinningTeams.Count);
        }

        [TestMethod]
        public void AtTheLimit_FfaStyle_NoDamage_SurvivorsShareFirst_EliminatedKeepPlacement()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            // Two teams already eliminated by combat before the time limit hits.
            wc.OnParticipantOut(match, match.Teams[4].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(1));

            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(2));

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: true);

            Assert.IsNotNull(outcome);
            Assert.IsFalse(outcome.IsDraw);
            Assert.IsTrue(outcome.Rated);
            Assert.AreEqual(EndReason.Timeout, outcome.Reason);

            // The three still-alive teams (0, 1, 2) share 1st.
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, outcome.WinningTeams.ToList());
            Assert.AreEqual(1, outcome.Placements[0]);
            Assert.AreEqual(1, outcome.Placements[1]);
            Assert.AreEqual(1, outcome.Placements[2]);

            // DESIGN "Win condition": an eliminated team's placement is the number of teams still alive when it
            // was eliminated, plus 1 - the same rule at a timeout as in Evaluate. Team 4 went out first with 4
            // teams left (5th); team 3 went out with 3 left (4th). Survivors sharing 1st does not pull them up.
            Assert.AreEqual(4, outcome.Placements[3]);
            Assert.AreEqual(5, outcome.Placements[4]);
        }

        // ================= Tugak G1: survivors ranked by enemy-player damage (owner ruling 2026-10-15) =================

        /// <summary>The FFA reward mapping (PvpArenaRewards.ResultFor, twoTeam false) for every team of an outcome.</summary>
        private static PvpBloodResult[] Results(MatchOutcome outcome)
            => Enumerable.Range(0, outcome.Placements.Count)
                .Select(i => PvpArenaRewards.ResultFor(outcome.IsDraw, twoTeam: false, won: outcome.WinningTeams.Contains(i), placement: outcome.Placements[i]))
                .ToArray();

        /// <summary>
        /// Three survivors with 300 / 100 / 0 damage and two already eliminated. Old rule: all three shared 1st and all three were
        /// paid a Win. G1: they place 1 / 2 / 3, only the 300 is a winner and only it is paid a Win, and the eliminated keep 4 / 5.
        /// </summary>
        [TestMethod]
        public void Timeout_ThreeSurvivors_RankedByDamage_OnlyTheTopIsAWin()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[4].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(1));
            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(2));

            // Team 1 dealt the most, team 0 the least: the ranking follows the damage, not the team order.
            var damage = new Dictionary<int, long> { [0] = 0, [1] = 300, [2] = 100, [3] = 999, [4] = 999 };

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: true, damageByTeam: damage);

            Assert.IsNotNull(outcome);
            Assert.IsFalse(outcome.IsDraw);
            Assert.IsTrue(outcome.Rated);
            Assert.AreEqual(EndReason.Timeout, outcome.Reason);
            CollectionAssert.AreEqual(new[] { 3, 1, 2, 4, 5 }, outcome.Placements.ToArray(), "survivors 300/100/0 place 1/2/3; the eliminated keep 4th and 5th");
            CollectionAssert.AreEquivalent(new[] { 1 }, outcome.WinningTeams.ToList(), "only the top survivor is a winner");
            CollectionAssert.AreEqual(
                new[] { PvpBloodResult.Loss, PvpBloodResult.Win, PvpBloodResult.Loss, PvpBloodResult.Loss, PvpBloodResult.Loss },
                Results(outcome),
                "only placement 1 is paid the Win that every survivor used to get");
        }

        /// <summary>The same three survivors through the FFA rating model: 1st gains more than 2nd, and 3rd now LOSES rating against them.</summary>
        [TestMethod]
        public void Timeout_ThreeSurvivors_RankedByDamage_RatingFollowsThePlacements()
        {
            var match = BuildSoloMatch(3);
            var wc = new EliminationWinCondition();
            var damage = new Dictionary<int, long> { [0] = 300, [1] = 100, [2] = 0 };

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: true, damageByTeam: damage);

            var teams = match.Teams.Select(t => new RatedTeam(t.TeamIndex, new[] { new RatedPlayer(t.Members[0].CharacterId, 1500, 24) })).ToList();
            var deltas = new ACE.Server.Pvp.Rating.PairwisePlacementEloModel(24).Deltas(teams, outcome);

            // Equal ratings: 1st beats both (+12), 2nd splits (0), 3rd loses both (-12). Under the old shared 1st every delta was 0.
            Assert.AreEqual(12, deltas[1u]);
            Assert.AreEqual(0, deltas[2u]);
            Assert.AreEqual(-12, deltas[3u]);
        }

        /// <summary>Two survivors with equal top damage share 1st and both win; the third survivor is 3rd (shared-placement numbering).</summary>
        [TestMethod]
        public void Timeout_EqualTopDamage_SharesFirst()
        {
            var match = BuildSoloMatch(4);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(1));

            var damage = new Dictionary<int, long> { [0] = 250, [1] = 40, [2] = 250 };

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: true, damageByTeam: damage);

            CollectionAssert.AreEqual(new[] { 1, 3, 1, 4 }, outcome.Placements.ToArray());
            CollectionAssert.AreEquivalent(new[] { 0, 2 }, outcome.WinningTeams.ToList());
            CollectionAssert.AreEqual(new[] { PvpBloodResult.Win, PvpBloodResult.Loss, PvpBloodResult.Win, PvpBloodResult.Loss }, Results(outcome));
        }

        /// <summary>
        /// A survivor who dealt nothing still places above every eliminated player, even ones who dealt far more damage (and a forfeiter,
        /// who is always last). Old rule: the 0-damage survivor shared 1st; G1: last of the survivors, but ahead of the eliminated.
        /// </summary>
        [TestMethod]
        public void Timeout_ZeroDamageSurvivor_StillAboveEveryEliminated()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[4].Members[0], ParticipantExit.ForfeitCommand);
            wc.Evaluate(match, LiveSince.AddMinutes(1));
            wc.OnParticipantOut(match, match.Teams[3].Members[0], ParticipantExit.Died);
            wc.OnParticipantOut(match, match.Teams[2].Members[0], ParticipantExit.Died);

            // Teams 2 and 3 die in the timeout's own tick (still pending): they are finalized as one batch before the ranking.
            var damage = new Dictionary<int, long> { [0] = 0, [1] = 10, [2] = 5000, [3] = 5000, [4] = 5000 };

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: true, damageByTeam: damage);

            CollectionAssert.AreEqual(new[] { 2, 1, 3, 3, 5 }, outcome.Placements.ToArray(), "0 damage is 2nd of 2 survivors, still above the 3rd-place batch and the forfeiter");
            CollectionAssert.AreEquivalent(new[] { 1 }, outcome.WinningTeams.ToList());

            var survivorsWorst = outcome.Placements[0];
            for (var i = 2; i < 5; i++)
                Assert.IsTrue(outcome.Placements[i] > survivorsWorst, $"eliminated team {i} must place below every survivor");
        }

        /// <summary>A timeout with one survivor is unchanged: it is 1st and the only winner, whatever the eliminated dealt.</summary>
        [TestMethod]
        public void Timeout_SingleSurvivor_Unchanged()
        {
            var match = BuildSoloMatch(3);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[2].Members[0], ParticipantExit.Died);
            wc.Evaluate(match, LiveSince.AddMinutes(1));

            // Team 1 dies in the timeout's tick, so ResolveTimeout (not Evaluate) decides it, with one survivor left.
            wc.OnParticipantOut(match, match.Teams[1].Members[0], ParticipantExit.Died);

            var damage = new Dictionary<int, long> { [0] = 0, [1] = 800, [2] = 900 };

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: true, damageByTeam: damage);

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, outcome.Placements.ToArray());
            CollectionAssert.AreEquivalent(new[] { 0 }, outcome.WinningTeams.ToList());
            CollectionAssert.AreEqual(new[] { PvpBloodResult.Win, PvpBloodResult.Loss, PvpBloodResult.Loss }, Results(outcome));
        }

        /// <summary>Last man standing is unchanged by G1: Evaluate never reads damage, and the last team standing is the one winner.</summary>
        [TestMethod]
        public void LastManStanding_Unchanged()
        {
            var match = BuildSoloMatch(3);
            var wc = new EliminationWinCondition();

            wc.OnParticipantOut(match, match.Teams[0].Members[0], ParticipantExit.Died);
            Assert.IsNull(wc.Evaluate(match, LiveSince.AddMinutes(1)));

            wc.OnParticipantOut(match, match.Teams[2].Members[0], ParticipantExit.Died);
            var outcome = wc.Evaluate(match, LiveSince.AddMinutes(2));

            Assert.IsNotNull(outcome);
            Assert.AreEqual(EndReason.Elimination, outcome.Reason);
            CollectionAssert.AreEqual(new[] { 3, 1, 2 }, outcome.Placements.ToArray());
            CollectionAssert.AreEquivalent(new[] { 1 }, outcome.WinningTeams.ToList());
            CollectionAssert.AreEqual(new[] { PvpBloodResult.Loss, PvpBloodResult.Win, PvpBloodResult.Loss }, Results(outcome));
        }

        /// <summary>With no ledger (a null damage map) every survivor ties at 0 and shares 1st: the pre-G1 behaviour, kept as the fallback.</summary>
        [TestMethod]
        public void Timeout_NoDamageMap_SurvivorsTieForFirst()
        {
            var match = BuildSoloMatch(3);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: true, damageByTeam: null);

            CollectionAssert.AreEqual(new[] { 1, 1, 1 }, outcome.Placements.ToArray());
            Assert.AreEqual(3, outcome.WinningTeams.Count);
        }

        [TestMethod]
        public void RespectsTimeoutRated_FalseForFfaWouldStillBeUnrated()
        {
            var match = BuildSoloMatch(5);
            var wc = new EliminationWinCondition();

            var outcome = wc.ResolveTimeout(match, LiveSince.AddSeconds(900), timeLimitSeconds: 900, rankSurvivors: true, rated: false);

            Assert.IsNotNull(outcome);
            Assert.IsFalse(outcome.Rated);
        }
    }
}
