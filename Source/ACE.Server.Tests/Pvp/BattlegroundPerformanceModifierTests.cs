using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;
using ACE.Server.Pvp.Rating;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The battleground performance modifier (Docs/Pvp/BATTLEGROUNDS.md "Rating and performance"; owner rulings 2026-10-08): P_i against the
    /// own team's averages, m_i = clamp(gain x (P_i - 1), -cap, +cap), the per-team zero-sum pass, the forfeiter / single-seat / same-IP
    /// rules, and delta_i = round(B_i x (1 + sign(B_i) x m_i)) on the unrounded team-Elo base.
    /// </summary>
    [TestClass]
    public class BattlegroundPerformanceModifierTests
    {
        private static readonly PerformanceDials Dials = PerformanceDials.From(BattlegroundTunables.Defaults);

        private static readonly MatchOutcome Team0Wins = new MatchOutcome(new HashSet<int> { 0 }, new[] { 1, 2 }, IsDraw: false, Rated: true, Reason: EndReason.Score);
        private static readonly MatchOutcome Team1Wins = new MatchOutcome(new HashSet<int> { 1 }, new[] { 2, 1 }, IsDraw: false, Rated: true, Reason: EndReason.Score);
        private static readonly MatchOutcome RatedDraw = new MatchOutcome(new HashSet<int>(), new[] { 1, 1 }, IsDraw: true, Rated: true, Reason: EndReason.Timeout);

        private static SeatPerformanceSnapshot Koth(uint id, int team, long hill = 0, long dmg = 0, long kills = 0, long nearHill = 0) =>
            new SeatPerformanceSnapshot(id, team, dmg, 0, 0, 0, kills, 0, 0, nearHill, hill);

        private static PerformanceSeat Seat(SeatPerformanceSnapshot s, string ip = null, bool forfeited = false, bool dispatched = true) =>
            new PerformanceSeat(s.CharacterId, s.TeamIndex, s, ip ?? $"10.0.0.{s.CharacterId}", forfeited, dispatched);

        private static IReadOnlyDictionary<uint, SeatPerformanceModifier> KothMods(params PerformanceSeat[] seats) =>
            BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, seats, Dials);

        private static RatedTeam Team(int index, IReadOnlyDictionary<uint, SeatPerformanceModifier> mods, params (uint Id, int Rating, int K, bool Forfeit)[] players) =>
            new RatedTeam(index, players.Select(p => new RatedPlayer(p.Id, p.Rating, p.K, ForceZeroScore: p.Forfeit, PerformanceModifier: mods != null && mods.TryGetValue(p.Id, out var m) ? m.Modifier : 0.0)).ToList());

        /// <summary>A top and a bottom performer on each team of two: player 1 / 3 everything, player 2 / 4 nothing.</summary>
        private static IReadOnlyDictionary<uint, SeatPerformanceModifier> TopAndBottom() => KothMods(
            Seat(Koth(1, 0, hill: 100, dmg: 500, kills: 3)), Seat(Koth(2, 0)),
            Seat(Koth(3, 1, hill: 100, dmg: 500, kills: 3)), Seat(Koth(4, 1)));

        // ================= the owner's example =================

        /// <summary>Base 20 (K 40, even teams). Win: top +25, bottom +15. Loss: top -15, bottom -25. Exactly.</summary>
        [TestMethod]
        public void OwnersExample_Base20_WinAndLoss()
        {
            var mods = TopAndBottom();

            Assert.AreEqual(0.25, mods[1].Modifier, 1e-12, "P = 2: gain 1 x (2 - 1) = 1, capped at 0.25");
            Assert.AreEqual(-0.25, mods[2].Modifier, 1e-12, "P = 0: capped at -0.25");

            var teams = new[] { Team(0, mods, (1, 1500, 40, false), (2, 1500, 40, false)), Team(1, mods, (3, 1500, 40, false), (4, 1500, 40, false)) };
            var deltas = new PerformanceTeamEloModel().Deltas(teams, Team0Wins);

            Assert.AreEqual(20, new TeamEloModel().Deltas(teams, Team0Wins)[1], "fixture: the team-Elo base is 20");
            Assert.AreEqual(25, deltas[1], "winning top performer");
            Assert.AreEqual(15, deltas[2], "winning bottom performer");
            Assert.AreEqual(-15, deltas[3], "losing top performer");
            Assert.AreEqual(-25, deltas[4], "losing bottom performer");
        }

        // ================= draws =================

        /// <summary>
        /// A rated draw between unequal teams: the favourite's base is negative, the underdog's positive, and each follows the sign of its own
        /// base with no special case. Team 0 (1600, K 24): B is about -6.2; team 1 (1400): about +6.2.
        /// </summary>
        [TestMethod]
        public void Draw_FollowsTheSignOfTheBase_BothSigns()
        {
            var mods = TopAndBottom();
            var teams = new[] { Team(0, mods, (1, 1600, 24, false), (2, 1600, 24, false)), Team(1, mods, (3, 1400, 24, false), (4, 1400, 24, false)) };

            var baseDeltas = new TeamEloModel().UnroundedDeltas(teams, RatedDraw);
            Assert.IsTrue(baseDeltas[1] < 0 && baseDeltas[3] > 0, "fixture: the favourite's base is negative, the underdog's positive");

            var deltas = new PerformanceTeamEloModel().Deltas(teams, RatedDraw);

            Assert.AreEqual(TeamEloModel.Round(baseDeltas[1] * 0.75), deltas[1], "negative base, top performer: the loss shrinks");
            Assert.AreEqual(TeamEloModel.Round(baseDeltas[2] * 1.25), deltas[2], "negative base, bottom performer: the loss grows");
            Assert.AreEqual(TeamEloModel.Round(baseDeltas[3] * 1.25), deltas[3], "positive base, top performer: the gain grows");
            Assert.AreEqual(TeamEloModel.Round(baseDeltas[4] * 0.75), deltas[4], "positive base, bottom performer: the gain shrinks");
            Assert.IsTrue(deltas[1] > deltas[2] && deltas[3] > deltas[4], "the better performer always ends higher");
        }

        // ================= the cap =================

        [TestMethod]
        public void Cap_HoldsForAnyOutlier()
        {
            var mods = KothMods(Seat(Koth(1, 0, hill: 100000, dmg: 900000, kills: 99)), Seat(Koth(2, 0, hill: 1)), Seat(Koth(3, 0)), Seat(Koth(5, 0)), Seat(Koth(4, 1)));

            foreach (var m in mods.Values)
                Assert.IsTrue(Math.Abs(m.Modifier) <= Dials.Cap + 1e-12, $"0x{m.CharacterId:X}: {m.Modifier}");

            Assert.AreEqual(0.25, mods[1].Modifier, 1e-12, "the outlier sits on the cap, not above it");

            var cap10 = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfCapPct = 10 });
            var mods10 = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, new[] { Seat(Koth(1, 0, hill: 9)), Seat(Koth(2, 0)) }, cap10);
            Assert.AreEqual(0.10, mods10[1].Modifier, 1e-12, "pvp_bg_perf_cap_pct is the cap");
            Assert.AreEqual(-0.10, mods10[2].Modifier, 1e-12);
        }

        /// <summary>The zero-sum pass only shrinks, so the cap still holds after it (and after rounding the delta stays within B x (1 +/- cap) +/- 0.5).</summary>
        [TestMethod]
        public void Cap_HoldsAfterTheZeroSumPass()
        {
            var mods = KothMods(Seat(Koth(1, 0, hill: 1000)), Seat(Koth(2, 0, hill: 10)), Seat(Koth(3, 0, hill: 10)), Seat(Koth(4, 1)), Seat(Koth(5, 1)));
            var teams = new[] { Team(0, mods, (1, 1500, 40, false), (2, 1500, 24, false), (3, 1500, 24, false)), Team(1, mods, (4, 1500, 24, false), (5, 1500, 24, false)) };

            var r = new PerformanceTeamEloModel().Compute(teams, Team0Wins);

            foreach (var kvp in r.AppliedModifiers)
                Assert.IsTrue(Math.Abs(kvp.Value) <= Dials.Cap + 1e-12, $"0x{kvp.Key:X}: {kvp.Value}");
        }

        // ================= zero-sum =================

        /// <summary>
        /// Uneven K and an outlier: before the pass the team's |B|-weighted modifiers do not cancel; after it, each team's total delta equals
        /// its team-Elo total to within 1 per player, and the |B|-weighted applied modifiers sum to 0.
        /// </summary>
        [TestMethod]
        public void ZeroSum_PerTeam_WithinOnePerPlayer()
        {
            var mods = KothMods(
                Seat(Koth(1, 0, hill: 300, dmg: 50, kills: 1)), Seat(Koth(2, 0, hill: 20, dmg: 900, kills: 0)), Seat(Koth(3, 0, hill: 0, dmg: 10, kills: 4, nearHill: 2)),
                Seat(Koth(4, 1, hill: 5, dmg: 70)), Seat(Koth(5, 1, hill: 600, dmg: 0, kills: 2)), Seat(Koth(6, 1)));
            var teams = new[]
            {
                Team(0, mods, (1, 1530, 40, false), (2, 1470, 24, false), (3, 1610, 24, false)),
                Team(1, mods, (4, 1490, 24, false), (5, 1500, 40, false), (6, 1555, 40, false)),
            };

            foreach (var outcome in new[] { Team0Wins, Team1Wins, RatedDraw })
            {
                var plain = new TeamEloModel();
                var baseDeltas = plain.UnroundedDeltas(teams, outcome);
                var r = new PerformanceTeamEloModel().Compute(teams, outcome);

                foreach (var team in teams)
                {
                    var ids = team.Players.Select(p => p.CharacterId).ToList();
                    var weighted = ids.Sum(id => Math.Abs(baseDeltas[id]) * r.AppliedModifiers[id]);
                    Assert.AreEqual(0.0, weighted, 1e-9, $"team {team.TeamIndex}: sum |B| x m is 0 after the pass");

                    var plainTotal = ids.Sum(id => TeamEloModel.Round(baseDeltas[id]));
                    var perfTotal = ids.Sum(id => r.Deltas[id]);
                    Assert.IsTrue(Math.Abs(perfTotal - plainTotal) <= ids.Count, $"team {team.TeamIndex}: {perfTotal} vs team Elo {plainTotal}");
                }
            }

            var raw = teams[0].Players.Sum(p => p.K * p.PerformanceModifier);
            Assert.AreNotEqual(0.0, raw, 1e-6, "control: before the pass the modifiers did NOT cancel, so the pass did real work");
        }

        [TestMethod]
        public void BalanceZeroSum_ScalesOnlyTheSideThatOvershoots()
        {
            var positive = BattlegroundPerformanceModifier.BalanceZeroSum(new[] { (10.0, 0.25), (10.0, 0.25), (10.0, -0.25) });
            Assert.AreEqual(0.125, positive[0], 1e-12, "positives halved");
            Assert.AreEqual(0.125, positive[1], 1e-12);
            Assert.AreEqual(-0.25, positive[2], 1e-12, "the negative side untouched");

            var negative = BattlegroundPerformanceModifier.BalanceZeroSum(new[] { (20.0, 0.1), (10.0, -0.25) });
            Assert.AreEqual(0.1, negative[0], 1e-12, "the positive side untouched");
            Assert.AreEqual(-0.2, negative[1], 1e-12, "negatives scaled until 20 x 0.1 + 10 x m = 0");

            CollectionAssert.AreEqual(new[] { 0.2, -0.2 }, BattlegroundPerformanceModifier.BalanceZeroSum(new[] { (5.0, 0.2), (5.0, -0.2) }), "already balanced: unchanged");
        }

        // ================= forfeiters =================

        /// <summary>
        /// A forfeiter takes -cap and is left out of the averages: the two who stayed are compared only with each other (P 1.5 and 0.5), not
        /// against a three-seat average the forfeiter's zeros would have pulled down. The forfeiter's -cap is not redistributed.
        /// </summary>
        [TestMethod]
        public void Forfeiter_TakesMinusCap_AndIsExcludedFromTheAverages()
        {
            var mods = KothMods(Seat(Koth(1, 0, hill: 150, dmg: 150, kills: 3)), Seat(Koth(2, 0, hill: 50, dmg: 50, kills: 1)), Seat(Koth(3, 0, hill: 900, dmg: 900, kills: 9), forfeited: true), Seat(Koth(4, 1)), Seat(Koth(5, 1)));

            Assert.AreEqual(-0.25, mods[3].Modifier, 1e-12, "the forfeiter: -cap, whatever it did before leaving");
            Assert.IsNull(mods[3].Score, "the forfeiter is not scored");
            Assert.AreEqual(1.5, mods[1].Score.Value, 1e-12, "150 / avg(150, 50) = 1.5: the forfeiter's 900 is not in the average");
            Assert.AreEqual(0.5, mods[2].Score.Value, 1e-12);

            var teams = new[] { Team(0, mods, (1, 1500, 24, false), (2, 1500, 24, false), (3, 1500, 24, true)), Team(1, mods, (4, 1500, 24, false), (5, 1500, 24, false)) };
            var r = new PerformanceTeamEloModel().Compute(teams, Team0Wins);

            Assert.AreEqual(-0.25, r.AppliedModifiers[3], 1e-12, "the forfeiter keeps -cap through the zero-sum pass");
            Assert.AreEqual(-15, r.Deltas[3], "forfeiter: S = 0, B = 24 x (0 - 0.5) = -12, x 1.25 = -15");
            Assert.AreEqual(0.25, r.AppliedModifiers[1], 1e-12, "the stayers balance only each other");
            Assert.AreEqual(-0.25, r.AppliedModifiers[2], 1e-12);
        }

        // ================= teams that are not compared =================

        [TestMethod]
        public void SingleEligibleSeat_GetsZero()
        {
            var mods = KothMods(Seat(Koth(1, 0, hill: 500)), Seat(Koth(2, 0), forfeited: true), Seat(Koth(3, 1, hill: 9)), Seat(Koth(4, 1)));

            Assert.AreEqual(0.0, mods[1].Modifier, "one eligible seat: nobody to compare with");
            Assert.AreEqual(-0.25, mods[2].Modifier, 1e-12, "the forfeiter still takes -cap");
            Assert.AreNotEqual(0.0, mods[3].Modifier, "control: the other team is compared");
        }

        [TestMethod]
        public void SameIpTeam_GetsZeroForEveryMember()
        {
            var mods = KothMods(Seat(Koth(1, 0, hill: 500), ip: "1.2.3.4"), Seat(Koth(2, 0), ip: "1.2.3.4"), Seat(Koth(5, 0, hill: 10)), Seat(Koth(3, 1, hill: 9)), Seat(Koth(4, 1)));

            Assert.AreEqual(0.0, mods[1].Modifier, "anti-alt-farming");
            Assert.AreEqual(0.0, mods[2].Modifier);
            Assert.AreEqual(0.0, mods[5].Modifier, "every member of that team, not just the pair");
            Assert.AreNotEqual(0.0, mods[3].Modifier, "control: the other team is compared");
        }

        [TestMethod]
        public void UndispatchedSeat_IsExcluded()
        {
            var mods = KothMods(Seat(Koth(1, 0, hill: 100)), Seat(Koth(2, 0, hill: 100)), Seat(Koth(3, 0), dispatched: false, forfeited: true), Seat(Koth(4, 1)), Seat(Koth(5, 1)));

            Assert.AreEqual(0.0, mods[3].Modifier, "undispatched: excluded, not even a forfeit penalty");
            Assert.AreEqual(1.0, mods[1].Score.Value, 1e-12, "the undispatched seat's zeros are not in the average");
        }

        // ================= stats =================

        [TestMethod]
        public void AllZeroStat_IsNeutral()
        {
            var mods = KothMods(Seat(Koth(1, 0, hill: 100)), Seat(Koth(2, 0, hill: 100)), Seat(Koth(3, 1)), Seat(Koth(4, 1)));

            Assert.AreEqual(1.0, mods[1].Score.Value, 1e-12, "damage and kills are 0 across the team: ratio 1 each, and hill is even");
            Assert.AreEqual(0.0, mods[1].Modifier);
            Assert.AreEqual(1.0, mods[3].Score.Value, 1e-12, "a team with every stat 0 is neutral");
            Assert.AreEqual(0.0, mods[3].Modifier);
        }

        /// <summary>A near-hill kill counts pvp_bg_perf_koth_near_kill_weight (2) kills: one near kill against one far kill is 2 : 1.</summary>
        [TestMethod]
        public void Koth_NearHillKill_CountsTheNearKillWeight()
        {
            var only = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfWeightKothHillSeconds = 0, PerfWeightKothDmg = 0 });
            var mods = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, new[] { Seat(Koth(1, 0, kills: 1, nearHill: 1)), Seat(Koth(2, 0, kills: 1)) }, only);

            Assert.AreEqual(2.0 / 1.5, mods[1].Score.Value, 1e-12, "weighted 2 vs average 1.5");
            Assert.AreEqual(1.0 / 1.5, mods[2].Score.Value, 1e-12);
        }

        /// <summary>Attack/Defend scores each role on its own stats: an attacker's defender-only counters and a defender's crystal damage are ignored.</summary>
        [TestMethod]
        public void AttackDefend_EachRoleUsesItsOwnStats()
        {
            SeatPerformanceSnapshot Ad(uint id, int team, long player = 0, long crystal = 0, long nearDmg = 0, long farDmg = 0, long kills = 0, long nearKills = 0, long farKills = 0) =>
                new SeatPerformanceSnapshot(id, team, player, crystal, nearDmg, farDmg, kills, nearKills, farKills, 0, 0);

            var mods = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.AttackDefendModeKey, new[]
            {
                Seat(Ad(1, CrystalWinCondition.AttackerTeam, crystal: 300, nearDmg: 9999)),
                Seat(Ad(2, CrystalWinCondition.AttackerTeam, crystal: 100)),
                Seat(Ad(3, CrystalWinCondition.DefenderTeam, crystal: 9999, nearDmg: 100, nearKills: 1)),
                Seat(Ad(4, CrystalWinCondition.DefenderTeam, farDmg: 100, farKills: 1)),
            }, Dials);

            // Attackers: crystal 300 vs avg 200 -> 1.5 (weight 30); player damage and kills all 0 -> 1 (weights 40, 30). P = (45 + 40 + 30) / 100.
            Assert.AreEqual(1.15, mods[1].Score.Value, 1e-12);
            // Defenders: near dmg 2, near kills 2 (weights 30, 30), far 0, 0 (weights 20, 20). P = (60 + 60) / 100.
            Assert.AreEqual(1.2, mods[3].Score.Value, 1e-12, "near 60% outweighs far 40%");
            Assert.AreEqual(0.8, mods[4].Score.Value, 1e-12);
        }

        // ================= switches =================

        /// <summary>With pvp_bg_perf_enabled off every modifier is 0 and the deltas equal TeamEloModel's exactly, for every outcome.</summary>
        [TestMethod]
        public void SettingOff_EqualsTeamElo()
        {
            var off = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfEnabled = false });
            var mods = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, new[] { Seat(Koth(1, 0, hill: 500)), Seat(Koth(2, 0)), Seat(Koth(3, 1, hill: 9)), Seat(Koth(4, 1)) }, off);

            Assert.IsTrue(mods.Values.All(m => m.Modifier == 0.0));

            var teams = new[] { Team(0, mods, (1, 1530, 40, false), (2, 1470, 24, true)), Team(1, mods, (3, 1490, 24, false), (4, 1700, 40, false)) };

            foreach (var outcome in new[] { Team0Wins, Team1Wins, RatedDraw })
                CollectionAssert.AreEquivalent(new TeamEloModel().Deltas(teams, outcome).ToList(), new PerformanceTeamEloModel().Deltas(teams, outcome).ToList());

            var on = TopAndBottom();
            var onTeams = new[] { Team(0, on, (1, 1500, 40, false), (2, 1500, 40, false)), Team(1, on, (3, 1500, 40, false), (4, 1500, 40, false)) };
            Assert.AreNotEqual(new TeamEloModel().Deltas(onTeams, Team0Wins)[1], new PerformanceTeamEloModel().Deltas(onTeams, Team0Wins)[1], "control: with modifiers the models differ");
        }

        /// <summary>
        /// The refactor keeps arena ratings byte-identical: TeamEloModel.Deltas equals the pre-refactor per-player EloRating.Delta(K, S, E) over a
        /// sweep of ratings, K values, forfeits and outcomes.
        /// </summary>
        [TestMethod]
        public void TeamElo_Refactor_IsByteIdenticalToEloRatingDelta()
        {
            var ratings = new[] { 900, 1213, 1500, 1501, 1777, 2400 };
            var ks = new[] { 24, 40, 17 };
            var checkedCount = 0;

            foreach (var ra in ratings)
                foreach (var rb in ratings)
                    foreach (var k in ks)
                        foreach (var outcome in new[] { Team0Wins, Team1Wins, RatedDraw })
                            foreach (var forfeit in new[] { false, true })
                            {
                                var a = new RatedTeam(0, new[] { new RatedPlayer(1, ra, k, ForceZeroScore: forfeit), new RatedPlayer(2, ra + 37, 24) });
                                var b = new RatedTeam(1, new[] { new RatedPlayer(3, rb, 40) });
                                var deltas = new TeamEloModel().Deltas(new[] { a, b }, outcome);

                                var eA = EloRating.ExpectedScore((ra + ra + 37) / 2.0, rb);
                                double S(int team, bool f) => f ? 0.0 : outcome.IsDraw ? 0.5 : outcome.WinningTeams.Contains(team) ? 1.0 : 0.0;

                                Assert.AreEqual(EloRating.Delta(k, S(0, forfeit), eA), deltas[1]);
                                Assert.AreEqual(EloRating.Delta(24, S(0, false), eA), deltas[2]);
                                Assert.AreEqual(EloRating.Delta(40, S(1, false), 1.0 - eA), deltas[3]);
                                checkedCount++;
                            }

            Assert.AreEqual(6 * 6 * 3 * 3 * 2, checkedCount);
        }

        [TestMethod]
        public void PerformanceDials_SanitizeTheSettings()
        {
            var d = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfGain = double.NaN, PerfCapPct = 400, PerfWeightKothDmg = -5, PerfKothNearKillWeight = double.PositiveInfinity });

            Assert.AreEqual(0.0, d.Gain, "NaN gain = 0");
            Assert.AreEqual(1.0, d.Cap, "cap clamped to 100 percent");
            Assert.AreEqual(0.0, d.KothDmg, "negative weight = 0");
            Assert.AreEqual(1.0, d.KothNearKillWeight, "infinite near-kill weight = 1");
            Assert.AreEqual(0.0, PerformanceDials.From(BattlegroundTunables.Defaults with { PerfCapPct = -3 }).Cap);
            Assert.AreEqual(0.25, Dials.Cap, 1e-12, "the shipped cap is 25 percent");
        }

        /// <summary>Every weight 0: no stat is scored, P = 1 and m = 0 for every eligible seat; the forfeiter still takes -cap.</summary>
        [TestMethod]
        public void AllWeightsZero_IsNeutral()
        {
            var zero = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfWeightKothHillSeconds = 0, PerfWeightKothDmg = 0, PerfWeightKothKills = 0 });
            var mods = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, new[] { Seat(Koth(1, 0, hill: 500, kills: 4)), Seat(Koth(2, 0)), Seat(Koth(5, 0), forfeited: true), Seat(Koth(3, 1)), Seat(Koth(4, 1)) }, zero);

            Assert.AreEqual(0.0, mods[1].Modifier);
            Assert.AreEqual(0.0, mods[2].Modifier);
            Assert.AreEqual(-0.25, mods[5].Modifier, 1e-12, "the forfeiter penalty does not depend on the weights");
        }

        /// <summary>Gain 0: every compared seat's m is 0, but the forfeiter penalty ignores gain (only the switch or a 0 cap removes it).</summary>
        [TestMethod]
        public void GainZero_ZeroesComparison_ButNotTheForfeiterPenalty()
        {
            var noGain = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfGain = 0.0 });
            var seats = new[] { Seat(Koth(1, 0, hill: 500)), Seat(Koth(2, 0)), Seat(Koth(5, 0), forfeited: true), Seat(Koth(3, 1)), Seat(Koth(4, 1)) };
            var mods = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, seats, noGain);

            Assert.AreEqual(0.0, mods[1].Modifier);
            Assert.AreEqual(0.0, mods[2].Modifier);
            Assert.AreEqual(1.5, mods[1].Score.Value, 1e-12, "P is still computed and logged: hill 2 x 50, damage and kills 1 x 50");
            Assert.AreEqual(-0.25, mods[5].Modifier, 1e-12, "the forfeiter penalty ignores gain");

            var capZero = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfCapPct = 0 });
            Assert.AreEqual(0.0, BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, seats, capZero)[5].Modifier, "a 0 cap removes it");
        }

        /// <summary>
        /// A near-kill weight of 1e308 with two near-hill kills each would overflow the weighted kill count to infinity, and inf / inf is a NaN
        /// that passes Math.Clamp and poisons the zero-sum pass. The weight is capped at 1000 and anything non-finite is neutral, so both
        /// teammates (identical stats) get m = 0 and every delta is finite and exactly the team-Elo one.
        /// </summary>
        [TestMethod]
        public void HugeNearKillWeight_StaysFinite_AndEqualsTeamElo()
        {
            var huge = PerformanceDials.From(BattlegroundTunables.Defaults with { PerfKothNearKillWeight = 1e308 });
            var mods = BattlegroundPerformanceModifier.Modifiers(BattlegroundModes.KothModeKey, new[]
            {
                Seat(Koth(1, 0, kills: 2, nearHill: 2)), Seat(Koth(2, 0, kills: 2, nearHill: 2)),
                Seat(Koth(3, 1, kills: 1, nearHill: 1)), Seat(Koth(4, 1, kills: 1, nearHill: 1)),
            }, huge);

            foreach (var m in mods.Values)
            {
                Assert.IsFalse(double.IsNaN(m.Modifier) || double.IsInfinity(m.Modifier), $"0x{m.CharacterId:X}: {m.Modifier}");
                Assert.AreEqual(0.0, m.Modifier, $"0x{m.CharacterId:X}: identical teammates");
            }

            var teams = new[] { Team(0, mods, (1, 1500, 40, false), (2, 1500, 40, false)), Team(1, mods, (3, 1500, 40, false), (4, 1500, 40, false)) };
            var r = new PerformanceTeamEloModel().Compute(teams, Team0Wins);
            var plain = new TeamEloModel().Deltas(teams, Team0Wins);

            foreach (var id in new uint[] { 1, 2, 3, 4 })
            {
                Assert.IsFalse(double.IsNaN(r.AppliedModifiers[id]), $"0x{id:X}");
                Assert.AreEqual(plain[id], r.Deltas[id], $"0x{id:X}: the team-Elo delta");
            }

            Assert.AreEqual(PerformanceDials.MaxKothNearKillWeight, huge.KothNearKillWeight, "capped");
        }

        /// <summary>The rating model treats a non-finite modifier handed to it as 0, for a forfeiter and a pooled seat alike.</summary>
        [TestMethod]
        public void Model_NonFiniteModifier_CountsAsZero()
        {
            var teams = new[]
            {
                new RatedTeam(0, new[] { new RatedPlayer(1, 1500, 40, PerformanceModifier: double.NaN), new RatedPlayer(2, 1500, 40, ForceZeroScore: true, PerformanceModifier: double.PositiveInfinity) }),
                new RatedTeam(1, new[] { new RatedPlayer(3, 1500, 40, PerformanceModifier: double.NegativeInfinity), new RatedPlayer(4, 1500, 40) }),
            };

            var r = new PerformanceTeamEloModel().Compute(teams, Team0Wins);

            CollectionAssert.AreEquivalent(new TeamEloModel().Deltas(teams, Team0Wins).ToList(), r.Deltas.ToList());
            Assert.IsTrue(r.AppliedModifiers.Values.All(v => v == 0.0));
        }

        [TestMethod]
        public void ChatPercent_RoundsAndHidesZero()
        {
            Assert.AreEqual("+18", BattlegroundPerformanceModifier.ChatPercent(0.18));
            Assert.AreEqual("-25", BattlegroundPerformanceModifier.ChatPercent(-0.25));
            Assert.AreEqual("+1", BattlegroundPerformanceModifier.ChatPercent(0.005), "halves away from zero");
            Assert.IsNull(BattlegroundPerformanceModifier.ChatPercent(0.0));
            Assert.IsNull(BattlegroundPerformanceModifier.ChatPercent(-0.004), "rounds to 0: nothing shown");
        }
    }
}
