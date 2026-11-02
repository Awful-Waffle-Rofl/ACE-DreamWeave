using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.Pvp;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The repeat-pairing cap's pure core (<see cref="PvpPairLedger"/>) and its tunables. Pure: no PropertyManager read except
    /// the default-parity test, which reads the compiled defaults dictionaries only.
    /// </summary>
    [TestClass]
    public class PvpPairLedgerTests
    {
        private const uint X = 1, Y = 2, Z = 3, W = 4;

        /// <summary>Two accounts reach the cap on day 100 (flagged), day 101 reads as 0 (not flagged), and the stale day is pruned on the next record.</summary>
        [TestMethod]
        public void DayRollover_ResetsTheCount()
        {
            var ledger = new PvpPairLedger();

            ledger.Record(X, Y, 100, 2);
            ledger.Record(X, Y, 100, 2);

            Assert.IsTrue(ledger.IsAtCap(X, Y, 100, 2));
            Assert.IsFalse(ledger.IsAtCap(X, Y, 101, 2), "the next arena day pays again");
            Assert.AreEqual(0, ledger.CountFor(X, Y, 101));

            ledger.Record(X, Y, 101, 2);

            Assert.AreEqual(1, ledger.CountFor(X, Y, 101), "recording on the new day starts from zero");
        }

        /// <summary>A count stamped with a day AFTER today (the clock stepped back) reads as today's, never as a fresh day.</summary>
        [TestMethod]
        public void FutureDayStamp_IsTreatedAsToday()
        {
            var ledger = new PvpPairLedger();

            ledger.Record(X, Y, 105, 2);
            ledger.Record(X, Y, 105, 2);

            Assert.IsTrue(ledger.IsAtCap(X, Y, 103, 2), "stamped day 105, today 103: still capped");
            Assert.AreEqual(2, ledger.CountFor(X, Y, 103));

            ledger.Record(X, Y, 103, 2);

            Assert.AreEqual(2, ledger.CountFor(X, Y, 103), "and recording on the earlier day saturates, not resets");
        }

        /// <summary>A cap of 0 or less is off: nothing is ever flagged, however many matches were recorded.</summary>
        [DataTestMethod]
        [DataRow(0L)]
        [DataRow(-1L)]
        public void CapOffIsNeverAtCap(long cap)
        {
            var ledger = new PvpPairLedger();

            for (var i = 0; i < 10; i++)
                ledger.Record(X, Y, 100, 5);

            Assert.IsFalse(ledger.IsAtCap(X, Y, 100, cap));
            Assert.IsFalse(ledger.IsUnpaidRematch(X, new[] { Y }, 100, cap));
        }

        /// <summary>The pair is unordered: (a, b) and (b, a) are one entry.</summary>
        [TestMethod]
        public void PairKeyIsOrderless()
        {
            var ledger = new PvpPairLedger();

            ledger.Record(Y, X, 100, 2);
            ledger.Record(X, Y, 100, 2);

            Assert.IsTrue(ledger.IsAtCap(X, Y, 100, 2));
            Assert.IsTrue(ledger.IsAtCap(Y, X, 100, 2));
        }

        /// <summary>Recording saturates at the cap rather than counting past it.</summary>
        [TestMethod]
        public void RecordSaturatesAtTheCap()
        {
            var ledger = new PvpPairLedger();

            for (var i = 0; i < 6; i++)
                ledger.Record(X, Y, 100, 2);

            Assert.AreEqual(2, ledger.CountFor(X, Y, 100));
        }

        /// <summary>
        /// 2v2: team one is X and Y, team two is Z and W. Only the X-Z pair has met the cap. Every seat that has Z on the other side
        /// of the net, or X, is flagged; a seat whose own cross pairs are all under the cap is not. Decided per seat.
        /// </summary>
        [TestMethod]
        public void TwoVTwo_CrossPairsAreFlaggedPerSeat()
        {
            var ledger = new PvpPairLedger();
            ledger.Record(X, Z, 100, 2);
            ledger.Record(X, Z, 100, 2);

            var teamOne = new[] { X, Y };
            var teamTwo = new[] { Z, W };

            Assert.IsTrue(ledger.IsUnpaidRematch(X, teamTwo, 100, 2), "X has met Z twice");
            Assert.IsTrue(ledger.IsUnpaidRematch(Z, teamOne, 100, 2), "and Z has met X twice");
            Assert.IsFalse(ledger.IsUnpaidRematch(Y, teamTwo, 100, 2), "Y and W have never met the cap with anyone");
            Assert.IsFalse(ledger.IsUnpaidRematch(W, teamOne, 100, 2));
        }

        /// <summary>
        /// A rotating partner does not reset the count: X and Z are capped, and X is still flagged against Z when X's partner changes
        /// from Y to W, because the key is each cross-team ACCOUNT pair, never the team.
        /// </summary>
        [TestMethod]
        public void PartnerRotation_DoesNotResetTheCount()
        {
            var ledger = new PvpPairLedger();
            ledger.Record(X, Z, 100, 2);
            ledger.Record(X, Z, 100, 2);

            // X now plays with W against Z and a new fourth account (5).
            Assert.IsTrue(ledger.IsUnpaidRematch(X, new uint[] { Z, 5 }, 100, 2));
            Assert.IsTrue(ledger.IsUnpaidRematch(Z, new uint[] { X, W }, 100, 2));
        }

        /// <summary>An account is never paired against itself: a seat whose "opponent" is its own account is not flagged by its own count.</summary>
        [TestMethod]
        public void OwnAccountIsNeverAnOpponent()
        {
            var ledger = new PvpPairLedger();
            ledger.Record(X, X, 100, 1);

            Assert.IsFalse(ledger.IsUnpaidRematch(X, new[] { X }, 100, 1));
        }

        /// <summary>No account id known (0) keys on the character id, the AccountLeaderboard.AccountKeyFor fallback.</summary>
        [TestMethod]
        public void AccountKey_FallsBackToCharacterId()
        {
            Assert.AreEqual(77u, PvpPairLedger.AccountKey(77, 0x50000001));
            Assert.AreEqual(0x50000001u, PvpPairLedger.AccountKey(0, 0x50000001));
        }

        /// <summary>The compiled defaults agree with the PropertyManager entries the tsv snapshots are generated from.</summary>
        [TestMethod]
        public void PairingTunableDefaults_MatchThePropertyManagerDefaults()
        {
            var d = PvpPairingTunables.Defaults;

            Assert.AreEqual(DefaultPropertyManager.DefaultLongProperties["pvp_arena_pair_paid_cap"].Item, d.PaidCap);
            Assert.AreEqual(DefaultPropertyManager.DefaultLongProperties["pvp_arena_mm_recent_opponents"].Item, d.MmRecentOpponents);
            Assert.AreEqual(DefaultPropertyManager.DefaultBooleanProperties["pvp_arena_pair_audit_enabled"].Item, d.AuditEnabled);
            Assert.AreEqual(DefaultPropertyManager.DefaultLongProperties["pvp_arena_pair_audit_window_hours"].Item, d.AuditWindowHours);
            Assert.AreEqual(DefaultPropertyManager.DefaultLongProperties["pvp_arena_pair_audit_min_swaps"].Item, d.AuditMinSwaps);

            Assert.AreEqual(2L, d.PaidCap);
            Assert.AreEqual(3L, d.MmRecentOpponents);
            Assert.IsTrue(d.AuditEnabled);
            Assert.AreEqual(24L, d.AuditWindowHours);
            Assert.AreEqual(3L, d.AuditMinSwaps);
        }

        /// <summary>PropertyManager reads throw in the test environment for an uncached key; the source falls back to the defaults.</summary>
        [TestMethod]
        public void DialSource_InTestEnvironment_FallsBackToDefaults()
        {
            Assert.AreEqual(PvpPairingTunables.Defaults, PvpPairingTunables.DialSource());
        }
    }
}
