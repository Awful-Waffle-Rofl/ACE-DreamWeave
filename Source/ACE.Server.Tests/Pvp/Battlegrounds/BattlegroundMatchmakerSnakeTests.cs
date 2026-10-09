using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp.Battlegrounds
{
    /// <summary>
    /// pvp_bg_snake_draft (Docs/Pvp/BATTLEGROUNDS.md "Matchmaking"): the preference key becomes (premade imbalance, snake deviation,
    /// clan pairs, rating sum difference). The hard constraints and the mirror rule always win over the snake.
    /// </summary>
    [TestClass]
    public class BattlegroundMatchmakerSnakeTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static MatchmakingContext Ctx(bool snake, bool splitClanmates = true, bool blockSameIp = true) => new MatchmakingContext(
            UtcNow: T0.AddHours(1),
            MmWindowInitial: 0, MmWindowGrowthPerMinute: 0, MmWindowMax: 0,
            DuoVsSoloAfterSeconds: 0,
            FfaTargetPlayers: 0, FfaMinPlayers: 0, FfaMaxPlayers: 0, FfaMinDecaySeconds: 0,
            BlockSameIp: blockSameIp,
            Bg: BattlegroundTunables.Defaults with { MinPlayers = 4, MaxPlayers = 12, FillWindowSeconds = 120, SplitClanmates = splitClanmates, SnakeDraft = snake });

        private static Guid IdFor(uint id) => new Guid((int)id, 0, 0, new byte[8]);

        private static QueueEntrant Solo(uint id, int rating, int queueOrder, string ip = null, uint monarch = 0) =>
            new QueueEntrant(IdFor(id), new[] { id }, new[] { rating }, ip, T0.AddSeconds(queueOrder), new[] { monarch });

        private static QueueEntrant Pair(uint firstId, int queueOrder) =>
            new QueueEntrant(IdFor(firstId), new[] { firstId, firstId + 1 }, new[] { 1500, 1500 }, null, T0.AddSeconds(queueOrder), null);

        // Characters 1..6 rated 1800, 1700, 1600, 1500, 1400, 1300 (character id = position in that list + 1).
        private static readonly int[] SixRatings = { 1800, 1700, 1600, 1500, 1400, 1300 };

        private static List<QueueEntrant> SixSolos(Func<int, int> queueOrder = null, Func<int, string> ip = null, Func<int, uint> monarch = null) =>
            Enumerable.Range(0, 6).Select(i => Solo((uint)(i + 1), SixRatings[i], queueOrder?.Invoke(i) ?? i, ip?.Invoke(i), monarch?.Invoke(i) ?? 0)).ToList();

        private static int[] RatingsOf(PvpTeam t, IReadOnlyDictionary<uint, int> ratingById) =>
            t.Members.Select(m => ratingById[m.CharacterId]).OrderByDescending(r => r).ToArray();

        private static Dictionary<uint, int> RatingMap(IEnumerable<QueueEntrant> units) =>
            units.SelectMany(u => u.CharacterIds.Select((c, i) => (c, r: u.Ratings[i]))).ToDictionary(x => x.c, x => x.r);

        private static string Describe(MatchProposal p, IReadOnlyDictionary<uint, int> r) =>
            p == null ? "null" : string.Join(" vs ", p.Teams.Select(t => "[" + string.Join(",", RatingsOf(t, r)) + "]"));

        /// <summary>True when the two sets of ratings are the two teams of the proposal, in either order.</summary>
        private static bool IsSplit(MatchProposal p, IReadOnlyDictionary<uint, int> r, int[] a, int[] b)
        {
            var t0 = RatingsOf(p.Teams[0], r);
            var t1 = RatingsOf(p.Teams[1], r);
            var x = a.OrderByDescending(v => v).ToArray();
            var y = b.OrderByDescending(v => v).ToArray();

            return (t0.SequenceEqual(x) && t1.SequenceEqual(y)) || (t0.SequenceEqual(y) && t1.SequenceEqual(x));
        }

        [TestMethod]
        public void SixSolos_SnakeOn_FormTheSnakeTeams()
        {
            var waiting = SixSolos();
            var r = RatingMap(waiting);
            var p = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: true));

            Assert.IsNotNull(p);
            Assert.IsTrue(IsSplit(p, r, new[] { 1800, 1500, 1400 }, new[] { 1700, 1600, 1300 }), Describe(p, r));
        }

        /// <summary>
        /// The oldest unit is pinned to team 0, so which team holds the top-rated player depends on queue order. Whichever way
        /// the queue is ordered, the same two teams form (the mirror labelling is irrelevant).
        /// </summary>
        [TestMethod]
        public void SixSolos_SnakeOn_SameTeamsWhoeverQueuedFirst()
        {
            foreach (var oldest in new[] { 0, 1, 5 })
            {
                var waiting = SixSolos(queueOrder: i => i == oldest ? -10 : i);
                var r = RatingMap(waiting);
                var p = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: true));

                Assert.IsNotNull(p);
                Assert.IsTrue(IsSplit(p, r, new[] { 1800, 1500, 1400 }, new[] { 1700, 1600, 1300 }), $"oldest={oldest}: {Describe(p, r)}");
            }
        }

        /// <summary>Snake off: only the rating-sum rule applies (best achievable difference here is 100; the premade test pins a case where it differs from the snake).</summary>
        [TestMethod]
        public void SixSolos_SnakeOff_UsesTheRatingSumRule()
        {
            var waiting = SixSolos();
            var r = RatingMap(waiting);
            var p = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: false));

            Assert.IsNotNull(p);
            var sums = p.Teams.Select(t => RatingsOf(t, r).Sum()).ToArray();
            Assert.AreEqual(100, Math.Abs(sums[0] - sums[1]), Describe(p, r));
        }

        /// <summary>A same-IP pair must stay together even where the snake would split it: the hard constraint wins.</summary>
        [TestMethod]
        public void SameIpHardConstraint_BeatsTheSnake()
        {
            // 1800 and 1700 (characters 1 and 2) share an IP; the snake puts them on opposite teams.
            var waiting = SixSolos(ip: i => i < 2 ? "10.0.0.1" : null);
            var p = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: true));

            Assert.IsNotNull(p);

            var teamOf1 = p.Teams.First(t => t.Members.Any(m => m.CharacterId == 1)).TeamIndex;
            var teamOf2 = p.Teams.First(t => t.Members.Any(m => m.CharacterId == 2)).TeamIndex;
            Assert.AreEqual(teamOf1, teamOf2, "the same-IP pair stays on one team");
        }

        /// <summary>
        /// Premades keep the side the split gave them and the solos snake into the remaining places. A pair plus four solos at
        /// 1800, 1700, 1600, 1100 (s = 3): the pair's team gets one solo. Snake on: 1700 (deviation 0, closer sum than 1800);
        /// snake off: 1600 (sum difference 0).
        /// </summary>
        [TestMethod]
        public void Premade_KeepsItsSide_SolosSnakeIntoTheRemainingPlaces()
        {
            var waiting = new List<QueueEntrant>
            {
                Pair(100, 0),
                Solo(1, 1800, 1),
                Solo(2, 1700, 2),
                Solo(3, 1600, 3),
                Solo(4, 1100, 4),
            };
            var r = RatingMap(waiting);

            var on = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: true));
            var off = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: false));

            Assert.IsNotNull(on);
            Assert.IsNotNull(off);

            int PairTeamSolo(MatchProposal p)
            {
                var team = p.Teams.First(t => t.Members.Any(m => m.CharacterId == 100));
                Assert.IsTrue(team.Members.Any(m => m.CharacterId == 101), "the pair stays together");
                Assert.AreEqual(3, team.Members.Count);
                return team.Members.Where(m => m.CharacterId < 100).Select(m => r[m.CharacterId]).Single();
            }

            Assert.AreEqual(1700, PairTeamSolo(on), Describe(on, r));
            Assert.AreEqual(1600, PairTeamSolo(off), Describe(off, r));
        }

        /// <summary>
        /// A is the team with the LOWER premade rating sum. The pair (3000) leaves one spare seat; the premade-free team (sum 0) is A,
        /// so 1800 goes to A, 1700 to the pair's team, then 1600 and 1100 to A. The clanmates 1800 and 1100 stay together (snake beats clan).
        /// Taking the better of both labellings would instead give the pair 1800.
        /// </summary>
        [TestMethod]
        public void LowerPremadeSumSide_IsA_WhenTheSidesDiffer()
        {
            var waiting = new List<QueueEntrant>
            {
                Pair(100, 0),
                Solo(1, 1800, 1, monarch: 7),
                Solo(2, 1700, 2),
                Solo(3, 1600, 3),
                Solo(4, 1100, 4, monarch: 7),
            };
            var r = RatingMap(waiting);
            var p = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: true, splitClanmates: true));

            Assert.IsNotNull(p);

            var pairTeam = p.Teams.First(t => t.Members.Any(m => m.CharacterId == 100));
            Assert.AreEqual(1700, pairTeam.Members.Where(m => m.CharacterId < 100).Select(m => r[m.CharacterId]).Single(), Describe(p, r));
        }

        /// <summary>Two premade pairs, one per team (the premade imbalance key keeps them apart): the four solos snake A, B, B, A.</summary>
        [TestMethod]
        public void TwoPremades_SolosSnakeIntoTheSpareSeats()
        {
            var waiting = new List<QueueEntrant>
            {
                Pair(100, 0),
                Pair(200, 1),
                Solo(1, 1800, 2),
                Solo(2, 1700, 3),
                Solo(3, 1600, 4),
                Solo(4, 1500, 5),
            };
            var r = RatingMap(waiting);
            var p = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: true));

            Assert.IsNotNull(p);
            Assert.IsTrue(p.Teams.All(t => t.Members.Count == 4));

            var teamOfFirstPair = p.Teams.First(t => t.Members.Any(m => m.CharacterId == 100));
            Assert.IsFalse(teamOfFirstPair.Members.Any(m => m.CharacterId == 200), "premades on opposite teams");

            var soloRatings = p.Teams.Select(t => t.Members.Where(m => m.CharacterId < 100).Select(m => r[m.CharacterId]).OrderByDescending(v => v).ToArray()).ToList();
            var snakeA = new[] { 1800, 1500 };
            var snakeB = new[] { 1700, 1600 };

            Assert.IsTrue((soloRatings[0].SequenceEqual(snakeA) && soloRatings[1].SequenceEqual(snakeB)) || (soloRatings[0].SequenceEqual(snakeB) && soloRatings[1].SequenceEqual(snakeA)),
                string.Join(" | ", soloRatings.Select(x => string.Join(",", x))));
        }

        /// <summary>Owner ruling 2026-10-08: the snake draft beats the clanmate split. 1800 and 1500 are clanmates the snake keeps together.</summary>
        [TestMethod]
        public void Snake_BeatsTheClanmateSplit()
        {
            var waiting = SixSolos(monarch: i => (i == 0 || i == 3) ? 7u : 0u);
            var r = RatingMap(waiting);

            var on = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: true, splitClanmates: true));
            Assert.IsNotNull(on);
            Assert.IsTrue(IsSplit(on, r, new[] { 1800, 1500, 1400 }, new[] { 1700, 1600, 1300 }), "snake keeps the clanmates together: " + Describe(on, r));

            var off = new BattlegroundMatchmaker().TryForm(waiting, Ctx(snake: false, splitClanmates: true));
            Assert.IsNotNull(off);

            var teamOf1 = off.Teams.First(t => t.Members.Any(m => m.CharacterId == 1)).TeamIndex;
            var teamOf4 = off.Teams.First(t => t.Members.Any(m => m.CharacterId == 4)).TeamIndex;
            Assert.AreNotEqual(teamOf1, teamOf4, "snake off: the clanmates are split as before");
        }

        /// <summary>The solo order is deterministic: equal ratings fall back to queue time, then EntrantId, whatever order the list arrives in.</summary>
        [TestMethod]
        public void EqualRatings_AreDeterministic_RegardlessOfListOrder()
        {
            var units = Enumerable.Range(0, 6).Select(i => Solo((uint)(i + 1), 1500, i)).ToList();
            var reversed = Enumerable.Reverse(units).ToList();

            string Teams(List<QueueEntrant> w)
            {
                var p = new BattlegroundMatchmaker().TryForm(w, Ctx(snake: true));
                return string.Join(" vs ", p.Teams.Select(t => string.Join(",", t.Members.Select(m => m.CharacterId).OrderBy(x => x))));
            }

            Assert.AreEqual(Teams(units), Teams(reversed));
        }
    }
}
