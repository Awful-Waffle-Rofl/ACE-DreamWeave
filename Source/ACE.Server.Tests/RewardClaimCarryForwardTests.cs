using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Server.Entity.RewardClaims;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Reward-claim carry-forward when an account's household changes: the pure per-row key decision, and the
    /// IpLimitManager.OnClusterMembershipChanged seam driven against the in-memory reward_claim (which carries
    /// the table's PRIMARY KEY and UNIQUE (key, ip) constraints).
    /// </summary>
    [TestClass]
    public class RewardClaimCarryForwardTests
    {
        private const string K1 = "carry-k1";
        private const string K2 = "carry-k2";
        private const string X = "203.0.113.50";

        private const uint A = 101;
        private const uint B = 102;
        private const uint D = 104;
        private const uint E = 105;

        private static readonly string ClusterC = IpLimitManager.ClusterKey("c");

        private FakeRewardClaimStore store;

        [TestInitialize]
        public void Init()
        {
            store = new FakeRewardClaimStore();
            RewardClaimService.ResetForTesting(store);
        }

        [TestCleanup]
        public void Cleanup()
        {
            RewardClaimService.ResetForTesting(null);
        }

        private RewardClaimInsertResult Claim(string key, uint account, string ipKey, string ipAddress)
            => store.TryInsert(new RewardClaim { ClaimKey = key, AccountId = account, IpKey = ipKey, IpAddress = ipAddress, ClaimToken = $"{key}-{account}" });

        private RewardClaim Row(string key, uint account) => store.Rows.Single(r => r.ClaimKey == key && r.AccountId == account);

        // ---- the pure decision ------------------------------------------------------------------------------

        [TestMethod]
        public void Enter_a_household_moves_the_row_to_the_household_key()
        {
            Assert.AreEqual(ClusterC, RewardClaimCarryForward.TargetIpKey(ClusterC, X, false));
            Assert.AreEqual("acct:7", RewardClaimCarryForward.TargetIpKey(IpLimitManager.AccountKey(7), X, false), "an exempt singleton is a household too");
            Assert.AreEqual(ClusterC, RewardClaimCarryForward.TargetIpKey(ClusterC, null, false), "entering needs no stored address");
        }

        [TestMethod]
        public void Leave_to_the_address_reverts_to_the_rows_own_stored_address()
        {
            Assert.AreEqual("203.0.113.50", RewardClaimCarryForward.TargetIpKey(null, X, false));
            Assert.AreEqual("203.0.113.50", RewardClaimCarryForward.TargetIpKey(null, "::ffff:203.0.113.50", false), "normalised exactly as claims are");
            Assert.AreEqual("2001:db8::1", RewardClaimCarryForward.TargetIpKey(null, "2001:DB8:0:0:0:0:0:1", false));
            Assert.AreEqual("198.51.100.9", RewardClaimCarryForward.TargetIpKey("198.51.100.1", "198.51.100.9", false),
                "a non-household new key means address keying, and the row's own address wins over any live one");
        }

        [TestMethod]
        public void Move_between_households_takes_the_new_key()
        {
            Assert.AreEqual(IpLimitManager.ClusterKey("d"), RewardClaimCarryForward.TargetIpKey(IpLimitManager.ClusterKey("d"), X, false));
            Assert.AreEqual("acct:9", RewardClaimCarryForward.TargetIpKey("acct:9", X, false), "cluster to exempt");
        }

        [TestMethod]
        public void Collision_means_null_whatever_the_direction()
        {
            Assert.IsNull(RewardClaimCarryForward.TargetIpKey(ClusterC, X, true));
            Assert.IsNull(RewardClaimCarryForward.TargetIpKey(null, X, true));
        }

        [TestMethod]
        public void A_null_or_unparseable_stored_address_leaves_no_key()
        {
            Assert.IsNull(RewardClaimCarryForward.TargetIpKey(null, null, false));
            Assert.IsNull(RewardClaimCarryForward.TargetIpKey(null, "", false));
            Assert.IsNull(RewardClaimCarryForward.TargetIpKey(null, "not-an-address", false));
        }

        [TestMethod]
        public void Only_a_real_key_change_rekeys()
        {
            Assert.IsFalse(RewardClaimCarryForward.ShouldRekey(null, null));
            Assert.IsFalse(RewardClaimCarryForward.ShouldRekey(ClusterC, ClusterC));
            Assert.IsTrue(RewardClaimCarryForward.ShouldRekey(null, ClusterC));
            Assert.IsTrue(RewardClaimCarryForward.ShouldRekey(ClusterC, null));
            Assert.IsTrue(RewardClaimCarryForward.IsHouseholdKey(ClusterC));
            Assert.IsTrue(RewardClaimCarryForward.IsHouseholdKey("acct:1"));
            Assert.IsFalse(RewardClaimCarryForward.IsHouseholdKey(X));
            Assert.IsFalse(RewardClaimCarryForward.IsHouseholdKey(null));
        }

        // ---- the seam over the store ------------------------------------------------------------------------

        [TestMethod]
        public void Joining_a_cluster_frees_the_old_address_and_keeps_the_account_record()
        {
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K1, B, X, X));
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K2, A, X, X));
            Assert.AreEqual(RewardClaimInsertResult.Duplicate, Claim(K1, D, X, X), "control: B holds X for K1 before the move");

            var outcome = IpLimitManager.OnClusterMembershipChanged(B, null, ClusterC);

            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual(1, outcome.Counts.Rekeyed);
            Assert.AreEqual(ClusterC, Row(K1, B).IpKey, "B's row moved with B");
            Assert.AreEqual(X, Row(K2, A).IpKey, "A's row is not B's and stays");

            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K1, D, X, X), "a third account under X may now claim K1");
            Assert.AreEqual(RewardClaimInsertResult.Duplicate, Claim(K1, B, ClusterC, X), "B still cannot claim K1 twice");
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K2, B, ClusterC, X), "A's completion under X does not block B's cluster");
        }

        [TestMethod]
        public void A_collision_nulls_the_row_and_keeps_it()
        {
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K1, E, ClusterC, "198.51.100.5"));
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K1, B, X, X));

            var outcome = IpLimitManager.OnClusterMembershipChanged(B, null, ClusterC);

            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual(1, outcome.Counts.NulledByCollision);
            Assert.AreEqual(0, outcome.Counts.Rekeyed);
            Assert.IsNull(Row(K1, B).IpKey, "the slot under X is freed");
            Assert.AreEqual(ClusterC, Row(K1, E).IpKey, "the holder is untouched");
            Assert.AreEqual(RewardClaimInsertResult.Duplicate, Claim(K1, B, ClusterC, X), "the PRIMARY KEY row remains");
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K1, D, X, X), "X is free for K1 again");
        }

        [TestMethod]
        public void Leaving_reverts_each_row_to_its_own_address_and_nulls_an_unusable_one()
        {
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K1, B, ClusterC, "::ffff:" + X));
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K2, B, ClusterC, null));

            var outcome = IpLimitManager.OnClusterMembershipChanged(B, ClusterC, null);

            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual(1, outcome.Counts.Rekeyed);
            Assert.AreEqual(1, outcome.Counts.NulledNoKey);
            Assert.AreEqual(X, Row(K1, B).IpKey);
            Assert.IsNull(Row(K2, B).IpKey);
        }

        [TestMethod]
        public void A_bypass_row_with_a_null_key_is_never_touched()
        {
            Assert.AreEqual(RewardClaimInsertResult.Inserted, Claim(K1, B, null, X));

            var outcome = IpLimitManager.OnClusterMembershipChanged(B, null, ClusterC);

            Assert.IsTrue(outcome.Ok);
            Assert.AreEqual(0, outcome.Counts.Examined);
            Assert.IsNull(Row(K1, B).IpKey);
        }

        [TestMethod]
        public void An_unchanged_key_never_reaches_the_store_and_a_failure_is_reported()
        {
            var skipped = IpLimitManager.OnClusterMembershipChanged(B, ClusterC, ClusterC);
            Assert.IsTrue(skipped.Skipped);
            Assert.AreEqual(0, store.RekeyCalls);

            store.FailRekeys = true;
            var failed = IpLimitManager.OnClusterMembershipChanged(B, null, ClusterC);
            Assert.IsFalse(failed.Ok);
            StringAssert.Contains(RewardClaimCarryForward.Describe("bob", failed), "NOT carried forward");
        }

        [TestMethod]
        public void The_report_names_rekeyed_and_collision_counts()
        {
            var text = RewardClaimCarryForward.Describe("bob", new RewardClaimCarryForwardOutcome
            {
                Ok = true,
                NewKey = ClusterC,
                Counts = new RewardClaimRekeyResult { Rekeyed = 3, NulledByCollision = 2 },
            });

            StringAssert.Contains(text, "3 row(s) re-keyed");
            StringAssert.Contains(text, "2 row(s) nulled because another account already held that claim");
            StringAssert.Contains(text, ClusterC);
        }
    }
}
