using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The alternating-result audit (Docs/Pvp/DESIGN.md "Alternating-result audit") wired into Resolve through the coordinator's
    /// auditSink seam. The pairing dials are injected through the rig, so no PropertyManager key is read here.
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        /// <summary>One 1v1 round between the two audit accounts; <paramref name="firstWins"/> picks whether the AcctOne character wins.</summary>
        private static (uint One, uint Two) AuditRound(Rig rig, int n, bool firstWins, string ip = null)
        {
            var match = RoundToLive(rig, n, out var one, out var two, ipOne: ip, ipTwo: ip);

            if (firstWins)
                rig.Death(two, match.MatchId, killer: one);
            else
                rig.Death(one, match.MatchId, killer: two);

            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            rig.PostMatch();

            return (one, two);
        }

        /// <summary>
        /// One 2v2 round on fresh characters: a duo on accounts <paramref name="duoA"/>/<paramref name="duoB"/> (fellowship) against two solos on
        /// accounts 3001 and 3002. The losing side dies to the last member.
        /// </summary>
        private static void AuditRound2v2(Rig rig, int n, uint duoA, uint duoB, bool duoWins)
        {
            var d1 = 0x50002000u + (uint)(n * 10);
            var d2 = d1 + 1;
            var s1 = d1 + 2;
            var s2 = d1 + 3;

            foreach (var (id, acct) in new[] { (d1, duoA), (d2, duoB), (s1, 3001u), (s2, 3002u) })
            {
                var player = rig.Gateway.Add(id, $"Char{id & 0xFFF}", fellowship: id == d1 || id == d2 ? new[] { d1, d2 } : null);
                player.Facts = player.Facts with { AccountId = acct };
            }

            Assert.IsTrue(rig.Coordinator.Join(d1, "2v2", true).Joined);
            Assert.IsTrue(rig.Coordinator.Join(s1, "2v2", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(s2, "2v2", false).Joined);

            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();
            rig.AcceptAll(d1, d2, s1, s2);

            var match = rig.DriveAcceptedToLive(d1);
            var losers = duoWins ? new[] { s1, s2 } : new[] { d1, d2 };
            var killer = duoWins ? d1 : s1;

            foreach (var loser in losers)
                rig.Death(loser, match.MatchId, killer: killer);

            rig.Tick();
            Assert.AreEqual(PvpMatchState.Resolving, match.State);
            rig.PostMatch();
        }

        /// <summary>Drives: five 1v1 rounds on fresh characters between two accounts, winners alternating. The line appears once, at the third swap, and not again.</summary>
        [TestMethod]
        public void PairAudit_AlternatingOneVOnes_EmitExactlyOnce()
        {
            var lines = new List<string>();
            var rig = new Rig { AuditSink = lines.Add }.Build();

            for (var n = 1; n <= 5; n++)
            {
                AuditRound(rig, n, firstWins: n % 2 == 1);

                Assert.AreEqual(n >= 4 ? 1 : 0, lines.Count, $"after round {n}");
            }

            StringAssert.StartsWith(lines[0], "[PVP-AUDIT]");
            StringAssert.Contains(lines[0], "Char");
            StringAssert.Contains(lines[0], "3 winner swaps");
            Assert.AreEqual(5, rig.Sink.Saves.Count, "the audit never changes what is saved");
        }

        /// <summary>Drives: the same alternating rounds with pvp_arena_pair_audit_enabled off. Nothing is emitted.</summary>
        [TestMethod]
        public void PairAudit_Disabled_EmitsNothing()
        {
            var lines = new List<string>();
            var rig = new Rig { AuditSink = lines.Add, Pairing = PvpPairingTunables.Defaults with { AuditEnabled = false } }.Build();

            for (var n = 1; n <= 5; n++)
                AuditRound(rig, n, firstWins: n % 2 == 1);

            Assert.AreEqual(0, lines.Count);
        }

        /// <summary>Drives: alternating rounds between same-IP opponents (pvp_arena_block_same_ip off; unrated, unpaid, not counted by the pair cap). The audit is for staff review, so it still emits once.</summary>
        [TestMethod]
        public void PairAudit_SameIpOneVOnes_StillEmit()
        {
            var lines = new List<string>();
            var rig = new Rig { AuditSink = lines.Add };
            rig.Dials = rig.Dials with { BlockSameIp = false };
            rig.Build();

            for (var n = 1; n <= 4; n++)
                AuditRound(rig, n, firstWins: n % 2 == 1, ip: "10.9.9.9");

            Assert.IsFalse(rig.Sink.Saves.Any(s => s.Match.Rated), "fixture: the matches really were same-IP (unrated)");
            Assert.AreEqual(1, lines.Count);
        }

        /// <summary>Drives: five 2v2 rounds, the same four accounts every time (fresh characters), the duo and the solo side trading wins. One line, at the third swap.</summary>
        [TestMethod]
        public void PairAudit_AlternatingTwoVTwos_SameFourAccounts_EmitOnce()
        {
            var lines = new List<string>();
            var rig = new Rig { AuditSink = lines.Add }.Build();

            for (var n = 1; n <= 5; n++)
            {
                AuditRound2v2(rig, n, 1001, 1002, duoWins: n % 2 == 1);

                Assert.AreEqual(n >= 4 ? 1 : 0, lines.Count, $"after round {n}");
            }

            StringAssert.Contains(lines[0], "1001+1002|3001+3002");
        }

        /// <summary>
        /// Drives: three alternating 2v2 rounds with duo accounts 1001+1002, then three more with the partner changed to 1001+1005 against the same
        /// solos. Each lineup has only two swaps; a key that ignored the partner would reach three across the six rounds. No line.
        /// </summary>
        [TestMethod]
        public void PairAudit_TwoVTwo_ChangedPartner_IsADifferentLineup()
        {
            var lines = new List<string>();
            var rig = new Rig { AuditSink = lines.Add }.Build();

            for (var n = 1; n <= 3; n++)
                AuditRound2v2(rig, n, 1001, 1002, duoWins: n % 2 == 1);

            for (var n = 4; n <= 6; n++)
                AuditRound2v2(rig, n, 1001, 1005, duoWins: n % 2 == 0);

            Assert.AreEqual(0, lines.Count);
        }

        /// <summary>
        /// Drives: alternating 1v1 rounds with a sink that throws at the flagging round, and a pair cap of 4. Every match still resolves and is saved,
        /// and the flagging round (4) still PAYS and still COUNTS toward the pair cap: round 5 is then an unpaid rematch. If the throw escaped the audit,
        /// the Blood payout and the pair ledger (both run after it in Resolve) would be skipped.
        /// </summary>
        [TestMethod]
        public void PairAudit_SinkThrowing_DoesNotBreakResolve_OrThePayoutOrThePairLedger()
        {
            var calls = 0;
            var rig = new Rig
            {
                AuditSink = _ => { calls++; throw new InvalidOperationException("sink down"); },
                Pairing = PvpPairingTunables.Defaults with { PaidCap = 4 }
            }.Build();

            var rounds = new List<(uint One, uint Two)>();

            for (var n = 1; n <= 5; n++)
                rounds.Add(AuditRound(rig, n, firstWins: n % 2 == 1));

            Assert.AreEqual(1, calls, "the sink was reached, at round 4");
            Assert.AreEqual(5, rig.Sink.Saves.Count, "all results saved");
            Assert.AreEqual(0, rig.Coordinator.ListMatches().Count, "and all matches closed");

            Assert.AreEqual((2, 1), rig.Gateway.Blood(rounds[3].Two), "round 4 (the throwing round): its winner was still paid");
            Assert.AreEqual((1, 1), rig.Gateway.Blood(rounds[3].One), "and its loser");
            Assert.IsTrue(LineupSaysUnpaid(rig, rounds[4].One), "round 4 still counted toward the pair cap, so round 5 is an unpaid rematch");
            Assert.AreEqual((0, 0), rig.Gateway.Blood(rounds[4].One));
        }
    }
}