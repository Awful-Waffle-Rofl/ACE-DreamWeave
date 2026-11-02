using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The fresh-opponent tier rule (Docs/Pvp/DESIGN.md "Fresh-opponent matchmaking") in both 1v1 and 2v2 matchmakers, driven through the
    /// pure <see cref="MatchmakingContext.Probe"/> / <see cref="MatchmakingContext.OnRefusedUnpaid"/> seams: fresh and paid first, then
    /// recent but paid, then unpaid; tiers only reorder, never remove; an unpaid side that refuses blocks the pairing and is reported.
    /// Pure: no PropertyManager, no coordinator.
    /// </summary>
    [TestClass]
    public class FreshOpponentMatchmakerTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static QueueEntrant Solo(uint id, int order, bool refuse = false) =>
            new QueueEntrant(Guid.NewGuid(), new[] { id }, new[] { 1500 }, null, Now.AddSeconds(order), RefuseUnpaidRematch: refuse);

        private static QueueEntrant Duo(uint a, uint b, int order, bool refuse = false) =>
            new QueueEntrant(Guid.NewGuid(), new[] { a, b }, new[] { 1500, 1500 }, null, Now.AddSeconds(order), RefuseUnpaidRematch: refuse);

        private static MatchmakingContext Ctx(
            Func<QueueEntrant, QueueEntrant, (bool Recent, IReadOnlyList<bool> UnpaidA, IReadOnlyList<bool> UnpaidB)> probe = null,
            Action<Guid, IReadOnlyList<uint>> onRefused = null,
            DateTime? now = null,
            Func<int, int> pickIndex = null) => new MatchmakingContext(
                UtcNow: now ?? Now.AddMinutes(10),
                MmWindowInitial: 100, MmWindowGrowthPerMinute: 50, MmWindowMax: 400,
                DuoVsSoloAfterSeconds: 120,
                FfaTargetPlayers: 10, FfaMinPlayers: 5, FfaMaxPlayers: 15, FfaMinDecaySeconds: 60,
                BlockSameIp: true,
                Probe: probe,
                OnRefusedUnpaid: onRefused,
                PickIndex: pickIndex);

        /// <summary>
        /// A probe over id sets: pairs listed in <paramref name="recent"/> are recent; a member whose id is in <paramref name="unpaidIds"/> is
        /// unpaid when any member of the other entrant is too. Per-member, like the coordinator's probe.
        /// </summary>
        private static Func<QueueEntrant, QueueEntrant, (bool, IReadOnlyList<bool>, IReadOnlyList<bool>)> Probe(
            (uint, uint)[] recent = null,
            uint[] unpaidIds = null)
        {
            unpaidIds ??= Array.Empty<uint>();

            var directed = unpaidIds.SelectMany(m => unpaidIds.Where(o => o != m).Select(o => (m, o))).ToArray();

            return Directed(recent, directed);
        }

        /// <summary>A probe whose unpaid bits are DIRECTED: (member, opponent) means that member is unpaid against an entrant containing that opponent - the other way round is not.</summary>
        private static Func<QueueEntrant, QueueEntrant, (bool, IReadOnlyList<bool>, IReadOnlyList<bool>)> Directed(
            (uint, uint)[] recent = null,
            (uint Member, uint Opponent)[] unpaid = null)
        {
            recent ??= Array.Empty<(uint, uint)>();
            unpaid ??= Array.Empty<(uint, uint)>();

            bool Has(QueueEntrant e, uint id) => e.CharacterIds.Contains(id);

            IReadOnlyList<bool> Side(QueueEntrant me, QueueEntrant other) =>
                me.CharacterIds.Select(m => unpaid.Any(u => u.Member == m && Has(other, u.Opponent))).ToList();

            return (a, b) =>
            {
                var isRecent = recent.Any(p => (Has(a, p.Item1) && Has(b, p.Item2)) || (Has(a, p.Item2) && Has(b, p.Item1)));

                return (isRecent, Side(a, b), Side(b, a));
            };
        }
        private static uint[] Ids(MatchProposal p) => p.Teams.SelectMany(t => t.Members).Select(m => m.CharacterId).OrderBy(i => i).ToArray();

        // ---------------- 1v1 ----------------

        [TestMethod]
        public void Pair_FreshCandidatePreferredOverEarlierRecentOne()
        {
            // 1 is the anchor; 2 is queued before 3 but was fought recently, so 3 wins.
            var waiting = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1), Solo(3, 2) };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 2u) })));

            CollectionAssert.AreEqual(new uint[] { 1, 3 }, Ids(proposal));
        }

        [TestMethod]
        public void Pair_RecentOnlyCandidate_StillPairs()
        {
            var waiting = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1) };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 2u) })));

            Assert.IsNotNull(proposal, "tiers reorder valid candidates; they never remove one");
            CollectionAssert.AreEqual(new uint[] { 1, 2 }, Ids(proposal));
        }

        [TestMethod]
        public void Pair_UnpaidIsLast_RecentPaidBeatsIt()
        {
            // 2 is unpaid against 1 (and not recent); 3 is recent but paid. Recent-paid is tier 2, unpaid tier 3.
            var waiting = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1), Solo(3, 2) };
            var probe = Probe(recent: new[] { (1u, 3u) }, unpaidIds: new[] { 1u, 2u });

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(probe));

            CollectionAssert.AreEqual(new uint[] { 1, 3 }, Ids(proposal));
        }

        [TestMethod]
        public void Pair_UnpaidOnlyCandidate_PairsWhenNobodyRefuses()
        {
            var waiting = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1) };

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Probe(unpaidIds: new[] { 1u, 2u })));

            Assert.IsNotNull(proposal);
        }

        [TestMethod]
        public void Pair_RefusingUnpaidSide_NeverForms_AndFiresCallback()
        {
            var refuser = Solo(1, 0, refuse: true);
            var other = Solo(2, 1);
            var refused = new List<Guid>();

            var proposal = new PairMatchmaker().TryForm(new List<QueueEntrant> { refuser, other }, Ctx(Probe(unpaidIds: new[] { 1u, 2u }), (id, _) => refused.Add(id)));

            Assert.IsNull(proposal);
            Assert.IsTrue(refused.Contains(refuser.EntrantId), "the refusing unit is reported");
            Assert.IsFalse(refused.Contains(other.EntrantId), "the unit that did not refuse is not");
        }

        [TestMethod]
        public void Pair_RefusingSide_NotBlockedWhenThePairingIsPaid()
        {
            var waiting = new List<QueueEntrant> { Solo(1, 0, refuse: true), Solo(2, 1) };
            var refused = new List<Guid>();

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 2u) }), (id, _) => refused.Add(id)));

            Assert.IsNotNull(proposal, "refusing only blocks UNPAID pairings; a recent paid one still forms");
            Assert.AreEqual(0, refused.Count);
        }

        [TestMethod]
        public void Pair_RefuserSkipsUnpaid_ButTakesLaterFresh()
        {
            var waiting = new List<QueueEntrant> { Solo(1, 0, refuse: true), Solo(2, 1), Solo(3, 2) };
            var refused = new List<Guid>();

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(Probe(unpaidIds: new[] { 1u, 2u }), (id, _) => refused.Add(id)));

            CollectionAssert.AreEqual(new uint[] { 1, 3 }, Ids(proposal));
        }

        [TestMethod]
        public void Pair_NullProbe_IsLegacy_FirstValidCandidateInQueueOrder()
        {
            var refusing = Solo(1, 0, refuse: true);
            var waiting = new List<QueueEntrant> { refusing, Solo(2, 1), Solo(3, 2) };
            var refused = new List<Guid>();

            var proposal = new PairMatchmaker().TryForm(waiting, Ctx(probe: null, onRefused: (id, _) => refused.Add(id)));

            CollectionAssert.AreEqual(new uint[] { 1, 2 }, Ids(proposal), "no probe: the first valid candidate, whatever the flags say");
            Assert.AreEqual(0, refused.Count, "no probe, no refusal");
        }

        [TestMethod]
        public void Pair_TierKeepsRatingWindowAndSameIpFilters()
        {
            // The fresh candidate is outside the window, the same-IP one is blocked: only the recent in-window one remains.
            var a = new QueueEntrant(Guid.NewGuid(), new uint[] { 1 }, new[] { 1500 }, "9.9.9.9", Now);
            var recentOk = new QueueEntrant(Guid.NewGuid(), new uint[] { 2 }, new[] { 1550 }, "1.1.1.1", Now.AddSeconds(1));
            var freshFar = new QueueEntrant(Guid.NewGuid(), new uint[] { 3 }, new[] { 2500 }, "2.2.2.2", Now.AddSeconds(2));
            var freshSameIp = new QueueEntrant(Guid.NewGuid(), new uint[] { 4 }, new[] { 1500 }, "9.9.9.9", Now.AddSeconds(3));

            var proposal = new PairMatchmaker().TryForm(new List<QueueEntrant> { a, recentOk, freshFar, freshSameIp },
                Ctx(Probe(recent: new[] { (1u, 2u) }), now: Now));

            CollectionAssert.AreEqual(new uint[] { 1, 2 }, Ids(proposal));
        }

        // ---------------- 2v2 ----------------

        [TestMethod]
        public void Duo_DuoVsDuo_FreshDuoPreferredOverRecentOne()
        {
            var waiting = new List<QueueEntrant> { Duo(1, 2, 0), Duo(3, 4, 1), Duo(5, 6, 2) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 3u) })));

            CollectionAssert.AreEqual(new uint[] { 1, 2, 5, 6 }, Ids(proposal));
        }

        [TestMethod]
        public void Duo_DuoVsDuo_RecentOnlyStillPairs()
        {
            var waiting = new List<QueueEntrant> { Duo(1, 2, 0), Duo(3, 4, 1) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (2u, 4u) })));

            Assert.IsNotNull(proposal);
        }

        [TestMethod]
        public void Duo_DuoVsDuo_UnpaidLastAndRefusalBlocks()
        {
            var refuser = Duo(1, 2, 0, refuse: true);
            var waiting = new List<QueueEntrant> { refuser, Duo(3, 4, 1), Duo(5, 6, 2) };
            var refused = new List<Guid>();

            // 3 and 4 are unpaid against the first duo: the refuser skips them and takes the fresh duo behind them.
            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(unpaidIds: new[] { 1u, 3u }), (id, _) => refused.Add(id)));

            CollectionAssert.AreEqual(new uint[] { 1, 2, 5, 6 }, Ids(proposal));

            // With only the unpaid duo, the refuser forms nothing and is reported.
            refused.Clear();
            var none = new DuoMatchmaker().TryForm(new List<QueueEntrant> { refuser, Duo(3, 4, 1) }, Ctx(Probe(unpaidIds: new[] { 1u, 3u }), (id, _) => refused.Add(id)));

            Assert.IsNull(none);
            CollectionAssert.Contains(refused, refuser.EntrantId);
        }

        [TestMethod]
        public void Duo_DuoVsTwoSolos_FreshSolosPreferredOverRecentOnes()
        {
            // The duo has waited past duo_vs_solo_after_seconds. Solos 3+4 are recent opponents of the duo; solos 5+6 are fresh.
            var waiting = new List<QueueEntrant> { Duo(1, 2, 0), Solo(3, 1), Solo(4, 2), Solo(5, 3), Solo(6, 4) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 3u), (2u, 4u) })));

            CollectionAssert.AreEqual(new uint[] { 1, 2, 5, 6 }, Ids(proposal));
        }

        [TestMethod]
        public void Duo_DuoVsTwoSolos_RecentOnlyStillPairs()
        {
            var waiting = new List<QueueEntrant> { Duo(1, 2, 0), Solo(3, 1), Solo(4, 2) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 3u) })));

            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4 }, Ids(proposal));
        }

        [TestMethod]
        public void Duo_DuoVsTwoSolos_RefusingSoloIsSkippedAndReported()
        {
            // Solo 3 refuses and is unpaid against the duo; the duo pairs with solos 4+5 instead.
            var refuser = Solo(3, 1, refuse: true);
            var waiting = new List<QueueEntrant> { Duo(1, 2, 0), refuser, Solo(4, 2), Solo(5, 3) };
            var refused = new List<Guid>();

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(unpaidIds: new[] { 1u, 3u }), (id, _) => refused.Add(id)));

            CollectionAssert.AreEqual(new uint[] { 1, 2, 4, 5 }, Ids(proposal));
            CollectionAssert.Contains(refused, refuser.EntrantId);
        }

        [TestMethod]
        public void Duo_DuoVsTwoSolos_NoProposalWhenEveryPairingIsRefused()
        {
            var waiting = new List<QueueEntrant> { Duo(1, 2, 0, refuse: true), Solo(3, 1), Solo(4, 2) };
            var refused = new List<Guid>();

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(unpaidIds: new[] { 1u, 3u }), (id, _) => refused.Add(id)));

            Assert.IsNull(proposal);
            Assert.IsTrue(refused.Count > 0);
        }

        [TestMethod]
        public void Duo_NullProbe_IsLegacyOrder()
        {
            var waiting = new List<QueueEntrant> { Duo(1, 2, 0, refuse: true), Duo(3, 4, 1), Duo(5, 6, 2) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(probe: null));

            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4 }, Ids(proposal));
        }

        // ---------------- per-member refusal and asymmetry ----------------

        [TestMethod]
        public void Pair_AsymmetricUnpaid_OnlyTheUnpaidSideRefusing_Blocks()
        {
            // 1 is unpaid against 2, but 2 is NOT unpaid against 1. 2 refuses (and is paid): no block. 1 refuses: block, 1 reported.
            var probe = Directed(unpaid: new[] { (1u, 2u) });

            var paidRefuser = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1, refuse: true) };
            Assert.IsNotNull(new PairMatchmaker().TryForm(paidRefuser, Ctx(probe)), "a refuser who is itself paid does not block");

            var unpaidRefuser = Solo(1, 0, refuse: true);
            var refused = new List<Guid>();
            var none = new PairMatchmaker().TryForm(new List<QueueEntrant> { unpaidRefuser, Solo(2, 1) }, Ctx(probe, (id, _) => refused.Add(id)));

            Assert.IsNull(none);
            CollectionAssert.AreEqual(new[] { unpaidRefuser.EntrantId }, refused.Distinct().ToList(), "only the unpaid refuser is reported (possibly once per anchor pass)");
        }

        [TestMethod]
        public void Duo_MemberLevel_RefuserMustItselfBeUnpaid()
        {
            // Duo (1, 2): member 1 refuses, member 2 does not; only member 2 is unpaid against the opponent duo.
            // Not blocked: the refusing member is paid. Then flip it: the refuser is the unpaid one - blocked, and only it is reported.
            QueueEntrant DuoWith(bool refuseFirst, bool refuseSecond) => new QueueEntrant(Guid.NewGuid(), new uint[] { 1, 2 }, new[] { 1500, 1500 }, null, Now,
                RefuseByMember: new[] { refuseFirst, refuseSecond });

            var opponents = Duo(3, 4, 1);

            var onlySecondUnpaid = Directed(unpaid: new[] { (2u, 3u) });
            var paidRefuser = DuoWith(true, false);
            Assert.IsNotNull(new DuoMatchmaker().TryForm(new List<QueueEntrant> { paidRefuser, opponents }, Ctx(onlySecondUnpaid)),
                "member 1 refuses but is paid; member 2 is unpaid but accepts");

            var unpaidRefuser = DuoWith(false, true);
            IReadOnlyList<uint> reported = null;
            var none = new DuoMatchmaker().TryForm(new List<QueueEntrant> { unpaidRefuser, Duo(3, 4, 1) },
                Ctx(onlySecondUnpaid, (id, members) => reported = members));

            Assert.IsNull(none);
            CollectionAssert.AreEqual(new uint[] { 2 }, reported.ToArray(), "only the refusing, unpaid member is named");
        }

        // ---------------- 2v2, four solos (phase 3) ----------------

        private static bool SameTeam(MatchProposal p, uint a, uint b) =>
            p.Teams.Any(t => t.Members.Any(m => m.CharacterId == a) && t.Members.Any(m => m.CharacterId == b));

        [TestMethod]
        public void Solos_LaterFreshSubsetBeatsEarlierRecentOne()
        {
            // Recent edges (1,2) and (1,3) make every split of {1,2,3,4} and {1,2,3,5} Recent; {1,2,4,5} has the Fresh split (1,2 | 4,5).
            var waiting = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1), Solo(3, 2), Solo(4, 3), Solo(5, 4) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 2u), (1u, 3u) })));

            Assert.IsNotNull(proposal);
            CollectionAssert.AreEqual(new uint[] { 1, 2, 4, 5 }, Ids(proposal));
            Assert.IsTrue(SameTeam(proposal, 1, 2) && SameTeam(proposal, 4, 5));
        }

        [TestMethod]
        public void Solos_TierTie_EarliestSubsetWins()
        {
            // Every pair is recent, so every split of every subset is Recent: the earliest subset {1,2,3,4} must win, not the last.
            var pairs = new[] { 1u, 2u, 3u, 4u, 5u }.SelectMany(a => new[] { 1u, 2u, 3u, 4u, 5u }.Where(b => b > a).Select(b => (a, b))).ToArray();
            var waiting = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1), Solo(3, 2), Solo(4, 3), Solo(5, 4) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Probe(recent: pairs)));

            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4 }, Ids(proposal));
        }

        [TestMethod]
        public void Solos_UnpaidRefuser_NeverOpposedToTheirUnpaidPlayer_AndIsReported()
        {
            var refuser = Solo(1, 0, refuse: true);
            var waiting = new List<QueueEntrant> { refuser, Solo(2, 1), Solo(3, 2), Solo(4, 3) };
            var refused = new List<(Guid Id, uint[] Members)>();

            // Solo 1 is unpaid against solo 2: the only valid split puts them on the same team.
            for (var pick = 0; pick < 3; pick++)
            {
                var p = pick;
                refused.Clear();

                var proposal = new DuoMatchmaker().TryForm(waiting,
                    Ctx(Directed(unpaid: new[] { (1u, 2u) }), (id, members) => refused.Add((id, members.ToArray())), pickIndex: n => Math.Min(p, n - 1)));

                Assert.IsNotNull(proposal);
                Assert.IsTrue(SameTeam(proposal, 1, 2));
                Assert.IsTrue(refused.Count > 0, "the blocked splits must report the refuser");
                Assert.IsTrue(refused.All(x => x.Id == refuser.EntrantId && x.Members.SequenceEqual(new uint[] { 1 })));
            }
        }

        [TestMethod]
        public void Solos_FirstSubsetHasNoValidSplit_LaterOneDoes()
        {
            // Solo 1 refuses and is unpaid against 2, 3 and 4: every split of every subset containing 1 puts it opposite one of them.
            var waiting = new List<QueueEntrant> { Solo(1, 0, refuse: true), Solo(2, 1), Solo(3, 2), Solo(4, 3), Solo(5, 4) };

            var proposal = new DuoMatchmaker().TryForm(waiting, Ctx(Directed(unpaid: new[] { (1u, 2u), (1u, 3u), (1u, 4u) })));

            Assert.IsNotNull(proposal);
            CollectionAssert.AreEqual(new uint[] { 2, 3, 4, 5 }, Ids(proposal));
        }

        [TestMethod]
        public void Solos_PickIndexSpy_ReceivesOnlyTheBestTierSplitCount_ThenTwoForTheSide()
        {
            var waiting = new List<QueueEntrant> { Solo(1, 0), Solo(2, 1), Solo(3, 2), Solo(4, 3) };
            var asked = new List<int>();

            // One recent edge (1,2): only the split with 1 and 2 as teammates is Fresh, so the split draw sees 1 candidate, not 3.
            new DuoMatchmaker().TryForm(waiting, Ctx(Probe(recent: new[] { (1u, 2u) }), pickIndex: n => { asked.Add(n); return 0; }));
            CollectionAssert.AreEqual(new[] { 1, 2 }, asked);

            // No history: all three splits tie.
            asked.Clear();
            new DuoMatchmaker().TryForm(waiting, Ctx(Probe(), pickIndex: n => { asked.Add(n); return 0; }));
            CollectionAssert.AreEqual(new[] { 3, 2 }, asked);
        }
    }
}
